# Shows the current public URL + opens the admin console.
# Run: powershell -ExecutionPolicy Bypass -File scripts\show-url.ps1 [-Open]
param(
    [switch]$Open,
    [int]$Port = 5080
)

$root = Split-Path -Parent $PSScriptRoot
$appSettings = Join-Path $root 'dist\server\appsettings.json'

$public = 'http://localhost:5080'
if (Test-Path $appSettings) {
    try { $public = (Get-Content $appSettings -Raw | ConvertFrom-Json).PublicBaseUrl } catch { }
}

$local = "http://localhost:$Port"
$listening = $false
try { $listening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop) } catch { }

Write-Host ""
Write-Host "  PulseDesk v.2" -ForegroundColor Cyan
Write-Host "  ----------------------------------------" -ForegroundColor DarkCyan
Write-Host "  Admin console : " -NoNewline; Write-Host $local -ForegroundColor White
Write-Host "  Public URL    : " -NoNewline; Write-Host $public -ForegroundColor ($(if ($public -match 'localhost') { 'Yellow' } else { 'Green' }))
Write-Host "  Server        : " -NoNewline
if ($listening) { Write-Host "listening on $Port" -ForegroundColor Green }
else { Write-Host "NOT running (run scripts\start-all.ps1)" -ForegroundColor Red }
Write-Host "  Invite links  : " -NoNewline; Write-Host "$public/join/<code>" -ForegroundColor DarkGray
Write-Host ""

if ($public -match 'localhost') {
    Write-Host "  Note: PublicBaseUrl localhost hai - employee kisi doosre network se install nahi kar sakega." -ForegroundColor Yellow
    Write-Host "  Tunnel chalayein: scripts\start-all.ps1 -Mode cloudflared" -ForegroundColor Yellow
    Write-Host ""
}

if ($Open) { Start-Process $local }
