# Deploys/updates the PulseDesk stable-URL Cloudflare Worker.
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\deploy-worker.ps1 -Origin https://<quick-tunnel>
# Credentials are read from local files, never from the command line or git.
param(
    [Parameter(Mandatory = $true)][string]$Origin,
    [string]$ScriptName = 'pulsedesk',
    [string]$AccountFile = 'C:\Users\numan\AppData\Local\Temp\opencode\cf-account.txt',
    [string]$TokenFile  = 'C:\Users\numan\AppData\Local\Temp\opencode\cf-token.txt'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $root 'scripts\worker-proxy.template.js'
if (-not (Test-Path $template)) { throw "missing $template" }
if (-not (Test-Path $TokenFile)) { throw "missing $TokenFile" }

$accountId = if (Test-Path $AccountFile) { (Get-Content $AccountFile -Raw).Trim() } else { 'da580f23a5e5d26af01912af833edee1' }
$token = (Get-Content $TokenFile -Raw).Trim()
$h = @{ 'Authorization' = "Bearer $token"; 'Content-Type' = 'application/javascript' }

# Embed the deployed admin UI so page loads never wait on the hidden origin (cold edge fix).
$indexPath = Join-Path $root 'dist\server\wwwroot\index.html'
if (-not (Test-Path $indexPath)) { throw "missing $indexPath" }
$html = Get-Content $indexPath -Raw
# Pin the live WebSocket to the hidden origin that is actually delivering frames.
# The page itself stays on the permanent workers.dev URL. Observed: worker's WS bridge can
# stall for a device while the direct origin still streams fine (0 frames vs 3 frames in the
# same test). Pinning removes that extra hop for video without changing the public URL,
# invites, enrolled devices or the agent.
$wsBase = $Origin.TrimEnd('/') -replace '^https:', 'wss:' -replace '^http:', 'ws:'
# Auto-refresh any tab that still has an older embedded UI (fixes stale stream/control logic
# without asking the admin to remember to reload after every deploy).
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $ver = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($html))).Replace('-', '')).Substring(0, 8) } finally { $sha.Dispose() }
$inject = "<script>window.PDS_WS_BASE='$wsBase/';(function(){try{var v='$ver';if(localStorage.getItem('pdsUiVer')!==v){localStorage.setItem('pdsUiVer',v);location.reload();}}catch(e){}})();</script>"
$html = $html.Replace('<head>', "<head>$inject")
# Windows PowerShell 5.1's ConvertTo-Json explodes this 65 KB HTML into ~45 MB (every escape
# gets re-escaped), which Cloudflare rejects with 400 or serves with intermittent 500s.
# A plain JS string literal is exact and stays ~65 KB.
$jsHtml = $html.Replace('\', '\\').Replace('"', '\"').Replace("`r", '').Replace("`n", '\n').Replace('</script>', '<\/script>')
$embedded = '"' + $jsHtml + '"'

# Seed the worker's login fallback using the DIRECT origin (fast and reachable from this machine
# even when Cloudflare -> Funnel is wedged). Only a password hash and the short-lived token are
# injected into the DEPLOYED script; nothing is printed, committed or stored in a file.
$seedHash = ''
$seedToken = ''
$seedDevices = '""'   # JS string literal for an empty string
try {
    $cfgPath = Join-Path $root 'dist\server\appsettings.json'
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $pw = [string]$cfg.AdminPassword
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $seedHash = ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($pw))).Replace('-', '')).ToLowerInvariant() }
    finally { $sha.Dispose() }
    $seedLogin = Invoke-RestMethod -Method Post -Uri "$($Origin.TrimEnd('/'))/api/admin/login" -TimeoutSec 15 `
                    -ContentType 'application/json' -Body (@{ password = $pw } | ConvertTo-Json)
    if ($seedLogin.token) { $seedToken = [string]$seedLogin.token }
    # Snapshot the device list with that token so the worker can serve it if the tunnel wedges.
    # ConvertTo-Json turns the raw JSON into a safe JS string literal (quotes/escapes handled).
    $devRaw = (Invoke-WebRequest -Uri "$($Origin.TrimEnd('/'))/api/admin/devices" -TimeoutSec 15 `
                -Headers @{ 'X-Admin-Token' = $seedToken } -UseBasicParsing).Content
    if ($devRaw) { $seedDevices = $devRaw | ConvertTo-Json -Compress }
} catch { Write-Warning "login/device seed skipped (worker will learn it on first real login): $($_.Exception.Message)" }

$js = Get-Content $template -Raw
$js = $js.Replace('__ORIGIN__', $Origin.TrimEnd('/'))
$js = $js.Replace('__EMBEDDED_INDEX__', $embedded)
$js = $js.Replace('__SEED_HASH__', $seedHash)
$js = $js.Replace('__SEED_TOKEN__', $seedToken)
$js = $js.Replace('__SEED_DEVICES__', $seedDevices)
$api = "https://api.cloudflare.com/client/v4/accounts/$accountId/workers/scripts/$ScriptName"

$r = Invoke-RestMethod -Method Put -Uri $api -Headers $h -Body $js -TimeoutSec 180
if (-not $r.success) { throw "worker deploy failed: $($r.messages | ForEach-Object { $_.message } | Out-String)" }

# Make sure the script is reachable on the account's free workers.dev subdomain.
try {
    $hd = @{ 'Authorization' = "Bearer $token"; 'Content-Type' = 'application/json' }
    $d = Invoke-RestMethod -Method Post -Uri "$api/subdomain" -Headers $hd -TimeoutSec 30 `
             -Body (@{ enabled = $true } | ConvertTo-Json)
    if (-not $d.success) { Write-Warning "workers.dev enable skipped: $($d.messages | ForEach-Object { $_.message } | Out-String)" }
} catch { Write-Warning "workers.dev enable skipped: $($_.Exception.Message)" }

Write-Output "worker=$ScriptName origin=$Origin deployed"
