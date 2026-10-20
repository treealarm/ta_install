<#
.SYNOPSIS
  Removes the TreeAlarm VMS services, the firewall rules and nothing else.

.DESCRIPTION
  Run by the uninstaller before it deletes the installation directory. The data directory --
  database, recordings, logs, site.env with its passwords -- is left in place: removing the program
  is not a decision to destroy the archive. Delete it by hand if that is what is wanted.
  Windows PowerShell 5.1.
#>
param(
  [string] $InstallDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string] $DataDir = (Join-Path $env:ProgramData 'TreeAlarm\VMS')
)

$ErrorActionPreference = 'Continue'

& (Join-Path $PSScriptRoot 'Stop-TaVms.ps1') -InstallDir $InstallDir

if (Get-Service 'TaVmsHost' -ErrorAction SilentlyContinue) {
  sc.exe delete TaVmsHost | Out-Null
}

if (Get-Service 'TaVms.RabbitMQ' -ErrorAction SilentlyContinue) {
  $env:ERLANG_HOME = Join-Path $InstallDir 'erlang'
  $env:RABBITMQ_BASE = Join-Path $DataDir 'rabbitmq'
  $env:RABBITMQ_SERVICENAME = 'TaVms.RabbitMQ'
  & (Join-Path $InstallDir 'rabbitmq\sbin\rabbitmq-service.bat') remove
  if (Get-Service 'TaVms.RabbitMQ' -ErrorAction SilentlyContinue) { sc.exe delete TaVms.RabbitMQ | Out-Null }
}

if (Get-Service 'TaVms.Postgres' -ErrorAction SilentlyContinue) {
  & (Join-Path $InstallDir 'pgsql\bin\pg_ctl.exe') unregister -N TaVms.Postgres
  if (Get-Service 'TaVms.Postgres' -ErrorAction SilentlyContinue) { sc.exe delete TaVms.Postgres | Out-Null }
}

Get-NetFirewallRule -Group 'TreeAlarm VMS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

Write-Host "Services removed. Data kept in $DataDir"
exit 0
