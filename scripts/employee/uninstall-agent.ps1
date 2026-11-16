# PulseDesk - permanent agent removal from ONE Windows laptop.
#
# Removes: running agent, logon auto-start entry (all loaded users), Programs-and-Features
# entry, legacy service, C:\Program Files\PulseDesk and C:\ProgramData\PulseDesk (config + logs).
#
# Run on the employee laptop (double-click "Run with PowerShell" is NOT enough - it needs admin):
#   powershell -ExecutionPolicy Bypass -File uninstall-agent.ps1
# The script elevates itself once (UAC = Yes).
#
# Remote route (no UAC needed on the laptop): run this file as SYSTEM, e.g.
#   psexec \\LAPTOP -s -d powershell -ExecutionPolicy Bypass -File C:\temp\uninstall-agent.ps1
# or push it with Intune / SCCM / a GPO startup script.

$ErrorActionPreference = 'SilentlyContinue'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Admin rights chahiye - UAC prompt aa raha hai (Yes dabaein)..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit
}

Write-Host "PulseDesk agent remove ho raha hai..." -ForegroundColor Cyan

# 1. stop the agent (and anything it spawned)
Get-Process PulseDesk.Agent -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force; Write-Host "  stopped agent pid=$($_.Id)" } catch { }
}
Start-Sleep -Seconds 2

# 2. logon auto-start entry: current user + every loaded user hive (the employee's session)
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PulseDesk -ErrorAction SilentlyContinue
Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-ItemProperty "$($_.PSPath)\Software\Microsoft\Windows\CurrentVersion\Run" -Name PulseDesk -ErrorAction SilentlyContinue
}

# 3. Programs and Features entry + legacy service (older builds installed a service)
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PulseDesk' -Recurse -Force -ErrorAction SilentlyContinue
sc.exe stop PulseDeskAgent | Out-Null
sc.exe delete PulseDeskAgent | Out-Null

# 3b. desktop + Start Menu icons
foreach ($f in @('CommonDesktopDirectory','DesktopDirectory','CommonPrograms','Programs')) {
    $dir = [Environment]::GetFolderPath($f)
    if ($dir) { Remove-Item (Join-Path $dir 'PulseDesk.lnk') -Force -ErrorAction SilentlyContinue }
}

# 4. files: install folder + ProgramData (agent.json, agent.log)
Remove-Item "$env:ProgramFiles\PulseDesk" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\PulseDesk" -Recurse -Force -ErrorAction SilentlyContinue

# 5. verify
$proc = Get-Process PulseDesk.Agent -ErrorAction SilentlyContinue
$pf   = Test-Path "$env:ProgramFiles\PulseDesk"
$pd   = Test-Path "$env:ProgramData\PulseDesk"
$run  = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PulseDesk -ErrorAction SilentlyContinue
$unin = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PulseDesk'
$task = Get-ScheduledTask -TaskName '*PulseDesk*' -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "agent running      : $([bool]$proc)"
Write-Host "Program Files dir  : $pf"
Write-Host "ProgramData dir    : $pd"
Write-Host "startup entry      : $([bool]$run)"
Write-Host "uninstall entry    : $unin"
Write-Host "scheduled tasks    : $(@($task).Count)"
Write-Host ""

if (-not $proc -and -not $pf -and -not $pd -and -not $run -and -not $unin) {
    Write-Host "RESULT: clean - PulseDesk is removed from this laptop." -ForegroundColor Green
    Write-Host "Server side: dashboard > Devices me ye laptop ab 'offline' rahega; chaho to device revoke/delete kar do." -ForegroundColor DarkGray
}
else {
    Write-Host "RESULT: kuch cheezein baqi hain (upar list). Script elevated chalao ya reboot ke baad dobara chalao." -ForegroundColor Yellow
}
