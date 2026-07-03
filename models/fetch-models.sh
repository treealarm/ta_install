#!/usr/bin/env bash
set -euo pipefail

# Downloads the OpenVINO models the analytics-worker mounts at /models.
#
# face_detector: OMZ face-detection-0205 (Apache-2.0) — fetched directly from the OpenVINO
# storage, no toolchain needed.
#
# primary_detector (person/vehicle): a YOLOv8-style OpenVINO export (see video_a README) — an
# Ultralytics export is not redistributed here for licensing reasons. Export it yourself:
#   pip install ultralytics && python -c "from ultralytics import YOLO; YOLO('yolov8n.pt').export(format='openvino')"
# then copy the resulting .xml/.bin here as primary_detector.xml / primary_detector.bin.

MODELS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OMZ_BASE="https://storage.openvinotoolkit.org/repositories/open_model_zoo/2023.0/models_bin/1"

echo "=== Fetching face-detection-0205 (FP32) ==="
curl -sSL -o "$MODELS_DIR/face_detector.xml" "$OMZ_BASE/face-detection-0205/FP32/face-detection-0205.xml"
curl -sSL -o "$MODELS_DIR/face_detector.bin" "$OMZ_BASE/face-detection-0205/FP32/face-detection-0205.bin"

echo "=== Done ==="
ls -la "$MODELS_DIR"/*.xml "$MODELS_DIR"/*.bin 2>/dev/null || true

if [ ! -f "$MODELS_DIR/primary_detector.xml" ]; then
    echo
    echo "NOTE: primary_detector.xml/.bin are missing — person/vehicle detection will run in"
    echo "stub mode until you export and copy them here (see the comment at the top of this script)."
fi
