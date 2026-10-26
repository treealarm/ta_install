<#
.SYNOPSIS
  Installs or upgrades the TreeAlarm VMS services on this machine. Run elevated, by the installer or
  by hand; safe to run again.

.DESCRIPTION
  The files are already in place (the installer copied them to -InstallDir), and so are the VC++,
  .NET and ASP.NET Core runtimes (setup.exe installs them before the MSI, from redist\). This sets
  up what Docker would otherwise provide:

    1. <DataDir>\config\site.env: generated passwords, free ports, storage paths; an upgrade adds
       only the settings a newer version introduced
    2. data directories and their ACLs (the services run as LocalService; passwords readable only
       by administrators and the services)
    3. a core or node of a deployment over several machines (-Role): TaVmsHost core, or
       TaVmsHost join, asking the core's web for the node's package or with a package file, once
    4. PostgreSQL: initdb on first install, service TaVms.Postgres (NetworkService); on the loopback,
       and on a core also at its address for the nodes (not on a node: it uses the core's)
    5. RabbitMQ: service TaVms.RabbitMQ, its user; listeners as PostgreSQL's (not on a node)
    6. firewall rules for what other machines must reach: the web UI, RTSP, WebRTC media, ONVIF
       discovery, and by role what the core's and nodes' services need from each other
    7. service TaVmsHost ("TreeAlarm VMS"), which runs everything else

  A log of each run goes to <DataDir>\install-logs. Written for Windows PowerShell 5.1, which is
  what a fresh Windows has.
#>
param(
  [string] $InstallDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string] $DataDir = (Join-Path $env:ProgramData 'TreeAlarm\VMS'),
  # First install only: where recordings and exports go. Ignored once site.env exists.
  [string] $StoragePath = '',
  # What this machine is (TaVmsHost's Role.cs): standalone, core or node. Empty: what the
  # installation already is, standalone on a first install. A standalone one may become the core;
  # nothing else changes role without an uninstall.
  [string] $Role = '',
  # A new core or node: this machine's address, the one the other machines reach it at. Asked for
  # rather than guessed: VPN, Hyper-V and remote-desktop adapters give a machine several.
  [string] $Address = '',
  # A new node: the core's address (and the port of its web, if not this installation's), where the
  # node asks for its package while the core's enrollment is open. The core's VmsCfg registers the
  # node and makes the package. Once it is closed, a package is needed instead (-NodePackage): an
  # administrator's password would not stay out of command lines and logs here.
  [string] $CoreAddress = '',
  # A new node: its name, which its services carry (vms-<name>, ...). Empty: this computer's name.
  [string] $NodeName = '',
  # A new node instead: its package, made in the core's web (Administration, Deployment) or fetched
  # by the wizard, on a local disk. Deleted once the node has joined: it holds the core's passwords.
  [string] $NodePackage = '',
  # A new node: 0 for no MediaMTX here, its cameras then publishing to the core's. Anything else,
  # empty included, runs one.
  [string] $NodeMediaMtx = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# The MSI passes directories as "<dir>\.", a bare trailing backslash would escape the closing quote.
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$DataDir = [IO.Path]::GetFullPath($DataDir)
if ($StoragePath) { $StoragePath = [IO.Path]::GetFullPath($StoragePath) }

# Well-known SIDs rather than names, which are translated on non-English Windows.
$SidAdmins = '*S-1-5-32-544'
$SidSystem = '*S-1-5-18'
$SidLocalService = '*S-1-5-19'
$SidNetworkService = '*S-1-5-20'

$HostService = 'TaVmsHost'
$PostgresService = 'TaVms.Postgres'
$RabbitService = 'TaVms.RabbitMQ'
$FirewallGroup = 'TreeAlarm VMS'

# A step marker with the time, so a transcript shows where an install spent its minutes.
function Write-Step([string] $what) { Write-Host ('== {0:HH:mm:ss} {1}' -f (Get-Date), $what) }

function Invoke-Checked([string] $exe, [string[]] $arguments, [int[]] $ok = @(0)) {
  & $exe @arguments
  if ($ok -notcontains $LASTEXITCODE) { throw "$exe $($arguments -join ' ') exited with $LASTEXITCODE" }
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

# Whether the file changed: a server reads such a file only when it starts.
function Set-TextFile([string] $path, [string[]] $lines) {
  $text = ($lines -join "`r`n") + "`r`n"
  if ((Test-Path $path) -and [IO.File]::ReadAllText($path) -eq $text) { return $false }
  [IO.File]::WriteAllText($path, $text)
  return $true
}

function Wait-Until([string] $what, [int] $seconds, [scriptblock] $test) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    if (& $test) { return }
    Start-Sleep -Seconds 2
  }
  throw "$what did not come up within $seconds s"
}

