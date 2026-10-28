#Requires -Version 7
<#
.SYNOPSIS
  Lays out everything the Windows installer ships, in the directory layout it installs.

.DESCRIPTION
  Inputs: the ta_vms Windows build (vms.exe and the published .NET services, from the
  windows-build release of treealarm/ta_vms) and the third-party components in versions.json,
  each downloaded once into -Cache and checked against its hash. Output, under -Stage:

    host\       TaVmsHost, the supervisor service
    vms\        vms.exe with its DLLs
    dotnet\     one folder per .NET service
    dapr\       daprd.exe, placement.exe
    mediamtx\   mediamtx.exe, and the same mediamtx.yml the Docker deployment mounts
    pgsql\      PostgreSQL binaries (pgAdmin, StackBuilder and the docs left out)
    erlang\     Erlang/OTP, portable
    rabbitmq\   RabbitMQ server
    keycloak\   Keycloak, built for PostgreSQL, with the realm to import in data\import
    jre\        the Java runtime Keycloak runs on
    mt_admin\   the user administration (multitenant_admin, standalone mode)
    redist\     installers of the .NET, ASP.NET Core and VC++ runtimes, and redist.wxi describing
                them; run by the bundle, not installed
    config\     ta_vms.env (= ../.env), windows.env, services.json
    setup\      the scripts the installer runs
    licenses\   licence texts the components' own archives do not carry: ta_vms's, those of the
                vcpkg libraries beside vms.exe (from the ta_vms build), Erlang/OTP's and Dapr's
    LICENSE.txt, THIRD-PARTY-NOTICES.txt

  installer\Package (the MSI) installs that directory as it is, redist\ aside; installer\Bundle
  (setup.exe) runs the runtime installers and then the MSI.
