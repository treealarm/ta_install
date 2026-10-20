<#
.SYNOPSIS
  Installs or upgrades the TreeAlarm VMS services on this machine. Run elevated, by the installer or
  by hand; safe to run again.

.DESCRIPTION
  The files are already in place (the installer copied them to -InstallDir). This sets up what
  Docker would otherwise provide:

    1. the VC++, .NET and ASP.NET Core runtimes, if missing
    2. <DataDir>\config\site.env on first install: generated passwords, free ports, storage paths
    3. data directories and their ACLs (the services run as LocalService; passwords readable only
       by administrators and the services)
    4. PostgreSQL: initdb on first install, service TaVms.Postgres (NetworkService)
    5. RabbitMQ: service TaVms.RabbitMQ, its user, loopback-only listeners
    6. firewall rules for what the LAN must reach: the web UI, RTSP, WebRTC media, ONVIF discovery
    7. service TaVmsHost ("TreeAlarm VMS"), which runs everything else

  A log of each run goes to <DataDir>\install-logs. Written for Windows PowerShell 5.1, which is
  what a fresh Windows has.
#>
param(
  [string] $InstallDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string] $DataDir = (Join-Path $env:ProgramData 'TreeAlarm\VMS'),
  # First install only: where recordings and exports go. Ignored once site.env exists.
  [string] $StoragePath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Well-known SIDs rather than names, which are translated on non-English Windows.
$SidAdmins = '*S-1-5-32-544'
$SidSystem = '*S-1-5-18'
$SidLocalService = '*S-1-5-19'
$SidNetworkService = '*S-1-5-20'

$HostService = 'TaVmsHost'
$PostgresService = 'TaVms.Postgres'
$RabbitService = 'TaVms.RabbitMQ'
$FirewallGroup = 'TreeAlarm VMS'

function Invoke-Checked([string] $exe, [string[]] $arguments, [int[]] $ok = @(0)) {
  & $exe @arguments
  if ($ok -notcontains $LASTEXITCODE) { throw "$exe $($arguments -join ' ') exited with $LASTEXITCODE" }
}

# The runtime installers are GUI programs: PowerShell's & neither waits for them nor sets
# $LASTEXITCODE, so they are started and waited for explicitly.
function Invoke-Installer([string] $exe, [int[]] $ok = @(0, 3010)) {
  $process = Start-Process -FilePath $exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
  if ($ok -notcontains $process.ExitCode) { throw "$exe exited with $($process.ExitCode)" }
}

function Grant-Access([string] $path, [string[]] $grants, [switch] $Private) {
  $arguments = @($path)
  if ($Private) { $arguments += '/inheritance:r' }
  foreach ($g in $grants) { $arguments += @('/grant:r', $g) }
  Invoke-Checked icacls.exe ($arguments + '/Q')
}

function New-Secret([int] $length = 24) {
  # Letters and digits only: some of these are handed to .bat files (rabbitmqctl), and cmd.exe
  # reinterprets & | ^ % < > in an argument.
  $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'.ToCharArray()
  $bytes = New-Object byte[] $length
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# 32 random bytes, base64: what web_srv requires of VMS_WHEP_SESSION_KEY.
function New-SessionKey {
  $bytes = New-Object byte[] 32
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  [Convert]::ToBase64String($bytes)
}

# The first port from $start on that can actually be bound -- which also skips the ranges Windows
# reserves for Hyper-V and WinNAT, where netstat shows nothing and binding still fails.
function Get-FreePort([int] $start) {
  for ($port = $start; $port -lt $start + 50; $port++) {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $port)
    try { $listener.Start(); return $port } catch { } finally { $listener.Stop() }
  }
  throw "no free port in $start..$($start + 49)"
}

# Values come from TaVmsHost itself, so that one parser reads the env files.
function Get-StackValue([string] $key) {
  $value = & (Join-Path $InstallDir 'host\TaVmsHost.exe') env $key --data $DataDir
  if ($LASTEXITCODE -ne 0) { throw "TaVmsHost env $key failed" }
  return ($value | Select-Object -Last 1).Trim()
}

