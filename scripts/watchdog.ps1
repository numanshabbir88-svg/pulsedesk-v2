# PulseDesk watchdog - keeps server + tunnel alive.
# Run: powershell -ExecutionPolicy Bypass -File scripts\watchdog.ps1 -Mode cloudflared
param(
    [ValidateSet('tailscale','cloudflared','none')]
    [string]$Mode = 'tailscale',
    [int]$CheckSeconds = 30
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$startAll = Join-Path $PSScriptRoot 'start-all.ps1'
$logDir = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'watchdog.log'

function Log($m) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m"
    Add-Content -Path $log -Value $line -Encoding utf8
}

function Test-Port([int]$port) {
    try {
        $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop
        return [bool]$c
    } catch { return $false }
}

function Test-Internet {
    # fast TCP probe with a couple of fallbacks (some networks block 1.1.1.1:443)
    foreach ($target in @(@('1.1.1.1', 443), @('8.8.8.8', 53), @('9.9.9.9', 443))) {
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $iar = $client.BeginConnect($target[0], $target[1], $null, $null)
            $ok = $iar.AsyncWaitHandle.WaitOne(1500, $false) -and $client.Connected
            $client.Close()
            if ($ok) { return $true }
        } catch { }
    }
    return $false
}

Log "watchdog started (mode=$Mode, every ${CheckSeconds}s)"
$lastNet = $true
$restarts = 0

while ($true) {
    try {
        # --- internet came back?
        $net = Test-Internet
        if ($net -and -not $lastNet) { Log "internet restored - re-checking stack" }
        if (-not $net -and $lastNet)  { Log "internet lost - waiting" }
        $lastNet = $net

        # --- server
        if (-not (Test-Port 5080)) {
            Log "server not listening on 5080 - restarting stack"
            & powershell -NoProfile -ExecutionPolicy Bypass -File $startAll -Mode $Mode | Out-Null
            $restarts++
        }

        # --- tunnel (cloudflared only; process may die when Wi-Fi flaps)
        if ($Mode -eq 'cloudflared') {
            if ($net -and -not (Get-Process cloudflared -ErrorAction SilentlyContinue)) {
                Log "cloudflared not running - restarting stack"
                & powershell -NoProfile -ExecutionPolicy Bypass -File $startAll -Mode $Mode | Out-Null
                $restarts++
            }
        }

        # --- tailscale funnel (local check, works even if the internet probe is blocked)
        if ($Mode -eq 'tailscale') {
            $tsExe = (Get-Command tailscale -ErrorAction SilentlyContinue).Source
            if (-not $tsExe) {
                foreach ($p in @("$env:ProgramFiles\Tailscale\tailscale.exe", "$env:LOCALAPPDATA\Tailscale\tailscale.exe")) {
                    if (Test-Path $p) { $tsExe = $p; break }
                }
            }
            if (-not $tsExe) { Log "tailscale CLI not found - cannot verify funnel" }
            else {
                $st = & $tsExe funnel status 2>&1 | Out-String
                if ($st -notmatch '5080') {
                    Log "tailscale funnel missing /pulsedesk -> 5080 - re-enabling"
                    & $tsExe funnel --bg --set-path /pulsedesk http://localhost:5080 2>&1 | ForEach-Object { Log $_ }
                    $restarts++
                }
            }
        }

        if ($restarts -gt 0 -and $restarts % 10 -eq 0) { Log "stack restarts so far: $restarts" }
    }
    catch {
        Log "watchdog error: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds $CheckSeconds
}
