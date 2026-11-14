# One-shot elevated deploy: stop watchdog+server, copy staged server files, restart watchdog.
# Use this after scripts\publish.ps1 staged new server files into dist\server_new
# while the PulseDesk server was already running (the exe is locked then).
# Run elevated:  powershell -ExecutionPolicy Bypass -File scripts\deploy-server-elevated.ps1
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root 'dist\logs\deploy-server.log'
$staging = Join-Path $root 'dist\server_new'
$target = Join-Path $root 'dist\server'
$taskName = 'PulseDesk Watchdog'

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
function Log($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }

if (-not (Test-Path $staging)) { Log "nothing staged in $staging - run scripts\publish.ps1 first"; exit 1 }

Log '=== deploy start ==='

schtasks /end /tn $taskName 2>&1 | ForEach-Object { Log $_ }
Start-Sleep -Seconds 2

Get-Process PulseDesk.Server -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force; Log "stopped server pid=$($_.Id)" } catch { Log "stop failed: $($_.Exception.Message)" }
}
Start-Sleep -Seconds 2

# keep the live config (password / public URL / SMTP) and data folder untouched
robocopy $staging $target /E /XF appsettings.json /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:1 | ForEach-Object { Log "robocopy: $_" }
Log "robocopy exit=$LASTEXITCODE"

schtasks /run /tn $taskName 2>&1 | ForEach-Object { Log $_ }
Start-Sleep -Seconds 10

$listen = Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue
Log ("port 5080 listening=" + [bool]$listen)
Log '=== deploy done ==='