function Wait-Until([string] $what, [int] $seconds, [scriptblock] $test) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    if (& $test) { return }
    Start-Sleep -Seconds 2
  }
  throw "$what did not come up within $seconds s"
}

function Install-Runtimes {
  $redist = Join-Path $InstallDir 'redist'

  # Always run: it exits at once with 1638 when this or a newer version is already there.
  Write-Host '== VC++ redistributable'
  Invoke-Installer (Join-Path $redist 'vc_redist.x64.exe') @(0, 1638, 3010)

  # Two installers: the ASP.NET Core one does not carry the .NET runtime it runs on.
  foreach ($runtime in @(@('Microsoft.NETCore.App', 'dotnet-runtime-*.exe'), @('Microsoft.AspNetCore.App', 'aspnetcore-runtime-*.exe'))) {
    Write-Host "== $($runtime[0]) 10"
    $shared = Join-Path $env:ProgramFiles "dotnet\shared\$($runtime[0])"
    if (Get-ChildItem $shared -Directory -Filter '10.*' -ErrorAction SilentlyContinue) {
      Write-Host '   already installed'
      continue
    }
    $installer = Get-ChildItem $redist -Filter $runtime[1] | Select-Object -First 1
    if (-not $installer) { throw "no $($runtime[1]) in $redist" }
    Invoke-Installer $installer.FullName
  }
}

function New-SiteEnv([string] $path) {
  if (Test-Path $path) {
    Write-Host "== $path exists, kept"
    return
  }
  Write-Host "== writing $path"
  $lines = @(
    '# This installation''s own settings, written once by the installer and kept by upgrades.',
    '# Overrides ta_vms.env and windows.env in the installation directory. TaVmsHost reads it on',
    '# start: restart the "TreeAlarm VMS" service after a change.',
    '#',
    '# The passwords below were generated for this machine. PostgreSQL and RabbitMQ were set up',
    '# with them; changing one here alone breaks the connection to that server.',
    "POSTGRES_PORT=$(Get-FreePort 5432)",
    "POSTGRES_PASSWORD=$(New-Secret)",
    "RABBITMQ_PORT=$(Get-FreePort 5672)",
    "RABBITMQ_MANAGEMENT_PORT=$(Get-FreePort 15672)",
    "RABBITMQ_DEFAULT_PASS=$(New-Secret)",
    "VMS_MEDIAMTX_PUBLISH_PASS=$(New-Secret)",
    "VMS_MEDIAMTX_READ_PASS=$(New-Secret)",
    "VMS_WHEP_SESSION_KEY=$(New-SessionKey)"
  )
  if ($StoragePath) {
    $lines += "VMS_STORAGE_BASE_PATH=$(Join-Path $StoragePath 'media_storage')"
    $lines += "VMS_EXPORT_BASE_PATH=$(Join-Path $StoragePath 'exports')"
  }
  [IO.File]::WriteAllLines($path, [string[]] $lines)
}

function Initialize-DataDirectories {
  Write-Host '== data directories'
  foreach ($d in 'config', 'dapr', 'postgres', 'rabbitmq', 'logs', 'host-logs') {
    New-Item -ItemType Directory -Force (Join-Path $DataDir $d) | Out-Null
  }
  # The services write logs, components and their own state under the data directory.
  Grant-Access $DataDir @("${SidLocalService}:(OI)(CI)M")
  # site.env and the generated components hold passwords: administrators and the services only.
  Grant-Access (Join-Path $DataDir 'config') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F", "${SidLocalService}:(OI)(CI)RX") -Private
  Grant-Access (Join-Path $DataDir 'dapr') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F", "${SidLocalService}:(OI)(CI)M") -Private
  # PostgreSQL runs as NetworkService. The installing user is granted too: initdb drops the
  # administrator group from its own token, and would otherwise be locked out of the directory.
  $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  Grant-Access (Join-Path $DataDir 'postgres') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F", "${SidNetworkService}:(OI)(CI)F", "*${me}:(OI)(CI)F") -Private
  # RabbitMQ (erlsrv) runs as LocalSystem.
  Grant-Access (Join-Path $DataDir 'rabbitmq') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F") -Private

  foreach ($key in 'VMS_STORAGE_BASE_PATH', 'VMS_EXPORT_BASE_PATH') {
    $dir = Get-StackValue $key
    New-Item -ItemType Directory -Force $dir | Out-Null
    Grant-Access $dir @("${SidLocalService}:(OI)(CI)M")
  }
}

