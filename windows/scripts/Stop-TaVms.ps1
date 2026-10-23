<#
.SYNOPSIS
  Stops every TreeAlarm VMS process on this machine, so that its files can be replaced or removed.

.DESCRIPTION
  Run by the MSI when an upgrade removes the version before it, and by Uninstall-TaVms.ps1. Stops the
  services in dependency order, then ends anything still running from its component directories:
  epmd, which the Erlang runtime leaves behind when RabbitMQ stops, and any child a crashed
  supervisor did not take down with it. Windows PowerShell 5.1.
#>
param(
  [Parameter(Mandatory)] [string] $InstallDir
)

$ErrorActionPreference = 'Stop'
# The MSI passes directories as "<dir>\.", a bare trailing backslash would escape the closing quote.
$InstallDir = [IO.Path]::GetFullPath($InstallDir)

foreach ($name in 'TaVmsHost', 'TaVms.RabbitMQ', 'TaVms.Postgres') {
  $service = Get-Service $name -ErrorAction SilentlyContinue
  if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "stopping $name"
    Stop-Service $name -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
  }
}

# Only the component directories: nothing else in the installation directory runs.
$root = Resolve-Path $InstallDir -ErrorAction SilentlyContinue
if ($root) {
  $prefixes = 'host', 'vms', 'dotnet', 'dapr', 'consul', 'mediamtx', 'pgsql', 'erlang', 'rabbitmq', 'keycloak', 'jre', 'mt_admin' |
    ForEach-Object { (Join-Path $root.Path $_) + '\' }
  Get-Process | Where-Object {
    $path = $_.Path
    $path -and ($prefixes | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
  } | ForEach-Object {
    Write-Host "ending $($_.Name) ($($_.Id))"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
  }
}
exit 0
