#!/usr/bin/env bash
set -euo pipefail

# Publishes the C++ base images (ta-deps, roi-deps) under their content tags, for CI to build
# on. Run it whenever ta_vms/ta-deps/ or the encoder's docker/intel/ changes: CI cannot build
# them itself -- ta-deps takes ~4 hours, which a hosted runner's 6-hour job limit and ~14 GB of
# disk do not reliably fit -- and fails with a pointer here when the tag it needs is missing.
#
# Authentication: run `docker login` beforehand, or export DOCKER_USER + DOCKER_TOKEN in the
# environment — credentials are never stored in this repository.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TA_VMS_DIR="${TA_VMS_DIR:-$SCRIPT_DIR/../../ta_vms}"
export TA_VMS_DIR
NAMESPACE="treealarm"
REGISTRY="docker.io"

if [ -n "${DOCKER_TOKEN:-}" ]; then
    echo "${DOCKER_TOKEN}" | docker login "$REGISTRY" -u "${DOCKER_USER:?DOCKER_USER must be set when DOCKER_TOKEN is used}" --password-stdin
fi

"$SCRIPT_DIR/build-deps-images.sh"

TAGS="$("$TA_VMS_DIR/scripts/deps-image-tags.sh")"
for remote in \
    "$NAMESPACE/ta-deps:$(sed -n 's/^TA_DEPS_TAG=//p' <<<"$TAGS")" \
    "$NAMESPACE/roi-deps:$(sed -n 's/^ROI_DEPS_TAG=//p' <<<"$TAGS")"; do
    echo "=== Pushing $remote ==="
    docker push "$remote"
done

echo "=== Done ==="
