# Findings on a real Windows machine

What the installer did on real hardware, as opposed to the CI runner. Each entry says what was
checked, what happened, why, and what was done about it. Newest run first.

## 2026-10-21: v0.1.3, upgraded from v0.1.2

**Machine.** Windows 10 Pro 22H2 (19045), Russian locale; Intel Core i5-750 (4 cores, 2009, no
Quick Sync); Realtek Ethernet, Radmin VPN, Bluetooth PAN, no Hyper-V. Cameras: OnvifSim on another
LAN host (`tests/onvifsim/onvifsim_lab.py --vms http://<box>:5134 --sim-host <sim host>` in ta_vms).

### Upgrade 0.1.2 -> 0.1.3: where the six minutes go

`/VERYSILENT` upgrade with `/LOG`, timed from outside: **376 s**, exit 0. From the Inno log, the
TaVmsHost log and the install transcript:

| Phase | Time | Why |
|---|---|---|
| setup.exe start to its log opening | 8 s | the loader checks the 523 MB file |
| extracting `Stop-TaVms.ps1` | 38 s | the archive is solid and the entry came last: the whole stage was decompressed to reach it |
| stopping TaVmsHost, RabbitMQ, PostgreSQL | 13 s | vmsfs 4 s, the sidecars 3 s, the rest under 1 s |
| `[InstallDelete]` of the old directories | 12 s | |
| unpacking | 146 s | 1.9 GB in 29 000 files, 684 MB / 16 000 of them pgAdmin 4 (below) |
| VC++ redistributable | 90 s | runs although the same version is installed; it tries to create a System Restore point first (VSS errors 8193/12292 in the Application log, every 15 s) |
| PostgreSQL and RabbitMQ re-registration, `rabbitmqctl` | about 40 s | RabbitMQ's own boot is 20 s of it ("Still booting" twice); each `rabbitmqctl` call is 1.5 s |
| TaVmsHost start to web interface up | 27 s | 24 processes one after another |

Measured separately and negligible: `TaVmsHost env` 0.2 s per call, `icacls` on the data directory
0.1 s (2 900 files).

**Fixed:**
- `scripts/Build-Stage.ps1` removed `pgsql\pgAdmin`, but the EDB archive calls it `pgAdmin 4`.
  It is removed now, with Erlang's `.pdb` files, docs, sources and examples; the stage fails when
  `pgsql` or `erlang` outgrows a size ceiling.
- `Install-TaVms.ps1` runs vc_redist only when the registered runtime is older than the bundled one.
- `ta_vms.iss` lists `Stop-TaVms.ps1` first in `[Files]`.
- The install transcript's step markers carry the time.

### After the upgrade
- Three services Running, `DisplayVersion` 0.1.3, "All 24 processes started" 21 s after TaVmsHost
  started.
- **Name resolution switched to sqlite** by itself: `dapr\config.yaml` names `sqlite`,
  `dapr\nameresolution.db` was created, no daprd log mentions mDNS after the upgrade. `site.env`
  had no resolver of its own, so nothing overrode `windows.env`.
- **Stored paths.** The 332 clips written by 0.1.2 have mixed separators
  (`sim-master2\main\2026/10/20/12\20261020T121401.mp4`), every clip since is `/`-only
  (`sim-master2/main/2026/10/21/07/20261021T074033.mp4`). An old clip still plays: its HLS playlist
  builds, a fragment and the whole-file download (61 s, H.264) come back 200. No migration needed.
