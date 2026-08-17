#!/usr/bin/env bash
# Creates every database named by a POSTGRES_*_DB variable, if it does not exist yet, and
# reports what it found. Idempotent: safe to run on every start and by hand at any time.
#
# Why this exists at all: the obvious place for this — a .sql file in
# docker-entrypoint-initdb.d — fires *only* when the data directory is initialised from empty.
# On a deployment that already has data, which is every deployment after the first, a newly
# added database is therefore never created, and nothing says so. The services then fail
# obscurely rather than loudly: a missing database raises the same NpgsqlException as an
# unreachable server, so DbMigrator used to retry "Postgres not ready" forever.
#
# The list of databases is discovered from the environment instead of hardcoded here. Adding a
# service means adding its POSTGRES_<NAME>_DB to .env and nothing else, and stamping a
# per-tenant set of databases is just a different set of values — no code change in this file.
# POSTGRES_DB itself is deliberately not matched: that is the maintenance database we connect
# to in order to issue CREATE DATABASE.
#
# It also cross-checks the Dapr components against this environment — see the block near the end
# for why that belongs here.

set -euo pipefail

CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --check) CHECK_ONLY=1 ;;
    -h|--help)
      echo "Usage: $0 [--check]"
      echo "  --check   report only; create nothing"
      exit 0
      ;;
    *)
      echo "unknown argument: $arg" >&2
      exit 2
      ;;
  esac
done

PGHOST="${POSTGRES_HOST:-postgres}"
PGPORT="${POSTGRES_PORT:-5432}"
PGUSER="${POSTGRES_USER:-postgres}"
export PGPASSWORD="${POSTGRES_PASSWORD:-postgres}"
MAINTENANCE_DB="${POSTGRES_MAINTENANCE_DB:-postgres}"

psql_main() {
  psql --host "$PGHOST" --port "$PGPORT" --username "$PGUSER" --dbname "$MAINTENANCE_DB" \
       --no-align --tuples-only --quiet "$@"
}

echo "== PostgreSQL preparation"
echo "   server: $PGUSER@$PGHOST:$PGPORT (maintenance db: $MAINTENANCE_DB)"

# Wait for the server. Compose's depends_on only waits for the container, not for postgres
# inside it to accept connections, and on a first start it restarts once after initdb.
deadline=$(( SECONDS + ${POSTGRES_WAIT_SECONDS:-60} ))
until pg_isready --host "$PGHOST" --port "$PGPORT" --username "$PGUSER" --quiet; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "   [fail] postgres did not become ready in ${POSTGRES_WAIT_SECONDS:-60}s" >&2
    exit 1
  fi
  sleep 1
done
echo "   [ ok ] server is accepting connections"

# Every POSTGRES_<SOMETHING>_DB in the environment names a database we are responsible for.
mapfile -t db_vars < <(compgen -A variable | grep -E '^POSTGRES_[A-Z0-9_]+_DB$' | sort)
if [ "${#db_vars[@]}" -eq 0 ]; then
  echo "   [fail] no POSTGRES_*_DB variables in the environment — nothing to prepare." >&2
  echo "          Expected e.g. POSTGRES_CFG_DB, POSTGRES_FS_DB. Is .env being passed in?" >&2
  exit 1
fi

existing="$(psql_main --command "SELECT datname FROM pg_database;")"
# -F: a tenant-stamped name is data, not a pattern — a dot in it must not match any character.
db_exists() { printf '%s\n' "$existing" | grep -qxF "$1"; }

# The names we are responsible for, for the component cross-check further down.
db_names=()
for var in "${db_vars[@]}"; do
  if [ -n "${!var}" ]; then
    db_names+=("${!var}")
  fi
done

