# ta_install

Standalone product deployment for the TreeAlarm VMS: the `ta_vms` services plus the `video_a`
analytics worker, packaged as one docker-compose stack. Self-contained — its own database, its own
broker, its own (isolated, default) docker network, no dependency on the `Square`/`multitenant_admin`
stack.

It pulls prebuilt images (`treealarm/...:latest`) from Docker Hub, so it reflects whatever was
last published with `scripts/push-images.sh` — no source checkouts are needed to *run* it.

## Run it

```sh
docker compose --env-file .env up -d
```

Postgres, RabbitMQ, MediaMTX, Dapr sidecars, all `vms*`/`web_vms` services and the analytics worker
come up together. The first start takes longer while images are pulled.

## What you get

- Web UI: `http://localhost:5134` (`WEB_VMS_PORT` in `.env`)
- MediaMTX: RTSP `8554`, WebRTC signalling `8889`, WebRTC media `8189/udp` — that last one goes
  straight to the browser, so live video will not play if it is blocked. MediaMTX's own HLS
  (`8888`) is deliberately not published: archive HLS is served by the gateway on `5134`.
- Video analytics: person/vehicle + face detection with crops in the Events gallery

Square integration is **off by default** — `KEYCLOAK_URL` and `SQUARE_*` vars are empty in
`.env`, so the UI runs with no login screen and no push to Square.

## Before a real deployment

`.env` ships working defaults so the stack starts unattended. Replace these:

- `VMS_MEDIAMTX_PUBLISH_PASS` / `VMS_MEDIAMTX_READ_PASS` — anyone with them can pull or overwrite
  any camera stream straight from MediaMTX.
- `VMS_WHEP_SESSION_KEY` — seals the WHEP session tokens handed to browsers;
  `openssl rand -base64 32` (must decode to 32 bytes).
- `POSTGRES_PASSWORD` — in **two** places: `.env` and the connection string in
  `init_files/dapr_components/statestore.yaml`, which backs the Dapr state store. Dapr cannot read
  environment variables in component metadata, so the component carries its own copy. Change one
  and not the other and `db-init` stops the stack on the next start, naming the file.
- `RABBITMQ_DEFAULT_PASS` — same story, in `.env` and in
  `init_files/dapr_components/orderpubsub.yaml`, which is the message bus. `db-init` checks this
  pair too. The broker itself is not published on the network; only its management UI is, and only
  on `127.0.0.1:15672`.

### Hardware encoding on a host with an Intel iGPU

`roitrc` re-encodes finished archive clips, keeping the regions analytics found sharp. It runs on
the CPU by default, which works everywhere and is several times slower — that only affects how
quickly the backlog of clips drains, never whether it drains.

To give it the iGPU:

```bash
cp docker-compose.gpu.yml.example docker-compose.override.yml
```

Compose merges `docker-compose.override.yml` automatically, so the `up` command above does not
change. The device is not in `docker-compose.yml` on purpose: `devices:` is resolved when the
container is created, so naming `/dev/dri` on a host that has none fails the create outright rather
than falling back — and the fallback lives inside the process, which never starts.

Set the group id in the copy before starting:

```bash
stat -c '%g' /dev/dri/renderD128
```

It has to be the number. `group_add: "render"` resolves the name inside the container, and the
roitrc image is a bare `ubuntu:24.04` with no `render` group at all. Get it wrong and the device is
there but cannot be opened, so the encoder falls back to the CPU and the passthrough looks like it
did nothing.

Check it worked — each command answers a different failure:

```bash
docker compose exec roitrc ls -l /dev/dri   # passed through at all?
docker compose exec roitrc vainfo           # driver loaded, encode profiles?
docker compose exec roitrc vpl-inspect      # a hardware oneVPL implementation?
```

## Databases

The `db-init` service creates them. It runs on every `docker compose up`, creates each database
named by a `POSTGRES_*_DB` variable in `.env` that does not exist yet, cross-checks the Dapr
components against `.env`, and exits; the services wait for it to finish. It is idempotent, so
adding a service later means adding its `POSTGRES_<NAME>_DB` to `.env` and nothing else.

To see what it would do without changing anything:

```sh
docker compose run --rm db-init --check
```

## Analytics models

Baked into the `analytics-worker` image at build time (see `video_a/Dockerfile` and
`video_a/models/`) — nothing to fetch or mount, works out of the box:

- `face_detector.xml/.bin` — OMZ face-detection-0205, Apache-2.0.
- `primary_detector.xml/.bin` (person/vehicle) — a YOLO11n OpenVINO export. **Licensing note:**
  Ultralytics YOLO11 is AGPL-3.0. Baking its weights into an image that gets deployed to
  customers over a network is a conscious, deliberate call made for now to get a working turnkey
  deploy — it has not been reconciled with AGPL's network-use clause (which can require
  open-sourcing the whole product, or an Ultralytics Enterprise license for closed distribution).
  Revisit before any real customer rollout: either clear the licensing properly or swap in a
  permissively-licensed detector.
- `person_embedder.onnx/.xml` (OPTIONAL) — an OSNet body ReID model (256×128 input, 512-d output),
  used for cross-camera / cross-gap object re-identification that drives event grouping. **Not
  bundled** — drop it into `video_a/models/` to enable body re-id. When absent, the worker logs
  "body re-id disabled" and simply emits no embedding; analytics still works, but each detection
  gets its own (per-track) object id and events group only within a single track.

## Adding a camera

No camera is pre-configured. Open the web UI and add one the normal way (ONVIF discovery or a
manual RTSP URL). To enable analytics on it: Admin → Analytics → add a watch (camera + stream +
classes), then arm the camera.

## Building and publishing images

Requires sibling source checkouts (`../ta_vms`, `../video_a`; override with
`TA_VMS_DIR`/`VIDEO_A_DIR`):

```sh
scripts/build-images.sh         # ta-deps (once) + all ta_vms services + analytics-worker
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
