# One-time elevated finalisation for the stable workers.dev URL.
# Self-elevates with UAC, then:
#   1) stops the SYSTEM server on 5081 and restarts it with PublicBaseUrl = stable URL
#   2) stops the orphaned session-0 agent (it reports a fake LOCKED screen)
#   3) restarts the SYSTEM keep-alive task so the new stable-URL script is loaded
# It deliberately does NOT start an agent: the agent must run in the user's desktop session.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root 'dist\logs\finalize-stable-elevated.log'
function L($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    L 'UAC elevation requested'
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
L '=== elevated finalize start ==='
$stable = 'https://pulsedesk.pulsedesk-numan.workers.dev'
$serverExe = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'
$serverDir = Join-Path $root 'dist\server'

try {
    $pids = @(Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($p in $pids) { try { Stop-Process -Id $p -Force -EA Stop; L "stopped server pid=$p" } catch { L "server stop pid=$p failed: $($_.Exception.Message)" } }
    Start-Sleep -Seconds 2

    foreach ($a in @(Get-CimInstance Win32_Process -Filter "Name='PulseDesk.Agent.exe'" -EA SilentlyContinue)) {
        if ($a.SessionId -eq 0) { try { Stop-Process -Id $a.ProcessId -Force -EA Stop; L "stopped session-0 agent pid=$($a.ProcessId)" } catch { L "session-0 agent stop failed: $($_.Exception.Message)" } }
    }

    try { Stop-ScheduledTask -TaskName 'PulseDesk KeepAlive' -EA Stop; L 'keep-alive task stopped' }
    catch { L "keep-alive stop note: $($_.Exception.Message)" }
    Start-Sleep -Seconds 2

    # One elevated self-heal pass restores a healthy tunnel and redeploys the Worker with the
    # new hidden origin before the normal (SYSTEM) loop is started again.
    $ka = Join-Path $root 'scripts\keep-alive.ps1'
    L 'running one elevated self-heal pass'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $ka -Once 2>&1 | Out-Null

    if (-not (Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue)) {
        Start-Process -FilePath $serverExe -ArgumentList @('--urls','http://0.0.0.0:5081',"--PublicBaseUrl=$stable") -WorkingDirectory $serverDir -WindowStyle Hidden
        for ($i=0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 700; if (Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue) { break } }
    }
    $up = [bool](Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue)
    L "server listening after restart: $up"

    try { Start-ScheduledTask -TaskName 'PulseDesk KeepAlive' -EA Stop; L 'keep-alive task started with new script' }
    catch { L "keep-alive start failed: $($_.Exception.Message)" }
    L '=== elevated finalize DONE ==='
} catch { L "ERROR: $($_.Exception.Message)" }
