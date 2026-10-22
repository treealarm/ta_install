# Findings on a real Windows machine

What the installer did on real hardware, as opposed to the CI runner. Each entry says what was
checked, what happened, why, and what was done about it. Newest run first.

## 2026-10-22: v0.1.6, the first WiX build, upgraded from v0.1.5 (Inno Setup)

The CI build of ta_install bb256ed (ta_vms 2851096), `ta_vms-setup-x64.exe /quiet /norestart`
over the Inno Setup installation of 0.1.5: **335 s**, exit code 0, the web interface answering 4 s
later. setup.exe is 466 MB (Inno 0.1.5: 477), the bare MSI 410 MB.

| Phase | |
|---|---|
| Burn: detect, plan | 8 s |
| system restore point, which then failed (VSS 0x8004230f) | 89 s |
| caching the MSI | 6 s |
| CostFinalize | 25 s |
| InstallValidate: costing finishing, then files in use | 21 s + 29 s |
| Inno Setup's uninstaller, stopping 0.1.5's stack | 32 s |
| ProcessComponents | 4 s |
| copying the files | 27 s |
| `Install-TaVms.ps1` | 74 s |

- **The restore point** is something Burn does before every per-machine apply unless told not
  to; on the CI runners System Restore is off, so it never showed there. It would protect the
  program folder, never the data. Fixed: `DisableSystemRestore="yes"`.
- **Files in use, the slow way.** The stack still runs when InstallValidate looks at the files it
  will replace, and it found 2468 of them held. With `MSIRESTARTMANAGERCONTROL=Disable` it looks
  for each holder by walking the processes, 29 s; it also named WhatsApp and msedgewebview2 as
  holders, for having a DLL of the same name loaded. Fixed: `DisableShutdown`, which detects
  through Restart Manager and still never closes or restarts anything.
- **The Inno step is a one-off** and was slow for the old reasons: 0.1.5's vms killed after 8 s,
  its Keycloak after 5 s (both fixed in 0.1.6). A WiX-to-WiX upgrade stops the stack with
  `Stop-TaVms.ps1` instead.
- Checked after the upgrade: the three services running, `site.env` byte for byte the same, the
  Inno registration and `unins000.exe` gone, one "TreeAlarm VMS" in Apps and Features (0.1.6).
  Package Cache 540 MB before, 541 after: `Cache="remove"` leaves the MSI out of it, Burn keeps only
  its own engine for the uninstall.
- **Stopping the service: 7.8 s, nothing killed.** vms 0.3 s after the request (it used to be
  killed at 8 s), Keycloak 1.3 s with no "Terminate batch job", vmsfs the slowest at 4.4 s.
  Starting it: 0.8 s for the service, the whole stack running within a minute.
- The TreeAlarm admin was put back to `admin`/`admin` with `password-change-required` through
  Keycloak's admin API: logs in, and gets 403 for the cameras until the password is changed.
- The machine has an NVIDIA GeForce GTX 1050 Ti (driver 582.53, NVENC and NVDEC present), so a
  hardware encoder can be tried on it after all; the i5-750 itself has no Quick Sync.
- Not checked yet: the wizard (GUI), a WiX-to-WiX upgrade with the two fixes, an uninstall.

## 2026-10-21: v0.1.5, upgraded from v0.1.4

The first release with Keycloak and user administration (multitenant_admin). Upgrade
`/VERYSILENT`: **216 s**. setup.exe is 477 MB, Keycloak and its Java runtime being the difference.

| Phase | 0.1.4 | 0.1.5 |
|---|---|---|
| loader, extracting `Stop-TaVms.ps1` | 5 s | 8 s |
| stopping the services | 12 s | 12 s |
| deleting the old directories | 9 s | 2 s |
| unpacking | 64 s | 106 s |
| site.env, data directories, PostgreSQL | | 4 s |
| RabbitMQ | 26 s | 29 s |
| firewall, service, web interface up | 29 s | 54 s |

