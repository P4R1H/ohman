// Ohman — WPF main window (layout in the embedded Ui.xaml), tray icon, hotkeys, live readouts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WPath = System.Windows.Shapes.Path;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace Ohman {

    static class Ui {
        public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush(Col(hex)); b.Freeze(); return b; }
        public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static SolidColorBrush Brush(Rgb c) { return Brush(Color.FromRgb(c.R, c.G, c.B)); }
        public static Color Col(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static readonly Color EcoColor = Col("#2FB984"), BalColor = Col("#5B8DEF"), PerfColor = Col("#F0603F"), FanColor = Col("#A78BFA");
        public static readonly Color Warn = Col("#FFB84D"), Danger = Col("#FF5C5C"), Ok = Col("#3DDC84");
        public static Color ModeColor(int i) { return i == 0 ? EcoColor : i == 2 ? PerfColor : BalColor; }
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily HeadFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");
        public static readonly Brush Surface = Brush("#161920"), SurfaceHi = Brush("#1E222B"), TextB = Brush("#ECEFF4"), Muted = Brush("#A4ADBF"), Track = Brush("#22262E");
    }

    /// <summary>One segment of the mode bar: icon + name, filled with the mode colour when selected.</summary>
    sealed class ModeSeg : Border {
        public readonly int Index;
        readonly WPath icon; readonly TextBlock txt; readonly Color color; bool sel;
        public event Action<int> Clicked;
        public ModeSeg(int index, string name, Color color, string pathData) {
            Index = index; this.color = color;
            CornerRadius = new CornerRadius(7); Background = Brushes.Transparent; Padding = new Thickness(0, 8, 0, 8); Cursor = Cursors.Hand;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            icon = new WPath { Data = Geometry.Parse(pathData), Stroke = Ui.Brush(color), StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 14, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
            txt = new TextBlock { Text = name, FontFamily = Ui.UiFont, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(icon); sp.Children.Add(txt); Child = sp;
            MouseLeftButtonUp += delegate { var h = Clicked; if (h != null) h(Index); };
            MouseEnter += delegate { if (!sel) { Background = Ui.SurfaceHi; txt.Foreground = Ui.TextB; } };
            MouseLeave += delegate { if (!sel) { Background = Brushes.Transparent; txt.Foreground = Ui.Muted; } };
        }
        public void SetSelected(bool on) {
            sel = on;
            Background = on ? Ui.Brush(color) : (IsMouseOver ? Ui.SurfaceHi : Brushes.Transparent);
            txt.Foreground = on ? Brushes.White : Ui.Muted;
            icon.Stroke = on ? Brushes.White : Ui.Brush(color); icon.Opacity = on ? 1 : 0.75;
        }
    }

    /// <summary>Compact metric tile: big number, unit, label and a thin level bar.</summary>
    sealed class MetricTile : Border {
        readonly TextBlock val, unit; readonly Border fill; readonly Grid barHost; readonly double max; double frac;
        public MetricTile(string label, string unitText, double maximum, bool last) {
            max = maximum; Background = Ui.Surface; CornerRadius = new CornerRadius(9); Padding = new Thickness(12, 9, 12, 10);
            Margin = new Thickness(0, 0, last ? 0 : 8, 0);
            var sp = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            val = new TextBlock { Text = "--", FontFamily = Ui.MonoFont, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.TextB };
            unit = new TextBlock { Text = unitText, FontFamily = Ui.UiFont, FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 4) };
            line.Children.Add(val); line.Children.Add(unit);
            var lbl = new TextBlock { Text = label, FontFamily = Ui.UiFont, FontSize = 11, Foreground = Ui.Muted, Margin = new Thickness(0, 1, 0, 9) };
            barHost = new Grid { Height = 3 };
            barHost.Children.Add(new Border { Background = Ui.Track, CornerRadius = new CornerRadius(1.5) });
            fill = new Border { Background = Ui.Brush(Ui.BalColor), CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            barHost.Children.Add(fill);
            barHost.SizeChanged += delegate { fill.Width = frac * barHost.ActualWidth; };
            sp.Children.Add(line); sp.Children.Add(lbl); sp.Children.Add(barHost); Child = sp;
        }
        public void Set(double v, Color c) {
            if (double.IsNaN(v)) { val.Text = "--"; frac = 0; }
            else { val.Text = v.ToString("0", CultureInfo.InvariantCulture); frac = Math.Max(0, Math.Min(1, v / max)); }
            fill.Background = Ui.Brush(c);
            double target = frac * barHost.ActualWidth;
            fill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>Small on-screen flash (bottom centre) shown when the OMEN key or a hotkey changes something while the panel is hidden.</summary>
    sealed class Osd : Window {
        readonly WPath icon; readonly TextBlock txt, sub; readonly DispatcherTimer hide;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        public Osd() {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = 0; IsHitTestVisible = false;
            var box = new Border { CornerRadius = new CornerRadius(12), Background = Ui.Brush("#F2161920"), BorderBrush = Ui.Brush("#2A2F3A"), BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 20, 12) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            icon = new WPath { StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 18, Height = 18, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt = new TextBlock { FontFamily = Ui.HeadFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.TextB };
            sub = new TextBlock { FontFamily = Ui.UiFont, FontSize = 11.5, Foreground = Ui.Muted, Margin = new Thickness(0, 1, 0, 0) };
            col.Children.Add(txt); col.Children.Add(sub); sp.Children.Add(icon); sp.Children.Add(col); box.Child = sp; Content = box;
            hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            hide.Tick += delegate { hide.Stop(); var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)); a.Completed += delegate { if (Opacity < 0.05) Hide(); }; BeginAnimation(OpacityProperty, a); };
            SourceInitialized += delegate {   // never steal focus (games), never appear in Alt-Tab
                var h = new WindowInteropHelper(this).Handle; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x00000080);
            };
        }
        public void Flash(string title, string detail, Color c, string pathData) {
            txt.Text = title; sub.Text = detail; sub.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            icon.Data = Geometry.Parse(pathData); icon.Stroke = Ui.Brush(c);
            if (!IsVisible) { Opacity = 0; Show(); }
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Bottom - ActualHeight - 72;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            hide.Stop(); hide.Start();
        }
    }

    public sealed class MainWindow : Window {
        Osd osd;
        public void Flash(string title, string detail, int modeIndex) {
            if (osd == null) osd = new Osd();
            string[] paths = { LEAF, SCALE, BOLT };
            osd.Flash(title, detail, modeIndex >= 0 ? Ui.ModeColor(modeIndex) : Ui.FanColor, modeIndex >= 0 ? paths[modeIndex] : FAN);
        }
        const string FAN = "M12 12 C9.6 8.4 8.4 5 10.3 2 C14.6 2.4 15.9 6.6 12 9 M12 12 C15.3 13.2 17.6 16.2 16.6 19.6 C12.5 20.6 9.4 17.6 12 15 M12 12 C11.1 15.4 8 17.8 4.6 16.4 C4 12.2 7.2 9.8 12 9";
        readonly Engine E; readonly Sensors sensors; readonly string screenshotPath; readonly bool openSettings;
        FrameworkElement root;
        readonly ModeSeg[] segs = new ModeSeg[3];
        readonly MetricTile[] tiles = new MetricTile[4];
        TextBlock txtModeSub, txtSensors, txtFanSub, txtFan1, txtFan2, txtPower, txtPowerSub, txtGpuSub, txtKeyInfo, txtFoot, txtKeyFoot, txtDiag, txtErr, txtMachine;
        RadioButton fanAuto, fanMax, fanManual, gpuBase, gpuBoost, gpuMax, keyCycle, keyShow, keyMax, keyOff;
        Slider slFan1, slFan2, slPower, slHue, slLevel;
        // keyboard lighting
        FrameworkElement lightRow, lightDivider, kbdPanel, svBox; Border miniHost, kbdHost; TextBlock txtLightSub, txtKbdKind, txtKbdSel, txtLevel;
        RadioButton kOff, kStatic, kBreathe, kCycle, kWave, kWin; Button btnAllZones, btnKbdClose; TextBox txtHex; Rectangle svHue; Ellipse svMarker; Canvas svCanvas;
        KeyboardView kbdMini, kbdBig; bool kbdOpen; double curH, curS = 1, curV = 1; DispatcherTimer colorDebounce, levelDebounce, speedDebounce, previewTimer; const double KbdWidth = 540;
        FrameworkElement colourBlock, infoBlock, speedRow, levelRow; TextBlock txtKbdInfo, txtSpeed; Slider slSpeed; Button btnWinLighting; double previewPhase;
        ToggleButton tgGpuAuto, tgSuppress, tgHotkeys, tgAutostart, tgEcoBattery, tgSyncPower, tgEcoCool;
        FrameworkElement fanPanel, settingsPanel, mainPanel, demoBadge, errBanner, infoBanner, header; TextBlock txtInfo;
        Border mark; Ellipse dotHb; bool footerMessage;
        Button btnSettings, btnMin, btnClose, btnLearn, btnLog, btnDiag, btnExit;
        WF.NotifyIcon tray; readonly WF.ToolStripMenuItem[] trayModes = new WF.ToolStripMenuItem[3]; WF.ToolStripMenuItem trayMax;
        readonly SD.Icon[] icons = new SD.Icon[3];
        bool syncing, exiting, reading, autostart; int shownMode = -1, lastBiosTemp = -1;
        DispatcherTimer uiTimer, fanDebounce, powerDebounce, learnTimer, toastTimer;
        HwndSource src; bool hotkeysRegistered;

        static readonly string[] ModeSubs = {
            "Windows efficiency mode · GPU base power",
            "Default thermal policy · GPU boost",
            "Performance thermal policy · GPU max"
        };
        const string LEAF = "M4 20 C4 11 10 4 20 4 C20 13 14 20 4 20 Z M4 20 L13 11";
        const string SCALE = "M12 3 L12 21 M8 21 L16 21 M4 7 L20 7 M4 7 L1.5 13 A2.5 2 0 0 0 6.5 13 Z M20 7 L17.5 13 A2.5 2 0 0 0 22.5 13 Z";
        const string BOLT = "M13 2 L4 14 L11 14 L10 22 L20 9 L13 9 Z";

        const int WM_HOTKEY = 0x0312; const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, VK_F11 = 0x7A;   // not F12: Windows reserves it for the debugger
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public MainWindow(Engine engine, Sensors s, string screenshot, bool settingsOpen) {
            E = engine; sensors = s; screenshotPath = screenshot; openSettings = settingsOpen;
            Title = Program.DisplayName; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanMinimize;
            Background = Ui.Brush("#0E1014"); Width = 440; SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
            ShowInTaskbar = true; SnapsToDevicePixels = true; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            root = LoadXaml(); Content = root;
            FindAll(); BuildModeBar(); BuildTiles(); BuildLighting(); BuildIcons(); BuildTray(); Wire(); Position(); SetSettingsIcon(false);
            if (E.Light != null) BuildKeyboardWindow();
            slFan1.Minimum = slFan2.Minimum = E.P.Curve.Floor; slFan1.Maximum = slFan2.Maximum = E.P.Curve.Ceiling;   // bounds belong to the profile
            slPower.Maximum = E.MaxOffset;
            if (!E.P.HasPowerGain) { F<FrameworkElement>("PowerRow").Visibility = Visibility.Collapsed; F<FrameworkElement>("PowerDivider").Visibility = Visibility.Collapsed; }
            if (!E.P.HasGpuPower) { F<FrameworkElement>("GpuRow").Visibility = Visibility.Collapsed; F<FrameworkElement>("GpuDivider").Visibility = Visibility.Collapsed; }
            if (openSettings) ShowSettings(true);

            E.StateChanged += delegate { Dispatcher.BeginInvoke((Action)Refresh); };
            E.Toast += delegate(string m, bool err) { Dispatcher.BeginInvoke((Action)delegate { ShowToast(m, err); }); };
            E.KeyPressed += delegate(KeyAction a) { Dispatcher.BeginInvoke((Action)delegate { OnKey(a); }); };
            E.AnyKeyEvent += delegate(uint id, uint data) { Dispatcher.BeginInvoke((Action)delegate { if (!E.Learning) txtKeyInfo.Text = KeyInfoText(); }); };
            sensors.Updated += delegate(SensorSnapshot snap) { Dispatcher.BeginInvoke((Action)delegate { OnSensors(snap); }); };
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;

            SourceInitialized += OnSourceInit;
            Loaded += OnLoaded;
            Closing += delegate(object o, System.ComponentModel.CancelEventArgs ce) { if (!exiting) { ce.Cancel = true; HideToTray(); } };
            Application.Current.SessionEnding += delegate { ExitApp(); };        // logoff/shutdown: leave cleanly instead of hiding
            StateChanged += delegate { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } };
            IsVisibleChanged += delegate { sensors.SetInterval(IsVisible ? 2000 : 15000); if (IsVisible) ReadHardwareAsync(); };
            LocationChanged += delegate { if (IsVisible && WindowState == WindowState.Normal && Left > -30000) { E.S.WinX = (int)Left; E.S.WinY = (int)Top; } };

            uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            uiTimer.Tick += delegate { if (IsVisible) ReadHardwareAsync(); UpdateFooter(); };
            uiTimer.Start();
            Refresh();
            if (!E.BiosOk && !E.Hw.IsDemo) ShowToast("BIOS interface unavailable: " + E.LastError, true);
            else if (E.LastError.Length > 0) ShowToast(E.LastError, true);      // a write already failed during Init, before this handler existed
            QueryAutostartAsync();
            StartShowListener();
        }

        // ---------- construction ----------
        static FrameworkElement LoadXaml() {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ohman.Ui.xaml")) {
                if (st == null) throw new InvalidOperationException("embedded Ui.xaml missing");
                return (FrameworkElement)XamlReader.Load(st);
            }
        }
        T F<T>(string name) where T : class {
            var o = root.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("XAML element missing: " + name);
            return o;
        }
        void FindAll() {
            header = F<FrameworkElement>("Header"); mark = F<Border>("Mark");
            demoBadge = F<FrameworkElement>("DemoBadge"); errBanner = F<FrameworkElement>("ErrBanner"); txtErr = F<TextBlock>("TxtErr"); infoBanner = F<FrameworkElement>("InfoBanner"); txtInfo = F<TextBlock>("TxtInfo");
            btnSettings = F<Button>("BtnSettings"); btnMin = F<Button>("BtnMin"); btnClose = F<Button>("BtnClose");
            mainPanel = F<FrameworkElement>("MainPanel"); txtModeSub = F<TextBlock>("TxtModeSub"); txtSensors = F<TextBlock>("TxtSensors");
            txtFanSub = F<TextBlock>("TxtFanSub"); fanAuto = F<RadioButton>("FanAuto"); fanMax = F<RadioButton>("FanMax"); fanManual = F<RadioButton>("FanManual");
            fanPanel = F<FrameworkElement>("FanPanel"); slFan1 = F<Slider>("SlFan1"); slFan2 = F<Slider>("SlFan2"); txtFan1 = F<TextBlock>("TxtFan1"); txtFan2 = F<TextBlock>("TxtFan2");
            txtPower = F<TextBlock>("TxtPower"); txtPowerSub = F<TextBlock>("TxtPowerSub"); slPower = F<Slider>("SlPower");
            gpuBase = F<RadioButton>("GpuBase"); gpuBoost = F<RadioButton>("GpuBoost"); gpuMax = F<RadioButton>("GpuMax"); tgGpuAuto = F<ToggleButton>("TgGpuAuto"); txtGpuSub = F<TextBlock>("TxtGpuSub");
            lightRow = F<FrameworkElement>("LightRow"); lightDivider = F<FrameworkElement>("LightDivider"); txtLightSub = F<TextBlock>("TxtLightSub"); miniHost = F<Border>("MiniHost");
            kbdPanel = F<FrameworkElement>("KbdPanel"); kbdHost = F<Border>("KbdHost"); txtKbdKind = F<TextBlock>("TxtKbdKind"); txtKbdSel = F<TextBlock>("TxtKbdSel"); btnKbdClose = F<Button>("BtnKbdClose");
            kOff = F<RadioButton>("KOff"); kStatic = F<RadioButton>("KStatic"); kBreathe = F<RadioButton>("KBreathe"); kCycle = F<RadioButton>("KCycle"); kWave = F<RadioButton>("KWave"); kWin = F<RadioButton>("KWin");
            svBox = F<FrameworkElement>("SvBox"); svHue = F<Rectangle>("SvHue"); svMarker = F<Ellipse>("SvMarker"); svCanvas = F<Canvas>("SvCanvas");
            slHue = F<Slider>("SlHue"); slLevel = F<Slider>("SlLevel"); txtLevel = F<TextBlock>("TxtLevel"); txtHex = F<TextBox>("TxtHex"); btnAllZones = F<Button>("BtnAllZones");
            colourBlock = F<FrameworkElement>("ColourBlock"); infoBlock = F<FrameworkElement>("InfoBlock"); speedRow = F<FrameworkElement>("SpeedRow"); levelRow = F<FrameworkElement>("LevelRow");
            txtKbdInfo = F<TextBlock>("TxtKbdInfo"); txtSpeed = F<TextBlock>("TxtSpeed"); slSpeed = F<Slider>("SlSpeed"); btnWinLighting = F<Button>("BtnWinLighting");
            settingsPanel = F<FrameworkElement>("SettingsPanel"); txtMachine = F<TextBlock>("TxtMachine");
            keyCycle = F<RadioButton>("KeyCycle"); keyShow = F<RadioButton>("KeyShow"); keyMax = F<RadioButton>("KeyMax"); keyOff = F<RadioButton>("KeyOff");
            txtKeyInfo = F<TextBlock>("TxtKeyInfo"); btnLearn = F<Button>("BtnLearn"); tgSuppress = F<ToggleButton>("TgSuppress");
            tgHotkeys = F<ToggleButton>("TgHotkeys"); tgEcoBattery = F<ToggleButton>("TgEcoBattery"); tgEcoCool = F<ToggleButton>("TgEcoCool"); tgSyncPower = F<ToggleButton>("TgSyncPower"); tgAutostart = F<ToggleButton>("TgAutostart");
            btnDiag = F<Button>("BtnDiag"); btnLog = F<Button>("BtnLog"); btnExit = F<Button>("BtnExit"); txtDiag = F<TextBlock>("TxtDiag");
            dotHb = F<Ellipse>("DotHb"); txtFoot = F<TextBlock>("TxtFoot"); txtKeyFoot = F<TextBlock>("TxtKeyFoot");
            F<TextBlock>("TxtTitle").Text = Program.DisplayName; btnExit.Content = "Exit " + Program.DisplayName;
        }


        void BuildModeBar() {
            var grid = F<UniformGrid>("ModeGrid");
            string[] paths = { LEAF, SCALE, BOLT };
            for (int i = 0; i < 3; i++) {
                var seg = new ModeSeg(i, Engine.ModeNames[i], Ui.ModeColor(i), paths[i]);
                seg.Clicked += ApplyModeAsync;
                segs[i] = seg; grid.Children.Add(seg);
            }
        }

        void BuildTiles() {
            var grid = F<UniformGrid>("TileGrid");
            tiles[0] = new MetricTile("CPU", "°C", 100, false);
            tiles[1] = new MetricTile("GPU", "°C", 100, false);
            tiles[2] = new MetricTile("CPU fan", "rpm", 6000, false);   // max-fan mode reads 5900 rpm on this unit
            tiles[3] = new MetricTile("GPU fan", "rpm", 6000, true);
            foreach (var t in tiles) grid.Children.Add(t);
        }

        void BuildIcons() {
            for (int i = 0; i < 3; i++) icons[i] = MakeIcon(Ui.ModeColor(i), 32);     // tray: 32 px, diamond fills the box
            try {
                using (var big = MakeIcon(Ui.BalColor, 256))                              // taskbar / Alt-Tab: 256 px source, scaled by Windows
                    Icon = Imaging.CreateBitmapSourceFromHIcon(big.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            } catch { }
        }

        /// <summary>The diamond-O mark as an icon: a rounded square rotated 45° in the given colour with a smaller diamond hole.</summary>
        public static SD.Icon MakeIcon(Color c, int size) {
            using (var bmp = DrawMark(SD.Color.FromArgb(c.R, c.G, c.B), size)) return SD.Icon.FromHandle(bmp.GetHicon());
        }
        public static SD.Bitmap DrawMark(SD.Color c, int size) {
            var bmp = new SD.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                float mid = size / 2f;
                float side = size * 0.66f;            // rotated 45°, the diagonal spans ~93 % of the box
                float radius = side * 0.24f;
                using (var path = RoundSquare(mid, side, radius, mid)) using (var b = new SD.SolidBrush(c)) g.FillPath(b, path);
                using (var hole = RoundSquare(mid, side * 0.40f, radius * 0.4f, mid)) using (var b = new SD.SolidBrush(SD.Color.FromArgb(0x0E, 0x10, 0x14))) g.FillPath(b, hole);
            }
            return bmp;
        }
        /// <summary>Writes a multi-size .ico (PNG-compressed entries) of the mark; used by build.cmd via "--make-ico app.ico".</summary>
        public static void WriteIco(string path, Color c) {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var blobs = new List<byte[]>();
            foreach (int s in sizes) {
                using (var bmp = DrawMark(SD.Color.FromArgb(c.R, c.G, c.B), s)) {
                    if (s >= 256) { using (var ms = new System.IO.MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); blobs.Add(ms.ToArray()); } }
                    else blobs.Add(Dib32(bmp));     // classic DIB frames for the small sizes: what Explorer and GDI expect
                }
            }
            using (var fs = System.IO.File.Create(path))
            using (var w = new System.IO.BinaryWriter(fs)) {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++) {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(blobs[i].Length); w.Write(offset);
                    offset += blobs[i].Length;
                }
                foreach (var b in blobs) w.Write(b);
            }
        }

        /// <summary>32-bpp bottom-up DIB with an empty AND mask, the classic .ico frame format.</summary>
        static byte[] Dib32(SD.Bitmap bmp) {
            int w = bmp.Width, h = bmp.Height, maskRow = ((w + 31) / 32) * 4;
            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms)) {
                bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32); bw.Write(0);
                bw.Write(w * h * 4 + maskRow * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++) { var p = bmp.GetPixel(x, y); bw.Write(p.B); bw.Write(p.G); bw.Write(p.R); bw.Write(p.A); }
                bw.Write(new byte[maskRow * h]);
                bw.Flush();
                return ms.ToArray();
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundSquare(float center, float side, float radius, float rotateAbout) {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float x = center - side / 2, y = center - side / 2, d = radius * 2;
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + side - d, y, d, d, 270, 90);
            p.AddArc(x + side - d, y + side - d, d, d, 0, 90); p.AddArc(x, y + side - d, d, d, 90, 90); p.CloseFigure();
            using (var m = new System.Drawing.Drawing2D.Matrix()) { m.RotateAt(45f, new SD.PointF(rotateAbout, rotateAbout)); p.Transform(m); }
            return p;
        }

        void BuildTray() {
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Show " + Program.DisplayName, null, delegate { ShowPanel(); });
            menu.Items.Add(new WF.ToolStripSeparator());
            for (int i = 0; i < 3; i++) {
                int idx = i;
                var it = new WF.ToolStripMenuItem(Engine.ModeNames[i]);
                it.Click += delegate { ApplyModeAsync(idx); };
                trayModes[i] = it; menu.Items.Add(it);
            }
            menu.Items.Add(new WF.ToolStripSeparator());
            trayMax = new WF.ToolStripMenuItem("Max fan");
            trayMax.Click += delegate { Bg(delegate { E.ToggleMaxFan(); }); };
            menu.Items.Add(trayMax);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            tray = new WF.NotifyIcon { Icon = icons[1], Text = Program.DisplayName, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += delegate(object o, WF.MouseEventArgs me) { if (me.Button == WF.MouseButtons.Left) TogglePanel(); };
        }

        void Wire() {
            header.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs me) { if (me.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
            btnSettings.Click += delegate { ShowSettings(settingsPanel.Visibility != Visibility.Visible); };
            btnMin.Click += delegate { HideToTray(); };
            btnClose.Click += delegate { HideToTray(); };

            RoutedEventHandler fanChanged = delegate {
                if (syncing) return;
                FanMode m = fanMax.IsChecked == true ? FanMode.Max : fanManual.IsChecked == true ? FanMode.Manual : FanMode.Auto;
                fanPanel.Visibility = m == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                Refit();
                int f1 = (int)slFan1.Value, f2 = (int)slFan2.Value;
                Bg(delegate { E.SetFan(m, f1, f2, true); });
            };
            fanAuto.Checked += fanChanged; fanMax.Checked += fanChanged; fanManual.Checked += fanChanged;

            fanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            fanDebounce.Tick += delegate { fanDebounce.Stop(); int f1 = (int)slFan1.Value, f2 = (int)slFan2.Value; Bg(delegate { E.SetFan(FanMode.Manual, f1, f2, false); }); };
            RoutedPropertyChangedEventHandler<double> fanSlid = delegate {
                txtFan1.Text = FanText((int)slFan1.Value); txtFan2.Text = FanText((int)slFan2.Value);
                if (!syncing && fanManual.IsChecked == true) { fanDebounce.Stop(); fanDebounce.Start(); }
            };
            slFan1.ValueChanged += fanSlid; slFan2.ValueChanged += fanSlid;

            powerDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            powerDebounce.Tick += delegate { powerDebounce.Stop(); int off = (int)slPower.Value; Bg(delegate { E.SetTdpOffset(off, true); }); };
            slPower.ValueChanged += delegate {
                txtPower.Text = "+" + (int)slPower.Value + " W";
                txtPowerSub.Text = "Dynamic Boost · " + (E.BaseTdp + (int)slPower.Value) + " W";
                if (!syncing) { powerDebounce.Stop(); powerDebounce.Start(); }
            };

            RoutedEventHandler gpuChanged = delegate {
                if (syncing) return;
                GpuLevel g = gpuMax.IsChecked == true ? GpuLevel.Max : gpuBoost.IsChecked == true ? GpuLevel.Boost : GpuLevel.Base;
                Bg(delegate { E.SetGpu(g, false, true); });
            };
            gpuBase.Checked += gpuChanged; gpuBoost.Checked += gpuChanged; gpuMax.Checked += gpuChanged;
            RoutedEventHandler gpuAuto = delegate { if (syncing) return; bool a = tgGpuAuto.IsChecked == true; Bg(delegate { E.SetGpu(E.S.Gpu, a, true); }); };
            tgGpuAuto.Checked += gpuAuto; tgGpuAuto.Unchecked += gpuAuto;

            RoutedEventHandler keyChanged = delegate {
                if (syncing) return;
                E.SetKey(keyShow.IsChecked == true ? KeyAction.Show : keyMax.IsChecked == true ? KeyAction.MaxFan : keyOff.IsChecked == true ? KeyAction.Off : KeyAction.Cycle);
            };
            keyCycle.Checked += keyChanged; keyShow.Checked += keyChanged; keyMax.Checked += keyChanged; keyOff.Checked += keyChanged;

            learnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            learnTimer.Tick += delegate { learnTimer.Stop(); E.Learning = false; txtKeyInfo.Text = KeyInfoText(); };
            btnLearn.Click += delegate { E.Learning = true; txtKeyInfo.Text = "Press the key now… (10 s)"; learnTimer.Stop(); learnTimer.Start(); };

            RoutedEventHandler sw = delegate {
                if (syncing) return;
                bool hk = tgHotkeys.IsChecked == true;
                E.S.Hotkeys = hk;
                E.S.EcoOnBattery = tgEcoBattery.IsChecked == true; E.S.SyncWinPower = tgSyncPower.IsChecked == true;
                E.S.Save();
                if (hk) RegisterHotkeys(); else UnregisterHotkeys();
                if (E.S.SyncWinPower) E.SetWinPowerOverlay(E.ModeIndex);
                Refresh();
            };
            foreach (var t in new ToggleButton[] { tgHotkeys, tgEcoBattery, tgSyncPower }) { t.Checked += sw; t.Unchecked += sw; }
            RoutedEventHandler suppress = delegate { if (syncing) return; bool on = tgSuppress.IsChecked == true; Bg(delegate { E.SetOghSuppression(on); }); };
            tgSuppress.Checked += suppress; tgSuppress.Unchecked += suppress;
            RoutedEventHandler ecoCool = delegate { if (syncing) return; bool on = tgEcoCool.IsChecked == true; Bg(delegate { E.SetEcoCool(on); }); };
            tgEcoCool.Checked += ecoCool; tgEcoCool.Unchecked += ecoCool;
            RoutedEventHandler auto = delegate { if (syncing) return; bool on = tgAutostart.IsChecked == true; Bg(delegate { SetAutostart(on); }); };
            tgAutostart.Checked += auto; tgAutostart.Unchecked += auto;

            btnDiag.Click += delegate {
                if (txtDiag.Visibility == Visibility.Visible) { txtDiag.Visibility = Visibility.Collapsed; Refit(); return; }
                txtDiag.Text = "running…"; txtDiag.Visibility = Visibility.Visible; Refit();
                Bg(delegate { string d = E.Diagnostics(); Log.Write(d); Dispatcher.BeginInvoke((Action)delegate { txtDiag.Text = d.TrimEnd(); Refit(); }); });
            };
            btnLog.Click += delegate { try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open log: " + ex.Message, true); } };
            btnExit.Click += delegate { ExitApp(); };
        }

        // ---------- keyboard lighting ----------
        int[] SelectedZones() { var l = new List<int>(kbdBig.Selected); l.Sort(); return l.ToArray(); }
        void BuildLighting() {
            if (E.Light == null) { lightRow.Visibility = Visibility.Collapsed; lightDivider.Visibility = Visibility.Collapsed; return; }
            var layout = KeyboardLayouts.Build(E.Light.Numpad, E.Light.Zones);
            kbdMini = new KeyboardView { Interactive = false }; kbdMini.SetLayout(layout); miniHost.Child = kbdMini;
            kbdMini.KeyClicked += delegate { ShowKeyboard(!kbdOpen); };
            kbdBig = new KeyboardView { Interactive = true }; kbdBig.SetLayout(layout); kbdHost.Child = kbdBig; kbdBig.Selected.Add(0);
            kbdBig.KeyClicked += delegate(KeyDef k) {
                if (k == null) return;
                bool multi = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (multi) { if (!kbdBig.Selected.Remove(k.Zone)) kbdBig.Selected.Add(k.Zone); if (kbdBig.Selected.Count == 0) kbdBig.Selected.Add(k.Zone); }
                else { kbdBig.Selected.Clear(); kbdBig.Selected.Add(k.Zone); }
                SyncPickerFromSelection(); kbdBig.Repaint();
            };
            txtKbdKind.Text = E.Light.Describe + (E.Light.Kind == LightKind.PerKey ? " · per-key editing comes later, zones for now" : "");
            btnKbdClose.Click += delegate { ShowKeyboard(false); };
            btnAllZones.Click += delegate { kbdBig.Selected.Clear(); for (int i = 0; i < E.Light.Zones; i++) kbdBig.Selected.Add(i); kbdBig.Repaint(); SyncPickerFromSelection(); };
            if (E.Light.Zones == 1) btnAllZones.Visibility = Visibility.Collapsed;
            if (!WinLighting.Present && !E.Hw.IsDemo) kWin.Visibility = Visibility.Collapsed;    // no Dynamic Lighting device for this keyboard
            RoutedEventHandler mode = delegate {
                if (syncing) return;
                int m = kWin.IsChecked == true ? 2 : kOff.IsChecked == true ? 0 : 1;
                int fx = kBreathe.IsChecked == true ? 1 : kCycle.IsChecked == true ? 2 : kWave.IsChecked == true ? 3 : 0;
                E.S.Light = m; E.S.LightEffect = fx; ApplyEditorState();
                Bg(delegate { E.SetLight(m, fx, true); });
            };
            foreach (var r in new RadioButton[] { kOff, kStatic, kBreathe, kCycle, kWave, kWin }) r.Checked += mode;
            colorDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            colorDebounce.Tick += delegate { colorDebounce.Stop(); Rgb v = Rgb.FromHsv(curH, curS, curV); int[] z = SelectedZones(); Bg(delegate { E.SetLightColor(z, v); }); };
            slHue.ValueChanged += delegate { if (syncing) return; curH = slHue.Value; svHue.Fill = Ui.Brush(Rgb.FromHue(curH)); PushColor(false); };
            MouseEventHandler svMove = delegate(object o, MouseEventArgs me) {
                if (me.LeftButton != MouseButtonState.Pressed) return;
                var p = me.GetPosition(svBox);
                curS = Math.Max(0, Math.Min(1, p.X / svBox.ActualWidth)); curV = Math.Max(0, Math.Min(1, 1 - p.Y / svBox.ActualHeight));
                PlaceMarker(); PushColor(false);
            };
            svBox.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs me) { svBox.CaptureMouse(); svMove(o, me); };
            svBox.MouseMove += svMove;
            svBox.MouseLeftButtonUp += delegate { svBox.ReleaseMouseCapture(); PushColor(true); };
            txtHex.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Enter) ApplyHex(); };
            txtHex.LostFocus += delegate { ApplyHex(); };
            levelDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            levelDebounce.Tick += delegate { levelDebounce.Stop(); int lv = (int)slLevel.Value; Bg(delegate { E.SetLightLevel(lv); }); };
            slLevel.ValueChanged += delegate { txtLevel.Text = (int)slLevel.Value + " %"; if (!syncing) { levelDebounce.Stop(); levelDebounce.Start(); } };
            speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            speedDebounce.Tick += delegate { speedDebounce.Stop(); int sp = (int)slSpeed.Value; Bg(delegate { E.SetLightSpeed(sp); }); };
            slSpeed.ValueChanged += delegate { txtSpeed.Text = SpeedText((int)slSpeed.Value); if (!syncing) { speedDebounce.Stop(); speedDebounce.Start(); } };
            btnWinLighting.Click += delegate { try { Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open Windows settings: " + ex.Message, true); } };
            svBox.SizeChanged += delegate { PlaceMarker(); };
            // live preview of an effect in both drawings, same maths as the firmware frames
            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            previewTimer.Tick += delegate {
                var S = E.S; if (S.Light != 1 || S.LightEffect == 0 || E.LightColors.Length == 0) { previewTimer.Stop(); return; }
                previewPhase += 0.12 * Engine.SpeedFactor(S.LightSpeed);
                var frame = new Rgb[E.LightColors.Length]; double lv = Math.Max(0.05, S.LightLevel / 100.0);
                for (int i = 0; i < frame.Length; i++) frame[i] = Engine.EffectFrame(S.LightEffect, previewPhase, E.LightColors, i).Scale(lv);
                kbdMini.ZoneColors = frame; kbdMini.Repaint();
                if (kbdOpen) { kbdBig.ZoneColors = frame; kbdBig.Repaint(); }
            };
            IsVisibleChanged += delegate { if (IsVisible) RefreshLighting(); else previewTimer.Stop(); };
        }
        static string SpeedText(int s) { return new[] { "slowest", "slow", "normal", "fast", "fastest" }[Math.Max(0, Math.Min(4, s - 1))]; }
        /// <summary>Show only what the current mode can use.</summary>
        void ApplyEditorState() {
            var S = E.S; int m = S.Light, fx = S.LightEffect;
            bool lit = m == 1, pick = lit && (fx == 0 || fx == 1), effect = lit && fx != 0;
            colourBlock.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            speedRow.Visibility = effect ? Visibility.Visible : Visibility.Collapsed;
            levelRow.Visibility = lit ? Visibility.Visible : Visibility.Collapsed;
            infoBlock.Visibility = pick ? Visibility.Collapsed : Visibility.Visible;
            btnWinLighting.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
            txtKbdInfo.Text = m == 0 ? "The keyboard backlight is off. Fn+F4 or a mode above turns it back on."
                : m == 2 ? "Windows Dynamic Lighting is painting the keyboard; its colours and effects come from Windows settings. Pick a mode above to take it back."
                : fx == 2 ? "Cycle runs every zone through the spectrum together." : "Wave runs the spectrum across the zones, left to right.";
            kbdBig.Selectable = pick; kbdBig.Off = m == 0 || m == 2; kbdMini.Off = m == 0 || m == 2;
            speedRow.Margin = new Thickness(0, pick ? 10 : 14, 0, 0);
            if (effect) { if (!previewTimer.IsEnabled) previewTimer.Start(); } else previewTimer.Stop();
        }
        void ApplyHex() {
            Rgb c; string t = txtHex.Text.Trim().TrimStart('#');
            if (!Rgb.TryParse(t, out c)) { SyncPickerControls(); return; }
            c.ToHsv(out curH, out curS, out curV); SyncPickerControls(); PushColor(true);
        }
        void PlaceMarker() {
            if (svBox.ActualWidth <= 0) return;
            Canvas.SetLeft(svMarker, curS * svBox.ActualWidth - svMarker.Width / 2); Canvas.SetTop(svMarker, (1 - curV) * svBox.ActualHeight - svMarker.Height / 2);
        }
        void SyncPickerControls() {
            syncing = true;
            try { slHue.Value = curH; svHue.Fill = Ui.Brush(Rgb.FromHue(curH)); txtHex.Text = "#" + Rgb.FromHsv(curH, curS, curV).Hex; PlaceMarker(); }
            finally { syncing = false; }
        }
        void SyncPickerFromSelection() {
            int[] z = SelectedZones();
            if (z.Length > 0 && E.LightColors.Length > z[0]) E.LightColors[z[0]].ToHsv(out curH, out curS, out curV);
            SyncPickerControls();
            txtKbdSel.Text = E.Light.Zones == 1 ? "whole keyboard" : z.Length == E.Light.Zones ? "all zones" : "zone " + string.Join(", ", Array.ConvertAll(z, delegate(int i) { return (i + 1).ToString(); })) + " · Ctrl-click adds";
        }
        /// <summary>Paint the selection in the drawings immediately, then send the colour to the firmware (debounced).</summary>
        void PushColor(bool now) {
            Rgb v = Rgb.FromHsv(curH, curS, curV);
            foreach (int z in kbdBig.Selected) if (z < E.LightColors.Length) E.LightColors[z] = v;
            kbdBig.ZoneColors = E.LightColors; kbdBig.Off = false; kbdBig.Repaint();
            kbdMini.ZoneColors = E.LightColors; kbdMini.Off = false; kbdMini.Repaint();
            syncing = true; try { txtHex.Text = "#" + v.Hex; } finally { syncing = false; }
            colorDebounce.Stop(); if (now) { Rgb vv = v; int[] z = SelectedZones(); Bg(delegate { E.SetLightColor(z, vv); }); } else colorDebounce.Start();
        }
        /// <summary>The editor is its own window, docked beside the panel (left when there is room), owned by it so it
        /// follows, hides and minimises with the panel. The panel itself never changes size.</summary>
        Window kbdWin; bool kbdDetached; const double KbdGap = 10;
        void BuildKeyboardWindow() {
            var parent = kbdPanel.Parent as Panel; if (parent != null) parent.Children.Remove(kbdPanel);
            var border = (Border)kbdPanel;
            border.Visibility = Visibility.Visible; border.BorderThickness = new Thickness(0); border.Background = Ui.Brush("#0E1014"); border.CornerRadius = new CornerRadius(8);
            kbdWin = new Window {
                Title = Program.DisplayName + " keyboard", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = Brushes.Transparent, AllowsTransparency = false, Width = 754, SizeToContent = SizeToContent.Height, Content = kbdPanel,
                ShowActivated = true, Icon = Icon
            };
            kbdWin.Background = Ui.Brush("#0E1014");
            kbdWin.SourceInitialized += delegate {
                var h = new WindowInteropHelper(kbdWin).Handle;
                try { int pref = 2; DwmSetWindowAttribute(h, 33, ref pref, 4); int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4); int border2 = 0x002C2823; DwmSetWindowAttribute(h, 34, ref border2, 4); } catch { }
            };
            kbdWin.Closing += delegate(object o, System.ComponentModel.CancelEventArgs ce) { if (!exiting) { ce.Cancel = true; ShowKeyboard(false); } };
            kbdWin.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Escape) ShowKeyboard(false); };
            var head = F<FrameworkElement>("KbdHeader");
            head.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs me) {
                if (me.LeftButton != MouseButtonState.Pressed) return;
                try { kbdWin.DragMove(); kbdDetached = true; } catch { }      // dragged away: stop docking it to the panel
            };
            LocationChanged += delegate { if (kbdOpen && !kbdDetached) PlaceKeyboardWindow(); };
        }
        /// <summary>Docked beside the panel: left of it when the work area allows, else right; tops aligned; clamped to the screen.</summary>
        void PlaceKeyboardWindow() {
            var wa = SystemParameters.WorkArea; double w = kbdWin.ActualWidth > 0 ? kbdWin.ActualWidth : kbdWin.Width, h = kbdWin.ActualHeight;
            double left = Left - w - KbdGap;
            if (left < wa.Left) left = Left + ActualWidth + KbdGap;
            if (left + w > wa.Right) left = Math.Max(wa.Left, wa.Right - w);
            double top = Top; if (h > 0 && top + h > wa.Bottom) top = Math.Max(wa.Top, wa.Bottom - h);
            kbdWin.Left = left; kbdWin.Top = top;
        }
        public void ShowKeyboard(bool on) {
            if (kbdMini == null || kbdWin == null || on == kbdOpen) return;
            kbdOpen = on;
            if (!on) { kbdWin.Hide(); previewTimer.Stop(); RefreshLighting(); Log.Write("keyboard editor closed"); return; }
            kbdDetached = false;
            SyncPickerFromSelection(); ApplyEditorState();
            if (kbdWin.Owner == null) kbdWin.Owner = this;                       // only allowed once the panel has been shown
            kbdWin.Show(); kbdWin.UpdateLayout();
            PlaceKeyboardWindow();
            // a short slide out from behind the panel
            double target = kbdWin.Left, dir = target < Left ? 1 : -1, start = target + dir * 36; DateTime t0 = DateTime.Now;
            if (screenshotPath == null) {
                kbdWin.Left = start;
                EventHandler tick = null;
                tick = delegate {
                    double t = Math.Min(1, (DateTime.Now - t0).TotalMilliseconds / 180.0), e = 1 - Math.Pow(1 - t, 3);
                    kbdWin.Left = start + (target - start) * e;
                    if (t >= 1) { CompositionTarget.Rendering -= tick; kbdWin.Left = target; }
                };
                CompositionTarget.Rendering += tick;
            }
            Log.Write("keyboard editor open");
        }
        void RefreshLighting() {
            if (E.Light == null || kbdMini == null) return;
            var S = E.S;
            kOff.IsChecked = S.Light == 0; kWin.IsChecked = S.Light == 2;
            kStatic.IsChecked = S.Light == 1 && S.LightEffect == 0; kBreathe.IsChecked = S.Light == 1 && S.LightEffect == 1; kCycle.IsChecked = S.Light == 1 && S.LightEffect == 2; kWave.IsChecked = S.Light == 1 && S.LightEffect == 3;
            ApplyEditorState();
            if (!(S.Light == 1 && S.LightEffect != 0)) {                       // static colours; effects repaint from the preview timer
                kbdMini.ZoneColors = E.LightColors; kbdMini.Repaint();
                kbdBig.ZoneColors = E.LightColors; kbdBig.Repaint();
            }
            slLevel.Value = Math.Max(5, S.LightLevel); txtLevel.Text = S.LightLevel + " %";
            slSpeed.Value = S.LightSpeed; txtSpeed.Text = SpeedText(S.LightSpeed);
            string fxName = new[] { "Static", "Breathe", "Cycle", "Wave" }[Math.Max(0, Math.Min(3, S.LightEffect))];
            bool off = S.Light == 0;
            txtLightSub.Text = S.Light == 2 ? "Windows Dynamic Lighting" : S.Light == 0 ? "Off" : E.Light.Describe + " · " + fxName + (S.LightLevel < 100 ? " · " + S.LightLevel + " %" : "");
            if (!kbdOpen) SyncPickerFromSelection();
        }

        // ---------- helpers ----------
        // header icon:        // ---------- helpers ----------
        // header icon: three "tune" sliders for Settings, a chevron for Back
        const string ICON_TUNE = "M4 7 H20 M4 12 H20 M4 17 H20 M9 7 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M15 12 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M10 17 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0";
        const string ICON_BACK = "M14.5 6 L8.5 12 L14.5 18";
        WPath settingsIcon;
        void SetSettingsIcon(bool back) {
            if (settingsIcon == null) {
                settingsIcon = new WPath { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 15, Height = 15, Stretch = Stretch.Uniform, Fill = Ui.Brush("#0E1014") };
                btnSettings.Content = settingsIcon;
            }
            settingsIcon.Data = Geometry.Parse(back ? ICON_BACK : ICON_TUNE);
            settingsIcon.Stroke = back ? Ui.Brush(Ui.BalColor) : Ui.Brush("#A7AFC0");
        }
        void ShowSettings(bool on) {
            settingsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            mainPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SetSettingsIcon(on);
            btnSettings.ToolTip = on ? "Back" : "Settings";
            Refit();
            if (on) {
                txtKeyInfo.Text = KeyInfoText();
                txtMachine.Text = E.Hw.IsDemo && screenshotPath == null ? "simulated hardware" : (E.BiosOk ? "BIOS " + E.FanCount + " fans · policy v" + E.Info.ThermalPolicy : "BIOS unavailable");
            }
        }
        /// <summary>A size-to-content window does not always grow when a panel is swapped in until the next layout pass;
        /// re-arming SizeToContent after the layout has settled makes the frame follow the content immediately.</summary>
        void Refit() {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate {
                try {
                    SizeToContent = SizeToContent.Manual;                         // from now on the height is set explicitly
                    double w = root.ActualWidth > 0 ? root.ActualWidth : Width;
                    root.Measure(new Size(w, double.PositiveInfinity));           // what the content wants with no height limit
                    double wanted = Math.Ceiling(root.DesiredSize.Height);
                    double max = Math.Max(420, SystemParameters.WorkArea.Height - 24);
                    double h = Math.Min(wanted, max);
                    if (Math.Abs(Height - h) >= 1) Height = h;
                    root.InvalidateMeasure();                                      // lay out again under the real constraint
                } catch (Exception ex) { Log.Write("refit: " + ex.Message); }
            });
        }
        static void Bg(Action a) { ThreadPool.QueueUserWorkItem(delegate { try { a(); } catch (Exception ex) { Log.Write("bg: " + ex); } }); }
        static string FanText(int level) { return (Math.Max(18, level) * 100).ToString(CultureInfo.InvariantCulture) + " rpm"; }
        static string KeyActionText(KeyAction a) { return a == KeyAction.Cycle ? "cycles mode" : a == KeyAction.Show ? "opens panel" : a == KeyAction.MaxFan ? "toggles max fan" : "off"; }
        string KeyInfoText() {
            string s = "Event " + E.KeyId + " / " + E.KeyData;
            if (E.LastEventTime != DateTime.MinValue) s += " · last seen " + E.LastEventId + "/" + E.LastEventData + " at " + E.LastEventTime.ToString("HH:mm:ss");
            return s;
        }

        void ApplyModeAsync(int idx) {
            SelectMode(idx);
            Bg(delegate { E.SetMode(idx, true); });
        }
        void SelectMode(int idx) {
            for (int i = 0; i < 3; i++) segs[i].SetSelected(i == idx);
            txtModeSub.Text = ModeSubs[idx];
            mark.Background = Ui.Brush(Ui.ModeColor(idx));
            if (shownMode != idx) { shownMode = idx; try { tray.Icon = icons[idx]; } catch { } }
        }

        public void Refresh() {
            syncing = true;
            try {
                var S = E.S; int mi = E.ModeIndex;
                SelectMode(mi);
                slPower.Value = S.TdpOffset; txtPower.Text = "+" + S.TdpOffset + " W";
                txtPowerSub.Text = "Dynamic Boost · " + E.CurrentTdp + " W";
                fanAuto.IsChecked = S.Fan == FanMode.Auto; fanMax.IsChecked = S.Fan == FanMode.Max; fanManual.IsChecked = S.Fan == FanMode.Manual;
                fanPanel.Visibility = S.Fan == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                slFan1.Value = S.Fan1; slFan2.Value = S.Fan2; txtFan1.Text = FanText(S.Fan1); txtFan2.Text = FanText(S.Fan2);
                txtFanSub.Text = E.GuardActive ? "Thermal guard: max fan until cool" : S.Fan == FanMode.Max ? "Max speed" : S.Fan == FanMode.Manual ? "Manual, held" : (E.AutoLevel1 > 0 ? "Curve · " + (E.AutoLevel1 * 100) + " / " + (E.AutoLevel2 * 100) + " rpm" : "Curve");
                GpuLevel g = E.EffectiveGpu;
                gpuBase.IsChecked = g == GpuLevel.Base; gpuBoost.IsChecked = g == GpuLevel.Boost; gpuMax.IsChecked = g == GpuLevel.Max;
                tgGpuAuto.IsChecked = S.GpuAuto;
                txtGpuSub.Text = S.GpuAuto ? "Follows the mode" : g == GpuLevel.Max ? "Custom TGP + PPAB" : g == GpuLevel.Boost ? "PPAB" : "Base TGP";
                RefreshLighting();
                keyCycle.IsChecked = S.Key == KeyAction.Cycle; keyShow.IsChecked = S.Key == KeyAction.Show; keyMax.IsChecked = S.Key == KeyAction.MaxFan; keyOff.IsChecked = S.Key == KeyAction.Off;
                if (!E.Learning) txtKeyInfo.Text = KeyInfoText();
                tgSuppress.IsChecked = S.SuppressOgh; tgHotkeys.IsChecked = S.Hotkeys; tgEcoBattery.IsChecked = S.EcoOnBattery; tgSyncPower.IsChecked = S.SyncWinPower; tgAutostart.IsChecked = autostart; tgEcoCool.IsChecked = S.EcoCool;
                txtKeyFoot.Text = "Fn+F12 " + KeyActionText(S.Key) + (S.Hotkeys && S.Key != KeyAction.Cycle ? " · Shift+F11 cycles" : "");
                demoBadge.Visibility = E.Hw.IsDemo && screenshotPath == null ? Visibility.Visible : Visibility.Collapsed;
                bool err = (!E.BiosOk || E.ReadOnly) && !E.Hw.IsDemo;
                errBanner.Visibility = err ? Visibility.Visible : Visibility.Collapsed;
                if (err) txtErr.Text = !E.BiosOk ? "BIOS interface unavailable: " + E.LastError
                    : "Unsupported laptop (board " + E.Board + "). Read-only: nothing is written to the firmware. Run tools\\support-info.cmd and open a GitHub issue to add it.";
                infoBanner.Visibility = E.Generic && !E.Hw.IsDemo ? Visibility.Visible : Visibility.Collapsed;
                if (E.Generic) txtInfo.Text = "Unverified model (board " + E.Board + "): using the generic OMEN commands for its firmware generation. If it behaves, say so in a GitHub issue so it can be marked verified.";
                for (int i = 0; i < 3; i++) trayModes[i].Checked = i == mi;
                trayMax.Checked = S.Fan == FanMode.Max;
                string tip = Program.DisplayName + " · " + E.ModeName + " · " + E.CurrentTdp + " W" + (S.Fan == FanMode.Max ? " · max fan" : "");
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                UpdateFooter();
                bool bannerNow = errBanner.Visibility == Visibility.Visible || infoBanner.Visibility == Visibility.Visible, fanNow = fanPanel.Visibility == Visibility.Visible;
                if (bannerNow != lastBanner || fanNow != lastFanPanel) { lastBanner = bannerNow; lastFanPanel = fanNow; Refit(); }
            } finally { syncing = false; }
        }
        bool lastBanner, lastFanPanel;

        void UpdateFooter() {
            if (footerMessage) return;
            txtFoot.Foreground = Ui.Muted;
            if (E.Hw.IsDemo && screenshotPath == null) { dotHb.Fill = Ui.Brush(Ui.Warn); txtFoot.Text = "Demo · run as administrator for real control"; return; }
            if (!E.BiosOk) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Text = "BIOS unavailable"; return; }
            if (E.GuardActive) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Foreground = Ui.Brush(Ui.Danger); txtFoot.Text = "Thermal guard: max fan until cool · chassis " + E.GuardChassis + "°"; return; }
            double age = (DateTime.Now - E.LastHeartbeat).TotalSeconds;
            bool fresh = E.LastHeartbeat != DateTime.MinValue && age < E.S.HeartbeatSec * 2.5;
            dotHb.Fill = Ui.Brush(fresh ? Ui.Ok : Ui.Warn);
            txtFoot.Text = "BIOS OK · " + E.FanCount + " fans" + (lastBiosTemp >= 0 ? " · chassis " + lastBiosTemp + "°" : "");
            txtFoot.ToolTip = fresh ? "Firmware settings refreshed " + E.LastHeartbeat.ToString("HH:mm:ss") : "Firmware refresh overdue";
        }

        bool sensorsSeen;
        void OnSensors(SensorSnapshot s) {
            E.CpuTemp = s.CpuTemp; E.GpuTemp = s.GpuTemp;
            if (!double.IsNaN(s.CpuTemp)) sensorsSeen = true;
            tiles[0].Set(s.CpuTemp, TempColor(s.CpuTemp));
            tiles[1].Set(s.GpuTemp, TempColor(s.GpuTemp));
            var parts = new List<string>();
            if (!double.IsNaN(s.CpuLoad)) parts.Add("CPU " + s.CpuLoad.ToString("0") + "%" + (double.IsNaN(s.CpuMhz) || s.CpuMhz <= 0 ? "" : " · " + (s.CpuMhz / 1000).ToString("0.0") + " GHz"));
            if (!double.IsNaN(s.GpuLoad)) parts.Add("GPU " + s.GpuLoad.ToString("0") + "%" + (double.IsNaN(s.GpuWatts) ? "" : " · " + s.GpuWatts.ToString("0") + " W"));
            if (s.BatteryPercent >= 0 && s.BatteryPercent <= 100) parts.Add((s.OnBattery ? "Battery " : "AC · ") + s.BatteryPercent + "%");
            txtSensors.Text = string.Join("    ", parts.ToArray());
        }
        static Color TempColor(double t) { return double.IsNaN(t) ? Ui.BalColor : t >= 85 ? Ui.Danger : t >= 70 ? Ui.Warn : Ui.BalColor; }

        void ReadHardwareAsync() {
            if (reading || (!E.BiosOk && !E.Hw.IsDemo)) return;
            reading = true;
            ThreadPool.QueueUserWorkItem(delegate {
                int[] f = null; int t = -1;
                try { f = E.Hw.GetFanLevels(); } catch (Exception ex) { Log.Write("read fans: " + ex.Message); }
                try { t = E.Hw.GetTemperature(); } catch { }
                Dispatcher.BeginInvoke((Action)delegate {
                    if (f != null) { tiles[2].Set(f[0] < 0 ? double.NaN : f[0] * 100, Ui.FanColor); tiles[3].Set(f[1] < 0 ? double.NaN : f[1] * 100, Ui.FanColor); }
                    if (t >= 0) lastBiosTemp = t;
                    reading = false;
                });
            });
        }

        void OnKey(KeyAction a) {
            switch (a) {
                case KeyAction.Cycle: CycleWithFlash(); break;
                case KeyAction.Show: TogglePanel(); break;
                case KeyAction.MaxFan: ToggleMaxWithFlash(); break;
            }
        }
        void CycleWithFlash() {
            int next = (E.ModeIndex + 1) % 3;
            Flash(Engine.ModeNames[next] + " mode", ModeSubs[next], next);
            ApplyModeAsync(next);
        }
        void ToggleMaxWithFlash() {
            bool on = E.S.Fan != FanMode.Max;
            Flash(on ? "Max fan" : "Fans auto", on ? "Both fans to full speed" : "Back to the firmware curve", -1);
            Bg(delegate { E.ToggleMaxFan(); });
        }

        void OnPowerMode(object o, Microsoft.Win32.PowerModeChangedEventArgs e) {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) E.OnResume();
            else if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) {
                bool onBattery = false;
                try { onBattery = WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Offline; } catch { }
                Bg(delegate { E.OnPowerSource(onBattery); });
            }
        }

        // ---------- window plumbing ----------
        void OnSourceInit(object o, EventArgs e) {
            var hwnd = new WindowInteropHelper(this).Handle;
            src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(Hook);
            try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, 4); int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, 4); int border = 0x002C2823; DwmSetWindowAttribute(hwnd, 34, ref border, 4); } catch { }
            if (E.S.Hotkeys) RegisterHotkeys();
        }
        void RegisterHotkeys() {
            if (hotkeysRegistered) return;
            var hwnd = new WindowInteropHelper(this).Handle; if (hwnd == IntPtr.Zero) return;
            RegisterHotKey(hwnd, 1, MOD_CONTROL | MOD_ALT, (uint)'E'); RegisterHotKey(hwnd, 2, MOD_CONTROL | MOD_ALT, (uint)'B'); RegisterHotKey(hwnd, 3, MOD_CONTROL | MOD_ALT, (uint)'P');
            RegisterHotKey(hwnd, 4, MOD_CONTROL | MOD_ALT, (uint)'M'); RegisterHotKey(hwnd, 5, MOD_CONTROL | MOD_ALT, (uint)'O');
            if (!RegisterHotKey(hwnd, 6, MOD_SHIFT, VK_F11)) Log.Write("hotkey Shift+F11 not available");   // next to the OMEN key: cycles modes
            hotkeysRegistered = true;
        }
        void UnregisterHotkeys() {
            if (!hotkeysRegistered) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            for (int i = 1; i <= 6; i++) UnregisterHotKey(hwnd, i);
            hotkeysRegistered = false;
        }
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
            if (msg == WM_HOTKEY) {
                int id = wp.ToInt32();
                if (id >= 1 && id <= 3) { Flash(Engine.ModeNames[id - 1] + " mode", ModeSubs[id - 1], id - 1); ApplyModeAsync(id - 1); }
                else if (id == 4) ToggleMaxWithFlash();
                else if (id == 5) TogglePanel();
                else if (id == 6) CycleWithFlash();
                handled = true;
            }
            return IntPtr.Zero;
        }

        void OnLoaded(object o, RoutedEventArgs e) {
            if (Program.FlashTest) Flash("Performance mode", ModeSubs[2], 2);
            if (Program.KeyboardTest) ShowKeyboard(true);
            if (screenshotPath == null) return;
            var started = DateTime.Now;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Program.FlashTest ? 600 : 500) };
            t.Tick += delegate {
                double age = (DateTime.Now - started).TotalMilliseconds;
                if (!Program.FlashTest && !sensorsSeen && age < 9000) return;       // wait for real temperatures unless they never come
                if (!Program.FlashTest && age < 2800) return;                        // let the first layout settle
                t.Stop();
                try {
                    Snapshot(screenshotPath); Log.Write("screenshot saved " + screenshotPath);
                    if (osd != null && osd.IsVisible) { var f = (FrameworkElement)osd.Content; osd.Opacity = 1; SnapshotElement(f, screenshotPath + ".osd.png"); }
                    try { using (var bmp = DrawMark(SD.Color.FromArgb(Ui.BalColor.R, Ui.BalColor.G, Ui.BalColor.B), 256)) bmp.Save(screenshotPath + ".icon.png", System.Drawing.Imaging.ImageFormat.Png); } catch { }
                } catch (Exception ex) { Log.Write("screenshot failed: " + ex); }
                ExitApp();
            };
            t.Start();
        }
        static void SnapshotElement(FrameworkElement el, string path) {
            el.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(el);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }
        void Snapshot(string path) {
            root.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(root);
            var parts = new List<FrameworkElement>(); if (kbdOpen) { kbdPanel.UpdateLayout(); parts.Add(kbdPanel); } parts.Add(root);
            double gap = parts.Count > 1 ? KbdGap : 0, w = 0, h = 0;
            foreach (var p in parts) { w += p.ActualWidth; h = Math.Max(h, p.ActualHeight); }
            w += gap * (parts.Count - 1);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) {
                double x = 0;
                foreach (var p in parts) {
                    var part = new RenderTargetBitmap((int)Math.Ceiling(p.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(p.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    part.Render(p);
                    dc.DrawImage(part, new Rect(x, 0, p.ActualWidth, p.ActualHeight));
                    x += p.ActualWidth + gap;
                }
            }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }

        void Position() {
            var S = E.S;
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;   // monitors left of/above the primary have negative coordinates
            if (S.WinX != -1 && S.WinY != -1 && S.WinX >= vl && S.WinY >= vt && S.WinX < vl + SystemParameters.VirtualScreenWidth - 100 && S.WinY < vt + SystemParameters.VirtualScreenHeight - 100) {
                WindowStartupLocation = WindowStartupLocation.Manual; Left = S.WinX; Top = S.WinY;
            } else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        public void ShowPanel() {
            Show(); WindowState = WindowState.Normal; Activate();
            Topmost = true; Topmost = false;
        }
        void TogglePanel() { if (IsVisible) HideToTray(); else ShowPanel(); }
        void HideToTray() { if (kbdOpen) ShowKeyboard(false); E.S.Save(); Hide(); }
        void StartShowListener() {
            var t = new Thread(delegate() {
                var handles = new List<WaitHandle>();
                if (Program.ShowEvent != null) handles.Add(Program.ShowEvent);
                if (Program.ExitEvent != null) handles.Add(Program.ExitEvent);
                if (handles.Count == 0) return;
                while (!exiting) {
                    try {
                        int i = WaitHandle.WaitAny(handles.ToArray(), 1000);
                        if (i == WaitHandle.WaitTimeout) continue;
                        if (handles[i] == Program.ExitEvent) { Log.Write("exit requested by another instance"); Dispatcher.BeginInvoke((Action)ExitApp); break; }
                        Dispatcher.BeginInvoke((Action)ShowPanel);
                    } catch { break; }
                }
            }) { IsBackground = true, Name = "show-listener" };
            t.Start();
        }
        void ExitApp() {
            if (exiting) return; exiting = true;
            try { E.S.Save(); } catch { }
            try { UnregisterHotkeys(); } catch { }
            try { uiTimer.Stop(); } catch { }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            try { if (osd != null) osd.Close(); } catch { }
            try { if (kbdWin != null) kbdWin.Close(); } catch { }
            try { E.Dispose(); } catch { }
            try { sensors.Dispose(); } catch { }
            Log.Write("exit");
            Application.Current.Shutdown();
        }

        // ---------- transient status: shown in the footer line so nothing is covered ----------
        void ShowToast(string msg, bool err) {
            if (E.GuardActive && !err) return;                                   // the guard line owns the footer while it is active
            footerMessage = true;
            txtFoot.Text = msg;
            txtFoot.Foreground = Ui.Brush(err ? Ui.Danger : Ui.Col("#C9D1E0"));
            if (toastTimer == null) {
                toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(err ? 4000 : 1800) };
                toastTimer.Tick += delegate { toastTimer.Stop(); footerMessage = false; UpdateFooter(); };
            }
            toastTimer.Interval = TimeSpan.FromMilliseconds(err ? 4000 : 1800);
            toastTimer.Stop(); toastTimer.Start();
        }

        // ---------- autostart (scheduled task with highest privileges = no UAC prompt at logon) ----------
        void QueryAutostartAsync() {
            Bg(delegate {
                bool on = RunSchtasks("/Query /TN " + Program.AppName) == 0;
                if (on && !E.Hw.IsDemo) {
                    // tasks made by 1.1 stop the app when the laptop goes on battery; re-register those once
                    string xml = SchtasksOut("/Query /TN " + Program.AppName + " /XML");
                    if (xml.IndexOf("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase) >= 0 || xml.IndexOf("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase) >= 0) {
                        Log.Write("logon task has battery restrictions; re-registering it");
                        SetAutostart(true); on = autostart;
                    }
                }
                if (!on && E.S.FirstRun && !E.Hw.IsDemo) {              // first launch: start with Windows like every vendor app does; the switch turns it off
                    SetAutostart(true); on = autostart;
                    if (on) Dispatcher.BeginInvoke((Action)delegate { ShowToast("Starts with Windows from now on (Settings to change)", false); });
                }
                Dispatcher.BeginInvoke((Action)delegate { autostart = on; syncing = true; tgAutostart.IsChecked = on; syncing = false; });
            });
        }
        void SetAutostart(bool on) {
            if (E.Hw.IsDemo) { Dispatcher.BeginInvoke((Action)delegate { ShowToast("Autostart needs the administrator build", true); }); return; }
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int rc;
            if (on) {
                // registered from XML: a task made with plain "schtasks /Create" stops the app when the laptop goes on battery and refuses to start it on battery
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Program.FileStem + "-task.xml");
                try { System.IO.File.WriteAllText(tmp, TaskXml(exe), System.Text.Encoding.Unicode); } catch (Exception ex) { Log.Write("task xml: " + ex.Message); }
                rc = RunSchtasks("/Create /TN " + Program.AppName + " /XML \"" + tmp + "\" /F");
                try { System.IO.File.Delete(tmp); } catch { }
            } else rc = RunSchtasks("/Delete /TN " + Program.AppName + " /F");
            Log.Write("autostart " + on + " rc=" + rc);
            autostart = on && rc == 0;
            if (!E.S.FirstRun) Dispatcher.BeginInvoke((Action)delegate { ShowToast(rc == 0 ? (on ? "Starts with Windows" : "Autostart removed") : "schtasks failed (" + rc + ")", rc != 0); });
        }
        /// <summary>Logon task for this user: highest privileges (no UAC prompt), starts and keeps running on battery, no time limit.</summary>
        static string TaskXml(string exe) {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>" + Program.AppName + " starts with Windows</Description></RegistrationInfo>\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + sid + "</UserId></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--hidden</Arguments></Exec></Actions>\n" +
                "</Task>\n";
        }
        static string SchtasksOut(string args) {
            try {
                var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return o; }
            } catch (Exception ex) { Log.Write("schtasks: " + ex.Message); return ""; }
        }
        static int RunSchtasks(string args) {
            try {
                using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) { p.WaitForExit(5000); return p.ExitCode; }
            } catch (Exception ex) { Log.Write("schtasks: " + ex.Message); return -1; }
        }
    }
}

