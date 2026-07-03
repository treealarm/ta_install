#!/usr/bin/env bash
set -euo pipefail

# Tags the locally built images (see build-images.sh) as treealarm/* and pushes them.
#
# Authentication: run `docker login` beforehand, or export DOCKER_USER + DOCKER_TOKEN in the
# environment — credentials are never stored in this repository.

NAMESPACE="treealarm"
REGISTRY="docker.io"

if [ -n "${DOCKER_TOKEN:-}" ]; then
    echo "${DOCKER_TOKEN}" | docker login "$REGISTRY" -u "${DOCKER_USER:?DOCKER_USER must be set when DOCKER_TOKEN is used}" --password-stdin
fi

# local image -> remote image
declare -a PAIRS=(
    "vms_rec-vmsutils:latest         $NAMESPACE/vmsutils:latest"
    "vms_rec-vmslogger:latest        $NAMESPACE/vmslogger:latest"
    "vms_rec-vmscfg:latest           $NAMESPACE/vmscfg:latest"
    "vms_rec-vmsonvif:latest         $NAMESPACE/vmsonvif:latest"
    "vms_rec-vmsonvif-actors:latest  $NAMESPACE/vmsonvif-actors:latest"
    "vms_rec-vmsfs:latest            $NAMESPACE/vmsfs:latest"
    "vms_rec-vmsanalytics:latest     $NAMESPACE/vmsanalytics:latest"
    "vms_rec-vms:latest              $NAMESPACE/vms:latest"
    "vms_rec-web_vms:latest          $NAMESPACE/web_vms:latest"
    "analytics-worker:latest         $NAMESPACE/analytics-worker:latest"
)

for pair in "${PAIRS[@]}"; do
    local_img=$(echo "$pair" | awk '{print $1}')
    remote_img=$(echo "$pair" | awk '{print $2}')

    echo "=== Pushing $local_img -> $remote_img ==="
    docker tag "$local_img" "$remote_img"
    docker push "$remote_img"
done

echo "=== Done ==="
