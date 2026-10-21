# TreeAlarm VMS on Windows, without Docker

`ta_vms-setup-x64.exe` installs the same services the compose stack runs, as native Windows
processes, on one machine. Download it from the
[latest release](https://github.com/treealarm/ta_install/releases/latest/download/ta_vms-setup-x64.exe).
`.github/workflows/windows-installer.yml` builds and checks it on every change here, with the run's
build as an artifact, and publishes it to a release on a `vX.Y.Z` tag.

**Not in the Windows version:** `roitrc` (ROI re-compression of clips) and the analytics worker —
neither builds for Windows —, the Square integration and the Keycloak login. Live HEVC is
re-encoded for WebRTC on the CPU: there is no hardware encoder for Windows yet. The web UI has no
login, exactly like the compose stack with `KEYCLOAK_URL` empty: anyone who can reach port 5134
can use it.

## Install

Run the installer as an administrator. It asks for one thing, the folder for recordings: pick a
large disk. Unattended:

```
ta_vms-setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /STORAGE=D:\TreeAlarm
```

Then open `http://localhost:5134`.

Running the installer again over an existing installation upgrades it and keeps the data.
Uninstalling removes the program and the services and **keeps the data directory** — the database,
the recordings and the passwords. Delete `C:\ProgramData\TreeAlarm\VMS` by hand to remove those too.

## What runs

Three Windows services:

| Service | What |
|---|---|
| `TaVms.Postgres` | PostgreSQL, as NetworkService, on the loopback interface |
| `TaVms.RabbitMQ` | RabbitMQ (the Dapr pub/sub broker), on the loopback interface |
| `TaVmsHost` ("TreeAlarm VMS") | everything else, as LocalService |

`TaVmsHost` is a supervisor (`windows/TaVmsHost`). It waits until PostgreSQL and RabbitMQ answer,
creates the databases `POSTGRES_*_DB` names, writes the Dapr components, and then starts Dapr
placement, MediaMTX, and every service behind its own `daprd` — in that order — as child processes.
Whatever exits is started again (1 s, 2 s, 4 s … 30 s apart). Stopping the service stops them in
reverse order. From an elevated console:

```
"C:\Program Files\TreeAlarm VMS\host\TaVmsHost.exe" status
"C:\Program Files\TreeAlarm VMS\host\TaVmsHost.exe" restart vmscfg        (or vmscfg.dapr, mediamtx, ...)
"C:\Program Files\TreeAlarm VMS\host\TaVmsHost.exe" check-db
```

## Where things are

| Path | What |
|---|---|
| `C:\Program Files\TreeAlarm VMS\` | the program; replaced by every upgrade |
| `…\config\ta_vms.env`, `windows.env` | defaults: this repository's `.env`, and what differs on Windows |
| `C:\ProgramData\TreeAlarm\VMS\config\site.env` | this machine's settings and generated passwords; kept by upgrades |
| `C:\ProgramData\TreeAlarm\VMS\logs\` | the services' own logs (the Logs tab in the web UI reads these) |
| `C:\ProgramData\TreeAlarm\VMS\host-logs\` | the supervisor's log and every process's console output |
| `C:\ProgramData\TreeAlarm\VMS\install-logs\` | one log per install or upgrade |
| `C:\ProgramData\TreeAlarm\VMS\postgres\`, `rabbitmq\` | database and broker state |

Settings are layered: `ta_vms.env` < `windows.env` < `site.env`, the last one winning. Change a
value in `site.env` and restart the `TreeAlarm VMS` service. The passwords in `site.env` were set
up in PostgreSQL and RabbitMQ at install time; changing one there alone breaks the connection to
that server.

When something does not come up, `host-logs\TaVmsHost.log` says which process keeps exiting, and
`host-logs\<name>.log` holds that process's own output.

## Building it

Everything the installer ships is laid out by `scripts/Build-Stage.ps1`, from two inputs:

- the ta_vms Windows build — `vms.exe` and the published .NET services — which ta_vms's CI
  publishes on every push to master as `ta_vms-windows-x64.zip` in its `windows-build` prerelease;
- the third-party components pinned in `versions.json` (PostgreSQL, Erlang/OTP, RabbitMQ, Dapr,
  MediaMTX, the .NET runtimes), each checked against its hash.

`installer/ta_vms.iss` (Inno Setup 6) packs that directory and runs `scripts/Install-TaVms.ps1`
after copying it. The scripts do the actual work and run on their own as well (Windows PowerShell
5.1, elevated):

```
.\scripts\Install-TaVms.ps1 -InstallDir <dir> [-StoragePath <dir>]
.\scripts\Uninstall-TaVms.ps1 -InstallDir <dir>
```

The workflow installs the result on a fresh runner, waits for the web UI to answer through the
sidecars, and uninstalls it again. It runs on pushes that touch `windows/`, and by hand after a new
ta_vms build: `gh workflow run windows-installer.yml`.

## Things that differ from the compose stack

- **One localhost for every sidecar.** Each `daprd` has ports of its own (`config/services.json`),
  and each service learns its sidecar's from `DAPR_GRPC_PORT`/`DAPR_HTTP_PORT`.
- **App ids resolve through Dapr's `sqlite` resolver**, not mDNS as in Docker: every sidecar
  registers at `127.0.0.1` (`DAPR_HOST_IP`) in a file in the data directory. mDNS announced them at
  the machine's LAN address, so calls between services on one machine broke when that address
  changed or the cable was out, and two installations on one LAN would have mixed their app ids.
- **The Dapr components are generated** from the environment on every start, so the passwords exist
  in one place only and there is nothing for `db-init` to cross-check.
- **MediaMTX** uses the same `mediamtx.yml`; its Control API and WHEP listener are on the loopback
  only (web_srv proxies WHEP), and RTMP, HLS and SRT — which nothing uses — are off.
- **Versions are pinned** (`versions.json`) where compose follows `latest`: an installer carries
  the binaries.

## Licences

The installation directory holds `LICENSE.txt` (TreeAlarm VMS, MIT) and `THIRD-PARTY-NOTICES.txt`,
which names every bundled component with its licence and the file holding the full text: the
components' own where their archives carry one, `licenses\` for the rest, `licenses\vcpkg\` for the
libraries beside `vms.exe`. `scripts/Build-Stage.ps1` writes it from `versions.json` and fails if a
named file is missing.
