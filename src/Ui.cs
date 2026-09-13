// Ohman — WPF main window (layout in the embedded Ui.xaml): a rail on the left with Home, Fans, Keyboard and
// Settings, one page visible at a time; the window morphs to each page's size. Also the tray icon, hotkeys and the
// live readouts. Every size and colour here comes from the panel design.
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
using System.Windows.Documents;
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


    public sealed class MainWindow : Window {
        enum Page { Home = 0, Fans = 1, Keyboard = 2, Settings = 3 }
        const double RailW = 62, PageW = 398, KbdPageW = 638, SettingsH = 640;

        Osd osd;
        public void Flash(string title, string detail, int modeIndex) {
            if (osd == null) osd = new Osd();
            string[] paths = { LEAF, SCALE, BOLT };
            osd.Flash(title, detail, modeIndex >= 0 ? Ui.ModeColor(modeIndex) : Ui.Accent.Color, modeIndex >= 0 ? paths[modeIndex] : FAN);
        }
        const string FAN = "M12 12 C9.6 8.4 8.4 5 10.3 2 C14.6 2.4 15.9 6.6 12 9 M12 12 C15.3 13.2 17.6 16.2 16.6 19.6 C12.5 20.6 9.4 17.6 12 15 M12 12 C11.1 15.4 8 17.8 4.6 16.4 C4 12.2 7.2 9.8 12 9";
        const string LEAF = "M4 20 C4 11 10 4 20 4 C20 13 14 20 4 20 Z M4 20 L13 11";
        const string SCALE = "M12 3 L12 21 M8 21 L16 21 M4 7 L20 7 M4 7 L1.5 13 A2.5 2 0 0 0 6.5 13 Z M20 7 L17.5 13 A2.5 2 0 0 0 22.5 13 Z";
        const string BOLT = "M13 2 L4 14 L11 14 L10 22 L20 9 L13 9 Z";
        const string ICO_HOME_RING = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 Z";
        const string ICO_HOME_DOT = "M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z";
        const string ICO_FANS = "M3 8.5 C5.5 5.5 8.5 11.5 12 8.5 C15.5 5.5 18.5 11.5 21 8.5 M3 15.5 C5.5 12.5 8.5 18.5 12 15.5 C15.5 12.5 18.5 18.5 21 15.5";
        const string ICO_KBD = "M2.5 7.5 A2 2 0 0 1 4.5 5.5 H19.5 A2 2 0 0 1 21.5 7.5 V16.5 A2 2 0 0 1 19.5 18.5 H4.5 A2 2 0 0 1 2.5 16.5 Z M6 9.5 H6.4 M9.8 9.5 H10.2 M13.6 9.5 H14 M17.4 9.5 H17.8 M6 12.5 H6.4 M9.8 12.5 H10.2 M13.6 12.5 H14 M17.4 12.5 H17.8 M7.5 15.5 H16.5";

        readonly Engine E; readonly Sensors sensors; readonly string screenshotPath; readonly bool openSettings;
        FrameworkElement root; Grid pageHost; ScrollViewer scroll; readonly FrameworkElement[] pages = new FrameworkElement[4]; Page cur = Page.Home; bool pageShown;
        readonly NavBtn[] nav = new NavBtn[4]; Border railPill; TranslateTransform railPillT; Canvas railCanvas; FrameworkElement rail, logoHost; StackPanel navBottom;
        ColorSource accentSrc; SolidColorBrush accent;
        // home
        TextBlock txtHomeTitle, txtHomeStatus, subCpu, subGpu, txtPower, txtFoot, txtFootRight, txtLightSub, txtErr, txtInfo; Run bigCpu, bigGpu, bigFan1, bigFan2;
        FrameworkElement demoBadge, errBanner, infoBanner, powerRow, lightRow; Border miniHost; Ellipse dotHb;
        Seg modeSeg; LinkSeg fanLinks; Slider slPower;
        // fans
        Seg fanSeg, stopAfterSeg; TextBlock txtFansStatus, txtCurveTitle, txtCurveHint, txtFan1, txtFan2, txtFanApplied, txtFanRight, btnFanAction, txtFloor, txtRamp, txtGuardNote;
        TextBlock maxFan1Sub, maxFan2Sub, maxTempSub, maxMinsSub, manSub1, manSub2;
        Run maxFan1, maxFan2, maxTemp, maxMins, maxMinsUnit, manPct1, manPct2;
        FrameworkElement curveBlock, maxBlock, manualBlock, optsAuto, optsCurve, optsMax, optsManual; Border curveWhichHost;
        LinkSeg curveWhich; CurveView curveView; Slider slFan1, slFan2, slFloor, slRamp; ToggleButton tgLink, tgEcoCool2, tgMaxCool, tgManualLink; bool curveGpu;
        // keyboard
        LinkSeg kbdModes; Seg granSeg; Border kbdHost, hexChip; TextBlock txtKbdStatus, txtKeySel, txtSpeed, txtSpeed2, txtLevel, txtLevel2, txtKbdInfo, btnWinLighting;
        FrameworkElement selectRow, colorEditor, effectEditor, kbdInfo, speedInline;
        Slider slSpeed, slSpeed2, slLevel, slLevel2; TextBox txtHex; StripPicker hueBar, shadeBar;
        KeyboardView kbdMini, kbdBig; DispatcherTimer colorDebounce, levelDebounce, speedDebounce, floorDebounce; bool miniNeedsFrame;
        string gran = "Zone"; Rgb curColor; bool hexTyping;
        // settings
        Seg keySeg, gfxSeg, hzSeg, gpuSeg; TextBlock txtMachine, txtKeyInfo, txtGfxSub, txtGpuSub, txtDiag, txtUpdate, btnLearn, btnUpdate, btnDiag, btnLog, btnExit;
        FrameworkElement keyCmdRow, gfxRow, hzRow, lowHzRow, gpuRow; TextBox txtKeyCmd; Ellipse keyDot;
        ToggleButton tgSuppress, tgHotkeys, tgAutostart, tgEcoBattery, tgSyncPower, tgLowHzBattery, tgTrayTemp, tgGuard, tgUpdateAuto;
        TextBlock txtGuardSub, txtMaxCoolSub, txtKeyCmdHint;
        Button btnClose; Border toast; TextBlock txtToast;
        readonly List<WF.ToolStripMenuItem> trayHz = new List<WF.ToolStripMenuItem>();
        WF.NotifyIcon tray; readonly WF.ToolStripMenuItem[] trayModes = new WF.ToolStripMenuItem[3];
        readonly SD.Icon[] icons = new SD.Icon[3]; readonly BitmapSource[] appIcons = new BitmapSource[3];
        bool syncing, exiting, reading, autostart; int shownMode = -1, lastBiosTemp = -1; int[] lastFans;
        readonly List<double> tempTrail = new List<double>();
        DispatcherTimer uiTimer, fanDebounce, powerDebounce, curveDebounce, learnTimer, toastTimer;
        HwndSource src; IntPtr hwnd; bool hotkeysRegistered;
        bool onBattery;                                    // last known power source; PollRate uses it

        static readonly string[] ModeSubs = {
            "Windows efficiency mode · GPU base power",
            "Default thermal policy · GPU boost",
            "Performance thermal policy · GPU max"
        };

        const int WM_HOTKEY = 0x0312; const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, VK_F11 = 0x7A;   // not F12: Windows reserves it for the debugger
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public MainWindow(Engine engine, Sensors s, string screenshot, bool settingsOpen) {
            E = engine; sensors = s; screenshotPath = screenshot; openSettings = settingsOpen;
            Ui.LoadFonts();
            Title = Program.DisplayName; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanMinimize;
            Background = Ui.Card; Width = RailW + PageW; Height = 600; SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = true; SnapsToDevicePixels = true; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            root = LoadXaml(); Content = root;
            root.Resources["UiFont"] = Ui.UiFont; root.Resources["MonoFont"] = Ui.MonoFont;
            // the live accent: a bound brush (unfreezable) behind the XAML's DynamicResource references and the code-built controls
            accentSrc = new ColorSource { Color = Ui.ModeColor(E.ModeIndex) }; accent = accentSrc.MakeBrush();
            root.Resources["Accent"] = accent; Ui.Accent = accent;
            FindAll(); Bounds(); BuildRail(); BuildHome(); BuildFans(); BuildKeyboard(); BuildSettings(); BuildIcons(); BuildTray(); Wire(); Position(); GuardText();
            if (!E.P.HasPowerGain) powerRow.Visibility = Visibility.Collapsed;
            SetAccent(E.ModeIndex, false);
            Navigate(Page.Home, false);
            root.Measure(new Size(Width, double.PositiveInfinity));
            if (pageHost.DesiredSize.Height > 100) Height = Math.Ceiling(pageHost.DesiredSize.Height);    // first frame already at the right size
            // This catches a page getting shorter. It cannot catch one getting taller: pageHost is aligned to the top
            // inside a clipping grid, so its ActualHeight is min(what the page wants, what the window has) and a page
            // that wants more than the window already is reports no change at all. Content that grows calls Remeasure.
            pageHost.SizeChanged += delegate { Morph(true); };

            E.StateChanged += delegate { Dispatcher.BeginInvoke((Action)Refresh); };
            E.Toast += delegate(string m, bool err) { Dispatcher.BeginInvoke((Action)delegate { ShowToast(m, err); }); };
            E.KeyPressed += delegate(KeyAction a) { Dispatcher.BeginInvoke((Action)delegate { OnKey(a); }); };
            E.AnyKeyEvent += delegate(uint id, uint data) { Dispatcher.BeginInvoke((Action)delegate { if (!E.Learning) UpdateKeyStatus(); }); };
            sensors.Updated += delegate(SensorSnapshot snap) { Dispatcher.BeginInvoke((Action)delegate { OnSensors(snap); }); };
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;

            SourceInitialized += OnSourceInit;
            Loaded += OnLoaded;
            Closing += delegate(object o, System.ComponentModel.CancelEventArgs ce) { if (!exiting) { ce.Cancel = true; HideToTray(); } };
            Application.Current.SessionEnding += delegate { ExitApp(); };        // logoff/shutdown: leave cleanly instead of hiding
            StateChanged += delegate { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } };
            IsVisibleChanged += delegate { PollRate(); if (IsVisible) ReadHardwareAsync(); };
            LocationChanged += delegate { if (IsVisible && WindowState == WindowState.Normal && Left > -30000 && !morphing) { E.S.WinX = (int)Left; E.S.WinY = (int)Top; } };
            KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Escape && !(Keyboard.FocusedElement is TextBox)) HideToTray(); };
            MouseEnter += delegate { Ui.Fade(btnClose, 1, 140); };
            MouseLeave += delegate { Ui.Fade(btnClose, 0, 200); };

            // Only drives things you can see, so it runs only while you can see them (PollRate starts and stops it).
            uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            uiTimer.Tick += delegate { ReadHardwareAsync(); UpdateFooter(); if (cur == Page.Fans && E.S.Fan == FanMode.Max) UpdateMaxBlock(); };
            PollRate();
            Refresh();
            if (!E.BiosOk && !E.Hw.IsDemo) ShowToast("BIOS interface unavailable: " + E.LastError, true);
            else if (E.LastError.Length > 0) ShowToast(E.LastError, true);      // a write already failed during Init, before this handler existed
            QueryAutostartAsync();
            if (!E.Hw.IsDemo && E.S.UpdateOnLaunch) Slow(delegate { E.CheckForUpdate(false); });
            sensors.SkipGpu = E.GpuMode == 3;                          // iGPU only: there is nothing to wake
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
            rail = F<FrameworkElement>("Rail"); railCanvas = F<Canvas>("RailCanvas"); railPill = F<Border>("RailPill"); railPillT = (TranslateTransform)railPill.RenderTransform; logoHost = F<FrameworkElement>("LogoHost"); F<Border>("Mark").Background = accentSrc.MakeGradient();
            scroll = F<ScrollViewer>("Scroll"); pageHost = F<Grid>("PageHost");
            pages[0] = F<FrameworkElement>("PageHome"); pages[1] = F<FrameworkElement>("PageFans"); pages[2] = F<FrameworkElement>("PageKbd"); pages[3] = F<FrameworkElement>("PageSettings");
            txtHomeTitle = F<TextBlock>("TxtHomeTitle"); txtHomeStatus = F<TextBlock>("TxtHomeStatus"); demoBadge = F<FrameworkElement>("DemoBadge");
            errBanner = F<FrameworkElement>("ErrBanner"); txtErr = F<TextBlock>("TxtErr"); infoBanner = F<FrameworkElement>("InfoBanner"); txtInfo = F<TextBlock>("TxtInfo");
            bigCpu = F<Run>("BigCpu"); bigGpu = F<Run>("BigGpu"); subCpu = F<TextBlock>("SubCpu"); subGpu = F<TextBlock>("SubGpu"); bigFan1 = F<Run>("BigFan1"); bigFan2 = F<Run>("BigFan2");
            powerRow = F<FrameworkElement>("PowerRow"); slPower = F<Slider>("SlPower"); txtPower = F<TextBlock>("TxtPower");
            lightRow = F<FrameworkElement>("LightRow"); txtLightSub = F<TextBlock>("TxtLightSub"); miniHost = F<Border>("MiniHost");
            dotHb = F<Ellipse>("DotHb"); txtFoot = F<TextBlock>("TxtFoot"); txtFootRight = F<TextBlock>("TxtFootRight");
            txtFansStatus = F<TextBlock>("TxtFansStatus"); curveBlock = F<FrameworkElement>("CurveBlock"); txtCurveTitle = F<TextBlock>("TxtCurveTitle"); curveWhichHost = F<Border>("CurveWhichHost"); txtCurveHint = F<TextBlock>("TxtCurveHint");
            maxBlock = F<FrameworkElement>("MaxBlock"); maxFan1 = F<Run>("MaxFan1"); maxFan1Sub = F<TextBlock>("MaxFan1Sub"); maxFan2 = F<Run>("MaxFan2"); maxFan2Sub = F<TextBlock>("MaxFan2Sub");
            maxTemp = F<Run>("MaxTemp"); maxTempSub = F<TextBlock>("MaxTempSub"); maxMins = F<Run>("MaxMins"); maxMinsUnit = F<Run>("MaxMinsUnit"); maxMinsSub = F<TextBlock>("MaxMinsSub");
            manualBlock = F<FrameworkElement>("ManualBlock"); manPct1 = F<Run>("ManPct1"); manSub1 = F<TextBlock>("ManSub1"); manPct2 = F<Run>("ManPct2"); manSub2 = F<TextBlock>("ManSub2");
            optsAuto = F<FrameworkElement>("OptsAuto"); tgEcoCool2 = F<ToggleButton>("TgEcoCool2");
            optsCurve = F<FrameworkElement>("OptsCurve"); tgLink = F<ToggleButton>("TgLink"); slFloor = F<Slider>("SlFloor"); txtFloor = F<TextBlock>("TxtFloor"); slRamp = F<Slider>("SlRamp"); txtRamp = F<TextBlock>("TxtRamp");
            optsMax = F<FrameworkElement>("OptsMax"); tgMaxCool = F<ToggleButton>("TgMaxCool");
            optsManual = F<FrameworkElement>("OptsManual"); slFan1 = F<Slider>("SlFan1"); slFan2 = F<Slider>("SlFan2"); txtFan1 = F<TextBlock>("TxtFan1"); txtFan2 = F<TextBlock>("TxtFan2");
            tgManualLink = F<ToggleButton>("TgManualLink"); txtGuardNote = F<TextBlock>("TxtGuardNote");
            txtFanApplied = F<TextBlock>("TxtFanApplied"); txtFanRight = F<TextBlock>("TxtFanRight"); btnFanAction = F<TextBlock>("BtnFanAction");
            txtKbdStatus = F<TextBlock>("TxtKbdStatus"); selectRow = F<FrameworkElement>("SelectRow"); txtKeySel = F<TextBlock>("TxtKeySel"); kbdHost = F<Border>("KbdHost");
            colorEditor = F<FrameworkElement>("ColorEditor"); effectEditor = F<FrameworkElement>("EffectEditor"); kbdInfo = F<FrameworkElement>("KbdInfo");
            hexChip = F<Border>("HexChip"); txtHex = F<TextBox>("TxtHex"); slLevel = F<Slider>("SlLevel"); txtLevel = F<TextBlock>("TxtLevel");
            speedInline = F<FrameworkElement>("SpeedInline"); slSpeed2 = F<Slider>("SlSpeed2"); txtSpeed2 = F<TextBlock>("TxtSpeed2");
            slSpeed = F<Slider>("SlSpeed"); txtSpeed = F<TextBlock>("TxtSpeed"); slLevel2 = F<Slider>("SlLevel2"); txtLevel2 = F<TextBlock>("TxtLevel2");
            txtKbdInfo = F<TextBlock>("TxtKbdInfo"); btnWinLighting = F<TextBlock>("BtnWinLighting");
            txtMachine = F<TextBlock>("TxtMachine"); txtKeyInfo = F<TextBlock>("TxtKeyInfo"); keyDot = F<Ellipse>("KeyDot"); btnLearn = F<TextBlock>("BtnLearn"); keyCmdRow = F<FrameworkElement>("KeyCmdRow"); txtKeyCmd = F<TextBox>("TxtKeyCmd");
            gfxRow = F<FrameworkElement>("GfxRow"); txtGfxSub = F<TextBlock>("TxtGfxSub"); hzRow = F<FrameworkElement>("HzRow"); lowHzRow = F<FrameworkElement>("LowHzRow");
            gpuRow = F<FrameworkElement>("GpuRow"); txtGpuSub = F<TextBlock>("TxtGpuSub");
            tgSuppress = F<ToggleButton>("TgSuppress"); tgHotkeys = F<ToggleButton>("TgHotkeys"); tgEcoBattery = F<ToggleButton>("TgEcoBattery"); tgLowHzBattery = F<ToggleButton>("TgLowHzBattery");
            tgSyncPower = F<ToggleButton>("TgSyncPower"); tgTrayTemp = F<ToggleButton>("TgTrayTemp"); tgAutostart = F<ToggleButton>("TgAutostart");
            tgGuard = F<ToggleButton>("TgGuard"); tgUpdateAuto = F<ToggleButton>("TgUpdateAuto");
            txtGuardSub = F<TextBlock>("TxtGuardSub"); txtMaxCoolSub = F<TextBlock>("TxtMaxCoolSub"); txtKeyCmdHint = F<TextBlock>("TxtKeyCmdHint");
            txtUpdate = F<TextBlock>("TxtUpdate"); btnUpdate = F<TextBlock>("BtnUpdate");
            btnDiag = F<TextBlock>("BtnDiag"); btnLog = F<TextBlock>("BtnLog"); btnExit = F<TextBlock>("BtnExit"); txtDiag = F<TextBlock>("TxtDiag"); btnClose = F<Button>("BtnClose");
            toast = F<Border>("Toast"); txtToast = F<TextBlock>("TxtToast");
            btnExit.Text = "Exit " + Program.DisplayName;
            foreach (string n in new[] { "SecKey", "SecPower", "SecDisplay", "SecApp" }) Track(n);
        }
        /// <summary>Swap a placeholder TextBlock for the letter-spaced section label the design uses.</summary>
        void Track(string name) {
            var tb = F<TextBlock>(name); var parent = tb.Parent as Panel; if (parent == null) return;
            int i = parent.Children.IndexOf(tb);
            var t = new Tracked { Text = tb.Text, Margin = tb.Margin, HorizontalAlignment = HorizontalAlignment.Left };
            parent.Children.RemoveAt(i); parent.Children.Insert(i, t);
        }

        /// <summary>Every slider's range comes from the profile, and must be set before anything is wired to it:
        /// changing Maximum coerces Value, which raises ValueChanged, which would look like the user moving it.</summary>
        void Bounds() {
            slFan1.Minimum = slFan2.Minimum = E.P.Curve.Floor; slFan1.Maximum = slFan2.Maximum = E.P.Curve.Ceiling;
            slFloor.Minimum = E.P.Curve.Floor; slFloor.Maximum = E.P.Curve.Floor + (E.P.Curve.Ceiling - E.P.Curve.Floor) * 2 / 3;
            slPower.Maximum = E.MaxOffset;
        }
        /// <summary>DragMove runs its own modal move loop; a morph started inside it would fight USER for the position,
        /// so content changes during a drag just resize at the end.</summary>
        bool dragging;
        void Drag() {
            dragging = true;
            try { DragMove(); } catch { } finally { dragging = false; }
        }
        void BuildRail() {
            var host = F<StackPanel>("NavHost"); var bottom = F<StackPanel>("NavBottom"); navBottom = bottom;
            nav[0] = new NavBtn(0, "Home", new[] { ICO_HOME_RING }, new[] { ICO_HOME_DOT });
            nav[1] = new NavBtn(1, "Fans", new[] { ICO_FANS }, new string[0]);
            nav[2] = new NavBtn(2, "Keyboard", new[] { ICO_KBD }, new string[0]);
            nav[3] = new NavBtn(3, "Settings", new string[0], new[] { GearPath(12, 12, 10, 7.6, 8, 3.4) });
            for (int i = 0; i < 4; i++) { nav[i].HorizontalAlignment = HorizontalAlignment.Center; nav[i].Clicked += delegate(int idx) { Navigate((Page)idx, true); }; }
            host.Children.Add(nav[0]); host.Children.Add(nav[1]); host.Children.Add(nav[2]); bottom.Children.Add(nav[3]);
            if (E.Light == null) nav[2].Visibility = Visibility.Collapsed;
            logoHost.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; Navigate(Page.Home, true); };
            rail.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) Drag(); };
            rail.SizeChanged += delegate { if (!morphing) PlaceRailPill(false); };   // the morph moves the rail every frame; the pill is aimed at the end position instead
        }
        /// <summary>An 8-tooth gear as path data (even-odd fill with the hole).</summary>
        static string GearPath(double cx, double cy, double rOut, double rIn, int teeth, double hole) {
            var sb = new System.Text.StringBuilder(); double step = 2 * Math.PI / teeth;
            for (int i = 0; i < teeth; i++) {
                double a0 = i * step;
                double[] angs = { a0 + 0.06 * step, a0 + 0.44 * step, a0 + 0.56 * step, a0 + 0.94 * step }; double[] rads = { rOut, rOut, rIn, rIn };
                for (int k = 0; k < 4; k++) {
                    double x = cx + rads[k] * Math.Cos(angs[k]), y = cy + rads[k] * Math.Sin(angs[k]);
                    sb.Append(i == 0 && k == 0 ? "M" : "L").Append(F2(x)).Append(' ').Append(F2(y)).Append(' ');
                }
            }
            sb.Append("Z ");
            string h = F2(hole), cys = F2(cy);
            sb.Append("M" + F2(cx - hole) + " " + cys + " A" + h + " " + h + " 0 1 0 " + F2(cx + hole) + " " + cys + " A" + h + " " + h + " 0 1 0 " + F2(cx - hole) + " " + cys + " Z");
            return "F0 " + sb.ToString();
        }
        static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        void PlaceRailPill(bool animate) {
            var b = nav[(int)cur]; if (b.ActualWidth <= 0 || railCanvas.ActualWidth <= 0) return;
            Point p = b.TranslatePoint(new Point(0, 0), railCanvas);
            if (morphing && b.Parent == navBottom) p.Y += (mt[3] - mt[1]) - rail.ActualHeight;   // bottom group: where it will be when the morph lands
            if (railPill.Opacity == 0 || !animate) { railPillT.BeginAnimation(TranslateTransform.XProperty, null); railPillT.BeginAnimation(TranslateTransform.YProperty, null); railPillT.X = p.X; railPillT.Y = p.Y; railPill.Opacity = 1; return; }
            Ui.Glide(railPillT, TranslateTransform.XProperty, p.X, 360, true); Ui.Glide(railPillT, TranslateTransform.YProperty, p.Y, 360, true);
        }

        void BuildHome() {
            modeSeg = new Seg(Engine.ModeNames, ModeSubs, null, Seg.Kind.Page); F<Border>("ModeHost").Child = modeSeg;
            modeSeg.Picked += ApplyModeAsync;
            fanLinks = new LinkSeg(new[] { "Auto", "Max", "Manual" }, 18, 13, 3, new[] { "This model's own curve", "Both fans at full speed", "Your own levels or curve, on the Fans page" });
            F<Border>("FanLinksHost").Child = fanLinks;
            fanLinks.Picked += delegate(int i) {
                if (i == 0) Bg(delegate { E.SetFan(FanMode.Auto, E.S.Fan1, E.S.Fan2, false); });
                else if (i == 1) Bg(delegate { E.SetFan(FanMode.Max, E.S.Fan1, E.S.Fan2, false); });
                else {
                    if (E.S.Fan == FanMode.Auto || E.S.Fan == FanMode.Max) { int f1 = E.S.Fan1, f2 = E.S.Fan2; E.S.Fan = FanMode.Manual; Bg(delegate { E.SetFan(FanMode.Manual, f1, f2, false); }); }
                    Navigate(Page.Fans, true);
                }
            };
            if (E.Light == null) lightRow.Visibility = Visibility.Collapsed;
            else {
                var layout = BuildLayout();
                kbdMini = new KeyboardView { Interactive = false, Gap = 1.5, RowPitch = 9.5 }; kbdMini.SetLayout(layout); miniHost.Child = kbdMini;
                lightRow.MouseLeftButtonUp += delegate { Navigate(Page.Keyboard, true); };
                lightRow.MouseEnter += delegate { miniNeedsFrame = true; };
            }
        }

        void BuildFans() {
            fanSeg = new Seg(Choice.Fan, new[] { "This model's own curve", "Both fans at full speed", "Your own curve, remembered per mode", "Fixed levels, held" }, null, Seg.Kind.Page);
            F<Border>("FanSegHost").Child = fanSeg;
            fanSeg.Picked += delegate(int i) {
                FanMode m = Choice.FanModes[i];
                int f1 = (int)slFan1.Value, f2 = (int)slFan2.Value;
                Bg(delegate { E.SetFan(m, f1, f2, false); });
                E.S.Fan = m; RefreshFans(true);            // the page follows at once; the engine's own Changed arrives after the write
            };
            curveWhich = new LinkSeg(new[] { "CPU", "GPU" }, 12, 13, 3, null); curveWhichHost.Child = curveWhich;
            curveWhich.Picked += delegate(int i) { curveGpu = i == 1; RefreshFans(true); };
            curveView = new CurveView { Floor = E.P.Curve.Floor, Ceiling = E.P.Curve.Ceiling, Temps = Engine.CurveTemps, Levels = (int[])E.S.Cur.CurveLevels.Clone() };
            F<Border>("CurveHost").Child = curveView;
            curveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            curveDebounce.Tick += delegate { curveDebounce.Stop(); int[] lv = (int[])curveView.Levels.Clone(); bool gpu = curveGpu; Bg(delegate { E.SetCurve(lv, gpu); }); };
            curveView.Changed += delegate { curveDebounce.Stop(); curveDebounce.Start(); };
            btnFanAction.MouseLeftButtonUp += delegate {
                if (E.S.Fan == FanMode.Auto) { Bg(delegate { E.SeedCurveFromVendor(); }); E.S.Fan = FanMode.Custom; RefreshFans(true); return; }
                int[] v = E.VendorCurveAt(curveGpu); bool gpu = curveGpu;
                curveView.Levels = (int[])v.Clone(); curveView.Repaint();
                Bg(delegate { E.SetCurve(v, gpu); });
            };
            OnSwitch(tgLink, delegate(bool on) { Bg(delegate { E.SetCurveLinked(on); }); });
            OnSwitch(tgEcoCool2, delegate(bool on) { Bg(delegate { E.SetEcoCool(on); }); });
            OnSwitch(tgMaxCool, delegate(bool on) { Bg(delegate { E.SetMaxBackWhenCool(on); }); });
            OnSwitch(tgManualLink, delegate(bool on) { Bg(delegate { E.SetManualLinked(on); }); });
            stopAfterSeg = new Seg(new[] { "15m", "30m", "60m", "Never" }, null, new object[] { 15, 30, 60, 0 }, Seg.Kind.Row); stopAfterSeg.SetMono(11.5);
            F<Border>("StopAfterHost").Child = stopAfterSeg;
            stopAfterSeg.Picked += delegate(int i) { int m = (int)stopAfterSeg.Tags[i]; Bg(delegate { E.SetMaxStopAfter(m); }); };
            floorDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            floorDebounce.Tick += delegate { floorDebounce.Stop(); int f = (int)slFloor.Value; Bg(delegate { E.SetCurveFloor(f); }); };
            slFloor.ValueChanged += delegate {
                int f = (int)slFloor.Value; txtFloor.Text = f <= E.P.Curve.Floor ? "off" : Pct(f);
                curveView.UserFloor = f <= E.P.Curve.Floor ? 0 : f; curveView.Repaint();
                if (!syncing) { floorDebounce.Stop(); floorDebounce.Start(); }
            };
            slRamp.ValueChanged += delegate { int r = (int)slRamp.Value; txtRamp.Text = r + " s"; if (!syncing) Bg(delegate { E.SetCurveRamp(r); }); };
            fanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            fanDebounce.Tick += delegate { fanDebounce.Stop(); int f1 = (int)slFan1.Value, f2 = (int)slFan2.Value; Bg(delegate { E.SetFan(FanMode.Manual, f1, f2, false); }); };
            RoutedPropertyChangedEventHandler<double> fanSlid = delegate(object o, RoutedPropertyChangedEventArgs<double> ev) {
                bool was = syncing;
                if (!was && E.S.ManualLinked) {
                    syncing = true;
                    try { if (ReferenceEquals(o, slFan1)) slFan2.Value = slFan1.Value; else slFan1.Value = slFan2.Value; } finally { syncing = false; }
                }
                int f1 = (int)slFan1.Value, f2 = (int)slFan2.Value;
                txtFan1.Text = Pct(f1); txtFan2.Text = Pct(f2);
                manPct1.Text = Pct(f1).TrimEnd('%'); manPct2.Text = Pct(f2).TrimEnd('%');
                manSub1.Text = E.Rpm(f1) + " · CPU fan"; manSub2.Text = E.Rpm(f2) + " · GPU fan";
                if (!was && E.S.Fan == FanMode.Manual) { fanDebounce.Stop(); fanDebounce.Start(); }
            };
            slFan1.ValueChanged += fanSlid; slFan2.ValueChanged += fanSlid;
        }

        void BuildSettings() {
            keySeg = new Seg(Choice.Key, new[] { "Next mode", "Open this window", "Toggle max fan", "Start a command of your choice", null }, null, Seg.Kind.Compact);
            F<Border>("KeySegHost").Child = keySeg;
            keySeg.Picked += delegate(int i) {
                KeyAction a = Choice.KeyActions[i];
                E.SetKey(a);
                keyCmdRow.Visibility = a == KeyAction.Run ? Visibility.Visible : Visibility.Collapsed;
            };
            gpuSeg = new Seg(new[] { "Base", "Boost", "Max", "Auto" }, new[] { "Base TGP", "PPAB", "Custom TGP + PPAB", "Follow the mode: Eco → Base, Balanced → Boost, Performance → Max" }, null, Seg.Kind.Row);
            F<Border>("GpuSegHost").Child = gpuSeg;
            gpuSeg.Picked += delegate(int i) {
                if (i == 3) Bg(delegate { E.SetGpu(E.S.Gpu, true, false); });
                else { GpuLevel g = (GpuLevel)i; Bg(delegate { E.SetGpu(g, false, false); }); }
            };
            if (!E.P.HasGpuPower) gpuRow.Visibility = Visibility.Collapsed;
            txtKeyCmd.LostFocus += delegate { E.SetKeyCommand(txtKeyCmd.Text); };
            txtKeyCmd.TextChanged += delegate { txtKeyCmdHint.Visibility = txtKeyCmd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            txtKeyCmd.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Enter) { E.SetKeyCommand(txtKeyCmd.Text); ShowToast("OMEN key runs: " + (E.S.KeyCommand.Length > 0 ? E.S.KeyCommand : "(nothing)"), false); } };
            BuildRefreshRates(); BuildGraphicsModes();
            learnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            learnTimer.Tick += delegate { learnTimer.Stop(); E.Learning = false; UpdateKeyStatus(); };
            btnLearn.MouseLeftButtonUp += delegate { E.Learning = true; keyDot.Fill = Ui.Brush(Ui.Warn); txtKeyInfo.Text = "press the OMEN key now… (10 s)"; learnTimer.Stop(); learnTimer.Start(); };

            OnSwitch(tgHotkeys, delegate(bool on) { Bg(delegate { E.SetHotkeys(on); }); if (on) RegisterHotkeys(); else UnregisterHotkeys(); });
            OnSwitch(tgEcoBattery, delegate(bool on) { Bg(delegate { E.SetEcoOnBattery(on); }); });
            OnSwitch(tgSyncPower, delegate(bool on) { Bg(delegate { E.SetSyncWinPower(on); }); });
            OnSwitch(tgLowHzBattery, delegate(bool on) { Bg(delegate { E.SetLowHzOnBattery(on); }); });
            OnSwitch(tgTrayTemp, delegate(bool on) {
                Bg(delegate { E.SetTrayTemp(on); });
                if (!on) { try { tray.Icon = icons[E.ModeIndex]; } catch { } }
                PollRate();
            });
            OnSwitch(tgSuppress, delegate(bool on) { Bg(delegate { E.SetOghSuppression(on); }); });
            OnSwitch(tgAutostart, delegate(bool on) { Slow(delegate { SetAutostart(on); }); });
            OnSwitch(tgGuard, delegate(bool on) {
                if (!on && !ConfirmGuardOff()) { Synced(delegate { tgGuard.IsChecked = true; }); return; }
                Bg(delegate { E.SetGuard(on); });
            });
            OnSwitch(tgUpdateAuto, delegate(bool on) { Bg(delegate { E.SetUpdateOnLaunch(on); }); });

            btnUpdate.MouseLeftButtonUp += delegate {
                if (E.UpdateAvailable) { try { Process.Start(new ProcessStartInfo(Update.ReleasesUrl) { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open the releases page: " + ex.Message, true); } return; }
                txtUpdate.Text = "checking…"; Slow(delegate { E.CheckForUpdate(true); });
            };
            btnDiag.MouseLeftButtonUp += delegate {
                if (txtDiag.Visibility == Visibility.Visible) { txtDiag.Visibility = Visibility.Collapsed; return; }
                txtDiag.Text = "running…"; txtDiag.Visibility = Visibility.Visible;
                Slow(delegate { string d = E.Diagnostics(); Log.Write(d); Dispatcher.BeginInvoke((Action)delegate { txtDiag.Text = d.TrimEnd(); }); });
            };
            btnLog.MouseLeftButtonUp += delegate { try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open log: " + ex.Message, true); } };
            btnExit.MouseLeftButtonUp += delegate { ExitApp(); };
            // the wheel moves a fixed, small distance and eases there; the default jumps three "lines" of a very tall panel
            scroll.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e) {
                e.Handled = true;
                scrollTo = Math.Max(0, Math.Min(scroll.ScrollableHeight, scrollTo - e.Delta / 120.0 * 54));
                if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollTick; }
            };
            scroll.ScrollChanged += delegate { if (!scrolling) scrollTo = scroll.VerticalOffset; };
        }
        double scrollTo; bool scrolling;
        void ScrollTick(object o, EventArgs e) {
            double at = scroll.VerticalOffset, d = scrollTo - at;
            if (Math.Abs(d) < 0.6) { scroll.ScrollToVerticalOffset(scrollTo); scrolling = false; CompositionTarget.Rendering -= ScrollTick; return; }
            scroll.ScrollToVerticalOffset(at + d * 0.28);
        }

        /// <summary>Every sentence that quotes a guard temperature reads it from the profile, so a new model only
        /// has to change GuardLimits and the words follow.</summary>
        void GuardText() {
            var g = E.P.Guard;
            txtGuardSub.Text = "Forces max fan above " + g.CpuHot + "° CPU or " + g.ChassisHot + "° chassis, and when the fans read stalled";
            txtMaxCoolSub.Text = "Below " + g.MaxFanCoolBelow + "° for " + (g.MaxFanCoolSeconds / 60) + " minutes";
            txtGuardNote.Text = "For your safety, " + Program.DisplayName + " forces max fan above " + g.CpuHot + "°";
        }
        /// <summary>Switching the safety net off is the one thing in here that asks twice, wherever it is switched off from.</summary>
        bool ConfirmGuardOff() {
            return MessageBox.Show(IsVisible ? (Window)this : null,
                "Turn the thermal guard off?\n\nThe guard forces both fans to maximum when the CPU passes " + E.P.Guard.CpuHot + "°, the chassis sensor passes " + E.P.Guard.ChassisHot + "°, or the fans read stalled while the machine is warm. With it off, nothing in " + Program.DisplayName + " will step in.\n\nTurn it off?",
                Program.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        void BuildIcons() {
            for (int i = 0; i < 3; i++) icons[i] = MakeIcon(Ui.ModeColor(i), 32);     // tray: 32 px, the mark fills the box
        }
        void SetAppIcon(int mode) {
            try {
                if (appIcons[mode] == null)
                    using (var big = MakeIcon(Ui.ModeColor(mode), 256))
                        appIcons[mode] = Imaging.CreateBitmapSourceFromHIcon(big.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Icon = appIcons[mode];
            } catch { }
        }

        /// <summary>The mark as an icon: a rounded square rotated 45°, filled, exactly like the one on the rail.</summary>
        public static SD.Icon MakeIcon(Color c, int size) {
            using (var bmp = DrawMark(SD.Color.FromArgb(c.R, c.G, c.B), size)) return SD.Icon.FromHandle(bmp.GetHicon());
        }
        public static SD.Bitmap DrawMark(SD.Color c, int size) { return DrawMark(c, size, null); }
        /// <summary>label != null: the number sits inside the diamond (the tray temperature). A diamond's corners reach
        /// the edge of the box at side = 0.707, so that is as large as the mark can be drawn.</summary>
        public static SD.Bitmap DrawMark(SD.Color c, int size, string label) {
            var bmp = new SD.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                float mid = size / 2f;
                float side = size * 0.74f;
                float radius = side * (label == null ? 0.15f : 0.11f);
                using (var path = RoundSquare(mid, side, radius, mid)) using (var b = new SD.SolidBrush(c)) g.FillPath(b, path);
                if (label == null) return bmp;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float em = size * (label.Length >= 3 ? 0.46f : 0.68f);
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var family = new SD.FontFamily("Segoe UI")) {
                    path.AddString(label, family, (int)SD.FontStyle.Bold, em, new SD.PointF(0, 0), SD.StringFormat.GenericTypographic);
                    var bounds = path.GetBounds();
                    using (var m = new System.Drawing.Drawing2D.Matrix()) { m.Translate(mid - bounds.X - bounds.Width / 2, mid - bounds.Y - bounds.Height / 2); path.Transform(m); }
                    using (var fill = new SD.SolidBrush(SD.Color.FromArgb(0x16, 0x13, 0x11))) g.FillPath(fill, path);
                }
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

        // ---------- the tray menu: everything the panel can do, without opening it ----------
        readonly List<WF.ToolStripMenuItem> trayFan = new List<WF.ToolStripMenuItem>(), trayGpu = new List<WF.ToolStripMenuItem>(),
            trayGfx = new List<WF.ToolStripMenuItem>(), trayLight = new List<WF.ToolStripMenuItem>(), trayBright = new List<WF.ToolStripMenuItem>(),
            trayPower = new List<WF.ToolStripMenuItem>(), traySw = new List<WF.ToolStripMenuItem>();
        WF.ToolStripMenuItem Item(string text, Action click) {
            var it = new WF.ToolStripMenuItem(text);
            if (click != null) it.Click += delegate { click(); };
            return it;
        }
        void BuildTray() {
            var menu = new WF.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            var head = new WF.ToolStripMenuItem(Program.DisplayName + "   " + Program.Version) { Enabled = false };
            menu.Items.Add(head);
            menu.Items.Add(new WF.ToolStripSeparator());
            for (int i = 0; i < 3; i++) {
                int idx = i;
                var it = Item(Engine.ModeNames[i], delegate { ApplyModeAsync(idx); });
                trayModes[i] = it; menu.Items.Add(it);
            }
            menu.Items.Add(new WF.ToolStripSeparator());

            // Fan
            var fanMenu = new WF.ToolStripMenuItem("Fan");
            for (int i = 0; i < Choice.Fan.Length; i++) {
                FanMode m = Choice.FanModes[i];
                var it = Item(Choice.Fan[i], delegate { Bg(delegate { E.SetFan(m, E.S.Fan1, E.S.Fan2, false); }); });
                trayFan.Add(it); fanMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(fanMenu);

            // Power gain: the same 0..max the slider offers, in the steps worth picking from a menu
            if (E.P.HasPowerGain && E.MaxOffset > 0) {
                var powerMenu = new WF.ToolStripMenuItem("Power gain");
                foreach (int w in PowerSteps(E.MaxOffset)) {
                    int watts = w;
                    var it = Item(w == 0 ? "Off" : "+" + w + " W", delegate { Bg(delegate { E.SetTdpOffset(watts, false); }); }); it.Tag = w;
                    trayPower.Add(it); powerMenu.DropDownItems.Add(it);
                }
                menu.Items.Add(powerMenu);
            }

            // Graphics: refresh rate, GPU power, and the BIOS graphics mode
            var gfxMenu = new WF.ToolStripMenuItem("Graphics");
            foreach (int hz in Display.Choices()) {
                int h = hz; var it = Item(hz + " Hz refresh rate", delegate { Bg(delegate { E.SetRefreshRate(h); }); }); it.Tag = hz;
                trayHz.Add(it); gfxMenu.DropDownItems.Add(it);
            }
            if (trayHz.Count >= 2) gfxMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            if (E.P.HasGpuPower) {
                string[] gpuNames = { "Base power", "Extra power", "Extra power with boost", "Follow the mode" };
                for (int i = 0; i < 4; i++) {
                    int idx = i;
                    var it = Item(gpuNames[i], delegate {
                        if (idx == 3) Bg(delegate { E.SetGpu(E.S.Gpu, true, false); });
                        else { GpuLevel gl = (GpuLevel)idx; Bg(delegate { E.SetGpu(gl, false, false); }); }
                    });
                    trayGpu.Add(it); gfxMenu.DropDownItems.Add(it);
                }
            }
            var gfxModes = new List<int>();
            foreach (int mode in new[] { 0, 1, 3 }) if (E.GpuModeOffered(mode)) gfxModes.Add(mode);
            if (gfxModes.Count >= 2) {
                gfxMenu.DropDownItems.Add(new WF.ToolStripSeparator());
                foreach (int mode in gfxModes) {
                    int m = mode;
                    var it = Item(Engine.GpuModeNames[m] + " graphics", delegate { SwitchGraphics(m); }); it.Tag = m;
                    trayGfx.Add(it); gfxMenu.DropDownItems.Add(it);
                }
            }
            if (gfxMenu.DropDownItems.Count > 0) menu.Items.Add(gfxMenu);

            // Keyboard
            if (E.Light != null) {
                var kbMenu = new WF.ToolStripMenuItem("Keyboard");
                for (int i = 0; i < Choice.Light.Length; i++) {
                    int idx = i;
                    var it = Item(Choice.Light[i] + (i == 5 ? " lighting" : ""), delegate {
                        int m = Choice.LightMode(idx), fx = Choice.LightEffect(idx);
                        Bg(delegate { E.SetLight(m, fx, false); });
                    });
                    trayLight.Add(it); kbMenu.DropDownItems.Add(it);
                }
                kbMenu.DropDownItems.Add(new WF.ToolStripSeparator());
                foreach (int pct in new[] { 25, 50, 75, 100 }) {
                    int p = pct;
                    var it = Item("Brightness " + pct + " %", delegate { Bg(delegate { E.SetLightLevel(p); }); }); it.Tag = pct;
                    trayBright.Add(it); kbMenu.DropDownItems.Add(it);
                }
                menu.Items.Add(kbMenu);
            }

            // Settings: the switches, so none of this needs the window open
            var setMenu = new WF.ToolStripMenuItem("Settings");
            AddSwitch(setMenu, "Start with Windows", delegate(bool on) { Slow(delegate { SetAutostart(on); }); }, delegate { return autostart; });
            AddSwitch(setMenu, "Temperature in the tray", delegate(bool on) { E.SetTrayTemp(on); if (!on) { try { tray.Icon = icons[E.ModeIndex]; } catch { } } PollRate(); }, delegate { return E.S.TrayTemp; });
            AddSwitch(setMenu, "Hotkeys", delegate(bool on) { E.SetHotkeys(on); if (on) RegisterHotkeys(); else UnregisterHotkeys(); }, delegate { return E.S.Hotkeys; });
            AddSwitch(setMenu, "Take over the OMEN key", delegate(bool on) { Bg(delegate { E.SetOghSuppression(on); }); }, delegate { return E.S.SuppressOgh; });
            setMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            AddSwitch(setMenu, "Eco on battery", delegate(bool on) { E.SetEcoOnBattery(on); }, delegate { return E.S.EcoOnBattery; });
            AddSwitch(setMenu, "Quieter fan policy for Eco", delegate(bool on) { Bg(delegate { E.SetEcoCool(on); }); }, delegate { return E.S.EcoCool; });
            AddSwitch(setMenu, "Sync Windows power mode", delegate(bool on) { E.SetSyncWinPower(on); }, delegate { return E.S.SyncWinPower; });
            AddSwitch(setMenu, "Thermal guard", delegate(bool on) { if (on || ConfirmGuardOff()) Bg(delegate { E.SetGuard(on); }); }, delegate { return E.S.Guard; });
            if (trayHz.Count >= 2) AddSwitch(setMenu, "Lowest refresh rate on battery", delegate(bool on) { E.SetLowHzOnBattery(on); }, delegate { return E.S.LowHzOnBattery; });
            menu.Items.Add(setMenu);

            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Item("Show " + Program.DisplayName, delegate { ShowPanel(); }));
            menu.Items.Add(Item("Exit", delegate { ExitApp(); }));
            menu.Opening += delegate { RefreshTray(); };
            tray = new WF.NotifyIcon { Icon = icons[1], Text = Program.DisplayName, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += delegate(object o, WF.MouseEventArgs me) { if (me.Button == WF.MouseButtons.Left) TogglePanel(); };
        }
        /// <summary>0, then 5 W steps up to the profile's ceiling, with the ceiling itself always last.</summary>
        static int[] PowerSteps(int max) {
            var steps = new List<int>();
            for (int w = 0; w < max; w += 5) steps.Add(w);
            steps.Add(max);
            return steps.ToArray();
        }
        readonly List<Func<bool>> swState = new List<Func<bool>>();
        void AddSwitch(WF.ToolStripMenuItem parent, string text, Action<bool> set, Func<bool> get) {
            var it = new WF.ToolStripMenuItem(text);
            it.Click += delegate { set(!get()); RefreshTray(); };
            traySw.Add(it); swState.Add(get); parent.DropDownItems.Add(it);
        }
        /// <summary>Tick what is currently true. Called when the menu opens and after every state change.</summary>
        void RefreshTray() {
            if (tray == null) return;
            var S = E.S;
            for (int i = 0; i < 3; i++) trayModes[i].Checked = i == E.ModeIndex;
            for (int i = 0; i < trayFan.Count; i++) trayFan[i].Checked = S.Fan == Choice.FanModes[i];
            foreach (var it in trayPower) it.Checked = (int)it.Tag == S.TdpOffset;
            int hzNow = Display.CurrentHz();
            foreach (var it in trayHz) it.Checked = (int)it.Tag == hzNow;
            if (trayGpu.Count == 4) { GpuLevel g = E.EffectiveGpu; for (int i = 0; i < 3; i++) trayGpu[i].Checked = !S.GpuAuto && (int)g == i; trayGpu[3].Checked = S.GpuAuto; }
            int gfx = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
            foreach (var it in trayGfx) it.Checked = (int)it.Tag == gfx;
            int litNow = Choice.OfLight(S.Light, S.LightEffect);
            for (int i = 0; i < trayLight.Count; i++) trayLight[i].Checked = i == litNow;
            foreach (var it in trayBright) it.Checked = (int)it.Tag == S.LightLevel;
            for (int i = 0; i < traySw.Count; i++) traySw[i].Checked = swState[i]();
        }
        /// <summary>Write a graphics mode and offer the restart it needs; shared by the Settings page and the tray.</summary>
        void SwitchGraphics(int m) {
            int current = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
            if (m == current) return;
            var answer = MessageBox.Show("Switch graphics to " + Engine.GpuModeNames[m] + "?\n\nThe change is written now and takes effect after a restart, the same way OMEN Gaming Hub does it.\n\nRestart now?",
                Program.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { Refresh(); return; }
            if (!E.SetGpuMode(m)) { Refresh(); return; }
            if (answer == MessageBoxResult.Yes) { try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"" + Program.DisplayName + ": graphics mode change\"") { CreateNoWindow = true, UseShellExecute = false }); } catch (Exception ex) { ShowToast("Restart failed: " + ex.Message, true); } }
            else ShowToast(Engine.GpuModeNames[m] + " after the next restart", false);
            Refresh();
        }

        void Wire() {
            foreach (string n in new[] { "HeadHome", "HeadFans", "HeadKbd", "HeadSettings" })
                F<FrameworkElement>(n).MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs me) { if (me.LeftButton == MouseButtonState.Pressed) Drag(); };
            btnClose.Click += delegate { HideToTray(); };
            powerDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            powerDebounce.Tick += delegate { powerDebounce.Stop(); int off = (int)slPower.Value; Bg(delegate { E.SetTdpOffset(off, false); }); };
            slPower.ValueChanged += delegate {
                txtPower.Text = "+" + (int)slPower.Value + " W";
                if (!syncing) { powerDebounce.Stop(); powerDebounce.Start(); }
            };
        }

        // ---------- pages ----------
        void Navigate(Page p, bool animate) {
            if (p == cur && pageShown) return;
            bool wasKbd = cur == Page.Keyboard; var old = pageShown ? pages[(int)cur] : null;
            cur = p; pageShown = true;
            var page = pages[(int)p];
            bool live = animate && screenshotPath == null && IsVisible && old != null && old != page;
            for (int i = 0; i < 4; i++) if (pages[i] != page && !(live && pages[i] == old)) ShowPage(pages[i], false);
            pageHost.Width = p == Page.Keyboard ? KbdPageW : PageW;
            for (int i = 0; i < 4; i++) nav[i].SetSelected(i == (int)p);
            if (p == Page.Keyboard) ApplyEditorState();
            else if (wasKbd) RefreshLighting();
            if (p == Page.Settings) {
                scroll.ScrollToTop(); scrollTo = 0; UpdateKeyStatus(); UpdateUpdateRow();
                txtMachine.Text = E.Hw.IsDemo && screenshotPath == null ? "simulated hardware" : (E.BiosOk ? "board " + E.Board : "firmware unavailable");
            }
            if (p == Page.Fans) RefreshFans(false);
            // the page has to be Visible before Morph measures it: a Collapsed element reports no desired size at all
            if (!live) ShowPage(page, true);
            else { page.BeginAnimation(UIElement.OpacityProperty, null); page.Opacity = 0; page.IsHitTestVisible = true; page.RenderTransform = null; page.Visibility = Visibility.Visible; }
            double was = ActualWidth * ActualHeight;
            Morph(animate);
            if (live) CrossFade(old, page, (RailW + pageHost.Width) * Math.Ceiling(NaturalHeight()) > was);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { PlaceRailPill(animate); });
            Log.Write("page " + p);
        }

        /// <summary>Something changed how tall page p wants to be. Re-aim the window once layout has caught up; see the
        /// note on pageHost.SizeChanged for why the layout pass cannot be relied on to do this by itself.</summary>
        void Remeasure(Page p) {
            if (cur != p || !IsVisible || screenshotPath != null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { if (cur == p) Morph(true); });
        }

        /// <summary>Park a page in a known state, visible and opaque or collapsed; any running fade is dropped.</summary>
        static void ShowPage(FrameworkElement page, bool on) {
            page.BeginAnimation(UIElement.OpacityProperty, null);
            page.Opacity = 1; page.IsHitTestVisible = true; page.RenderTransform = null;
            page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The Dynamic-Island order: the old page drops out fast; the new one arrives once the frame has mostly got there
        /// when growing (it would otherwise be revealed by the moving edge), almost at once when shrinking (it fits inside the old frame).</summary>
        // the two fades overlap rather than meeting: with the frame now moving from the first frame, the incoming page
        // can start while the outgoing one is still going, and the panel is never briefly empty
        const int FadeOutMs = 110, FadeInMs = 170, GrowDelayMs = 70, ShrinkDelayMs = 25;
        void CrossFade(FrameworkElement old, FrameworkElement page, bool grow) {
            old.IsHitTestVisible = false;                                                 // it overlaps the new page in the host grid
            var outAn = new DoubleAnimation(old.Opacity, 0, TimeSpan.FromMilliseconds(FadeOutMs)) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            outAn.Completed += delegate { if (pages[(int)cur] != old) ShowPage(old, false); };
            old.BeginAnimation(UIElement.OpacityProperty, outAn);
            var tt = new TranslateTransform(0, 0); page.RenderTransform = tt;
            var delay = TimeSpan.FromMilliseconds(grow ? GrowDelayMs : ShrinkDelayMs);
            var inAn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs)) { BeginTime = delay, FillBehavior = FillBehavior.HoldEnd, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            inAn.Completed += delegate { if (pages[(int)cur] == page) { page.Opacity = 1; page.BeginAnimation(UIElement.OpacityProperty, null); } };
            page.BeginAnimation(UIElement.OpacityProperty, inAn);
            var slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = delay, FillBehavior = FillBehavior.Stop, EasingFunction = new SpringEase() };
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // ---------- the morph: four edge springs, one SetWindowPos per rendered frame, no WPF animation on the window itself ----------
        // Why it is built this way (see anim-report.md):
        //  * CompositionTarget.Rendering + RenderingTime: WPF snaps that time to the next vsync, so every step is one display frame.
        //  * WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE around the morph: WPF then presents each resized frame synchronously inside WM_SIZE
        //    (HwndTarget.OnResize only waits for the present during user resizes), so the DWM never composes the new frame with old content.
        //  * SWP_NOCOPYBITS: nothing of the old surface is worth copying, WPF repaints everything. No SWP_NOSENDCHANGING: WPF uses
        //    WM_WINDOWPOSCHANGING to park its render thread while USER swaps the surface.
        //  * Edges, not left+width: each edge is rounded to device pixels on its own, so a right-anchored move never wobbles the right edge.
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOCOPYBITS = 0x0100, SWP_NOOWNERZORDER = 0x0200;
        // springs in Apple's (duration, bounce) form: w0 = 2π/duration, zeta = 1 - bounce. Grow: 1.5 px of overshoot on 240 px, 90 % there
        // at 208 ms. Shrink: critically damped, 90 % at 200 ms; a window that shrinks and comes back reads as a glitch.
        const double GrowDur = 0.40, GrowBounce = 0.15, ShrinkDur = 0.32, ShrinkBounce = 0.0, MorphMaxSec = 1.2;
        bool morphing, morphGrow, morphTicking, morphSizeMove; TimeSpan morphLast; long morphWall; double morphAge;
        readonly double[] mx = new double[4], mv = new double[4], mt = new double[4];   // edges l, t, r, b in DIPs: position, velocity, target
        readonly int[] mpx = new int[4];                                                // last device rect sent, so an unchanged frame costs nothing

        void Morph(bool animate) {
            if (pageHost == null || dragging) return;
            double w = RailW + pageHost.Width, natural = NaturalHeight();
            if (natural < 100) return;
            var wa = SystemParameters.WorkArea;
            double h = Math.Min(Math.Ceiling(natural), Math.Max(360, wa.Height - 24));
            double l = Left, t = Top; bool move = IsVisible && screenshotPath == null && !double.IsNaN(l) && !double.IsNaN(t) && l > -30000;
            if (move) {
                if (l + w > wa.Right - 6) l = Math.Max(wa.Left + 6, wa.Right - 6 - w);
                if (t + h > wa.Bottom - 6) t = Math.Max(wa.Top + 6, wa.Bottom - 6 - h);
            } else { l = double.IsNaN(l) ? 0 : l; t = double.IsNaN(t) ? 0 : t; }
            if (!animate || !IsVisible || screenshotPath != null || hwnd == IntPtr.Zero) {
                StopMorph();
                Width = w; Height = h;
                if (move) { if (Math.Abs(l - Left) > 0.5) Left = l; if (Math.Abs(t - Top) > 0.5) Top = t; }
                return;
            }
            if (!morphing) {
                if (Math.Abs(ActualWidth - w) < 0.5 && Math.Abs(ActualHeight - h) < 0.5 && Math.Abs(Left - l) < 0.5 && Math.Abs(Top - t) < 0.5) return;
                mx[0] = Left; mx[1] = Top; mx[2] = Left + ActualWidth; mx[3] = Top + ActualHeight;
                for (int i = 0; i < 4; i++) mv[i] = 0;
                mpx[0] = int.MinValue; morphAge = 0;
            } else if (Math.Abs(mt[0] - l) < 0.5 && Math.Abs(mt[1] - t) < 0.5 && Math.Abs(mt[2] - (l + w)) < 0.5 && Math.Abs(mt[3] - (t + h)) < 0.5) return;
            morphGrow = w * h > (mx[2] - mx[0]) * (mx[3] - mx[1]);                    // retargets keep their velocity; only the spring changes
            mt[0] = l; mt[1] = t; mt[2] = l + w; mt[3] = t + h;
            morphLast = TimeSpan.Zero; morphWall = Stopwatch.GetTimestamp();
            if (!morphing) {
                morphing = true;
                SendMessage(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero); morphSizeMove = true;
                CompositionTarget.Rendering += MorphTick;
            }
        }
        /// <summary>What the visible page wants to be, measured with no height limit (Settings is a fixed, scrolling page). Only the
        /// current page counts: the outgoing one is still in the host while it fades.</summary>
        double NaturalHeight() {
            if (cur == Page.Settings) return SettingsH;
            var page = pages[(int)cur]; if (page == null || pageHost.Width <= 0) return 0;
            page.Measure(new Size(pageHost.Width, double.PositiveInfinity));
            return page.DesiredSize.Height;
        }
        void StopMorph() {
            if (!morphing) return;
            morphing = false; CompositionTarget.Rendering -= MorphTick;
            if (morphSizeMove) { morphSizeMove = false; SendMessage(hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero); }
        }
        void MorphTick(object o, EventArgs e) {
            var re = e as RenderingEventArgs;
            if (re == null || morphTicking || !morphing) return;
            if (re.RenderingTime == morphLast) return;          // WPF re-raises Rendering (same locked tick time) inside the synchronous render our own SetWindowPos causes
            double dt;
            if (morphLast == TimeSpan.Zero) dt = Math.Max(1.0 / 120, Math.Min(1.0 / 30, (Stopwatch.GetTimestamp() - morphWall) / (double)Stopwatch.Frequency));   // first frame moves too
            else dt = Math.Min(0.05, (re.RenderingTime - morphLast).TotalSeconds);      // a stall is not a leap
            morphLast = re.RenderingTime; morphAge += dt;
            double w0 = 2 * Math.PI / (morphGrow ? GrowDur : ShrinkDur), zeta = 1 - (morphGrow ? GrowBounce : ShrinkBounce);
            bool settled = morphAge >= MorphMaxSec;
            if (!settled) {
                settled = true;
                for (int i = 0; i < 4; i++) {
                    Ui.SpringStep(ref mx[i], ref mv[i], mt[i], dt, w0, zeta);
                    if (Math.Abs(mx[i] - mt[i]) > 0.25 || Math.Abs(mv[i]) > 6) settled = false;
                }
            }
            if (settled) for (int i = 0; i < 4; i++) { mx[i] = mt[i]; mv[i] = 0; }
            morphTicking = true;
            try { ApplyBounds(mx[0], mx[1], mx[2], mx[3]); } finally { morphTicking = false; }
            if (settled) {
                StopMorph();
                Width = mt[2] - mt[0]; Height = mt[3] - mt[1]; E.S.WinX = (int)mt[0]; E.S.WinY = (int)mt[1];
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { PlaceRailPill(true); });   // residual is sub-pixel; Glide snaps it
            }
        }
        void ApplyBounds(double l, double t, double r, double b) {
            var ps = PresentationSource.FromVisual(this); if (ps == null || ps.CompositionTarget == null) return;
            var m = ps.CompositionTarget.TransformToDevice;
            int x0 = (int)Math.Round(l * m.M11), y0 = (int)Math.Round(t * m.M22), x1 = (int)Math.Round(r * m.M11), y1 = (int)Math.Round(b * m.M22);
            if (x0 == mpx[0] && y0 == mpx[1] && x1 == mpx[2] && y1 == mpx[3]) return;
            mpx[0] = x0; mpx[1] = y0; mpx[2] = x1; mpx[3] = y1;
            SetWindowPos(hwnd, IntPtr.Zero, x0, y0, x1 - x0, y1 - y0, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS);
        }

        void SetAccent(int modeIndex, bool animate) {
            Color c = Ui.ModeColor(modeIndex);
            if (!animate) { accentSrc.BeginAnimation(ColorSource.ColorProperty, null); accentSrc.Color = c; }
            else Ui.GlideColor(accentSrc, c, 320);
            if (curveView != null) curveView.Repaint();
        }

        // ---------- keyboard lighting ----------
        void BuildKeyboard() {
            if (E.Light == null) return;
            var layout = BuildLayout();
            kbdBig = new KeyboardView { Interactive = true, Gap = 5, RowPitch = 39 }; kbdBig.SetLayout(layout); kbdHost.Child = kbdBig;
            kbdBig.KeyClicked += delegate(KeyDef k) { if (k != null) SelectGroup(k); };
            kbdBig.KeyHovered += delegate(KeyDef k) {
                kbdBig.Hover.Clear();
                if (k != null) foreach (int i in Group(k)) kbdBig.Hover.Add(i);
                kbdBig.Repaint();
            };
            kbdModes = new LinkSeg(Choice.Light, 22, 13.5, 4, new[] { null, null, "The zone colours pulse", "Every zone through the spectrum together", "The spectrum travels across the zones", "Hand the keyboard to Windows Dynamic Lighting" });
            F<Border>("KbdModeHost").Child = kbdModes;
            if (E.Light.Inert)
                for (int i = 1; i <= 4; i++) kbdModes.SetEnabled(i, false, "HP's firmware lighting interface does not drive per-key keyboards");
            kbdModes.Picked += delegate(int i) {
                int m = Choice.LightMode(i), fx = Choice.LightEffect(i);
                E.S.Light = m; E.S.LightEffect = fx; ApplyEditorState();
                Bg(delegate { E.SetLight(m, fx, false); });
            };
            // what one click takes. Key and Row need per-key hardware; the firmware's zone table cannot address one key.
            granSeg = new Seg(new[] { "Key", "Row", "Zone", "All" }, null, null, Seg.Kind.Row);
            F<Border>("GranHost").Child = granSeg;
            bool perKey = E.Light.Kind == LightKind.PerKey && E.Light.Zones > 4;
            granSeg.SetEnabled(0, perKey, "This keyboard lights in zones, not per key");
            granSeg.SetEnabled(1, perKey, "This keyboard lights in zones, not per key");
            gran = perKey ? "Key" : "Zone";
            granSeg.Select(perKey ? 0 : 2, false);
            granSeg.Picked += delegate(int i) { gran = new[] { "Key", "Row", "Zone", "All" }[i]; kbdBig.Selected.Clear(); kbdBig.Hover.Clear(); kbdBig.Repaint(); UpdateSelectionText(); };
            hueBar = new StripPicker { Cells = 36 }; F<Border>("HueHost").Child = hueBar;
            shadeBar = new StripPicker { Cells = 30, Shade = true }; F<Border>("ShadeHost").Child = shadeBar;
            hueBar.Picked += delegate(Rgb c) { Paint(c, false); };
            shadeBar.Picked += delegate(Rgb c) { Paint(c, false); };
            colorDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            colorDebounce.Tick += delegate { colorDebounce.Stop(); Rgb v = curColor; int[] z = SelectedZones(); Bg(delegate { E.SetLightColor(z, v); }); };
            txtHex.TextChanged += delegate {
                if (syncing) return;
                string t = txtHex.Text.Trim().TrimStart('#'); Rgb c;
                if (t.Length == 6 && Rgb.TryParse(t, out c)) { hexTyping = true; Paint(c, true); hexTyping = false; }
            };
            levelDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            levelDebounce.Tick += delegate { levelDebounce.Stop(); int lv = (int)slLevel.Value; Bg(delegate { E.SetLightLevel(lv); }); };
            RoutedPropertyChangedEventHandler<double> level = delegate(object o, RoutedPropertyChangedEventArgs<double> ev) {
                bool was = syncing;
                if (!was) { syncing = true; try { if (ReferenceEquals(o, slLevel)) slLevel2.Value = slLevel.Value; else slLevel.Value = slLevel2.Value; } finally { syncing = false; } }
                int v = (int)slLevel.Value; txtLevel.Text = v + "%"; txtLevel2.Text = v + "%";
                if (kbdBig != null) { kbdBig.Level = kbdMini.Level = v / 100.0; kbdBig.Repaint(); kbdMini.Repaint(); }   // the drawing dims with the slider
                if (!was) { levelDebounce.Stop(); levelDebounce.Start(); }
            };
            slLevel.ValueChanged += level; slLevel2.ValueChanged += level;
            speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            speedDebounce.Tick += delegate { speedDebounce.Stop(); int sp = (int)slSpeed.Value; Bg(delegate { E.SetLightSpeed(sp); }); };
            RoutedPropertyChangedEventHandler<double> speed = delegate(object o, RoutedPropertyChangedEventArgs<double> ev) {
                bool was = syncing;
                if (!was) { syncing = true; try { if (ReferenceEquals(o, slSpeed)) slSpeed2.Value = slSpeed.Value; else slSpeed.Value = slSpeed2.Value; } finally { syncing = false; } }
                int v = (int)slSpeed.Value; txtSpeed.Text = v.ToString(CultureInfo.InvariantCulture); txtSpeed2.Text = v.ToString(CultureInfo.InvariantCulture);
                if (!was) { speedDebounce.Stop(); speedDebounce.Start(); }
            };
            slSpeed.ValueChanged += speed; slSpeed2.ValueChanged += speed;
            btnWinLighting.MouseLeftButtonUp += delegate { try { Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open Windows settings: " + ex.Message, true); } };
            // the drawings show the frames the keyboard actually received; the Home glyph only while the pointer is over the row
            E.FrameChanged += delegate(Rgb[] f) { Dispatcher.BeginInvoke((Action)delegate { OnFrame(f); }); };
        }
        /// <summary>The keys one click takes: this key, its row, its zone, or the whole board.</summary>
        List<int> Group(KeyDef k) {
            var l = new List<int>();
            foreach (var x in kbdBig.Keys)
                if (gran == "All" || (gran == "Row" && x.Row == k.Row) || (gran == "Zone" && x.Zone == k.Zone) || (gran == "Key" && x.Index == k.Index)) l.Add(x.Index);
            return l;
        }
        void SelectGroup(KeyDef k) {
            bool add = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!add) kbdBig.Selected.Clear();
            foreach (int i in Group(k)) kbdBig.Selected.Add(i);
            kbdBig.Repaint(); UpdateSelectionText(); SyncPickerFromSelection();
        }
        int[] SelectedZones() {
            var set = new List<int>();
            foreach (var k in kbdBig.Keys) if (kbdBig.Selected.Contains(k.Index) && !set.Contains(k.Zone)) set.Add(k.Zone);
            set.Sort(); return set.ToArray();
        }
        void UpdateSelectionText() {
            int n = kbdBig.Selected.Count;
            if (n == 0) { txtKeySel.Text = "Click the map to select"; return; }
            if (gran == "All") { txtKeySel.Text = "whole keyboard"; return; }
            if (gran == "Zone") {
                var z = SelectedZones(); var names = new List<string>();
                foreach (int zz in KeyboardLayouts.DisplayOrder(E.Light.Zones)) if (Array.IndexOf(z, zz) >= 0) names.Add(KeyboardLayouts.ZoneName(E.Light.Zones, zz).ToLowerInvariant());
                txtKeySel.Text = z.Length == E.Light.Zones ? "every zone" : string.Join(" + ", names.ToArray());
                return;
            }
            txtKeySel.Text = n + (n == 1 ? " key selected" : " keys selected");
        }
        /// <summary>Paint the selection: the drawing and the swatch update at once, the firmware write is debounced.</summary>
        void Paint(Rgb c, bool fromHex) {
            if (kbdBig.Selected.Count == 0) { foreach (var k in kbdBig.Keys) kbdBig.Selected.Add(k.Index); UpdateSelectionText(); }
            curColor = c;
            foreach (int z in SelectedZones()) if (z < E.LightColors.Length) E.LightColors[z] = c;
            kbdBig.SetColors(E.LightColors, false); kbdMini.SetColors(E.LightColors, false);
            SyncSwatch(fromHex);
            colorDebounce.Stop(); colorDebounce.Start();
        }
        void SyncSwatch(bool fromHex) {
            bool was = syncing; syncing = true;
            try {
                bool one = OneColour();
                hexChip.Background = one ? Ui.Brush(curColor) : Ui.Brush("#2C2825");
                if (!fromHex && !hexTyping) txtHex.Text = one ? curColor.Hex.ToLowerInvariant() : "";
                hueBar.Current = curColor; hueBar.HasCurrent = one; hueBar.Repaint();
                double h, s, v; curColor.ToHsv(out h, out s, out v);
                shadeBar.Hue = one ? h : 210; shadeBar.Current = curColor; shadeBar.HasCurrent = one; shadeBar.Repaint();
            } finally { syncing = was; }
        }
        /// <summary>True when the selection is one colour, so there is something to show in the field.</summary>
        bool OneColour() {
            int[] z = SelectedZones(); if (z.Length == 0) return false;
            for (int i = 1; i < z.Length; i++) {
                if (z[i] >= E.LightColors.Length || z[0] >= E.LightColors.Length) return false;
                var a = E.LightColors[z[0]]; var b = E.LightColors[z[i]];
                if (a.R != b.R || a.G != b.G || a.B != b.B) return false;
            }
            return true;
        }
        void SyncPickerFromSelection() {
            int[] z = SelectedZones();
            if (z.Length > 0 && E.LightColors.Length > z[0]) curColor = E.LightColors[z[0]];
            SyncSwatch(false);
        }
        void OnFrame(Rgb[] f) {
            if (kbdMini == null) return;
            if (cur == Page.Keyboard && IsVisible) kbdBig.SetColors(f, true);
            if (lightRow.IsMouseOver || miniNeedsFrame) { miniNeedsFrame = false; kbdMini.SetColors(f, true); }
        }
        /// <summary>Show only what the current mode can use: colour for the per-zone modes, speed and brightness for
        /// the effects that paint their own colours.</summary>
        /// <summary>The drawn keyboard for this machine. Four zones come from the layout itself; on a per-key board
        /// the keyboard says which of its lamps sits under each key, so the device decides and we do not carry a table.</summary>
        List<KeyDef> BuildLayout() {
            var layout = KeyboardLayouts.Build(E.Light.Numpad, E.Light.Zones);
            var perKey = E.Light as PerKeyLighting;
            if (perKey != null) KeyboardLayouts.BindLamps(layout, perKey.Device);
            return layout;
        }

        void ApplyEditorState() {
            if (kbdBig == null) return;
            var S = E.S; int m = S.Light, fx = S.LightEffect;
            // A per-key board answers every firmware lighting call and lights nothing by them, so none of our own
            // modes can be offered: Windows Dynamic Lighting talks to the keyboard directly and is the only one
            // that works there. Keeping the editor on screen would just discard everything the user picked.
            bool inert = E.Light.Inert;
            if (inert) m = S.Light == 2 ? 2 : 0;
            bool lit = m == 1, pick = lit && (fx == 0 || fx == 1), effect = lit && fx != 0;
            kbdModes.Select(Choice.OfLight(m, fx), IsVisible && cur == Page.Keyboard);
            selectRow.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            colorEditor.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            effectEditor.Visibility = effect && fx != 1 ? Visibility.Visible : Visibility.Collapsed;
            speedInline.Visibility = fx == 1 && lit ? Visibility.Visible : Visibility.Collapsed;      // breathe: colours and a speed
            kbdInfo.Visibility = lit ? Visibility.Collapsed : Visibility.Visible;
            btnWinLighting.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
            txtKbdInfo.Text = inert && m != 2
                ? "This is a per-key keyboard. HP's firmware interface answers for it but does not light it, so Ohman cannot set its colours yet \u2014 they go over the keyboard's own USB interface, which is only documented for the 2025 boards. Windows Dynamic Lighting drives it today."
                : m == 0 ? "The backlight is off. Pick a mode to turn it back on, or press the keyboard backlight key."
                : (WinLighting.Present || E.Hw.IsDemo ? "Windows Dynamic Lighting has the keyboard. Its colours and effects come from Windows settings."
                                                      : "No Dynamic Lighting device for this keyboard was found; Windows cannot drive it.");
            kbdBig.Selectable = pick; kbdBig.Off = m == 0; kbdBig.WindowsOwned = m == 2; kbdBig.Smooth = effect;
            kbdMini.Off = m == 0; kbdMini.WindowsOwned = m == 2; kbdMini.Smooth = effect;
            kbdBig.Level = kbdMini.Level = S.LightLevel / 100.0;
            if (!pick) { kbdBig.Selected.Clear(); kbdBig.Hover.Clear(); }
            string fxName = new[] { "static", "breathe", "cycle", "wave" }[Math.Max(0, Math.Min(3, fx))];
            // breathe runs in the firmware too, but it breathes the colours you picked, so it is not "colour fixed"
            txtKbdStatus.Text = inert && m != 2 ? "per-key · not driveable yet" : m == 2 ? "windows lighting" : m == 0 ? "backlight off" : fx >= 2 ? "firmware effect · colour fixed" : E.Light.Describe + " · " + fxName;
            UpdateSelectionText();
            miniNeedsFrame = true; kbdBig.Repaint(); kbdMini.Repaint();
            Remeasure(Page.Keyboard);        // the colour editor and the effect editor are different heights
        }
        void RefreshLighting() {
            if (E.Light == null || kbdMini == null) return;
            var S = E.S;
            ApplyEditorState();
            if (!(S.Light == 1 && S.LightEffect != 0)) { kbdMini.SetColors(E.LightColors, false); kbdBig.SetColors(E.LightColors, false); }   // effects repaint from the engine's frames
            bool was = syncing; syncing = true;
            try {
                slLevel.Value = slLevel2.Value = Math.Max(5, S.LightLevel); txtLevel.Text = txtLevel2.Text = S.LightLevel + "%";
                slSpeed.Value = slSpeed2.Value = S.LightSpeed; txtSpeed.Text = txtSpeed2.Text = S.LightSpeed.ToString(CultureInfo.InvariantCulture);
            } finally { syncing = was; }
            if (cur != Page.Keyboard) SyncPickerFromSelection();
            string fxName = new[] { "Static", "Breathe", "Cycle", "Wave" }[Math.Max(0, Math.Min(3, S.LightEffect))];
            txtLightSub.Text = S.Light == 2 ? "Windows Dynamic Lighting" : S.Light == 0 ? "Off" : fxName + " · " + E.Light.Describe;
        }

        // ---------- fans page ----------
        void RefreshFans(bool animate) {
            var S = E.S; FanMode f = S.Fan; bool linked = S.Cur.CurveLinked;
            bool was = syncing; syncing = true;
            try {
                fanSeg.Select(Choice.Of(f), animate && IsVisible);
                curveBlock.Visibility = f == FanMode.Auto || f == FanMode.Custom ? Visibility.Visible : Visibility.Collapsed;
                maxBlock.Visibility = f == FanMode.Max ? Visibility.Visible : Visibility.Collapsed;
                manualBlock.Visibility = f == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                optsAuto.Visibility = f == FanMode.Auto ? Visibility.Visible : Visibility.Collapsed;
                optsCurve.Visibility = f == FanMode.Custom ? Visibility.Visible : Visibility.Collapsed;
                optsMax.Visibility = f == FanMode.Max ? Visibility.Visible : Visibility.Collapsed;
                optsManual.Visibility = f == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                tgEcoCool2.IsChecked = S.EcoCool; tgMaxCool.IsChecked = S.MaxBackWhenCool; tgManualLink.IsChecked = S.ManualLinked; tgLink.IsChecked = linked;
                for (int i = 0; i < stopAfterSeg.Count; i++) if ((int)stopAfterSeg.Tags[i] == S.MaxStopAfterMin) stopAfterSeg.Select(i, animate && IsVisible);
                if (linked || f != FanMode.Custom) curveGpu = false;
                curveWhichHost.Visibility = f == FanMode.Custom && !linked ? Visibility.Visible : Visibility.Collapsed;
                curveWhich.Select(curveGpu ? 1 : 0, animate && IsVisible);
                int floor = S.Cur.CurveFloor <= E.P.Curve.Floor ? E.P.Curve.Floor : S.Cur.CurveFloor;
                slFloor.Value = Math.Min(slFloor.Maximum, floor); txtFloor.Text = floor <= E.P.Curve.Floor ? "off" : Pct(floor);
                slRamp.Value = S.Cur.CurveRamp; txtRamp.Text = S.Cur.CurveRamp + " s";
                curveView.ReadOnly = f != FanMode.Custom;
                curveView.UserFloor = f == FanMode.Custom && floor > E.P.Curve.Floor ? floor : 0;
                if (f == FanMode.Auto) {
                    curveView.Levels = E.VendorCurveAt(false);
                    txtCurveTitle.Text = "This model's curve"; txtCurveHint.Text = "read-only";
                } else if (f == FanMode.Custom) {
                    if (!curveDebounce.IsEnabled) curveView.Levels = (int[])(curveGpu ? S.Cur.GpuCurveLevels : S.Cur.CurveLevels).Clone();
                    txtCurveTitle.Text = curveGpu ? "GPU curve" : "CPU curve"; txtCurveHint.Text = "drag a point · shift-drag moves all";
                }
                slFan1.Value = S.Fan1; slFan2.Value = S.Fan2;
                txtFan1.Text = Pct(S.Fan1); txtFan2.Text = Pct(S.Fan2);
                manPct1.Text = Pct(S.Fan1).TrimEnd('%'); manPct2.Text = Pct(S.Fan2).TrimEnd('%');
                manSub1.Text = E.Rpm(S.Fan1) + " · CPU fan"; manSub2.Text = E.Rpm(S.Fan2) + " · GPU fan";
                UpdateCurveLive(); UpdateMaxBlock(); UpdateFanStatus(); UpdateFanFooter();
            } finally { syncing = was; }
            Remeasure(Page.Fans);            // the curve, max and manual blocks are different heights
        }
        void UpdateMaxBlock() {
            if (E.S.Fan != FanMode.Max) return;
            int f1 = lastFans != null && lastFans[0] > 0 ? lastFans[0] : 0, f2 = lastFans != null && lastFans[1] > 0 ? lastFans[1] : 0;
            maxFan1.Text = Level(f1); maxFan2.Text = Level(f2);
            maxFan1Sub.Text = "CPU fan" + (f1 > 0 ? " · " + Pct(f1) : ""); maxFan2Sub.Text = "GPU fan" + (f2 > 0 ? " · " + Pct(f2) : "");
            maxTemp.Text = double.IsNaN(E.CpuTemp) ? "--" : E.CpuTemp.ToString("0", CultureInfo.InvariantCulture);
            maxTempSub.Text = "CPU · " + Trend();
            var left = E.MaxLeft;
            if (E.S.MaxStopAfterMin > 0 && left > TimeSpan.Zero) { maxMins.Text = ((int)Math.Ceiling(left.TotalMinutes)).ToString(CultureInfo.InvariantCulture); maxMinsUnit.Text = " min"; maxMinsSub.Text = "Until it stops"; }
            else { maxMins.Text = E.MaxMinutes.ToString(CultureInfo.InvariantCulture); maxMinsUnit.Text = " min"; maxMinsSub.Text = "Running at max"; }
        }
        string Trend() {
            if (tempTrail.Count < 4) return "steady";
            double a = 0, b = 0; int half = tempTrail.Count / 2;
            for (int i = 0; i < half; i++) a += tempTrail[i];
            for (int i = half; i < tempTrail.Count; i++) b += tempTrail[i];
            double d = b / (tempTrail.Count - half) - a / half;
            return d > 0.8 ? "rising" : d < -0.8 ? "falling" : "steady";
        }
        void UpdateFanFooter() {
            var S = E.S;
            bool link = S.Fan == FanMode.Auto || S.Fan == FanMode.Custom;
            btnFanAction.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
            txtFanRight.Visibility = link ? Visibility.Collapsed : Visibility.Visible;
            btnFanAction.Text = S.Fan == FanMode.Auto ? "Edit as curve" : "Reset curve";
            if (S.Fan == FanMode.Max) {
                txtFanApplied.Text = "Loud" + (lastBiosTemp >= 0 ? " · chassis " + lastBiosTemp + "°" : "");
                txtFanRight.Text = "Ctrl+Alt+M toggles";
            } else {
                txtFanApplied.Text = E.GuardActive ? "Thermal guard: max fan until cool" : "Applied to " + E.ModeName;
                txtFanRight.Text = "CPU " + (double.IsNaN(E.CpuTemp) ? "--" : E.CpuTemp.ToString("0") + "°") + " · GPU " + (double.IsNaN(E.GpuTemp) ? "--" : E.GpuTemp.ToString("0") + "°");
            }
        }
        void UpdateCurveLive() {
            var S = E.S; double t; int lvl;
            if (S.Fan == FanMode.Custom && !S.Cur.CurveLinked && curveGpu) { t = E.GpuTemp; lvl = E.AutoLevel2; }
            else if (S.Fan == FanMode.Custom && S.Cur.CurveLinked) { t = double.IsNaN(E.CpuTemp) ? E.GpuTemp : double.IsNaN(E.GpuTemp) ? E.CpuTemp : Math.Max(E.CpuTemp, E.GpuTemp); lvl = E.AutoLevel1; }
            else { t = E.CpuTemp; lvl = E.AutoLevel1; }
            curveView.LiveTemp = t; curveView.LiveLevel = lvl; curveView.Repaint();
        }
        void UpdateFanStatus() {
            if (lastFans == null) { txtFansStatus.Text = ""; return; }
            string rpm = Level(lastFans[0]) + " / " + E.Rpm(Math.Max(0, lastFans[1]));
            int pct = lastFans[0] > 0 && E.P.Curve.Ceiling > 0 ? (int)Math.Round(100.0 * Math.Max(lastFans[0], lastFans[1]) / E.P.Curve.Ceiling) : -1;
            txtFansStatus.Text = rpm + (pct >= 0 ? " · " + pct + "%" : "");
        }
        string Pct(int level) { return E.Percent(level); }
        /// <summary>A fan level as a bare number for the big readouts: rpm on a model that reports rpm, else a percentage.</summary>
        string Level(int level) {
            if (level < 0) return "--";
            return E.P.RpmPerLevel > 0 ? (level * E.P.RpmPerLevel).ToString(CultureInfo.InvariantCulture) : E.Percent(level).TrimEnd('%');
        }

        // ---------- refresh rate, graphics, key command, tray temperature ----------
        void BuildRefreshRates() {
            int[] rates = Display.Choices();
            if (rates.Length < 2) return;
            hzRow.Visibility = Visibility.Visible; lowHzRow.Visibility = Visibility.Visible;
            var names = new string[rates.Length]; var tags = new object[rates.Length];
            for (int i = 0; i < rates.Length; i++) { names[i] = i == rates.Length - 1 ? rates[i] + " Hz" : rates[i].ToString(CultureInfo.InvariantCulture); tags[i] = rates[i]; }
            hzSeg = new Seg(names, null, tags, Seg.Kind.Row); hzSeg.SetMono(11.5); F<Border>("HzSegHost").Child = hzSeg;
            hzSeg.Picked += delegate(int i) { int h = (int)hzSeg.Tags[i]; Bg(delegate { E.SetRefreshRate(h); }); };
        }
        /// <summary>Graphics modes the firmware offers, as a segment; a change is written at once and needs a restart to take effect.</summary>
        void BuildGraphicsModes() {
            if (!E.BiosOk && !E.Hw.IsDemo) return;
            int[] order = { 0, 1, 3 };                                       // Hybrid, Discrete (the MUX), iGPU only
            var names = new List<string>(); var tags = new List<object>();
            foreach (int mode in order) if (E.GpuModeOffered(mode)) { names.Add(Engine.GpuModeNames[mode]); tags.Add(mode); }
            if (names.Count < 2) return;
            gfxSeg = new Seg(names.ToArray(), null, tags.ToArray(), Seg.Kind.Row); F<Border>("GfxSegHost").Child = gfxSeg;
            gfxRow.Visibility = Visibility.Visible;
            gfxSeg.Picked += delegate(int i) { SwitchGraphics((int)gfxSeg.Tags[i]); };
        }
        void RunKeyCommand() {
            string cmd = E.S.KeyCommand.Trim();
            if (cmd.Length == 0) { ShowToast("OMEN key: no command set (Settings)", true); return; }
            try {
                string file = cmd, args = "";
                if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); if (q > 0) { file = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); } }
                else { int sp = cmd.IndexOf(' '); if (sp > 0) { file = cmd.Substring(0, sp); args = cmd.Substring(sp + 1); } }
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                Flash("OMEN key", cmd, E.ModeIndex);
            } catch (Exception ex) { ShowToast("OMEN key command failed: " + ex.Message, true); }
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        int trayTempShown = int.MinValue; SD.Icon trayTempIcon;
        void UpdateTrayTemp(double cpu) {
            if (tray == null) return;
            if (!E.S.TrayTemp || double.IsNaN(cpu)) { if (trayTempShown != int.MinValue) { trayTempShown = int.MinValue; try { tray.Icon = icons[E.ModeIndex]; } catch { } } return; }
            int t = (int)Math.Round(cpu); int key = t * 4 + E.ModeIndex;
            if (key == trayTempShown) return;
            trayTempShown = key;
            try {
                var old = trayTempIcon;
                trayTempIcon = TempIcon(t, Ui.ModeColor(E.ModeIndex));
                tray.Icon = trayTempIcon;
                if (old != null) { IntPtr h = old.Handle; old.Dispose(); DestroyIcon(h); }
            } catch (Exception ex) { Log.Write("tray temp icon: " + ex.Message); }
        }
        /// <summary>The CPU temperature inside the mark: the diamond in the mode colour, the number on it.</summary>
        static SD.Icon TempIcon(int temp, Color c) {
            using (var bmp = DrawMark(SD.Color.FromArgb(c.R, c.G, c.B), 32, temp.ToString(CultureInfo.InvariantCulture))) return SD.Icon.FromHandle(bmp.GetHicon());
        }

        // ---------- helpers ----------
        /// <summary>Hand work to the engine's worker thread. Ordered: two quick clicks reach the firmware in order.</summary>
        void Bg(Action a) { E.Post(a); }
        /// <summary>Work that is slow and touches no firmware (the network, schtasks, the diagnostics dump). Keeping it off
        /// the ordered queue means a 15 s update check cannot sit in front of a mode the user just clicked.</summary>
        static void Slow(Action a) { ThreadPool.QueueUserWorkItem(delegate { try { a(); } catch (Exception ex) { Log.Write("slow: " + ex); } }); }
        /// <summary>Set controls from state without the handlers reading it back as a user action. Restores the
        /// previous value, so it nests: a Refresh inside a Synced block cannot clear the flag underneath it.</summary>
        void Synced(Action a) {
            bool was = syncing; syncing = true;
            try { a(); } finally { syncing = was; }
        }
        /// <summary>Wire a switch once: the flag check and the "only when the user did it" rule live here, not in
        /// eleven copies that each have to remember them.</summary>
        void OnSwitch(ToggleButton t, Action<bool> set) {
            RoutedEventHandler h = delegate { if (syncing) return; set(t.IsChecked == true); };
            t.Checked += h; t.Unchecked += h;
        }
        /// <summary>"HP OMEN Transcend 14 (2024, 14-fb0xxx)" reads as "Transcend 14" in a footer.</summary>
        string ShortModel() {
            string n = E.P.Name ?? "";
            int p = n.IndexOf('('); if (p > 0) n = n.Substring(0, p);
            n = n.Replace("HP ", "").Replace("OMEN ", "").Trim();
            return n.Length == 0 ? "OMEN" : n;
        }
        /// <summary>The key line: green when the OMEN key is known, amber when it has never been seen.</summary>
        void UpdateKeyStatus() {
            if (E.Learning) return;
            bool learned = E.S.KeyId != 0, seen = E.LastEventTime != DateTime.MinValue;
            bool known = learned || seen || (E.P.Verified && E.KeyId != 0);
            keyDot.Fill = Ui.Brush(known ? Ui.Ok : Ui.Warn);
            txtKeyInfo.Text = learned ? "OMEN key learned" : known ? "OMEN key auto-detected" : "OMEN key not detected";
            btnLearn.Text = known ? "Relearn" : "Learn";
        }
        void UpdateUpdateRow() {
            bool newer = E.UpdateAvailable;
            txtUpdate.Text = Program.Version + " · " + Update.Ago(E.LastUpdateCheck) + (newer ? " · " + E.LatestVersion + " available" : "");
            txtUpdate.Foreground = newer ? (Brush)accent : Ui.Desc;
            btnUpdate.Text = newer ? "Download" : "Check now";
        }

        void ApplyModeAsync(int idx) {
            SelectMode(idx, true);
            Bg(delegate { E.SetMode(idx, false); });
        }
        void SelectMode(int idx, bool animate) {
            modeSeg.Select(idx, animate && IsVisible);
            txtHomeTitle.Text = Engine.ModeNames[idx];
            if (shownMode != idx) { shownMode = idx; SetAccent(idx, animate && IsVisible); SetAppIcon(idx); try { tray.Icon = icons[idx]; } catch { } }
        }

        public void Refresh() {
            PollRate();                       // the fan mode decides how fast the sensors have to run; it changes here
            bool wasSyncing = syncing; syncing = true;
            try {
                var S = E.S; int mi = E.ModeIndex;
                SelectMode(mi, true);
                slPower.Value = S.TdpOffset; txtPower.Text = "+" + S.TdpOffset + " W";
                GpuLevel g = E.EffectiveGpu;
                string gpuName = g == GpuLevel.Max ? "GPU max" : g == GpuLevel.Boost ? "GPU boost" : "GPU base";
                txtHomeStatus.Text = (E.P.HasGpuPower ? gpuName : E.CurrentTdp + " W") + (lastBiosTemp >= 0 ? " · chassis " + lastBiosTemp + "°" : "");
                fanLinks.SetText(2, S.Fan == FanMode.Custom ? "Curve" : "Manual");
                fanLinks.Select(S.Fan == FanMode.Auto ? 0 : S.Fan == FanMode.Max ? 1 : 2, IsVisible);
                if (gpuSeg != null) { gpuSeg.Select(S.GpuAuto ? 3 : (int)g, IsVisible && cur == Page.Settings); txtGpuSub.Text = S.GpuAuto ? "Follows the mode" : g == GpuLevel.Max ? "Custom TGP + PPAB" : g == GpuLevel.Boost ? "PPAB" : "Base TGP"; }
                RefreshLighting();
                if (cur == Page.Fans) RefreshFans(true);
                keySeg.Select(Choice.Of(S.Key), IsVisible && cur == Page.Settings);
                keyCmdRow.Visibility = S.Key == KeyAction.Run ? Visibility.Visible : Visibility.Collapsed; if (!txtKeyCmd.IsKeyboardFocused) txtKeyCmd.Text = S.KeyCommand;
                tgLowHzBattery.IsChecked = S.LowHzOnBattery; tgTrayTemp.IsChecked = S.TrayTemp; tgGuard.IsChecked = S.Guard; tgUpdateAuto.IsChecked = S.UpdateOnLaunch;
                int hzNow = Display.CurrentHz();
                if (hzSeg != null) for (int i = 0; i < hzSeg.Count; i++) if ((int)hzSeg.Tags[i] == hzNow) hzSeg.Select(i, IsVisible && cur == Page.Settings);
                foreach (var m in trayHz) m.Checked = (int)m.Tag == hzNow;
                int gfx = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
                if (gfxSeg != null) for (int i = 0; i < gfxSeg.Count; i++) if ((int)gfxSeg.Tags[i] == gfx) gfxSeg.Select(i, IsVisible && cur == Page.Settings);
                txtGfxSub.Text = E.GpuModePending >= 0 && E.GpuModePending != E.GpuMode ? Engine.GpuModeNames[E.GpuModePending] + " after the next restart" : "Takes effect after a restart";
                UpdateKeyStatus(); UpdateUpdateRow();
                tgSuppress.IsChecked = S.SuppressOgh; tgHotkeys.IsChecked = S.Hotkeys; tgEcoBattery.IsChecked = S.EcoOnBattery; tgSyncPower.IsChecked = S.SyncWinPower; tgAutostart.IsChecked = autostart;
                demoBadge.Visibility = E.Hw.IsDemo && screenshotPath == null ? Visibility.Visible : Visibility.Collapsed;
                bool err = (!E.BiosOk || E.ReadOnly) && !E.Hw.IsDemo;
                errBanner.Visibility = err ? Visibility.Visible : Visibility.Collapsed;
                if (err) txtErr.Text = !E.BiosOk ? "BIOS interface unavailable: " + E.LastError
                    : "Unsupported laptop (board " + E.Board + "). Read-only: nothing is written to the firmware. Run tools\\support-info.cmd and open a GitHub issue to add it.";
                infoBanner.Visibility = E.Generic && !E.Hw.IsDemo ? Visibility.Visible : Visibility.Collapsed;
                if (E.Generic) txtInfo.Text = "Unverified model (board " + E.Board + "): using the generic OMEN commands for its firmware generation. If it behaves, say so in a GitHub issue so it can be marked verified.";
                RefreshTray();
                string tip = Program.DisplayName + " · " + E.ModeName + " · " + E.CurrentTdp + " W" + (S.Fan == FanMode.Max ? " · max fan" : "");
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                UpdateFooter();
            } finally { syncing = wasSyncing; }
        }

        void UpdateFooter() {
            txtFoot.Foreground = Ui.Foot;
            if (E.Hw.IsDemo && screenshotPath == null) { dotHb.Fill = Ui.Brush(Ui.Warn); txtFoot.Text = "Demo · run as administrator"; return; }
            if (!E.BiosOk) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Text = "Firmware unavailable"; return; }
            if (E.GuardActive) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Foreground = Ui.Brush(Ui.Danger); txtFoot.Text = "Thermal guard · chassis " + E.GuardChassis + "°"; return; }
            double age = (DateTime.Now - E.LastHeartbeat).TotalSeconds;
            bool fresh = E.LastHeartbeat != DateTime.MinValue && age < E.S.HeartbeatSec * 2.5;
            dotHb.Fill = Ui.Brush(fresh ? Ui.Ok : Ui.Warn);
            txtFoot.Text = ShortModel() + " · " + E.FanCount + " fans";
            txtFoot.ToolTip = "Ohman is driving this laptop's firmware" + (fresh ? ", last refreshed " + E.LastHeartbeat.ToString("HH:mm:ss") : "; the refresh is overdue");
        }

        bool sensorsSeen;
        void OnSensors(SensorSnapshot s) {
            E.CpuTemp = s.CpuTemp; E.GpuTemp = s.GpuTemp;
            onBattery = s.OnBattery;
            UpdateTrayTemp(s.CpuTemp);
            if (!double.IsNaN(s.CpuTemp)) { tempTrail.Add(s.CpuTemp); if (tempTrail.Count > 8) tempTrail.RemoveAt(0); sensorsSeen = true; }
            if (!IsVisible) return;          // the rest of this writes text nobody is looking at
            if (cur == Page.Fans) { UpdateCurveLive(); UpdateMaxBlock(); UpdateFanFooter(); }
            bigCpu.Text = double.IsNaN(s.CpuTemp) ? "--" : s.CpuTemp.ToString("0", CultureInfo.InvariantCulture);
            bigGpu.Text = double.IsNaN(s.GpuTemp) ? "--" : s.GpuTemp.ToString("0", CultureInfo.InvariantCulture);
            bigCpu.Foreground = TempBrush(s.CpuTemp); bigGpu.Foreground = TempBrush(s.GpuTemp);
            subCpu.Text = "CPU" + (double.IsNaN(s.CpuLoad) ? "" : " · " + s.CpuLoad.ToString("0") + "%") + (double.IsNaN(s.CpuMhz) || s.CpuMhz <= 0 ? "" : " · " + (s.CpuMhz / 1000).ToString("0.0") + " GHz") + (double.IsNaN(s.CpuWatts) ? "" : " · " + s.CpuWatts.ToString("0") + " W");
            subGpu.Text = "GPU" + (double.IsNaN(s.GpuLoad) ? "" : " · " + s.GpuLoad.ToString("0") + "%") + (double.IsNaN(s.GpuWatts) ? "" : " · " + s.GpuWatts.ToString("0") + " W");
            txtFootRight.Text = s.BatteryPercent >= 0 && s.BatteryPercent <= 100 ? (s.OnBattery ? "Battery " : "AC · ") + s.BatteryPercent + "%" : "";
        }
        Brush TempBrush(double t) { return double.IsNaN(t) ? Ui.TextB : t >= E.P.Guard.CpuHot ? Ui.Brush(Ui.Danger) : t >= E.P.Guard.WarnAt ? Ui.Brush(Ui.Warn) : Ui.TextB; }

        void ReadHardwareAsync() {
            if (reading || (!E.BiosOk && !E.Hw.IsDemo)) return;
            reading = true;
            ThreadPool.QueueUserWorkItem(delegate {
                int[] f = null; int t = -1;
                try { f = E.Hw.GetFanLevels(); } catch (Exception ex) { Log.Write("read fans: " + ex.Message); }
                try { t = E.Hw.GetTemperature(); } catch { }
                Dispatcher.BeginInvoke((Action)delegate {
                    if (f != null) {
                        lastFans = f;
                        bigFan1.Text = Level(f[0]); bigFan2.Text = Level(f[1]);
                        UpdateFanStatus(); if (cur == Page.Fans) UpdateMaxBlock();
                    }
                    if (t >= 0 && t != lastBiosTemp) {
                        lastBiosTemp = t; GpuLevel g = E.EffectiveGpu;
                        txtHomeStatus.Text = (E.P.HasGpuPower ? (g == GpuLevel.Max ? "GPU max" : g == GpuLevel.Boost ? "GPU boost" : "GPU base") : E.CurrentTdp + " W") + " · chassis " + t + "°";
                    }
                    reading = false;
                });
            });
        }

        void OnKey(KeyAction a) {
            switch (a) {
                case KeyAction.Cycle: CycleWithFlash(); break;
                case KeyAction.Show: TogglePanel(); break;
                case KeyAction.MaxFan: ToggleMaxWithFlash(); break;
                case KeyAction.Run: RunKeyCommand(); break;
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
                bool bat = false;
                try { bat = WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Offline; } catch { }
                onBattery = bat; PollRate();
                Bg(delegate { E.OnPowerSource(bat); });
            }
        }

        /// <summary>How hard the app works right now. Reading sensors wakes performance counters and, on a hybrid
        /// laptop, sometimes the discrete GPU, so this is the app's largest recurring power cost and it is worth
        /// scaling. In front of you it has to keep up with the eye; behind you it only feeds the tray number and the
        /// fan curve, and on battery even that can wait longer. The per-frame work (the footer, the fan readouts)
        /// has no reader at all when the window is hidden, so its timer stops outright.</summary>
        void PollRate() {
            // Whoever is reading these numbers sets the floor. The software fan curve steps every 5 s and steps from
            // the last reading it was given, so polling slower than that would make the fans answer a poll interval
            // late — not a saving worth having. With nothing but the tray number waiting on them, they can wait.
            bool curveDrivesFans = !E.ReadOnly && (E.S.Fan == FanMode.Auto || E.S.Fan == FanMode.Custom);
            int ms = IsVisible ? 2000
                   : curveDrivesFans ? 5000
                   : E.S.TrayTemp ? (onBattery ? 10000 : 5000)
                   : (onBattery ? 30000 : 15000);
            sensors.SetInterval(ms);
            if (uiTimer == null) return;
            if (IsVisible) { if (!uiTimer.IsEnabled) uiTimer.Start(); } else uiTimer.Stop();
        }

        // ---------- window plumbing ----------
        void OnSourceInit(object o, EventArgs e) {
            hwnd = new WindowInteropHelper(this).Handle;
            src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(Hook);
            try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, 4); int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, 4); int border = 0x0025282C; DwmSetWindowAttribute(hwnd, 34, ref border, 4); } catch { }
            if (E.S.Hotkeys) RegisterHotkeys();
        }
        void RegisterHotkeys() {
            if (hotkeysRegistered) return;
            var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
            RegisterHotKey(h, 1, MOD_CONTROL | MOD_ALT, (uint)'E'); RegisterHotKey(h, 2, MOD_CONTROL | MOD_ALT, (uint)'B'); RegisterHotKey(h, 3, MOD_CONTROL | MOD_ALT, (uint)'P');
            RegisterHotKey(h, 4, MOD_CONTROL | MOD_ALT, (uint)'M'); RegisterHotKey(h, 5, MOD_CONTROL | MOD_ALT, (uint)'O');
            if (!RegisterHotKey(h, 6, MOD_SHIFT, VK_F11)) Log.Write("hotkey Shift+F11 not available");   // next to the OMEN key: cycles modes
            hotkeysRegistered = true;
        }
        void UnregisterHotkeys() {
            if (!hotkeysRegistered) return;
            var h = new WindowInteropHelper(this).Handle;
            for (int i = 1; i <= 6; i++) UnregisterHotKey(h, i);
            hotkeysRegistered = false;
        }
        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
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
            string page = Program.StartPage;
            if (openSettings) page = "settings"; if (Program.KeyboardTest) page = "keyboard";
            if (page == "fans") Navigate(Page.Fans, false); else if (page == "keyboard" && E.Light != null) Navigate(Page.Keyboard, false); else if (page == "settings") Navigate(Page.Settings, false);
            Morph(false);
            if (Program.FlashTest) Flash("Performance mode", ModeSubs[2], 2);
            if (screenshotPath == null) return;
            var started = DateTime.Now;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Program.FlashTest ? 600 : 500) };
            t.Tick += delegate {
                double age = (DateTime.Now - started).TotalMilliseconds;
                if (!Program.FlashTest && !sensorsSeen && age < 9000) return;       // wait for real temperatures unless they never come
                if (!Program.FlashTest && age < 2800) return;                        // let the first layout settle
                t.Stop();
                try {
                    Morph(false); root.UpdateLayout();
                    SnapshotElement(root, screenshotPath); Log.Write("screenshot saved " + screenshotPath);
                    if (osd != null && osd.IsVisible) { var f = (FrameworkElement)osd.Content; osd.Opacity = 1; SnapshotElement(f, screenshotPath + ".osd.png"); }
                    try { using (var bmp = DrawMark(SD.Color.FromArgb(Ui.BalColor.R, Ui.BalColor.G, Ui.BalColor.B), 256)) bmp.Save(screenshotPath + ".icon.png", System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    try { using (var bmp = DrawMark(SD.Color.FromArgb(Ui.PerfColor.R, Ui.PerfColor.G, Ui.PerfColor.B), 32, "70")) bmp.Save(screenshotPath + ".tray.png", System.Drawing.Imaging.ImageFormat.Png); } catch { }
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

        void Position() {
            var S = E.S;
            if (screenshotPath != null) { WindowStartupLocation = WindowStartupLocation.Manual; Left = -4000; Top = 0; ShowInTaskbar = false; return; }   // rendered off-screen, out of the way
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;   // monitors left of/above the primary have negative coordinates
            if (S.WinX != -1 && S.WinY != -1 && S.WinX >= vl && S.WinY >= vt && S.WinX < vl + SystemParameters.VirtualScreenWidth - 100 && S.WinY < vt + SystemParameters.VirtualScreenHeight - 100) {
                WindowStartupLocation = WindowStartupLocation.Manual; Left = S.WinX; Top = S.WinY;
            } else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        public void ShowPanel() {
            Show(); WindowState = WindowState.Normal; Activate();
            Topmost = true; Topmost = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { Morph(false); PlaceRailPill(false); });
        }
        void TogglePanel() { if (IsVisible) HideToTray(); else ShowPanel(); }
        void HideToTray() {
            StopMorph(); E.S.Save(); Hide();
            // hidden in the tray it is a background process again: hand back the pages WPF cached while it was on
            // screen. The pages are rebuilt from the same visual tree on the next show, so nothing is lost.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)delegate {
                if (IsVisible) return;
                try { GC.Collect(2, GCCollectionMode.Optimized); SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
            });
        }
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
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
            try { if (trayTempIcon != null) { IntPtr h = trayTempIcon.Handle; trayTempIcon.Dispose(); DestroyIcon(h); } } catch { }
            try { E.Dispose(); } catch { }
            try { sensors.Dispose(); } catch { }
            Log.Write("exit");
            Application.Current.Shutdown();
        }

        // ---------- transient status: a small strip over the bottom of the page ----------
        void ShowToast(string msg, bool err) {
            if (E.GuardActive && !err) return;
            txtToast.Text = msg;
            txtToast.Foreground = err ? Ui.Brush(Ui.Danger) : Ui.TextB;
            Ui.Fade(toast, 0.92, 140);
            if (toastTimer == null) {
                toastTimer = new DispatcherTimer();
                toastTimer.Tick += delegate { toastTimer.Stop(); Ui.Fade(toast, 0, 260); };
            }
            toastTimer.Interval = TimeSpan.FromMilliseconds(err ? 4200 : 1900);
            toastTimer.Stop(); toastTimer.Start();
        }

        // ---------- autostart (scheduled task with highest privileges = no UAC prompt at logon) ----------
        void QueryAutostartAsync() {
            Slow(delegate {
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