The last row grew because Keycloak's JVM starts beside the 24 other processes and takes the CPU
from them: they came up in 35 s, not 27. Nothing waits for Keycloak itself.

- **Keycloak.** First start 49 s, 21 of them creating its schema in the new `ta_keycloak`
  database, then the realm imported; later starts 16-18 s. 443 MB working set, all of the
  installation's processes 3.2 GB (2.6 GB before).
- **It listened on the LAN.** Keycloak's `start` defaults to a clustered cache, and JGroups bound
  `192.168.1.100:7800` and `*:57800` to look for other Keycloak nodes. Fixed: `KC_CACHE=local`
  (a runtime option; `kc build --cache=local` is not persisted). Since then it listens on the
  loopback only, takes 364 MB and starts in 16 s.
- **Every service stop waited 5 s on `kc.bat`.** Ctrl+C stopped Keycloak's java in 1.1 s, and then
  cmd asked "Terminate batch job (Y/N)?" on its hidden console, until the supervisor gave up and
  killed it. Fixed: the supervisor hands every child an empty stdin, and cmd, reading its end,
  finishes the batch at once (0.1 s, tried in a SYSTEM task as the service runs).
- **vms was killed on every stop**, here and in every earlier log: "vms did not stop within 8s".
  Not Windows: the same 26 s on Linux, with the cameras stopped one at a time and each reader
  waiting out a 5 s reconnect pause or socket timeout. Fixed in ta_vms 86629d4: 0.8 s while
  recording, with the open clip closed and complete.
- `site.env` got `KEYCLOAK_ADMIN_PASSWORD` added, nothing else in it changed.
- Over the LAN, from another machine: without a token every API answers 401, including
  `/api/media-mtx/servers`, which used to hand out the MediaMTX passwords. `admin`/`admin` logs in
  and gets 403 `password_change_required` for everything but the change; a password shorter than 8
  is refused; after the change all seven cameras are there and `admin`/`admin` no longer works.
- multitenant_admin on 8013, from the LAN: reports standalone mode and realm `tavms`; created an
  operator, who was made to change the password in TreeAlarm VMS, saw no cameras (none granted)
  and was refused by the console as not an administrator; deleted again. The administrator cannot
  delete their own account.
- Live WebRTC with a Keycloak token: first frame in 2.6 s. Recording resumed a minute after the
  simulator came back.
- The machine went off at 15:37 that day. Not a crash: winlogon powered it off on behalf of SYSTEM
  (event 1074), and TaVmsHost stopped everything within 9 s.

## 2026-10-21: v0.1.4, upgraded from v0.1.3

The fixes found below, on the same machine. Upgrade `/VERYSILENT`: **152 s** (0.1.3: 376 s).
setup.exe is 279 MB (was 523), the installation 5 760 files (was about 29 000).

| Phase | 0.1.3 | 0.1.4 |
|---|---|---|
| loader, extracting `Stop-TaVms.ps1` | 46 s | 5 s |
| stopping the services | 13 s | 12 s |
| deleting the old directories | 12 s | 9 s |
| unpacking | 146 s | 64 s |
| VC++ redistributable | 90 s | 0 s (already installed, skipped) |
| PostgreSQL, RabbitMQ | about 40 s | 28 s, RabbitMQ 26 of them |
| firewall, service, web interface up | about 30 s | 29 s |

Since the upgrade:
- **A new camera has live video at once.** It was added, and its first WHEP offer was answered in
  1.3 s with the first frame at 4.3 s. vms re-pointed the push 2 s after the camera was configured.
  One failed attempt before that is still logged: it uses the environment's default target, which
  the Windows install leaves empty.
- web-vms's sidecar no longer logs `error getting topics from app`.

What is left of the six minutes is RabbitMQ's own boot and the 24 processes starting one after
another; neither depends on the installer.

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
