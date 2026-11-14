param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [ValidateSet('tailscale','cloudflared','none')]
    [string]$Mode = 'tailscale'
)

$ErrorActionPreference = 'Continue'
$serverExe = Join-Path $Root 'dist\server\PulseDesk.Server.exe'
$serverDir = Split-Path $serverExe
$appSettings = Join-Path $serverDir 'appsettings.json'
$logDir = Join-Path $Root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'start-all.log'
$tunnelErr = Join-Path $logDir 'tunnel.err.log'
$funnelPath = '/pulsedesk'

function Log($m) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m"
    Add-Content -Path $log -Value $line -Encoding utf8
    Write-Host $line
}

function Get-Listener5080 {
    try { return (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction Stop) } catch { return $null }
}

function Stop-Server {
    $l = Get-Listener5080
    if (-not $l) { return }
    foreach ($procId in ($l | Select-Object -ExpandProperty OwningProcess -Unique)) {
        try { Stop-Process -Id $procId -Force -ErrorAction Stop; Log "stopped old server pid=$procId" } catch { }
    }
    Start-Sleep -Seconds 1
}

# ---------------------------------------------------------------- config
function Set-PublicBaseUrl([string]$url) {
    if (-not $url) { return $false }
    if (-not (Test-Path $appSettings)) { Log "appsettings.json missing: $appSettings"; return $false }
    try {
        $cfg = Get-Content $appSettings -Raw | ConvertFrom-Json
        if ($cfg.PublicBaseUrl -eq $url) { return $false }
        $cfg.PublicBaseUrl = $url
        $json = $cfg | ConvertTo-Json -Depth 12
        [System.IO.File]::WriteAllText((Resolve-Path $appSettings), $json, (New-Object System.Text.UTF8Encoding($false)))
        Log "PublicBaseUrl updated -> $url"
        return $true
    }
    catch { Log "could not update appsettings: $($_.Exception.Message)"; return $false }
}

# ---------------------------------------------------------------- server
function Start-Server {
    if (-not (Test-Path $serverExe)) { Log "server exe missing: $serverExe"; return $false }
    if (Get-Listener5080) { return $true }

    Log "starting server..."
    Start-Process -FilePath $serverExe -WorkingDirectory $serverDir -WindowStyle Hidden `
        -RedirectStandardOutput "$logDir\server.out.log" -RedirectStandardError "$logDir\server.err.log" | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 700
        if (Get-Listener5080) { Log "server listening on 5080"; return $true }
    }
    Log "server did not open port 5080"
    return $false
}

# ---------------------------------------------------------------- tailscale funnel
function Start-TailscaleFunnel {
    $tsExe = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
    if (-not $tsExe) {
        foreach ($p in @("$env:ProgramFiles\Tailscale\tailscale.exe", "$env:LOCALAPPDATA\Tailscale\tailscale.exe")) {
            if (Test-Path $p) { $tsExe = $p; break }
        }
    }
    if (-not $tsExe) { Log "tailscale not found - run scripts\install-tunnel.ps1 -Mode tailscale"; return $false }

    try {
        $json = & $tsExe status --json 2>$null | Out-String
        $st = $json | ConvertFrom-Json
        $dns = $st.Self.DNSName
        if (-not $dns) { Log "tailscale not connected (no DNS name)"; return $false }
        $base = "https://$($dns.TrimEnd('.'))$funnelPath"
    }
    catch { Log "could not read tailscale status: $($_.Exception.Message)"; return $false }

    $status = & $tsExe funnel status 2>&1 | Out-String
    if ($status -notmatch '5080') {
        Log "enabling tailscale funnel $funnelPath -> 5080"
        & $tsExe funnel --bg --set-path $funnelPath http://localhost:5080 2>&1 | ForEach-Object { Log $_ }
    }
    else {
        Log "tailscale funnel already mounted on $funnelPath"
    }

    Set-PublicBaseUrl $base | Out-Null
    Log "public URL: $base"
    return $true
}

# ---------------------------------------------------------------- cloudflared
function Get-TunnelUrl {
    if (-not (Test-Path $tunnelErr)) { return $null }
    $txt = Get-Content $tunnelErr -ErrorAction SilentlyContinue -Tail 400 | Out-String
    $m = [regex]::Match($txt, 'https://[a-z0-9]+(?:-[a-z0-9]+)+\.trycloudflare\.com')
    if ($m.Success) { return $m.Value }
    return $null
}

function Start-Cloudflared {
    $cf = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
    if (-not $cf) {
        foreach ($p in @("$env:ProgramFiles\cloudflared\cloudflared.exe", "$env:LOCALAPPDATA\cloudflared\cloudflared.exe")) {
            if (Test-Path $p) { $cf = $p; break }
        }
    }
    if (-not $cf) { Log "cloudflared not installed - run scripts\install-tunnel.ps1 -Mode cloudflared"; return $false }
    if (Get-Process cloudflared -ErrorAction SilentlyContinue) { Log "cloudflared already running"; return $true }

    Log "starting cloudflared quick tunnel..."
    if (Test-Path $tunnelErr) { Remove-Item $tunnelErr -Force -ErrorAction SilentlyContinue }
    Start-Process -FilePath $cf -ArgumentList 'tunnel','--url','http://localhost:5080','--no-autoupdate' `
        -WindowStyle Hidden -RedirectStandardOutput "$logDir\tunnel.out.log" -RedirectStandardError $tunnelErr | Out-Null

    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 800
        $u = Get-TunnelUrl
        if ($u) { Set-PublicBaseUrl $u | Out-Null; Log "public URL: $u"; return $true }
    }
    Log "cloudflared did not return a URL (network block?). Tailscale funnel is more reliable here."
    return $false
}

# ---------------------------------------------------------------- main
Log "=== start-all v.2 (mode=$Mode) ==="

$changed = $false
if ($Mode -eq 'tailscale') {
    $changed = Start-TailscaleFunnel
}
elseif ($Mode -eq 'cloudflared') {
    $changed = Start-Cloudflared
}

if ($changed -and (Get-Listener5080)) {
    Log "public URL changed - restarting server to pick it up"
    Stop-Server
}

Start-Server | Out-Null
Log "done"
