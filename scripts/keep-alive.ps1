# PulseDesk master keep-alive - stable PUBLIC URL + self-healing quick-tunnel origin.
# Keeps alive: admin server (5081), Cloudflare quick tunnel (private origin), Cloudflare Worker
# proxy (permanent public URL), enrolled Windows agent (started in the user session by the agent
# itself, never by this SYSTEM task).
#
# IMPORTANT: the public URL is the free workers.dev URL and NEVER changes. The quick tunnel is
# only a hidden upstream. When the home internet changes and cloudflared gets a new hostname,
# this script redeploys scripts\worker-proxy.template.js with the new ORIGIN. Links, invites,
# agent.json, the mobile app and admin shortcuts stay on the stable URL, so employees do not
# need a new invite and the admin never has to hunt for a new address.
#
# Registered as a SYSTEM scheduled task by install-keepalive.ps1.
param([switch]$Once)   # -Once runs a single self-heal pass (used by the elevated finaliser)

$ErrorActionPreference = 'Continue'
$root        = Split-Path -Parent $PSScriptRoot
$logDir      = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log         = Join-Path $logDir 'keep-alive.log'
$cf          = "C:\Users\numan\AppData\Local\Microsoft\WinGet\Packages\Cloudflare.cloudflared_Microsoft.Winget.Source_8wekyb3d8bbwe\cloudflared.exe"
$agentExe    = Join-Path $root 'dist\server\agent\PulseDesk.Agent.exe'
if (-not (Test-Path $agentExe)) { $agentExe = 'C:\Program Files\PulseDesk\PulseDesk.Agent.exe' }
$agentDir    = Split-Path -Parent $agentExe
# FIXED server only: the old dist\server\PulseDesk.Server.exe on 5080 is locked by a stale
# process and must never be started or killed. The working build is dist\server_new.
$serverExe   = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'
$serverDir   = Join-Path $root 'dist\server'
$cfgPath     = Join-Path $root 'dist\server\appsettings.json'
$inviteEmail = 'chaudhrynuman69@gmail.com'
$inviteFile  = Join-Path $logDir 'CURRENT-INVITE.txt'
$originFile  = Join-Path $logDir 'CURRENT-ORIGIN.txt'   # hidden quick-tunnel upstream
$legacyUrl   = Join-Path $logDir 'CURRENT-URL.txt'      # old file: used only to migrate
$workerDeploy = Join-Path $PSScriptRoot 'deploy-worker.ps1'
$stableUrl   = 'https://pulsedesk.pulsedesk-numan.workers.dev'   # permanent, never rewritten
# State for a candidate tunnel whose Worker redeploy has not succeeded yet.
$script:newTunnelPid  = 0
$script:pendingOrigin = $null
$script:pendingPid    = 0

function Log($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }
function Test-Port5081 { try { return [bool](Get-NetTCPConnection -LocalPort 5081 -State Listen -ErrorAction Stop) } catch { return $false } }

function Get-OriginUrl {
    if (Test-Path $originFile) { return (Get-Content $originFile -Raw -EA SilentlyContinue).Trim() }
    if (Test-Path $legacyUrl) { return (Get-Content $legacyUrl -Raw -EA SilentlyContinue).Trim() }
    return $null
}

function Test-UrlAlive([string]$u) {
    if (-not $u) { return $false }
    try {
        # Invoke-WebRequest from a SYSTEM/elevated PowerShell cannot reach trycloudflare.com on
        # this machine (proxy/DNS), so it reported healthy tunnels as dead and churned them.
        # curl.exe works in every context, including the SYSTEM scheduled task.
        $curlExe = (Get-Command curl.exe -EA Stop).Source
        # A quick tunnel can answer 200 but take 20-47s to do so. That is technically "up" but
        # unusable for live video/control, and used to leave the public URL crawling for ages.
        # Require a healthy latency too, so a slow tunnel is replaced instead of left running.
        $result = & $curlExe -sS -o NUL -w '%{http_code}|%{time_total}' --max-time 25 "$u/index.html" 2>$null
        if ($result -match '^(\d+)\|([\d.]+)$') {
            return ([string]$Matches[1] -eq '200' -and [double]$Matches[2] -le 12.0)
        }
        return $false
    } catch { return $false }
}