# The role this run installs. MSI properties are not kept between installations, so an upgrade
# passes none and the role comes from site.env itself, read here directly: TaVmsHost cannot read the
# environment before site.env exists.
function Resolve-Role {
  $site = Join-Path $DataDir 'config\site.env'
  $current = $null
  if (Test-Path $site) {
    $current = 'standalone'
    foreach ($line in Get-Content $site) {
      if ($line -match '^\s*TAVMS_ROLE\s*=\s*"?([A-Za-z]+)"?\s*$') { $current = $Matches[1] }
    }
  }
  if (-not $Role) { if ($current) { return $current } else { return 'standalone' } }
  if ($Role -notin 'standalone', 'core', 'node') { throw "-Role $Role`: expected standalone, core or node" }
  if (-not $current -or $current -eq $Role -or ($current -eq 'standalone' -and $Role -eq 'core')) { return $Role }
  throw "this installation is a $current and cannot become a $Role; uninstall it first (the data directory is kept)"
}

function Assert-OwnAddress {
  if (-not $Address) { throw "-Role $TheRole needs -Address: this machine's address on the network the other machines share" }
  if (-not (Get-NetIPAddress -IPAddress $Address -ErrorAction SilentlyContinue)) {
    throw "-Address $Address is not an address of this machine"
  }
}

# TaVmsHost core or join, once: both refuse to run twice, so a later run of this script (an upgrade)
# finds their files and leaves them alone.
function Initialize-Role {
  $dist = Join-Path $DataDir 'distributed'
  $hostExe = Join-Path $InstallDir 'host\TaVmsHost.exe'
  if ($TheRole -eq 'core' -and -not (Test-Path (Join-Path $dist 'core\jwt.key'))) {
    Write-Step "making this machine the core at $Address"
    Assert-OwnAddress
    # A standalone installation that becomes the core: its services restart with the new role.
    Stop-Service $HostService -ErrorAction SilentlyContinue
    Invoke-Checked $hostExe @('core', $Address, '--data', $DataDir)
  }
  # VmsCfg runs as LocalService and signs the tokens of the nodes that join, with the key a core made
  # before it did that kept from the services (TaVmsHost core opens it to them on a new core).
  $signingKey = Join-Path $dist 'core\jwt.key'
  if ($TheRole -eq 'core' -and (Test-Path $signingKey)) {
    & icacls.exe $signingKey /grant "${SidLocalService}:(R)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $signingKey failed: $LASTEXITCODE" }
  }
  if ($TheRole -eq 'node' -and -not (Test-Path (Join-Path $dist 'shared.env'))) {
    Assert-OwnAddress
    if ($NodePackage) {
      Write-Step "joining the core with $NodePackage"
      if (-not (Test-Path $NodePackage)) { throw "-NodePackage $NodePackage does not exist" }
      $join = @('join', [IO.Path]::GetFullPath($NodePackage), $Address)
    } elseif ($CoreAddress) {
      $name = if ($NodeName) { $NodeName } else { Get-DefaultNodeName }
      Write-Step "joining the core at $CoreAddress as node '$name'"
      $join = @('join', '--core', $CoreAddress, '--name', $name, $Address)
    } else {
      throw "-Role node needs -CoreAddress, the core to join, or -NodePackage, a package made there"
    }
    if ($NodeMediaMtx -eq '0') { $join += '--no-mediamtx' }
    Invoke-Checked $hostExe ($join + @('--data', $DataDir))
    if ($NodePackage) {
      Remove-Item -LiteralPath $NodePackage -Force -ErrorAction SilentlyContinue
      Write-Step "deleted $NodePackage: the node has joined, and it held the core's passwords"
    }
  }
}

