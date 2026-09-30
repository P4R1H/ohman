# Ohman per-key check, for OMEN 16/17 per-key keyboards (Primax 0461:4E9A / 4E9B). Run from Terminal (Admin):
#   irm https://raw.githubusercontent.com/P4R1H/ohman/main/tools/check-perkey.ps1 | iex
# What it does to the machine: reads the keyboard's own description, asks it three questions, then lights it
# red and a few single keys for a minute, asking you what you see. Nothing is saved to the keyboard; a restart
# puts back whatever was there. Writes ohman-perkey.txt to the Desktop.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Core') {
    Write-Host "This one needs Windows PowerShell. Search the Start menu for Windows PowerShell, right click it, Run as administrator, and paste the line there." -ForegroundColor Yellow; return
}
if (Get-Process OmenCommandCenterBackground -ErrorAction SilentlyContinue) {
    Write-Host "OMEN Gaming Hub is running and holds the keyboard. Close it (and end OmenCommandCenterBackground in Task Manager), then paste the line again." -ForegroundColor Yellow; return
}
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ohman-perkey.txt'
Set-Content $out "ohman per-key check $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8
function W([string]$s) { Write-Host $s; Add-Content $out $s -Encoding utf8 }

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class OhmanPk {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS {
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
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort len, IntPtr p);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

    public static List<string> Paths() {
        var r = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++) {
            int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(need);
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) r.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
            Marshal.FreeHGlobal(buf);
        }
        SetupDiDestroyDeviceInfoList(set);
        return r;
    }

    // Opened with no access: reads the device's own description and sends it nothing.
    public static string Describe(string path, out int reportId, out int outLen) {
        reportId = -1; outLen = 0;
        var sb = new StringBuilder();
        using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (h.IsInvalid) return "  cannot open (error " + Marshal.GetLastWin32Error() + ")";
            var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a); HidD_GetAttributes(h, ref a);
            IntPtr p; if (!HidD_GetPreparsedData(h, out p)) return "  no preparsed data";
            var c = new HIDP_CAPS(); HidP_GetCaps(p, ref c);
            outLen = c.OutputReportByteLength;
            sb.AppendLine("  version " + a.VersionNumber.ToString("X4") + "  usage " + c.UsagePage.ToString("X4") + "/" + c.Usage.ToString("X4")
                + "  report bytes in/out/feature " + c.InputReportByteLength + "/" + c.OutputReportByteLength + "/" + c.FeatureReportByteLength);
            foreach (int type in new[] { 0, 1 }) {        // 0 input, 1 output
                string name = type == 0 ? "input" : "output";
                ushort n = (ushort)(type == 0 ? c.NumberInputValueCaps : c.NumberOutputValueCaps);
                if (n > 0) {
                    var b = new byte[72 * n]; HidP_GetValueCaps(type, b, ref n, p);
                    for (int i = 0; i < n; i++) { int id = b[72 * i + 2]; sb.AppendLine("  " + name + " value cap " + i + ": report id " + id); if (type == 1 && reportId < 0) reportId = id; }
                }
                n = (ushort)(type == 0 ? c.NumberInputButtonCaps : c.NumberOutputButtonCaps);
                if (n > 0) {
                    var b = new byte[72 * n]; HidP_GetButtonCaps(type, b, ref n, p);
                    for (int i = 0; i < n; i++) { int id = b[72 * i + 2]; sb.AppendLine("  " + name + " button cap " + i + ": report id " + id); if (type == 1 && reportId < 0) reportId = id; }
                }
            }
            HidD_FreePreparsedData(p);
        }
        return sb.ToString().TrimEnd();
    }

    static FileStream fs;
    static System.Threading.Tasks.Task<int> pending;
    static byte[] pendingBuf;
    public static int ReportId;
    public static bool Open(string path) {
        var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);   // read/write, overlapped
        if (h.IsInvalid) return false;
        fs = new FileStream(h, FileAccess.ReadWrite, 65, true);
        return true;
    }
    public static void Close() { if (fs != null) { fs.Dispose(); fs = null; } }

    // Only these commands can leave this script: 0x80/0x83 questions, 0x09 lighting on, 0x05-0x07 colour pages.
    // 0x0A (save to the keyboard) and 0x10 (factory reset / firmware mode) are refused here whatever the caller asks.
    public static string Send(byte cmd, byte index, byte[] data, int len) {
        if (!(cmd == 0x80 || cmd == 0x83 || cmd == 0x09 || cmd == 0x05 || cmd == 0x06 || cmd == 0x07)) return "refused by the script";
        var b = new byte[65];
        b[0] = (byte)ReportId; b[1] = cmd; b[2] = index; b[3] = (byte)(len & 0xFF); b[4] = (byte)(len >> 8);
        if (data != null) Array.Copy(data, 0, b, 5, Math.Min(60, data.Length));
        try { var w = fs.WriteAsync(b, 0, 65); if (!w.Wait(2000)) return "write timed out"; }
        catch (Exception e) { return "write failed: " + (e.InnerException ?? e).Message; }
        var until = DateTime.Now.AddMilliseconds(2000);
        while (DateTime.Now < until) {
            // One read outstanding at a time: a read left over from a timed-out command is waited on again rather
            // than abandoned, or it would take the next command's answer.
            if (pending == null) { pendingBuf = new byte[65]; pending = fs.ReadAsync(pendingBuf, 0, 65); }
            int left = (int)(until - DateTime.Now).TotalMilliseconds;
            try { if (left <= 0 || !pending.Wait(left)) return "no reply"; } catch (Exception e) { pending = null; return "read failed: " + (e.InnerException ?? e).Message; }
            var r = pendingBuf; int n = pending.Result; pending = null;
            if (r[5] == 0xEC && r[6] == 0xBD) continue;                  // a key event, not our answer
            return BitConverter.ToString(r, 0, Math.Min(n, 48)).Replace("-", " ");
        }
        return "no reply";
    }
}
"@

