# ta_install

Standalone product deployment for the TreeAlarm VMS: the `vms_rec` services plus the `video_a`
analytics worker, packaged as one docker-compose stack. Self-contained — its own Redis, its own
(isolated, default) docker network, no dependency on the `Square`/`multitenant_admin` stack.

It pulls prebuilt images (`treealarm/...:latest`) from Docker Hub, so it reflects whatever was
last published with `scripts/push-images.sh` — no source checkouts are needed to *run* it.

## Run it

```sh
docker compose --env-file .env up -d
```

Postgres, Redis, MediaMTX, Dapr sidecars, all `vms*`/`web_vms` services and the analytics worker
come up together. The first start takes longer while images are pulled.

## What you get

- Web UI: `http://localhost:5134` (`WEB_VMS_PORT` in `.env`)
- pgAdmin: `http://localhost:5050` (`admin@admin.com` / `admin123` by default)
- MediaMTX: RTSP `8554`, WebRTC `8889`, HLS `8000`
- Video analytics: person/vehicle + face detection with crops in the Events gallery

Square integration is **off by default** — `KEYCLOAK_URL` and `SQUARE_*` vars are empty in
`.env`, so the UI runs with no login screen and no push to Square.

## Analytics models

Baked into the `analytics-worker` image at build time (see `video_a/Dockerfile` and
`video_a/models/`) — nothing to fetch or mount, works out of the box:

- `face_detector.xml/.bin` — OMZ face-detection-0205, Apache-2.0.
- `primary_detector.xml/.bin` (person/vehicle) — a YOLOv8n OpenVINO export. **Licensing note:**
  Ultralytics YOLOv8 is AGPL-3.0. Baking its weights into an image that gets deployed to
  customers over a network is a conscious, deliberate call made for now to get a working turnkey
  deploy — it has not been reconciled with AGPL's network-use clause (which can require
  open-sourcing the whole product, or an Ultralytics Enterprise license for closed distribution).
  Revisit before any real customer rollout: either clear the licensing properly or swap in a
  permissively-licensed detector.

## Adding a camera

No camera is pre-configured. Open the web UI and add one the normal way (ONVIF discovery or a
manual RTSP URL). To enable analytics on it: Admin → Analytics → add a watch (camera + stream +
classes), then arm the camera.

## Building and publishing images

Requires sibling source checkouts (`../vms_rec`, `../video_a`; override with
`VMS_REC_DIR`/`VIDEO_A_DIR`):

```sh
scripts/build-images.sh         # vms-deps (once) + all vms_rec services + analytics-worker
docker login                    # or export DOCKER_USER + DOCKER_TOKEN
scripts/push-images.sh          # tag treealarm/* and push
```

The first `analytics-worker` build is long (vcpkg + OpenVINO from source); later builds reuse
the layer cache.

## Stopping

```sh
docker compose --env-file .env down
```

Recorded data, detection crops and Postgres state live under `./data` on the host and are **not**
removed by `down` (add `-v` only to wipe everything).
