#!/usr/bin/env bash
set -euo pipefail

# Builds every product image from sibling source checkouts (override the paths via env when the
# checkouts live elsewhere). Publish afterwards with push-images.sh.

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VMS_REC_DIR="${VMS_REC_DIR:-$SCRIPT_DIR/../../vms_rec}"
VIDEO_A_DIR="${VIDEO_A_DIR:-$SCRIPT_DIR/../../video_a}"

for dir in "$VMS_REC_DIR" "$VIDEO_A_DIR"; do
    [ -d "$dir" ] || { echo "Source checkout not found: $dir (set VMS_REC_DIR/VIDEO_A_DIR)"; exit 1; }
done

# 1. Base image with prebuilt C++ deps for media_server — only when missing (it's huge and
#    changes rarely; `docker rmi vms-deps` to force a rebuild).
if ! docker image inspect vms-deps &>/dev/null; then
    echo "=== Building vms-deps ==="
    docker build -t vms-deps "$VMS_REC_DIR/vms-deps"
else
    echo "=== vms-deps already exists, skipping ==="
fi

# 2. All vms_rec services (produces vms_rec-* images via the dev compose build definitions).
#    Built one at a time, not `compose build`'s default parallel mode: a parallel build peaks
#    higher on memory (e.g. linking the C++ `vms` target alongside a `web_vms` dotnet build can
#    exceed a resource-limited Docker VM), so serialize to keep peak usage down.
echo "=== Building vms_rec services ==="
BUILDABLE_SERVICES=$(docker compose -f "$VMS_REC_DIR/docker-compose.yml" --profile app config 2>/dev/null | python3 -c "
import sys, yaml
d = yaml.safe_load(sys.stdin)
for name, svc in d.get('services', {}).items():
    if 'build' in svc:
        print(name)
")
for svc in $BUILDABLE_SERVICES; do
    echo "--- Building $svc ---"
    docker compose -f "$VMS_REC_DIR/docker-compose.yml" --profile app build "$svc"
done

# 3. video_a analytics worker — its Dockerfile builds FROM vms-deps (step 1), so this only
#    compiles video_a's own small source tree, not protobuf/grpc/spdlog/ffmpeg/openvino again.
echo "=== Building analytics-worker ==="
docker build -t analytics-worker "$VIDEO_A_DIR"

echo "=== Done ==="
