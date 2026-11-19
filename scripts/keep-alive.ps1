# PulseDesk keep-alive - stable PUBLIC URL pinned to the fast Tailscale Funnel upstream.
# Keeps alive: the admin servers (5081 legacy + 5080 Funnel listener), the Cloudflare Worker
# proxy (permanent public URL), and verifies the enrolled Windows agent is in the user session.
#
# IMPORTANT: the public URL is the free workers.dev URL and NEVER changes. Its hidden upstream
# is the EXISTING Tailscale Funnel route https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk
# -> 127.0.0.1:5080. Cloudflare quick tunnels were tried first but were unusable: every
# internet/Wi-Fi change gave a new hostname and the edge responses took 15-40 s, which broke
# live video and remote control. Tailscale Funnel keeps ONE hostname across network changes
# and answers in ~0.05 s, with no new invite needed by employees.
#
# This script never creates or kills a tunnel and never touches the existing Tailscale/domain
# configuration - it only makes sure the Worker stays pinned to the Funnel origin.
#
# Registered as a SYSTEM scheduled task by install-keepalive.ps1 (if elevation is available).
param([switch]$Once)   # -Once runs a single self-heal pass (used by the elevated finaliser)

$ErrorActionPreference = 'Continue'
$root        = Split-Path -Parent $PSScriptRoot
$logDir      = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log         = Join-Path $logDir 'keep-alive.log'
$serverDir   = Join-Path $root 'dist\server'
$cfgPath     = Join-Path $root 'dist\server\appsettings.json'
$legacyExe   = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'   # 5081 legacy build
$funnelExe   = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'   # 5080 Funnel listener (resumable download)
$originFile  = Join-Path $logDir 'CURRENT-ORIGIN.txt'
$deployHashFile = Join-Path $logDir 'worker-deploy.hash'
$workerDeploy = Join-Path $PSScriptRoot 'deploy-worker.ps1'
$workerTemplate = Join-Path $PSScriptRoot 'worker-proxy.template.js'
$adminHtml = Join-Path $root 'dist\server\wwwroot\index.html'
$stableUrl   = 'https://pulsedesk.pulsedesk-numan.workers.dev'          # permanent, never rewritten
$funnelOrigin = 'https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk'   # fast, stable upstream
$tsExe       = 'C:\Program Files\Tailscale\tailscale.exe'

function Log($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }
function Test-Port([int]$port) {
    try { return [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop) }
    catch { return $false }
}
function Get-OriginUrl {
    if (Test-Path $originFile) { return (Get-Content $originFile -Raw -EA SilentlyContinue).Trim() }
    return $null
}

# curl.exe works from the SYSTEM scheduled task where Invoke-WebRequest cannot reach some hosts.
function Test-UrlFast([string]$u, [double]$maxSec = 8.0) {
    if (-not $u) { return $false }
    try {
        $curlExe = (Get-Command curl.exe -EA Stop).Source
        $result = & $curlExe -sS -o NUL -w '%{http_code}|%{time_total}' --max-time 15 "$u/index.html" 2>$null
        if ($result -match '^(\d+)\|([\d.]+)$') {
            return ([string]$Matches[1] -eq '200' -and [double]$Matches[2] -le $maxSec)
        }
    } catch {}
    return $false
}

function Start-Server([int]$port, [string]$exe, [string]$bind, [string]$label) {
    if (-not (Test-Path $exe)) { Log "  $label exe missing: $exe"; return $false }
    $args = @('--urls', $bind, "--PublicBaseUrl=$stableUrl")
    Log "  starting ${label}: $exe (cwd=$serverDir, args=$($args -join ' '))"
    Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory $serverDir -WindowStyle Hidden
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 700
        if (Test-Port $port) { Log "  $label back on $port"; return $true }
    }
    Log "  $label FAILED TO START on $port"
    return $false
}

function Deploy-Worker([string]$u, [string]$scriptName = 'pulsedesk') {
    # Bound the deploy: a hung Cloudflare API call must not stall the self-heal loop.
    if (-not (Test-Path $workerDeploy)) { Log "  worker deploy script missing: $workerDeploy"; return $false }
    $do = Join-Path $logDir 'worker-deploy.out.log'
    $de = Join-Path $logDir 'worker-deploy.err.log'
    try {
        Set-Content $do ''; Set-Content $de ''
        $dp = Start-Process powershell `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$workerDeploy`"",'-Origin',"`"$u`"",'-ScriptName',"`"$scriptName`"") `
            -RedirectStandardOutput $do -RedirectStandardError $de -WindowStyle Hidden -PassThru
        if ($dp.WaitForExit(90000)) {
            $out = (Get-Content $do -Raw -EA SilentlyContinue) + "`n" + (Get-Content $de -Raw -EA SilentlyContinue)
            ($out -split "`r?`n") | Where-Object { $_ } | ForEach-Object { Log "    worker: $_" }
            # The deploy script prints "origin=... deployed" only after the PUT succeeded. The
            # workers.dev subdomain step is best-effort and may warn without affecting the deploy.
            return ($out -match 'origin=.*deployed')
        }
        try { $dp.Kill() } catch {}
        Log "  worker deploy timed out after 90s"
    } catch { Log "  worker deploy FAIL: $($_.Exception.Message)" }
    return $false
}

