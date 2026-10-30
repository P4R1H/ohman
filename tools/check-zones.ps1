# Ohman zone keyboard check, for four-zone and one-zone keyboards that only take colours through Windows Dynamic
# Lighting (HyperX OMEN 15-ga0xxx 8EDE in #44, 8EDD in #103). Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-zones.ps1 | iex
# What it does to the machine: for about a minute it paints the keyboard through HP's own lighting device (the
# interface Windows Dynamic Lighting itself uses) and asks you what you see. Windows Dynamic Lighting is switched
# off for HP's virtual lighting device during the test, the way Ohman does before it paints, and both are put back
# exactly as they were afterwards. Nothing is saved to the keyboard; no fans, modes or power are touched.
# Writes ohman-zones.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Core') {
    Write-Host "This one needs Windows PowerShell. Search the Start menu for Windows PowerShell, right click it, Run as administrator, and paste the line there." -ForegroundColor Yellow; return
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This needs administrator rights. Close this window, right click the Start button and pick Terminal (Admin) on Windows 11, or Windows PowerShell (Admin) on Windows 10. Click Yes, then paste the line again." -ForegroundColor Yellow; return
}
if (Get-Process Ohman -ErrorAction SilentlyContinue) {
    Write-Host "Quit Ohman first (right click its tray icon, Exit), then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-zones.txt'
Set-Content $out "ohman zone keyboard check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function Scrub([string]$s) { $s -replace [regex]::Escape($env:USERNAME), '<user>' -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
function W([string]$s) { $s = Scrub $s; Write-Host $s; Add-Content $out $s -Encoding utf8 }
function Ask([string]$q) { $a = Read-Host $q; W "  $q -> $a"; return $a }

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class OhmanLa {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int n);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int n);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

    // Every HID collection whose report descriptor says Lighting And Illumination / LampArray (0x59 / 0x01).
    // Opened with no access rights: enough to read its description, sends it nothing.
    public static List<string> LampArrays() {
        var r = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++) {
            int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(need);
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            string path = null;
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            Marshal.FreeHGlobal(buf);
            if (path == null) continue;
            using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                if (h.IsInvalid) continue;
                IntPtr p; if (!HidD_GetPreparsedData(h, out p)) continue;
                var c = new HIDP_CAPS(); HidP_GetCaps(p, ref c); HidD_FreePreparsedData(p);
                if (c.UsagePage == 0x59 && c.Usage == 0x01) r.Add(path + "|" + c.FeatureReportByteLength);
            }
        }
        SetupDiDestroyDeviceInfoList(set);
        return r;
    }

    static SafeFileHandle h;
    static int len;
    public static bool Open(string path, int featureLen) {
        h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // read/write, shared with Windows
        len = featureLen;
        return !h.IsInvalid;
    }
    public static void Close() { if (h != null) { h.Dispose(); h = null; } }

    // Report ids and field order are the HID Lighting And Illumination spec's (HUTRR84), the same ones src/Hid.cs uses.
    public static byte[] Get(byte id, byte[] request) {
        if (request != null) { var q = new byte[len]; Array.Copy(request, q, Math.Min(request.Length, len)); if (!HidD_SetFeature(h, q, len)) return null; }
        var b = new byte[len]; b[0] = id;
        return HidD_GetFeature(h, b, len) ? b : null;
    }
    // Only the three writes the spec defines for painting, and AutonomousMode: 4 multi update, 5 range update, 6 control.
    public static bool Set(byte[] report) {
        if (report == null || report.Length == 0 || !(report[0] == 4 || report[0] == 5 || report[0] == 6)) return false;
        var b = new byte[len]; Array.Copy(report, b, Math.Min(report.Length, len));
        return HidD_SetFeature(h, b, len);
    }
    public static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
    public static int I32(byte[] b, int i) { return b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24); }
}
"@

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
W ("HP software running: " + ((Get-Process | Where-Object { $_.Name -match 'OMEN|Omen|HyperX|LightStudio|HPLighting' } | ForEach-Object { $_.Name } | Sort-Object -Unique) -join ', '))

