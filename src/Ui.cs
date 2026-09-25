// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman: WPF main window (layout in the embedded Ui.xaml): a rail on the left with Home, Fans, Keyboard and
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
        const double RailW = 62, PageW = 450, KbdPageW = 638, SettingsH = 640;   // 450: four modes (Unleashed) without clipping "Performance"

        Osd osd;
        public void Flash(string title, string detail, int modeIndex) {
            if (osd == null) osd = new Osd();
            string[] paths = { LEAF, SCALE, BOLT, FLAME };
            osd.Flash(title, detail, modeIndex >= 0 ? Ui.ModeColor(modeIndex) : Ui.Accent.Color, modeIndex >= 0 ? paths[modeIndex] : FAN);
        }
        const string FAN = "M12 12 C9.6 8.4 8.4 5 10.3 2 C14.6 2.4 15.9 6.6 12 9 M12 12 C15.3 13.2 17.6 16.2 16.6 19.6 C12.5 20.6 9.4 17.6 12 15 M12 12 C11.1 15.4 8 17.8 4.6 16.4 C4 12.2 7.2 9.8 12 9";
        const string LEAF = "M4 20 C4 11 10 4 20 4 C20 13 14 20 4 20 Z M4 20 L13 11";
        const string SCALE = "M12 3 L12 21 M8 21 L16 21 M4 7 L20 7 M4 7 L1.5 13 A2.5 2 0 0 0 6.5 13 Z M20 7 L17.5 13 A2.5 2 0 0 0 22.5 13 Z";
        const string BOLT = "M13 2 L4 14 L11 14 L10 22 L20 9 L13 9 Z";
        const string FLAME = "M12 2 C13 6 18 8.5 18 14 A6 6 0 0 1 6 14 C6 10.5 8.5 9 9 5.5 C10.8 7.2 11 9 11.2 10.5 C12.8 8.8 12.8 5.5 12 2 Z";
        const string ICO_HOME_RING = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 Z";
        const string ICO_HOME_DOT = "M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z";
        const string ICO_UPDATE = "M12 4 V14.2 M7.6 10.2 L12 14.8 L16.4 10.2 M4.6 19 H19.4";
        const string ICO_FANS = "M3 8.5 C5.5 5.5 8.5 11.5 12 8.5 C15.5 5.5 18.5 11.5 21 8.5 M3 15.5 C5.5 12.5 8.5 18.5 12 15.5 C15.5 12.5 18.5 18.5 21 15.5";
        const string ICO_KBD = "M2.5 7.5 A2 2 0 0 1 4.5 5.5 H19.5 A2 2 0 0 1 21.5 7.5 V16.5 A2 2 0 0 1 19.5 18.5 H4.5 A2 2 0 0 1 2.5 16.5 Z M6 9.5 H6.4 M9.8 9.5 H10.2 M13.6 9.5 H14 M17.4 9.5 H17.8 M6 12.5 H6.4 M9.8 12.5 H10.2 M13.6 12.5 H14 M17.4 12.5 H17.8 M7.5 15.5 H16.5";

        readonly Engine E;
        readonly Sensors sensors;
        readonly string screenshotPath;
        readonly bool openSettings;
        FrameworkElement root;
        Grid pageHost;
        ScrollViewer scroll;
        readonly FrameworkElement[] pages = new FrameworkElement[4];
        Page cur = Page.Home;
        bool pageShown;
        readonly NavBtn[] nav = new NavBtn[4];
        Border railPill;
        TranslateTransform railPillT;
        Canvas railCanvas;
        FrameworkElement rail, logoHost;
        StackPanel navBottom;
        ColorSource accentSrc;
        SolidColorBrush accent;
        // home
        TextBlock txtHomeTitle, txtHomeStatus, subCpu, subGpu, txtPower, txtFoot, txtFootRight, txtLightSub, txtErr, txtInfo, btnSupport, btnReset;
        Run bigCpu, bigGpu, bigFan1, bigFan2;
        FrameworkElement demoBadge, errBanner, infoBanner, powerRow, lightRow;
        Border miniHost, infoClose;
        Ellipse dotHb;
        Seg modeSeg;
        LinkSeg fanLinks;
        Slider slPower;
        // fans
        Seg fanSeg, stopAfterSeg;
        TextBlock txtFansStatus, txtCurveTitle, txtCurveHint, txtFan1, txtFan2, txtFanApplied, txtFanRight, btnFanAction, txtFloor, txtRamp, txtGuardNote;
        TextBlock maxFan1Sub, maxFan2Sub, maxTempSub, maxMinsSub, manSub1, manSub2;
        Run maxFan1, maxFan2, maxTemp, maxMins, maxMinsUnit, manPct1, manPct2;
        FrameworkElement curveBlock, maxBlock, manualBlock, optsAuto, optsCurve, optsMax, optsManual;
        Border curveWhichHost;
        LinkSeg curveWhich;
        CurveView curveView;
        Slider slFan1, slFan2, slFloor, slRamp;
        ToggleButton tgLink, tgEcoCool2, tgMaxCool, tgManualLink;
        bool curveGpu;
        // keyboard
        LinkSeg kbdModes;
        Seg granSeg;
        Border kbdHost, hexChip;
        TextBlock txtKbdStatus, txtKeySel, txtSpeed, txtLevel, txtLevel2, txtKbdInfo, btnWinLighting;
        FrameworkElement selectRow, colorEditor, effectEditor, kbdInfo, levelInline;
        Slider slSpeed, slLevel, slLevel2;
        TextBox txtHex;
        StripPicker hueBar, shadeBar;
        KeyboardView kbdMini, kbdBig;
        DispatcherTimer colorDebounce, levelDebounce, speedDebounce, floorDebounce;
        bool miniNeedsFrame;
        string gran = "Zone";
        Rgb curColor;
        bool hexTyping;
        // settings
        Seg keySeg, gfxSeg, hzSeg, gpuSeg, pollSeg;
        TextBlock txtMachine, txtKeyInfo, txtGfxSub, txtGpuSub, txtDiag, txtUpdate, txtUpdateTitle, btnLearn, btnUpdate, btnDiag, btnLog, btnExit;
        // driver
        TextBlock txtDriverTitle, txtDriverSub, btnDriver, txtDriverNudge, txtDriverNudgeSub, btnDriverNudge;
        ToggleButton tgDriver;
        FrameworkElement driverBanner, driverClose, driverRow;
        Run runCpuHead, runCpuWatts, runCpuTail;    // the CPU caption in three pieces, so the limits can hang off the wattage alone
        string cpuLimitsTip = "?";
        bool cpuTipFromDriver;
        enum DriverState { Busy, NotInstalled, RestartPending, Off, Outdated, Ready, Broken }
        DriverState driverState = DriverState.NotInstalled;   // what the row is showing, so the click knows what it means
        Brush subCpuBrush;
        string driverRowFor = "?";          // the state the row was last built for, so Refresh does not rebuild it every tick
        Border updateRow;
        string updateTitleFor = "?";        // staged version the title was last built for ("" = none); "?" = not built yet
        StackPanel updateText;
        NavBtn navUpdate;
        FrameworkElement keyCmdRow, gfxRow, hzRow, lowHzRow, gpuRow;
        TextBox txtKeyCmd;
        Ellipse keyDot;
        ToggleButton tgSuppress, tgHotkeys, tgAutostart, tgEcoBattery, tgSyncPower, tgLowHzBattery, tgTrayTemp, tgGuard, tgUpdateAuto;
        // CPU power limits: the Settings switch, the experimental Unleashed PL2 switch, and its slider on Home
        FrameworkElement cpuLimitsRow, pl2ExpRow, pl2Row, pl1Row, ecoPl1Row, ecoHzRow;
        ToggleButton tgCpuLimits, tgPl2Exp, tgEcoHz;
        TextBlock txtCpuLimitsSub, txtPl2ExpSub, txtPl2, txtPl1, txtEcoPl1;
        Slider slPl2, slPl1, slEcoPl1;
        DispatcherTimer pl2Debounce, pl1Debounce, ecoPl1Debounce;
        // hotkeys
        Button btnHotkeys;
        TextBlock txtHotkeysSub;
        StackPanel hotkeyPanel;
        bool hotkeysOpen;
        int listening = -1;                                     // the action waiting for a key press, or -1
        readonly bool[] hotkeyBusy = new bool[HotkeyTable.Count];   // Windows refused this one: another program has it
        string hotkeySubFor = "?";
        TextBlock listeningCap;                                 // the "Press keys" cap while listening, so held modifiers can show in it
        // thermal guard: the rule as a sentence with the numbers in it
        WrapPanel guardLine;
        Button btnGuard;
        bool guardOpen;
        ValueLink chipCpu, chipChassis, chipFans, chipHold;
        int cpuLo = 70, chassisLo = 40;                             // the first entry of each temperature list
        static readonly int[] HoldChoices = { 30, 60, 120, 180, 300 };
        int[] holdChoices = HoldChoices;                        // plus the stored value when it is not one of these
        int[] guardLevels = new int[0];                         // what each entry of the fans dropdown means, 0 = max
        DispatcherTimer guardDebounce;
        int lastChassis = -1, cpuTipFor = -1, chassisTipFor = -1;
        TextBlock txtGuardSub, txtMaxCoolSub, txtKeyCmdHint;
        Button btnClose;
        Border toast;
        TextBlock txtToast;
        readonly List<WF.ToolStripMenuItem> trayHz = new List<WF.ToolStripMenuItem>();
        WF.NotifyIcon tray;
        readonly WF.ToolStripMenuItem[] trayModes = new WF.ToolStripMenuItem[Settings.MaxModes];
        readonly SD.Icon[] icons = new SD.Icon[Settings.MaxModes];
        readonly BitmapSource[] appIcons = new BitmapSource[Settings.MaxModes];
        bool syncing, exiting, reading, autostart;
        int shownMode = -1, lastBiosTemp = -1;
        int[] lastFans;
        readonly List<double> tempTrail = new List<double>();
        DispatcherTimer uiTimer, fanDebounce, powerDebounce, curveDebounce, learnTimer, toastTimer;
        HwndSource src;
        IntPtr hwnd;
        bool hotkeysRegistered;
        bool onBattery;                                    // last known power source; PollRate uses it

        static readonly string[] ModeSubs = {
            "Windows efficiency mode · GPU base power",
            "Default thermal policy · GPU boost",
            "Performance thermal policy · GPU max",
            "Unleashed thermal policy · GPU max"
        };

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, VK_F11 = 0x7A;   // not F12: Windows reserves it for the debugger
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        const uint MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public MainWindow(Engine engine, Sensors s, string screenshot, bool settingsOpen) {
            E = engine;
            sensors = s;
            screenshotPath = screenshot;
            openSettings = settingsOpen;
            Ui.LoadFonts();
            Title = Program.DisplayName;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanMinimize;
            Background = Ui.Card;
            Width = RailW + PageW;
            Height = 600;
            SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = true;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            root = LoadXaml();
            Content = root;
            root.Resources["UiFont"] = Ui.UiFont;
            root.Resources["MonoFont"] = Ui.MonoFont;
            // the live accent: a bound brush (unfreezable) behind the XAML's DynamicResource references and the code-built controls
            accentSrc = new ColorSource { Color = Ui.ModeColor(E.ModeIndex) }; accent = accentSrc.MakeBrush();
            root.Resources["Accent"] = accent;
            Ui.Accent = accent;
            FindAll();
            Bounds();
            BuildRail();
            BuildHome();
            BuildFans();
            BuildKeyboard();
            BuildSettings();
            BuildIcons();
            BuildTray();
            Wire();
            Position();
            GuardText();
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
            Application.Current.SessionEnding += delegate { quietExit = true; ExitApp(); };   // logoff/shutdown: leave cleanly, and quietly
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
            StartShowListener();
            // Build the window handle now rather than on first Show. Started from the logon task this window
            // may never be shown at all, and without a handle there is nowhere to register a hotkey or to hook
            // WM_HOTKEY: every hotkey was silently dead after a reboot.
            new WindowInteropHelper(this).EnsureHandle();
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
            rail = F<FrameworkElement>("Rail");
            railCanvas = F<Canvas>("RailCanvas");
            railPill = F<Border>("RailPill");
            railPillT = (TranslateTransform)railPill.RenderTransform;
            logoHost = F<FrameworkElement>("LogoHost");
            F<Border>("Mark").Background = accentSrc.MakeGradient();
            scroll = F<ScrollViewer>("Scroll");
            pageHost = F<Grid>("PageHost");
            pages[0] = F<FrameworkElement>("PageHome");
            pages[1] = F<FrameworkElement>("PageFans");
            pages[2] = F<FrameworkElement>("PageKbd");
            pages[3] = F<FrameworkElement>("PageSettings");
            txtHomeTitle = F<TextBlock>("TxtHomeTitle");
            txtHomeStatus = F<TextBlock>("TxtHomeStatus");
            demoBadge = F<FrameworkElement>("DemoBadge");
            errBanner = F<FrameworkElement>("ErrBanner");
            txtErr = F<TextBlock>("TxtErr");
            infoBanner = F<FrameworkElement>("InfoBanner");
            txtInfo = F<TextBlock>("TxtInfo");
            infoClose = F<Border>("InfoClose");
            infoClose.MouseLeftButtonUp += delegate {
                E.S.InfoDismissed = true; try { E.S.Save(); } catch { }
                infoBanner.Visibility = Visibility.Collapsed;
                Remeasure(cur);   // the page just got shorter
            };
            bigCpu = F<Run>("BigCpu");
            bigGpu = F<Run>("BigGpu");
            subCpu = F<TextBlock>("SubCpu");
            subGpu = F<TextBlock>("SubGpu");
            bigFan1 = F<Run>("BigFan1");
            bigFan2 = F<Run>("BigFan2");
            powerRow = F<FrameworkElement>("PowerRow");
            slPower = F<Slider>("SlPower");
            txtPower = F<TextBlock>("TxtPower");
            pl2Row = F<FrameworkElement>("Pl2Row");
            pl1Row = F<FrameworkElement>("Pl1Row");
            slPl1 = F<Slider>("SlPl1");
            txtPl1 = F<TextBlock>("TxtPl1");
            ecoPl1Row = F<FrameworkElement>("EcoPl1Row");
            slEcoPl1 = F<Slider>("SlEcoPl1");
            txtEcoPl1 = F<TextBlock>("TxtEcoPl1");
            ecoHzRow = F<FrameworkElement>("EcoHzRow");
            tgEcoHz = F<ToggleButton>("TgEcoHz");
            slPl2 = F<Slider>("SlPl2");
            txtPl2 = F<TextBlock>("TxtPl2");
            cpuLimitsRow = F<FrameworkElement>("CpuLimitsRow");
            pl2ExpRow = F<FrameworkElement>("Pl2ExpRow");
            tgCpuLimits = F<ToggleButton>("TgCpuLimits");
            tgPl2Exp = F<ToggleButton>("TgPl2Exp");
            txtCpuLimitsSub = F<TextBlock>("TxtCpuLimitsSub");
            txtPl2ExpSub = F<TextBlock>("TxtPl2ExpSub");
            lightRow = F<FrameworkElement>("LightRow");
            txtLightSub = F<TextBlock>("TxtLightSub");
            miniHost = F<Border>("MiniHost");
            dotHb = F<Ellipse>("DotHb");
            txtFoot = F<TextBlock>("TxtFoot");
            txtFootRight = F<TextBlock>("TxtFootRight");
            txtFansStatus = F<TextBlock>("TxtFansStatus");
            curveBlock = F<FrameworkElement>("CurveBlock");
            txtCurveTitle = F<TextBlock>("TxtCurveTitle");
            curveWhichHost = F<Border>("CurveWhichHost");
            txtCurveHint = F<TextBlock>("TxtCurveHint");
            maxBlock = F<FrameworkElement>("MaxBlock");
            maxFan1 = F<Run>("MaxFan1");
            maxFan1Sub = F<TextBlock>("MaxFan1Sub");
            maxFan2 = F<Run>("MaxFan2");
            maxFan2Sub = F<TextBlock>("MaxFan2Sub");
            maxTemp = F<Run>("MaxTemp");
            maxTempSub = F<TextBlock>("MaxTempSub");
            maxMins = F<Run>("MaxMins");
            maxMinsUnit = F<Run>("MaxMinsUnit");
            maxMinsSub = F<TextBlock>("MaxMinsSub");
            manualBlock = F<FrameworkElement>("ManualBlock");
            manPct1 = F<Run>("ManPct1");
            manSub1 = F<TextBlock>("ManSub1");
            manPct2 = F<Run>("ManPct2");
            manSub2 = F<TextBlock>("ManSub2");
            optsAuto = F<FrameworkElement>("OptsAuto");
            tgEcoCool2 = F<ToggleButton>("TgEcoCool2");
            optsCurve = F<FrameworkElement>("OptsCurve");
            tgLink = F<ToggleButton>("TgLink");
            slFloor = F<Slider>("SlFloor");
            txtFloor = F<TextBlock>("TxtFloor");
            slRamp = F<Slider>("SlRamp");
            txtRamp = F<TextBlock>("TxtRamp");
            optsMax = F<FrameworkElement>("OptsMax");
            tgMaxCool = F<ToggleButton>("TgMaxCool");
            optsManual = F<FrameworkElement>("OptsManual");
            slFan1 = F<Slider>("SlFan1");
            slFan2 = F<Slider>("SlFan2");
            txtFan1 = F<TextBlock>("TxtFan1");
            txtFan2 = F<TextBlock>("TxtFan2");
            tgManualLink = F<ToggleButton>("TgManualLink");
            txtGuardNote = F<TextBlock>("TxtGuardNote");
            txtFanApplied = F<TextBlock>("TxtFanApplied");
            txtFanRight = F<TextBlock>("TxtFanRight");
            btnFanAction = F<TextBlock>("BtnFanAction");
            txtKbdStatus = F<TextBlock>("TxtKbdStatus");
            selectRow = F<FrameworkElement>("SelectRow");
            txtKeySel = F<TextBlock>("TxtKeySel");
            kbdHost = F<Border>("KbdHost");
            colorEditor = F<FrameworkElement>("ColorEditor");
            effectEditor = F<FrameworkElement>("EffectEditor");
            kbdInfo = F<FrameworkElement>("KbdInfo");
            hexChip = F<Border>("HexChip");
            txtHex = F<TextBox>("TxtHex");
            slLevel = F<Slider>("SlLevel");
            txtLevel = F<TextBlock>("TxtLevel");
            levelInline = F<FrameworkElement>("LevelInline");
            slSpeed = F<Slider>("SlSpeed");
            txtSpeed = F<TextBlock>("TxtSpeed");
            slLevel2 = F<Slider>("SlLevel2");
            txtLevel2 = F<TextBlock>("TxtLevel2");
            txtKbdInfo = F<TextBlock>("TxtKbdInfo");
            btnWinLighting = F<TextBlock>("BtnWinLighting");
            txtMachine = F<TextBlock>("TxtMachine");
            txtKeyInfo = F<TextBlock>("TxtKeyInfo");
            keyDot = F<Ellipse>("KeyDot");
            btnLearn = F<TextBlock>("BtnLearn");
            keyCmdRow = F<FrameworkElement>("KeyCmdRow");
            txtKeyCmd = F<TextBox>("TxtKeyCmd");
            gfxRow = F<FrameworkElement>("GfxRow");
            txtGfxSub = F<TextBlock>("TxtGfxSub");
            hzRow = F<FrameworkElement>("HzRow");
            lowHzRow = F<FrameworkElement>("LowHzRow");
            gpuRow = F<FrameworkElement>("GpuRow");
            txtGpuSub = F<TextBlock>("TxtGpuSub");
            tgSuppress = F<ToggleButton>("TgSuppress");
            tgHotkeys = F<ToggleButton>("TgHotkeys");
            btnHotkeys = F<Button>("BtnHotkeys");
            txtHotkeysSub = F<TextBlock>("TxtHotkeysSub");
            hotkeyPanel = F<StackPanel>("HotkeyPanel");
            guardLine = F<WrapPanel>("GuardLine");
            btnGuard = F<Button>("BtnGuard");
            tgEcoBattery = F<ToggleButton>("TgEcoBattery");
            tgLowHzBattery = F<ToggleButton>("TgLowHzBattery");
            tgSyncPower = F<ToggleButton>("TgSyncPower");
            tgTrayTemp = F<ToggleButton>("TgTrayTemp");
            tgAutostart = F<ToggleButton>("TgAutostart");
            tgGuard = F<ToggleButton>("TgGuard");
            tgUpdateAuto = F<ToggleButton>("TgUpdateAuto");
            txtGuardSub = F<TextBlock>("TxtGuardSub");
            txtMaxCoolSub = F<TextBlock>("TxtMaxCoolSub");
            txtKeyCmdHint = F<TextBlock>("TxtKeyCmdHint");
            txtUpdate = F<TextBlock>("TxtUpdate");
            btnUpdate = F<TextBlock>("BtnUpdate");

            txtUpdateTitle = F<TextBlock>("TxtUpdateTitle");
            updateRow = F<Border>("UpdateRow");
            updateText = F<StackPanel>("UpdateText");
            btnDiag = F<TextBlock>("BtnDiag");
            btnSupport = F<TextBlock>("BtnSupport");
            btnReset = F<TextBlock>("BtnReset");
            btnLog = F<TextBlock>("BtnLog");
            btnExit = F<TextBlock>("BtnExit");
            txtDiag = F<TextBlock>("TxtDiag");
            txtDriverTitle = F<TextBlock>("TxtDriverTitle");
            txtDriverSub = F<TextBlock>("TxtDriverSub");
            btnDriver = F<TextBlock>("BtnDriver");
            tgDriver = F<ToggleButton>("TgDriver");
            driverBanner = F<FrameworkElement>("DriverBanner");
            txtDriverNudge = F<TextBlock>("TxtDriverNudge");
            txtDriverNudgeSub = F<TextBlock>("TxtDriverNudgeSub");
            btnDriverNudge = F<TextBlock>("BtnDriverNudge");
            driverClose = F<FrameworkElement>("DriverClose");
            driverRow = F<FrameworkElement>("DriverRow");
            subCpuBrush = subCpu.Foreground;
            btnClose = F<Button>("BtnClose");
            toast = F<Border>("Toast");
            txtToast = F<TextBlock>("TxtToast");
            btnExit.Text = "Exit " + Program.DisplayName;
            foreach (string n in new[] { "SecKey", "SecPower", "SecDisplay", "SecDriver", "SecApp" }) Track(n);
        }
        /// <summary>Swap a placeholder TextBlock for the letter-spaced section label the design uses.</summary>
        void Track(string name) {
            var tb = F<TextBlock>(name);
            var parent = tb.Parent as Panel;
            if (parent == null) return;
            int i = parent.Children.IndexOf(tb);
            var t = new Tracked { Text = tb.Text, Margin = tb.Margin, HorizontalAlignment = HorizontalAlignment.Left };
            parent.Children.RemoveAt(i);
            parent.Children.Insert(i, t);
        }

        /// <summary>Every slider's range comes from the profile, and must be set before anything is wired to it:
        /// changing Maximum coerces Value, which raises ValueChanged, which would look like the user moving it.</summary>
        void Bounds() {
            slFan1.Minimum = slFan2.Minimum = 0;
            slFan1.Maximum = slFan2.Maximum = E.P.Curve.Ceiling;
            slFloor.Minimum = E.P.Curve.Floor;
            slFloor.Maximum = E.P.Curve.Floor + (E.P.Curve.Ceiling - E.P.Curve.Floor) * 2 / 3;
            slPower.Maximum = E.MaxOffset;
            if (E.HasUnleashedSliders) {
                slPl1.Minimum = E.P.UnlPl1Range[0]; slPl1.Maximum = E.P.UnlPl1Range[1];
                slPl2.Minimum = E.P.UnlPl2Range[0]; slPl2.Maximum = E.P.UnlPl2Range[1];
            }
            if (E.HasEcoSlider) { slEcoPl1.Minimum = E.P.EcoPl1Range[0]; slEcoPl1.Maximum = E.P.EcoPl1Range[1]; }
        }
        /// <summary>DragMove runs its own modal move loop; a morph started inside it would fight USER for the position,
        /// so content changes during a drag just resize at the end.</summary>
        bool dragging;
        void Drag() {
            dragging = true;
            try { DragMove(); } catch { } finally { dragging = false; }
        }
        void BuildRail() {
            var host = F<StackPanel>("NavHost");
            var bottom = F<StackPanel>("NavBottom");
            navBottom = bottom;
            nav[0] = new NavBtn(0, "Home", new[] { ICO_HOME_RING }, new[] { ICO_HOME_DOT });
            nav[1] = new NavBtn(1, "Fans", new[] { ICO_FANS }, new string[0]);
            nav[2] = new NavBtn(2, "Keyboard", new[] { ICO_KBD }, new string[0]);
            nav[3] = new NavBtn(3, "Settings", new string[0], new[] { GearPath(12, 12, 10, 7.6, 8, 3.4) });
            for (int i = 0; i < 4; i++) { nav[i].HorizontalAlignment = HorizontalAlignment.Center; nav[i].Clicked += delegate(int idx) { Navigate((Page)idx, true); }; }
            host.Children.Add(nav[0]);
            host.Children.Add(nav[1]);
            host.Children.Add(nav[2]);
            // Above Settings, and only while there is something to install. It is a signpost rather than a page:
            // it goes where the update lives instead of doing anything itself, so nothing is one stray click from
            // restarting the app. It stays out of nav[] deliberately -- the rail pill tracks the current page, and
            // this button never is one.
            navUpdate = new NavBtn(-1, "Update available", new[] { ICO_UPDATE }, new string[0]);
            navUpdate.HorizontalAlignment = HorizontalAlignment.Center;
            navUpdate.Accent = true;
            navUpdate.Visibility = Visibility.Collapsed;
            navUpdate.Clicked += delegate { ShowUpdateRow(); };
            bottom.Children.Add(navUpdate);
            bottom.Children.Add(nav[3]);
            if (E.Light == null) nav[2].Visibility = Visibility.Collapsed;
            logoHost.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; Navigate(Page.Home, true); };
            rail.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) Drag(); };
            rail.SizeChanged += delegate { if (!morphing) PlaceRailPill(false); };   // the morph moves the rail every frame; the pill is aimed at the end position instead
        }
        /// <summary>An 8-tooth gear as path data (even-odd fill with the hole).</summary>
        static string GearPath(double cx, double cy, double rOut, double rIn, int teeth, double hole) {
            var sb = new System.Text.StringBuilder();
            double step = 2 * Math.PI / teeth;
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
            var b = nav[(int)cur];
            if (b.ActualWidth <= 0 || railCanvas.ActualWidth <= 0) return;
            Point p = b.TranslatePoint(new Point(0, 0), railCanvas);
            if (morphing && b.Parent == navBottom) p.Y += (mt[3] - mt[1]) - rail.ActualHeight;   // bottom group: where it will be when the morph lands
            if (railPill.Opacity == 0 || !animate) { railPillT.BeginAnimation(TranslateTransform.XProperty, null); railPillT.BeginAnimation(TranslateTransform.YProperty, null); railPillT.X = p.X; railPillT.Y = p.Y; railPill.Opacity = 1; return; }
            Ui.Glide(railPillT, TranslateTransform.XProperty, p.X, 360, true);
            Ui.Glide(railPillT, TranslateTransform.YProperty, p.Y, 360, true);
        }

        void BuildHome() {
            // Unleashed is a fourth segment only on boards whose profile has its byte; everywhere else the row is unchanged.
            string[] modeNames = new string[E.ModeCount];
            Array.Copy(Engine.ModeNames, modeNames, modeNames.Length);
            modeSeg = new Seg(modeNames, ModeSubs, null, Seg.Kind.Page);
            F<Border>("ModeHost").Child = modeSeg;
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
            // The CPU caption is three runs rather than one string: the power limits belong to the wattage and
            // nothing else on the page, so they hang off that piece of it and stay out of everybody's way.
            runCpuHead = new Run("CPU");
            runCpuWatts = new Run();
            runCpuTail = new Run();
            subCpu.Inlines.Clear();
            subCpu.Inlines.Add(runCpuHead);
            subCpu.Inlines.Add(runCpuWatts);
            subCpu.Inlines.Add(runCpuTail);
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
                E.S.Fan = m;
                RefreshFans(true);            // the page follows at once; the engine's own Changed arrives after the write
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
                int[] v = E.VendorCurveAt(curveGpu);
                bool gpu = curveGpu;
                curveView.Levels = (int[])v.Clone();
                curveView.Repaint();
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
                int f = (int)slFloor.Value;
                txtFloor.Text = f <= E.P.Curve.Floor ? "off" : Pct(f);
                curveView.UserFloor = f <= E.P.Curve.Floor ? 0 : f;
                curveView.Repaint();
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
                txtFan1.Text = Pct(f1);
                txtFan2.Text = Pct(f2);
                manPct1.Text = Pct(f1).TrimEnd('%');
                manPct2.Text = Pct(f2).TrimEnd('%');
                manSub1.Text = E.Rpm(f1) + " · CPU fan";
                manSub2.Text = E.Rpm(f2) + " · GPU fan";
                if (!was && E.S.Fan == FanMode.Manual) { fanDebounce.Stop(); fanDebounce.Start(); }
            };
            slFan1.ValueChanged += fanSlid;
            slFan2.ValueChanged += fanSlid;
        }

        void BuildSettings() {
            keySeg = new Seg(Choice.Key, new[] { "Next mode", "Open this window", "Toggle max fan", "Start a command of your choice", null }, null, Seg.Kind.Compact);
            F<Border>("KeySegHost").Child = keySeg;
            keySeg.Picked += delegate(int i) {
                KeyAction a = Choice.KeyActions[i];
                E.SetKey(a);
                keyCmdRow.Visibility = a == KeyAction.Run ? Visibility.Visible : Visibility.Collapsed;
            };
            gpuSeg = new Seg(new[] { "Base", "Boost", "Auto" },
                new[] { E.HasUnleashed ? "Performance and Unleashed request cTGP without borrowing; Eco and Balanced use the standard limit" : "Performance requests cTGP without borrowing; Eco and Balanced use the standard limit",
                        "Uses cTGP and lets the GPU borrow power from the CPU",
                        E.HasUnleashed ? "Base in Eco and Balanced, Boost in Performance and Unleashed" : "Base in Eco and Balanced, Boost in Performance" }, null, Seg.Kind.Row);
            F<Border>("GpuSegHost").Child = gpuSeg;
            // Somebody watching clocks under load wants them to move; somebody on battery does not want the cost.
            // Only affects the open window: hidden, the rate is still decided by what is actually waiting on it.
            var pollMs = new[] { 500, 1000, 2000 };
            pollSeg = new Seg(new[] { "0.5s", "1s", "2s" },
                new[] { "Updates twice a second", "Updates every second", "The default" }, null, Seg.Kind.Row);
            F<Border>("PollSegHost").Child = pollSeg;
            pollSeg.Picked += delegate(int i) {
                if (i < 0 || i >= pollMs.Length) return;
                E.S.PollMs = pollMs[i];
                E.S.Save();
                PollRate();
            };
            gpuSeg.Picked += delegate(int i) {
                if (i == 2) Bg(delegate { E.SetGpu(E.S.Gpu, true, false); });
                else { GpuLevel g = (GpuLevel)i; Bg(delegate { E.SetGpu(g, false, false); }); }
            };
            if (!E.P.HasGpuPower) gpuRow.Visibility = Visibility.Collapsed;
            txtKeyCmd.LostFocus += delegate { E.SetKeyCommand(txtKeyCmd.Text); };
            txtKeyCmd.TextChanged += delegate { txtKeyCmdHint.Visibility = txtKeyCmd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            txtKeyCmd.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Enter) { E.SetKeyCommand(txtKeyCmd.Text); ShowToast("OMEN key runs: " + (E.S.KeyCommand.Length > 0 ? E.S.KeyCommand : "(nothing)"), false); } };
            BuildRefreshRates();
            BuildGraphicsModes();
            learnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            learnTimer.Tick += delegate { learnTimer.Stop(); E.Learning = false; UpdateKeyStatus(); };
            btnLearn.MouseLeftButtonUp += delegate { E.Learning = true; keyDot.Fill = Ui.Brush(Ui.Warn); txtKeyInfo.Text = "press the OMEN key now… (10 s)"; learnTimer.Stop(); learnTimer.Start(); };

            OnSwitch(tgHotkeys, delegate(bool on) { StopListening(); Bg(delegate { E.SetHotkeys(on); }); if (on) RegisterHotkeys(); else UnregisterHotkeys(); });   // a capture in progress ends first, or its bindings would be registered under it
            btnHotkeys.Click += delegate { ToggleHotkeyPanel(); };
            btnHotkeys.Content = PencilGlyph(false);
            PreviewKeyUp += delegate { ShowHeldModifiers(); };
            BuildGuardLine();
            // The sentence is the row. Its values are grey words until the pencil, then the accent and a list on
            // click; the tick greys them again. Same pencil and tick as the hotkeys row.
            txtGuardSub.Visibility = Visibility.Collapsed;
            guardLine.Visibility = Visibility.Visible;
            btnGuard.Content = PencilGlyph(false);
            btnGuard.ToolTip = "Change the rule";
            btnGuard.Click += delegate {
                guardOpen = !guardOpen;
                btnGuard.Content = PencilGlyph(guardOpen);
                btnGuard.ToolTip = guardOpen ? "Done" : "Change the rule";
                foreach (ValueLink v in new[] { chipCpu, chipChassis, chipFans, chipHold }) v.Editable = guardOpen;
            };
            guardDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            guardDebounce.Tick += delegate {
                guardDebounce.Stop();
                int cpu = cpuLo + chipCpu.Index, ch = chassisLo + chipChassis.Index, lvl = guardLevels[chipFans.Index], hold = holdChoices[chipHold.Index];
                Bg(delegate { E.SetGuardLimits(cpu, ch, lvl, hold); });
            };
            PreviewKeyDown += OnHotkeyCapture;
            Deactivated += delegate { StopListening(); };     // another window took the keyboard; the keys are not coming here
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
            OnSwitch(tgCpuLimits, delegate(bool on) { Bg(delegate { E.SetCpuLimits(on); }); });
            OnSwitch(tgPl2Exp, delegate(bool on) {
                if (on && !ConfirmExperimentalLimits()) { Synced(delegate { tgPl2Exp.IsChecked = false; }); return; }
                Bg(delegate { E.SetUnleashedCustom(on); });
            });
            OnSwitch(tgEcoHz, delegate(bool on) { Bg(delegate { E.SetEcoLowHz(on); }); });
            OnSwitch(tgDriver, delegate(bool on) { Bg(delegate { E.SetDriverUse(on); }); });
            // One link, whose meaning is the row's state: install, update, restart, troubleshoot, remove.
            btnDriver.MouseLeftButtonUp += delegate { DriverAction(); };
            btnDriverNudge.MouseLeftButtonUp += delegate { InstallDriver(); };
            driverClose.MouseLeftButtonUp += delegate { E.DismissDriverNudge(); driverBanner.Visibility = Visibility.Collapsed; Remeasure(cur); };

            // The right-hand link is whatever is left to say: the changelog once the title is doing the
            // installing, the release page while there is only news of a build, and the check itself otherwise.
            btnUpdate.MouseLeftButtonUp += delegate {
                if (E.Staged != null || E.UpdateAvailable) { OpenReleases(); return; }
                txtUpdate.Text = "checking…"; Slow(delegate { E.CheckForUpdate(true); });
            };
            updateText.MouseLeftButtonUp += delegate { if (E.Staged != null) DoUpdate(); };
            btnDiag.MouseLeftButtonUp += delegate {
                if (txtDiag.Visibility == Visibility.Visible) { txtDiag.Visibility = Visibility.Collapsed; return; }
                txtDiag.Text = "running…";
                txtDiag.Visibility = Visibility.Visible;
                Slow(delegate { string d = E.Diagnostics(); Log.Write(d); Dispatcher.BeginInvoke((Action)delegate { txtDiag.Text = d.TrimEnd(); }); });
            };
            // One button instead of "download the zip, find tools\, run it as administrator". Ohman is already
            // elevated, so it can ask the firmware the same questions directly, and the answer goes straight to
            // the clipboard because the next thing anyone does with it is paste it into an issue.
            btnSupport.MouseLeftButtonUp += delegate {
                btnSupport.Text = "collecting…";
                Slow(delegate {
                    string r;
                    try { r = Support.Report(E); } catch (Exception ex) { r = "support report failed: " + ex.Message; }
                    string path = "";
                    try {
                        path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Log.Path), "support-info.txt");
                        System.IO.File.WriteAllText(path, r);
                    } catch { path = ""; }
                    Dispatcher.BeginInvoke((Action)delegate {
                        btnSupport.Text = "Report Issue";
                        bool copied = false;
                        try { Clipboard.SetText(r); copied = true; } catch { }      // the clipboard is shared; it can be busy
                        txtDiag.Text = r.TrimEnd();
                        txtDiag.Visibility = Visibility.Visible;
                        Remeasure(cur);
                        ShowToast(copied ? "Copied. Paste it into the issue, or drag support-info.txt in"
                                         : "Saved as support-info.txt, drag it into the issue", !copied);
                        // Explorer first, browser second, so the issue form is the window left in front. The
                        // report is too big for a URL, so it travels as a paste or as the revealed file.
                        if (path.Length > 0)
                            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
                            catch (Exception ex) { Log.Write("could not reveal the report: " + ex.Message); }
                        try { Process.Start(new ProcessStartInfo(Support.IssueUrl(E, r)) { UseShellExecute = true }); }
                        catch (Exception ex) { Log.Write("could not open the issue form: " + ex.Message); }
                    });
                });
            };
            btnLog.MouseLeftButtonUp += delegate { try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open log: " + ex.Message, true); } };
            btnExit.MouseLeftButtonUp += delegate { ExitApp(); };
            // Somebody has to be able to leave. A tool that writes to firmware and switches off the vendor's own
            // software owes the owner a way back that does not depend on waiting for the author to ship a fix.
            btnReset.MouseLeftButtonUp += delegate {
                string ask = "Undo everything " + Program.DisplayName + " changed, then quit?\n\n"
                    + "It re-enables OMEN Gaming Hub's tasks, gives the keyboard back to Windows, puts the refresh rate back, "
                    + "sets the mode to balanced, hands the fans back to the firmware, turns the backlight on, and deletes its "
                    + "own settings and log.\n\n"
                    + "The graphics mode is left as you set it, because changing that needs a restart.\n\n"
                    + "Afterwards you can delete " + Program.AppName + ".exe and nothing of it is left behind.";
                if (MessageBox.Show(IsVisible ? (Window)this : null, ask, Program.DisplayName,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                // The driver is the one thing Ohman may have put on the machine outside its own folder. Only when
                // it was us, and only with a yes: FanControl or LibreHardwareMonitor may be using the same one.
                // Asked here, beside the other question, so that both answers are in before any work starts -
                // running somebody's uninstaller takes as long as it takes, and the thread that has to paint the
                // window is not the thread to wait for it on.
                bool alsoDriver = E.S.DriverInstalledByOhman && !E.Hw.IsDemo && E.DriverInstalled
                    && MessageBox.Show(IsVisible ? (Window)this : null,
                        "Also remove the driver " + Program.DisplayName + " installed?\n\nSay No if another program (FanControl, LibreHardwareMonitor) uses it.",
                        Program.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                btnReset.Text = "resetting...";
                Slow(delegate {
                    string what = "";
                    try { what = E.FactoryReset(); } catch (Exception ex) { Log.Write("factory reset: " + ex.Message); }
                    if (alsoDriver) {
                        try { if (E.RemoveDriver(false)) what += "  - removed the driver\n"; } catch (Exception ex) { Log.Write("reset driver: " + ex.Message); }
                    }
                    Dispatcher.BeginInvoke((Action)delegate {
                        try { SetAutostart(false); } catch (Exception ex) { Log.Write("reset autostart: " + ex.Message); }
                        resetting = true;                    // ExitApp must delete the settings, not save them
                        string folder = "";
                        try { folder = System.IO.Path.GetDirectoryName(Log.Path); } catch { }
                        // The reset takes a moment and the window can be sent to the tray inside it. An
                        // unowned dialog can then open behind whatever is in front, and this one is the last
                        // thing the app says before it exits, so make sure there is a window to own it.
                        if (!IsVisible) { try { Show(); WindowState = WindowState.Normal; Activate(); } catch { } }
                        MessageBox.Show(IsVisible ? (Window)this : null,
                            (what.Length > 0 ? "Done:\n\n" + what + "\n" : "Done.\n\n")
                                + Program.DisplayName + " closes now. Delete " + Program.AppName + ".exe when it does.",
                            Program.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
                        if (folder.Length > 0)
                            try { Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true }); } catch { }
                        ExitApp();
                    });
                });
            };

            // the wheel moves a fixed, small distance and eases there; the default jumps three "lines" of a very tall panel
            scroll.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e) {
                e.Handled = true;
                scrollTo = Math.Max(0, Math.Min(scroll.ScrollableHeight, scrollTo - e.Delta / 120.0 * 54));
                if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollTick; }
            };
            scroll.ScrollChanged += delegate { if (!scrolling) scrollTo = scroll.VerticalOffset; };
        }
        double scrollTo;
        bool scrolling;
        void ScrollTick(object o, EventArgs e) {
            double at = scroll.VerticalOffset, d = scrollTo - at;
            if (Math.Abs(d) < 0.6) { scroll.ScrollToVerticalOffset(scrollTo); scrolling = false; CompositionTarget.Rendering -= ScrollTick; return; }
            scroll.ScrollToVerticalOffset(at + d * 0.28);
        }

        /// <summary>Every sentence that quotes a guard temperature reads it from the profile, so a new model only
        /// has to change GuardLimits and the words follow.</summary>
        void GuardText() {
            var g = E.P.Guard;
            SyncGuardLine();
            txtMaxCoolSub.Text = "Below " + g.MaxFanCoolBelow + "° for " + (g.MaxFanCoolSeconds / 60) + " minutes";
            txtGuardNote.Text = Program.DisplayName + " forces " + (E.GuardLevel > 0 ? E.Rpm(E.GuardLevel) : "max fan") + " above " + E.GuardCpuHot + "° CPU";
        }
        /// <summary>Switching the safety net off is the one thing in here that asks twice, wherever it is switched off from.</summary>
        bool ConfirmGuardOff() {
            return MessageBox.Show(IsVisible ? (Window)this : null,
                "Turn the thermal guard off?\n\nThe guard forces both fans to maximum when the CPU passes " + E.P.Guard.CpuHot + "°, the chassis sensor passes " + E.P.Guard.ChassisHot + "°, or the fans read stalled while the machine is warm. With it off, nothing in " + Program.DisplayName + " will step in.\n\nTurn it off?",
                Program.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        bool ConfirmExperimentalLimits() {
            return MessageBox.Show(IsVisible ? (Window)this : null,
                "Turn on the experimental power limit sliders?\n\nIn Unleashed they set the CPU's long-term limit (PL1, " + E.P.UnlPl1Range[0] + " to " + E.P.UnlPl1Range[1] + " W) and short-term limit (PL2, " + E.P.UnlPl2Range[0] + " to " + E.P.UnlPl2Range[1] + " W), the same ranges OMEN Gaming Hub offers. Above 65/80 W is more than this laptop's BIOS sets, so the CPU runs hotter and louder under load. The thermal guard stays on.\n\nHWiNFO showed a second, dynamic copy of both limits on this laptop (PL1 65 W, PL2 80 W) that neither the firmware call nor the driver can change; above those the CPU may stay capped there.\n\nTurn them on?",
                Program.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        void BuildIcons() {
            for (int i = 0; i < E.ModeCount; i++) icons[i] = MakeIcon(Ui.ModeColor(i), 32);     // tray: 32 px, the mark fills the box
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
                // Same light-to-dark ramp as the mark on the rail and on the website. The path is already rotated,
                // so the gradient is simply vertical in device space: top point light, bottom point dark. The end
                // points are pushed a little past the shape so GDI+ does not band at the extremes.
                float half = side * 0.71f;
                using (var path = RoundSquare(mid, side, radius, mid))
                using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new SD.PointF(0, mid - half - 1), new SD.PointF(0, mid + half + 1),
                        Shade(c, 0.24f), Shade(c, -0.28f)))
                    g.FillPath(b, path);
                if (label == null) return bmp;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float em = size * (label.Length >= 3 ? 0.46f : 0.68f);
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var family = new SD.FontFamily("Segoe UI")) {
                    path.AddString(label, family, (int)SD.FontStyle.Bold, em, new SD.PointF(0, 0), SD.StringFormat.GenericTypographic);
                    var bounds = path.GetBounds();
                    using (var m = new System.Drawing.Drawing2D.Matrix()) { m.Translate(mid - bounds.X - bounds.Width / 2, mid - bounds.Y - bounds.Height / 2); path.Transform(m); }
                    // White, not the card colour. On paper a dark glyph has the better contrast ratio against all
                    // three mode colours, but the tray is sixteen pixels on a dark taskbar: the diamond reads as a
                    // dark object and a dark number inside it disappears. White is what is actually legible there.
                    using (var fill = new SD.SolidBrush(SD.Color.White)) g.FillPath(fill, path);
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
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++) {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(blobs[i].Length);
                    w.Write(offset);
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
                bw.Write(40);
                bw.Write(w);
                bw.Write(h * 2);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(0);
                bw.Write(w * h * 4 + maskRow * h);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++) { var p = bmp.GetPixel(x, y); bw.Write(p.B); bw.Write(p.G); bw.Write(p.R); bw.Write(p.A); }
                bw.Write(new byte[maskRow * h]);
                bw.Flush();
                return ms.ToArray();
            }
        }
        /// <summary>Toward white for a positive amount, toward black for a negative one.</summary>
        static SD.Color Shade(SD.Color c, float t) {
            int to = t > 0 ? 255 : 0;
            float k = Math.Abs(t);
            return SD.Color.FromArgb(c.A,
                (int)Math.Round(c.R + (to - c.R) * k),
                (int)Math.Round(c.G + (to - c.G) * k),
                (int)Math.Round(c.B + (to - c.B) * k));
        }
        static System.Drawing.Drawing2D.GraphicsPath RoundSquare(float center, float side, float radius, float rotateAbout) {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float x = center - side / 2, y = center - side / 2, d = radius * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + side - d, y, d, d, 270, 90);
            p.AddArc(x + side - d, y + side - d, d, d, 0, 90);
            p.AddArc(x, y + side - d, d, d, 90, 90);
            p.CloseFigure();
            using (var m = new System.Drawing.Drawing2D.Matrix()) { m.RotateAt(45f, new SD.PointF(rotateAbout, rotateAbout)); p.Transform(m); }
            return p;
        }

        // ---------- the tray menu: everything the panel can do, without opening it ----------
        readonly List<WF.ToolStripMenuItem> trayFan = new List<WF.ToolStripMenuItem>(), trayGfx = new List<WF.ToolStripMenuItem>();
        WF.ToolStripMenuItem trayLight;
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
            for (int i = 0; i < E.ModeCount; i++) {
                int idx = i;
                var it = Item(Engine.ModeNames[i], delegate { ApplyModeAsync(idx); });
                trayModes[i] = it;
                menu.Items.Add(it);
            }
            menu.Items.Add(new WF.ToolStripSeparator());

            // Fan
            var fanMenu = new WF.ToolStripMenuItem("Fan");
            for (int i = 0; i < Choice.Fan.Length; i++) {
                FanMode m = Choice.FanModes[i];
                var it = Item(Choice.Fan[i], delegate { Bg(delegate { E.SetFan(m, E.S.Fan1, E.S.Fan2, false); }); });
                trayFan.Add(it);
                fanMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(fanMenu);

            // Screen: the refresh rate, and the graphics mode for anyone who switches between hybrid and the MUX
            var gfxMenu = new WF.ToolStripMenuItem("Screen");
            foreach (int hz in Display.Choices()) {
                int h = hz; var it = Item(hz + " Hz refresh rate", delegate { Bg(delegate { E.SetRefreshRate(h); }); }); it.Tag = hz;
                trayHz.Add(it);
                gfxMenu.DropDownItems.Add(it);
            }
            var gfxModes = new List<int>();
            foreach (int mode in new[] { 0, 1, 3 }) if (E.GpuModeOffered(mode)) gfxModes.Add(mode);
            if (gfxModes.Count >= 2) {
                gfxMenu.DropDownItems.Add(new WF.ToolStripSeparator());
                foreach (int mode in gfxModes) {
                    int m = mode;
                    var it = Item(Engine.GpuModeNames[m] + " graphics", delegate { SwitchGraphics(m); }); it.Tag = m;
                    trayGfx.Add(it);
                    gfxMenu.DropDownItems.Add(it);
                }
            }
            if (gfxMenu.DropDownItems.Count > 0) menu.Items.Add(gfxMenu);

            // Keyboard: on or off. Which effect and what colour is a decision you make once, looking at it.
            if (E.Light != null) {
                trayLight = Item("Keyboard lighting", delegate {
                    int fx = E.S.LightEffect;
                    int m = E.S.Light == 0 ? 1 : 0;          // back on in whatever mode it was last in
                    Bg(delegate { E.SetLight(m, fx, false); });
                });
                menu.Items.Add(trayLight);
            }

            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Item("Show " + Program.DisplayName, delegate { ShowPanel(); }));
            menu.Items.Add(Item("Exit", delegate { ExitApp(); }));
            menu.Opening += delegate { RefreshTray(); };
            tray = new WF.NotifyIcon { Icon = icons[1], Text = Program.DisplayName, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += delegate(object o, WF.MouseEventArgs me) { if (me.Button == WF.MouseButtons.Left) TogglePanel(); };
        }
        /// <summary>Tick what is currently true. Called when the menu opens and after every state change.</summary>
        void RefreshTray() {
            if (tray == null) return;
            var S = E.S;
            for (int i = 0; i < E.ModeCount; i++) trayModes[i].Checked = i == E.ModeIndex;
            for (int i = 0; i < trayFan.Count; i++) trayFan[i].Checked = S.Fan == Choice.FanModes[i];
            int hzNow = Display.CurrentHz();
            foreach (var it in trayHz) it.Checked = (int)it.Tag == hzNow;
            int gfx = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
            foreach (var it in trayGfx) it.Checked = (int)it.Tag == gfx;
            if (trayLight != null) trayLight.Checked = S.Light != 0;
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
            foreach (string n in new[] { "HeadHome", "HeadFans", "HeadKbd", "HeadSettings", "DragStrip" })
                F<FrameworkElement>(n).MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs me) { if (me.LeftButton == MouseButtonState.Pressed) Drag(); };
            btnClose.Click += delegate { HideToTray(); };
            powerDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            powerDebounce.Tick += delegate { powerDebounce.Stop(); int off = (int)slPower.Value; Bg(delegate { E.SetTdpOffset(off, false); }); };
            // The three limit sliders: the label follows the thumb at once, the write waits for it to settle.
            pl2Debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            pl2Debounce.Tick += delegate { pl2Debounce.Stop(); int w = (int)slPl2.Value; Bg(delegate { E.SetUnleashedPl2(w); }); };
            slPl2.ValueChanged += delegate {
                txtPl2.Text = Math.Max((int)slPl2.Value, (int)slPl1.Value) + " W";
                if (!syncing) { pl2Debounce.Stop(); pl2Debounce.Start(); }
            };
            pl1Debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            pl1Debounce.Tick += delegate { pl1Debounce.Stop(); int w = (int)slPl1.Value; Bg(delegate { E.SetUnleashedPl1(w); }); };
            slPl1.ValueChanged += delegate {
                txtPl1.Text = (int)slPl1.Value + " W";
                txtPl2.Text = Math.Max((int)slPl2.Value, (int)slPl1.Value) + " W";   // PL2 is carried along by a higher PL1
                if (!syncing) { pl1Debounce.Stop(); pl1Debounce.Start(); }
            };
            ecoPl1Debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            ecoPl1Debounce.Tick += delegate { ecoPl1Debounce.Stop(); int w = (int)slEcoPl1.Value; Bg(delegate { E.SetEcoPl1(w); }); };
            slEcoPl1.ValueChanged += delegate {
                EcoPl1Text((int)slEcoPl1.Value);
                if (!syncing) { ecoPl1Debounce.Stop(); ecoPl1Debounce.Start(); }
            };
            slPower.ValueChanged += delegate {
                txtPower.Text = "+" + (int)slPower.Value + " W";
                if (!syncing) { powerDebounce.Stop(); powerDebounce.Start(); }
            };
        }

        // ---------- pages ----------
        void Navigate(Page p, bool animate) {
            if (p != Page.Settings) StopListening();
            if (p == cur && pageShown) return;
            bool wasKbd = cur == Page.Keyboard;
            var old = pageShown ? pages[(int)cur] : null;
            cur = p;
            pageShown = true;
            var page = pages[(int)p];
            bool live = animate && screenshotPath == null && IsVisible && old != null && old != page;
            for (int i = 0; i < 4; i++) if (pages[i] != page && !(live && pages[i] == old)) ShowPage(pages[i], false);
            pageHost.Width = p == Page.Keyboard ? KbdPageW : PageW;
            for (int i = 0; i < 4; i++) nav[i].SetSelected(i == (int)p);
            if (p == Page.Keyboard) ApplyEditorState();
            else if (wasKbd) RefreshLighting();
            if (p == Page.Settings) {
                scroll.ScrollToTop();
                scrollTo = 0;
                UpdateKeyStatus();
                UpdateUpdateRow();
                txtMachine.Text = E.Hw.IsDemo && screenshotPath == null ? "simulated hardware" : (E.BiosOk ? "board " + E.Board : "firmware unavailable");
            }
            if (p == Page.Fans) RefreshFans(false);
            // the page has to be Visible before Morph measures it: a Collapsed element reports no desired size at all
            if (!live) ShowPage(page, true);
            else { page.BeginAnimation(UIElement.OpacityProperty, null); page.Opacity = 0; page.IsHitTestVisible = true; page.RenderTransform = null; page.Visibility = Visibility.Visible; }
            double was = ActualWidth * ActualHeight;
            Morph(animate);
            if (live) CrossFade(old, page, (RailW + pageHost.Width) * Math.Ceiling(NaturalHeight()) > was);
            // Render, not Loaded: Loaded sits below Render and Input, so every sensor tick, toast and Refresh that
            // lands first pushes the pill back another pass. Render is still after layout, which is all it needs.
            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate { PlaceRailPill(animate); });
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
            page.Opacity = 1;
            page.IsHitTestVisible = true;
            page.RenderTransform = null;
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
            var tt = new TranslateTransform(0, 0);
            page.RenderTransform = tt;
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
        bool morphing, morphGrow, morphTicking, morphSizeMove;
        TimeSpan morphLast;
        long morphWall;
        double morphAge;
        readonly double[] mx = new double[4], mv = new double[4], mt = new double[4];   // edges l, t, r, b in DIPs: position, velocity, target
        readonly int[] mpx = new int[4];                                                // last device rect sent, so an unchanged frame costs nothing

        void Morph(bool animate) {
            if (pageHost == null || dragging) return;
            double w = RailW + pageHost.Width, natural = NaturalHeight();
            if (natural < 100) return;
            var wa = SystemParameters.WorkArea;
            double h = Math.Min(Math.Ceiling(natural), Math.Max(360, wa.Height - 24));
            double l = Left, t = Top;
            bool move = IsVisible && screenshotPath == null && !double.IsNaN(l) && !double.IsNaN(t) && l > -30000;
            if (move) {
                if (l + w > wa.Right - 6) l = Math.Max(wa.Left + 6, wa.Right - 6 - w);
                if (t + h > wa.Bottom - 6) t = Math.Max(wa.Top + 6, wa.Bottom - 6 - h);
            } else { l = double.IsNaN(l) ? 0 : l; t = double.IsNaN(t) ? 0 : t; }
            if (!animate || !IsVisible || screenshotPath != null || hwnd == IntPtr.Zero) {
                StopMorph();
                Width = w;
                Height = h;
                if (move) { if (Math.Abs(l - Left) > 0.5) Left = l; if (Math.Abs(t - Top) > 0.5) Top = t; }
                return;
            }
            if (!morphing) {
                if (Math.Abs(ActualWidth - w) < 0.5 && Math.Abs(ActualHeight - h) < 0.5 && Math.Abs(Left - l) < 0.5 && Math.Abs(Top - t) < 0.5) return;
                mx[0] = Left;
                mx[1] = Top;
                mx[2] = Left + ActualWidth;
                mx[3] = Top + ActualHeight;
                for (int i = 0; i < 4; i++) mv[i] = 0;
                mpx[0] = int.MinValue;
                morphAge = 0;
            } else if (Math.Abs(mt[0] - l) < 0.5 && Math.Abs(mt[1] - t) < 0.5 && Math.Abs(mt[2] - (l + w)) < 0.5 && Math.Abs(mt[3] - (t + h)) < 0.5) return;
            morphGrow = w * h > (mx[2] - mx[0]) * (mx[3] - mx[1]);                    // retargets keep their velocity; only the spring changes
            mt[0] = l;
            mt[1] = t;
            mt[2] = l + w;
            mt[3] = t + h;
            morphLast = TimeSpan.Zero;
            morphWall = Stopwatch.GetTimestamp();
            if (!morphing) {
                morphing = true;
                SendMessage(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
                morphSizeMove = true;
                CompositionTarget.Rendering += MorphTick;
            }
        }
        /// <summary>What the visible page wants to be, measured with no height limit (Settings is a fixed, scrolling page). Only the
        /// current page counts: the outgoing one is still in the host while it fades.</summary>
        double NaturalHeight() {
            if (cur == Page.Settings) return SettingsH;
            var page = pages[(int)cur];
            if (page == null || pageHost.Width <= 0) return 0;
            page.Measure(new Size(pageHost.Width, double.PositiveInfinity));
            return page.DesiredSize.Height;
        }
        void StopMorph() {
            if (!morphing) return;
            morphing = false;
            CompositionTarget.Rendering -= MorphTick;
            if (morphSizeMove) { morphSizeMove = false; SendMessage(hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero); }
        }
        void MorphTick(object o, EventArgs e) {
            var re = e as RenderingEventArgs;
            if (re == null || morphTicking || !morphing) return;
            if (re.RenderingTime == morphLast) return;          // WPF re-raises Rendering (same locked tick time) inside the synchronous render our own SetWindowPos causes
            double dt;
            if (morphLast == TimeSpan.Zero) dt = Math.Max(1.0 / 120, Math.Min(1.0 / 30, (Stopwatch.GetTimestamp() - morphWall) / (double)Stopwatch.Frequency));   // first frame moves too
            else dt = Math.Min(0.05, (re.RenderingTime - morphLast).TotalSeconds);      // a stall is not a leap
            morphLast = re.RenderingTime;
            morphAge += dt;
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
                Width = mt[2] - mt[0];
                Height = mt[3] - mt[1];
                E.S.WinX = (int)mt[0];
                E.S.WinY = (int)mt[1];
                Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate { PlaceRailPill(true); });   // residual is sub-pixel; Glide snaps it
            }
        }
        void ApplyBounds(double l, double t, double r, double b) {
            var ps = PresentationSource.FromVisual(this);
            if (ps == null || ps.CompositionTarget == null) return;
            var m = ps.CompositionTarget.TransformToDevice;
            int x0 = (int)Math.Round(l * m.M11), y0 = (int)Math.Round(t * m.M22), x1 = (int)Math.Round(r * m.M11), y1 = (int)Math.Round(b * m.M22);
            if (x0 == mpx[0] && y0 == mpx[1] && x1 == mpx[2] && y1 == mpx[3]) return;
            mpx[0] = x0;
            mpx[1] = y0;
            mpx[2] = x1;
            mpx[3] = y1;
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
                for (int i = 1; i <= 4; i++) kbdModes.SetEnabled(i, false, "This keyboard offers no lighting interface Ohman can drive; see the note below");
            kbdModes.Picked += delegate(int i) {
                int m = Choice.LightMode(i), fx = Choice.LightEffect(i);
                E.S.Light = m;
                E.S.LightEffect = fx;
                ApplyEditorState();
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
                string t = txtHex.Text.Trim().TrimStart('#');
                Rgb c;
                if (t.Length == 6 && Rgb.TryParse(t, out c)) { hexTyping = true; Paint(c, true); hexTyping = false; }
            };
            levelDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            levelDebounce.Tick += delegate { levelDebounce.Stop(); int lv = (int)slLevel.Value; Bg(delegate { E.SetLightLevel(lv); }); };
            RoutedPropertyChangedEventHandler<double> level = delegate(object o, RoutedPropertyChangedEventArgs<double> ev) {
                bool was = syncing;
                if (!was) { syncing = true; try { if (ReferenceEquals(o, slLevel)) slLevel2.Value = slLevel.Value; else slLevel.Value = slLevel2.Value; } finally { syncing = false; } }
                int v = (int)slLevel.Value;
                txtLevel.Text = v + "%";
                txtLevel2.Text = v + "%";
                if (kbdBig != null) { kbdBig.Level = kbdMini.Level = v / 100.0; kbdBig.Repaint(); kbdMini.Repaint(); }   // the drawing dims with the slider
                if (!was) { levelDebounce.Stop(); levelDebounce.Start(); }
            };
            slLevel.ValueChanged += level;
            slLevel2.ValueChanged += level;
            speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            speedDebounce.Tick += delegate { speedDebounce.Stop(); int sp = (int)slSpeed.Value; Bg(delegate { E.SetLightSpeed(sp); }); };
            slSpeed.ValueChanged += delegate {
                txtSpeed.Text = ((int)slSpeed.Value).ToString(CultureInfo.InvariantCulture);
                if (!syncing) { speedDebounce.Stop(); speedDebounce.Start(); }
            };
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
            kbdBig.Repaint();
            UpdateSelectionText();
            SyncPickerFromSelection();
        }
        int[] SelectedZones() {
            var set = new List<int>();
            foreach (var k in kbdBig.Keys) if (kbdBig.Selected.Contains(k.Index) && !set.Contains(k.Zone)) set.Add(k.Zone);
            set.Sort();
            return set.ToArray();
        }
        void UpdateSelectionText() {
            int n = kbdBig.Selected.Count;
            if (n == 0) { txtKeySel.Text = "Click the map to select"; return; }
            if (gran == "All") { txtKeySel.Text = "whole keyboard"; return; }
            if (gran == "Zone") {
                var z = SelectedZones();
                var names = new List<string>();
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
            kbdBig.SetColors(E.LightColors, false);
            kbdMini.SetColors(E.LightColors, false);
            SyncSwatch(fromHex);
            colorDebounce.Stop();
            colorDebounce.Start();
        }
        void SyncSwatch(bool fromHex) {
            bool was = syncing;
            syncing = true;
            try {
                bool one = OneColour();
                hexChip.Background = one ? Ui.Brush(curColor) : Ui.Brush("#2C2825");
                if (!fromHex && !hexTyping) txtHex.Text = one ? curColor.Hex.ToLowerInvariant() : "";
                hueBar.Current = curColor;
                hueBar.HasCurrent = one;
                hueBar.Repaint();
                double h, s, v;
                curColor.ToHsv(out h, out s, out v);
                shadeBar.Hue = one ? h : 210;
                shadeBar.Current = curColor;
                shadeBar.HasCurrent = one;
                shadeBar.Repaint();
            } finally { syncing = was; }
        }
        /// <summary>True when the selection is one colour, so there is something to show in the field.</summary>
        bool OneColour() {
            int[] z = SelectedZones();
            if (z.Length == 0) return false;
            for (int i = 1; i < z.Length; i++) {
                if (z[i] >= E.LightColors.Length || z[0] >= E.LightColors.Length) return false;
                var a = E.LightColors[z[0]];
                var b = E.LightColors[z[i]];
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
            var S = E.S;
            int m = S.Light, fx = S.LightEffect;
            // A per-key board answers every firmware lighting call and lights nothing by them, so none of our own
            // modes can be offered: Windows Dynamic Lighting talks to the keyboard directly and is the only one
            // that works there. Keeping the editor on screen would just discard everything the user picked.
            bool inert = E.Light.Inert;
            if (inert) m = S.Light == 2 ? 2 : 0;
            bool lit = m == 1, pick = lit && (fx == 0 || fx == 1), effect = lit && fx != 0;
            kbdModes.Select(Choice.OfLight(m, fx), IsVisible && cur == Page.Keyboard);
            selectRow.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            colorEditor.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            // Breathe is the one mode that is both a colour and an effect, so it gets both blocks. Its brightness
            // then comes from the effect row, and the inline one would be a second copy of the same slider.
            effectEditor.Visibility = effect ? Visibility.Visible : Visibility.Collapsed;
            effectEditor.Margin = new Thickness(0, pick ? 18 : 0, 0, 0);
            levelInline.Visibility = pick && !effect ? Visibility.Visible : Visibility.Collapsed;
            kbdInfo.Visibility = lit ? Visibility.Collapsed : Visibility.Visible;
            btnWinLighting.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
            // Reaching here means both ways in failed: HP's firmware answers and lights nothing, and this keyboard
            // offers no HID lighting interface either. Say which, so it does not read as "per-key is unsupported".
            txtKbdInfo.Text = inert && m != 2
                ? "Ohman cannot light this keyboard. HP's firmware interface answers for per-key boards but does nothing, and this keyboard offers no lighting interface of its own." + (WinLighting.KeyboardFound ? " Windows Dynamic Lighting can still light it." : "") + " Run Ohman.exe --lamps and open an issue with what it prints."
                : m == 0 ? "The backlight is off. Pick a mode to turn it back on, or press the keyboard backlight key."
                : (WinLighting.Present || E.Hw.IsDemo ? "Windows Dynamic Lighting has the keyboard. Its colours and effects come from Windows settings."
                                                      : "No Dynamic Lighting device for this keyboard was found; Windows cannot drive it.");
            kbdBig.Selectable = pick;
            kbdBig.Off = m == 0;
            kbdBig.WindowsOwned = m == 2;
            kbdBig.Smooth = effect;
            kbdMini.Off = m == 0;
            kbdMini.WindowsOwned = m == 2;
            kbdMini.Smooth = effect;
            kbdBig.Level = kbdMini.Level = S.LightLevel / 100.0;
            if (!pick) { kbdBig.Selected.Clear(); kbdBig.Hover.Clear(); }
            string fxName = new[] { "static", "breathe", "cycle", "wave" }[Math.Max(0, Math.Min(3, fx))];
            // breathe runs in the firmware too, but it breathes the colours you picked, so it is not "colour fixed"
            txtKbdStatus.Text = inert && m != 2 ? "per-key · no interface we can drive" : m == 2 ? "windows lighting" : m == 0 ? "backlight off" : fx >= 2 ? "firmware effect · colour fixed" : E.Light.Describe + " · " + fxName;
            UpdateSelectionText();
            miniNeedsFrame = true;
            kbdBig.Repaint();
            kbdMini.Repaint();
            Remeasure(Page.Keyboard);        // the colour editor and the effect editor are different heights
        }
        void RefreshLighting() {
            if (E.Light == null || kbdMini == null) return;
            var S = E.S;
            ApplyEditorState();
            if (!(S.Light == 1 && S.LightEffect != 0)) { kbdMini.SetColors(E.LightColors, false); kbdBig.SetColors(E.LightColors, false); }   // effects repaint from the engine's frames
            bool was = syncing;
            syncing = true;
            try {
                slLevel.Value = slLevel2.Value = Math.Max(5, S.LightLevel);
                txtLevel.Text = txtLevel2.Text = S.LightLevel + "%";
                slSpeed.Value = S.LightSpeed;
                txtSpeed.Text = S.LightSpeed.ToString(CultureInfo.InvariantCulture);
            } finally { syncing = was; }
            if (cur != Page.Keyboard) SyncPickerFromSelection();
            string fxName = new[] { "Static", "Breathe", "Cycle", "Wave" }[Math.Max(0, Math.Min(3, S.LightEffect))];
            txtLightSub.Text = S.Light == 2 ? "Windows Dynamic Lighting" : S.Light == 0 ? "Off" : fxName + " · " + E.Light.Describe;
        }

        // ---------- fans page ----------
        void RefreshFans(bool animate) {
            var S = E.S;
            FanMode f = S.Fan;
            bool linked = S.Cur.CurveLinked;
            bool was = syncing;
            syncing = true;
            try {
                fanSeg.Select(Choice.Of(f), animate && IsVisible);
                curveBlock.Visibility = f == FanMode.Auto || f == FanMode.Custom ? Visibility.Visible : Visibility.Collapsed;
                maxBlock.Visibility = f == FanMode.Max ? Visibility.Visible : Visibility.Collapsed;
                manualBlock.Visibility = f == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                optsAuto.Visibility = f == FanMode.Auto ? Visibility.Visible : Visibility.Collapsed;
                optsCurve.Visibility = f == FanMode.Custom ? Visibility.Visible : Visibility.Collapsed;
                optsMax.Visibility = f == FanMode.Max ? Visibility.Visible : Visibility.Collapsed;
                optsManual.Visibility = f == FanMode.Manual ? Visibility.Visible : Visibility.Collapsed;
                tgEcoCool2.IsChecked = S.EcoCool;
                tgMaxCool.IsChecked = S.MaxBackWhenCool;
                tgManualLink.IsChecked = S.ManualLinked;
                tgLink.IsChecked = linked;
                for (int i = 0; i < stopAfterSeg.Count; i++) if ((int)stopAfterSeg.Tags[i] == S.MaxStopAfterMin) stopAfterSeg.Select(i, animate && IsVisible);
                if (linked || f != FanMode.Custom) curveGpu = false;
                curveWhichHost.Visibility = f == FanMode.Custom && !linked ? Visibility.Visible : Visibility.Collapsed;
                curveWhich.Select(curveGpu ? 1 : 0, animate && IsVisible);
                int floor = S.Cur.CurveFloor <= E.P.Curve.Floor ? E.P.Curve.Floor : S.Cur.CurveFloor;
                slFloor.Value = Math.Min(slFloor.Maximum, floor);
                txtFloor.Text = floor <= E.P.Curve.Floor ? "off" : Pct(floor);
                slRamp.Value = S.Cur.CurveRamp;
                txtRamp.Text = S.Cur.CurveRamp + " s";
                curveView.ReadOnly = f != FanMode.Custom;
                curveView.UserFloor = f == FanMode.Custom && floor > E.P.Curve.Floor ? floor : 0;
                if (f == FanMode.Auto) {
                    curveView.Levels = E.VendorCurveAt(false);
                    txtCurveTitle.Text = "This model's curve";
                    txtCurveHint.Text = "read-only";
                } else if (f == FanMode.Custom) {
                    if (!curveDebounce.IsEnabled) curveView.Levels = (int[])(curveGpu ? S.Cur.GpuCurveLevels : S.Cur.CurveLevels).Clone();
                    txtCurveTitle.Text = curveGpu ? "GPU curve" : "CPU curve";
                    txtCurveHint.Text = "drag a point · shift-drag moves all";
                }
                slFan1.Value = S.Fan1;
                slFan2.Value = S.Fan2;
                txtFan1.Text = Pct(S.Fan1);
                txtFan2.Text = Pct(S.Fan2);
                manPct1.Text = Pct(S.Fan1).TrimEnd('%');
                manPct2.Text = Pct(S.Fan2).TrimEnd('%');
                manSub1.Text = E.Rpm(S.Fan1) + " · CPU fan";
                manSub2.Text = E.Rpm(S.Fan2) + " · GPU fan";
                UpdateCurveLive();
                UpdateMaxBlock();
                UpdateFanStatus();
                UpdateFanFooter();
            } finally { syncing = was; }
            Remeasure(Page.Fans);            // the curve, max and manual blocks are different heights
        }
        void UpdateMaxBlock() {
            if (E.S.Fan != FanMode.Max) return;
            int f1 = lastFans != null && lastFans[0] > 0 ? lastFans[0] : 0, f2 = lastFans != null && lastFans[1] > 0 ? lastFans[1] : 0;
            maxFan1.Text = Level(f1);
            maxFan2.Text = Level(f2);
            maxFan1Sub.Text = "CPU fan" + (f1 > 0 ? " · " + Pct(f1) : "");
            maxFan2Sub.Text = "GPU fan" + (f2 > 0 ? " · " + Pct(f2) : "");
            maxTemp.Text = double.IsNaN(E.CpuTemp) ? "--" : E.CpuTemp.ToString("0", CultureInfo.InvariantCulture);
            maxTempSub.Text = "CPU · " + Trend();
            var left = E.MaxLeft;
            if (E.S.MaxStopAfterMin > 0 && left > TimeSpan.Zero) { maxMins.Text = ((int)Math.Ceiling(left.TotalMinutes)).ToString(CultureInfo.InvariantCulture); maxMinsUnit.Text = " min"; maxMinsSub.Text = "Until it stops"; }
            else { maxMins.Text = E.MaxMinutes.ToString(CultureInfo.InvariantCulture); maxMinsUnit.Text = " min"; maxMinsSub.Text = "Running at max"; }
        }
        string Trend() {
            if (tempTrail.Count < 4) return "steady";
            double a = 0, b = 0;
            int half = tempTrail.Count / 2;
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
                txtFanApplied.Text = "Loud" + (ShowChassis(lastBiosTemp) ? " · ambient " + lastBiosTemp + "°" : "");
                txtFanRight.Text = "Ctrl+Alt+M toggles";
            } else {
                txtFanApplied.Text = E.GuardActive ? "Thermal guard: max fan until cool" : "Applied to " + E.ModeName;
                txtFanRight.Text = "CPU " + (double.IsNaN(E.CpuTemp) ? "--" : E.CpuTemp.ToString("0") + "°") + " · GPU " + (double.IsNaN(E.GpuTemp) ? "--" : E.GpuTemp.ToString("0") + "°");
            }
        }
        void UpdateCurveLive() {
            var S = E.S;
            double t;
            int lvl;
            if (S.Fan == FanMode.Custom && !S.Cur.CurveLinked && curveGpu) { t = E.GpuTemp; lvl = E.AutoLevel2; }
            else if (S.Fan == FanMode.Custom && S.Cur.CurveLinked) { t = double.IsNaN(E.CpuTemp) ? E.GpuTemp : double.IsNaN(E.GpuTemp) ? E.CpuTemp : Math.Max(E.CpuTemp, E.GpuTemp); lvl = E.AutoLevel1; }
            else { t = E.CpuTemp; lvl = E.AutoLevel1; }
            curveView.LiveTemp = t;
            curveView.LiveLevel = lvl;
            curveView.Repaint();
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
            hzRow.Visibility = Visibility.Visible;
            lowHzRow.Visibility = Visibility.Visible;
            ecoHzRow.Visibility = Visibility.Visible;
            var names = new string[rates.Length];
            var tags = new object[rates.Length];
            for (int i = 0; i < rates.Length; i++) { names[i] = i == rates.Length - 1 ? rates[i] + " Hz" : rates[i].ToString(CultureInfo.InvariantCulture); tags[i] = rates[i]; }
            hzSeg = new Seg(names, null, tags, Seg.Kind.Row);
            hzSeg.SetMono(11.5);
            F<Border>("HzSegHost").Child = hzSeg;
            hzSeg.Picked += delegate(int i) { int h = (int)hzSeg.Tags[i]; Bg(delegate { E.SetRefreshRate(h); }); };
        }
        /// <summary>Graphics modes the firmware offers, as a segment; a change is written at once and needs a restart to take effect.</summary>
        void BuildGraphicsModes() {
            if (!E.BiosOk && !E.Hw.IsDemo) return;
            int[] order = { 0, 1, 3 };                                       // Hybrid, Discrete (the MUX), iGPU only
            var names = new List<string>();
            var tags = new List<object>();
            foreach (int mode in order) if (E.GpuModeOffered(mode)) { names.Add(Engine.GpuModeNames[mode]); tags.Add(mode); }
            if (names.Count < 2) return;
            gfxSeg = new Seg(names.ToArray(), null, tags.ToArray(), Seg.Kind.Row);
            F<Border>("GfxSegHost").Child = gfxSeg;
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
        int trayTempShown = int.MinValue;
        SD.Icon trayTempIcon;
        void UpdateTrayTemp(double cpu) {
            if (tray == null) return;
            if (!E.S.TrayTemp || double.IsNaN(cpu)) { if (trayTempShown != int.MinValue) { trayTempShown = int.MinValue; try { tray.Icon = icons[E.ModeIndex]; } catch { } } return; }
            int t = (int)Math.Round(cpu);
            int key = t * 4 + E.ModeIndex;
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
        /// <summary>The Settings rows and the Home slider for the CPU limits. Rows only exist on boards whose
        /// profile has per-mode limits; the slider only in Unleashed, once its owner has switched it on.</summary>
        /// <summary>Under Intel's 35 W minimum assured power for the 285H the value turns amber: allowed, and slower.</summary>
        void EcoPl1Text(int w) {
            txtEcoPl1.Text = w + " W";
            if (w < 35) { txtEcoPl1.Foreground = Ui.Brush(Ui.Warn); txtEcoPl1.ToolTip = "Below Intel's 35 W minimum assured power for this CPU: it still works, just slower under load"; }
            else { txtEcoPl1.ClearValue(TextBlock.ForegroundProperty); txtEcoPl1.ToolTip = null; }
        }
        void RefreshCpuLimits(int mi) {
            var S = E.S;
            bool offered = E.HasCpuLimits;
            bool rowsWere = cpuLimitsRow.Visibility == Visibility.Visible, expWas = pl2ExpRow.Visibility == Visibility.Visible, slWas = pl2Row.Visibility == Visibility.Visible;
            cpuLimitsRow.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
            if (offered) {
                bool ready = E.CpuLimitsReady;
                tgCpuLimits.IsEnabled = ready;
                tgCpuLimits.IsChecked = S.CpuLimits;
                txtCpuLimitsSub.Text = !ready ? "Needs the hardware driver or a firmware that takes them"
                    : !S.CpuLimits ? "Off · the firmware's own limits"
                    : E.CpuLimitsWhy.Length > 0 ? "Not applied: " + E.CpuLimitsWhy
                    : E.CpuPl1Written >= 0 ? "Now PL1 " + E.CpuPl1Written + " W · PL2 " + E.CpuPl2Written + " W" + (E.CpuLimitsRoute.Length > 0 ? " · via " + E.CpuLimitsRoute : "")
                        + (E.CpuLimitsFallback ? " · OMEN Gaming Hub's values, ours were not accepted (" + E.CpuFallbackWhy + ")" : " · set per mode")
                    : "Set per mode";
            }
            bool exp = offered && E.HasUnleashedSliders && S.CpuLimits;
            pl2ExpRow.Visibility = exp ? Visibility.Visible : Visibility.Collapsed;
            if (exp) {
                tgPl2Exp.IsChecked = S.UnlCustom;
                txtPl2ExpSub.Text = S.UnlCustom ? "PL1 and PL2 sliders on Home in Unleashed · now " + E.UnlPl1 + " / " + E.UnlPl2Effective + " W"
                    : "Adds PL1 (" + E.P.UnlPl1Range[0] + "-" + E.P.UnlPl1Range[1] + " W) and PL2 (" + E.P.UnlPl2Range[0] + "-" + E.P.UnlPl2Range[1] + " W) sliders on Home in Unleashed";
            }
            bool slider = exp && S.UnlCustom && E.CpuLimitsReady && mi == 3;
            pl1Row.Visibility = pl2Row.Visibility = slider ? Visibility.Visible : Visibility.Collapsed;
            if (slider) {
                slPl1.Value = E.UnlPl1; slPl2.Value = E.UnlPl2;
                txtPl1.Text = E.UnlPl1 + " W"; txtPl2.Text = E.UnlPl2Effective + " W";
            }
            bool eco = offered && E.HasEcoSlider && S.CpuLimits && E.CpuLimitsReady && mi == 0;
            bool ecoWas = ecoPl1Row.Visibility == Visibility.Visible;
            ecoPl1Row.Visibility = eco ? Visibility.Visible : Visibility.Collapsed;
            if (eco) { slEcoPl1.Value = E.EcoPl1; EcoPl1Text(E.EcoPl1); }
            if (slWas != slider || ecoWas != eco) Remeasure(Page.Home);
            if (rowsWere != offered || expWas != exp) Remeasure(Page.Settings);
        }
        void Synced(Action a) {
            bool was = syncing;
            syncing = true;
            try { a(); } finally { syncing = was; }
        }
        /// <summary>Wire a switch once: the flag check and the "only when the user did it" rule live here, not in
        /// eleven copies that each have to remember them.</summary>
        void OnSwitch(ToggleButton t, Action<bool> set) {
            RoutedEventHandler h = delegate { if (syncing) return; set(t.IsChecked == true); };
            t.Checked += h;
            t.Unchecked += h;
        }
        /// <summary>"HP OMEN Transcend 14 (2024, 14-fb0xxx)" reads as "Transcend 14" in a footer.</summary>
        string ShortModel() {
            string n = E.P.Name ?? "";
            int p = n.IndexOf('(');
            if (p > 0) n = n.Substring(0, p);
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
            string staged = E.Staged;
            bool newer = E.UpdateAvailable;
            // Three states, and the difference that matters is whether the new build is already on disk. Only
            // then is restarting a promise we can keep, so only then does the row offer it.
            // The title is the button once there is a build to install. It names what restarts, which a laptop
            // utility has to, and it does it in the space a heading takes anyway - so the right of the row is
            // free for Changelog, and nothing has to wrap.
            // Accent only on the words that are the action. "to update" is what it is for, not part of the press,
            // and colouring it too made the whole heading read as one long link.
            // Only when it actually changes. This runs on the refresh tick, and tearing the inline collection down
            // and building two Runs every time is a layout invalidation a second for a heading that changes twice
            // in the life of the process.
            if (updateTitleFor != (staged ?? "")) {
                updateTitleFor = staged ?? "";
                txtUpdateTitle.Inlines.Clear();
                if (staged != null) {
                    txtUpdateTitle.Inlines.Add(new Run("Restart " + Program.DisplayName) { Foreground = accent });
                    txtUpdateTitle.Inlines.Add(new Run(" to update") { Foreground = Ui.TextB });
                } else txtUpdateTitle.Inlines.Add(new Run("Check for updates") { Foreground = Ui.TextB });
                updateText.Cursor = staged != null ? Cursors.Hand : null;
            }
            txtUpdate.Text = staged != null
                ? Program.Version + " → " + staged
                : Program.Version + " · " + Update.Ago(E.LastUpdateCheck) + (newer ? " · " + E.LatestVersion + " available" : "");
            // Only the thing that can be clicked is accent. The version line under it went blue too, which put
            // two competing blues in one row and made a plain caption look like a second link.
            txtUpdate.Foreground = (staged == null && newer) ? (Brush)accent : Ui.Desc;
            btnUpdate.Text = staged != null ? "Changelog" : newer ? "Download" : "Check now";
            if (navUpdate != null) {
                var want = staged != null ? Visibility.Visible : Visibility.Collapsed;
                if (navUpdate.Visibility != want) {
                    navUpdate.Visibility = want;
                    navUpdate.ToolTip = staged == null ? "Update available" : "Update to " + staged + " is ready";
                    // The button sits above Settings, so showing it moves the gear down by its own height. The
                    // rail pill is placed from the selected button's position and is only replaced on navigation
                    // or a rail resize, neither of which happens here, so it would sit detached until the owner
                    // clicked something else.
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate { PlaceRailPill(true); });
                }
            }
        }
        // ---------- the driver row and the Home nudge ----------
        /// <summary>The row's state, as one string: rebuilt only when it changes, since Refresh runs on a timer.</summary>
        void UpdateDriverRow() {
            var S = E.S;
            bool demo = E.Hw.IsDemo;
            bool installed = E.DriverInstalled;
            string title = "Hardware driver", sub, link = null, name = null;
            bool showSwitch = installed && !E.DriverBusy;
            // In the words of somebody who does not know what a register is. Only Intel gives up its power limits
            // and throttle reasons to the module we carry, so a Ryzen is promised the one thing it will get.
            bool intel = demo || CpuRegisters.IsIntel;
            string gains = (intel ? "more accurate CPU temperature, power limits & throttle reasons" : "a more accurate CPU temperature")
                + ((E.P.DriverFor & DriverFor.FanLevels) != 0 ? ", and fan levels on this board" : "");
            if (E.DriverBusy) { sub = E.DriverProgress; driverState = DriverState.Busy; }
            // A pending restart is a more specific state than "not installed", so it is asked about first: the
            // installer has done its half and the device only appears after a reboot.
            else if (S.DriverRestartPending && !E.DriverReady) { sub = "Installed · restart Windows to finish"; link = "Restart now"; driverState = DriverState.RestartPending; }
            else if (!installed) { sub = "Adds " + gains; link = "Install"; driverState = DriverState.NotInstalled; }
            else if (!S.DriverUse) { sub = "Off · adds " + gains; driverState = DriverState.Off; }
            else if (E.DriverOutdated) { name = DriverName(); sub = " · needs " + PawnIo.MinVersion + " or newer"; link = "Update"; driverState = DriverState.Outdated; }
            else if (E.DriverReady) {
                // Said as what it is doing, not as what it opened. "CPU registers" answered a question nobody asked.
                // The simulated build takes this branch too, so the preview shows the row people will actually see.
                name = DriverName();
                string does = E.Route == Engine.FanRoute.Ec ? "fan levels" : null;
                if (E.Cpu != null) does = (does != null ? does + " and " : "") + (intel ? "CPU temperature and power limits" : "CPU temperature");
                sub = " · " + (demo ? "simulated" : does ?? "open");
                driverState = DriverState.Ready;
                if (!demo && S.DriverInstalledByOhman) link = "Remove";
            } else { title = "Hardware driver not detected"; sub = E.DriverWhy; link = "Troubleshoot"; driverState = DriverState.Broken; }
            string key = title + "|" + (name ?? "") + sub + "|" + (link ?? "") + "|" + showSwitch;
            if (key != driverRowFor) {
                driverRowFor = key;
                txtDriverTitle.Text = title;
                if (name != null) DriverSubLinked(name, sub); else txtDriverSub.Text = sub;
                btnDriver.Text = link ?? "";
                btnDriver.Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
                // Not whose it is and not what it is called: an unfamiliar name in front of a decision is what
                // made the old dialog read as a warning. The row credits PawnIO by link once it is installed.
                btnDriver.ToolTip =
                    driverState == DriverState.NotInstalled || driverState == DriverState.Outdated
                        ? "Same driver FanControl, LibreHardwareMonitor and a dozen other hardware tools install. Removable any time."
                    : driverState == DriverState.Ready
                        ? "Safe, " + Program.DisplayName + " falls back to the temperature Windows reports."
                    : null;
                tgDriver.Visibility = showSwitch ? Visibility.Visible : Visibility.Collapsed;
            }
            tgDriver.IsChecked = demo || S.DriverUse;
            // The Home nudge: the same install, from where the owner is.
            string nudge = E.DriverNudge;
            var want = nudge != null ? Visibility.Visible : Visibility.Collapsed;
            if (nudge != null) {
                txtDriverNudge.Text = nudge;
                bool restart = S.DriverRestartPending;
                txtDriverNudgeSub.Text = restart ? "The driver is installed and waits for a restart" : "Your firmware refuses fan levels through BIOS commands. " + Program.DisplayName + " can set them through a driver.";
                btnDriverNudge.Text = restart ? "Restart now" : "Install driver";
            }
            if (driverBanner.Visibility != want) { driverBanner.Visibility = want; if (cur == Page.Home) Remeasure(cur); }
        }
        /// <summary>"PawnIO 2.2.0", and never the fourth field: nobody needs the build number of somebody else's driver.</summary>
        string DriverName() {
            Version v = E.DriverVersion;
            return v == null ? "PawnIO" : "PawnIO " + (v.Build >= 0 ? v.ToString(3) : v.ToString());
        }
        /// <summary>The sub-line with the driver's name as a link to its source. Somebody who has just been asked to
        /// trust a kernel driver should be one click from its code, not one search away from whatever a search finds.</summary>
        void DriverSubLinked(string name, string rest) {
            var link = new Hyperlink(new Run(name)) { Foreground = accent, TextDecorations = null, Cursor = Cursors.Hand, ToolTip = PawnIo.SourceUrl };
            link.Click += delegate { OpenUrl(PawnIo.SourceUrl); };
            link.MouseEnter += delegate { link.TextDecorations = TextDecorations.Underline; };
            link.MouseLeave += delegate { link.TextDecorations = null; };
            txtDriverSub.Inlines.Clear();
            txtDriverSub.Inlines.Add(link);
            txtDriverSub.Inlines.Add(new Run(rest));
        }
        void OpenUrl(string url) {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { ShowToast("Cannot open " + url + ": " + ex.Message, true); }
        }
        void DriverAction() {
            // On the state, not on the words: comparing against btnDriver.Text made the label load-bearing, and
            // rewording the row would have disconnected the click from its action with nothing to notice it.
            if (driverState == DriverState.NotInstalled || driverState == DriverState.Outdated) InstallDriver();
            else if (driverState == DriverState.RestartPending) RestartWindows("the driver");
            else if (driverState == DriverState.Broken) DriverCheck();
            // Removing asks no more than installing did. It is one click to put back, the row says what else
            // uses it before the press, and a dialog between somebody and the button they aimed at is the thing
            // that made this feel like a warning rather than a setting.
            else if (driverState == DriverState.Ready) Slow(delegate { E.RemoveDriver(); });
        }
        void InstallDriver() {
            if (E.Hw.IsDemo) { ShowToast("Simulated hardware: nothing to install", true); return; }
            if (E.DriverBusy) return;
            // No confirmation: pressing Install is the consent. Windows wants nothing here either, since the app
            // is already elevated and hands that token to the installer. What the dialog said is on the tooltip.
            Slow(delegate { E.InstallDriver(); });
        }
        void DriverCheck() {
            btnDriver.Text = "checking…";
            Slow(delegate {
                string d;
                try { d = Support.DriverReport(E); } catch (Exception ex) { d = "driver check failed: " + ex.Message; }
                Log.Write(d);
                Dispatcher.BeginInvoke((Action)delegate {
                    driverRowFor = "?";
                    UpdateDriverRow();
                    bool copied = false;
                    try { Clipboard.SetText(d); copied = true; } catch { }
                    txtDiag.Text = d.TrimEnd();
                    txtDiag.Visibility = Visibility.Visible;
                    Remeasure(cur);
                    ShowToast(copied ? "Copied - paste it into a GitHub issue or Discord" : "See below", false);
                });
            });
        }
        void RestartWindows(string why) {
            if (MessageBox.Show(IsVisible ? (Window)this : null, "Restart Windows now to finish installing " + why + "?", Program.DisplayName,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"" + Program.DisplayName + ": finishing the driver install\"") { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { ShowToast("Restart failed: " + ex.Message, true); }
        }

        /// <summary>The rail button and the Settings link both end here. The row is near the bottom of a page that
        /// scrolls, so arriving at Settings without this puts the thing that was clicked for off-screen.</summary>
        void ShowUpdateRow() { ShowRow(updateRow); }
        void ShowDriverRow() { ShowRow(driverRow); }
        void ShowRow(FrameworkElement row) {
            Navigate(Page.Settings, true);
            // After the page has laid out, or the row has no position yet. BringIntoView would do the smallest
            // scroll that makes it visible, which leaves it jammed against the bottom edge; this puts it a little
            // way up the page and eases there the way every other scroll in the app does.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate {
                try {
                    var content = scroll.Content as FrameworkElement;
                    if (content == null) return;
                    double y = row.TranslatePoint(new Point(0, 0), content).Y;
                    scrollTo = Math.Max(0, Math.Min(scroll.ScrollableHeight, y - 96));
                    if (screenshotPath != null) { scroll.ScrollToVerticalOffset(scrollTo); return; }   // no frames to ease over before the capture
                    if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollTick; }
                } catch { }
            });
        }
        void OpenReleases() {
            try { Process.Start(new ProcessStartInfo(Update.ReleasesUrl) { UseShellExecute = true }); }
            catch (Exception ex) { ShowToast("Cannot open the releases page: " + ex.Message, true); }
        }
        void DoUpdate() {
            string err;
            if (!E.StartUpdate(out err)) { ShowToast("Could not install the update: " + err, true); return; }
            // The replacement is already starting and is waiting on the single-instance mutex for this process to
            // end, so the fans are handed straight over: no reason to raise the parting level for a gap that is
            // about a second long.
            quietExit = true;
            ExitApp();
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
            bool wasSyncing = syncing;
            syncing = true;
            try {
                var S = E.S;
                int mi = E.ModeIndex;
                SelectMode(mi, true);
                slPower.Value = S.TdpOffset;
                txtPower.Text = "+" + S.TdpOffset + " W";
                GpuLevel g = E.EffectiveGpu;
                string gpuName = g == GpuLevel.Boost ? "GPU boost" : "GPU base";
                // Two ways this number lies. 0 means the firmware does not implement 0x23 at all. And on a board
                // nobody has measured, the scale is unknown: one 8BCD sits at 50 and above around the clock while
                // the laptop is cool to the touch. We already refuse to let that sensor drive the fans there
                // (FanCurve.UseChassis), and showing it beside real temperatures is the same mistake one layer up.
                // Same rule as the chassis reading: a board whose firmware never reported a base TDP has no
                // wattage to show, and CurrentTdp there is a leftover slider offset with nothing under it.
                txtHomeStatus.Text = (E.P.HasGpuPower ? gpuName : E.P.HasPowerGain ? E.CurrentTdp + " W" : "")
                    + (ShowChassis(lastBiosTemp) ? " · ambient " + lastBiosTemp + "°" : "");
                fanLinks.SetText(2, S.Fan == FanMode.Custom ? "Curve" : "Manual");
                fanLinks.Select(S.Fan == FanMode.Auto ? 0 : S.Fan == FanMode.Max ? 1 : 2, IsVisible);
                if (pollSeg != null) pollSeg.Select(S.PollMs <= 500 ? 0 : S.PollMs <= 1000 ? 1 : 2, IsVisible && cur == Page.Settings);
                if (gpuSeg != null) { gpuSeg.Select(S.GpuAuto ? 2 : (int)g, IsVisible && cur == Page.Settings); txtGpuSub.Text = S.GpuAuto ? "Follows the mode" : g == GpuLevel.Boost ? "cTGP + PPAB" : mi >= 2 ? "cTGP, no PPAB" : "Standard TGP"; }
                RefreshLighting();
                if (cur == Page.Fans) RefreshFans(true);
                keySeg.Select(Choice.Of(S.Key), IsVisible && cur == Page.Settings);
                keyCmdRow.Visibility = S.Key == KeyAction.Run ? Visibility.Visible : Visibility.Collapsed;
                if (!txtKeyCmd.IsKeyboardFocused) txtKeyCmd.Text = S.KeyCommand;
                tgLowHzBattery.IsChecked = S.LowHzOnBattery;
                tgEcoHz.IsChecked = E.EcoLowHz;
                tgTrayTemp.IsChecked = S.TrayTemp;
                tgGuard.IsChecked = S.Guard;
                RefreshCpuLimits(mi);
                tgUpdateAuto.IsChecked = S.UpdateOnLaunch;
                int hzNow = Display.CurrentHz();
                if (hzSeg != null) for (int i = 0; i < hzSeg.Count; i++) if ((int)hzSeg.Tags[i] == hzNow) hzSeg.Select(i, IsVisible && cur == Page.Settings);
                foreach (var m in trayHz) m.Checked = (int)m.Tag == hzNow;
                int gfx = E.GpuModePending >= 0 ? E.GpuModePending : E.GpuMode;
                if (gfxSeg != null) for (int i = 0; i < gfxSeg.Count; i++) if ((int)gfxSeg.Tags[i] == gfx) gfxSeg.Select(i, IsVisible && cur == Page.Settings);
                txtGfxSub.Text = E.GpuModePending >= 0 && E.GpuModePending != E.GpuMode ? Engine.GpuModeNames[E.GpuModePending] + " after the next restart" : "Takes effect after a restart";
                UpdateKeyStatus();
                UpdateUpdateRow();
                UpdateDriverRow();
                tgSuppress.IsChecked = S.SuppressOgh;
                tgHotkeys.IsChecked = S.Hotkeys;
                string omen = S.Key == KeyAction.Show ? Program.DisplayName : S.Key == KeyAction.Cycle ? "cycles" : S.Key == KeyAction.MaxFan ? "max fan" : S.Key == KeyAction.Run ? "runs a command" : null;
                string hk = HotkeyTable.Summary(E.GetHotkeys(), omen);
                if (hk != hotkeySubFor) { hotkeySubFor = hk; txtHotkeysSub.Text = hk; if (hotkeysOpen) BuildHotkeyPanel(); }
                tgEcoBattery.IsChecked = S.EcoOnBattery;
                tgSyncPower.IsChecked = S.SyncWinPower;
                tgAutostart.IsChecked = autostart;
                demoBadge.Visibility = E.Hw.IsDemo && screenshotPath == null ? Visibility.Visible : Visibility.Collapsed;
                bool err = (!E.BiosOk || E.ReadOnly) && !E.Hw.IsDemo;
                errBanner.Visibility = err ? Visibility.Visible : Visibility.Collapsed;
                if (err) txtErr.Text = !E.BiosOk ? "BIOS interface unavailable: " + E.LastError
                    : "Unsupported laptop (board " + E.Board + "). Read-only: nothing is written to the firmware. Run tools\\support-info.cmd and open a GitHub issue to add it.";
                bool firstHere = E.Generic && !E.Hw.IsDemo && !S.InfoDismissed && !Platforms.Reported(E.Board);
                infoBanner.Visibility = firstHere ? Visibility.Visible : Visibility.Collapsed;
                if (firstHere) txtInfo.Text = "You're the first to try this on your laptop. If it works, tell us on Discord or Reddit and we'll mark it verified.";
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
            if (E.GuardActive) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Foreground = Ui.Brush(Ui.Danger); txtFoot.Text = "Thermal guard · ambient " + E.GuardChassis + "°"; return; }
            double age = (DateTime.Now - E.LastHeartbeat).TotalSeconds;
            bool fresh = E.LastHeartbeat != DateTime.MinValue && age < E.S.HeartbeatSec * 2.5;
            dotHb.Fill = Ui.Brush(fresh ? Ui.Ok : Ui.Warn);
            // A firmware without the fan table reports no count; saying "-1 fans" is worse than saying nothing.
            txtFoot.Text = ShortModel() + (E.FanCount > 0 ? " · " + E.FanCount + " fans" : "");
            txtFoot.ToolTip = "Ohman is driving this laptop's firmware" + (fresh ? ", last refreshed " + E.LastHeartbeat.ToString("HH:mm:ss") : "; the refresh is overdue");
        }

        bool sensorsSeen;
        void OnSensors(SensorSnapshot s) {
            E.CpuTemp = s.CpuTemp;
            E.CpuTempNow = s.CpuTempNow;
            if (!double.IsNaN(s.CpuTemp)) { int d = (int)Math.Round(s.CpuTemp); if (d != cpuTipFor) { cpuTipFor = d; chipCpu.ToolTip = "CPU is " + d + "\u00b0 right now"; } }
            E.GpuTemp = s.GpuTemp;
            onBattery = s.OnBattery;
            UpdateTrayTemp(s.CpuTemp);
            if (!double.IsNaN(s.CpuTemp)) { tempTrail.Add(s.CpuTemp); if (tempTrail.Count > 8) tempTrail.RemoveAt(0); sensorsSeen = true; }
            if (!IsVisible) return;          // the rest of this writes text nobody is looking at
            if (cur == Page.Fans) { UpdateCurveLive(); UpdateMaxBlock(); UpdateFanFooter(); }
            bigCpu.Text = double.IsNaN(s.CpuTemp) ? "--" : s.CpuTemp.ToString("0", CultureInfo.InvariantCulture);
            bigGpu.Text = double.IsNaN(s.GpuTemp) ? "--" : s.GpuTemp.ToString("0", CultureInfo.InvariantCulture);
            bigCpu.Foreground = TempBrush(s.CpuTemp);
            bigGpu.Foreground = TempBrush(s.GpuTemp);
            runCpuHead.Text = "CPU" + (double.IsNaN(s.CpuLoad) ? "" : " · " + s.CpuLoad.ToString("0") + "%") + (double.IsNaN(s.CpuMhz) || s.CpuMhz <= 0 ? "" : " · " + (s.CpuMhz / 1000).ToString("0.0") + " GHz");
            runCpuWatts.Text = double.IsNaN(s.CpuWatts) ? "" : " · " + s.CpuWatts.ToString("0") + " W";
            // Only when it changes. Assigning a tooltip invalidates the inline it hangs off, and these two move
            // twice in a session while this runs every couple of seconds.
            string limits = PowerLimits(s);
            if (limits != cpuLimitsTip) { cpuLimitsTip = limits; runCpuWatts.ToolTip = limits; }
            runCpuTail.Text = s.Throttle.Length > 0 ? " · " + s.Throttle : "";
            // Amber while the CPU says it is being held back, and the caption says why: that is the one word
            // the ACPI zone could never supply, and the reason the driver exists on boards with working fans.
            subCpu.Foreground = s.Throttle.Length > 0 ? Ui.Brush(Ui.Warn) : subCpuBrush;
            if (s.CpuFromDriver != cpuTipFromDriver) {
                cpuTipFromDriver = s.CpuFromDriver;
                bigCpu.ToolTip = s.CpuFromDriver ? "Die temperature, read from the CPU itself" : null;
            }
            subGpu.Text = "GPU" + (double.IsNaN(s.GpuLoad) ? "" : " · " + s.GpuLoad.ToString("0") + "%") + (double.IsNaN(s.GpuWatts) ? "" : " · " + s.GpuWatts.ToString("0") + " W");
            txtFootRight.Text = s.BatteryPercent >= 0 && s.BatteryPercent <= 100 ? (s.OnBattery ? "Battery " : "AC · ") + s.BatteryPercent + "%" : "";
        }
        Brush TempBrush(double t) { return double.IsNaN(t) ? Ui.TextB : t >= E.GuardCpuHot ? Ui.Brush(Ui.Danger) : t >= E.P.Guard.WarnAt ? Ui.Brush(Ui.Warn) : Ui.TextB; }
        /// <summary>What the wattage is measured against, for the tooltip on it. Null without the driver: the
        /// limits are the CPU's own registers and nothing else on this machine will say what they are.</summary>
        static string PowerLimits(SensorSnapshot s) {
            string a = double.IsNaN(s.Pl1) ? null : "PL1: " + s.Pl1.ToString("0", CultureInfo.InvariantCulture) + " W";
            string b = double.IsNaN(s.Pl2) ? null : "PL2: " + s.Pl2.ToString("0", CultureInfo.InvariantCulture) + " W";
            if (a == null && b == null) return null;
            return a != null && b != null ? a + " · " + b : (a ?? b);
        }

        void ReadHardwareAsync() {
            if (reading || (!E.BiosOk && !E.Hw.IsDemo)) return;
            reading = true;
            ThreadPool.QueueUserWorkItem(delegate {
                int[] f = null;
                int t = -1;
                try { f = E.Hw.GetFanLevels(); } catch (Exception ex) { Log.Write("read fans: " + ex.Message); }
                try { if (f != null) E.NoteFanLevels(f); } catch { }
                try { t = E.Hw.GetTemperature(); } catch { }
                Dispatcher.BeginInvoke((Action)delegate {
                    if (f != null) {
                        lastFans = f;
                        if (t >= 0) { lastChassis = t; if (t != chassisTipFor) { chassisTipFor = t; chipChassis.ToolTip = "Chassis is " + t + "\u00b0 right now"; } }
                        bigFan1.Text = Level(f[0]);
                        bigFan2.Text = Level(f[1]);
                        UpdateFanStatus();
                        if (cur == Page.Fans) UpdateMaxBlock();
                    }
                    if (t >= 0 && t != lastBiosTemp) {
                        lastBiosTemp = t;
                        GpuLevel g = E.EffectiveGpu;
                        txtHomeStatus.Text = (E.P.HasGpuPower ? (g == GpuLevel.Boost ? "GPU boost" : "GPU base") : E.P.HasPowerGain ? E.CurrentTdp + " W" : "")
                            + (ShowChassis(t) ? " · ambient " + t + "°" : "");
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
            int next = (E.ModeIndex + 1) % E.ModeCount;
            Flash(Engine.ModeNames[next] + " mode", ModeSubs[next], next);
            ApplyModeAsync(next);
        }
        void ToggleMaxWithFlash() {
            bool on = E.S.Fan != FanMode.Max;
            Flash(on ? "Max fan" : "Fans auto", on ? "Both fans to full speed" : "Back to the firmware curve", -1);
            Bg(delegate { E.ToggleMaxFan(); });
        }

        void OnPowerMode(object o, Microsoft.Win32.PowerModeChangedEventArgs e) {
            // The app has no WM_DEVICECHANGE hook, and these two are the only notice it gets that the machine it
            // woke up on is not the machine it went to sleep on: a dock or an undock arrives as one or the other.
            // Cheap to be wrong here (one HID enumeration on next use), expensive to be stale (see Forget).
            WinLighting.Forget();
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) E.OnResume();
            else if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) {
                bool bat = false;
                try { bat = WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Offline; } catch { }
                onBattery = bat;
                PollRate();
                Bg(delegate { E.OnPowerSource(bat); });
            }
        }

        /// <summary>How hard the app works right now. Reading sensors wakes performance counters and, on a hybrid
        /// laptop, sometimes the discrete GPU, so this is the app's largest recurring power cost and it is worth
        /// scaling. In front of you it has to keep up with the eye; behind you it only feeds the tray number and the
        /// fan curve, and on battery even that can wait longer. The per-frame work (the footer, the fan readouts)
        /// has no reader at all when the window is hidden, so its timer stops outright.</summary>
        /// <summary>Whether the chassis reading is worth putting on screen: it has to be a real number, and
        /// it has to come from a board where somebody has seen what that number means.</summary>
        bool ShowChassis(int c) { return c > 0 && E.P != null && E.P.Curve != null && E.P.Curve.UseChassis; }

        void PollRate() {
            // Whoever is reading these numbers sets the floor. The software fan curve steps every 5 s and steps from
            // the last reading it was given, so polling slower than that would make the fans answer a poll interval
            // late, not a saving worth having. With nothing but the tray number waiting on them, they can wait.
            bool curveDrivesFans = !E.ReadOnly && (E.S.Fan == FanMode.Auto || E.S.Fan == FanMode.Custom);
            // Reading the discrete GPU means running nvidia-smi, and on a hybrid laptop that wakes a GPU which was
            // asleep in D3. Doing it on battery with the window shut costs real runtime for a number nobody is
            // looking at, so we stop entirely: the curve reads max(CPU, GPU) and falls back to the CPU alone when
            // the GPU is unknown, and the thermal guard never used the GPU. On AC, or with the window open, or on
            // a machine where the GPU is the only thing there is, it polls as before.
            sensors.SkipGpu = E.GpuMode == 3 || (onBattery && !IsVisible);
            int ms = IsVisible ? E.S.PollMs
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
        /// <summary>Every binding in the table, id = action + 1. One Windows refuses (another program holds it) is
        /// remembered so the panel can say so next to it rather than the shortcut silently doing nothing.</summary>
        void RegisterHotkeys() {
            if (hotkeysRegistered) return;
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            Hotkey[] b = E.GetHotkeys();
            for (int i = 0; i < b.Length; i++) {
                hotkeyBusy[i] = false;
                if (b[i].IsEmpty) continue;
                // NOREPEAT: holding the keys a moment too long sent the hotkey again, and a toggle pressed twice is a
                // toggle not pressed. Max fan went off and straight back on, and read as stuck on.
                if (!RegisterHotKey(h, i + 1, b[i].Mods | MOD_NOREPEAT, b[i].Vk)) { hotkeyBusy[i] = true; Log.Write("hotkey " + b[i] + " (" + HotkeyTable.Names[i] + ") not available: another program has it"); }
            }
            hotkeysRegistered = true;
        }
        void UnregisterHotkeys() {
            if (!hotkeysRegistered) return;
            var h = new WindowInteropHelper(this).Handle;
            for (int i = 1; i <= HotkeyTable.Count; i++) UnregisterHotKey(h, i);
            hotkeysRegistered = false;
        }

        // ---------- customising them ----------
        /// <summary>A filled pencil to open and a tick to close, drawn rather than typed: the icon font's pencil is
        /// a hairline at this size and reads as a scratch on the row.</summary>
        static System.Windows.Shapes.Path PencilGlyph(bool open) {
            const string pencil = "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z";
            const string tick = "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z";
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(open ? tick : pencil), Fill = Ui.Sub, Width = 14, Height = 14, Stretch = Stretch.Uniform };
        }
        void ToggleHotkeyPanel() {
            hotkeysOpen = !hotkeysOpen;
            if (!hotkeysOpen) StopListening();
            // A pencil to open, a tick to close: the same spot, one glyph, no words to wrap the sub-line around.
            btnHotkeys.Content = PencilGlyph(hotkeysOpen);
            btnHotkeys.ToolTip = hotkeysOpen ? "Done" : "Change the shortcuts: click one, then press the keys you want. Esc keeps the old one, Backspace removes it.";
            txtHotkeysSub.Visibility = hotkeysOpen ? Visibility.Collapsed : Visibility.Visible;   // the panel is the sub-line, in full
            if (hotkeysOpen) BuildHotkeyPanel();
            hotkeyPanel.Visibility = hotkeysOpen ? Visibility.Visible : Visibility.Collapsed;
            Remeasure(cur);
        }
        /// <summary>One line per action, name then one key cap, tight rows. The page is about 325 device pixels
        /// wide, which two columns of name and cap do not fit at any size, so five short rows it is. Rebuilt whole
        /// on every change, which is nothing. The hint lives on the pencil's tooltip, not down here.</summary>
        void BuildHotkeyPanel() {
            hotkeyPanel.Children.Clear();
            Hotkey[] b = E.GetHotkeys();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < b.Length; r++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            if (E.HotkeysCustomised) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            for (int i = 0; i < b.Length; i++) {
                int idx = i;
                var name = new TextBlock { Text = HotkeyTable.Names[i], FontFamily = Ui.UiFont, FontSize = 13, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                Grid.SetRow(name, i);
                var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                Grid.SetRow(cell, i); Grid.SetColumn(cell, 1);
                if (listening == i) {
                    Border cap = KeyCap("Press keys…", accent);
                    listeningCap = (TextBlock)cap.Child;
                    cap.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                    cell.Children.Add(cap);
                } else if (b[i].IsEmpty) {
                    cell.Children.Add(KeyCap("none", Ui.Desc));
                } else {
                    Border cap = KeyCap(b[i].ToString(), hotkeyBusy[i] ? Ui.Brush(Ui.Warn) : null);
                    // Amber is the whole signal; the words are on hover, where they cost no width.
                    if (hotkeyBusy[i]) { cap.BorderBrush = Ui.Brush(Ui.Warn); cap.ToolTip = "In use by another program, so it does nothing here. Click to pick a different one."; }
                    cell.Children.Add(cap);
                }
                name.MouseLeftButtonUp += delegate { StartListening(idx); };
                cell.MouseLeftButtonUp += delegate { StartListening(idx); };
                g.Children.Add(name);
                g.Children.Add(cell);
            }
            if (E.HotkeysCustomised) {
                var reset = new TextBlock { Text = "Reset all", FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = accent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(reset, b.Length); Grid.SetColumn(reset, 1);
                reset.MouseLeftButtonUp += delegate { StopListening(); UnregisterHotkeys(); E.ResetHotkeys(); if (HotkeysOn) RegisterHotkeys(); BuildHotkeyPanel(); };
                g.Children.Add(reset);
            }
            hotkeyPanel.Children.Add(g);
        }
        Border KeyCap(string text, Brush fg) {
            return new Border {
                Background = Ui.Pill, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0),
                BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = text, FontFamily = Ui.MonoFont, FontSize = 11, Foreground = fg ?? Ui.TextB }
            };
        }
        /// <summary>Wait for the next key press and bind it. The global hotkeys come off while waiting, so the
        /// combination being typed reaches this window rather than firing whatever it is bound to now.</summary>
        void StartListening(int idx) {
            listening = idx;
            UnregisterHotkeys();
            BuildHotkeyPanel();
            Focus();
        }
        /// <summary>What the switch says, not E.S.Hotkeys: the switch handler posts the engine update to a queue,
        /// and this can run before that item does.</summary>
        bool HotkeysOn { get { return tgHotkeys.IsChecked == true; } }
        void StopListening() {
            if (listening < 0) return;
            listening = -1;
            if (HotkeysOn) RegisterHotkeys();
            if (hotkeysOpen) BuildHotkeyPanel();
        }
        void OnHotkeyCapture(object o, KeyEventArgs e) {
            if (listening < 0) return;
            e.Handled = true;
            int idx = listening;
            var action = (HotkeyAction)idx;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { StopListening(); return; }
            if (key == Key.Back) { E.SetHotkey(action, Hotkey.None); StopListening(); return; }
            Hotkey h;
            if (!Hotkey.FromKey(key, Keyboard.Modifiers, out h)) { ShowHeldModifiers(); return; }   // a modifier on its own: show it, keep waiting
            if (!h.Valid) { ShowToast("Hold Ctrl, Alt, Shift or Win with it, or use an F-key", true); return; }
            string[] before = E.SnapshotHotkeys();
            E.SetHotkey(action, h);
            listening = -1;
            hotkeyBusy[idx] = false;                 // a verdict on the old key is not one on the new
            if (HotkeysOn) RegisterHotkeys();
            // Windows said no: another program owns that combination. The old binding comes back, and the
            // toast says why nothing changed rather than leaving a shortcut on screen that does nothing.
            // Only a registration that ran can say so; with the switch off the new key is simply kept.
            if (HotkeysOn && hotkeyBusy[idx]) {
                UnregisterHotkeys();
                E.RestoreHotkeys(before);          // including any action the new key was taken from
                if (HotkeysOn) RegisterHotkeys();
                ShowToast(h + " is already used by another program", true);
            }
            BuildHotkeyPanel();
        }
        /// <summary>The cap follows the hands: Ctrl held reads "Ctrl+…", Ctrl and Alt read "Ctrl+Alt+…", and it
        /// goes back to "Press keys…" when they are let go, so the owner sees the combination forming.</summary>
        void ShowHeldModifiers() {
            if (listening < 0 || listeningCap == null) return;
            uint mods = 0;
            ModifierKeys m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= Hotkey.Ctrl;
            if ((m & ModifierKeys.Alt) != 0) mods |= Hotkey.Alt;
            if ((m & ModifierKeys.Shift) != 0) mods |= Hotkey.Shift;
            if ((m & ModifierKeys.Windows) != 0) mods |= Hotkey.Win;
            if (mods == 0) { listeningCap.Text = "Press keys\u2026"; return; }
            string t = new Hotkey(mods, 'X').ToString();
            listeningCap.Text = t.Substring(0, t.Length - 1) + "\u2026";
        }

        // ---------- the thermal guard's rule, as a sentence ----------
        static string HoldText(int s) { return s % 60 == 0 && s >= 60 ? (s / 60) + " min" : s + " s"; }
        static TextBlock Word(string t) { return new TextBlock { Text = t, FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Desc, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }; }
        static string[] Degrees(int lo, int hi) { var r = new string[hi - lo + 1]; for (int i = 0; i < r.Length; i++) r[i] = (lo + i) + "\u00b0"; return r; }
        /// <summary>"When CPU [95°] or chassis [62°], turn fans [Max] for (dial) 1 min". The words are the sub-line's
        /// and the numbers are the controls, so there is nothing to open and the rule reads as the rule.</summary>
        void BuildGuardLine() {
            guardLine.Children.Clear();
            int tj = 100;
            try { if (E.Cpu != null) { int t = E.Cpu.Poll(false).TjMax; if (t > 0) tj = t; } } catch { }   // light: TjMax is cached, no need to spend the energy window
            // Every list contains the value the engine is running, wherever it came from: a hand-edited file, a
            // ceiling the learner has since moved, a limit past the usual range. The words must never say one
            // rule while the guard runs another.
            cpuLo = Math.Min(70, E.GuardCpuHot); chassisLo = Math.Min(40, E.GuardChassisHot);
            chipCpu = new ValueLink { Items = Degrees(cpuLo, Math.Max(Math.Max(90, tj - 5), E.GuardCpuHot)) };
            chipChassis = new ValueLink { Items = Degrees(chassisLo, Math.Max(75, E.GuardChassisHot)) };
            // Max, then a ladder of levels down to half of the ceiling, in the unit this board shows fans in.
            int ceiling = E.P.Curve.Ceiling;
            var levels = new System.Collections.Generic.List<int> { 0 };
            for (int pct = 90; pct >= 50; pct -= 10) { int lvl = (int)Math.Round(ceiling * pct / 100.0); if (!levels.Contains(lvl)) levels.Add(lvl); }
            if (E.GuardLevel > 0 && !levels.Contains(E.GuardLevel)) { levels.Add(E.GuardLevel); levels.Sort(); levels.Reverse(); levels.Remove(0); levels.Insert(0, 0); }
            guardLevels = levels.ToArray();
            var names = new string[guardLevels.Length];
            for (int i = 0; i < names.Length; i++) names[i] = guardLevels[i] == 0 ? "max" : E.Rpm(guardLevels[i]);
            chipFans = new ValueLink { Items = names, ToolTip = "A stalled fan always gets max, whatever this says" };
            var holdList = new System.Collections.Generic.List<int>(HoldChoices);
            if (!holdList.Contains(E.GuardHoldSeconds)) { holdList.Add(E.GuardHoldSeconds); holdList.Sort(); }
            holdChoices = holdList.ToArray();
            var holds = new string[holdChoices.Length];
            for (int i = 0; i < holds.Length; i++) holds[i] = HoldText(holdChoices[i]);
            chipHold = new ValueLink { Items = holds, ToolTip = "How long it holds on after both readings are back under" };
            foreach (ValueLink v in new[] { chipCpu, chipChassis, chipFans, chipHold }) v.Editable = guardOpen;
            Action changed = delegate { if (syncing) return; guardDebounce.Stop(); guardDebounce.Start(); };
            chipCpu.Changed += delegate { changed(); };
            chipChassis.Changed += delegate { changed(); };
            chipFans.Changed += delegate { changed(); };
            chipHold.Changed += delegate { changed(); };
            // Word by word, so the line runs up to the pencil before it breaks.
            foreach (string w in "When CPU".Split(' ')) guardLine.Children.Add(Word(w));
            guardLine.Children.Add(chipCpu);
            foreach (string w in "or chassis".Split(' ')) guardLine.Children.Add(Word(w));
            guardLine.Children.Add(chipChassis);
            foreach (string w in "turn fans".Split(' ')) guardLine.Children.Add(Word(w));
            guardLine.Children.Add(chipFans);
            guardLine.Children.Add(Word("for"));
            guardLine.Children.Add(chipHold);
        }
        /// <summary>The controls from the settings, inside Synced so their Changed does not write them back.</summary>
        void SyncGuardLine() {
            // Rebuild when the engine's rule is not on the lists, so the sentence never shows a neighbour of it.
            bool stale = E.GuardCpuHot < cpuLo || E.GuardCpuHot >= cpuLo + chipCpu.Items.Length
                || E.GuardChassisHot < chassisLo || E.GuardChassisHot >= chassisLo + chipChassis.Items.Length
                || Array.IndexOf(guardLevels, E.GuardLevel) < 0 || Array.IndexOf(holdChoices, E.GuardHoldSeconds) < 0;
            if (stale) BuildGuardLine();
            Synced(delegate {
                chipCpu.Index = E.GuardCpuHot - cpuLo;
                chipChassis.Index = E.GuardChassisHot - chassisLo;
                chipFans.Index = Array.IndexOf(guardLevels, E.GuardLevel);
                chipHold.Index = Array.IndexOf(holdChoices, E.GuardHoldSeconds);
            });
        }
        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
            if (msg == WM_HOTKEY) {
                int id = wp.ToInt32() - 1;
                if (id >= 0 && id < HotkeyTable.Count && PassAltGr(id)) { handled = true; return IntPtr.Zero; }
                if (id >= 0 && id <= 2) { Flash(Engine.ModeNames[id] + " mode", ModeSubs[id], id); ApplyModeAsync(id); }
                else if (id == (int)HotkeyAction.MaxFan) ToggleMaxWithFlash();
                else if (id == (int)HotkeyAction.Cycle) CycleWithFlash();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>Windows reports AltGr as Ctrl+Alt, so on a keyboard where AltGr+E is a letter the Eco hotkey took
        /// it: a Polish owner could not type "ę". When the right Alt is held and the foreground window's layout
        /// types a character with this combination, the key goes back to that window instead: the hotkey steps
        /// aside, the key is sent again, and the hotkey returns a moment later. Left Ctrl+Alt still switches.</summary>
        bool PassAltGr(int id) {
            if ((GetAsyncKeyState(0xA5) & 0x8000) == 0) return false;          // right Alt not held: Ctrl+Alt on purpose
            Hotkey k = E.GetHotkey((HotkeyAction)id);
            IntPtr layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
            if (!k.TypesCharacter(layout)) return false;
            IntPtr h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, id + 1);
            byte scan = (byte)MapVirtualKey(k.Vk, 0);
            keybd_event((byte)k.Vk, scan, 0, UIntPtr.Zero);
            keybd_event((byte)k.Vk, scan, 2, UIntPtr.Zero);                     // KEYEVENTF_KEYUP
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += delegate {
                t.Stop();
                // Only if nothing re-registered or switched the hotkeys off in the meantime.
                if (hotkeysRegistered && !exiting && E.GetHotkey((HotkeyAction)id).Same(k)) RegisterHotKey(h, id + 1, k.Mods | MOD_NOREPEAT, k.Vk);
            };
            t.Start();
            return true;
        }

        void OnLoaded(object o, RoutedEventArgs e) {
            string page = Program.StartPage;
            if (openSettings) page = "settings";
            if (Program.KeyboardTest) page = "keyboard";
            if (page == "fans") Navigate(Page.Fans, false);
            else if (page == "keyboard" && E.Light != null) Navigate(Page.Keyboard, false);
            else if (page == "settings") Navigate(Page.Settings, false);
            else if (page == "update") ShowUpdateRow();          // where the rail button goes: settings, at the update row
            else if (page == "driver") ShowDriverRow();          // settings, at the driver row (screenshot aid)
            else if (page == "hotkeys") { Navigate(Page.Settings, false); ToggleHotkeyPanel(); }   // settings, hotkey panel open (screenshot aid)
            else if (page == "guard") { Navigate(Page.Settings, false); btnGuard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            else if (page == "guardrow") Navigate(Page.Settings, false);      // the row closed, for screenshots
            Morph(false);
            if (Program.JustUpdated) ShowToast("Updated to " + Program.Version, false);
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
                    Morph(false);
                    root.UpdateLayout();
                    // --page update / driver: the eased scroll never ran (no frames rendered off-screen), so put
                    // the row in view now that the page has its final layout.
                    FrameworkElement at = Program.StartPage == "driver" ? driverRow : Program.StartPage == "update" ? updateRow : Program.StartPage == "hotkeys" ? (FrameworkElement)hotkeyPanel : Program.StartPage == "guard" || Program.StartPage == "guardrow" ? (FrameworkElement)txtGuardSub.Parent : null;
                    if (at != null && cur == Page.Settings) {
                        try {
                            var content = scroll.Content as FrameworkElement;
                            if (content != null) { scroll.ScrollToVerticalOffset(Math.Max(0, at.TranslatePoint(new Point(0, 0), content).Y - 96)); root.UpdateLayout(); }
                        } catch { }
                    }
                    SnapshotElement(root, screenshotPath);
                    Log.Write("screenshot saved " + screenshotPath);
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
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }

        void Position() {
            var S = E.S;
            if (screenshotPath != null) { WindowStartupLocation = WindowStartupLocation.Manual; Left = -4000; Top = 0; ShowInTaskbar = false; return; }   // rendered off-screen, out of the way
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;   // monitors left of/above the primary have negative coordinates
            if (S.WinX != -1 && S.WinY != -1 && S.WinX >= vl && S.WinY >= vt && S.WinX < vl + SystemParameters.VirtualScreenWidth - 100 && S.WinY < vt + SystemParameters.VirtualScreenHeight - 100) {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = S.WinX;
                Top = S.WinY;
            } else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        public void ShowPanel() {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { Morph(false); PlaceRailPill(false); });
        }
        void TogglePanel() { if (IsVisible) HideToTray(); else ShowPanel(); }
        void HideToTray() {
            StopListening();
            StopMorph();
            E.S.Save();
            Hide();
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
        bool resetting, quietExit;
        void ExitApp() {
            if (exiting) return;
            exiting = true;
            if (resetting) { try { E.S.Delete(); } catch { } } else { try { E.S.Save(); } catch { } }
            try { UnregisterHotkeys(); } catch { }
            try { uiTimer.Stop(); } catch { }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            try { if (osd != null) osd.Close(); } catch { }
            try { if (trayTempIcon != null) { IntPtr h = trayTempIcon.Handle; trayTempIcon.Dispose(); DestroyIcon(h); } } catch { }
            try { E.Park(quietExit); } catch { }   // before Dispose: the timers must still be alive to write
            // Sensors first. Its thread reads the CPU's registers through a handle the engine owns, so disposing
            // the engine first left a tick in flight holding a closed handle and wrote a driver failure into the
            // log of every clean exit.
            try { sensors.Dispose(); } catch { }
            try { E.Dispose(); } catch { }
            Log.Write("exit");
            // Last, because everything above still logs. The dialog said the log goes too, so it has to.
            if (resetting) { try { Log.Delete(); } catch { } }
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
            toastTimer.Stop();
            toastTimer.Start();
        }

        // ---------- autostart (scheduled task with highest privileges = no UAC prompt at logon) ----------
        /// <summary>One logon task per Windows user ("Ohman-<user>"): a single shared name meant the second user to
        /// switch autostart on silently took the first one's away.</summary>
        static string TaskName {
            get {
                var sb = new System.Text.StringBuilder(Program.AppName + "-");
                foreach (char c in Environment.UserName) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
                return sb.ToString();
            }
        }
        void QueryAutostartAsync() {
            Slow(delegate {
                bool on = RunSchtasks("/Query /TN " + TaskName) == 0;
                if (!on && !E.Hw.IsDemo && RunSchtasks("/Query /TN " + Program.AppName) == 0) {
                    // The task from before per-user names. Adopt it if it starts Ohman for this user; leave it alone
                    // if it is somebody else's.
                    string old = SchtasksOut("/Query /TN " + Program.AppName + " /XML");
                    string sid = "";
                    try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } catch { }
                    if (sid.Length > 0 && old.IndexOf(sid, StringComparison.OrdinalIgnoreCase) >= 0) {
                        Log.Write("autostart: moving this user's logon task to " + TaskName);
                        SetAutostart(true);
                        if (autostart) RunSchtasks("/Delete /TN " + Program.AppName + " /F");
                        on = autostart;
                    }
                }
                if (on && !E.Hw.IsDemo) {
                    // tasks made by 1.1 stop the app when the laptop goes on battery; re-register those once
                    string xml = SchtasksOut("/Query /TN " + TaskName + " /XML");
                    if (xml.IndexOf("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase) >= 0 || xml.IndexOf("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase) >= 0) {
                        Log.Write("logon task has battery restrictions; re-registering it");
                        SetAutostart(true);
                        on = autostart;
                    }
                }
                if (!on && E.S.FirstRun && !E.Hw.IsDemo) {              // first launch: start with Windows like every vendor app does; the switch turns it off
                    SetAutostart(true);
                    on = autostart;
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
                rc = RunSchtasks("/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
                try { System.IO.File.Delete(tmp); } catch { }
            } else rc = RunSchtasks("/Delete /TN " + TaskName + " /F");
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
                var outLines = new System.Text.StringBuilder();
                using (var p = new Process()) {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outLines) outLines.Append(e.Data).Append("\n"); };
                    p.Start();
                    p.BeginOutputReadLine();
                    if (p.WaitForExit(5000)) { p.WaitForExit(); return outLines.ToString(); }
                    try { p.Kill(); } catch { }
                    p.WaitForExit(2000);
                    if (!p.HasExited) Log.Write("could not stop schtasks: " + args);
                    Log.Write("schtasks timed out: " + args);
                    return "";
                }
            } catch (Exception ex) { Log.Write("schtasks: " + ex.Message); return ""; }
        }
        static int RunSchtasks(string args) {
            try {
                using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) {
                    if (p.WaitForExit(5000)) return p.ExitCode;
                    try { p.Kill(); } catch { }
                    p.WaitForExit(2000);
                    if (!p.HasExited) Log.Write("could not stop schtasks: " + args);
                    Log.Write("schtasks timed out: " + args);
                    return -1;
                }
            } catch (Exception ex) { Log.Write("schtasks: " + ex.Message); return -1; }
        }
    }
}
