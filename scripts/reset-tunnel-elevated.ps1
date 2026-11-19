# Final elevated reset: one keep-alive loop, one healthy quick tunnel, stable public URL.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root 'dist\logs\reset-tunnel-elevated.log'
function L($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    L 'UAC elevation requested'
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
L '=== elevated tunnel reset start ==='
function Test-Url([string]$u) {
    try {
        $curlExe = (Get-Command curl.exe -EA Stop).Source
        $code = & $curlExe -sS -o NUL -w '%{http_code}' --max-time 90 "$u/index.html" 2>$null
        return ([string]$code -eq '200')
    } catch { return $false }
}
$cf = 'C:\Users\numan\AppData\Local\Microsoft\WinGet\Packages\Cloudflare.cloudflared_Microsoft.Winget.Source_8wekyb3d8bbwe\cloudflared.exe'
$logDir = Join-Path $root 'dist\logs'
$originFile = Join-Path $logDir 'CURRENT-ORIGIN.txt'
$legacyUrl = Join-Path $logDir 'CURRENT-URL.txt'
$deploy = Join-Path $root 'scripts\deploy-worker.ps1'
$keepAlive = Join-Path $root 'scripts\keep-alive.ps1'
$serverExe = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'
$serverDir = Join-Path $root 'dist\server'
$stable = 'https://pulsedesk.pulsedesk-numan.workers.dev'

try {
    Stop-ScheduledTask -TaskName 'PulseDesk KeepAlive' -EA SilentlyContinue
    L 'keep-alive task stopped'

    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -EA SilentlyContinue)) {
        $isKeeper = ($p.CommandLine -match 'keep-alive\.ps1') -or
                    ($p.CommandLine -match 'reset-tunnel-elevated\.ps1') -or
                    ($p.SessionId -eq 0)
        if ($isKeeper -and $p.ProcessId -ne $PID) {
            try { Stop-Process -Id $p.ProcessId -Force -EA Stop; L "stopped old keep-alive/reset powershell pid=$($p.ProcessId)" } catch {}
        }
    }
    Get-Process cloudflared -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    L 'old cloudflared processes stopped'
    Get-ChildItem $logDir -Filter 'tunnel-new-*.log' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue

    $origin = $null
    foreach ($attempt in 1..6) {
        L "tunnel attempt $attempt"
        $out = Join-Path $logDir "reset-$attempt.out.log"
        $err = Join-Path $logDir "reset-$attempt.err.log"
        Set-Content $out ''; Set-Content $err ''
        $proc = Start-Process -FilePath $cf -ArgumentList @('tunnel','--url','http://localhost:5081','--no-autoupdate') `
            -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
        for ($i=0; $i -lt 75; $i++) {
            Start-Sleep -Seconds 2
            $txt = (Get-Content $out -Raw -EA SilentlyContinue) + "`n" + (Get-Content $err -Raw -EA SilentlyContinue)
            if ($txt -match 'https://[a-z0-9]+(?:-[a-z0-9]+)+\.trycloudflare\.com') {
                $candidate = $Matches[0]
                # A freshly issued quick tunnel can take 30-60 s on a loaded link. Do NOT kill
                # it after two seconds: that churn is what kept the public URL down.
                for ($h=0; $h -lt 12; $h++) {
                    Start-Sleep -Seconds 10
                    if (Test-Url $candidate) { $origin = $candidate; break }
                    L "  candidate still warming up ($h)"
                }
                if ($origin) { break }
            }
            if ($proc.HasExited) { L "  cloudflared exited early on attempt $attempt"; break }
        }
        if ($origin) { L "healthy tunnel: $origin"; break }
        try { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue } catch {}
    }
    if (-not $origin) { L 'NO HEALTHY TUNNEL - leaving stack stopped for manual attention'; return }

    [IO.File]::WriteAllText($originFile, $origin)
    [IO.File]::WriteAllText($legacyUrl, $origin)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $deploy -Origin $origin 2>&1 | ForEach-Object { L "worker: $_" }

    $workerOk = $false
    for ($i=0; $i -lt 6; $i++) {
        if (Test-Url $stable) { $workerOk = $true; break }
        L "worker not ready yet ($i)"
        Start-Sleep -Seconds 5
    }
    L "worker healthy: $workerOk"

    # Server with the permanent public URL (only one instance should be listening).
    foreach ($p in @(Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)) {
        try { Stop-Process -Id $p -Force -EA Stop; L "stopped server pid=$p" } catch {}
    }
    Start-Sleep -Seconds 2
    Start-Process -FilePath $serverExe -ArgumentList @('--urls','http://0.0.0.0:5081',"--PublicBaseUrl=$stable") -WorkingDirectory $serverDir -WindowStyle Hidden
    for ($i=0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 700; if (Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue) { break } }
    L "server listening: $([bool](Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue))"

    Start-ScheduledTask -TaskName 'PulseDesk KeepAlive' -EA SilentlyContinue
    L 'keep-alive task started'
    L "=== reset DONE origin=$origin workerOk=$workerOk ==="
} catch { L "ERROR: $($_.Exception.Message)" }
