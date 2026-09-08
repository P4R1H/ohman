// OmenLite - lightweight, polished OMEN Gaming Hub replacement for HP OMEN Transcend 14.
// Reverse-engineered WMI/BIOS interface (root\wmi hpqBIntM, Command 0x20008, Sign "SECU").
// Custom GDI+ owner-drawn UI. Build with in-box .NET Framework csc.exe (no SDK).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OmenLite {

    static class Palette {
        public static readonly Color Bg        = Color.FromArgb(0x0E, 0x0F, 0x13);
        public static readonly Color Surface   = Color.FromArgb(0x17, 0x19, 0x1F);
        public static readonly Color SurfaceHi  = Color.FromArgb(0x1E, 0x20, 0x28);
        public static readonly Color Border     = Color.FromArgb(0x2A, 0x2C, 0x34);
        public static readonly Color Text       = Color.FromArgb(0xF3, 0xF4, 0xF6);
        public static readonly Color TextMuted  = Color.FromArgb(0x82, 0x86, 0x90);
        public static readonly Color TextFaint  = Color.FromArgb(0x5A, 0x5E, 0x68);
        public static readonly Color Accent     = Color.FromArgb(0x7C, 0x5C, 0xFF);
        public static readonly Color AccentHi   = Color.FromArgb(0x9E, 0x86, 0xFF);
        public static readonly Color Success    = Color.FromArgb(0x3D, 0xDC, 0x84);
        public static readonly Color Warning    = Color.FromArgb(0xFF, 0xB8, 0x4D);
        public static readonly Color Danger     = Color.FromArgb(0xFF, 0x5C, 0x5C);
    }

    static class Draw {
        public static readonly StringFormat Tight = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        public static readonly StringFormat Center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        public static readonly StringFormat Right = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };

        public static GraphicsPath Round(Rectangle r, int radius) {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width; if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void Fill(Graphics g, Rectangle r, int radius, Color c) { using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
        public static void Stroke(Graphics g, Rectangle r, int radius, Color c, float w) { using (var p = Round(r, radius)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p); }
        public static float Measure(Graphics g, string t, Font f) { return g.MeasureString(t, f, PointF.Empty, Tight).Width; }
        // draw a text run and advance x by tight width + gap (fixes trailing-space collapse)
        public static void Run(Graphics g, ref float x, float y, string t, Font f, Color c, float gap) {
            using (var b = new SolidBrush(c)) g.DrawString(t, f, b, x, y, Tight);
            x += Measure(g, t, f) + gap;
        }
        public static void Str(Graphics g, string t, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(t, f, b, x, y, Tight); }
    }

    static class Fonts {
        public static readonly Font Title   = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Label    = new Font("Segoe UI", 8.25f, FontStyle.Bold);   // uppercase section labels
        public static readonly Font Status   = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Body     = new Font("Segoe UI", 10.5f);
        public static readonly Font SegBig    = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font SegSmall  = new Font("Segoe UI Semibold", 9f);
        public static readonly Font Big       = new Font("Consolas", 30f, FontStyle.Bold);
        public static readonly Font Unit      = new Font("Segoe UI", 11f);
        public static readonly Font Temp      = new Font("Consolas", 17f, FontStyle.Bold);
        public static readonly Font Mono      = new Font("Consolas", 8.25f, FontStyle.Bold);
        public static readonly Font Foot      = new Font("Segoe UI", 8.25f);
        public static readonly Font Pill      = new Font("Segoe UI Semibold", 8.5f);
    }

    static class Log {
        static readonly string Path_ = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omenlite.log");
        public static void Write(string s) { try { File.AppendAllText(Path_, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { } }
    }

    // ---- BIOS call wrapper ----
    static class Bios {
        const uint CMD = 0x20008;
        static readonly byte[] SIGN = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
        static readonly object Sync = new object();
        static ManagementObject GetInterface() {
            var s = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM hpqBIntM");
            foreach (ManagementObject mo in s.Get()) return mo;
            throw new Exception("hpqBIntM WMI class not found");
        }
        public static byte[] Call(uint commandType, byte[] data) { return Call(commandType, data, 4); }
        public static byte[] Call(uint commandType, byte[] data, int outSize) {
            if (data == null) data = new byte[0];
            lock (Sync) {
                using (ManagementObject intf = GetInterface()) {
                    var cls = new ManagementClass("root\\wmi", "hpqBDataIn", null);
                    ManagementBaseObject din = cls.CreateInstance();
                    din["Sign"] = SIGN; din["Command"] = CMD; din["CommandType"] = commandType;
                    din["Size"] = (uint)data.Length; din["hpqBData"] = data;
                    string method = "hpqBIOSInt" + outSize;
                    ManagementBaseObject inP = intf.GetMethodParameters(method);
                    inP["InData"] = din;
                    ManagementBaseObject outP = intf.InvokeMethod(method, inP, null);
                    ManagementBaseObject outD = (ManagementBaseObject)outP["OutData"];
                    uint rc = Convert.ToUInt32(outD["rwReturnCode"]);
                    byte[] o = outD["Data"] as byte[];
                    if (rc != 0) throw new Exception("BIOS rc=" + rc + " (0x" + commandType.ToString("X") + ")");
                    return o ?? new byte[0];
                }
            }
        }
        public static int  GetFanCount()            { var d = Call(0x10, new byte[] { 0, 0, 0, 0 }); return d.Length > 0 ? d[0] : -1; }
        public static void SetMode(byte m)          { Call(0x1A, new byte[] { 0xFF, m, 0, 0 }); }
        public static void SetMaxFan(bool on)       { Call(0x27, new byte[] { (byte)(on ? 1 : 0) }); }
        public static void SetConcurrentTdp(byte w) { Call(0x29, new byte[] { 0xFF, 0xFF, 0xFF, w }); }
        public static void SetGpuBoost(bool on)     { Call(0x22, new byte[] { 1, (byte)(on ? 1 : 0), 1, 87 }); }
        public static void SetFanLevel(byte lvl)    { byte[] d = new byte[128]; d[0] = lvl; d[1] = lvl; Call(0x2E, d); }
        public static int  GetFanLevel() { var d = Call(0x2D, new byte[] { 0, 0, 0, 0 }, 128); return d.Length > 0 ? d[0] : -1; }
        public static int  GetTemp()     { var d = Call(0x23, new byte[] { 0, 0, 0, 0 });      return d.Length > 0 ? d[0] : -1; }
        public static bool GetMaxFan()   { var d = Call(0x26, new byte[] { 0, 0, 0, 0 });      return d.Length > 0 && (d[0] & 1) != 0; }
        public static bool GetGpuBoost() { var d = Call(0x21, new byte[] { 0, 0, 0, 0 });      return d.Length > 1 && d[1] != 0; }
    }

    // ================= custom controls =================
    abstract class Painted : Control {
        protected Painted() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Palette.Bg; }
        public virtual void Paint2(Graphics g) { }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; Paint2(e.Graphics); }
    }

    class Seg : Painted {
        public string[] Items = new string[0];
        int sel = 0; bool small;
        public event EventHandler SelectedChanged;
        public Seg(bool compact) { small = compact; Cursor = Cursors.Hand; }
        public int Selected { get { return sel; } set { if (value != sel) { sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } } }
        public void SetSilent(int i) { sel = i; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (Items.Length == 0) return; int s = e.X * Items.Length / Width; if (s < 0) s = 0; if (s >= Items.Length) s = Items.Length - 1; Selected = s; }
        public override void Paint2(Graphics g) {
            g.Clear(BackColor);
            var full = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.Fill(g, full, 11, Palette.Surface); Draw.Stroke(g, full, 11, Palette.Border, 1);
            int n = Items.Length; if (n == 0) return; float w = (float)Width / n;
            if (sel >= 0 && sel < n) {
                var r = Rectangle.Round(new RectangleF(sel * w + 4, 4, w - 8, Height - 8));
                if (!small) for (int i = 3; i >= 1; i--) { var gr = Rectangle.Inflate(r, i * 2, i * 2); Draw.Fill(g, gr, 9 + i, Color.FromArgb(13 * i, Palette.Accent)); }
                Draw.Fill(g, r, 8, Palette.Accent);
            }
            Font f = small ? Fonts.SegSmall : Fonts.SegBig;
            for (int i = 0; i < n; i++) using (var b = new SolidBrush(i == sel ? Color.White : Palette.TextMuted)) g.DrawString(Items[i], f, b, new RectangleF(i * w, 0, w, Height), Draw.Center);
        }
    }

    class Toggle : Painted {
        public string Label = ""; bool on;
        public event EventHandler CheckedChanged;
        public Toggle() { Cursor = Cursors.Hand; }
        public bool Checked { get { return on; } set { if (on != value) { on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } } }
        public void SetSilent(bool v) { on = v; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { Checked = !Checked; }
        public override void Paint2(Graphics g) {
            g.Clear(BackColor);
            using (var b = new SolidBrush(Palette.Text)) g.DrawString(Label, Fonts.Body, b, new RectangleF(2, 0, Width - 50, Height), new StringFormat { LineAlignment = StringAlignment.Center });
            int pw = 42, ph = 24, px = Width - pw, py = (Height - ph) / 2;
            Draw.Fill(g, new Rectangle(px, py, pw, ph), ph / 2, on ? Palette.Accent : Palette.Border);
            int ts = 18, tx = on ? px + pw - ts - 3 : px + 3;
            if (on) using (var gl = new SolidBrush(Color.FromArgb(80, Palette.Accent))) g.FillEllipse(gl, tx - 3, py, ts + 6, ts + 6);
            using (var th = new SolidBrush(on ? Color.White : Palette.TextMuted)) g.FillEllipse(th, tx, py + 3, ts, ts);
        }
    }

    class Pill : Painted {
        public string Caption = "HOLD"; bool on;
        public event EventHandler Toggled;
        public Pill() { Cursor = Cursors.Hand; }
        public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); if (Toggled != null) Toggled(this, EventArgs.Empty); } } }
        public void SetSilent(bool v) { on = v; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { On = !On; }
        public override void Paint2(Graphics g) {
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.Fill(g, r, Height / 2, on ? Palette.Accent : Palette.Surface);
            Draw.Stroke(g, r, Height / 2, on ? Palette.Accent : Palette.Border, 1);
            using (var b = new SolidBrush(on ? Color.White : Palette.TextMuted)) g.DrawString(Caption, Fonts.Pill, b, new RectangleF(0, 0, Width, Height), Draw.Center);
        }
    }

    class Slider : Painted {
        public int Min = 0, Max = 57; int val; bool drag;
        public event EventHandler ValueChanged, ValueCommitted;
        public int Value { get { return val; } set { int v = value; if (v < Min) v = Min; if (v > Max) v = Max; if (v != val) { val = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } } }
        public void SetSilent(int v) { if (v < Min) v = Min; if (v > Max) v = Max; val = v; Invalidate(); }
        void FromX(int x) { float t = (float)(x - 9) / (Width - 18); if (t < 0) t = 0; if (t > 1) t = 1; Value = Min + (int)Math.Round(t * (Max - Min)); }
        protected override void OnMouseDown(MouseEventArgs e) { drag = true; FromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty); }
        public override void Paint2(Graphics g) {
            g.Clear(BackColor);
            int cy = Height / 2, x0 = 9, w = Width - 18;
            float t = (float)(val - Min) / (Max - Min); int tx = x0 + (int)(t * w);
            Draw.Fill(g, new Rectangle(x0, cy - 3, w, 6), 3, Palette.Border);
            Draw.Fill(g, new Rectangle(x0, cy - 3, Math.Max(1, tx - x0), 6), 3, Palette.Accent);
            int ts = 16;
            using (var gl = new SolidBrush(Color.FromArgb(drag ? 100 : 55, Palette.Accent))) g.FillEllipse(gl, tx - ts / 2 - 3, cy - ts / 2 - 3, ts + 6, ts + 6);
            using (var b = new SolidBrush(Palette.Text)) g.FillEllipse(b, tx - ts / 2, cy - ts / 2, ts, ts);
            using (var p = new Pen(Palette.Accent, 2)) g.DrawEllipse(p, tx - ts / 2, cy - ts / 2, ts, ts);
        }
    }

    class Readout : Painted {
        int fan, temp;
        public void Set(int f, int t) { fan = f; temp = t; Invalidate(); }
        public override void Paint2(Graphics g) {
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.Fill(g, r, 14, Palette.Surface); Draw.Stroke(g, r, 14, Palette.Border, 1);
            // fill bar
            float f01 = Math.Max(0.03f, Math.Min(1f, fan / 57f));
            Draw.Fill(g, new Rectangle(16, 14, Width - 32, 4), 2, Palette.SurfaceHi);
            Draw.Fill(g, new Rectangle(16, 14, (int)((Width - 32) * f01), 4), 2, Palette.Accent);
            // left column: caption + big number + /57
            Draw.Str(g, "FAN SPEED", Fonts.Label, Palette.TextMuted, 17, 28);
            string num = fan < 0 ? "--" : fan.ToString();
            Draw.Str(g, num, Fonts.Big, Palette.Text, 15, 44);
            float nw = Draw.Measure(g, num, Fonts.Big);
            Draw.Str(g, "/ 57", Fonts.Unit, Palette.TextFaint, 18 + nw, 66);
            // right column: TEMP caption + value (right aligned)
            var capRect = new RectangleF(0, 28, Width - 18, 14);
            using (var b = new SolidBrush(Palette.TextMuted)) g.DrawString("TEMP", Fonts.Label, b, capRect, Draw.Right);
            Color tc = temp >= 85 ? Palette.Danger : temp >= 70 ? Palette.Warning : Palette.Success;
            string ts = (temp < 0 ? "--" : temp.ToString()) + "°C";
            var tRect = new RectangleF(0, 46, Width - 18, 24);
            using (var b = new SolidBrush(tc)) g.DrawString(ts, Fonts.Temp, b, tRect, Draw.Right);
        }
    }

    // ================= main window =================
    public class MainForm : Form {
        // layout constants
        const int W = 360, H = 430, PAD = 20;

        Seg segMode, segPower;
        Toggle tglMax, tglBoost;
        Slider slider; Pill hold; Readout readout;
        NotifyIcon tray;
        ManagementEventWatcher keyWatcher;
        System.Windows.Forms.Timer heartbeat, fanHold, uiPoll;
        DateTime lastKey = DateTime.MinValue;
        bool reallyExit = false, closeHover = false;

        public byte CurrentMode = 0x30; public int CurrentFan = 0; public bool ManualFan = false;
        public bool MaxFanState = false, BoostState = false; public byte PowerW = 30; int TempC = 0;
        public bool OwnKey = true;
        readonly byte[] cycle = new byte[] { 0x11, 0x30, 0x31 };
        static readonly string StateFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omenlite.state");
        public static EventWaitHandle ShowEvt;

        const int WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        const uint MOD_ALT = 1, MOD_CONTROL = 2;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int msg, int wp, int lp);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();

        public MainForm() {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H); BackColor = Palette.Bg; Text = "OmenLite";
            Region = new Region(Draw.Round(new Rectangle(0, 0, W, H), 14));
            DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            IntPtr h = this.Handle;
            LoadState();
            BuildUi();
            BuildTray();
            SetupKeyWatcher();
            SetupTimers();
            SelfTest();
            Do(() => Bios.SetMode(CurrentMode), "Restored mode " + ModeName(CurrentMode));
            ManualFan = false; ReadStateFromSystem();
            SyncControls();
            if (OwnKey) SuppressOgh();
            SetupShowListener();
            Log.Write("OmenLite started, mode=" + ModeName(CurrentMode));
        }

        public string ModeName(byte m) { return m == 0x11 ? "Eco" : m == 0x30 ? "Balanced" : m == 0x31 ? "Performance" : "0x" + m.ToString("X"); }
        int ModeIndex(byte m) { int i = Array.IndexOf(cycle, m); return i < 0 ? 1 : i; }

        // layout y-anchors (shared with preview)
        public const int Y_STATUS = 44, Y_MODE = 66, H_MODE = 44, Y_CARD = 122, H_CARD = 96,
            Y_FANLBL = 228, Y_SLIDER = 246, H_SLIDER = 30, Y_TOGGLE = 290, H_TOGGLE = 36,
            Y_POWLBL = 336, Y_POWER = 354, H_POWER = 32, Y_FOOT = 402;

        void BuildUi() {
            segMode = new Seg(false) { Items = new[] { "Eco", "Balanced", "Performance" }, Left = PAD, Top = Y_MODE, Width = W - 2 * PAD, Height = H_MODE };
            segMode.SelectedChanged += (s, e) => ApplyMode(cycle[segMode.Selected]);
            readout = new Readout { Left = PAD, Top = Y_CARD, Width = W - 2 * PAD, Height = H_CARD };
            slider = new Slider { Left = PAD, Top = Y_SLIDER, Width = W - 2 * PAD - 76, Height = H_SLIDER, Min = 0, Max = 57 };
            slider.ValueCommitted += (s, e) => SetFan(slider.Value, hold.On);
            hold = new Pill { Caption = "HOLD", Left = W - PAD - 66, Top = Y_SLIDER, Width = 66, Height = H_SLIDER };
            hold.Toggled += (s, e) => SetFan(slider.Value, hold.On);
            int tw = (W - 2 * PAD - 16) / 2;
            tglMax = new Toggle { Label = "Max Fan", Left = PAD, Top = Y_TOGGLE, Width = tw, Height = H_TOGGLE };
            tglMax.CheckedChanged += (s, e) => { MaxFanState = tglMax.Checked; Do(() => Bios.SetMaxFan(MaxFanState), "Max Fan " + (MaxFanState ? "ON" : "OFF")); };
            tglBoost = new Toggle { Label = "GPU Boost", Left = W - PAD - tw, Top = Y_TOGGLE, Width = tw, Height = H_TOGGLE };
            tglBoost.CheckedChanged += (s, e) => { BoostState = tglBoost.Checked; Do(() => Bios.SetGpuBoost(BoostState), "GPU Boost " + (BoostState ? "ON" : "OFF")); };
            segPower = new Seg(true) { Items = new[] { "0 W", "+5 W", "+10 W", "+15 W" }, Left = PAD, Top = Y_POWER, Width = W - 2 * PAD, Height = H_POWER };
            segPower.SelectedChanged += (s, e) => { PowerW = (byte)(30 + segPower.Selected * 5); Do(() => Bios.SetConcurrentTdp(PowerW), "Concurrent TDP " + PowerW + " W"); };
            Controls.AddRange(new Control[] { segMode, readout, slider, hold, tglMax, tglBoost, segPower });
        }

        // ---- chrome painter (static so the preview harness can reuse it) ----
        public static void PaintChrome(Graphics g, int w, int h, string modeName, bool closeHover) {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Palette.Bg);
            Draw.Stroke(g, new Rectangle(0, 0, w - 1, h - 1), 14, Palette.Border, 1);
            // logo mark + title
            Draw.Fill(g, new Rectangle(16, 12, 13, 13), 4, Palette.Accent);
            Draw.Fill(g, new Rectangle(20, 16, 5, 5), 2, Palette.Bg);
            Draw.Str(g, "OmenLite", Fonts.Title, Palette.Text, 37, 9);
            using (var cb = new SolidBrush(closeHover ? Palette.Danger : Palette.TextFaint)) g.FillEllipse(cb, w - 26, 13, 11, 11);
            // gradient hairline
            using (var lg = new LinearGradientBrush(new Rectangle(0, 34, w, 2), Color.FromArgb(170, Palette.Accent), Palette.Bg, LinearGradientMode.Horizontal)) g.FillRectangle(lg, 0, 34, w, 1);
            // status line
            float x = 21;
            Draw.Run(g, ref x, Y_STATUS, "CURRENTLY", Fonts.Status, Palette.TextMuted, 7);
            Draw.Run(g, ref x, Y_STATUS, modeName.ToUpper(), Fonts.Status, Palette.AccentHi, 0);
            // section labels
            Draw.Str(g, "FAN CONTROL", Fonts.Label, Palette.TextMuted, 21, Y_FANLBL);
            Draw.Str(g, "EXTRA CPU POWER", Fonts.Label, Palette.TextMuted, 21, Y_POWLBL);
            // footer
            float fx = 21;
            Draw.Run(g, ref fx, Y_FOOT, "Fn+F12", Fonts.Mono, Palette.TextMuted, 5);
            Draw.Run(g, ref fx, Y_FOOT, "cycles", Fonts.Foot, Palette.TextFaint, 10);
            Draw.Run(g, ref fx, Y_FOOT, "Ctrl+Alt+E/B/P", Fonts.Mono, Palette.TextMuted, 0);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintChrome(e.Graphics, Width, Height, ModeName(CurrentMode), closeHover); }

        protected override void OnMouseDown(MouseEventArgs e) {
            if (e.Y <= 34) {
                if (e.X >= Width - 30 && e.X <= Width - 10 && e.Y >= 10 && e.Y <= 28) { Hide(); return; }
                ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            bool hov = e.X >= Width - 30 && e.X <= Width - 10 && e.Y >= 10 && e.Y <= 28;
            if (hov != closeHover) { closeHover = hov; Invalidate(new Rectangle(Width - 32, 8, 30, 24)); }
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT, (uint)Keys.E);
            RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT, (uint)Keys.B);
            RegisterHotKey(Handle, 3, MOD_CONTROL | MOD_ALT, (uint)Keys.P);
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_HOTKEY) { int id = m.WParam.ToInt32(); if (id >= 1 && id <= 3) segMode.Selected = id - 1; }
            base.WndProc(ref m);
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }

        void Do(Action a, string ok) { try { a(); Toast(ok); Log.Write(ok); } catch (Exception ex) { Toast("Failed: " + ex.Message); Log.Write("ERROR " + ex.Message); } }
        void ApplyMode(byte mode) { Do(() => { Bios.SetMode(mode); CurrentMode = mode; }, "Mode: " + ModeName(mode)); SaveState(); Invalidate(); UpdateTrayText(); }
        void SetFan(int level, bool manual) { Do(() => { Bios.SetFanLevel((byte)level); CurrentFan = level; ManualFan = manual; }, "Fan " + level + (manual ? " (hold)" : "")); SaveState(); }
        void CycleMode() { segMode.Selected = (segMode.Selected + 1) % cycle.Length; }

        void ReadStateFromSystem() {
            try { int f = Bios.GetFanLevel(); if (f >= 0) CurrentFan = f; } catch (Exception ex) { Log.Write("rd fan " + ex.Message); }
            try { MaxFanState = Bios.GetMaxFan(); } catch (Exception ex) { Log.Write("rd max " + ex.Message); }
            try { BoostState = Bios.GetGpuBoost(); } catch (Exception ex) { Log.Write("rd boost " + ex.Message); }
            try { TempC = Bios.GetTemp(); } catch { }
            Log.Write("ReadState fan=" + CurrentFan + " max=" + MaxFanState + " boost=" + BoostState + " temp=" + TempC);
        }
        void SyncControls() {
            segMode.SetSilent(ModeIndex(CurrentMode));
            segPower.SetSilent(Math.Max(0, Math.Min(3, (PowerW - 30) / 5)));
            slider.SetSilent(CurrentFan); hold.SetSilent(ManualFan);
            tglMax.SetSilent(MaxFanState); tglBoost.SetSilent(BoostState);
            readout.Set(CurrentFan, TempC); Invalidate(); UpdateTrayText();
        }

        void SetupKeyWatcher() {
            try { var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"), new WqlEventQuery("SELECT * FROM hpqBEvnt")); w.EventArrived += OnBiosEvent; w.Start(); keyWatcher = w; Log.Write("hpqBEvnt watcher started"); }
            catch (Exception ex) { Log.Write("watcher err " + ex.Message); }
        }
        void OnBiosEvent(object s, EventArrivedEventArgs e) {
            try {
                uint id = Convert.ToUInt32(e.NewEvent["EventID"]), data = Convert.ToUInt32(e.NewEvent["EventData"]);
                Log.Write("hpqBEvnt id=" + id + " data=" + data);
                if (id == 29 && data == 8613) {
                    if ((DateTime.Now - lastKey).TotalMilliseconds < 500) return;
                    lastKey = DateTime.Now;
                    if (OwnKey) SuppressOgh();
                    try { BeginInvoke((Action)CycleMode); } catch { }
                }
            } catch (Exception ex) { Log.Write("evt err " + ex.Message); }
        }
        void SuppressOgh() { try { foreach (var p in Process.GetProcessesByName("OmenCommandCenterBackground")) { try { p.Kill(); Log.Write("killed OmenCommandCenterBackground"); } catch { } } } catch { } }

        void SetupTimers() {
            heartbeat = new System.Windows.Forms.Timer { Interval = 60000 };
            heartbeat.Tick += (s, e) => { try { Bios.SetMode(CurrentMode); if (OwnKey) SuppressOgh(); } catch (Exception ex) { Log.Write("hb " + ex.Message); } };
            heartbeat.Start();
            fanHold = new System.Windows.Forms.Timer { Interval = 1000 };
            fanHold.Tick += (s, e) => { if (ManualFan) { try { Bios.SetFanLevel((byte)CurrentFan); } catch { } } };
            fanHold.Start();
            uiPoll = new System.Windows.Forms.Timer { Interval = 2000 };
            uiPoll.Tick += (s, e) => { if (!Visible) return; try { int f = Bios.GetFanLevel(); TempC = Bios.GetTemp(); if (f >= 0 && !ManualFan) CurrentFan = f; readout.Set(CurrentFan, TempC); } catch { } };
            uiPoll.Start();
        }
        void SelfTest() { try { int fc = Bios.GetFanCount(); Log.Write("SelfTest OK fans=" + fc); } catch (Exception ex) { Toast("BIOS access FAILED: " + ex.Message); Log.Write("SelfTest FAIL " + ex.Message); } }
        void Toast(string m) { try { tray.BalloonTipTitle = "OmenLite"; tray.BalloonTipText = m; tray.ShowBalloonTip(1000); } catch { } }
        void UpdateTrayText() { try { string t = "OmenLite - " + ModeName(CurrentMode); tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; } catch { } }

        void BuildTray() {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show OmenLite", null, (s, e) => ShowWindow());
            var own = new ToolStripMenuItem("Own OMEN key (suppress OGH)") { CheckOnClick = true, Checked = OwnKey };
            own.CheckedChanged += (s, e) => { OwnKey = own.Checked; SaveState(); };
            menu.Items.Add(own);
            var auto = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = AutostartExists() };
            auto.CheckedChanged += (s, e) => SetAutostart(auto.Checked);
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { reallyExit = true; Cleanup(); Application.Exit(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "OmenLite", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowWindow();
            UpdateTrayText();
        }
        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; BringToFront(); Activate(); }
        void SetupShowListener() { var t = new Thread(() => { while (true) { try { if (ShowEvt == null) break; ShowEvt.WaitOne(); BeginInvoke((Action)ShowWindow); } catch { break; } } }); t.IsBackground = true; t.Start(); }

        bool AutostartExists() { try { var p = Process.Start(new ProcessStartInfo("schtasks", "/Query /TN OmenLite") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }); p.WaitForExit(3000); return p.ExitCode == 0; } catch { return false; } }
        void SetAutostart(bool on) {
            try {
                string exe = Application.ExecutablePath;
                string args = on ? "/Create /TN OmenLite /TR \"\\\"" + exe + "\\\"\" /SC ONLOGON /RL HIGHEST /F" : "/Delete /TN OmenLite /F";
                var p = Process.Start(new ProcessStartInfo("schtasks", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                p.WaitForExit(3000); Log.Write("autostart " + on + " exit=" + p.ExitCode);
            } catch (Exception ex) { Log.Write("autostart err " + ex.Message); }
        }

        void LoadState() { try { if (File.Exists(StateFile)) { var p = File.ReadAllText(StateFile).Split('|'); CurrentMode = (byte)int.Parse(p[0], CultureInfo.InvariantCulture); CurrentFan = int.Parse(p[1], CultureInfo.InvariantCulture); ManualFan = p.Length > 2 && p[2] == "1"; if (p.Length > 3) PowerW = (byte)int.Parse(p[3], CultureInfo.InvariantCulture); if (p.Length > 4) OwnKey = p[4] == "1"; } } catch (Exception ex) { Log.Write("load " + ex.Message); } }
        void SaveState() { try { File.WriteAllText(StateFile, ((int)CurrentMode) + "|" + CurrentFan + "|" + (ManualFan ? "1" : "0") + "|" + PowerW + "|" + (OwnKey ? "1" : "0")); } catch { } }

        void Cleanup() {
            try { UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); UnregisterHotKey(Handle, 3); } catch { }
            try { if (keyWatcher != null) keyWatcher.Stop(); } catch { }
            try { if (heartbeat != null) heartbeat.Stop(); if (fanHold != null) fanHold.Stop(); if (uiPoll != null) uiPoll.Stop(); } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
        }

        [STAThread]
        static void Main() {
            bool created;
            using (var mtx = new Mutex(true, "OmenLite_SingleInstance", out created)) {
                if (!created) { try { EventWaitHandle.OpenExisting("OmenLite_ShowPanel").Set(); } catch { } return; }
                ShowEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "OmenLite_ShowPanel");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mtx);
            }
        }
    }
}