- **Licences:** `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `licenses\vcpkg\` (25 files),
  `licenses\Dapr.txt`, `licenses\Erlang-OTP.txt`, and every `licenseFile` `versions.json` names are
  in the installation directory.
- **web-vms's sidecar** logs `error getting topics from app: invalid character '<'` on every start:
  web_srv has no `/dapr/subscribe`, so the SPA fallback answered with index.html. Fixed in ta_vms
  (an empty subscription list).
- **PTZ** after the resolver switch: the OnvifSim bench's `provision`, `calibrate` and
  `aim-check` pass; aim error 0.000°, the person wholly in the PTZ camera's picture 13 of 13 times.

### Storage on a Cyrillic path

A storage at `C:\ProgramData\TreeAlarm\VMS\Архив` (a test only: nothing in the product uses
Cyrillic, but an administrator may pick such a directory), with one camera assigned to it for 20
minutes:
- 20 segments written there; `clips.file_path` is `/`-separated, the storage root stays in VmsCfg.
- HLS playback, the whole-file download (what "export" of an archive clip is) and the archive
  frame JPEG — which vms opens with FFmpeg by absolute path — all answer 200.
- No error in the vms or VmsFs log.
- Left behind on the machine as a disabled storage, `arch-cyr`.

Not checked on hardware: a Cyrillic `VMS_EXPORT_BASE_PATH`. The code read it as a narrow string,
which Windows converts through the ANSI code page; fixed in ta_vms (read as UTF-8) without a run.

### Edge import

Not checked: OnvifSim implements no ONVIF Recording/Search/Replay, and the one camera with an
archive (192.168.2.64) is not reachable from the test LAN. The paths it records go through the same
UTF-8, `/`-separated helpers as live recording.

### Live video of a newly added camera (all platforms)

Found while measuring HEVC: a camera added while vms runs never reached MediaMTX, and vms logged
`failed to write rtsp header` every 3 s. The cause, reproduced on the Linux dev stack:
1. web_srv asks VmsCfg for the camera's MediaMTX server before the camera row exists, and gets none.
2. vms builds the push target from the environment's default, without the server's publish
   credentials; MediaMTX answers 401.
3. The first WHEP offer assigns the server, but VmsCfg did not announce that write, so vms kept the
   old target until it restarted.

Fixed in ta_vms: VmsCfg announces the assignment (`server-config-changed`) and vms re-points the
stream within a second; the RTSP log line now says why the header failed.

### HEVC transcoding CPU

1080p25 HEVC at 4 Mbit/s (`testsrc2`, libx265), published into the machine's MediaMTX, transcoded
to H.264 for one WebRTC viewer, with `transcode_hevc` on for the MediaMTX server:

| | vms CPU | of the machine |
|---|---|---|
| idle, stream connected, no viewer | 3–4 % of a core | 1 % |
| one viewer, transcoding | 135 % of a core | 34 % |

The viewer got a steady 25 fps at 1920x1080, first frame 2.2 s after the offer.
- The hardware engine is tried first and fails at once:
  `failed to open session: no H.264 encoder available: h264_vaapi: not in this FFmpeg build`,
  in the same second the stream starts. The CPU path behind it (libx264 `veryfast`,
  `zerolatency`) does the work.
- On this machine that is two, at most three, transcoded HEVC streams.

### Reboot

The stack comes back by itself.
- Boot to TaVmsHost start: 56 s.
- TaVmsHost start to all 24 processes: 37 s; RabbitMQ is the wait, about 14 s.
- Recording resumes within a minute; live video works.

**Fixed:** shutdown. Windows tells each child, on its hidden console, to exit about as soon as
the shutdown starts. The SCM's stop reached TaVmsHost 20 s later, and the supervisor restarted
every child that exited in the meantime. TaVmsHost now starts its stop from the same console event.
On the next reboot that came 5 s after the children, with two restarts instead of twenty. The last
segment before the reboot was closed and indexed in both cases.

### Uninstall and reinstall

- Uninstall, `/VERYSILENT`: 43 s. Afterwards there are no TaVmsHost, TaVms.Postgres or
  TaVms.RabbitMQ services, no uninstall key, no leftover processes (epmd included), no
  installation directory, firewall rules or shortcuts.
- `C:\ProgramData\TreeAlarm\VMS` is kept whole: `site.env`, the database, RabbitMQ's data, logs
  and the archive (391 files).
- The machine's own PostgreSQL 12 and 16 kept running beside ours throughout.
- Installing 0.1.3 again over the kept data: 298 s. All seven cameras came back, and a clip
  recorded by 0.1.2 still plays.

### Not checked, and why
- **WS-Discovery behind Hyper-V's vEthernet adapters.** OnvifLib skips every adapter whose name
  contains `veth`, which includes `vEthernet (...)`. This machine has no Hyper-V, so the problem
  could not be confirmed and OnvifLib was left alone. ONVIF discovery itself was not exercised
  either: the OnvifSim container does not answer multicast from another host.
- **Security, known and deferred:** without Keycloak the web interface is open to the LAN, and
  `GET /api/media-mtx/servers` returns the MediaMTX publish and read passwords with it. Own
  authorisation for standalone installs is a separate requirement.
