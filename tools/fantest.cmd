@echo off
:: Runs fantest.ps1 elevated (see the notes at the top of fantest.ps1). Only when the laptop is cool and idle.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fantest.ps1"
echo.
pause