# This computer's name as a node's: lower case, letters, digits and '-' (TaVmsHost's Role.cs).
function Get-DefaultNodeName {
  $name = ($env:COMPUTERNAME.ToLowerInvariant() -replace '[^a-z0-9-]', '-').Trim('-')
  if (-not $name -or $name -eq 'core') { throw "-NodeName is needed: '$env:COMPUTERNAME' cannot name a node" }
  $name
}

function New-SiteEnv([string] $path) {
  # Generated for this machine. A new installation gets all of them; an upgrade gets only the ones a
  # newer version introduced, and nothing already in the file is touched: PostgreSQL, RabbitMQ and
  # Keycloak were set up with what is there. A node gets none: it runs no PostgreSQL, RabbitMQ or
  # Keycloak, and its MediaMTX and web use the core's credentials, which come in its package.
  $generated = if ($TheRole -eq 'node') { [ordered]@{} } else { [ordered]@{
    POSTGRES_PORT             = { Get-FreePort 5432 }
    POSTGRES_PASSWORD         = { New-Secret }
    RABBITMQ_PORT             = { Get-FreePort 5672 }
    RABBITMQ_MANAGEMENT_PORT  = { Get-FreePort 15672 }
    RABBITMQ_DEFAULT_PASS     = { New-Secret }
    VMS_MEDIAMTX_PUBLISH_PASS = { New-Secret }
    VMS_MEDIAMTX_READ_PASS    = { New-Secret }
    VMS_WHEP_SESSION_KEY      = { New-SessionKey }
    # Keycloak's own administrator (master realm). Keycloak takes it on its first start only.
    KEYCLOAK_ADMIN_PASSWORD   = { New-Secret }
  } }

  if (Test-Path $path) {
    $present = @(Get-Content $path | Where-Object { $_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=' } | ForEach-Object { $Matches[1] })
    $missing = @($generated.Keys | Where-Object { $present -notcontains $_ })
    if ($missing.Count -eq 0) {
      Write-Step "$path exists, kept"
      return
    }
    Write-Step "$path exists, adding $($missing -join ', ')"
    # A file last saved by hand may not end in a newline, and the first line added would join it.
    $text = [IO.File]::ReadAllText($path)
    if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { [IO.File]::AppendAllText($path, [Environment]::NewLine) }
    [IO.File]::AppendAllLines($path, [string[]] ($missing | ForEach-Object { "$_=$(& $generated[$_])" }))
    return
  }

  Write-Step "writing $path"
  $lines = @(
    '# This installation''s own settings, written once by the installer and kept by upgrades.',
    '# Overrides ta_vms.env and windows.env in the installation directory. TaVmsHost reads it on',
    '# start: restart the "TreeAlarm VMS" service after a change.',
    '#',
    '# The passwords below were generated for this machine. PostgreSQL, RabbitMQ and Keycloak were',
    '# set up with them; changing one here alone breaks the connection to that server.'
  ) + @($generated.Keys | ForEach-Object { "$_=$(& $generated[$_])" })
  if ($StoragePath) {
    $lines += "VMS_STORAGE_BASE_PATH=$(Join-Path $StoragePath 'media_storage')"
    $lines += "VMS_EXPORT_BASE_PATH=$(Join-Path $StoragePath 'exports')"
  }
  [IO.File]::WriteAllLines($path, [string[]] $lines)
}

function Initialize-DataDirectories {
  Write-Step 'data directories'
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
  # Keycloak writes its temporary files and transaction log beside itself, in the installation
  # directory, which is otherwise read-only to the services.
  Grant-Access (Join-Path $InstallDir 'keycloak\data') @("${SidLocalService}:(OI)(CI)M")
  # RabbitMQ (erlsrv) runs as LocalSystem.
  Grant-Access (Join-Path $DataDir 'rabbitmq') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F") -Private
  # Consul's state, its gossip keyring among it. TaVmsHost closes <DataDir>\distributed itself.
  if ($TheRole -ne 'standalone') {
    New-Item -ItemType Directory -Force (Join-Path $DataDir 'consul') | Out-Null
    Grant-Access (Join-Path $DataDir 'consul') @("${SidAdmins}:(OI)(CI)F", "${SidSystem}:(OI)(CI)F", "${SidLocalService}:(OI)(CI)M") -Private
  }

  foreach ($key in 'VMS_STORAGE_BASE_PATH', 'VMS_EXPORT_BASE_PATH') {
    $dir = Get-StackValue $key
    New-Item -ItemType Directory -Force $dir | Out-Null
    Grant-Access $dir @("${SidLocalService}:(OI)(CI)M")
  }
}

function Install-Postgres {
  Write-Step 'PostgreSQL'
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
  }
  # Ours, in files of their own that every run rewrites; included last, so that they win over what
  # initdb wrote (and over the port and listen_addresses earlier versions appended). On one machine
  # the loopback only; a core also listens at its address, for the nodes' state stores and VmsFs.
  $changed = $false
  if (-not (Select-String -Path (Join-Path $data 'postgresql.conf') -SimpleMatch "include = 'tavms.conf'" -Quiet)) {
    Add-Content (Join-Path $data 'postgresql.conf') "`r`n# TreeAlarm VMS, rewritten by every install`r`ninclude = 'tavms.conf'`r`n"
    $changed = $true
  }
  if (-not (Select-String -Path (Join-Path $data 'pg_hba.conf') -SimpleMatch "include tavms_hba.conf" -Quiet)) {
    Add-Content (Join-Path $data 'pg_hba.conf') "`r`n# TreeAlarm VMS, rewritten by every install`r`ninclude tavms_hba.conf`r`n"
    $changed = $true
  }
  $listen = if ($TheRole -eq 'core') { "localhost, $(Get-StackValue 'DAPR_HOST_IP')" } else { 'localhost' }
  $changed = (Set-TextFile (Join-Path $data 'tavms.conf') @(
    '# Written by Install-TaVms.ps1 on every install.',
    "port = $port",
    "listen_addresses = '$listen'")) -or $changed
  $hba = @('# Written by Install-TaVms.ps1 on every install. A core lets the nodes on its subnet in.')
  if ($TheRole -eq 'core') { $hba += 'host all all samenet scram-sha-256' }
  $changed = (Set-TextFile (Join-Path $data 'tavms_hba.conf') $hba) -or $changed

  if (-not (Get-Service $PostgresService -ErrorAction SilentlyContinue)) {
    Invoke-Checked (Join-Path $bin 'pg_ctl.exe') @('register', '-N', $PostgresService,
      '-U', 'NT AUTHORITY\NetworkService', '-D', $data, '-S', 'auto', '-w')
    Invoke-Checked sc.exe @('description', $PostgresService, 'PostgreSQL for TreeAlarm VMS')
  }
  # listen_addresses takes a restart, not a reload.
  if ($changed -and (Get-Service $PostgresService).Status -eq 'Running') { Restart-Service $PostgresService }
  Start-Service $PostgresService
  Wait-Until 'PostgreSQL' 120 {
    & (Join-Path $bin 'pg_isready.exe') -h localhost -p $port -q
    $LASTEXITCODE -eq 0
  }
}

function Install-RabbitMq {
  Write-Step 'RabbitMQ'
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
  # Erlang distribution (25672) and epmd (4369) stay on every address, behind the cookie: the
  # Windows service starts distribution before it reads this file, so a listener setting here does
  # nothing, and with both forced onto the loopback rabbitmqctl, which looks the node up by the
  # machine's name and so at its LAN address, could not reach it any more (tried on the test box).
  # On one machine the loopback only; a core also listens at its address, for the nodes' pub/sub.
  $listeners = @("listeners.tcp.1 = 127.0.0.1:$port")
  if ($TheRole -eq 'core') { $listeners += "listeners.tcp.2 = $(Get-StackValue 'DAPR_HOST_IP'):$port" }
  [IO.File]::WriteAllLines((Join-Path $base 'rabbitmq.conf'), [string[]] (@(
    '# Written by Install-TaVms.ps1 on every install.') + $listeners + @(
    'management.tcp.ip = 127.0.0.1',
    "management.tcp.port = $(Get-StackValue 'RABBITMQ_MANAGEMENT_PORT')"
  )))
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
  Write-Step 'firewall'
  Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  # Any profile: a camera network without a gateway is "unidentified", which Windows files as Public.
  $common = @{ Group = $FirewallGroup; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any' }
  # What only the other machines of the deployment reach: from this subnet, not from anywhere.
  $deployment = $common + @{ RemoteAddress = 'LocalSubnet' }
  # Every machine has a web: an operator opens any of them.
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - web' -Protocol TCP -LocalPort (Get-StackValue 'WEB_VMS_PORT') `
    -Program (Join-Path $InstallDir 'dotnet\web_srv.Server\web_srv.Server.exe') | Out-Null
  if ($TheRole -ne 'node') {
    New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - users' -Protocol TCP -LocalPort (Get-StackValue 'MT_ADMIN_PORT') `
      -Program (Join-Path $InstallDir 'mt_admin\mt_admin.Server.exe') | Out-Null
  }
  $mediaMtx = (Get-StackValue 'TAVMS_MEDIAMTX') -eq '1'
  if ($mediaMtx) {
    # A core's MediaMTX also takes the cameras of nodes that have none, published over RTSP.
    New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - RTSP' -Protocol TCP -LocalPort 8554 `
      -Program (Join-Path $InstallDir 'mediamtx\mediamtx.exe') | Out-Null
    New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - WebRTC media' -Protocol UDP -LocalPort 8189 `
      -Program (Join-Path $InstallDir 'mediamtx\mediamtx.exe') | Out-Null
  }
  # ProbeMatch replies come back to whatever port the probe went out from.
  New-NetFirewallRule @common -DisplayName 'TreeAlarm VMS - ONVIF discovery' -Protocol UDP `
    -Program (Join-Path $InstallDir 'dotnet\VmsOnvif\VmsOnvif.exe') | Out-Null
  if ($TheRole -eq 'standalone') { return }

  # Every machine: the sidecars' internal gRPC ports (services.json), mutual TLS between them, and
  # Consul's gossip.
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - Dapr sidecars' -Protocol TCP -LocalPort '19011-19027' `
    -Program (Join-Path $InstallDir 'dapr\daprd.exe') | Out-Null
  foreach ($protocol in 'TCP', 'UDP') {
    New-NetFirewallRule @deployment -DisplayName "TreeAlarm VMS - Consul gossip ($protocol)" -Protocol $protocol -LocalPort 8301 `
      -Program (Join-Path $InstallDir 'consul\consul.exe') | Out-Null
  }
  if ($mediaMtx) {
    # VmsUtils on the core asks every MediaMTX who is watching; every machine's web relays WHEP.
    New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - MediaMTX API and WHEP' -Protocol TCP -LocalPort '8889', '9997' `
      -Program (Join-Path $InstallDir 'mediamtx\mediamtx.exe') | Out-Null
  }
  if ($TheRole -ne 'core') { return }

  # The core: what the nodes' services and sidecars connect to.
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - Consul server' -Protocol TCP -LocalPort 8300 `
    -Program (Join-Path $InstallDir 'consul\consul.exe') | Out-Null
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - Dapr Sentry' -Protocol TCP -LocalPort (Get-StackValue 'DAPR_SENTRY_PORT') `
    -Program (Join-Path $InstallDir 'dapr\sentry.exe') | Out-Null
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - Dapr placement' -Protocol TCP -LocalPort (Get-StackValue 'DAPR_PLACEMENT_PORT') `
    -Program (Join-Path $InstallDir 'dapr\placement.exe') | Out-Null
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - PostgreSQL' -Protocol TCP -LocalPort (Get-StackValue 'POSTGRES_PORT') `
    -Program (Join-Path $InstallDir 'pgsql\bin\postgres.exe') | Out-Null
  # By port: the listener is erl.exe, in a directory named after the Erlang version.
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - RabbitMQ' -Protocol TCP -LocalPort (Get-StackValue 'RABBITMQ_PORT') | Out-Null
  # Every node's web logs users in here. By port: the listener is the bundled JRE's java.exe.
  New-NetFirewallRule @deployment -DisplayName 'TreeAlarm VMS - Keycloak' -Protocol TCP -LocalPort (Get-StackValue 'KEYCLOAK_PORT') | Out-Null
}

function Install-HostService {
  Write-Step 'TreeAlarm VMS service'
  $exe = Join-Path $InstallDir 'host\TaVmsHost.exe'
  if (-not (Get-Service $HostService -ErrorAction SilentlyContinue)) {
    # A node runs neither PostgreSQL nor RabbitMQ: it uses the core's.
    $depends = if ($TheRole -eq 'node') { @{} } else { @{ DependsOn = $PostgresService, $RabbitService } }
    New-Service -Name $HostService -DisplayName 'TreeAlarm VMS' -StartupType Automatic `
      -BinaryPathName "`"$exe`" --data `"$DataDir`"" @depends `
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

# The Start menu and desktop shortcuts (installer\Package) point at this file rather than at a URL of their
# own, so the port is read here, from the same env files the service reads, and a port changed in
# site.env reaches the shortcuts with the next install.
function Get-WebUrl {
  # Every machine of a deployment has a web of its own, which shows the whole deployment.
  "http://localhost:$(Get-StackValue 'WEB_VMS_PORT')/"
}

function New-WebShortcut {
  $url = Get-WebUrl
  $lines = '[InternetShortcut]', "URL=$url", "IconFile=$(Join-Path $InstallDir 'ta_vms.ico')", 'IconIndex=0'
  Set-Content -Path (Join-Path $InstallDir 'TreeAlarm VMS.url') -Value $lines -Encoding ASCII
}

# So that the "Open TreeAlarm VMS" at the end of the wizard finds a page. Not reaching it is not a
# failed install: the service keeps starting, and host-logs say why it is slow.
function Wait-WebInterface([int] $seconds = 180) {
  $port = [int] (Get-StackValue 'WEB_VMS_PORT')
  Write-Step "waiting for the web interface on port $port"
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    $client = New-Object Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $port); Write-Host 'up'; return } catch { Start-Sleep -Seconds 2 } finally { $client.Dispose() }
  }
  Write-Host "WARNING: nothing listens on port $port after $seconds s; see $(Join-Path $DataDir 'host-logs')"
}

$logDir = Join-Path $DataDir 'install-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir ('install-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))) | Out-Null
try {
  Write-Host "Installing TreeAlarm VMS: $InstallDir, data in $DataDir"
  New-Item -ItemType Directory -Force (Join-Path $DataDir 'config') | Out-Null
  $TheRole = Resolve-Role
  Write-Step "role: $TheRole"
  New-SiteEnv (Join-Path $DataDir 'config\site.env')
  Initialize-DataDirectories
  Initialize-Role
  if ($TheRole -ne 'node') {
    Install-Postgres
    Install-RabbitMq
  }
  Set-FirewallRules
  Install-HostService
  New-WebShortcut
  if ($TheRole -ne 'node') { Wait-WebInterface }
  Write-Host ''
  Write-Host "Done. Web UI: $(Get-WebUrl)"
  Write-Host "Service logs: $(Join-Path $DataDir 'host-logs')"
  exit 0
} catch {
  Write-Host "INSTALL FAILED: $_"
  Write-Host $_.ScriptStackTrace
  exit 1
} finally {
  Stop-Transcript | Out-Null
}