function Install-Postgres {
  Write-Host '== PostgreSQL'
  $bin = Join-Path $InstallDir 'pgsql\bin'
  $data = Join-Path $DataDir 'postgres'
  $port = Get-StackValue 'POSTGRES_PORT'

  if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) {
    $pwfile = [IO.Path]::GetTempFileName()
    try {
      [IO.File]::WriteAllText($pwfile, (Get-StackValue 'POSTGRES_PASSWORD'))
      Invoke-Checked (Join-Path $bin 'initdb.exe') @('-D', $data, '-U', (Get-StackValue 'POSTGRES_USER'),
        "--pwfile=$pwfile", '-E', 'UTF8', '--locale=C', '-A', 'scram-sha-256')
    } finally {
      Remove-Item $pwfile -Force
    }
    # Last one wins in postgresql.conf. Loopback only: every client is on this machine.
    Add-Content (Join-Path $data 'postgresql.conf') "`r`n# TreeAlarm VMS`r`nport = $port`r`nlisten_addresses = 'localhost'`r`n"
  }

  if (-not (Get-Service $PostgresService -ErrorAction SilentlyContinue)) {
    Invoke-Checked (Join-Path $bin 'pg_ctl.exe') @('register', '-N', $PostgresService,
      '-U', 'NT AUTHORITY\NetworkService', '-D', $data, '-S', 'auto', '-w')
    Invoke-Checked sc.exe @('description', $PostgresService, 'PostgreSQL for TreeAlarm VMS')
  }
  Start-Service $PostgresService
  Wait-Until 'PostgreSQL' 120 {
    & (Join-Path $bin 'pg_isready.exe') -h localhost -p $port -q
    $LASTEXITCODE -eq 0
  }
}

function Install-RabbitMq {
  Write-Host '== RabbitMQ'
  $sbin = Join-Path $InstallDir 'rabbitmq\sbin'
  $base = Join-Path $DataDir 'rabbitmq'
  # Read by the .bat scripts below. The service keeps what it was installed with, so there is no
  # need to set these machine-wide.
  $env:ERLANG_HOME = Join-Path $InstallDir 'erlang'
  $env:RABBITMQ_BASE = $base
  $env:RABBITMQ_SERVICENAME = $RabbitService

  $port = Get-StackValue 'RABBITMQ_PORT'
  $user = Get-StackValue 'RABBITMQ_DEFAULT_USER'
  $pass = Get-StackValue 'RABBITMQ_DEFAULT_PASS'
  [IO.File]::WriteAllLines((Join-Path $base 'rabbitmq.conf'), [string[]] @(
    '# Written by Install-TaVms.ps1 on every install. Loopback only: every client is on this machine.',
    "listeners.tcp.default = 127.0.0.1:$port",
    'management.tcp.ip = 127.0.0.1',
    "management.tcp.port = $(Get-StackValue 'RABBITMQ_MANAGEMENT_PORT')",
    'distribution.listener.interface = 127.0.0.1'
  ))
  [IO.File]::WriteAllText((Join-Path $base 'enabled_plugins'), '[rabbitmq_management].')

  # The service runs as LocalSystem and reads its cookie from that profile; the CLI is handed the
  # same value explicitly rather than through the installing user's own profile.
  $cookieFile = Join-Path $env:SystemRoot 'System32\config\systemprofile\.erlang.cookie'
  if (-not (Test-Path $cookieFile)) {
    [IO.File]::WriteAllText($cookieFile, (New-Secret 32).ToUpperInvariant())
  }
  $cookie = (Get-Content $cookieFile -Raw).Trim()

  # Re-registered on every run: the service records the path of the Erlang runtime, which an
  # upgrade to a newer Erlang moves.
  if (Get-Service $RabbitService -ErrorAction SilentlyContinue) {
    Stop-Service $RabbitService -ErrorAction SilentlyContinue
    Invoke-Checked (Join-Path $sbin 'rabbitmq-service.bat') @('remove')
  }
  Invoke-Checked (Join-Path $sbin 'rabbitmq-service.bat') @('install')
  Invoke-Checked sc.exe @('config', $RabbitService, 'start=', 'auto')
  Invoke-Checked sc.exe @('description', $RabbitService, 'RabbitMQ for TreeAlarm VMS')
  Start-Service $RabbitService

  $ctl = Join-Path $sbin 'rabbitmqctl.bat'
  Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'await_startup', '--timeout', '120')
  $users = & $ctl --erlang-cookie $cookie list_users --silent
  if ($LASTEXITCODE -ne 0) { throw 'rabbitmqctl list_users failed' }
  if ($users | Where-Object { ($_ -split '\s+')[0] -eq $user }) {
    Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'change_password', $user, $pass)
  } else {
    Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'add_user', $user, $pass)
  }
  Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'set_permissions', '-p', '/', $user, '.*', '.*', '.*')
  Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'set_user_tags', $user, 'administrator')
  if ($users | Where-Object { ($_ -split '\s+')[0] -eq 'guest' }) {
    Invoke-Checked $ctl @('--erlang-cookie', $cookie, 'delete_user', 'guest')
  }
}