function Start-FixedServer {
    if (-not (Test-Path $serverExe)) { Log "fixed server exe missing: $serverExe"; return $false }
    if (Test-Port5081) { return $true }
    $args = @('--urls', 'http://0.0.0.0:5081', "--PublicBaseUrl=$stableUrl")
    Log "  starting fixed server: $serverExe (cwd=$serverDir, args=$($args -join ' '))"
    Start-Process -FilePath $serverExe -ArgumentList $args -WorkingDirectory $serverDir -WindowStyle Hidden `
        -RedirectStandardOutput "$logDir\server.out.log" -RedirectStandardError "$logDir\server.err.log"
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 700; if (Test-Port5081) { return $true } }
    return $false
}

function New-QuickTunnel {
    # Returns a NEW https URL only after it actually answers. Never kill the existing tunnel
    # here: the Worker must be redeployed FIRST, otherwise a failed deploy leaves the public
    # URL pointing at a dead upstream. Old tunnels are stopped only after a successful deploy.
    $script:newTunnelPid = 0
    if (-not (Test-Path $cf)) { Log "  cloudflared missing: $cf"; return $null }
    # Unique filename per attempt: a previous cloudflared from this same loop can still hold
    # tunnel-new-$PID.* open, and Set-Content used to throw "file in use", aborting the heal.
    $tag = "$PID-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $out = Join-Path $logDir "tunnel-new-$tag.out.log"
    $err = Join-Path $logDir "tunnel-new-$tag.err.log"
    Set-Content $out ''; Set-Content $err ''
    $p = Start-Process -FilePath $cf `
        -ArgumentList @('tunnel','--url','http://localhost:5081','--no-autoupdate') `
        -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        $txt = (Get-Content $out -Raw -EA SilentlyContinue) + "`n" + (Get-Content $err -Raw -EA SilentlyContinue)
        # Real quick-tunnel hostnames always contain a hyphen. The loose pattern used to match
        # "https://api.trycloudflare.com" from Cloudflare's status text and waste minutes.
        if ($txt -match 'https://[a-z0-9]+(?:-[a-z0-9]+)+\.trycloudflare\.com') {
            $candidate = $Matches[0]
            if (Test-UrlAlive $candidate) {
                $script:newTunnelPid = $p.Id
                return $candidate
            }
            Log "  candidate tunnel not answering yet: $candidate"
        }
        if ($p.HasExited) {
            Log "  cloudflared exited early code=$($p.ExitCode)"
            try { Stop-Process -Id $p.Id -Force -EA SilentlyContinue } catch {}
            return $null
        }
    }
    # Failed: leave the old tunnel alone and clean up only our new process.
    try { Stop-Process -Id $p.Id -Force -EA SilentlyContinue } catch {}
    return $null
}

function Apply-NewOrigin([string]$newUrl) {
    $old = Get-OriginUrl
    if (-not $newUrl) { return $false }
    if ($old -eq $newUrl) { return $true }
    Log "  worker redeploy requested: $old -> $newUrl"
    $deployed = $false
    if (Test-Path $workerDeploy) {
        try {
            $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $workerDeploy -Origin $newUrl 2>&1
            $out | ForEach-Object { Log "    worker: $_" }
            $deployed = ($LASTEXITCODE -eq 0 -and (($out | Out-String) -match 'deployed'))
        } catch { Log "    worker redeploy FAIL: $($_.Exception.Message)" }
    } else { Log "    worker deploy script missing: $workerDeploy" }
    if (-not $deployed) {
        # Never write CURRENT-ORIGIN before this succeeds: the file is what the next loop trusts.
        Log "  worker redeploy failed; keeping old origin $old so the public URL stays up"
        return $false
    }
    [System.IO.File]::WriteAllText($originFile, $newUrl)
    if (Test-Path $legacyUrl) { [System.IO.File]::WriteAllText($legacyUrl, $newUrl) }
    # Only now can the old upstream tunnels go away: the Worker already serves the new one.
    $keep = $script:newTunnelPid
    Get-Process cloudflared -EA SilentlyContinue |
        Where-Object { $keep -le 0 -or $_.Id -ne $keep } | Stop-Process -Force -EA SilentlyContinue
    Log "  quick origin changed: $old -> $newUrl (worker deployed, tunnel pid=$keep)"
    # Deliberately NO link/config/shortcut rewrites and NO server restart: the public URL is
    # stable, so nothing except the Worker's hidden ORIGIN changed.
    return $true
}

