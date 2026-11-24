@echo off
title PulseDesk v.2 - connection check
color 0F
echo ============================================================
echo   PulseDesk v.2 - connection check
echo ============================================================
echo.

echo [1] Windows me login hone ke baad hi agent chalta hai.
echo     Laptop abhi lock screen par hai to pehle login karein.
echo.

echo [2] Agent process:
tasklist /fi "imagename eq PulseDesk.Agent.exe" | findstr /i "PulseDesk.Agent.exe"
if errorlevel 1 echo     AGENT PROCESS NAHI CHAL RAHA
echo.

echo [3] Auto-start entry (path + --run, koi token nahi dikhta):
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PulseDesk 2>nul
if errorlevel 1 echo     AUTO-START ENTRY Nahi MILI
echo.

echo [3b] Enrollment file (andar ka token/device id nahi dikha rahe):
if exist "%ProgramData%\PulseDesk\agent.json" (
  echo     ENROLLMENT OK - purana one-click update chalaya ja sakta hai
) else (
  echo     ENROLLMENT FILE NAHI MILI - installer se dobara enroll karna padega
)
echo.

echo [4] Server se link:
powershell -NoProfile -Command "try{$r=Invoke-WebRequest -Uri 'https://desktop-l21qf2i.taila5e0e7.ts.net/pulsedesk/agent-update.json' -UseBasicParsing -TimeoutSec 15; '    SERVER OK status='+$r.StatusCode}catch{'    SERVER FAIL: '+$_.Exception.Message}"
echo.

echo [5] Agent log (aakhri 15 line, token/device id chhupa diya gaya):
powershell -NoProfile -Command "$p='C:\ProgramData\PulseDesk\agent.log'; if(Test-Path $p){ Get-Content $p -Tail 15 | ForEach-Object { '    ' + ($_ -replace '[a-fA-F0-9]{32,}','<hidden>') } } else { '    log file nahi mila' }"
echo.

echo [6] Agent manually start karna (agar process nahi chal raha):
set "EXE=%ProgramFiles%\PulseDesk\PulseDesk.Agent.exe"
set "ALT=%LOCALAPPDATA%\PulseDesk\app\PulseDesk.Agent.exe"
if exist "%ALT%" set "EXE=%ALT%"
if exist "%EXE%" (
  echo     start: "%EXE%" --run
  start "" "%EXE%" --run
) else (
  echo     EXE NAHI MILA: "%EXE%"
)
echo.

echo 10 second baad admin screen refresh (Ctrl+Shift+R) karke device check karein.
echo.
pause