function Set-FirewallRules {
  Write-Host '== firewall'
  Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  # Any profile: a camera network without a gateway is "unidentified", which Windows files as Public.
  $common = @{ Group = $FirewallGroup; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any' }
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - web' -Protocol TCP -LocalPort (Get-StackValue 'WEB_VMS_PORT') `
    -Program (Join-Path $InstallDir 'dotnet\web_srv.Server\web_srv.Server.exe') | Out-Null
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - RTSP' -Protocol TCP -LocalPort 8554 `
    -Program (Join-Path $InstallDir 'mediamtx\mediamtx.exe') | Out-Null
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - WebRTC media' -Protocol UDP -LocalPort 8189 `
    -Program (Join-Path $InstallDir 'mediamtx\mediamtx.exe') | Out-Null
  # ProbeMatch replies come back to whatever port the probe went out from.
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - ONVIF discovery' -Protocol UDP `
    -Program (Join-Path $InstallDir 'dotnet\VmsOnvif\VmsOnvif.exe') | Out-Null
}

function Install-HostService {
  Write-Host '== TreeAlarm VMS service'
  $exe = Join-Path $InstallDir 'host\TaVmsHost.exe'
  if (-not (Get-Service $HostService -ErrorAction SilentlyContinue)) {
    New-Service -Name $HostService -DisplayName 'TreeAlarm VMS' -StartupType Automatic `
      -BinaryPathName "`"$exe`" --data `"$DataDir`"" -DependsOn $PostgresService, $RabbitService `
      -Description 'Runs the TreeAlarm VMS services and their Dapr sidecars. Logs: host-logs in the data directory.' | Out-Null
    $svc = Get-CimInstance Win32_Service -Filter "Name='$HostService'"
    $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' }
    if ($result.ReturnValue -ne 0) { throw "cannot set the service account (Win32_Service.Change returned $($result.ReturnValue))" }
    # Restart on failure, including a clean exit with a non-zero code (failureflag).
    Invoke-Checked sc.exe @('failure', $HostService, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000')
    Invoke-Checked sc.exe @('failureflag', $HostService, '1')
  }
  Start-Service $HostService
}

$logDir = Join-Path $DataDir 'install-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir ('install-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))) | Out-Null
try {
  Write-Host "Installing TreeAlarm VMS: $InstallDir, data in $DataDir"
  Install-Runtimes
  New-Item -ItemType Directory -Force (Join-Path $DataDir 'config') | Out-Null
  New-SiteEnv (Join-Path $DataDir 'config\site.env')
  Initialize-DataDirectories
  Install-Postgres
  Install-RabbitMq
  Set-FirewallRules
  Install-HostService
  Write-Host ''
  Write-Host "Done. Web UI: http://localhost:$(Get-StackValue 'WEB_VMS_PORT')"
  Write-Host "Service logs: $(Join-Path $DataDir 'host-logs')"
  exit 0
} catch {
  Write-Host "INSTALL FAILED: $_"
  Write-Host $_.ScriptStackTrace
  exit 1
} finally {
  Stop-Transcript | Out-Null
}
