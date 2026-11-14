# PulseDesk - publish server + agent into .\dist
# Run:  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#
# The server exe is locked while PulseDesk is running, so server output always goes to
# dist\server_new first. If the server is stopped it is copied in automatically; otherwise
# run scripts\deploy-server-elevated.ps1 (elevated) to swap it in and restart the watchdog.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$appSettings = Join-Path $root 'dist\server\appsettings.json'
$backupFile = Join-Path $root 'dist\appsettings.backup.json'
$staging = Join-Path $root 'dist\server_new'
$target = Join-Path $root 'dist\server'

# Live admin config that must survive a republish (publish overwrites appsettings.json)
$keepKeys = @('AdminPassword', 'PublicBaseUrl', 'SmtpHost', 'SmtpPort', 'SmtpUser', 'SmtpPass', 'SmtpFrom')

Write-Host "PulseDesk publish" -ForegroundColor Cyan

if (Test-Path $appSettings) {
    Copy-Item $appSettings $backupFile -Force
    Write-Host "`n[0/4] Backed up live appsettings.json -> dist\appsettings.backup.json" -ForegroundColor DarkGray
}

Write-Host "`n[1/4] Server -> dist\server_new ..." -ForegroundColor Yellow
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& $dotnet publish "src\PulseDesk.Server\PulseDesk.Server.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $staging
# never ship the template config - the live one holds the real password / public URL
Remove-Item (Join-Path $staging 'appsettings.json') -Force -ErrorAction SilentlyContinue

Write-Host "`n[2/4] Agent (single file)..." -ForegroundColor Yellow
& $dotnet publish "src\PulseDesk.Agent\PulseDesk.Agent.csproj" -c Release -r win-x64 --self-contained true `
    -o "dist\agent"

Write-Host "`n[3/4] Copy agent bundle into server..." -ForegroundColor Yellow
$agentDir = Join-Path $target 'agent'
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Copy-Item 'dist\agent\PulseDesk.Agent.exe' (Join-Path $agentDir 'PulseDesk.Agent.exe') -Force

# Android APK (optional - only when scripts\build-mobile.ps1 has produced one)
$apk = Join-Path $root 'dist\server\agent\PulseDeskMobile.apk'
if (Test-Path $apk) {
    $stagingAgent = Join-Path $staging 'agent'
    New-Item -ItemType Directory -Force -Path $stagingAgent | Out-Null
    Copy-Item $apk (Join-Path $stagingAgent 'PulseDeskMobile.apk') -Force
    Write-Host "APK bundled: $([math]::Round((Get-Item $apk).Length/1MB,2)) MB" -ForegroundColor DarkGray
}
else {
    Write-Host "APK not found - run scripts\build-mobile.ps1 to build the Android agent" -ForegroundColor DarkGray
}

Write-Host "`n[4/4] Server deploy..." -ForegroundColor Yellow
$running = $false
try { $running = [bool](Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction Stop) } catch { $running = $false }

if ($running) {
    Write-Host "Server chal raha hai (port 5080) - files staged in dist\server_new." -ForegroundColor Yellow
    Write-Host "Deploy ke liye elevated PowerShell mein chalayein:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File scripts\deploy-server-elevated.ps1" -ForegroundColor Cyan
}
else {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    robocopy $staging $target /E /XF appsettings.json /NFL /NDL /NJH /NJS /NC /NS | Out-Null
    Write-Host "Server files copied into dist\server." -ForegroundColor Green
}

# Restore live admin config over the freshly published appsettings.json (if a template exists)
if (Test-Path $backupFile) {
    $live = Get-Content $backupFile -Raw | ConvertFrom-Json
    $freshPath = Join-Path $target 'appsettings.json'
    if (Test-Path $freshPath) {
        $fresh = Get-Content $freshPath -Raw | ConvertFrom-Json
        $restored = @()
        foreach ($k in $keepKeys) {
            $val = $live.$k
            if ($null -ne $val -and "$val" -ne '') {
                $fresh | Add-Member -NotePropertyName $k -NotePropertyValue $val -Force
                $restored += $k
            }
        }
        [System.IO.File]::WriteAllText($freshPath, ($fresh | ConvertTo-Json -Depth 12),
            (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Restored live config: $($restored -join ', ')" -ForegroundColor DarkGray
    }
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Server  : dist\server\PulseDesk.Server.exe"
Write-Host "  Agent   : dist\agent\PulseDesk.Agent.exe"
Write-Host "  Bundle  : dist\server\agent\PulseDesk.Agent.exe"