#>
param(
  [Parameter(Mandatory)] [string] $TaVmsZip,
  # multitenant_admin's Windows build (its windows-build release): the user administration.
  [Parameter(Mandatory)] [string] $MtAdminZip,
  [Parameter(Mandatory)] [string] $Stage,
  [Parameter(Mandatory)] [string] $Cache
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$windows = Join-Path $repo 'windows'

function Get-Component([string] $name) {
  $c = $versions.$name
  if (-not $c) { throw "versions.json has no '$name'" }
  $file = Join-Path $Cache ([IO.Path]::GetFileName(([Uri]$c.url).AbsolutePath))
  if (-not (Test-Path $file)) {
    Write-Host "downloading $($c.url)"
    $tmp = "$file.part"
    Invoke-WebRequest -Uri $c.url -OutFile $tmp
    Move-Item $tmp $file -Force
  }
  $algorithm, $expected = if ($c.sha256) { 'SHA256', $c.sha256 } elseif ($c.sha512) { 'SHA512', $c.sha512 } else { throw "versions.json: '$name' has no hash" }
  $actual = (Get-FileHash $file -Algorithm $algorithm).Hash
  if ($actual -ne $expected) {
    Remove-Item $file
    throw "$name`: $algorithm mismatch for $file (expected $expected, got $actual) -- the cached copy was removed"
  }
  return $file
}

function Expand-Into([string] $zip, [string] $dest, [string] $inner = $null) {
  $tmp = Join-Path $Cache ('x-' + [IO.Path]::GetRandomFileName())
  Expand-Archive $zip -DestinationPath $tmp
  $from = if ($inner) { Join-Path $tmp $inner } else { $tmp }
  New-Item -ItemType Directory -Force $dest | Out-Null
  Get-ChildItem $from -Force | Move-Item -Destination $dest
  Remove-Item $tmp -Recurse -Force
}

$versions = Get-Content (Join-Path $windows 'versions.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Cache | Out-Null
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null

# ta_vms itself.
Expand-Into $TaVmsZip (Join-Path $Stage 'ta_vms-tmp')
Move-Item (Join-Path $Stage 'ta_vms-tmp\vms') (Join-Path $Stage 'vms')
Move-Item (Join-Path $Stage 'ta_vms-tmp\dotnet') (Join-Path $Stage 'dotnet')
New-Item -ItemType Directory -Force (Join-Path $Stage 'redist') | Out-Null
Move-Item (Join-Path $Stage 'ta_vms-tmp\redist\vc_redist.x64.exe') (Join-Path $Stage 'redist\vc_redist.x64.exe')
if (-not (Test-Path (Join-Path $Stage 'ta_vms-tmp\licenses\vcpkg'))) { throw 'the ta_vms build carries no licenses\vcpkg: it predates them' }
Move-Item (Join-Path $Stage 'ta_vms-tmp\licenses') (Join-Path $Stage 'licenses')
Remove-Item (Join-Path $Stage 'ta_vms-tmp') -Recurse -Force
foreach ($app in (Get-Content (Join-Path $windows 'config\services.json') -Raw | ConvertFrom-Json).apps) {
  if (-not (Test-Path (Join-Path $Stage $app.exe))) { throw "services.json names $($app.exe), which the ta_vms build does not contain" }
}

# User administration.
Expand-Into $MtAdminZip (Join-Path $Stage 'mt_admin') 'mt_admin'
if (-not (Test-Path (Join-Path $Stage 'mt_admin\mt_admin.Server.exe'))) { throw 'the mt_admin build carries no mt_admin.Server.exe' }

# The supervisor.
dotnet publish (Join-Path $windows 'TaVmsHost\TaVmsHost.csproj') -c Release -r win-x64 --self-contained false `
  --nologo -o (Join-Path $Stage 'host')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish TaVmsHost failed' }

# Dapr and MediaMTX; Sentry and Consul for a deployment over several machines (services.json runs
# them on a core or a node only). The licence text in Consul's archive is the one in licenses\.
Expand-Into (Get-Component 'daprd') (Join-Path $Stage 'dapr')
Expand-Into (Get-Component 'placement') (Join-Path $Stage 'dapr')
Expand-Into (Get-Component 'sentry') (Join-Path $Stage 'dapr')
Expand-Into (Get-Component 'consul') (Join-Path $Stage 'consul')
Remove-Item (Join-Path $Stage 'consul\LICENSE.txt') -ErrorAction SilentlyContinue
Expand-Into (Get-Component 'mediamtx') (Join-Path $Stage 'mediamtx')
Copy-Item (Join-Path $repo 'init_files\config\mediamtx.yml') (Join-Path $Stage 'mediamtx\mediamtx.yml') -Force

# PostgreSQL, without pgAdmin (the archive's directory is "pgAdmin 4": 680 MB in 16 000 files, which
# by itself made unpacking take minutes) and the rest nobody on a server needs.
Expand-Into (Get-Component 'postgresql') (Join-Path $Stage 'pgsql') 'pgsql'
Get-ChildItem (Join-Path $Stage 'pgsql') -Directory |
  Where-Object { $_.Name -like 'pgAdmin*' -or $_.Name -in 'StackBuilder', 'doc', 'include', 'symbols' } |
  Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $Stage 'pgsql') -Filter '*_3rd_party_licenses.txt' | Where-Object Name -notlike 'commandlinetools*' | Remove-Item
Remove-Item (Join-Path $Stage 'pgsql\pgAdmin_license.txt') -ErrorAction SilentlyContinue

# Erlang, without its own VC++ redistributable (see versions.json), and RabbitMQ.
Expand-Into (Get-Component 'erlang') (Join-Path $Stage 'erlang')
Remove-Item (Join-Path $Stage 'erlang\doc'), (Join-Path $Stage 'erlang\vc_redist.exe') -Recurse -Force -ErrorAction SilentlyContinue
# Debug symbols, documentation, sources and examples: a third of the archive's size and two thirds of
# its files, none of it read at run time.
Get-ChildItem (Join-Path $Stage 'erlang') -Recurse -File -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem (Join-Path $Stage 'erlang') -Directory | Where-Object Name -like 'erts-*' |
  ForEach-Object { Join-Path $_.FullName 'doc'; Join-Path $_.FullName 'man'; Join-Path $_.FullName 'src' } |
  Where-Object { Test-Path $_ } | Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $Stage 'erlang\lib') -Directory | ForEach-Object {
  foreach ($extra in 'doc', 'src', 'examples') {
    $dir = Join-Path $_.FullName $extra
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  }
}
Expand-Into (Get-Component 'rabbitmq') (Join-Path $Stage 'rabbitmq') "rabbitmq_server-$($versions.rabbitmq.version)"

Copy-Item (Get-Component 'dotnet'), (Get-Component 'aspnetcore') (Join-Path $Stage 'redist')
# What the bundle (installer\Bundle) needs to know about these to tell whether a machine already has
# them: the VC++ build it carries, the runtime versions and installer names.
$redist = Join-Path $Stage 'redist'
$vcBuild = ([version] (Get-Item (Join-Path $redist 'vc_redist.x64.exe')).VersionInfo.FileVersion).Build
Set-Content -Path (Join-Path $redist 'redist.wxi') -Encoding utf8 -Value @(
  '<?xml version="1.0" encoding="utf-8"?>',
  '<!-- Written by scripts/Build-Stage.ps1. -->',
  '<Include>',
  "  <?define VcRedistBld = $vcBuild ?>",
  "  <?define DotnetVersion = $($versions.dotnet.version) ?>",
  "  <?define DotnetInstaller = $([IO.Path]::GetFileName((Get-Component 'dotnet'))) ?>",
  "  <?define AspNetCoreVersion = $($versions.aspnetcore.version) ?>",
  "  <?define AspNetCoreInstaller = $([IO.Path]::GetFileName((Get-Component 'aspnetcore'))) ?>",
  '</Include>'
)

# Keycloak and the Java runtime it runs on. Built for PostgreSQL here, once, so that the installed
# server starts --optimized instead of rebuilding itself on every start; the realm it imports on its
# first start is the one the Docker deployment mounts.
Expand-Into (Get-Component 'jre') (Join-Path $Stage 'jre') "jdk-$($versions.jre.version)-jre"
Expand-Into (Get-Component 'keycloak') (Join-Path $Stage 'keycloak') "keycloak-$($versions.keycloak.version)"
$import = New-Item -ItemType Directory -Force (Join-Path $Stage 'keycloak\data\import')
Copy-Item (Join-Path $repo 'init_files\keycloak\realm.json') $import
$env:JAVA_HOME = Join-Path $Stage 'jre'
& (Join-Path $Stage 'keycloak\bin\kc.bat') build --db=postgres
if ($LASTEXITCODE -ne 0) { throw 'kc build failed' }
Remove-Item env:JAVA_HOME
# What the build left behind besides itself; the import directory stays.
Remove-Item (Join-Path $Stage 'keycloak\data\tmp') -Recurse -Force -ErrorAction SilentlyContinue

# Configuration and the scripts the installer runs.
$config = New-Item -ItemType Directory -Force (Join-Path $Stage 'config')
Copy-Item (Join-Path $repo '.env') (Join-Path $config 'ta_vms.env')
Copy-Item (Join-Path $windows 'config\windows.env'), (Join-Path $windows 'config\services.json') $config
# What every role runs has to be in the stage, apps and the rest alike: a missing one would only
# show on a customer's machine of that role.
foreach ($infra in (Get-Content (Join-Path $windows 'config\services.json') -Raw | ConvertFrom-Json).infrastructure) {
  if (-not (Test-Path (Join-Path $Stage $infra.exe))) { throw "services.json names $($infra.exe) ($($infra.name)), which is not staged" }
}
$setup = New-Item -ItemType Directory -Force (Join-Path $Stage 'setup')
Copy-Item (Join-Path $windows 'scripts\Install-TaVms.ps1'), (Join-Path $windows 'scripts\Uninstall-TaVms.ps1'), `
  (Join-Path $windows 'scripts\Stop-TaVms.ps1') $setup

# Licences. Every component is listed with the licence versions.json gives it and the file holding
# its full text; a component whose file is missing fails the build rather than shipping without it.
Copy-Item (Join-Path $windows 'licenses\*') (Join-Path $Stage 'licenses')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $Stage 'LICENSE.txt')
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('TreeAlarm VMS for Windows is MIT-licensed (LICENSE.txt). It installs the third-party software')
$notices.Add('below, each under its own licence; paths are relative to the installation directory.')
$notices.Add('')
foreach ($component in $versions.PSObject.Properties) {
  $c = $component.Value
  if (-not $c.license) { throw "versions.json gives $($component.Name) no licence" }
  $where = if ($c.licenseFile) {
    if (-not (Test-Path (Join-Path $Stage $c.licenseFile))) { throw "$($component.Name): no $($c.licenseFile) in the stage" }
    $c.licenseFile
  } else { 'installed with the runtime, from redist\' }
  $notices.Add(('{0} {1}' -f $component.Name, $c.version))
  $notices.Add("  licence: $($c.license); text: $where")
  $notices.Add("  from:    $($c.url)")
}
$notices.Add('')
$notices.Add('vms\ (the media server) links libraries built by vcpkg, each listed with its licence in')
$notices.Add('licenses\vcpkg\. Among them is FFmpeg, built with the GPL encoders x264 and x265, which makes')
$notices.Add('that build GPL: its source is at https://ffmpeg.org, https://code.videolan.org/videolan/x264')
$notices.Add('and https://bitbucket.org/multicoreware/x265_git, and the versions built here are those of')
$notices.Add('the vcpkg baseline pinned in treealarm/ta_vms (.github/workflows/windows.yml, VCPKG_COMMIT).')
$notices.Add('')
$notices.Add('dotnet\ holds the .NET services; the NuGet packages published with them are under their own')
$notices.Add('licences, named on nuget.org for each package.')
Set-Content -Path (Join-Path $Stage 'THIRD-PARTY-NOTICES.txt') -Value $notices -Encoding ascii

# Per component, and with a ceiling for the two whose archives carry far more than a server needs:
# a renamed directory in a new upstream archive must fail the build, not ship another 700 MB.
$ceilingMb = @{ pgsql = 300; erlang = 250 }
foreach ($dir in Get-ChildItem $Stage -Directory) {
  $files = Get-ChildItem $dir.FullName -Recurse -File
  $mb = ($files | Measure-Object Length -Sum).Sum / 1MB
  Write-Host ("  {0,-10} {1,6:N0} MB {2,6} files" -f $dir.Name, $mb, $files.Count)
  if ($ceilingMb.ContainsKey($dir.Name) -and $mb -gt $ceilingMb[$dir.Name]) {
    throw "$($dir.Name) is $([int]$mb) MB, over its $($ceilingMb[$dir.Name]) MB ceiling: something the pruning above should remove is back"
  }
}
$size = (Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("staged {0:N0} MB in {1}" -f ($size / 1MB), $Stage)
