@echo off
chcp 65001 >nul
title PulseDesk Connection Check
echo Checking both PulseDesk addresses. This takes about a minute...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0PulseDesk-Diagnostic.ps1"
echo.
echo Send the PulseDesk-Diagnostic.txt file from your Desktop to your admin.
pause
