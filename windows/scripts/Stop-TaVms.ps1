<#
.SYNOPSIS
  Stops every TreeAlarm VMS process on this machine, so that its files can be replaced or removed.

.DESCRIPTION
  Run by the installer before it copies files (an upgrade) and by Uninstall-TaVms.ps1. Stops the
  services in dependency order, then ends anything still running from the installation directory:
  epmd, which the Erlang runtime leaves behind when RabbitMQ stops, and any child a crashed
  supervisor did not take down with it. Windows PowerShell 5.1.
#>
param(
  [Parameter(Mandatory)] [string] $InstallDir
)

$ErrorActionPreference = 'Stop'

foreach ($name in 'TaVmsHost', 'TaVms.RabbitMQ', 'TaVms.Postgres') {
  $service = Get-Service $name -ErrorAction SilentlyContinue
  if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "stopping $name"
    Stop-Service $name -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
  }
}

$prefix = (Resolve-Path $InstallDir -ErrorAction SilentlyContinue)
if ($prefix) {
  $prefix = $prefix.Path.TrimEnd('\') + '\'
  Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
      Write-Host "ending $($_.Name) ($($_.Id))"
      Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}
exit 0
