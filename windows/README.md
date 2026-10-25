# TreeAlarm VMS on Windows, without Docker

`ta_vms-setup-x64.exe` installs the same services the compose stack runs, as native Windows
processes, on one machine. Download it from the
[latest release](https://github.com/treealarm/ta_install/releases/latest/download/ta_vms-setup-x64.exe).
`.github/workflows/windows-installer.yml` builds and checks it on every change here, with the run's
build as an artifact, and publishes it to a release on a `vX.Y.Z` tag.

**Not in the Windows version:** `roitrc` (ROI re-compression of clips) and the analytics worker —
neither builds for Windows — and the Square integration. Live HEVC is re-encoded for WebRTC on the
CPU: there is no hardware encoder for Windows yet.

**Login** goes through a Keycloak that the installer ships with its Java runtime, on the loopback
(`127.0.0.1:5160`). The first sign-in is `admin` / `admin`; nothing makes anyone change a
password, and whoever wants to does it in the web UI. Users are added at `http://<this machine>:8013`
(`mt_admin`), by the web UI's administrators: new users, password resets, the administrator role. Keycloak's own administrator is `admin` with the password
generated into `site.env` (`KEYCLOAK_ADMIN_PASSWORD`); its console is at `http://127.0.0.1:5160` on
this machine.

## Install

Run the installer as an administrator. Options lets you choose the program folder and the folder
for recordings: pick a large disk for the second. Unattended:

```
ta_vms-setup-x64.exe /quiet /norestart /log setup.log StorageFolder=D:\TreeAlarm
```

`InstallFolder=<dir>` moves the program as well. Both names are case-sensitive, and both are fixed
by the first install: an upgrade keeps them. Then open `http://localhost:5134`.

`ta_vms-setup-x64.exe` is a WiX bundle. It installs the VC++, .NET and ASP.NET Core runtimes the
services need, when missing, and then `ta_vms-x64.msi`, which the release also carries on its own:
for software distribution (Group Policy, Intune) to machines that have the runtimes already,

```
msiexec /i ta_vms-x64.msi /qn STORAGEPATH=D:\TreeAlarm /l*v ta_vms.log
```

Running a newer installer over an existing installation upgrades it and keeps the data; so does the
first WiX one (0.1.6) over an Inno Setup installation of 0.1.5 or before, which it uninstalls first.
Uninstalling (Apps and Features, or `ta_vms-setup-x64.exe /uninstall /quiet`) removes the program
and the services and **keeps the data directory**: the database, the recordings and the passwords.
Delete `C:\ProgramData\TreeAlarm\VMS` by hand to remove those too.

## Several machines: a core and nodes

One installation is one machine (`standalone`). A deployment can also be spread out: a **core**
with the infrastructure and the system-wide services, which is a node too, and **nodes**, each with
its own cameras, recording and ONVIF. Either may be a Windows machine or a Linux one
(`../README.md`, "Several machines"): a package made on a core of one kind joins a node of the
other.

In the wizard, **Role** beside Options on the first page offers standalone (the default), core and
node: a core and a node pick this machine's address from a list of its IPv4 addresses (those of an
interface with a default gateway first), a node also the package its core made for it and whether
it runs a MediaMTX of its own (checked by default; unattended, `NodeMediaMtx=0` for none). The page is
not there on an upgrade, which keeps the role. Unattended, the same goes on the command line:

```
rem the core; Address is this machine's own, the one the nodes reach it at
ta_vms-setup-x64.exe /quiet /norestart /log setup.log Role=core Address=192.168.1.10
rem on the core, for a node named "hall": C:\ProgramData\TreeAlarm\VMS\distributed\node-hall.tar.gz
"C:\Program Files\TreeAlarm VMS\host\TaVmsHost.exe" package hall
powershell Restart-Service TaVmsHost
rem on the node, with the package copied to a local disk
ta_vms-setup-x64.exe /quiet /norestart /log setup.log Role=node Address=192.168.1.20 NodePackage=C:\Temp\node-hall.tar.gz
```

The MSI takes the same as `TAVMSROLE`, `TAVMSADDRESS`, `NODEPACKAGE` and `NODEMEDIAMTX`. A standalone installation
can become the core by installing over it with `Role=core`; its MediaMTX server in VmsCfg still
names `127.0.0.1` then, and its host has to be set to the core's address in the web interface. Any
other change of role takes an uninstall first. An upgrade keeps the role.

