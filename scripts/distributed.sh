#!/usr/bin/env bash
# A deployment over several machines: one core, any number of nodes (ta_vms CLAUDE.md, "Distributed
# layout"). Writes data/distributed/, which docker-compose.distributed.yml reads, and runs compose
# with it. A single machine needs none of this: docker-compose.yml is the standalone deployment.
#
#   On the core:
#     scripts/distributed.sh core <this machine's address>
#     scripts/distributed.sh package <node name>        -> data/distributed/node-<name>.tar.gz
#     scripts/distributed.sh up | down | ps | logs ...    (compose with the right files)
#   On a node, with the package copied over:
#     scripts/distributed.sh join <package> <this machine's address>
#     scripts/distributed.sh up
#
# What crosses machines, and how it is protected:
#   - every Dapr sidecar's internal gRPC port: mutual TLS, certificates from Dapr Sentry on the core.
#     Sentry signs a certificate for an app id only against a token for that app id (its jwks
#     validator); without one it would sign anything for anybody (its "insecure" default).
#   - app ids are found through HashiCorp Consul: a server on the core, an agent on every node,
#     gossip encrypted with a key of this deployment. The agents' HTTP API stays on the loopback.
#   - PostgreSQL, RabbitMQ, Dapr placement and MediaMTX on the core, behind their passwords and the
#     core's firewall (see README.md).
#
# Needs openssl, jq and docker compose.
set -euo pipefail

cd "$(dirname "$0")/.."
DIST=data/distributed
COMPOSE=(docker compose -f docker-compose.distributed.yml --env-file .env --env-file "$DIST/distributed.env")
# Sentry's own SPIFFE id, which every token names as its audience; and the trust domains Dapr uses
# by default outside Kubernetes: "localhost" for the control plane, "public" for applications.
SENTRY_ID=spiffe://localhost/ns/default/dapr-sentry

die() { echo "error: $*" >&2; exit 1; }
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
env_value() { sed -n "s/^$1=//p" .env | tail -n1 | tr -d '"'; }

