# Create a PulseDesk invite link and print it (works for Windows PC and Android).
# Run:  powershell -ExecutionPolicy Bypass -File scripts\new-invite.ps1 [-Email you@example.com]
param(
    [string]$Email = "",
    [string]$Server = "http://localhost:5080"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cfg = Get-Content (Join-Path $root 'dist\server\appsettings.json') -Raw | ConvertFrom-Json
$password = $cfg.AdminPassword
if (-not $password) { throw "AdminPassword not found in dist\server\appsettings.json" }

$login = Invoke-RestMethod -Uri "$Server/api/admin/login" -Method Post -ContentType 'application/json' `
    -Body (@{ password = $password } | ConvertTo-Json)
if (-not $login.ok) { throw "login failed" }

$invite = Invoke-RestMethod -Uri "$Server/api/admin/invite" -Method Post -ContentType 'application/json' `
    -Headers @{ 'X-Admin-Token' = $login.token } `
    -Body (@{ email = $Email; note = 'manual invite' } | ConvertTo-Json)

Write-Host ""
Write-Host "Invite code : $($invite.code)" -ForegroundColor Green
Write-Host "Link        : $($invite.link)" -ForegroundColor Green
Write-Host "Email sent  : $($invite.emailSent)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Phone (Android): link phone ke browser mein kholen -> 'Download Android App' -> app kholein -> code daalein." -ForegroundColor DarkGray
Write-Host "Windows PC     : wahi link computer par kholen -> setup exe download -> install." -ForegroundColor DarkGray