- **A node's services carry its name**: `vms-hall`, `vmsfs-hall`, `vmsonvif-hall`,
  `vmsonvif-actors-hall`, `vmslogger-hall`, `web-vms-hall`. Its cameras are added with server
  `vms-hall`; ONVIF reaches them from the node. A node runs no PostgreSQL, RabbitMQ or Keycloak: it
  uses the core's.
- **Every machine has the web interface**, and each shows the whole deployment; users log in at the
  core's Keycloak. User administration is on the core only.
- **A node's MediaMTX** keeps its cameras' live video on the node: add it once in the web
  interface (Administration → MediaMTX: host = the node's address, node = `vms-hall`; the accounts
  come filled in from the shared one), and the node's new cameras publish to it. A node installed
  without one publishes to the core's.
- **A node's cameras record to a storage on that node.** None is assigned to them by default: add
  one in the web interface with a path on the node's own disk
  (`C:\ProgramData\TreeAlarm\VMS\media_storage` on Windows, `/data/media_storage` on a Linux node)
  and assign it to the node's cameras.
- **App ids are found through HashiCorp Consul**, a server on the core and an agent on each node,
  gossip encrypted with the deployment's key; the agents' HTTP API stays on the loopback.
- **Sidecars talk over mutual TLS**, with certificates from Dapr Sentry on the core. Sentry signs
  one for an app id only against a token for it, signed with the core's key; a node's package holds
  tokens for its own app ids only.
- **The package is a credential**: the node's tokens, the core's database, broker, MediaMTX and
  Keycloak passwords, the live-video session key, and Consul's key. Move it as you would a password, and delete it once installed.
- **The address must stay put**: the sidecars register at it and Consul binds to it. Give the core
  and the nodes static addresses, and keep their clocks right (certificates allow 15 minutes).
- **Firewall**, made by the installer for the role: everywhere the web, and where MediaMTX runs its
  RTSP and WebRTC media; on the core PostgreSQL, RabbitMQ, Keycloak, placement (6050), Sentry
  (6055), Consul (8300, 8301 TCP/UDP) and the sidecars' internal ports (19011–19027); on a node
  Consul's 8301 and the sidecars' ports; and wherever MediaMTX runs, its API and WHEP (9997,
  8889). Those for the other machines are open to the local subnet only, so the
  machines of one deployment have to share one; PostgreSQL lets in that subnet too (`samenet`).
- **Size**: Consul (196 MB) and Sentry (55 MB) are in every installation; only a core or node runs
  them.
- **Not yet across machines**: analytics workers (there are none on Windows anyway) and the Square
  integration.

`TaVmsHost core <address>`, `package <node>` and `join <package> <address>` are what the installer
runs; they keep their files in `C:\ProgramData\TreeAlarm\VMS\distributed` (keys readable by
administrators only), laid out as `data/distributed` is on Linux.

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

`installer/Package` (WiX 5, `ta_vms-x64.msi`) installs that directory and runs
`scripts/Install-TaVms.ps1` after copying it; `installer/Bundle` (`ta_vms-setup-x64.exe`) runs the
runtime installers from `redist\` and then the MSI:

```
msbuild installer/BAFunctions/BAFunctions.vcxproj -restore -p:Configuration=Release -p:Platform=x64
dotnet build installer/Bundle/Bundle.wixproj -c Release -p:StageDir=<stage> -p:Version=<x.y.z>
```

`installer/BAFunctions` is the wizard's Role page code (C++, Visual Studio's MSBuild with the C++
workload: dotnet cannot build it), which the bundle carries.

The scripts do the actual work and run on their own as well (Windows PowerShell 5.1, elevated):

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
- **Nothing but the web interface listens on the LAN for the stack's own traffic.** Every service
  listens on the loopback, since its sidecar is its only client; every sidecar's API and metrics
  are on the loopback too, and its internal port on `DAPR_HOST_IP`, which is the loopback here.
  daprd listens on every address unless told otherwise, and its API calls any service and reads
  and writes the state store without asking who calls. What the LAN does reach is in the firewall
  rules: web_srv, mt_admin, RTSP, WebRTC media and ONVIF discovery — and RabbitMQ's Erlang
  distribution (25672) and epmd (4369), behind the cookie: the Windows service starts distribution
  before it reads its configuration, and `rabbitmqctl` finds the node by the machine's name.
- **App ids resolve through Dapr's `sqlite` resolver** on a standalone machine, not mDNS as in
  Docker (and through Consul on a core or node, above): every sidecar
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

HashiCorp Consul is under the Business Source License 1.1 (`licenses\Consul.txt`) since 1.17. It
forbids only offering a product that competes with Consul's own; a VMS that runs it to find its
services does not.
