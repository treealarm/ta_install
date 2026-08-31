#!/usr/bin/env bash
set -euo pipefail

# Builds every product image from sibling source checkouts (override the paths via env when the
# checkouts live elsewhere). Publish afterwards with push-images.sh.

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TA_VMS_DIR="${TA_VMS_DIR:-$SCRIPT_DIR/../../ta_vms}"
VIDEO_A_DIR="${VIDEO_A_DIR:-$SCRIPT_DIR/../../video_a}"

for dir in "$TA_VMS_DIR" "$VIDEO_A_DIR"; do
    [ -d "$dir" ] || { echo "Source checkout not found: $dir (set TA_VMS_DIR/VIDEO_A_DIR)"; exit 1; }
done

# The encoder is a submodule of ta_vms, not a directory in it: an unpopulated checkout
# fails here with a fix rather than deep inside cmake with a missing header.
[ -f "$TA_VMS_DIR/roitrc/sve/CMakeLists.txt" ] || {
    echo "roitrc/sve is empty -- run: git -C $TA_VMS_DIR submodule update --init --recursive"
    exit 1
}

# 1. Base image with prebuilt C++ deps for media_server — only when missing (it's huge and
#    changes rarely; `docker rmi ta-deps` to force a rebuild).
if ! docker image inspect ta-deps &>/dev/null; then
    echo "=== Building ta-deps ==="
    docker build -t ta-deps "$TA_VMS_DIR/ta-deps"
else
    echo "=== ta-deps already exists, skipping ==="
fi

# 1b. Same again for roitrc, which has its own base: it is the only consumer that needs
#     qsv/vaapi, and it does not link openvino, which is most of what ta-deps spends its time
#     on. The Dockerfile belongs to the encoder and arrives with the submodule; the service
#     image itself is built below with the rest of ta_vms.
if ! docker image inspect roi-deps &>/dev/null; then
    echo "=== Building roi-deps ==="
    docker build -t roi-deps -f "$TA_VMS_DIR/roitrc/sve/docker/intel/Dockerfile.deps" \
        "$TA_VMS_DIR/roitrc/sve/docker/intel"
else
    echo "=== roi-deps already exists, skipping ==="
fi

# 2. All ta_vms services (produces ta_vms-* images via the dev compose build definitions).
#    Built one at a time, not `compose build`'s default parallel mode: a parallel build peaks
#    higher on memory (e.g. linking the C++ `vms` target alongside a `web_vms` dotnet build can
#    exceed a resource-limited Docker VM), so serialize to keep peak usage down.
echo "=== Building ta_vms services ==="
BUILDABLE_SERVICES=$(docker compose -f "$TA_VMS_DIR/docker-compose.yml" --profile app config 2>/dev/null | python3 -c "
import sys, yaml
d = yaml.safe_load(sys.stdin)
for name, svc in d.get('services', {}).items():
    if 'build' in svc:
        print(name)
")
for svc in $BUILDABLE_SERVICES; do
    echo "--- Building $svc ---"
    docker compose -f "$TA_VMS_DIR/docker-compose.yml" --profile app build "$svc"
done

# 3. video_a analytics worker — its Dockerfile builds FROM ta-deps (step 1), so this only
#    compiles video_a's own small source tree, not protobuf/grpc/spdlog/ffmpeg/openvino again.
echo "=== Building analytics-worker ==="
docker build -t analytics-worker "$VIDEO_A_DIR"

echo "=== Done ==="