# A JWT for Sentry's jwks validator, signed with the deployment's Ed25519 key. No "exp": the token is
# the credential of a machine's sidecar, and what revokes it is a new key in the JWKS.
mint_token() {
  local header payload signature signed
  header=$(printf '{"alg":"EdDSA","typ":"JWT","kid":"tavms"}' | b64url)
  payload=$(printf '{"sub":"%s","aud":["%s"],"iat":%s}' "$1" "$SENTRY_ID" "$(date +%s)" | b64url)
  # A file, not a pipe: Ed25519 signs the whole message at once, and openssl has to know its size.
  signed=$(mktemp)
  printf '%s.%s' "$header" "$payload" > "$signed"
  signature=$(openssl pkeyutl -sign -rawin -inkey "$DIST/core/jwt.key" -in "$signed" | b64url)
  rm -f "$signed"
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

# The state store and the bus, at the core's address. Dapr does not expand variables in component
# metadata, so these are written out with the values from .env.
write_components() {
  local host=$1 user pass db ruser rpass port rport
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
    value: "amqp://$ruser:$rpass@$host:$rport/"
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

  # The key that signs the sidecars' tokens, and the JWKS Sentry checks them against.
  openssl genpkey -algorithm ed25519 -out "$DIST/core/jwt.key"
  local x
  x=$(openssl pkey -in "$DIST/core/jwt.key" -pubout -outform DER | tail -c 32 | b64url)
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
          source: '{"keys":[{"kty":"OKP","crv":"Ed25519","x":"$x","kid":"tavms","alg":"EdDSA","use":"sig"}]}'
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
  local role=$1 address=$2 node=$3 pairs
  pairs="vms:vmsfs:vmsonvif:vmslogger"
  [[ -f "$DIST/nodes" ]] && pairs+=$(sed 's/^/,/' "$DIST/nodes" | tr -d '\n')
  {
    cat "$DIST/shared.env"
    # A profile, not COMPOSE_PROFILES: the package step asks compose for the node profile on the
    # core, and must not get the core's sidecars (and tokens) with it.
    echo "TAVMS_ROLE=$role"
    # The containers run as the owner of $DIST, whose keys and tokens nobody else may read.
    echo "TAVMS_UID=$(id -u)"
    echo "TAVMS_GID=$(id -g)"
    echo "TAVMS_NODE_NAME=$node"
    echo "DAPR_HOST_IP=$address"
    echo "MEDIA_SERVER_PAIRS=$pairs"
    # What differs from .env, as windows/config/windows.env is for Windows: no compose network to
    # name hosts by, so the core's address; the ports Dapr's control plane listens on; and what a
    # distributed deployment does not run yet.
    local core
    core=$(sed -n 's/^CORE_ADDRESS=//p' "$DIST/shared.env")
    echo "POSTGRES_HOST=$core"
    echo "VMS_MTX_HOST=$core"
    echo "KEYCLOAK_URL=http://127.0.0.1:$(env_value KEYCLOAK_PORT)"
    echo "DAPR_PLACEMENT_PORT=6050"
    echo "DAPR_SENTRY_PORT=6055"
    echo "DAPR_ANALYTICS_WORKER_APP_IDS="
    echo "DAPR_SQUARE_SINGLETON_APP_ID="
    echo "SQUARE_INTEGRATION_SINK_APP_ID="
  } > "$DIST/distributed.env"
}

cmd_package() {
  local node=${1:?usage: distributed.sh package <node name>}
  [[ -f "$DIST/core/jwt.key" ]] || die "not a core: run 'distributed.sh core' first"
  [[ "$node" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] || die "node name: lower-case letters, digits and '-'"
  local pair="vms-$node:vmsfs-$node:vmsonvif-$node:vmslogger-$node"
  grep -qxF "$pair" "$DIST/nodes" 2>/dev/null || echo "$pair" >> "$DIST/nodes"
  write_machine_env core "$(sed -n 's/^CORE_ADDRESS=//p' "$DIST/shared.env")" ""

  local tmp
  tmp=$(mktemp -d); trap 'rm -rf "$tmp"' RETURN
  umask 077
  mkdir -p "$tmp/tokens"
  cp "$DIST/shared.env" "$DIST/nodes" "$tmp/"
  cp .env "$tmp/core.env"
  echo "$node" > "$tmp/name"
  # The node's sidecars, by the app ids the compose file gives them under this name.
  while read -r id _; do
    mint_token "spiffe://public/ns/default/$id" > "$tmp/tokens/$id.token"
  done < <(sidecars node "$node")
  tar -czf "$DIST/node-$node.tar.gz" -C "$tmp" .
  echo "wrote $DIST/node-$node.tar.gz: copy it to the node and run 'distributed.sh join' there."
  # MEDIA_SERVER_PAIRS changed in distributed.env, and compose recreates whatever reads it.
  echo "Then run 'scripts/distributed.sh up -d' here, so that the core's services learn the node."
}

cmd_join() {
  local package=${1:?usage: distributed.sh join <package> <this machine address>} address=${2:?this machine address}
  [[ ! -e "$DIST" ]] || die "$DIST exists: this machine is part of a deployment already"
  umask 077
  mkdir -p "$DIST/tokens"
  tar -xzf "$package" -C "$DIST"
  # The core's passwords and settings, so that this node's services and components use the same.
  cp "$DIST/core.env" .env
  write_machine_env node "$address" "$(cat "$DIST/name")"
  write_components "$(sed -n 's/^CORE_ADDRESS=//p' "$DIST/shared.env")"
  write_sidecars node "$(cat "$DIST/name")"
  echo "node ready: $DIST. Start it with: scripts/distributed.sh up"
}

case "${1:-}" in
  core) shift; cmd_core "$@" ;;
  package) shift; cmd_package "$@" ;;
  join) shift; cmd_join "$@" ;;
  up|down|ps|logs|pull|restart|stop|start|config|exec)
    [[ -f "$DIST/distributed.env" ]] || die "run 'distributed.sh core' or 'distributed.sh join' first"
    "${COMPOSE[@]}" --profile "$(sed -n 's/^TAVMS_ROLE=//p' "$DIST/distributed.env")" "$@" ;;
  *) sed -n '2,21p' "$0"; exit 2 ;;
esac
