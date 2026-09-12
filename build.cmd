@echo off
:: Builds Ohman with the C# compiler that ships inside Windows (.NET Framework 4.8) - no SDK needed.
::   build.cmd            -> Ohman.exe (real; asks for administrator rights), preview\Ohman.exe, tools\omenprobe.exe
::   build.cmd preview    -> only preview\Ohman.exe (same UI, simulated hardware, no elevation)
:: The exe icon (app.ico) is drawn by the app itself so the mark has a single source of truth.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set REFS=/r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xaml.dll /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\WindowsBase.dll"
set SRC=src\Platform.cs src\Hardware.cs src\Lighting.cs src\Keyboard.cs src\Display.cs src\Sensors.cs src\Engine.cs src\Ui.cs src\Program.cs
set RES=/resource:src\Ui.xaml,Ohman.Ui.xaml
if not exist preview mkdir preview

:: 1. preview build without an icon, used to generate app.ico
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:1 /out:preview\Ohman.exe /win32manifest:app.demo.manifest %RES% %REFS% %SRC%
if errorlevel 1 exit /b 1
preview\Ohman.exe --make-ico app.ico
if exist app.ico (set ICO=/win32icon:app.ico) else (set ICO=)

:: 2. preview build with the icon
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:1 /out:preview\Ohman.exe /win32manifest:app.demo.manifest %ICO% %RES% %REFS% %SRC%
if errorlevel 1 exit /b 1
if /i "%~1"=="preview" (echo Built preview\Ohman.exe & exit /b 0)

:: 3. the real thing + the probe tool
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:1 /out:Ohman.exe /win32manifest:app.manifest %ICO% %RES% %REFS% %SRC%
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /warn:1 /out:tools\omenprobe.exe /r:System.Management.dll tools\omenprobe.cs
if errorlevel 1 exit /b 1
echo Built Ohman.exe, preview\Ohman.exe and tools\omenprobe.exe
