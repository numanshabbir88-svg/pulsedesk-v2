@echo off
chcp 65001 >nul
title PulseDesk One-Click Repair
REM One small file, no setup re-download. Finds a working PulseDesk address, updates the
REM installed agent config, allows it through Windows Firewall/Defender and restarts it.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$code = @'
$ErrorActionPreference = 'Continue'
$dir = 'C:\ProgramData\PulseDesk'
$cfg = Join-Path $dir 'agent.json'
$exe = 'C:\Program Files\PulseDesk\PulseDesk.Agent.exe'
$report = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PulseDesk-Repair.txt'
$out = New-Object System.Collections.Generic.List[string]
$out.Add('PulseDesk one-click repair - ' + (Get-Date))
$out.Add('Computer: ' + $env:COMPUTERNAME)

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  $out.Add('Not elevated - relaunching as administrator (please click Yes on UAC).')
  $out | Set-Content $report -Encoding UTF8
  Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $MyInvocation.MyCommand.Path)
  Write-Host 'Approve the UAC prompt. Report: ' $report
  exit
}

$urls = @(
  'https://pulsedesk.pulsedesk-numan.workers.dev',
  'https://pdsbackup.pulsedesk-numan.workers.dev',
  'https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk'
)
$working = $null
foreach ($u in $urls) {
  try {
    $r = Invoke-WebRequest -Uri ($u + '/index.html') -UseBasicParsing -TimeoutSec 15
    if ([int]$r.StatusCode -eq 200) { $working = $u; $out.Add('REACHABLE  ' + $u); break }
    $out.Add('FAILED     ' + $u + '  HTTP ' + [int]$r.StatusCode)
  } catch {
    $out.Add('FAILED     ' + $u + '  ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
  }
}

if (-not $working) {
  $out.Add('')
  $out.Add('None of the three PulseDesk addresses are reachable from this laptop.')
  $out.Add('This is an internet/DNS/proxy issue on this machine - send this report to the admin.')
  $out | Set-Content $report -Encoding UTF8
  Write-Host 'No address reachable. Report: ' $report
  exit 1
}

if (Test-Path $cfg) {
  $s = Get-Content $cfg -Raw | ConvertFrom-Json
  $out.Add('Old server: ' + $s.Server)
  $s.Server = $working
  $s | ConvertTo-Json -Depth 5 | Set-Content $cfg -Encoding UTF8
  $out.Add('New server: ' + $working)
} else {
  $out.Add('agent.json not found - PulseDesk is not installed on this laptop.')
}

$netsh = Join-Path $env:windir 'System32\netsh.exe'
if (Test-Path $netsh) {
  & $netsh advfirewall firewall delete rule name='PulseDesk Agent' 2>$null | Out-Null
  & $netsh advfirewall firewall add rule name='PulseDesk Agent' dir=in action=allow program=$exe enable=yes 2>$null | Out-Null
  & $netsh advfirewall firewall add rule name='PulseDesk Agent Out' dir=out action=allow program=$exe enable=yes 2>$null | Out-Null
  $out.Add('Firewall: program-scoped allow rules ensured (firewall itself stays ON).')
}
$ps = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path $ps) {
  $mp = 'Add-MpPreference -ExclusionPath ''{0}'' -ErrorAction SilentlyContinue' -f $exe
  & $ps -NoProfile -Command $mp 2>$null | Out-Null
  $out.Add('Defender: exclusion added for the exact agent file only.')
}

Get-Process -Name 'PulseDesk.Agent' -EA SilentlyContinue | ForEach-Object {
  try { Stop-Process -Id $_.Id -Force; $out.Add('Restarted agent pid=' + $_.Id) } catch { }
}
Start-Sleep -Seconds 2
if (Test-Path $exe) {
  Start-Process -FilePath $exe -ArgumentList '--run' -WindowStyle Hidden
  $out.Add('Agent started.')
}
Start-Sleep -Seconds 10
$log = Join-Path $dir 'agent.log'
if (Test-Path $log) {
  $out.Add('')
  $out.Add('--- agent.log (last 25 lines) ---')
  $out.Add((Get-Content $log -Tail 25 | Out-String).Trim())
}
$out | Set-Content $report -Encoding UTF8
Write-Host 'Done. Report: ' $report
'@; $f = Join-Path $env:TEMP 'pds-repair-run.ps1'; Set-Content -Path $f -Value $code -Encoding UTF8; & $f"
echo.
echo Report on Desktop: PulseDesk-Repair.txt
pause
