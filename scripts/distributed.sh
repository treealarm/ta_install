#!/usr/bin/env bash
# A deployment over several machines: one core, any number of nodes (ta_vms CLAUDE.md, "Distributed
# layout"). Writes data/distributed/, which docker-compose.distributed.yml reads, and runs compose
# with it. A single machine needs none of this: docker-compose.yml is the standalone deployment.
#
#   On the core:
#     scripts/distributed.sh core <this machine's address>
#     scripts/distributed.sh up | down | ps | logs ...    (compose with the right files)
#   On a node, asking the core's web for its package (VmsCfg keeps the deployment's nodes):
#     scripts/distributed.sh join --core <core address[:web port]> --name <node> <this machine's address> [--no-mediamtx]
#     scripts/distributed.sh up
#   or with a package file made in the core's web (Administration, Deployment, "+"):
#     scripts/distributed.sh join <package> <this machine's address> [--no-mediamtx]
#   Once the administrator has closed enrollment there, joining takes an administrator's user name
#   and password: TAVMS_JOIN_USER and TAVMS_JOIN_PASSWORD, or asked for on the terminal.
#   A node runs its own MediaMTX unless joined with --no-mediamtx, its cameras then publishing to
#   the core's; and a web of its own, logging users in at the core's Keycloak.
#
# What crosses machines, and how it is protected:
#   - every Dapr sidecar's internal gRPC port: mutual TLS, certificates from Dapr Sentry on the core.
#     Sentry signs a certificate for an app id only against a token for that app id (its jwks
#     validator); without one it would sign anything for anybody (its "insecure" default).
#   - app ids are found through HashiCorp Consul: a server on the core, an agent on every node,
#     gossip encrypted with a key of this deployment. The agents' HTTP API stays on the loopback.
#   - PostgreSQL, RabbitMQ, Dapr placement and Keycloak on the core, and every MediaMTX, behind their
#     passwords and the firewall (see README.md).
#
# Needs openssl, jq, curl, perl and docker compose.
set -euo pipefail

cd "$(dirname "$0")/.."
DIST=data/distributed
COMPOSE=(docker compose -f docker-compose.distributed.yml --env-file .env --env-file "$DIST/distributed.env")
# Sentry's own SPIFFE id, which every token names as its audience; and the trust domains Dapr uses
# by default outside Kubernetes: "localhost" for the control plane, "public" for applications.
SENTRY_ID=spiffe://localhost/ns/default/dapr-sentry
die() { echo "error: $*" >&2; exit 1; }
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
# A value of this machine's environment: .env, overridden by the core's values on a node (core.env,
# which distributed.env repeats) and by distributed.env. Double quotes are stripped; single quotes
# are not, which no generated value uses.
env_value() {
  { cat .env; for f in "$DIST/core.env" "$DIST/distributed.env"; do if [[ -f "$f" ]]; then cat "$f"; fi; done; } |
    sed -n "s/^$1[[:space:]]*=[[:space:]]*//p" | tail -n1 | tr -d '"'
}

# A JWT for Sentry's jwks validator, signed with the deployment's ES256 key: the algorithm the core's
# VmsCfg signs node packages with (.NET has no Ed25519), and the Windows core's. No "exp": the token
# is the credential of a machine's sidecar, and what revokes it is a new key in the JWKS.
mint_token() {
  local header payload signature
  header=$(printf '{"alg":"ES256","typ":"JWT","kid":"tavms"}' | b64url)
  payload=$(printf '{"sub":"%s","aud":["%s"],"iat":%s}' "$1" "$SENTRY_ID" "$(date +%s)" | b64url)
  # openssl signs ECDSA as a DER sequence of r and s; a JWS wants the two as 32 bytes each.
  signature=$(printf '%s.%s' "$header" "$payload" | openssl dgst -sha256 -sign "$DIST/core/jwt.key" |
    openssl asn1parse -inform DER | awk -F: '/INTEGER/ { printf "%64s", $NF }' | tr ' ' 0 |
    perl -pe 's/([0-9A-Fa-f]{2})/chr(hex($1))/ge' | b64url)
  printf '%s.%s.%s' "$header" "$payload" "$signature"
}

# Every sidecar of a profile, as "<app id> <dapr http port>", read from the compose file itself so
# that its ports are written in one place.
sidecars() {
  TAVMS_NODE_NAME="${2:-}" docker compose -f docker-compose.distributed.yml --env-file .env \
    --env-file "$DIST/distributed.env" --profile "$1" config --format json |
    jq -r '.services[] | select(.image | startswith("daprio/daprd")) | .command
           | "\(.[index("-app-id") + 1]) \(.[index("-dapr-http-port") + 1])"'
}