# The firmware's own colour table, for comparison: on 8EDE it read zeros whatever was written to it.
$intf = Get-WmiObject -Namespace root\wmi -Class hpqBIntM -ErrorAction SilentlyContinue | Select-Object -First 1
function Table {
    if (-not $intf) { return "(no HP firmware interface)" }
    try {
        $in = ([wmiclass]"root\wmi:hpqBDataIn").CreateInstance()
        $in.Sign = [byte[]](0x53,0x45,0x43,0x55); $in.Command = 0x20009; $in.CommandType = 0x02; $in.Size = 1; $in.hpqBData = [byte[]]@(0)
        $p = $intf.GetMethodParameters("hpqBIOSInt128"); $p.InData = $in
        $od = $intf.InvokeMethod("hpqBIOSInt128", $p, $null).OutData
        "rc=" + $od.rwReturnCode + " " + ((@($od.Data) | Select-Object -First 40 | ForEach-Object { ([byte]$_).ToString('X2') }) -join ' ')
    } catch { "read failed: " + $_ }
}
W ("firmware colour table (0x20009/02): " + (Table))

$found = @([OhmanLa]::LampArrays())
W ""; W "===== HID lighting devices (usage page 0x59) ====="
if ($found.Count -eq 0) { W "  none on this machine"; Start-Process explorer.exe "/select,`"$out`""; return }
$pick = $null; $pickLen = 0
foreach ($f in $found) {
    $path, $fl = $f -split '\|'
    $id = if ($path -match 'vid_[0-9a-f]{4}&pid_[0-9a-f]{4}') { $Matches[0] } else { '?' }
    $vhf = $path -match 'HID_DEVICE_SYSTEM_VHF'
    W ("  " + $id + $(if ($vhf) { "  (HP virtual device)" } else { "" }) + "  feature bytes " + $fl)
    if ($vhf -and -not $pick) { $pick = $path; $pickLen = [int]$fl }
}
if (-not $pick) { W "  no HP virtual lighting device, so nothing to test here"; Start-Process explorer.exe "/select,`"$out`""; return }
if (-not [OhmanLa]::Open($pick, $pickLen)) { W "  could not open HP's lighting device for writing"; Start-Process explorer.exe "/select,`"$out`""; return }

$dlSaved = @()
try {
    $dlSaved = @(Get-ChildItem 'HKCU:\Software\Microsoft\Lighting\Devices' -ErrorAction Stop | Where-Object { $_.PSChildName -match 'VHF' } | ForEach-Object {
        try { $props = Get-ItemProperty $_.PSPath -ErrorAction Stop; [pscustomobject]@{ Path = $_.PSPath; Value = $props.AmbientLightingEnabled } } catch { }
    })
} catch { }
if ($dlSaved.Count -gt 0) {
    Write-Host "If this window is closed before it finishes, turn Dynamic Lighting back on in Windows Settings > Personalization > Dynamic Lighting." -ForegroundColor DarkGray
}
try {
    W ""; W "===== What the device says about itself (nothing written) ====="
    $a = [OhmanLa]::Get(1, $null)
    if (-not $a) { W "  attributes report (id 1) refused"; return }
    $n = [OhmanLa]::U16($a, 1)
    if ($n -lt 1) { W "  the device reports no lamps"; return }
    W ("  lamps " + $n + ", kind " + [OhmanLa]::I32($a, 15) + ", " + ([OhmanLa]::I32($a, 3) / 1000) + " x " + ([OhmanLa]::I32($a, 7) / 1000) + " mm, min update " + ([OhmanLa]::I32($a, 19) / 1000) + " ms")
    $levels = @()
    for ($i = 0; $i -lt [Math]::Min($n, 16); $i++) {
        $r = [OhmanLa]::Get(3, [byte[]]@(2, ($i -band 0xFF), ($i -shr 8)))
        if (-not $r) { W "  lamp $i : no answer"; $levels += ,@(255, 255, 255, 255); continue }
        W ("  lamp " + [OhmanLa]::U16($r, 1) + ": x " + ([OhmanLa]::I32($r, 3) / 1000) + " mm, y " + ([OhmanLa]::I32($r, 7) / 1000) + " mm, purposes " + [OhmanLa]::I32($r, 19) +
           ", levels R/G/B/I " + $r[23] + "/" + $r[24] + "/" + $r[25] + "/" + $r[26] + ", programmable " + $r[27] + ", key 0x" + $r[28].ToString('X2'))
        $levels += ,@($r[23], $r[24], $r[25], $r[26])
    }
    # A colour channel is counted in the device's own levels, so full red is RedLevelCount - 1 (255 when it says 256 or 0).
    function Top($c) { if ($c -le 1) { 255 } else { [Math]::Min(255, $c - 1) } }
    $topR = Top $levels[0][0]; $topI = Top $levels[0][3]; $topG = Top $levels[0][1]; $topB = Top $levels[0][2]

    foreach ($d in $dlSaved) { Set-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -Value 0 -Type DWord -ErrorAction Stop }
    W ""; W ("===== Test (Windows Dynamic Lighting off for " + $dlSaved.Count + " HP virtual device(s)) =====")
    Start-Sleep -Seconds 2
    [void](Ask "Before anything is written: what colour is the keyboard now? (type it)")
    W ("  take the keyboard (6, autonomous off): " + [OhmanLa]::Set([byte[]]@(6, 0)))
    $last = $n - 1
    W ("  all red (5, range update): " + [OhmanLa]::Set([byte[]]@(5, 1, 0, 0, ($last -band 0xFF), ($last -shr 8), $topR, 0, 0, $topI)))
    Start-Sleep -Milliseconds 500
    W ("  firmware colour table now: " + (Table))
    $red = Ask "Is the keyboard red now? (y/n)"
    Start-Sleep -Seconds 10
    [void](Ask "Ten seconds later, is it still red? (y/n, or the colour it went back to)")
    if ($red -match '^\s*y') {
        # One lamp at a time, to learn which lamp is which zone. Multi update: count, flags, eight lamp ids, eight colours.
        for ($i = 0; $i -lt [Math]::Min($n, 8); $i++) {
            $b = New-Object byte[] 51
            $b[0] = 4; $b[1] = [byte][Math]::Min($n, 8); $b[2] = 1
            for ($j = 0; $j -lt [Math]::Min($n, 8); $j++) {
                $b[3 + 2 * $j] = [byte]$j
                $on = $j -eq $i; $ch = 3 + 16 + 4 * $j
                $b[$ch] = $(if ($on) { $topR } else { 0 }); $b[$ch + 1] = $(if ($on) { $topG } else { 0 }); $b[$ch + 2] = $(if ($on) { $topB } else { 0 }); $b[$ch + 3] = $(if ($on) { $topI } else { 0 })
            }
            W ("  lamp $i white, the rest off (4, multi update): " + [OhmanLa]::Set($b))
            [void](Ask "Which part of the keyboard is lit? (left / middle / right / wasd / all / none)")
        }
    }
} finally {
    try { W ("  keyboard back to its own lighting (6, autonomous on): " + [OhmanLa]::Set([byte[]]@(6, 1))) } catch { W ("  hand back failed: " + $_) }
    [OhmanLa]::Close()
    foreach ($d in $dlSaved) {
        try {
            if ($null -eq $d.Value) { Remove-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $d.Path -Name AmbientLightingEnabled -Value $d.Value -Type DWord -ErrorAction Stop }
        } catch { W ("  Dynamic Lighting restore failed: " + $_) }
    }
    W "  keyboard and Windows Dynamic Lighting put back"
}

Write-Host ""
Write-Host "Done. Drag ohman-zones.txt from your Desktop into the GitHub issue, then open Ohman again." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
