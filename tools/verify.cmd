@echo off
:: OmenLite hardware verification. Right-click -> "Run as administrator" (or accept the UAC prompt).
:: Runs READ-ONLY BIOS queries through the HP WMI interface and writes the raw results to verify.txt
:: next to this file, then prints them. Nothing is changed on the machine.
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo OmenLite verification  %date% %time% > verify.txt
echo ---- machine ---- >> verify.txt
powershell -NoProfile -Command "$cs=Get-CimInstance Win32_ComputerSystem; $bb=Get-CimInstance Win32_BaseBoard; $b=Get-CimInstance Win32_BIOS; 'Model: '+$cs.Model+'  Board: '+$bb.Product+'  BIOS: '+$b.SMBIOSBIOSVersion" >> verify.txt
echo ---- BIOS read-only queries ---- >> verify.txt
omenprobe.exe read >> verify.txt 2>&1
echo ---- Thermal zone (ACPI) ---- >> verify.txt
powershell -NoProfile -Command "try { Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature | %% { $_.InstanceName + ' = ' + [math]::Round($_.CurrentTemperature/10-273.15,1) + ' C' } } catch { 'n/a: ' + $_ }" >> verify.txt
echo ---- hpqBEvnt (press the OMEN key and any Fn keys within 15 s) ---- >> verify.txt
echo Press the OMEN key now (and optionally other Fn keys) - listening 15 seconds...
omenprobe.exe watch 15 >> verify.txt 2>&1
echo. >> verify.txt
type verify.txt
echo.
echo Results saved to %~dp0verify.txt
pause
