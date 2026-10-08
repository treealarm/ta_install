#!/usr/bin/env bash
set -euo pipefail

# Makes the two C++ base images available locally, under both names they are used by:
#
#   ta-deps,  treealarm/ta-deps:<tag>    -- base of media_server (`vms`) and video_a's analytics-worker
#   roi-deps, treealarm/roi-deps:<tag>   -- base of roitrc
#
# The bare name is what the Dockerfiles say `FROM`; the tagged one is what CI pulls (see
# ta_vms/.github/workflows/images.yml). <tag> is the git tree hash of the directory each image is
# built from, computed by ta_vms/scripts/deps-image-tags.sh -- so an image already present under
# that tag is the right one by construction, and one built from an older Dockerfile is not
# mistaken for it the way a bare `ta-deps` used to be.
#
# For each image, in order: use the local one, else pull the published one (same tag, same
# content), else build it. Building ta-deps takes ~4 hours, mostly openvino.

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TA_VMS_DIR="${TA_VMS_DIR:-$SCRIPT_DIR/../../ta_vms}"
NAMESPACE="treealarm"

[ -d "$TA_VMS_DIR" ] || { echo "Source checkout not found: $TA_VMS_DIR (set TA_VMS_DIR)"; exit 1; }

TAGS="$("$TA_VMS_DIR/scripts/deps-image-tags.sh")"
TA_DEPS_TAG="$(sed -n 's/^TA_DEPS_TAG=//p' <<<"$TAGS")"
ROI_DEPS_TAG="$(sed -n 's/^ROI_DEPS_TAG=//p' <<<"$TAGS")"

# ensure_image <local name> <tag> <dockerfile> <context>
ensure_image() {
    local name="$1" tag="$2" dockerfile="$3" context="$4"
    local remote="$NAMESPACE/$name:$tag"

    if docker image inspect "$remote" &>/dev/null; then
        echo "=== $remote already present locally ==="
    elif docker pull "$remote" 2>/dev/null; then
        echo "=== Pulled $remote ==="
    else
        echo "=== Building $remote ==="
        docker build -t "$remote" -f "$dockerfile" "$context"
    fi
    docker tag "$remote" "$name"
}

ensure_image ta-deps "$TA_DEPS_TAG" "$TA_VMS_DIR/ta-deps/Dockerfile" "$TA_VMS_DIR/ta-deps"

# roitrc has its own base: it is the only consumer that needs qsv, and it does not link
# openvino, which is most of what ta-deps spends its time on. The Dockerfile belongs to the
# encoder and arrives with the submodule.
ensure_image roi-deps "$ROI_DEPS_TAG" \
    "$TA_VMS_DIR/roitrc/ta_roienc/docker/intel/Dockerfile.deps" "$TA_VMS_DIR/roitrc/ta_roienc/docker/intel"
