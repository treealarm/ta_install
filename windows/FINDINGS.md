# Findings on a real Windows machine

What the installer did on real hardware, as opposed to the CI runner. Each entry says what was
checked, what happened, why, and what was done about it. Newest run first.

## 2026-10-25, evening: a web on every machine, a MediaMTX on the node

The installer of ta_install ab43dd8 (CI run 40217110528, every job green; ta_vms 8c981d9), on the
machine below, as a node of a core on the Linux machine next to it, both from that commit. The point:
an operator opens any machine's web and sees the whole deployment, and a node's cameras' live
video comes from the node's own MediaMTX.

- `Role=node Address=192.168.1.82 NodePackage=...` (MediaMTX by default): **93 s**. site.env got
  `KEYCLOAK_URL=http://192.168.1.45:8180` (the core's), an empty `MT_ADMIN_PORT` and
  `TAVMS_MEDIAMTX=1`; the firewall the web, RTSP, WebRTC media and "MediaMTX API and WHEP". Six
  services healthy in the core's Consul, `web-vms-nhu` among them.
- **Everything below went through the node's web** (`192.168.1.82:5134`), logged in at the core's
  Keycloak: the node's MediaMTX added with the shared one's accounts, OnvifSim's map pair added on
  `vms-nhu` and its plan pair on the core's `vms`. VmsCfg gave the node's cameras the node's
  MediaMTX and the core's the shared one, with no choice made in the request.
- Both webs list the same four cameras. **Live video** (aiortc as the browser, 6–8 s each): the
  node's camera through either web came from `192.168.1.82:8189`, the node's MediaMTX; the core's
  camera from the core's. The first offer after a stream went idle got the documented 503 ("stream is
  still starting, retry"), which the player retries.
- **The archive across**: each web lists both cameras' clips, and plays a clip of the other
  machine's camera (an fMP4 segment through `/api/arch/hls`). **PTZ across**: each web turned the
  other machine's PTZ to the requested position. **Edge import** through the node's web filled a
  63.6 s hole from the camera's card.
- From the LAN the node now answers on its web (5134), MediaMTX (8554, 8889, 9997) and the web's
  sidecar (19011) besides Consul and the other sidecars. MediaMTX also listened on **8892**: MoQ
  (Media over QUIC, reading and publishing on 8892/8893), on by default in newer MediaMTX. Now off
  (`MTX_MOQ=no`) in services.json and the distributed compose; checked on both machines, live video
  unchanged. The standalone compose publishes only the ports it names, so 8892 never left it.

Not tried here: a node with `NodeMediaMtx=0` (TaVmsHost.Tests covers what it runs) and the
checkbox on the wizard's Role page (the install was unattended).

## 2026-10-25: the same machine as a node of a Linux core, Consul's key off the command line

The installer of ta_install 919163d (CI run 40204151038, whose `core` job passed; ta_vms 8f22563),
on the machine below, as a node of a core on the Linux machine next to it (`distributed.sh core
192.168.1.45`, `treealarm/*:latest` of ta_vms dcf646f). The point was the gossip key, which now
reaches Consul through `CONSUL_LOCAL_CONFIG` in compose and `distributed\consul.json` on Windows.

- The standalone installation came off in 43 s; `Role=node Address=192.168.1.82
  NodePackage=node-nhu.tar.gz` took **55 s**, and the node joined the core's Consul at once, its
  five services healthy within two minutes.
- **The key is on no command line**: `consul.exe agent ... -config-file=...\distributed\consul.json`,
  the file readable by SYSTEM, Administrators and LocalService only; on the core neither `ps` nor
  `docker top` shows it. Gossip encrypted on both, 8302 closed on both.
- OnvifSim's four cameras on `vms-nhu`, added through the core's web: ONVIF through
  `vmsonvif-nhu`, the masters' detections in the core. **Edge import through the node**: a 64.0 s
  hole filled from the camera's card, the first recovered clip 1.8 s before the hole, on a key frame.
- From the LAN the node answered on Consul's 8301 and the sidecars' 19013–19016 and 19018 only,
  as the first box did.
- The machine went off the network for an hour in the middle: Kernel-Power 42 with reason 4
  (application API) and target S4, and a boot of type 1 — a shutdown from the Start menu with Fast
  Startup, which hibernates the kernel and leaves the boot time as it was. Not sleep: idle sleep is
  off on this machine. Nothing of ours keeps Windows awake, but nothing was seen to need it.

The node was taken off again (64 s); the machine is left with nothing of ours installed.

## 2026-10-25: a second machine — a fresh install, a power cut, and the logs

A second box for when the first is busy: DESKTOP-NHU77Q0, Windows 10 Pro 22H2, Russian locale,
Intel Core i5-3450 (Ivy Bridge, HD Graphics 2500, no discrete GPU), 7.7 GB, on Wi-Fi, 17 GB free on
C: and the archive put on D:. Someone else uses it at the console.

- **A fresh install**, the CI build of ta_install 7876bda (ta_vms 8f22563),
  `ta_vms-setup-x64.exe /quiet /norestart StorageFolder=D:\TreeAlarm`: **197 s**, exit code 0, the
  three services running and the web interface answering. The first run of the installer on a
  machine that never had it; every earlier entry is an upgrade.
- **The power went** seven minutes later (Kernel-Power 41, no bugcheck, the power button not
  pressed; 260 of the 287 boots in that machine's log follow one). After the boot the whole stack came up
  on its own: PostgreSQL and RabbitMQ recovered, all 26 processes started, the web interface 200.
- **The logs, measured.** With no camera at all, 288 KB every five minutes (80 MB a day), nearly
  all of it in `host-logs\vmscfg.log`: ASP.NET's own console logger, which the services never
  removed, wrote two lines for every gRPC request and the text of every SQL command at Information
  — none of it in the services' own logs, where `Microsoft` is held at Warning. With the OnvifSim
  cameras added, `vmsutils` wrote four lines per MediaMTX API call (IHttpClientFactory). Ten PTZ
  status calls to a camera that was off wrote 316 KB: the SOAP envelope of each request at Debug,
  and OnvifLib's "connection refused" with its whole stack, six times per call. The Russian text of
  that error reached `host-logs` as mojibake (the console's code page, 866, read as UTF-8). Fixed
  in ta_vms (VmsLog: Serilog the only provider, OnvifLib under a category of its own at
  Information, HttpClient at Warning, a redirected console in UTF-8) and in OnvifLib (an unreachable
  camera without its stack), tried here by replacing the DLLs: no camera, nothing in five minutes;
  the ten calls 20 KB, the error text readable. What is left is in ta_vms
  `docs/future_plans/log-volume-leftovers.md`.
- **Edge import works on Windows**, the first time it could be tried: OnvifSim's Profile G
  (`tests/onvifsim/onvifsim_lab.py edge` in ta_vms, the sim on the Linux machine). sim-master off
  the network for 60 s and recording on its own card; once it was back, the 64.7 s hole was found,
  replayed from the camera and filed, the first recovered clip 6.9 s before the hole and 0.54 s
  from a key frame of the camera's recording. Clips under `D:\TreeAlarm\media_storage`.
- **Docker kept container output for ever** — no `logging` in either compose file, and json-file
  rotates nothing by default. Both now bound it at 10 MB × 3 per container.

## 2026-10-23: a core and a node on two machines, both ways

The installer of ta_install bf937ed (CI run 39997009100, ta_vms 49228fc), this box as one machine
of the pair and the Linux machine next to it (compose, `treealarm/*:latest`) as the other. The
Linux images carry daprd 1.19.0, the installer 1.18.7: the two talk without trouble.

**A: the core here, the node on Linux.** `Role=core Address=192.168.1.100` over the standalone
0.1.7 installation took 5.0 minutes and kept its data. Consul's server died twice before running:
`sync ...\consul\raft\wal: Access is denied`. Its default log store, wal, fsyncs the directory
whenever it opens or removes a segment, and Windows does not allow that on a directory handle; the
third start found its files made and ran, until the next segment. `services.json` now gives the
server `raft_logstore { backend = "boltdb" }`, and the CI's core job fails on any restart of
Consul, Sentry or placement. With that, nothing restarted and all eleven sidecars were healthy.

`TaVmsHost package asus` wrote a 1 948-byte package; the Linux machine joined with
`distributed.sh join` and came up with its five services healthy in the core's Consul (16 checks,
two members). Then, all through the core's web interface:

- the real camera on the Linux machine's camera network (192.168.2.64) answered ONVIF through
  `vmsonvif-asus`, 2.9 s for the profiles;
- the MediaMTX server still named 127.0.0.1, as `TaVmsHost core` warns; with its host set to
  192.168.1.100, `vms-asus` measured 2688×1520 and 640×360 at 25 fps and published both to the
  core's MediaMTX, and Chrome played the main stream through the core's WHEP at 25 fps, no loss;
- recording started once a storage on the node's own disk was assigned (none is by default, now in
  the README); its clips were in the core's index and played back through the core;
- the node's logs came through `vmslogger-asus`; its actors registered with the core's placement.

From the LAN the core answered on what its role needs (5134, 8013, 8554, PostgreSQL 5434, RabbitMQ
5672, placement 6050, Sentry 6055, Consul 8300 and 8301, the sidecars' 19011–19027), and also on
MediaMTX's 8889 and 9997 (now on the core's address, behind its credentials), the health and metrics
ports of Sentry and placement, Consul's 8302 (all in ta_vms `docs/future_plans/distributed-hardening.md`)
and 5432, this box's own PostgreSQL. Closed: Consul's 8500, every sidecar API and every app port.
This box's Ethernet is a Private network and its Private firewall profile is off, so here nothing is
closed by the firewall: only what listens on the loopback is out of reach.

**B: the core on Linux, the node here.** The core uninstalled in 2.4 minutes, its data directory
moved aside. `Role=node NodePackage=... Address=192.168.1.100` took 1.2 minutes: no database,
broker or web interface, the service up with `consul-agent` and five services healthy in the Linux
core's Consul. A camera of OnvifSim (on the Linux machine) on `vms-win`: ONVIF profiles through
`vmsonvif-win` (11 s, the first call), live video through the Linux core's WHEP, PTZ moved through
the core, the archive in the core's index and played back, the logs through `vmslogger-win`. From
the LAN the node answered on Consul's 8301 and the sidecars' 19013–19016 and 19018 only.

Two things for ta_vms, written up there under `docs/future_plans/`: recording here began 96 s after
the storage was assigned, against ten on the Linux node (`storage-assignment-delay.md`); and the
Linux core's own VmsFs never subscribed to its topics at all, which TaVmsHost's start order hides on
Windows (`streaming-subscription-recovery.md`). Both fixed on 2026-10-24: every subscriber now opens
its stream again whenever it ends (ta_vms 8519590), and VmsFs no longer caches a failed or
superseded storage fetch (4734417, with video_srv's side in 8f22563); on the dev stack a new
assignment then reached the recorder in 2 s.

Taking the node off took 11 minutes, with the console user's game holding the CPU at 99%. Left on
the box: nothing installed; the data directory of this test's core, which was the standalone
installation before, moved to `C:\ProgramData\TreeAlarm\VMS.core-bak`.

## 2026-10-23: an upgrade through the wizard that took 28 minutes

The wizard upgrade to 0b04dab (from de6e28f) started at 16:23, was left on its last page, and
was closed in the next session. Exit code 0, the stack up and the web interface answering; but
16:26 to 16:54 in the MSI, against 4–7 minutes for the upgrades above. By its log (every line is
the elevated part, `MSI (s)`):

| Phase | |
|---|---|
| CostFinalize, MigrateFeatureStates, InstallValidate: costing 13 301 components | 196 s + 247 s + 308 s |
| the old package's removal | 168 s |
| TaVmsSetUp (Install-TaVms.ps1, the web interface's wait included) | 320 s |
| committing, removing the rollback copies | 224 s |

Costing alone took twelve minutes, where it takes under one; nothing in the MSI's own work
explains that. The machine was in use meanwhile: the console session (another user) had Roblox
running, which by the evening had 1 100 s of CPU, and Defender had 1 900 s since the morning, on
four cores of an i5-750. Contention is the likely cause; it was not measured at the time, so it is
not proven. Nothing to change in the installer for it.

Found on the way, and not ours to fix: Defender on this machine excludes `C:\Windows`,
`C:\Windows\system32` and all of `C:\ProgramData`, and names among its exclusions files that are
typical of miners and droppers (`C:\ProgramData\WindowsTask\AMD.exe`, `MicrosoftHost.exe`,
`C:\ProgramData\ReaItekHD\taskhost.exe`, `C:\WINDOWS\windefender.exe`) and of KMS activators.
None of them was running and no scheduled task starts them, but the machine should not be taken as
clean, and what is installed on it (site.env, a core's keys) treated accordingly.

## 2026-10-23: the wizard, upgrading WiX 0.1.6 to WiX 0.1.6

Four upgrades through the wizard, the stack running each time: the first by hand, the other three
driven by UI Automation in the interactive session (started elevated, so no UAC prompt). Each
over the build before it.

| Build | Files In Use | Open | Install button to the end |
|---|---|---|---|
| c198af7 over bb256ed, by hand | twice, 96 applications, NVIDIA Overlay first | failed | 7 min 11 s |
| 1e8d946 over c198af7 | Keycloak's java, then 112 applications | worked (elevated) | 5 min 15 s without the dialog |
| 0c67096 over 1e8d946 | epmd.exe | worked, through Explorer | 4 min 26 s |
| de6e28f over 0c67096 | none | worked, through Explorer | 4 min 23 s |

- **Fixed: "Files In Use" on every upgrade.** InstallValidate asks who holds the files it will
  replace, and the stack was still up: the old version's `TaVmsStop` runs only later, inside the
  transaction. Restart Manager (`DisableShutdown`, 7091247) made the check faster, not quieter —
  the dialog named Keycloak's java (Retry / Ignore / Exit), and the old package's removal checked
  again (112 applications, 53 s; the first answer is remembered, so it did not ask twice). `/quiet`
  answers the dialog by itself, which is why the CI upgrade job never showed it. An upgrade now
  stops the services and ends whatever runs from the installation directory before InstallValidate
  (447cf2e, de6e28f — epmd, which RabbitMQ leaves running, was next in line): 8 s, then 18 s and 3 s
  for the two InstallValidates, no dialog.

### The first run, by hand

The CI build of ta_install c198af7 (ta_vms 8a8e7e1) over the installation of bb256ed, run by hand
through the wizard for the first time: exit code 0, no restart, the web interface up. 7 min 11 s
from the Install button to the end, of which 2 min 22 s were the old package's files-in-use
check and the user's answers to it.

| Phase | |
|---|---|
| caching the MSI | 6 s |
| the new MSI: costing, InstallValidate through Restart Manager | 57 s |
| the old MSI's InstallValidate, the old way, with two dialogs | 51 s + 91 s |
| stopping the old stack (`Stop-TaVms.ps1`) | 10 s |
| removing the old files, copying the new | 95 s |
| `Install-TaVms.ps1` (`site.env` exists, kept) | 87 s, RabbitMQ 40 s of it |

- **A files-in-use dialog naming NVIDIA Overlay.** The old package is removed inside the new
  one's transaction, and it brings its own properties: bb256ed still had
  `MSIRESTARTMANAGERCONTROL=Disable`. Its InstallValidate ran before its `TaVmsStop`, with the
  whole stack up, looked for holders the old way and reported 96 applications twice: ours, and
  everything with a DLL of the same name loaded — NVIDIA Overlay (`jre\bin\msvcp140.dll`),
  PhoneExperienceHost, OneDrive. The dialog shows only processes with a window, so it named
  NVIDIA Overlay and none of ours. The new package's own InstallValidate went through Restart
  Manager and asked nothing this time; the next upgrades showed that was luck (above).
- **Fixed: the Options page came back on an upgrade.** The bundle looked for
  `config\site.env` from its unelevated process; `config` is open to administrators only, so the
  search failed with "access denied" (0x80070005) and the bundle took the install for a first
  one. Setting the archive's place there would have been ignored silently, as `Install-TaVms.ps1`
  keeps the one in `site.env`. It looks for the `config` directory now, which an unelevated
  process can see: checked on the box with a limited token (a8e3157).
- **The Open button did nothing.** Six clicks, six `Failed to launch target ...
  TreeAlarm VMS.url` (0x80070483, no application associated). WixStdBA calls `ShellExecuteEx`
  with "open"; the same call with the same flags, the same path (doubled backslash and all) and
  system32-only DLL search opened the page in Chrome from an SSH session, from the interactive
  session and from an MTA thread, and the wizard's own button worked when the wizard was started
  elevated. Not reproduced, so not explained: the button now starts Explorer with the shortcut
  (0c67096), which opened the page from a limited process and from the wizard. Not yet clicked in
  a wizard started through the UAC prompt.
- **The stop with the new VmsFs: 4.5 s, nothing killed.** VmsFs 0.9 s after the request (4.4 s
  and 7.0 s with the old one, which crashed on every stop), the whole stack 4.3 s. The old
  stack, stopped by the upgrade itself, took 10 s.

## 2026-10-22: HEVC transcoding on NVENC (GeForce GTX 1050 Ti)

The v0.1.6 installation with only `vms` replaced by CI builds of ta_vms' `nvenc` branch
(roi_transcode's `Engine::Nvenc`: `h264_nvenc` through libavcodec on a CUDA device, the decoder on
the same device). The same source as the CPU measurement below: 1080p25 HEVC at 4 Mbit/s
(`testsrc2`, libx265) published from the LAN into the machine's MediaMTX, `transcode_hevc` on,
viewers in headless Chrome on another machine, CPU over 60 s.

| One stream, one viewer | vms CPU | of the machine | what the viewer got |
|---|---|---|---|
| libx264 (`video_transcode_processor`), same day | 121 % of a core | 44 % | 25 fps, 3.9 Mbit/s |
| NVENC, constant QP (first build) | 7 % of a core | 26 % | 10 Mbit/s, losses and freezes |
| NVENC, VBR at the target (fixed) | 7–12 % of a core | 32 % | 25 fps, 3.9 Mbit/s, no loss |

- vms logs `decoding on cuda, encoding on 'h264_nvenc (NVIDIA NVENC, no regions)'`: NVDEC to
  NVENC without leaving the card. VA-API is tried first and declines in the same millisecond.
- **Fixed: the bitrate.** The first build ran NVENC at constant QP 23 and left the target to
  ta_roienc's outer loop, as VA-API does. That loop's actuator is a QP offset on the background,
  carried as a region, and libavcodec's nvenc reads no regions: the loop moved nothing and the
  test pattern went out at 10 Mbit/s against a 3.9 Mbit/s target, 16 000 packets lost at the
  start. NVENC now holds the target with its own VBR (roi_transcode f3ae689).
- **Fixed: a warning on every stream start**, "bitstream SEI requested but not supported", and a
  fallback counted for it: the live transcode asked for SEI it has nothing to put in (ta_vms
  e702525; VA-API did the same on Linux).
- **Eight sessions.** The ninth and tenth streams fail to open (`OpenEncodeSessionEx failed:
  incompatible client key (21)`, the GeForce session limit) and go to libx264, as designed. With
  eight on NVENC and two on libx264 the machine sat at 100 %.

| Streams on NVENC, one viewer each | vms | MediaMTX | machine | encoder / decoder | VRAM |
|---|---|---|---|---|---|
| 1 | 7–12 % of a core | 16 % | 32 % | 5 % / 4 % | 0.6 GB |
| 4 | 22 % | 31 % | 21 % | 22 % / 19 % | 1.5 GB |
| 8 | 40 % | 64 % | 41 % | 35 % / 23 % | 2.7 GB |

- The encoder keeps up with eight (25 fps each by `nvidia-smi`). Four viewers in one browser got
  25 fps each, with losses only in the first ten seconds, when four sessions start at once.
  Eight in one browser did not get a clean picture: MediaMTX logged `reader is too slow,
  discarding frames` for every session, and the test machine, which also encoded the source with
  libx265, stalled it for ten seconds. Not separated: delivering eight 1080p WebRTC streams needs a
  test with the source and the viewers on different machines.

Found on the way:
- **Fixed: every ASP.NET service ran as Development** in both installs, inherited from the dev
  `.env` (ta_install 378388c): Swagger UI was open on web_vms, and a request that threw got the
  developer exception page with a stack trace. Run as Production on the box: every process up,
  the API and live video the same.
- **Fixed: the web interface's root answered 401** since Keycloak came with 0.1.5 — the
  installer's shortcut opens exactly `http://localhost:5134/`. Routing runs before
  `UseDefaultFiles`, so "/" reached the SPA fallback endpoint, which demanded a token; only
  `/index.html` worked. The fallback is anonymous now (ta_vms dd4cfcb); with that build the root
  answers 200 with the login form and the API still 401 without a token.
- **Fixed: VmsFs crashed on every stop**, 19 times in its log since 2026-10-20: four of its
  hosted services are registered twice, disposed twice, and the second dispose cancelled a
  disposed token source. Usually the process exited anyway; once it hung, got killed at 8 s, and
  the stop took 14 s instead of 8 (ta_vms 8a8e7e1).
- Open: with the OnvifSim cameras unreachable, `vmsonvif-actors` fills a 10 MB log every 15
  minutes — every SOAP request at debug level and each exception twice, through ASP.NET's console
  logger and through Serilog. The Russian text of Windows socket errors arrives in the logs as
  mojibake (the console's OEM code page read as UTF-8).

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
- The TreeAlarm admin was put back to `admin`/`admin` through Keycloak's admin API, first with
  `password-change-required` (it logged in and got 403 for the cameras until the password changed),
  then without it: the forced change is gone from the product (user, the same day), and admin
  sees all seven cameras straight away.
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
