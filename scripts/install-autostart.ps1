# Registers PulseDesk watchdog as a Scheduled Task that starts at boot
# and restarts if it dies. Runs as SYSTEM so no user logon / password is needed.
# Run once as Administrator:
#   powershell -ExecutionPolicy Bypass -File scripts\install-autostart.ps1 -Mode tailscale
param(
    [ValidateSet('tailscale','cloudflared','none')]
    [string]$Mode = 'tailscale'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$watchdog = Join-Path $PSScriptRoot 'watchdog.ps1'
$taskName = 'PulseDesk Watchdog'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Admin rights chahiye - UAC prompt aa raha hai..." -ForegroundColor Yellow
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode $Mode"
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

if (-not (Test-Path $watchdog)) { throw "watchdog.ps1 not found: $watchdog" }

Write-Host "Registering scheduled task: $taskName (mode=$Mode, run as SYSTEM)" -ForegroundColor Cyan

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watchdog`" -Mode $Mode" `
    -WorkingDirectory $root

$triggers = @(
    New-ScheduledTaskTrigger -AtStartup
    # safety net: re-launch every 10 minutes if the watchdog ever dies
    New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
        -RepetitionInterval (New-TimeSpan -Minutes 10) `
        -RepetitionDuration (New-TimeSpan -Days 3650)
)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers `
    -Settings $settings -Principal $principal `
    -Description 'PulseDesk admin server + tunnel watchdog (auto start at boot, auto restart).' | Out-Null

Write-Host "Starting task..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 6

$info = Get-ScheduledTask -TaskName $taskName
Write-Host ""
Write-Host "Task state: $($info.State)" -ForegroundColor Green
Write-Host "Ab laptop boot / Wi-Fi reconnect par PulseDesk khud start ho jayega." -ForegroundColor Green
Write-Host ""
Write-Host "Useful commands (admin PowerShell):" -ForegroundColor DarkGray
Write-Host "  Get-ScheduledTask -TaskName '$taskName'" -ForegroundColor DarkGray
Write-Host "  Stop-ScheduledTask -TaskName '$taskName'" -ForegroundColor DarkGray
Write-Host "  Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false" -ForegroundColor DarkGray