missing=0
created=0
for var in "${db_vars[@]}"; do
  name="${!var}"
  if [ -z "$name" ]; then
    echo "   [warn] $var is set but empty — skipped"
    continue
  fi

  if db_exists "$name"; then
    echo "   [ ok ] $name already exists ($var)"
    continue
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then
    echo "   [MISSING] $name ($var)"
    missing=$(( missing + 1 ))
    continue
  fi

  # Quoted so that a tenant-stamped name with mixed case or dashes still works.
  psql_main --command "CREATE DATABASE \"$name\";" >/dev/null
  echo "   [ new] $name created ($var)"
  created=$(( created + 1 ))
done

# A wrong POSTGRES_PORT is the failure this catches: both this stack and multitenant_admin
# publish 5432 on the host, so it is entirely possible to reach the other server, authenticate,
# and find none of our databases. Saying so beats "database does not exist" from every service.
if [ "$missing" -gt 0 ] || { [ "$created" -gt 0 ] && [ "$CHECK_ONLY" -eq 0 ]; }; then
  if printf '%s\n' "$existing" | grep -qx "mapstore"; then
    echo "   [warn] this server also holds 'mapstore' — it looks like multitenant_admin's"
    echo "          postgres, not ours. Both publish port 5432 on the host; check POSTGRES_PORT."
  fi
fi

# ---------------------------------------------------------------------------------------------
# Cross-check the Dapr components against the environment.
#
# Dapr does not expand environment variables in component metadata, so a component carries its
# connection string as a literal — the only place in the deployment where a database name or a
# credential is written down twice. Changing POSTGRES_PASSWORD or RABBITMQ_DEFAULT_PASS in .env
# and forgetting the component does not fail loudly: daprd logs one component error at startup
# and carries on with a store or a bus that never works, which then looks like "actors are
# broken", "permissions are cached forever" or "nobody reacts to config changes". Comparing the
# two copies here costs nothing.
#
# Two kinds of component are recognised: PostgreSQL (host=...) and RabbitMQ (amqp://...).
# Components pointing at some other server are none of our business and are skipped: only the
# ones whose host is this very server are compared.
component_dirs=()
if [ -n "${DAPR_COMPONENTS_DIR:-}" ]; then
  component_dirs+=("$DAPR_COMPONENTS_DIR")
fi
# /components is the read-only mount the db-init service gets; the relative path is for running
# this script straight from a checkout.
component_dirs+=("/components")
component_dirs+=("$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/../init_files/dapr_components")

conn_field() { printf '%s\n' "$1" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -n1; }
known_db() {
  [ "${#db_names[@]}" -gt 0 ] || return 1
  printf '%s\n' "${db_names[@]}" | grep -qxF "$1"
}