# Only ONE keep-alive loop may touch the tunnel. An orphaned elevated copy used to fight the
# SYSTEM task, killing each other's tunnels and locking the shared log files.
$created = $false
# V2 mutex: an older in-memory loop (session 0) can hold the legacy name while stuck on the
# old file-lock bug. A fresh loop must be able to take over self-healing without elevation.
$single = New-Object System.Threading.Mutex($true, 'Global\PulseDeskKeepAliveV2', [ref]$created)
if (-not $created) { Log 'another keep-alive instance is already running; exiting'; exit }

Log "keep-alive started (stable workers.dev URL, self-healing tunnel origin)"

while ($true) {
    try {
        # 1) admin server - restart ONLY the fixed 5081 build, always with the stable URL.
        if (-not (Test-Port5081)) {
            Log "fixed server down on 5081 -> starting dist\server_new build"
            if (Start-FixedServer) { Log "  fixed server back on 5081" } else { Log "  FIXED SERVER FAILED TO START" }
        }

        # 2) hidden quick-tunnel origin. The Worker keeps the public URL alive, so we watch the
        #    origin directly; when it changes, only the Worker script is redeployed.
        $origin = Get-OriginUrl
        # A candidate tunnel whose Worker deploy failed is kept alive and retried. Otherwise a
        # transient Cloudflare API timeout used to strand the Worker on a killed old tunnel.
        if ($script:pendingOrigin) {
            Log "retrying pending worker deploy to $script:pendingOrigin"
            $script:newTunnelPid = $script:pendingPid
            if (Apply-NewOrigin $script:pendingOrigin) {
                $script:pendingOrigin = $null; $script:pendingPid = 0
                $origin = Get-OriginUrl
            } else {
                $script:newTunnelPid = $script:pendingPid
            }
        }
        if (-not (Get-Process cloudflared -EA SilentlyContinue) -or -not (Test-UrlAlive $origin)) {
            Log "tunnel origin unhealthy (url=$origin) -> creating fresh quick tunnel"
            $newUrl = New-QuickTunnel
            if ($newUrl) {
                Log "  new tunnel origin: $newUrl"
                $ready = $false
                for ($i=0; $i -lt 20; $i++) {
                    Start-Sleep -Seconds 2
                    if (Test-UrlAlive $newUrl) { $ready = $true; break }
                }
                if ($ready) {
                    if (-not (Apply-NewOrigin $newUrl)) {
                        $script:pendingOrigin = $newUrl
                        $script:pendingPid    = $script:newTunnelPid
                        Log "  worker not switched yet; keeping candidate $newUrl for retry"
                    }
                } else { Log "  new origin not reachable yet; will retry next cycle" }
            } else { Log "  failed to create tunnel" }
        }

        # 3) agent: the enrolled agent starts itself in the user's desktop session (HKCU Run +
        #    EmployeeHome watchdog). This SYSTEM task must NEVER start it in session 0: that
        #    copy sees a black desktop and its fake "screen locked" status breaks live view.
        $agents = @(Get-CimInstance Win32_Process -Filter "Name='PulseDesk.Agent.exe'" -EA SilentlyContinue)
        foreach ($a in $agents) {
            if ($a.SessionId -eq 0) {
                try { Stop-Process -Id $a.ProcessId -Force; Log "stopped orphan session-0 agent pid=$($a.ProcessId)" }
                catch { Log "could not stop session-0 agent pid=$($a.ProcessId): $($_.Exception.Message)" }
            }
        }
        $interactive = @($agents | Where-Object { $_.SessionId -gt 0 })
        if ($interactive.Count -eq 0) { Log "no interactive agent found; agent self-start is handled by the installed user-session startup entry" }
    }
    catch { Log "keep-alive error: $($_.Exception.Message)" }
    if ($Once) { break }
    Start-Sleep -Seconds 20
}
