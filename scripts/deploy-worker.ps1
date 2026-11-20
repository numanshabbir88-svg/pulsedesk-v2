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
$embedded = $html | ConvertTo-Json -Compress -Depth 5
$js = (Get-Content $template -Raw).Replace('__ORIGIN__', $Origin.TrimEnd('/')).Replace('__EMBEDDED_INDEX__', $embedded)
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