component_problems=0
component_checked=0
seen_dirs=""
for dir in "${component_dirs[@]}"; do
  [ -d "$dir" ] || continue
  # The candidates above can resolve to the same directory (the mount and the checkout are the
  # same files when this runs outside a container); report each file once.
  canonical="$(cd "$dir" && pwd -P)"
  case ":$seen_dirs:" in *":$canonical:"*) continue ;; esac
  seen_dirs="$seen_dirs:$canonical"
  while IFS= read -r -d '' file; do
    file_problems=0

    # RabbitMQ: amqp://user:password@host:port/. Only the credentials are compared — a wrong host
    # or port fails immediately and visibly ("connection refused"), while a wrong password is the
    # silent one, and the in-network and host-side twins legitimately differ in both.
    amqp="$(grep -oE 'amqp://[^"[:space:]]+' "$file" | head -n1)" || true
    if [ -n "$amqp" ]; then
      if [ -z "${RABBITMQ_DEFAULT_USER:-}" ] && [ -z "${RABBITMQ_DEFAULT_PASS:-}" ]; then
        echo "   [skip] $(basename "$file"): RABBITMQ_DEFAULT_USER/PASS not in the environment"
        continue
      fi
      component_checked=$(( component_checked + 1 ))
      credentials="${amqp#amqp://}"
      credentials="${credentials%%@*}"
      a_user="${credentials%%:*}"
      a_pass="${credentials#*:}"
      if [ "$a_user" != "${RABBITMQ_DEFAULT_USER:-}" ]; then
        echo "   [FAIL] $file: user '$a_user' != RABBITMQ_DEFAULT_USER '${RABBITMQ_DEFAULT_USER:-}'" >&2
        file_problems=$(( file_problems + 1 ))
      fi
      if [ "$a_pass" != "${RABBITMQ_DEFAULT_PASS:-}" ]; then
        echo "   [FAIL] $file: password does not match RABBITMQ_DEFAULT_PASS" >&2
        echo "          Dapr cannot read it from the environment — change it in both places." >&2
        file_problems=$(( file_problems + 1 ))
      fi
      if [ "$a_user" = "guest" ]; then
        echo "   [FAIL] $file: 'guest' cannot be used — RabbitMQ accepts it only from localhost," >&2
        echo "          so every sidecar in a container is rejected." >&2
        file_problems=$(( file_problems + 1 ))
      fi
      if [ "$file_problems" -eq 0 ]; then
        echo "   [ ok ] $(basename "$file") -> amqp as '$a_user', matches the environment"
      fi
      component_problems=$(( component_problems + file_problems ))
      continue
    fi

    # A password with a space in it would not survive this split — but it would not survive the
    # unquoted connection string in the component either, so it is the same constraint.
    conn="$(grep -oE '"[^"]*host=[^"]*"' "$file" | head -n1 | tr -d '"')" || true
    [ -n "$conn" ] || continue

    c_host="$(conn_field "$conn" host)"
    case "$c_host" in
      "$PGHOST"|localhost|127.0.0.1|postgres) ;;
      *)
        echo "   [skip] $(basename "$file"): points at host '$c_host', not this server"
        continue
        ;;
    esac

    component_checked=$(( component_checked + 1 ))
    c_db="$(conn_field "$conn" database)"
    c_user="$(conn_field "$conn" user)"
    c_pass="$(conn_field "$conn" password)"

    if [ -n "$c_db" ] && ! known_db "$c_db"; then
      echo "   [FAIL] $file" >&2
      echo "          connects to database '$c_db', which no POSTGRES_*_DB names — so nothing" >&2
      echo "          creates it. Add it to .env (e.g. POSTGRES_STATE_DB=$c_db)." >&2
      file_problems=$(( file_problems + 1 ))
    fi
    if [ -n "$c_user" ] && [ "$c_user" != "$PGUSER" ]; then
      echo "   [FAIL] $file: user '$c_user' != POSTGRES_USER '$PGUSER'" >&2
      file_problems=$(( file_problems + 1 ))
    fi
    if [ -n "$c_pass" ] && [ "$c_pass" != "$PGPASSWORD" ]; then
      echo "   [FAIL] $file: password does not match POSTGRES_PASSWORD" >&2
      echo "          Dapr cannot read it from the environment — change it in both places." >&2
      file_problems=$(( file_problems + 1 ))
    fi

    if [ "$file_problems" -eq 0 ]; then
      echo "   [ ok ] $(basename "$file") -> $c_db, matches the environment"
    fi
    component_problems=$(( component_problems + file_problems ))
  done < <(find "$dir" -type f -name '*.yaml' -print0)
done
if [ "$component_checked" -eq 0 ]; then
  echo "   [note] no PostgreSQL Dapr components found to cross-check"
fi

if [ "$CHECK_ONLY" -eq 1 ]; then
  if [ "$missing" -gt 0 ] || [ "$component_problems" -gt 0 ]; then
    if [ "$missing" -gt 0 ]; then
      echo "== $missing database(s) missing. Run this script without --check to create them." >&2
    fi
    if [ "$component_problems" -gt 0 ]; then
      echo "== $component_problems component mismatch(es) — fix the files above." >&2
    fi
    exit 1
  fi
  echo "== All databases present."
  exit 0
fi

if [ "$component_problems" -gt 0 ]; then
  echo "== $component_problems component mismatch(es): the databases are ready, but Dapr would" >&2
  echo "   not be able to use them. Fix the files above." >&2
  exit 1
fi

echo "== Done: $created created, $(( ${#db_vars[@]} - created )) already present."
