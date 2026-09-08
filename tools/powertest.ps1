# Measures what the "Power gain" slider actually changes on this machine.
# Usage: start a GPU load (game or benchmark loop), then run this script once with the slider at +0 W and
# once at +15 W. Compare the averages: GPU watts / clock rising = the gain went to the GPU (Dynamic Boost).
#   powershell -ExecutionPolicy Bypass -File powertest.ps1 -Seconds 60 -Label "+15W"
param([int]$Seconds = 60, [string]$Label = "")
$nv = "$env:SystemRoot\System32\nvidia-smi.exe"
if (-not (Test-Path $nv)) { "nvidia-smi not found"; exit 1 }
$cpuUtil = New-Object System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Utility", "_Total", $true)
$cpuFreq = New-Object System.Diagnostics.PerformanceCounter("Processor Information", "Processor Frequency", "_Total", $true)
[void]$cpuUtil.NextValue()
$rows = @()
"sampling $Seconds s $Label ... (keep the load running)"
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $g = (& $nv --query-gpu=power.draw,clocks.gr,clocks.mem,temperature.gpu,utilization.gpu --format=csv,noheader,nounits) -split ','
    $rows += [pscustomobject]@{
        gpuW = (Num $g[0]); gpuMHz = (Num $g[1]); gpuTemp = (Num $g[3]); gpuUtil = (Num $g[4])
        cpuUtil = [Math]::Min(100, $cpuUtil.NextValue()); cpuMHz = $cpuFreq.NextValue()
    }
    if ($i % 10 -eq 9) { "  {0,3}s  GPU {1,5:N1} W  {2,4:N0} MHz  {3,3:N0} %   CPU {4,3:N0} %  {5,4:N0} MHz" -f ($i + 1), $rows[-1].gpuW, $rows[-1].gpuMHz, $rows[-1].gpuUtil, $rows[-1].cpuUtil, $rows[-1].cpuMHz }
}
$avg = $rows | Measure-Object -Property gpuW, gpuMHz, gpuTemp, gpuUtil, cpuUtil, cpuMHz -Average
$max = $rows | Measure-Object -Property gpuW, gpuMHz -Maximum
""
"RESULT $Label  ($Seconds s)"
"  GPU power   avg {0,6:N1} W   max {1,6:N1} W" -f ($avg | ? Property -eq gpuW).Average, ($max | ? Property -eq gpuW).Maximum
"  GPU clock   avg {0,6:N0} MHz max {1,6:N0} MHz" -f ($avg | ? Property -eq gpuMHz).Average, ($max | ? Property -eq gpuMHz).Maximum
"  GPU temp    avg {0,6:N1} C    util {1,5:N0} %" -f ($avg | ? Property -eq gpuTemp).Average, ($avg | ? Property -eq gpuUtil).Average
"  CPU clock   avg {0,6:N0} MHz  util {1,5:N0} %" -f ($avg | ? Property -eq cpuMHz).Average, ($avg | ? Property -eq cpuUtil).Average
$out = Join-Path $PSScriptRoot 'powertest.txt'
"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Label  GPU avg {0:N1} W / {1:N0} MHz (max {2:N1} W)  CPU avg {3:N0} MHz @ {4:N0} %" -f ($avg | ? Property -eq gpuW).Average, ($avg | ? Property -eq gpuMHz).Average, ($max | ? Property -eq gpuW).Maximum, ($avg | ? Property -eq cpuMHz).Average, ($avg | ? Property -eq cpuUtil).Average | Out-File $out -Append -Encoding utf8
"appended to $out"
