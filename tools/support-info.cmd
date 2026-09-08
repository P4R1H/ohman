@echo off
:: Collects the information needed to add your laptop model to Ohman (no personal data). Asks for admin once.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0support-info.ps1"
echo.
pause