# The Dapr configuration of one sidecar: Consul resolves app ids, and the sidecar registers itself
# with a health check of its own. Consul's default check would call the sidecar's HTTP API at its
# LAN address, which stays on the loopback.
write_dapr_config() {
  cat > "$DIST/dapr/$1.yaml" <<EOF
apiVersion: dapr.io/v1alpha1
kind: Configuration
metadata:
  name: $1
spec:
  nameResolution:
    component: "consul"
    configuration:
      client:
        address: "127.0.0.1:8500"
      selfRegister: true
      selfDeregister: true
      useCache: true
      checks:
        - name: "Dapr Health Status"
          checkID: "daprHealth:$1"
          interval: "5s"
          http: "http://127.0.0.1:$2/v1.0/healthz"
EOF
}

# The state store and the bus, at the core's PostgreSQL and RabbitMQ. Dapr does not expand variables
# in component metadata, so these are written out with the values from .env.
write_components() {
  local host=$1 rhost=${2:-$1} user pass db ruser rpass port rport
  user=$(env_value POSTGRES_USER); pass=$(env_value POSTGRES_PASSWORD); db=$(env_value POSTGRES_STATE_DB)
  port=$(env_value POSTGRES_PORT); ruser=$(env_value RABBITMQ_DEFAULT_USER); rpass=$(env_value RABBITMQ_DEFAULT_PASS)
  rport=$(env_value RABBITMQ_PORT)
  mkdir -p "$DIST/components"
  cat > "$DIST/components/statestore.yaml" <<EOF
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: statestore
spec:
  type: state.postgresql
  version: v1
  metadata:
  - name: connectionString
    value: "host=$host port=$port user=$user password=$pass database=$db sslmode=disable"
  - name: actorStateStore
    value: "true"
  - name: keyPrefix
    value: "name"
EOF
  cat > "$DIST/components/orderpubsub.yaml" <<EOF
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: orderpubsub
spec:
  type: pubsub.rabbitmq
  version: v1
  metadata:
  - name: connectionString
    value: "amqp://$ruser:$rpass@$rhost:$rport/"
  - name: durable
    value: "true"
  - name: deletedWhenUnused
    value: "false"
  - name: maxLen
    value: "10000"
  - name: ttlInSeconds
    value: "3600"
EOF
}

# Passwords that every compose default ships with would be open to the whole LAN here.
require_own_passwords() {
  [[ "$(env_value POSTGRES_PASSWORD)" != postgres ]] || die "set POSTGRES_PASSWORD in .env: PostgreSQL is reachable from the nodes"
  [[ "$(env_value RABBITMQ_DEFAULT_PASS)" != vms ]] || die "set RABBITMQ_DEFAULT_PASS in .env: RabbitMQ is reachable from the nodes"
}

# The sidecars of a profile: their configurations, and their tokens when this machine holds the key.
write_sidecars() {
  local profile=$1 node=${2:-} id port
  mkdir -p "$DIST/dapr" "$DIST/tokens"
  while read -r id port; do
    write_dapr_config "$id" "$port"
    if [[ -f "$DIST/core/jwt.key" ]]; then
      mint_token "spiffe://public/ns/default/$id" > "$DIST/tokens/$id.token"
    fi
  done < <(sidecars "$profile" "$node")
}