W ("Board:  " + (Get-CimInstance Win32_BaseBoard).Product + "   Model: " + (Get-CimInstance Win32_ComputerSystem).Model + "   BIOS: " + (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)
$paths = [OhmanPk]::Paths() | Where-Object { $_ -match 'vid_0461&pid_(4e9a|4e9b)' }
if (-not $paths) { W "No OMEN 16/17 per-key keyboard (0461:4E9A or 4E9B) found."; Start-Process explorer.exe "/select,`"$out`""; return }
W ""; W "===== The keyboard's own description (nothing sent) ====="
$lamp = $null; $rid = -1
foreach ($p in $paths) {
    $id = -1; $ol = 0
    W (($p -replace '^.*?(vid_[^#]*).*$', '$1'))
    W ([OhmanPk]::Describe($p, [ref]$id, [ref]$ol))
    if ($p -match 'mi_02' -and $ol -gt 0) { $lamp = $p; $rid = $id }
}
if (-not $lamp) { W "No lighting interface (mi_02) with an output report."; Start-Process explorer.exe "/select,`"$out`""; return }
[OhmanPk]::ReportId = [Math]::Max(0, $rid)
W ("using report id " + [OhmanPk]::ReportId)

if (-not [OhmanPk]::Open($lamp)) { W "Could not open the lighting interface for talking (is another RGB program running?)."; Start-Process explorer.exe "/select,`"$out`""; return }
try {
    W ""; W "===== Questions (change nothing) ====="
    W ("device info  80 01: " + [OhmanPk]::Send(0x80, 1, $null, 0))
    W ("key status   80 02: " + [OhmanPk]::Send(0x80, 2, $null, 0))
    W ("effect       83 00: " + [OhmanPk]::Send(0x83, 0, $null, 0))

    function Paint([int[]]$lit, [byte]$r, [byte]$g, [byte]$b) {
        $res = @()
        foreach ($ch in @(@(5, $r), @(6, $g), @(7, $b))) {
            for ($page = 0; $page -lt 3; $page++) {
                $data = New-Object byte[] 60
                for ($i = 0; $i -lt 60; $i++) { $led = $page * 60 + $i; if ($led -lt 168 -and ($lit -eq $null -or $lit -contains $led)) { $data[$i] = $ch[1] } }
                $res += [OhmanPk]::Send([byte]$ch[0], [byte]$page, $data, 0)
            }
        }
        $ok = @($res | Where-Object { $_ -match '^([0-9A-F]{2} ){5}EC AC' }).Count
        "$ok of 9 pages acknowledged" + $(if ($ok -lt 9) { "; first reply: " + $res[0] } else { "" })
    }

    W ""; W "===== Test (nothing is saved to the keyboard) ====="
    W ("lighting on  09 00: " + [OhmanPk]::Send(0x09, 0, [byte[]]@(1), 1))
    W ("all red: " + (Paint $null 255 0 0))
    $a = Read-Host "Is the whole keyboard red now? (y/n)"; W "  whole keyboard red -> $a"
    # 4E9A: 36-38, 140 and 146-148 were settled on 8BAD in #20. Next: the bottom row right of the space bar (AltGr,
    # Menu, '<>' or right Fn, right Ctrl, as OGH draws 101, 107, 163, 164), then keys the old drawing never had.
    $leds = 36, 37, 38, 140, 146, 147, 148
    if ($lamp -match 'pid_4e9a') { $leds = 101, 107, 163, 164, 122, 124, 125, 137, 150, 155, 166 }
    foreach ($led in $leds) {
        W ("  led $led only: " + (Paint @($led) 255 255 255))
        $a = Read-Host "Only one key should be lit white. Which key is it? (type it, or 'none')"; W "  led $led -> $a"
    }
    W ("dark again: " + (Paint @() 0 0 0))
} finally { [OhmanPk]::Close() }

Write-Host ""
Write-Host "Done. Restart the laptop to get your previous lighting back. Drag ohman-perkey.txt from your Desktop into the GitHub issue." -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
