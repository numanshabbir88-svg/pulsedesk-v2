@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
title PulseDesk One-Click Repair
REM Single small file, no setup re-download. Finds a working PulseDesk address, updates the
REM installed agent config, allows it through Windows Firewall/Defender and restarts it.
REM Pure batch + short PowerShell one-liners: no here-strings, no hidden files, no persistence.

set "CFG=C:\ProgramData\PulseDesk\agent.json"
set "EXE=C:\Program Files\PulseDesk\PulseDesk.Agent.exe"
set "LOG=C:\ProgramData\PulseDesk\agent.log"
set "REPORT=%USERPROFILE%\Desktop\PulseDesk-Repair.txt"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Admin rights chahiye - UAC prompt par Yes dabayein...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

> "%REPORT%" echo PulseDesk one-click repair - %DATE% %TIME%
>>"%REPORT%" echo Computer: %COMPUTERNAME%

set "WORKING="
for %%U in (
  https://pulsedesk.pulsedesk-numan.workers.dev
  https://pdsbackup.pulsedesk-numan.workers.dev
  https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk
) do (
  if not defined WORKING (
    powershell -NoProfile -Command "try { $r=Invoke-WebRequest -Uri '%%U/index.html' -UseBasicParsing -TimeoutSec 15; if ([int]$r.StatusCode -eq 200) { exit 0 } else { exit 1 } } catch { exit 1 }"
    if !errorlevel! equ 0 (
      set "WORKING=%%U"
      >>"%REPORT%" echo REACHABLE  %%U
    ) else (
      >>"%REPORT%" echo FAILED     %%U
    )
  )
)

if not defined WORKING (
  >>"%REPORT%" echo.
  >>"%REPORT%" echo Kisi bhi PulseDesk address tak is laptop se raasta nahi hai.
  >>"%REPORT%" echo Ye internet/DNS/proxy ya security software ka block hai - report admin ko bhejein.
  echo No address reachable. Report: %REPORT%
  echo.
  pause
  exit /b 1
)

if exist "%CFG%" (
  >>"%REPORT%" echo Selected: %WORKING%
  powershell -NoProfile -Command "$p='%CFG%'; $s=Get-Content $p -Raw | ConvertFrom-Json; $s.Server='%WORKING%'; $s | ConvertTo-Json -Depth 5 | Set-Content $p -Encoding UTF8; Write-Output ('Server set to: ' + $s.Server)" >>"%REPORT%" 2>&1
) else (
  >>"%REPORT%" echo agent.json missing - PulseDesk is not installed on this laptop.
)

netsh advfirewall firewall delete rule name="PulseDesk Agent" >nul 2>&1
netsh advfirewall firewall add rule name="PulseDesk Agent" dir=in action=allow program="%EXE%" enable=yes >nul 2>&1
netsh advfirewall firewall add rule name="PulseDesk Agent Out" dir=out action=allow program="%EXE%" enable=yes >nul 2>&1
>>"%REPORT%" echo Firewall: program-scoped rules ensured (firewall stays ON).

powershell -NoProfile -Command "Add-MpPreference -ExclusionPath '%EXE%' -ErrorAction SilentlyContinue" >nul 2>&1
>>"%REPORT%" echo Defender: exclusion added for the exact agent file only.

taskkill /F /IM PulseDesk.Agent.exe >nul 2>&1
timeout /t 2 >nul
if exist "%EXE%" (
  start "" "%EXE%" --run
  >>"%REPORT%" echo Agent restarted.
)

timeout /t 10 >nul
if exist "%LOG%" (
  >>"%REPORT%" echo.
  >>"%REPORT%" echo --- agent.log last 25 lines ---
  powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 25" >>"%REPORT%" 2>&1
)

echo.
echo Done. Report: %REPORT%
echo.
pause
