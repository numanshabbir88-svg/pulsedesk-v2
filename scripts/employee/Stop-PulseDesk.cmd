@echo off
title PulseDesk - stop monitoring (no admin needed)
color 0F
echo ============================================================
echo   PulseDesk v.2 - STOP monitoring on this computer
echo ============================================================
echo.
echo Use this when you do NOT have administrator rights.
echo It stops the agent now and removes it from your logon startup,
echo so it will not come back when you sign in again.
echo.
echo It does NOT delete the program files (that needs an administrator).
echo.
pause

echo Stopping the agent...
taskkill /f /im PulseDesk.Agent.exe >nul 2>&1
if errorlevel 1 (
    echo   agent was not running
) else (
    echo   agent stopped
)

echo Removing the logon auto-start entry...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PulseDesk /f >nul 2>&1
if errorlevel 1 (
    echo   no startup entry for this user \(already clean\)
) else (
    echo   startup entry removed
)

echo.
echo Monitoring is now OFF for this Windows account.
echo For a full removal, ask IT/admin to run "Remove-PulseDesk.cmd".
echo.
pause
