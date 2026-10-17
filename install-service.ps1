#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Publishes CacheJanitor and installs it as an auto-start Windows service.
.EXAMPLE
  .\install-service.ps1                          # build + install to C:\Program Files\CacheJanitor
  .\install-service.ps1 -EnableLastAccess        # also turn on NTFS last-access updates (needs a reboot)
  .\install-service.ps1 -Uninstall
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\CacheJanitor",
    [switch]$EnableLastAccess,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$name = 'CacheJanitor'

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing existing service..."
    if ($existing.Status -ne 'Stopped') { Stop-Service $name -Force }
    sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
}
if ($Uninstall) { Write-Host "Uninstalled. Files left in $InstallDir."; return }

# Keep an existing config across upgrades.
$config = Join-Path $InstallDir 'appsettings.json'
$backup = $null
if (Test-Path $config) { $backup = Get-Content $config -Raw }

Write-Host "Publishing to $InstallDir..."
dotnet publish "$PSScriptRoot\CacheJanitor.csproj" -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if ($backup) { Set-Content -Path $config -Value $backup -NoNewline; Write-Host "Kept existing appsettings.json." }

New-Service -Name $name -BinaryPathName "`"$InstallDir\CacheJanitor.exe`"" `
    -DisplayName 'Cache Janitor (LRU cache size limiter)' `
    -Description 'Keeps configured cache folders under a size limit by deleting least recently used files.' `
    -StartupType Automatic | Out-Null
# Restart automatically if it ever crashes.
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

if ($EnableLastAccess) {
    fsutil behavior set disablelastaccess 0
    Write-Warning "Last-access updates enabled. Reboot for it to take effect."
}

Start-Service $name
Write-Host "Installed and started. Edit $config and run 'Restart-Service $name' to change settings."
Write-Host "Logs: Event Viewer > Windows Logs > Application, source '$name'."
