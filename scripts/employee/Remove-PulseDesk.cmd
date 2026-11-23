@echo off
title PulseDesk v.2 - remove
color 0F
echo ============================================================
echo   PulseDesk v.2 - remove
echo ============================================================
echo.
echo This removes the PulseDesk agent from THIS computer:
echo   - the running agent (stops immediately)
echo   - the logon auto-start entry
echo   - the "PulseDesk" entry in Settings / Apps
echo   - C:\Program Files\PulseDesk  and  C:\ProgramData\PulseDesk
echo.
echo Windows will ask for permission (UAC). Click YES.
echo If it asks for a password, this account is not an administrator -
echo give this file to your IT / admin instead.
echo.
pause

set "PD=%ProgramFiles%\PulseDesk\PulseDesk.Agent.exe"
if not exist "%PD%" (
    echo Agent file not found - it may already be removed.
    echo Running the full cleanup script instead...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-agent.ps1"
    goto :done
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process -FilePath '%PD%' -ArgumentList '--uninstall' -Verb RunAs"

:done
echo.
echo If the PulseDesk window asked "remove agent?" click YES.
echo Done.
pause
