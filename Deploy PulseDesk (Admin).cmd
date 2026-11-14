@echo off
setlocal
title PulseDesk - server deploy (admin)
cd /d "%~dp0"

echo ============================================================
echo  PulseDesk server deploy
echo  UAC prompt aayega - "Yes" dabaein
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-Command','Set-Location ''%~dp0''; & ''%~dp0scripts\deploy-server-elevated.ps1''; Write-Host ''--- deploy log ---'' -ForegroundColor Cyan; Get-Content ''%~dp0dist\logs\deploy-server.log'' -Tail 15'"

echo.
echo Elevated window khul gayi hogi - wahan result dekhein.
timeout /t 8 >nul
