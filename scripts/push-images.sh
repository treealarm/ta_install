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
    "ta_vms-vmsutils:latest        $NAMESPACE/vmsutils:latest"
    "ta_vms-vmssingleton:latest    $NAMESPACE/vmssingleton:latest"
    "ta_vms-vmssquaresingleton:latest $NAMESPACE/vmssquaresingleton:latest"
    "ta_vms-vmslogger:latest       $NAMESPACE/vmslogger:latest"
    "ta_vms-vmscfg:latest          $NAMESPACE/vmscfg:latest"
    "ta_vms-vmsonvif:latest        $NAMESPACE/vmsonvif:latest"
    "ta_vms-vmsonvif-actors:latest $NAMESPACE/vmsonvif-actors:latest"
    "ta_vms-vmsfs:latest           $NAMESPACE/vmsfs:latest"
    "ta_vms-vmsanalyticssingleton:latest    $NAMESPACE/vmsanalyticssingleton:latest"
    "ta_vms-vms:latest             $NAMESPACE/vms:latest"
    "treealarm/roitrc:latest       $NAMESPACE/roitrc:latest"
    "ta_vms-web_vms:latest         $NAMESPACE/web_vms:latest"
    "analytics-worker:latest      $NAMESPACE/analytics-worker:latest"
)

for pair in "${PAIRS[@]}"; do
    local_img=$(echo "$pair" | awk '{print $1}')
    remote_img=$(echo "$pair" | awk '{print $2}')

    echo "=== Pushing $local_img -> $remote_img ==="
    docker tag "$local_img" "$remote_img"
    docker push "$remote_img"
done

echo "=== Done ==="