function Get-WorkerSourceHash {
    $files = @($workerDeploy, $workerTemplate, $adminHtml)
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $files) {
        if (Test-Path $f) {
            $item = Get-Item $f
            [void]$sb.AppendLine("$($item.Name)|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)")
        }
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString())))
    } finally { $sha.Dispose() }
}

function Ensure-FunnelOrigin {
    $cur = Get-OriginUrl
    $hash = Get-WorkerSourceHash
    $oldHash = if (Test-Path $deployHashFile) { (Get-Content $deployHashFile -Raw -EA SilentlyContinue).Trim() } else { '' }
    $needDeploy = ($cur -ne $funnelOrigin) -or ($hash -ne $oldHash)
    if (-not $needDeploy) {
        if (-not (Test-UrlFast $funnelOrigin)) {
            Log "funnel origin slow/down ($funnelOrigin) - Tailscale will reconnect; no tunnel churn"
        }
        return
    }
    if ($cur -ne $funnelOrigin) { Log "worker origin drifted ($cur) -> pinning to Funnel $funnelOrigin" }
    else { Log "worker source changed (UI/template/deploy script) -> redeploying" }
    # Keep BOTH free public hostnames on the same origin. New setups embed both, so a laptop
    # blocked on one hostname can enroll/stream through the other; old devices are untouched.
    if ((Deploy-Worker $funnelOrigin 'pulsedesk') -and (Deploy-Worker $funnelOrigin 'pdsbackup')) {
        [System.IO.File]::WriteAllText($originFile, $funnelOrigin)
        [System.IO.File]::WriteAllText($deployHashFile, $hash)
        Log "  worker updated; public URL unchanged"
    } else {
        Log "  worker update failed; keeping last origin $cur and retrying next cycle"
    }
}

function Ensure-FunnelRoute {
    # Verify existing Tailscale Funnel still has /pulsedesk -> 5080. Log only: never rewrite
    # the user's pre-existing domain/route configuration.
    if (-not (Test-Path $tsExe)) { return }
    try {
        $j = & $tsExe funnel status --json 2>$null | Out-String | ConvertFrom-Json
        $handlers = $j.Web.PSObject.Properties.Value.Handlers
        $route = $null
        foreach ($h in $handlers) {
            if ($h.PSObject.Properties.Name -contains '/pulsedesk') { $route = $h.'/pulsedesk'.Proxy }
        }
        if (-not $route) { Log "WARNING: Tailscale Funnel /pulsedesk route missing - leaving existing config untouched" }
    } catch { Log "could not read Tailscale Funnel status: $($_.Exception.Message)" }
}

# Only ONE keep-alive loop may touch the origin.
$created = $false
$single = New-Object System.Threading.Mutex($true, 'Global\PulseDeskKeepAliveV2', [ref]$created)
if (-not $created) { Log 'another keep-alive instance is already running; exiting'; exit }

Log "keep-alive started (stable workers.dev URL pinned to Tailscale Funnel)"

while ($true) {
    try {
        # 1) legacy admin server on 5081 (protected process stays untouched if alive).
        if (-not (Test-Port 5081)) {
            Log "legacy server down on 5081 -> starting dist\server_new build"
            Start-Server 5081 $legacyExe 'http://0.0.0.0:5081' 'legacy server' | Out-Null
        }

        # 2) Funnel listener on 5080. Both the agent and admin viewers must land here so the
        #    in-memory live-video broker can forward frames between them.
        if (-not (Test-Port 5080)) {
            Log "Funnel listener down on 5080 -> restarting"
            Start-Server 5080 $funnelExe 'http://127.0.0.1:5080' 'Funnel listener' | Out-Null
        }

        # 3) keep the Worker pinned to the fast Funnel origin (no quick-tunnel churn).
        Ensure-FunnelOrigin
        Ensure-FunnelRoute

        # 4) agent: the enrolled agent self-starts in the user's desktop session (HKCU Run +
        #    EmployeeHome watchdog). Stop any session-0 copy that would show a black desktop.
        $agents = @(Get-CimInstance Win32_Process -Filter "Name='PulseDesk.Agent.exe'" -EA SilentlyContinue)
        foreach ($a in $agents) {
            if ($a.SessionId -eq 0) {
                try { Stop-Process -Id $a.ProcessId -Force; Log "stopped orphan session-0 agent pid=$($a.ProcessId)" }
                catch { Log "could not stop session-0 agent pid=$($a.ProcessId): $($_.Exception.Message)" }
            }
        }
    }
    catch { Log "keep-alive error: $($_.Exception.Message)" }
    if ($Once) { break }
    Start-Sleep -Seconds 20
}
