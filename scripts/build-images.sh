#!/usr/bin/env bash
set -euo pipefail

# Builds every product image from sibling source checkouts (override the paths via env when the
# checkouts live elsewhere). Publish afterwards with push-images.sh.

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TA_VMS_DIR="${TA_VMS_DIR:-$SCRIPT_DIR/../../ta_vms}"
VIDEO_A_DIR="${VIDEO_A_DIR:-$SCRIPT_DIR/../../video_a}"
ROI_TRANSCODE_DIR="${ROI_TRANSCODE_DIR:-$SCRIPT_DIR/../../roi_transcode}"

for dir in "$TA_VMS_DIR" "$VIDEO_A_DIR" "$ROI_TRANSCODE_DIR"; do
    [ -d "$dir" ] || { echo "Source checkout not found: $dir (set TA_VMS_DIR/VIDEO_A_DIR/ROI_TRANSCODE_DIR)"; exit 1; }
done

# 1. Base image with prebuilt C++ deps for media_server — only when missing (it's huge and
#    changes rarely; `docker rmi ta-deps` to force a rebuild).
if ! docker image inspect ta-deps &>/dev/null; then
    echo "=== Building ta-deps ==="
    docker build -t ta-deps "$TA_VMS_DIR/ta-deps"
else
    echo "=== ta-deps already exists, skipping ==="
fi

# 1b. roi_transcode builds its own images. It left the ta_vms monorepo on 2026-08-31 -- it is
#     the customer's deliverable under its own specification -- and it owns both its base
#     (roi-deps: the only consumer that needs qsv/vaapi, and the only one that does not link
#     openvino, which is most of what ta-deps spends its time on) and treealarm/roitrc itself.
#     Its script skips the base when the tag is already there, exactly as this one does.
echo "=== Building roitrc (in $ROI_TRANSCODE_DIR) ==="
"$ROI_TRANSCODE_DIR/docker/build.sh"

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
