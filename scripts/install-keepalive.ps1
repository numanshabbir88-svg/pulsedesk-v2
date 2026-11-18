# Registers the PulseDesk master keep-alive as a SYSTEM scheduled task:
#   - starts at boot (no user logon / password needed)
#   - re-launches every 10 minutes as a safety net if the loop ever dies
# Runs as Administrator (self-elevates with UAC).
# Safe to run repeatedly: it unregisters and re-registers the same task.

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$watchdog  = Join-Path $PSScriptRoot 'keep-alive.ps1'
$taskName  = 'PulseDesk KeepAlive'
$oldTask   = 'PulseDesk Watchdog'
$resultLog = Join-Path $root 'dist\logs\install-keepalive.result.log'
New-Item -ItemType Directory -Force -Path (Split-Path $resultLog) | Out-Null

function RLog($m) {
    Add-Content -Path $resultLog -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m"
    Write-Host $m
}

try {
    RLog "=== install-keepalive start ==="

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    RLog "isAdmin=$isAdmin"

    if (-not $isAdmin) {
        RLog "self-elevating via UAC..."
        $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        Start-Process powershell -Verb RunAs -ArgumentList $argList
        exit
    }

    if (-not (Test-Path $watchdog)) { throw "keep-alive.ps1 not found: $watchdog" }
    RLog "watchdog script found: $watchdog"

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watchdog`"" `
        -WorkingDirectory $root

    $triggers = @(
        New-ScheduledTaskTrigger -AtStartup
        New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
            -RepetitionInterval (New-TimeSpan -Minutes 10) `
            -RepetitionDuration (New-TimeSpan -Days 3650)
    )

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    Unregister-ScheduledTask -TaskName $oldTask -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    RLog "old tasks removed"

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers `
        -Settings $settings -Principal $principal `
        -Description 'PulseDesk no-domain keep-alive: server + cloudflare tunnel + agent.' | Out-Null
    RLog "task registered"

    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 4
    RLog "task started"

    $info = Get-ScheduledTask -TaskName $taskName
    RLog "Task: $($info.TaskName) State: $($info.State)"
    RLog "=== install-keepalive DONE ==="
}
catch {
    RLog "ERROR: $($_.Exception.Message)"
    RLog "STACK: $($_.ScriptStackTrace)"
}
