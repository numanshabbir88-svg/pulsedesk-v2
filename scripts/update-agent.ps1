# Update / reinstall the PulseDesk agent on THIS machine from the local build.
# Run:  powershell -ExecutionPolicy Bypass -File scripts\update-agent.ps1
# The script elevates itself once (UAC = Yes), stops the old agent, copies the new
# binary over it and starts it again - the device keeps its enrollment.
#
# Why this is needed: a deployed agent keeps the OLD code in memory until it is restarted,
# and the installed file is not replaced by a server deploy. Without this step features
# added after the laptop was enrolled (remote control, input lock, DPI fix) do nothing.

param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\server\agent\PulseDesk.Agent.exe')
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Admin rights chahiye - UAC prompt (Yes dabaein)..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$Source`""
    )
    exit
}

if (-not (Test-Path $Source)) { throw "Naya agent nahi mila: $Source  (pehle scripts\publish.ps1 chalao)" }

$installDir = Join-Path $env:ProgramFiles 'PulseDesk'
$target = Join-Path $installDir 'PulseDesk.Agent.exe'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

Write-Host "Purana agent band..." -ForegroundColor Cyan
Get-Process PulseDesk.Agent -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force; Write-Host "  stopped pid=$($_.Id)" } catch { }
}
Start-Sleep -Seconds 2

Write-Host "Naya agent copy: $Source -> $target" -ForegroundColor Cyan
Copy-Item $Source $target -Force

Write-Host "Agent start..." -ForegroundColor Cyan
Start-Process $target
Start-Sleep -Seconds 4

$p = Get-Process PulseDesk.Agent -ErrorAction SilentlyContinue | Select-Object -First 1
$hash = (Get-FileHash $target).Hash
Write-Host ""
Write-Host "installed exe : $((Get-Item $target).LastWriteTime)"
Write-Host "hash          : $hash"
Write-Host "running       : $([bool]$p)  pid=$($p.Id)  started=$($p.StartTime)"
Write-Host ""
Get-Content "$env:ProgramData\PulseDesk\agent.log" -Tail 6 -ErrorAction SilentlyContinue