cmd_core() {
  local address=${1:?usage: distributed.sh core <this machine address>}
  [[ ! -e "$DIST/core" ]] || die "$DIST/core exists: this machine is a core already"
  require_own_passwords
  umask 077
  mkdir -p "$DIST/core" "$DIST/sentry"

  # The root of the deployment's Dapr certificates and Sentry's issuer under it, ten years each:
  # Sentry would make its own on the first start, but for one year only, and the sidecars need the
  # root before anything runs. The root's key stays here, outside Sentry's directory.
  openssl ecparam -name prime256v1 -genkey -noout -out "$DIST/core/root.key"
  openssl req -x509 -new -key "$DIST/core/root.key" -subj "/O=localhost" -days 3650 -out "$DIST/sentry/ca.crt" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,digitalSignature,keyCertSign"
  openssl ecparam -name prime256v1 -genkey -noout -out "$DIST/sentry/issuer.key"
  openssl req -new -key "$DIST/sentry/issuer.key" -subj "/O=spiffe:\/\/localhost\/ns\/default\/dapr-sentry" |
    openssl x509 -req -CA "$DIST/sentry/ca.crt" -CAkey "$DIST/core/root.key" -CAcreateserial -CAserial "$DIST/core/root.srl" \
      -days 3650 -extfile <(printf 'basicConstraints=critical,CA:TRUE\nkeyUsage=critical,digitalSignature,keyCertSign,cRLSign\n') \
      -out "$DIST/sentry/issuer.crt" 2>/dev/null

  # The key that signs the sidecars' tokens, and the JWKS Sentry checks them against. VmsCfg reads it
  # too, to sign the tokens of the nodes that join later (TAVMS_DISTRIBUTED_DIR).
  openssl ecparam -name prime256v1 -genkey -noout -out "$DIST/core/jwt.key"
  local point x y
  point=$(mktemp)
  openssl ec -in "$DIST/core/jwt.key" -pubout -outform DER 2>/dev/null | tail -c 64 > "$point"
  x=$(head -c 32 "$point" | b64url); y=$(tail -c 32 "$point" | b64url)
  rm -f "$point"
  cat > "$DIST/sentry/config.yaml" <<EOF
kind: Configuration
apiVersion: dapr.io/v1alpha1
metadata:
  name: sentry
spec:
  mtls:
    enabled: true
    workloadCertTTL: "24h"
    allowedClockSkew: "15m"
    tokenValidators:
      - name: jwks
        options:
          source: '{"keys":[{"kty":"EC","crv":"P-256","x":"$x","y":"$y","kid":"tavms","alg":"ES256","use":"sig"}]}'
EOF

  # Shared by every machine of the deployment: the core's address, Consul's gossip key and the root
  # of trust. Quoted on several lines, which docker compose reads back as one value.
  {
    echo "CORE_ADDRESS=$address"
    echo "CONSUL_ENCRYPT=$(openssl rand -base64 32)"
    echo "DAPR_TRUST_ANCHORS=\"$(cat "$DIST/sentry/ca.crt")\""
  } > "$DIST/shared.env"
  write_machine_env core "$address" ""
  write_components 127.0.0.1
  mint_token "spiffe://localhost/ns/default/dapr-placement" > "$DIST/core/placement.token"
  write_sidecars core
  echo "core ready: $DIST. Start it with: scripts/distributed.sh up"
}

# This machine's own part of the environment, after the shared part, as one file compose reads.
write_machine_env() {
  local role=$1 address=$2 node=$3 mediamtx=${4:-1} pairs
  pairs="vms:vmsfs:vmsonvif:vmslogger"
  # Nodes that joined a core made before VmsCfg kept the deployment's nodes, listed by the package
  # command it had then. A node joining now is VmsCfg's alone and is not added here.
  [[ -f "$DIST/nodes" ]] && pairs+=$(sed 's/^/,/' "$DIST/nodes" | tr -d '\n')
  {
    cat "$DIST/shared.env"
    # A profile, not COMPOSE_PROFILES: `up` picks the profiles itself (node-mediamtx), and
    # `sidecars` asks compose for one profile at a time.
    echo "TAVMS_ROLE=$role"
    # The containers run as the owner of $DIST, whose keys and tokens nobody else may read.
    echo "TAVMS_UID=$(id -u)"
    echo "TAVMS_GID=$(id -g)"
    echo "TAVMS_NODE_NAME=$node"
    echo "DAPR_HOST_IP=$address"
    echo "MEDIA_SERVER_PAIRS=$pairs"
    # Whether this machine runs MediaMTX: always on the core; a node's, unless --no-mediamtx
    # (`up` adds the profile node-mediamtx).
    echo "TAVMS_MEDIAMTX=$mediamtx"
    # Where the core's VmsCfg finds the keys it signs nodes' tokens with, inside its container.
    if [[ $role == core ]]; then echo "TAVMS_DISTRIBUTED_DIR=/data/distributed"; fi
    # What differs from .env, as windows/config/windows.env is for Windows: no compose network to
    # name hosts by, so the core's address; the ports Dapr's control plane listens on; and what a
    # distributed deployment does not run yet. A node's package names each of the core's services
    # in core.env, which comes last and wins; these are for a package from before it did.
    local core
    core=$(sed -n 's/^CORE_ADDRESS=//p' "$DIST/shared.env")
    echo "POSTGRES_HOST=$core"
    echo "RABBITMQ_HOST=$core"
    echo "VMS_MTX_HOST=$core"
    # The core's Keycloak, on its address (KC_HTTP_HOST), and on a node at the core's port.
    echo "KEYCLOAK_URL=http://$core:$(env_value KEYCLOAK_PORT)"
    echo "DAPR_PLACEMENT_PORT=6050"
    echo "DAPR_SENTRY_PORT=6055"
    echo "DAPR_ANALYTICS_WORKER_APP_IDS="
    echo "DAPR_SQUARE_SINGLETON_APP_ID="
    echo "SQUARE_INTEGRATION_SINK_APP_ID="
    # A node: the core's shared values, last, so that they win over this machine's .env.
    if [[ -f "$DIST/core.env" ]]; then cat "$DIST/core.env"; fi
  } > "$DIST/distributed.env"
}

