# PulseDesk master keep-alive - keeps the whole stack alive WITHOUT a custom domain.
# Keeps alive: admin server (5080), Cloudflare tunnel (fixed creds -> stable .trycloudflare.com URL),
#             and the enrolled Windows agent.
# IMPORTANT: never edits appsettings.json / PublicBaseUrl. The URL stays the one baked into dist config.
# Runs forever; registered as a SYSTEM scheduled task by install-keepalive.ps1.

$ErrorActionPreference = 'Continue'
$root       = Split-Path -Parent $PSScriptRoot
$logDir     = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log        = Join-Path $logDir 'keep-alive.log'
$startAll   = Join-Path $PSScriptRoot 'start-all.ps1'

# --- tunnel identity (same fixed credentials that are running right now) ---
$cf        = "C:\Users\numan\AppData\Local\Microsoft\WinGet\Packages\Cloudflare.cloudflared_Microsoft.Winget.Source_8wekyb3d8bbwe\cloudflared.exe"
$cred      = Join-Path $env:USERPROFILE '.cloudflared\pd-tunnel.json'
$tunnelId  = '85c405c9-73e7-4a1b-a9a7-3269a699231b'

# --- agent ---
$agentExe  = 'C:\Program Files\PulseDesk\PulseDesk.Agent.exe'
$agentDir  = 'C:\Program Files\PulseDesk'

function Log($m) {
    Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8
}

function Test-Port5080 {
    try { return [bool](Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction Stop) } catch { return $false }
}

Log "keep-alive started (no-domain mode; PublicBaseUrl untouched)"

while ($true) {
    try {
        # 1) admin server - reuse the proven start-all with -Mode none (never rewrites URL)
        if (-not (Test-Port5080)) {
            Log "server down on 5080 -> start-all -Mode none"
            & powershell -NoProfile -ExecutionPolicy Bypass -File $startAll -Mode none 2>&1 | ForEach-Object { Log "  $_" }
        }

        # 2) Cloudflare tunnel - fixed creds keep the same .trycloudflare.com hostname
        if (-not (Get-Process cloudflared -ErrorAction SilentlyContinue)) {
            if ((Test-Path $cf) -and (Test-Path $cred)) {
                Log "cloudflared down -> restarting with fixed credentials (stable URL)"
                Start-Process -FilePath $cf `
                    -ArgumentList @('tunnel','--credentials-file',$cred,'--url','http://localhost:5080','run',$tunnelId) `
                    -WindowStyle Hidden
            } else {
                Log "cloudflared exe or credentials missing: cf=$cf cred=$cred"
            }
        }

        # 3) enrolled Windows agent
        if ((Test-Path $agentExe) -and -not (Get-Process PulseDesk.Agent -ErrorAction SilentlyContinue)) {
            Log "agent down -> restarting"
            Start-Process -FilePath $agentExe -WorkingDirectory $agentDir -WindowStyle Hidden
        }
    }
    catch {
        Log "keep-alive error: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 15
}
