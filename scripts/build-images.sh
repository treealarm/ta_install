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

# 1. C++ base images (ta-deps for media_server and analytics-worker, roi-deps for roitrc):
#    reused when present under their content tag, else pulled, else built -- see
#    build-deps-images.sh. A base built from an older Dockerfile no longer passes for current.
TA_VMS_DIR="$TA_VMS_DIR" "$SCRIPT_DIR/build-deps-images.sh"

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