# Asks the core's web for this node's package (POST /api/deployment/join) and writes it to $1.
fetch_package() {
  local out=$1 core=$2 name=$3 address=$4 mediamtx=$5 url status body user pass
  [[ $core == *:* ]] || core="$core:$(env_value WEB_VMS_PORT)"
  url="http://$core/api/deployment"
  body=$(curl -fsS --max-time 10 "$url") || die "no TreeAlarm VMS web answers at $url"
  [[ $(jq -r .distributed <<<"$body") == true ]] || die "$core is not the core of a deployment over several machines"
  user=${TAVMS_JOIN_USER:-}; pass=${TAVMS_JOIN_PASSWORD:-}
  if [[ $(jq -r .enrollmentOpen <<<"$body") != true && -z $user ]]; then
    [[ -t 0 ]] || die "enrollment is closed at $core: set TAVMS_JOIN_USER and TAVMS_JOIN_PASSWORD to an administrator's"
    read -rp "Enrollment is closed at $core. Administrator: " user
    read -rsp "Password: " pass; echo
  fi
  status=$(jq -n --arg name "$name" --arg address "$address" --argjson mediaMtx "$([[ $mediamtx == 1 ]] && echo true || echo false)" \
      --arg username "$user" --arg password "$pass" \
      '{name: $name, address: $address, mediaMtx: $mediaMtx, username: $username, password: $password}' |
    curl -sS --max-time 30 -o "$out" -w '%{http_code}' -H 'Content-Type: application/json' --data-binary @- "$url/join") ||
    die "$url/join did not answer"
  [[ $status == 200 ]] || die "the core refused: $(jq -r '.error // .' "$out" 2>/dev/null || cat "$out")"
}

cmd_join() {
  local usage="usage: distributed.sh join (<package> | --core <core address[:port]> --name <node>) <this machine address> [--no-mediamtx]"
  local package="" core="" name="" address="" mediamtx=1
  while (($#)); do
    case "$1" in
      --core) core=${2:?$usage}; shift 2 ;;
      --name) name=${2:?$usage}; shift 2 ;;
      --no-mediamtx) mediamtx=0; shift ;;
      -*) die "unknown option '$1'" ;;
      *) if [[ -z $core && -z $package ]]; then package=$1; elif [[ -z $address ]]; then address=$1; else die "$usage"; fi; shift ;;
    esac
  done
  [[ -n $address && ( -n $package || ( -n $core && -n $name ) ) ]] || die "$usage"
  [[ ! -e "$DIST" ]] || die "$DIST exists: this machine is part of a deployment already"
  umask 077
  if [[ -n $core ]]; then
    package=$(mktemp); trap 'rm -f "$package"' EXIT
    fetch_package "$package" "$core" "$name" "$address" "$mediamtx"
  fi
  mkdir -p "$DIST/tokens"
  tar -xzf "$package" -C "$DIST"
  # The core's passwords and ports go into distributed.env (write_machine_env), on top of this
  # machine's own .env, so that this node's services and components use the same as the core's.
  [[ -f "$DIST/core.env" ]] || die "$package has no core.env"
  write_machine_env node "$address" "$(cat "$DIST/name")" "$mediamtx"
  write_components "$(env_value POSTGRES_HOST)" "$(env_value RABBITMQ_HOST)"
  write_sidecars node "$(cat "$DIST/name")"
  echo "node ready: $DIST. Start it with: scripts/distributed.sh up"
}

case "${1:-}" in
  core) shift; cmd_core "$@" ;;
  join) shift; cmd_join "$@" ;;
  up|down|ps|logs|pull|restart|stop|start|config|exec)
    [[ -f "$DIST/distributed.env" ]] || die "run 'distributed.sh core' or 'distributed.sh join' first"
    profiles=(--profile "$(sed -n 's/^TAVMS_ROLE=//p' "$DIST/distributed.env")")
    if grep -qx 'TAVMS_ROLE=node' "$DIST/distributed.env" && grep -qx 'TAVMS_MEDIAMTX=1' "$DIST/distributed.env"; then
      profiles+=(--profile node-mediamtx)
    fi
    "${COMPOSE[@]}" "${profiles[@]}" "$@" ;;
  *) sed -n '2,25p' "$0"; exit 2 ;;
esac
