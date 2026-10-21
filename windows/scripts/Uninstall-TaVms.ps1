<#
.SYNOPSIS
  Removes the TreeAlarm VMS services, the firewall rules and nothing else.

.DESCRIPTION
  Run by the uninstaller (the MSI) before it deletes the installation directory, and by the MSI's
  rollback of a first install that failed. The data directory --
  database, recordings, logs, site.env with its passwords -- is left in place: removing the program
  is not a decision to destroy the archive. Delete it by hand if that is what is wanted.
  Windows PowerShell 5.1.
#>
param(
  [string] $InstallDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string] $DataDir = (Join-Path $env:ProgramData 'TreeAlarm\VMS')
)

$ErrorActionPreference = 'Continue'

# The MSI passes directories as "<dir>\.", a bare trailing backslash would escape the closing quote.
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$DataDir = [IO.Path]::GetFullPath($DataDir)

$logDir = Join-Path $DataDir 'install-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir ('uninstall-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))) | Out-Null

& (Join-Path $PSScriptRoot 'Stop-TaVms.ps1') -InstallDir $InstallDir

if (Get-Service 'TaVmsHost' -ErrorAction SilentlyContinue) {
  sc.exe delete TaVmsHost | Out-Null
}

if (Get-Service 'TaVms.RabbitMQ' -ErrorAction SilentlyContinue) {
  # erlsrv itself rather than rabbitmq-service.bat, which first runs erl and so starts a fresh
  # epmd.exe -- one that would then hold files in the directory about to be deleted.
  $erlsrv = Get-ChildItem (Join-Path $InstallDir 'erlang') -Filter erlsrv.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($erlsrv) { & $erlsrv.FullName remove TaVms.RabbitMQ }
  if (Get-Service 'TaVms.RabbitMQ' -ErrorAction SilentlyContinue) { sc.exe delete TaVms.RabbitMQ | Out-Null }
}

if (Get-Service 'TaVms.Postgres' -ErrorAction SilentlyContinue) {
  & (Join-Path $InstallDir 'pgsql\bin\pg_ctl.exe') unregister -N TaVms.Postgres
  if (Get-Service 'TaVms.Postgres' -ErrorAction SilentlyContinue) { sc.exe delete TaVms.Postgres | Out-Null }
}

Get-NetFirewallRule -Group 'TreeAlarm VMS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Again, now that the services are gone: whatever removing them started (pg_ctl, erlsrv) must not
# keep a file of the installation directory open while the uninstaller deletes it.
& (Join-Path $PSScriptRoot 'Stop-TaVms.ps1') -InstallDir $InstallDir

# What the services wrote beside themselves, which the MSI does not know about and would otherwise
# leave behind, keeping the installation directory from being removed: Keycloak's temporary files
# and transaction log. Its realm file, in data\import, is the MSI's own.
Get-ChildItem (Join-Path $InstallDir 'keycloak\data') -Force -ErrorAction SilentlyContinue |
  Where-Object Name -ne 'import' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Services removed. Data kept in $DataDir"
Stop-Transcript | Out-Null
exit 0
