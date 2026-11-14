# Set the PulseDesk admin password, save it, and restart the server so it takes effect.
# Run:  powershell -ExecutionPolicy Bypass -File scripts\set-password.ps1 [-Password "MyPass"]
param(
    [string]$Password
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$appSettings = Join-Path $root 'dist\server\appsettings.json'
if (-not (Test-Path $appSettings)) { throw "appsettings.json not found - run scripts\publish.ps1 first" }

if (-not $Password) {
    $bytes = New-Object byte[] 12
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $Password = ([Convert]::ToBase64String($bytes) -replace '[+/=]', '').Substring(0, 16)
}

$cfg = Get-Content $appSettings -Raw | ConvertFrom-Json
$cfg.AdminPassword = $Password
[System.IO.File]::WriteAllText($appSettings, ($cfg | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
Set-Content (Join-Path $root 'dist\ADMIN_PASSWORD.txt') $Password -Encoding ASCII

Write-Host "Password saved to dist\ADMIN_PASSWORD.txt"

# restart server (tunnel/funnel stays up)
$listener = Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue
foreach ($procId in ($listener | Select-Object -ExpandProperty OwningProcess -Unique)) {
    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\start-all.ps1') -Mode tailscale | Out-Null

$base = (Get-Content $appSettings -Raw | ConvertFrom-Json).PublicBaseUrl
Write-Host "Console : $base/index.html"
Write-Host "Password: $Password"
