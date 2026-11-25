// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the main window (layout in Ui.axaml): a rail on the left with Home, Fans, Keyboard, Benchmark
// and Settings, one page visible at a time; the window morphs to each page's size. Also the tray icon, hotkeys and the
// live readouts. Every size and colour here comes from the panel design.
// The Avalonia port of src\Ui.cs: same class, same methods in the same order, same fields, same comments, so a
// change there can be carried over from its diff.
//
// Differences from the WPF original
//  - Types only, unless listed here: FrameworkElement is Control, Brush is IBrush, Visibility is IsVisible,
//    ActualWidth is Bounds.Width, MouseLeftButtonUp is Ui.LeftUp, element.ToolTip is Ui.Tip, BeginAnimation is
//    Ui.Animate / Ui.StopAnimation, CompositionTarget.Rendering is Ui.Rendering (all in Theme.cs).
//  - Two methods are renamed because Avalonia's Window already has members of those names: Bounds() is
//    SliderBounds(), Position() is PlaceWindow().
//  - What WPF supplies and Avalonia does not is declared once, near the fields: Dispatcher.BeginInvoke (a shim over
//    Dispatcher.UIThread.Post), Left / Top (Window.Position in DIPs), WorkArea (SystemParameters.WorkArea), heldMods
//    (Keyboard.Modifiers, kept from the last key or pointer event), ScrollTo / ScrollableHeight, LeftDown.
//    MessageBox is in Dialogs.cs: a small modal window with the same Show(...) calls and results.
//  - The window. No title bar and no resize border, as in WPF (SystemDecorations.None). Windows 11 rounds the
//    corners and draws a one pixel border around a borderless window (the three DwmSetWindowAttribute calls); here
//    the corners are square and the border is a one pixel line drawn inside the window's edge (live windows only,
//    not in screenshots). Dragging is BeginMoveDrag, which hands the move to the window manager.
//  - The morph. The same four edge springs, driven by Ui.Rendering, each frame applied as Window.Position plus
//    Width/Height. The WM_ENTERSIZEMOVE / SetWindowPos machinery has no counterpart. Position is honoured on X11
//    and under XWayland; a compositor that ignores it leaves the window anchored at its top-left corner.
//  - The tray is Avalonia's TrayIcon with a NativeMenu (a StatusNotifierItem on Linux): the same items in the same
//    order. A left click toggles the panel where the desktop reports one. There are no balloon tips; the benchmark's
//    were the only ones. The icons are drawn with Avalonia instead of System.Drawing: DrawMark returns a bitmap and
//    MakeIcon / TempIcon a WindowIcon. The number on the tray icon is IBM Plex Sans SemiBold, not Segoe UI Bold.
//    WriteIco and Dib32 (the .ico writer build.cmd uses) are not here.
//  - Global hotkeys are GlobalKeys.cs: XGrabKey on an X11 session, nothing on Wayland (the row says so). The
//    WM_HOTKEY hook is OnHotkey. The AltGr pass-through (PassAltGr) is gone: X11 does not report AltGr as Ctrl+Alt.
//    A key event becomes a Hotkey in FromKey here (WPF: KeyNames.FromKey, which takes a WPF key).
//  - Power events. There is no SystemEvents.PowerModeChanged and no display-off broadcast. NotePower forwards a
//    change of power source from the sensor snapshot and calls E.OnResume when the wall clock has jumped past the
//    monotonic one (a suspend). E.OnDisplay is never called.
//  - Autostart is the core's Autostart (an XDG autostart entry), not a scheduled task, so the schtasks helpers
//    (TaskXml, SchtasksOut, RunSchtasks, SelfExe, FullPath) are gone.
//  - Rows and words that only make sense on one system follow Host's flags (HostRows): "Take over the OMEN key",
//    the system power profile row, the benchmark overlay row, the Windows lighting choice, the driver's install /
//    update / remove links. Restart is "systemctl reboot" behind the same question.
//  - Every TextBlock built here is a WpfTextBlock (Controls.cs), which measures a line the way WPF does, so the
//    pages come out the same height as on Windows.
//  - Tool tips on a Run (the CPU wattage, the big CPU number) sit on the whole TextBlock: an Avalonia Run takes none.
//    The driver's name in its row is a Run the line hit-tests itself (WPF: a Hyperlink).
//  - Text is spaced as WPF's Display mode spaces it (WpfAdvance) and wrapped lines are pitched as it pitches them
//    (WpfLines), so captions break where they break on Windows.
//  - The benchmark page is BenchUi.cs, as on Windows, and sits in its slot (BenchHost). What Windows says in a
//    tray balloon when a run ends while the window is hidden (done, not saved, ended early) is said in the
//    on-screen flash here: the tray icon has no balloons, and nothing to click that opens the result. Every
//    place this file touches the benchmark is marked "// BENCH:".
//  - "// CORE:" marks the places that wait on, or stand in for, something the shared core does not offer on Linux.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WPath = Avalonia.Controls.Shapes.Path;

namespace Ohman {


    public sealed class MainWindow : Window {
        enum Page { Home = 0, Fans = 1, Keyboard = 2, Settings = 3, Bench = 4 }
        const double RailW = 62, PageW = 398, KbdPageW = 638, SettingsH = 640;

        Osd osd;
        public void Flash(string title, string detail, int modeIndex) {
            if (osd == null) NewOsd();
            string[] paths = { LEAF, SCALE, BOLT };
            osd.Flash(title, detail, modeIndex >= 0 ? Ui.ModeColor(modeIndex) : Ui.Accent.Color, modeIndex >= 0 ? paths[modeIndex] : FAN);
        }
        const string FAN = "M12 12 C9.6 8.4 8.4 5 10.3 2 C14.6 2.4 15.9 6.6 12 9 M12 12 C15.3 13.2 17.6 16.2 16.6 19.6 C12.5 20.6 9.4 17.6 12 15 M12 12 C11.1 15.4 8 17.8 4.6 16.4 C4 12.2 7.2 9.8 12 9";
        const string LEAF = "M4 20 C4 11 10 4 20 4 C20 13 14 20 4 20 Z M4 20 L13 11";
        const string SCALE = "M12 3 L12 21 M8 21 L16 21 M4 7 L20 7 M4 7 L1.5 13 A2.5 2 0 0 0 6.5 13 Z M20 7 L17.5 13 A2.5 2 0 0 0 22.5 13 Z";
        const string BOLT = "M13 2 L4 14 L11 14 L10 22 L20 9 L13 9 Z";
        const string ICO_HOME_RING = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 Z";
        const string ICO_HOME_DOT = "M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z";
        const string ICO_UPDATE = "M12 4 V14.2 M7.6 10.2 L12 14.8 L16.4 10.2 M4.6 19 H19.4";
        const string ICO_FANS = "M3 8.5 C5.5 5.5 8.5 11.5 12 8.5 C15.5 5.5 18.5 11.5 21 8.5 M3 15.5 C5.5 12.5 8.5 18.5 12 15.5 C15.5 12.5 18.5 18.5 21 15.5";
        const string ICO_BENCH = "M4.2 17.5 A8.5 8.5 0 1 1 19.8 17.5 M12 13.5 L16.2 9.3", ICO_BENCH_DOT = "M12 12 A1.5 1.5 0 1 0 12 15 A1.5 1.5 0 1 0 12 12 Z";
        const string ICO_KBD = "M2.5 7.5 A2 2 0 0 1 4.5 5.5 H19.5 A2 2 0 0 1 21.5 7.5 V16.5 A2 2 0 0 1 19.5 18.5 H4.5 A2 2 0 0 1 2.5 16.5 Z M6 9.5 H6.4 M9.8 9.5 H10.2 M13.6 9.5 H14 M17.4 9.5 H17.8 M6 12.5 H6.4 M9.8 12.5 H10.2 M13.6 12.5 H14 M17.4 12.5 H17.8 M7.5 15.5 H16.5";

        readonly Engine E;
        readonly Sensors sensors;
        readonly string screenshotPath;
        readonly bool openSettings;
        Control root;
        Grid pageHost;
        ScrollViewer scroll;
        readonly Control[] pages = new Control[5];
        Page cur = Page.Home;
        bool pageShown;
        readonly NavBtn[] nav = new NavBtn[5];
        // BENCH: benchmark
        Bench bench;
        BenchPage benchPage;
        BenchPill pill;
        Border railPill;
        TranslateTransform railPillT;
        Canvas railCanvas;
        Control rail, logoHost;
        StackPanel navBottom;
        ColorSource accentSrc;
        SolidColorBrush accent;
        // home
        TextBlock txtHomeTitle, txtHomeStatus, subCpu, subGpu, txtPower, txtFoot, txtFootRight, txtLightSub, txtErr, txtInfo, btnSupport, btnReset;
        Run bigCpu, bigGpu, bigFan1, bigFan2;
        Control demoBadge, errBanner, infoBanner, powerRow, lightRow;
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
        Control curveBlock, maxBlock, manualBlock, optsAuto, optsCurve, optsMax, optsManual;
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
        Control selectRow, colorEditor, effectEditor, kbdInfo, levelInline;
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
        Control driverBanner, driverClose, driverRow;
        Run runCpuHead, runCpuWatts, runCpuTail;    // the CPU caption in three pieces, so the limits can hang off the wattage alone
        string cpuLimitsTip = "?";
        bool cpuTipFromDriver;
        enum DriverState { Busy, NotInstalled, RestartPending, Off, Outdated, Ready, Broken }
        DriverState driverState = DriverState.NotInstalled;   // what the row is showing, so the click knows what it means
        IBrush subCpuBrush;
        string driverRowFor = "?";          // the state the row was last built for, so Refresh does not rebuild it every tick
        Border updateRow;
        string updateTitleFor = "?";        // staged version the title was last built for ("" = none); "?" = not built yet
        StackPanel updateText;
        NavBtn navUpdate;
        Control keyCmdRow, gfxRow, hzRow, lowHzRow, gpuRow;
        TextBox txtKeyCmd;
        Ellipse keyDot;
        ToggleButton tgSuppress, tgHotkeys, tgAutostart, tgEcoBattery, tgSyncPower, tgLowHzBattery, tgTrayTemp, tgGuard, tgUpdateAuto, tgLightPage, tgBenchPill;
        Border lightPageRow;
        TextBlock txtLightPageSub;
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
        TextBlock txtGuardSub, txtGuardRelease, txtMaxCoolSub, txtKeyCmdHint;
        Button btnClose;
        Border toast;
        TextBlock txtToast;
        readonly List<TrayItem> trayHz = new List<TrayItem>();
        TrayIcon tray;
        readonly TrayItem[] trayModes = new TrayItem[3];
        readonly WindowIcon[] icons = new WindowIcon[3];
        readonly WindowIcon[] appIcons = new WindowIcon[3];
        bool syncing, exiting, reading, autostart;
        int shownMode = -1, lastBiosTemp = -1;
        int[] lastFans;
        readonly List<double> tempTrail = new List<double>();
        DispatcherTimer uiTimer, fanDebounce, powerDebounce, curveDebounce, learnTimer, toastTimer;
        int curvePending;                          // curve saves posted to the worker and not yet applied
        bool curveEditGpu;                         // the curve the last drag edited
        IntPtr hwnd;                                       // the window's platform handle; zero until it has opened, and always under --screenshot
        bool hotkeysRegistered, hotkeysNoted;
        bool onBattery;                                    // last known power source; PollRate uses it

        static readonly string[] ModeSubs = {
            "Power saver profile · GPU base power",
            "Default thermal policy · GPU boost",
            "Performance thermal policy · GPU max"
        };

        // ---------- what WPF supplies and Avalonia does not ----------
        /// <summary>Dispatcher.BeginInvoke, so the calls read as they do in the WPF window.</summary>
        sealed class Disp {
            public void BeginInvoke(Action a) { Avalonia.Threading.Dispatcher.UIThread.Post(a); }
            public void BeginInvoke(DispatcherPriority p, Action a) { Avalonia.Threading.Dispatcher.UIThread.Post(a, p); }
        }
        readonly Disp Dispatcher = new Disp();
        /// <summary>Keyboard.Modifiers: what the last key or pointer event said was held.</summary>
        KeyModifiers heldMods;
        static KeyModifiers Held(KeyEventArgs e, bool down) {
            KeyModifiers k = KeyModifiers.None;
            switch (e.Key) {
                case Key.LeftCtrl: case Key.RightCtrl: k = KeyModifiers.Control; break;
                case Key.LeftAlt: case Key.RightAlt: k = KeyModifiers.Alt; break;
                case Key.LeftShift: case Key.RightShift: k = KeyModifiers.Shift; break;
                case Key.LWin: case Key.RWin: k = KeyModifiers.Meta; break;
            }
            // X11 reports the state from before the event, so the key itself is added on the way down and taken off on the way up
            return down ? e.KeyModifiers | k : e.KeyModifiers & ~k;
        }
        /// <summary>Window.Left / Window.Top, in DIPs.</summary>
        double Scale { get { double k = DesktopScaling; return k > 0 ? k : 1; } }
        double Left { get { return Position.X / Scale; } set { Position = new PixelPoint((int)Math.Round(value * Scale), Position.Y); } }
        double Top { get { return Position.Y / Scale; } set { Position = new PixelPoint(Position.X, (int)Math.Round(value * Scale)); } }
        /// <summary>SystemParameters.WorkArea: the working area of the screen the window is on, in DIPs.</summary>
        Rect WorkArea {
            get {
                try {
                    var s = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                    if (s != null) { double k = s.Scaling > 0 ? s.Scaling : 1; var r = s.WorkingArea; return new Rect(r.X / k, r.Y / k, r.Width / k, r.Height / k); }
                } catch { }
                return new Rect(0, 0, 1920, 1080);
            }
        }
        double ScrollableHeight { get { return Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height); } }
        void ScrollTo(double y) { scroll.Offset = new Vector(scroll.Offset.X, y); }
        static bool LeftDown(PointerPressedEventArgs e) { return e.GetCurrentPoint(null).Properties.IsLeftButtonPressed; }

        /// <summary>The width of text, the way WPF sets it. In the Display text mode the WPF window uses, every glyph's
        /// advance is rounded to a whole device pixel; Avalonia keeps the font's own fractional advances. The glyphs
        /// are the same, but a line of them comes out a little longer on Windows (IBM Plex Sans) or a little shorter
        /// (IBM Plex Mono, whose one advance of 0.6 em rounds down at most sizes), enough to wrap a caption one word
        /// earlier or later. Mono is exact: its one advance, rounded. Sans is the average its text gains per letter.</summary>
        static double TextScale = 1;
        static void WpfAdvance(TextBlock tb) {
            double k = TextScale > 0 ? TextScale : 1, fs = tb.FontSize;
            var f = tb.FontFamily;
            if (f == null || fs <= 0 || plainAdvance) return;
            bool mono = f.Name != null && f.Name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0;
            bool sans = !mono && tb.FontWeight < FontWeight.Medium && f.Name != null && f.Name.IndexOf("Plex", StringComparison.OrdinalIgnoreCase) >= 0;   // measured on the regular weight; the heavier ones already agree
            double ls = mono ? Math.Round(0.6 * fs * k, MidpointRounding.AwayFromZero) / k - 0.6 * fs : sans ? 0.11 / k : 0;
            if (Math.Abs(tb.LetterSpacing - ls) > 0.001) tb.LetterSpacing = ls;
        }
        /// <summary>The flash is a window of its own and WPF formats its text in the default (Ideal) mode, with the
        /// font's own advances: built with the spacing switched off.</summary>
        static bool plainAdvance;
        void NewOsd() {
            plainAdvance = true;
            try { osd = new Osd(); } finally { plainAdvance = false; }
            Program.HeadlessScale(osd);
        }
        static MainWindow() {
            TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
            TextBlock.FontFamilyProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
            TextBlock.FontWeightProperty.Changed.AddClassHandler<TextBlock>(delegate(TextBlock tb, AvaloniaPropertyChangedEventArgs e) { WpfAdvance(tb); });
        }

        public MainWindow(Engine engine, Sensors s, string screenshot, bool settingsOpen) {
            E = engine;
            sensors = s;
            screenshotPath = screenshot;
            openSettings = settingsOpen;
            Ui.LoadFonts();
            Title = Program.DisplayName;
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            Background = Ui.Card;
            Width = RailW + PageW;
            Height = 600;
            SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = true;
            UseLayoutRounding = true;
            Program.HeadlessScale(this);
            try { TextScale = screenshotPath != null ? Program.ShotScale : Screens.Primary != null ? Screens.Primary.Scaling : 1; } catch { TextScale = 1; }
            root = LoadXaml();
            Content = root;
            root.Resources["UiFont"] = Ui.UiFont;
            root.Resources["MonoFont"] = Ui.MonoFont;
            // the live accent: a bound brush behind the XAML's DynamicResource references and the code-built controls
            accentSrc = new ColorSource { Color = Ui.ModeColor(E.ModeIndex) }; accent = accentSrc.MakeBrush();
            root.Resources["Accent"] = accent;
            Ui.Accent = accent;
            FindAll();
            HostRows();
            SliderBounds();
            BuildRail();
            BuildHome();
            BuildFans();
            BuildKeyboard();
            BuildSettings();
            BuildBench();
            BuildIcons();
            BuildTray();
            Wire();
            PlaceWindow();
            GuardText();
            if (!E.P.HasPowerGain) powerRow.IsVisible = false;
            SetAccent(E.ModeIndex, false);
            Navigate(Page.Home, false);
            root.Measure(new Size(Width, double.PositiveInfinity));
            if (pageHost.DesiredSize.Height > 100) Height = Math.Ceiling(pageHost.DesiredSize.Height);    // first frame already at the right size
            // This catches a page getting shorter. It cannot catch one getting taller: pageHost is aligned to the top
            // inside a clipping grid, so its height is min(what the page wants, what the window has) and a page
            // that wants more than the window already is reports no change at all. Content that grows calls Remeasure.
            pageHost.SizeChanged += delegate { Morph(true); };

            E.StateChanged += delegate { Dispatcher.BeginInvoke((Action)Refresh); };
            E.Toast += delegate(string m, bool err) { Dispatcher.BeginInvoke((Action)delegate { ShowToast(m, err); }); };
            E.KeyPressed += delegate(KeyAction a) { Dispatcher.BeginInvoke((Action)delegate { OnKey(a); }); };
            E.AnyKeyEvent += delegate(uint id, uint data) { Dispatcher.BeginInvoke((Action)delegate { if (!E.Learning) UpdateKeyStatus(); }); };
            sensors.Updated += delegate(SensorSnapshot snap) { Dispatcher.BeginInvoke((Action)delegate { OnSensors(snap); }); };
            GlobalKeys.Pressed += delegate(int id) { Dispatcher.BeginInvoke((Action)delegate { OnHotkey(id - 1); }); };

            Opened += OnSourceInit;
            Loaded += OnLoaded;
            Closing += delegate(object o, WindowClosingEventArgs ce) { if (!exiting) { ce.Cancel = true; HideToTray(); } };
            Program.SessionEnding += delegate { quietExit = true; ExitApp(); };   // logout/shutdown: leave cleanly, and quietly
            PropertyChanged += delegate(object o, AvaloniaPropertyChangedEventArgs pe) {
                if (pe.Property == WindowStateProperty) { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } }
                else if (pe.Property == IsVisibleProperty) { PollRate(); if (IsVisible) ReadHardwareAsync(); }
            };
            PositionChanged += delegate { if (IsVisible && WindowState == WindowState.Normal && Left > -30000 && !morphing && screenshotPath == null) { E.S.WinX = (int)Left; E.S.WinY = (int)Top; } };
            KeyDown += delegate(object o, KeyEventArgs ke) {
                if (FocusManager != null && FocusManager.GetFocusedElement() is TextBox) return;
                if (ke.Key == Key.Escape) HideToTray();
                else if ((ke.Key == Key.Left || ke.Key == Key.Right) && cur == Page.Bench) { benchPage.Flip(ke.Key == Key.Left ? -1 : 1); ke.Handled = true; }   // BENCH
            };
            AddHandler(PointerPressedEvent, delegate(object o, PointerPressedEventArgs pe) { heldMods = pe.KeyModifiers; }, RoutingStrategies.Tunnel);
            PointerEntered += delegate { Ui.Fade(btnClose, 1, 140); };
            PointerExited += delegate { Ui.Fade(btnClose, 0, 200); };

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
            // Now rather than on first Show. Started at login this window may never be shown at all, and the
            // hotkeys do not need it: they are grabbed on the X server's root window.
            if (E.S.Hotkeys && screenshotPath == null) RegisterHotkeys();
        }

        /// <summary>The rows and words that follow what this system can do (Host), so the page is the Windows page
        /// minus what has no meaning here. Program.AllRows keeps every row, to compare the page with the Windows one.</summary>
        void HostRows() {
            bool all = Program.AllRows;
            bool vendor = Host.HasVendorApp || all;
            F<Control>("SuppressRow").IsVisible = vendor;
            if (!vendor) F<Control>("HotkeysRow").Margin = new Thickness(0, 12, 0, 0);     // the gap the row above it carried
            F<Control>("SyncPowerRow").IsVisible = Host.HasPowerModeSync || all;
            F<Control>("BenchPillRow").IsVisible = Host.HasFrameCapture || all;
            if (!Host.IsWindows && !all) {
                F<TextBlock>("TxtSyncPower").Text = "Sync the system power profile";
                F<TextBlock>("TxtSyncPowerSub").Text = "Power saver · Balanced · Performance";
                F<TextBlock>("TxtAutostart").Text = "Start at login";
                F<TextBlock>("TxtAutostartSub").Text = "An autostart entry for this user";
            }
            // The one pixel border Windows draws around a borderless window, drawn here because nothing else will.
            if (screenshotPath == null) {
                var edge = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), IsHitTestVisible = false };
                Grid.SetColumnSpan(edge, 2);
                ((Grid)root).Children.Add(edge);
            }
        }

        // ---------- construction ----------
        static Control LoadXaml() {
            return (Control)AvaloniaXamlLoader.Load(new Uri("avares://ohman/Ui.axaml"));
        }
        T F<T>(string name) where T : class {
            var scope = NameScope.GetNameScope(root);
            var o = (scope != null ? scope.Find(name) : null) as T;
            if (o == null) throw new InvalidOperationException("XAML element missing: " + name);
            return o;
        }
        void FindAll() {
            rail = F<Control>("Rail");
            railCanvas = F<Canvas>("RailCanvas");
            railPill = F<Border>("RailPill");
            railPillT = (TranslateTransform)railPill.RenderTransform;
            logoHost = F<Control>("LogoHost");
            F<Border>("Mark").Background = accentSrc.MakeGradient();
            scroll = F<ScrollViewer>("Scroll");
            pageHost = F<Grid>("PageHost");
            pages[0] = F<Control>("PageHome");
            pages[1] = F<Control>("PageFans");
            pages[2] = F<Control>("PageKbd");
            pages[3] = F<Control>("PageSettings");
            txtHomeTitle = F<TextBlock>("TxtHomeTitle");
            txtHomeStatus = F<TextBlock>("TxtHomeStatus");
            demoBadge = F<Control>("DemoBadge");
            errBanner = F<Control>("ErrBanner");
            txtErr = F<TextBlock>("TxtErr");
            infoBanner = F<Control>("InfoBanner");
            txtInfo = F<TextBlock>("TxtInfo");
            infoClose = F<Border>("InfoClose");
            Ui.LeftUp(infoClose, delegate {
                E.S.InfoDismissed = true; try { E.S.Save(); } catch { }
                infoBanner.IsVisible = false;
                Remeasure(cur);   // the page just got shorter
            });
            bigCpu = F<Run>("BigCpu");
            bigGpu = F<Run>("BigGpu");
            subCpu = F<TextBlock>("SubCpu");
            subGpu = F<TextBlock>("SubGpu");
            bigFan1 = F<Run>("BigFan1");
            bigFan2 = F<Run>("BigFan2");
            powerRow = F<Control>("PowerRow");
            slPower = F<Slider>("SlPower");
            txtPower = F<TextBlock>("TxtPower");
            lightRow = F<Control>("LightRow");
            txtLightSub = F<TextBlock>("TxtLightSub");
            miniHost = F<Border>("MiniHost");
            dotHb = F<Ellipse>("DotHb");
            txtFoot = F<TextBlock>("TxtFoot");
            txtFootRight = F<TextBlock>("TxtFootRight");
            txtFansStatus = F<TextBlock>("TxtFansStatus");
            curveBlock = F<Control>("CurveBlock");
            txtCurveTitle = F<TextBlock>("TxtCurveTitle");
            curveWhichHost = F<Border>("CurveWhichHost");
            txtCurveHint = F<TextBlock>("TxtCurveHint");
            maxBlock = F<Control>("MaxBlock");
            maxFan1 = F<Run>("MaxFan1");
            maxFan1Sub = F<TextBlock>("MaxFan1Sub");
            maxFan2 = F<Run>("MaxFan2");
            maxFan2Sub = F<TextBlock>("MaxFan2Sub");
            maxTemp = F<Run>("MaxTemp");
            maxTempSub = F<TextBlock>("MaxTempSub");
            maxMins = F<Run>("MaxMins");
            maxMinsUnit = F<Run>("MaxMinsUnit");
            maxMinsSub = F<TextBlock>("MaxMinsSub");
            manualBlock = F<Control>("ManualBlock");
            manPct1 = F<Run>("ManPct1");
            manSub1 = F<TextBlock>("ManSub1");
            manPct2 = F<Run>("ManPct2");
            manSub2 = F<TextBlock>("ManSub2");
            optsAuto = F<Control>("OptsAuto");
            tgEcoCool2 = F<ToggleButton>("TgEcoCool2");
            optsCurve = F<Control>("OptsCurve");
            tgLink = F<ToggleButton>("TgLink");
            slFloor = F<Slider>("SlFloor");
            txtFloor = F<TextBlock>("TxtFloor");
            slRamp = F<Slider>("SlRamp");
            txtRamp = F<TextBlock>("TxtRamp");
            optsMax = F<Control>("OptsMax");
            tgMaxCool = F<ToggleButton>("TgMaxCool");
            optsManual = F<Control>("OptsManual");
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
            selectRow = F<Control>("SelectRow");
            txtKeySel = F<TextBlock>("TxtKeySel");
            kbdHost = F<Border>("KbdHost");
            colorEditor = F<Control>("ColorEditor");
            effectEditor = F<Control>("EffectEditor");
            kbdInfo = F<Control>("KbdInfo");
            hexChip = F<Border>("HexChip");
            txtHex = F<TextBox>("TxtHex");
            slLevel = F<Slider>("SlLevel");
            txtLevel = F<TextBlock>("TxtLevel");
            levelInline = F<Control>("LevelInline");
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
            keyCmdRow = F<Control>("KeyCmdRow");
            txtKeyCmd = F<TextBox>("TxtKeyCmd");
            gfxRow = F<Control>("GfxRow");
            txtGfxSub = F<TextBlock>("TxtGfxSub");
            hzRow = F<Control>("HzRow");
            lowHzRow = F<Control>("LowHzRow");
            gpuRow = F<Control>("GpuRow");
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
            tgBenchPill = F<ToggleButton>("TgBenchPill");
            tgLightPage = F<ToggleButton>("TgLightPage");
            lightPageRow = F<Border>("LightPageRow");
            txtLightPageSub = F<TextBlock>("TxtLightPageSub");
            tgAutostart = F<ToggleButton>("TgAutostart");
            tgGuard = F<ToggleButton>("TgGuard");
            tgUpdateAuto = F<ToggleButton>("TgUpdateAuto");
            txtGuardSub = F<TextBlock>("TxtGuardSub");
            txtGuardRelease = F<TextBlock>("TxtGuardRelease");
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
            driverBanner = F<Control>("DriverBanner");
            txtDriverNudge = F<TextBlock>("TxtDriverNudge");
            txtDriverNudgeSub = F<TextBlock>("TxtDriverNudgeSub");
            btnDriverNudge = F<TextBlock>("BtnDriverNudge");
            driverClose = F<Control>("DriverClose");
            driverRow = F<Control>("DriverRow");
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
        void SliderBounds() {
            slFan1.Minimum = slFan2.Minimum = 0;
            slFan1.Maximum = slFan2.Maximum = E.P.Curve.Ceiling;
            slFloor.Minimum = E.P.Curve.Floor;
            slFloor.Maximum = E.P.Curve.Floor + (E.P.Curve.Ceiling - E.P.Curve.Floor) * 2 / 3;
            slPower.Maximum = E.MaxOffset;
            // Avalonia: a slider whose value lies outside its range (0 under a Minimum of 1) is only pulled into it
            // when the control initialises, which is after everything has been wired, and that would arrive as
            // the user dragging it to its minimum. Put every value inside its range now.
            foreach (Slider sl in new[] { slPower, slFan1, slFan2, slFloor, slRamp, slLevel, slLevel2, slSpeed })
                sl.Value = Math.Max(sl.Minimum, Math.Min(sl.Maximum, sl.Value));
        }
        /// <summary>The window manager runs the move. BeginMoveDrag returns at once, so there is no modal loop for a
        /// morph to fight with; the flag only covers the call itself.</summary>
        bool dragging;
        void Drag(PointerPressedEventArgs e) {
            dragging = true;
            try { BeginMoveDrag(e); } catch { } finally { dragging = false; }
        }
        void BuildRail() {
            var host = F<StackPanel>("NavHost");
            var bottom = F<StackPanel>("NavBottom");
            navBottom = bottom;
            nav[0] = new NavBtn(0, "Home", new[] { ICO_HOME_RING }, new[] { ICO_HOME_DOT });
            nav[1] = new NavBtn(1, "Fans", new[] { ICO_FANS }, new string[0]);
            nav[2] = new NavBtn(2, "Keyboard", new[] { ICO_KBD }, new string[0]);
            nav[3] = new NavBtn(3, "Settings", new string[0], new[] { GearPath(12, 12, 10, 7.6, 8, 3.4) });
            nav[4] = new NavBtn(4, "Benchmark", new[] { ICO_BENCH }, new[] { ICO_BENCH_DOT });
            for (int i = 0; i < nav.Length; i++) { nav[i].HorizontalAlignment = HorizontalAlignment.Center; nav[i].Clicked += delegate(int idx) { Navigate((Page)idx, true); }; }
            host.Children.Add(nav[0]);
            host.Children.Add(nav[1]);
            host.Children.Add(nav[2]);
            host.Children.Add(nav[4]);
            // Above Settings, and only while there is something to install. It is a signpost rather than a page:
            // it goes where the update lives instead of doing anything itself, so nothing is one stray click from
            // restarting the app. It stays out of nav[] deliberately -- the rail pill tracks the current page, and
            // this button never is one.
            navUpdate = new NavBtn(-1, "Update available", new[] { ICO_UPDATE }, new string[0]);
            navUpdate.HorizontalAlignment = HorizontalAlignment.Center;
            navUpdate.Accent = true;
            navUpdate.IsVisible = false;
            navUpdate.Clicked += delegate { ShowUpdateRow(); };
            bottom.Children.Add(navUpdate);
            bottom.Children.Add(nav[3]);
            if (E.Light == null) nav[2].IsVisible = false;
            logoHost.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (!LeftDown(e)) return; e.Handled = true; Navigate(Page.Home, true); };
            rail.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (LeftDown(e)) Drag(e); };
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
            if (b.Bounds.Width <= 0 || railCanvas.Bounds.Width <= 0) return;
            Point p = b.TranslatePoint(new Point(0, 0), railCanvas).GetValueOrDefault();
            if (morphing && b.Parent == navBottom) p = p.WithY(p.Y + (mt[3] - mt[1]) - rail.Bounds.Height);   // bottom group: where it will be when the morph lands
            if (railPill.Opacity == 0 || !animate) { Ui.StopAnimation(railPillT, TranslateTransform.XProperty); Ui.StopAnimation(railPillT, TranslateTransform.YProperty); railPillT.X = p.X; railPillT.Y = p.Y; railPill.Opacity = 1; return; }
            Ui.Glide(railPillT, TranslateTransform.XProperty, p.X, 360, true);
            Ui.Glide(railPillT, TranslateTransform.YProperty, p.Y, 360, true);
        }

        void BuildHome() {
            modeSeg = new Seg(Engine.ModeNames, ModeSubs, null, Seg.Kind.Page);
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
            subCpu.Text = null;
            subCpu.Inlines.Clear();
            subCpu.Inlines.Add(runCpuHead);
            subCpu.Inlines.Add(runCpuWatts);
            subCpu.Inlines.Add(runCpuTail);
            if (E.Light == null) lightRow.IsVisible = false;
            else {
                var layout = BuildLayout();
                kbdMini = new KeyboardView { Interactive = false, Gap = 1.5, RowPitch = 9.5 }; kbdMini.SetLayout(layout); miniHost.Child = kbdMini;
                Ui.LeftUp(lightRow, delegate { Navigate(Page.Keyboard, true); });
                lightRow.PointerEntered += delegate { miniNeedsFrame = true; };
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
            // Pending from the moment the debounce fires until the worker has saved it: a refresh in between used to
            // repaint the curve from settings that did not have the drag in them yet, and points snapped back (#65).
            curveDebounce.Tick += delegate {
                curveDebounce.Stop();
                int[] lv = (int[])curveView.Levels.Clone(); bool gpu = curveEditGpu;
                Interlocked.Increment(ref curvePending);
                Bg(delegate {
                    try { E.SetCurve(lv, gpu); }
                    finally { Interlocked.Decrement(ref curvePending); Dispatcher.BeginInvoke((Action)delegate { RefreshFans(false); }); }
                });
            };
            // Which curve was edited is taken when the drag ends, not when the debounce fires: switching CPU/GPU
            // inside those 250 ms saved the CPU drawing into the GPU curve.
            curveView.Changed += delegate { curveEditGpu = curveGpu; curveDebounce.Stop(); curveDebounce.Start(); };
            Ui.LeftUp(btnFanAction, delegate {
                if (E.S.Fan == FanMode.Auto) { Bg(delegate { E.SeedCurveFromVendor(); }); E.S.Fan = FanMode.Custom; RefreshFans(true); return; }
                int[] v = E.VendorCurveAt(curveGpu);
                bool gpu = curveGpu;
                curveView.Levels = (int[])v.Clone();
                curveView.Repaint();
                Bg(delegate { E.SetCurve(v, gpu); });
            });
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
            EventHandler<RangeBaseValueChangedEventArgs> fanSlid = delegate(object o, RangeBaseValueChangedEventArgs ev) {
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
                keyCmdRow.IsVisible = a == KeyAction.Run;
            };
            gpuSeg = new Seg(new[] { "Base", "Boost", "Max", "Auto" },
                new[] { "The GPU's standard power limit",
                        "Lets the GPU borrow power from the CPU when it needs it",
                        "A raised power limit as well as the borrowing",
                        "Base in Eco, Boost in Balanced, Max in Performance" }, null, Seg.Kind.Row);
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
                if (i == 3) Bg(delegate { E.SetGpu(E.S.Gpu, true, false); });
                else { GpuLevel g = (GpuLevel)i; Bg(delegate { E.SetGpu(g, false, false); }); }
            };
            if (!E.P.HasGpuPower) gpuRow.IsVisible = false;
            txtKeyCmd.LostFocus += delegate { E.SetKeyCommand(txtKeyCmd.Text); };
            txtKeyCmd.TextChanged += delegate { txtKeyCmdHint.IsVisible = txtKeyCmd.Text.Length == 0; };
            txtKeyCmd.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Enter) { E.SetKeyCommand(txtKeyCmd.Text); ShowToast("OMEN key runs: " + (E.S.KeyCommand.Length > 0 ? E.S.KeyCommand : "(nothing)"), false); } };
            BuildRefreshRates();
            BuildGraphicsModes();
            learnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            learnTimer.Tick += delegate { learnTimer.Stop(); E.Learning = false; UpdateKeyStatus(); };
            Ui.LeftUp(btnLearn, delegate { E.Learning = true; keyDot.Fill = Ui.Brush(Ui.Warn); txtKeyInfo.Text = "press the OMEN key now… (10 s)"; learnTimer.Stop(); learnTimer.Start(); });

            OnSwitch(tgHotkeys, delegate(bool on) { StopListening(); Bg(delegate { E.SetHotkeys(on); }); if (on) RegisterHotkeys(); else UnregisterHotkeys(); });   // a capture in progress ends first, or its bindings would be registered under it
            btnHotkeys.Click += delegate { ToggleHotkeyPanel(); };
            btnHotkeys.Content = PencilGlyph(false);
            AddHandler(KeyUpEvent, delegate(object o, KeyEventArgs ke) { heldMods = Held(ke, false); ShowHeldModifiers(); }, RoutingStrategies.Tunnel);
            BuildGuardLine();
            // The sentence is the row. Its values are grey words until the pencil, then the accent and a list on
            // click; the tick greys them again. Same pencil and tick as the hotkeys row.
            txtGuardSub.IsVisible = false;
            guardLine.IsVisible = true;
            btnGuard.Content = PencilGlyph(false);
            Ui.Tip(btnGuard, "Change the rule");
            btnGuard.Click += delegate {
                guardOpen = !guardOpen;
                btnGuard.Content = PencilGlyph(guardOpen);
                Ui.Tip(btnGuard, guardOpen ? "Done" : "Change the rule");
                foreach (ValueLink v in new[] { chipCpu, chipChassis, chipFans, chipHold }) v.Editable = guardOpen;
            };
            guardDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            guardDebounce.Tick += delegate {
                guardDebounce.Stop();
                int cpu = cpuLo + chipCpu.Index, ch = chassisLo + chipChassis.Index, lvl = guardLevels[chipFans.Index], hold = holdChoices[chipHold.Index];
                Bg(delegate { E.SetGuardLimits(cpu, ch, lvl, hold); });
            };
            AddHandler(KeyDownEvent, OnHotkeyCapture, RoutingStrategies.Tunnel);
            Deactivated += delegate { StopListening(); };     // another window took the keyboard; the keys are not coming here
            OnSwitch(tgEcoBattery, delegate(bool on) { Bg(delegate { E.SetEcoOnBattery(on); }); });
            OnSwitch(tgSyncPower, delegate(bool on) { Bg(delegate { E.SetSyncWinPower(on); }); });
            OnSwitch(tgLowHzBattery, delegate(bool on) { Bg(delegate { E.SetLowHzOnBattery(on); }); });
            OnSwitch(tgBenchPill, delegate(bool on) {
                Bg(delegate { E.SetBenchPill(on); });
                if (!on && pill != null) pill.Off();   // BENCH
            });
            OnSwitch(tgTrayTemp, delegate(bool on) {
                Bg(delegate { E.SetTrayTemp(on); });
                if (!on) { try { tray.Icon = icons[E.ModeIndex]; } catch { } }
                PollRate();
            });
            // Only where there is a keyboard to hide, or one already hidden to bring back.
            if (E.Light == null && !E.S.HideLight) lightPageRow.IsVisible = false;
            OnSwitch(tgLightPage, delegate(bool on) {
                if (!on) {
                    nav[2].IsVisible = false;
                    lightRow.IsVisible = false;
                    if (trayLight != null) trayLight.Visible = false;
                    Bg(delegate { E.SetHideLight(true); });
                } else {
                    Bg(delegate { E.SetHideLight(false); });
                    if (E.Light == null) ShowToast("The keyboard page comes back when Ohman next starts", false);
                }
            });
            OnSwitch(tgSuppress, delegate(bool on) { Bg(delegate { E.SetOghSuppression(on); }); });
            OnSwitch(tgAutostart, delegate(bool on) { Slow(delegate { SetAutostart(on); }); });
            OnSwitch(tgGuard, delegate(bool on) {
                if (!on && !ConfirmGuardOff()) { Synced(delegate { tgGuard.IsChecked = true; }); return; }
                Bg(delegate { E.SetGuard(on); });
            });
            OnSwitch(tgUpdateAuto, delegate(bool on) { Bg(delegate { E.SetUpdateOnLaunch(on); }); });
            OnSwitch(tgDriver, delegate(bool on) { Bg(delegate { E.SetDriverUse(on); }); });
            // One link, whose meaning is the row's state: install, update, restart, troubleshoot, remove.
            Ui.LeftUp(btnDriver, delegate { DriverAction(); });
            Ui.LeftUp(btnDriverNudge, delegate { InstallDriver(); });
            Ui.LeftUp(driverClose, delegate { E.DismissDriverNudge(); driverBanner.IsVisible = false; Remeasure(cur); });

            // The right-hand link is whatever is left to say: the changelog once the title is doing the
            // installing, the release page while there is only news of a build, and the check itself otherwise.
            Ui.LeftUp(btnUpdate, delegate {
                if (E.Staged == null && E.Downloading) return;
                if (E.Staged != null || E.UpdateAvailable) { OpenReleases(); return; }
                txtUpdate.Text = "checking…"; Slow(delegate { E.CheckForUpdate(true); });
            });
            Ui.LeftUp(updateText, delegate { if (E.Staged != null) DoUpdate(); });
            Ui.LeftUp(btnDiag, delegate {
                if (txtDiag.IsVisible) { txtDiag.IsVisible = false; return; }
                txtDiag.Text = "running…";
                txtDiag.IsVisible = true;
                Slow(delegate { string d = E.Diagnostics(); Log.Write(d); Dispatcher.BeginInvoke((Action)delegate { txtDiag.Text = d.TrimEnd(); }); });
            });
            // One button instead of "download the zip, find tools\, run it as administrator". Ohman is already
            // elevated, so it can ask the firmware the same questions directly, and the answer goes straight to
            // the clipboard because the next thing anyone does with it is paste it into an issue.
            Ui.LeftUp(btnSupport, delegate {
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
                        try { Clipboard.SetTextAsync(r); copied = true; } catch { }      // the clipboard is shared; it can be busy
                        txtDiag.Text = r.TrimEnd();
                        txtDiag.IsVisible = true;
                        Remeasure(cur);
                        ShowToast(copied ? "Copied. Paste it into the issue, or drag support-info.txt in"
                                         : "Saved as support-info.txt, drag it into the issue", !copied);
                        // The folder first, browser second, so the issue form is the window left in front. The
                        // report is too big for a URL, so it travels as a paste or as the file in the folder shown.
                        if (path.Length > 0)
                            try { Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path)) { UseShellExecute = true }); }
                            catch (Exception ex) { Log.Write("could not reveal the report: " + ex.Message); }
                        try { Process.Start(new ProcessStartInfo(Support.IssueUrl(E, r)) { UseShellExecute = true }); }
                        catch (Exception ex) { Log.Write("could not open the issue form: " + ex.Message); }
                    });
                });
            });
            Ui.LeftUp(btnLog, delegate { try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open log: " + ex.Message, true); } });
            Ui.LeftUp(btnExit, delegate { ExitApp(); });
            // Somebody has to be able to leave. A tool that writes to firmware and switches off the vendor's own
            // software owes the owner a way back that does not depend on waiting for the author to ship a fix.
            Ui.LeftUp(btnReset, delegate {
                string ask = "Undo everything " + Program.DisplayName + " changed, then quit?\n\n"
                    + "It puts the refresh rate back, sets the mode to balanced, hands the fans back to the firmware, turns the "
                    + "backlight on, removes its autostart entry, and deletes its own settings and log.\n\n"
                    + "The graphics mode is left as you set it, because changing that needs a restart.\n\n"
                    + "Afterwards you can remove the " + Program.FileStem + " package and nothing of it is left behind.";
                if (MessageBox.Show(IsVisible ? (Window)this : null, ask, Program.DisplayName,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                // The driver is the one thing Ohman may have put on the machine outside its own folder. Only when
                // it was us, and only with a yes: FanControl or LibreHardwareMonitor may be using the same one.
                // Asked here, beside the other question, so that both answers are in before any work starts -
                // running somebody's uninstaller takes as long as it takes, and the thread that has to paint the
                // window is not the thread to wait for it on.
                bool alsoDriver = Host.CanInstallDriver && E.S.DriverInstalledByOhman && !E.Hw.IsDemo && E.DriverInstalled
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
                                + Program.DisplayName + " closes now.",
                            Program.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
                        if (folder.Length > 0)
                            try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
                        ExitApp();
                    });
                });
            });

            // the wheel moves a fixed, small distance and eases there; the default jumps three "lines" of a very tall panel
            scroll.AddHandler(PointerWheelChangedEvent, delegate(object o, PointerWheelEventArgs e) {
                e.Handled = true;
                scrollTo = Math.Max(0, Math.Min(ScrollableHeight, scrollTo - e.Delta.Y * 54));
                if (!scrolling) { scrolling = true; Ui.Rendering += ScrollTick; }
            }, RoutingStrategies.Tunnel);
            scroll.ScrollChanged += delegate { if (!scrolling) scrollTo = scroll.Offset.Y; };
        }
        double scrollTo;
        bool scrolling;
        void ScrollTick(object o, EventArgs e) {
            double at = scroll.Offset.Y, d = scrollTo - at;
            if (Math.Abs(d) < 0.6) { ScrollTo(scrollTo); scrolling = false; Ui.Rendering -= ScrollTick; return; }
            ScrollTo(at + d * 0.28);
        }

        /// <summary>Every sentence that quotes a guard temperature reads it from the profile, so a new model only
        /// has to change GuardLimits and the words follow.</summary>
        void GuardText() {
            var g = E.P.Guard;
            SyncGuardLine();
            txtMaxCoolSub.Text = "Below " + g.MaxFanCoolBelow + "° for " + (g.MaxFanCoolSeconds / 60) + " minutes";
            txtGuardNote.Text = Program.DisplayName + " forces " + (E.GuardLevel > 0 ? "at least " + E.Rpm(E.GuardLevel) : "max fan") + " above " + E.GuardCpuHot + "° CPU";
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
                if (appIcons[mode] == null) appIcons[mode] = MakeIcon(Ui.ModeColor(mode), 256);
                Icon = appIcons[mode];
            } catch { }
        }

        /// <summary>The mark as an icon: a rounded square rotated 45°, filled, exactly like the one on the rail.</summary>
        public static WindowIcon MakeIcon(Color c, int size) {
            using (var bmp = DrawMark(c, size)) return ToIcon(bmp);
        }
        static WindowIcon ToIcon(Bitmap bmp) {
            var ms = new System.IO.MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        public static RenderTargetBitmap DrawMark(Color c, int size) { return DrawMark(c, size, null); }
        /// <summary>label != null: the number sits inside the diamond (the tray temperature). A diamond's corners reach
        /// the edge of the box at side = 0.707, so that is as large as the mark can be drawn.</summary>
        public static RenderTargetBitmap DrawMark(Color c, int size, string label) {
            var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var g = bmp.CreateDrawingContext()) {
                double mid = size / 2.0;
                double side = size * 0.74;
                double radius = side * (label == null ? 0.15 : 0.11);
                // Same light-to-dark ramp as the mark on the rail and on the website. The path is already rotated,
                // so the gradient is simply vertical in device space: top point light, bottom point dark. The end
                // points are pushed a little past the shape, as the Windows icon's are.
                double half = side * 0.71;
                var path = RoundSquare(mid, side, radius, mid);
                var box = path.Bounds;
                var b = new LinearGradientBrush {
                    StartPoint = new RelativePoint(0.5, (mid - half - 1 - box.Y) / box.Height, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, (mid + half + 1 - box.Y) / box.Height, RelativeUnit.Relative) };
                b.GradientStops.Add(new GradientStop(Shade(c, 0.24), 0));
                b.GradientStops.Add(new GradientStop(Shade(c, -0.28), 1));
                g.DrawGeometry(b, null, path);
                if (label == null) return bmp;
                // IBM Plex's figures are wider than Segoe UI's, which the Windows icon is drawn with: 0.92 brings
                // two of them to the width they have there.
                double em = size * (label.Length >= 3 ? 0.46 : 0.68) * 0.92;
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Ui.UiFont, FontStyle.Normal, FontWeight.Bold), em, Brushes.White);
                var glyphs = ft.BuildGeometry(new Point(0, 0));
                if (glyphs != null) {
                    var bounds = glyphs.Bounds;
                    // White, not the card colour. On paper a dark glyph has the better contrast ratio against all
                    // three mode colours, but the tray is sixteen pixels on a dark panel: the diamond reads as a
                    // dark object and a dark number inside it disappears. White is what is actually legible there.
                    using (g.PushTransform(Matrix.CreateTranslation(mid - bounds.X - bounds.Width / 2, mid - bounds.Y - bounds.Height / 2)))
                        g.DrawGeometry(Brushes.White, null, glyphs);
                }
            }
            return bmp;
        }
        /// <summary>Toward white for a positive amount, toward black for a negative one.</summary>
        static Color Shade(Color c, double t) {
            int to = t > 0 ? 255 : 0;
            double k = Math.Abs(t);
            return Color.FromArgb(c.A,
                (byte)Math.Round(c.R + (to - c.R) * k),
                (byte)Math.Round(c.G + (to - c.G) * k),
                (byte)Math.Round(c.B + (to - c.B) * k));
        }
        static Geometry RoundSquare(double center, double side, double radius, double rotateAbout) {
            double x = center - side / 2, y = center - side / 2;
            var p = new RectangleGeometry(new Rect(x, y, side, side), radius, radius);
            p.Transform = new RotateTransform(45, rotateAbout, rotateAbout);
            return p;
        }

        // ---------- the tray menu: everything the panel can do, without opening it ----------
        readonly List<TrayItem> trayFan = new List<TrayItem>(), trayGfx = new List<TrayItem>();
        TrayItem trayLight;
        /// <summary>A menu item with what the WinForms one had and the native one lacks: a Tag, Checked, a list of children.</summary>
        sealed class TrayItem : NativeMenuItem {
            public object Tag;
            public TrayItem(string text) : base(text) { }
            public bool Checked { get { return IsChecked; } set { ToggleType = NativeMenuItemToggleType.CheckBox; IsChecked = value; } }
            public bool Visible { get { return IsVisible; } set { IsVisible = value; } }
            public IList<NativeMenuItemBase> DropDownItems { get { if (Menu == null) Menu = new NativeMenu(); return Menu.Items; } }
        }
        TrayItem Item(string text, Action click) {
            var it = new TrayItem(text);
            if (click != null) it.Click += delegate { click(); };
            return it;
        }
        void BuildTray() {
            var menu = new NativeMenu();
            var head = new TrayItem(Program.DisplayName + "   " + Program.Version) { IsEnabled = false };
            menu.Items.Add(head);
            menu.Items.Add(new NativeMenuItemSeparator());
            for (int i = 0; i < 3; i++) {
                int idx = i;
                var it = Item(Engine.ModeNames[i], delegate { ApplyModeAsync(idx); });
                trayModes[i] = it;
                menu.Items.Add(it);
            }
            menu.Items.Add(new NativeMenuItemSeparator());

            // Fan
            var fanMenu = new TrayItem("Fan");
            for (int i = 0; i < Choice.Fan.Length; i++) {
                FanMode m = Choice.FanModes[i];
                var it = Item(Choice.Fan[i], delegate { Bg(delegate { E.SetFan(m, E.S.Fan1, E.S.Fan2, false); }); });
                trayFan.Add(it);
                fanMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(fanMenu);

            // Screen: the refresh rate, and the graphics mode for anyone who switches between hybrid and the MUX
            var gfxMenu = new TrayItem("Screen");
            foreach (int hz in Display.Choices()) {
                int h = hz; var it = Item(hz + " Hz refresh rate", delegate { Bg(delegate { E.SetRefreshRate(h); }); }); it.Tag = hz;
                trayHz.Add(it);
                gfxMenu.DropDownItems.Add(it);
            }
            var gfxModes = new List<int>();
            foreach (int mode in Engine.GpuModeOrder) if (E.GpuModeOffered(mode)) gfxModes.Add(mode);
            if (gfxModes.Count >= 2) {
                if (gfxMenu.DropDownItems.Count > 0) gfxMenu.DropDownItems.Add(new NativeMenuItemSeparator());
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

            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Show " + Program.DisplayName, delegate { ShowPanel(); }));
            menu.Items.Add(Item("Exit", delegate { ExitApp(); }));
            menu.Opening += delegate { RefreshTray(); };
            if (screenshotPath != null) return;              // rendered off-screen: there is no panel to put an icon in
            // A desktop without a StatusNotifier host (GNOME without the AppIndicator extension) shows no icon and
            // raises nothing here; starting Ohman again shows the window, which is the way back in there.
            try {
                tray = new TrayIcon { Icon = icons[1], ToolTipText = Program.DisplayName, Menu = menu, IsVisible = true };
                tray.Clicked += delegate { TogglePanel(); };
                // BENCH: the benchmark's balloon tips (and a click on one opening the Bench page) have no counterpart
                // here; BenchSay shows the same words in the on-screen flash.
            } catch (Exception ex) { Log.Write("tray: " + ex.Message); tray = null; }
        }
        /// <summary>Tick what is currently true. Called when the menu opens and after every state change.</summary>
        void RefreshTray() {
            if (tray == null) return;
            var S = E.S;
            for (int i = 0; i < 3; i++) trayModes[i].Checked = i == E.ModeIndex;
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
            var answer = MessageBox.Show(IsVisible ? (Window)this : null, "Switch graphics to " + Engine.GpuModeNames[m] + "?\n\nThe change is written to the firmware now and takes effect after a restart.\n\nRestart now?",
                Program.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { Refresh(); return; }
            if (!E.SetGpuMode(m)) { Refresh(); return; }
            if (answer == MessageBoxResult.Yes) Reboot();
            else ShowToast(Engine.GpuModeNames[m] + " after the next restart", false);
            Refresh();
        }

        // ---------- benchmark ----------
        // BENCH: everything from here to TrayTip, as in src\Ui.cs.
        Func<double, double> BenchFit;                 // benchPage.Fit, what the page wants to be for a given work-area height
        void BuildBench() {
            bench = new Bench(E, E.Hw.IsDemo);
            benchPage = new BenchPage(E, bench);
            // The page goes into its slot, and the slot stays shown: the page itself is what Navigate shows and hides,
            // as it is the element in pageHost on Windows.
            var host = F<Grid>("BenchHost");
            host.Children.Add(benchPage);
            host.IsVisible = true;
            pages[(int)Page.Bench] = benchPage;
            BenchFit = benchPage.Fit;
            benchPage.StartClicked += StartBench;
            benchPage.Toast += delegate(string m, bool err) { ShowToast(m, err); };
            benchPage.Resized += delegate { Remeasure(Page.Bench); };
            benchPage.StopKey = BenchKey;
            bench.Changed += delegate { Dispatcher.BeginInvoke((Action)OnBenchChanged); };
            bench.Finished += delegate(BenchRun r) { Dispatcher.BeginInvoke((Action)delegate { OnBenchFinished(r); }); };
            bench.EndedEarly += delegate(string why) { Dispatcher.BeginInvoke((Action)delegate { OnBenchEnded(why); }); };
        }
        /// <summary>Start a run: the page's Start, Run again, or the hotkey. Ohman steps out of the way and says once
        /// what to do; the pill takes over when the game is found.</summary>
        void StartBench() {
            if (!bench.ToolReady || !bench.Start()) {
                if (!IsVisible) ShowPanel();
                Navigate(Page.Bench, true);
                benchPage.Render();
                return;
            }
            PollRate();
            if (pill != null) pill.Off();                 // the last run's result pill, if it is still up
            benchPage.RunStarted();
            Hotkey hk = BenchKey();
            if (osd == null) NewOsd();
            osd.Flash("Benchmark", "Switch to your game" + (!hk.IsEmpty ? " · " + hk + " stops it" : ""), Ui.Accent.Color, ICO_BENCH);
            if (IsVisible) HideToTray();
        }
        /// <summary>The key that stops a run from inside the game, or none: shortcuts off, unset, or refused by the
        /// system because another program has it. A hint must not name a key that does nothing.</summary>
        Hotkey BenchKey() {
            int i = (int)HotkeyAction.Benchmark;
            Hotkey hk = E.GetHotkey(HotkeyAction.Benchmark);
            return E.S.Hotkeys && !hk.IsEmpty && !hotkeyBusy[i] ? hk : Hotkey.None;
        }
        void ToggleBench() { if (bench.Running) bench.Stop(); else StartBench(); }
        /// <summary>--bench-selftest, simulated build only: the whole state machine once, on simulated frames, and out.</summary>
        void BenchSelfTest() {
            int w = bench.WarmChoice, m = bench.MeasureChoice;
            bench.SetChoices(2, 0, false);   // this run only: the owner's choices stay as they are
            bench.Finished += delegate(BenchRun r) { Log.Write("selftest: finished " + r.Id + " frames=" + r.Frames + " avg=" + r.AvgFps.ToString("0.0") + " low1=" + r.Low1.ToString("0.0") + " stutter=" + r.StutterPct.ToString("0.00") + " samples=" + r.Samples.Count); Dispatcher.BeginInvoke((Action)delegate { bench.SetChoices(w, m, false); ExitApp(); }); };
            bench.EndedEarly += delegate(string why) { Log.Write("selftest: ended early: " + why); Dispatcher.BeginInvoke((Action)delegate { bench.SetChoices(w, m, false); ExitApp(); }); };
            Log.Write("selftest: starting");
            StartBench();
        }

        BenchPhase lastPhase = BenchPhase.Idle;
        void OnBenchChanged() {
            var v = bench.Snapshot();
            if (v.Phase != lastPhase) { lastPhase = v.Phase; PollRate(); }
            if (cur == Page.Bench && IsVisible) benchPage.Render();
            UpdatePill(v);
            TrayTip();
        }
        void NewPill() {
            plainAdvance = true;                           // its own window, formatted like the flash: the font's own advances
            try { pill = new BenchPill(); } finally { plainAdvance = false; }
            Program.HeadlessScale(pill);
        }
        /// <summary>The pill follows the run while the game is in front, and is gone two seconds before measuring
        /// starts so the game is back on its own when the frames count.</summary>
        void UpdatePill(BenchView v) {
            if (!E.S.BenchPill) { if (pill != null) pill.Off(); return; }   // switched off in Settings
            if (pill == null) NewPill();
            Color acc = Ui.Accent.Color;
            if (v.Phase == BenchPhase.Warming && !v.Paused) {
                double cap = v.WarmChoice == 1 ? 120 : 300;
                pill.Display(v.Window, acc, "warm", Ui.Col("#A8A3A0"), Ink.Clock(v.WarmSec), v.WarmChoice == 1 ? "of 2:00" : "settling", v.WarmSec / cap, Ink.Ref, 0);
            } else if (v.Phase == BenchPhase.Countdown && !v.Paused && v.CountLeft > 2) {
                pill.Display(v.Window, acc, "measuring in", Ui.Col("#A8A3A0"), Math.Ceiling(v.CountLeft).ToString("0", CultureInfo.InvariantCulture), "", -1, acc, 0);
            } else if (!pill.Timed) pill.Off();
        }
        /// <summary>What Windows puts in a tray balloon while the window is hidden. The tray icon here has none, so
        /// the on-screen flash says it.</summary>
        void BenchSay(string title, string line, Color c) {
            if (osd == null) NewOsd();
            osd.Flash(title, line, c, ICO_BENCH);
        }
        void OnBenchFinished(BenchRun r) {
            benchPage.Finished(r);
            if (pill == null) NewPill();
            var v = bench.Snapshot();
            if (E.S.BenchPill) pill.Display(v.Window, Ui.ModeColor(r.ModeIndex), Ink.F0(r.AvgFps), Ui.Col("#EDEAE8"), "", "avg · " + (double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1)) + " low", -1, Ink.Ref, 8);
            string line = r.Game + " · " + Ink.F0(r.AvgFps) + " fps avg" + (double.IsNaN(r.Low1) ? "" : " · " + Ink.F0(r.Low1) + " 1% low");
            if (r.SaveError.Length > 0) {
                // the result is on screen, but nowhere else: say so now rather than let it vanish at the next start
                string why = "Benchmark done, but it could not be saved: " + r.SaveError;
                if (IsVisible) ShowToast(why, true);
                else BenchSay("Benchmark not saved", r.SaveError, Ink.Warn);
            }
            else if (IsVisible) { if (cur != Page.Bench) ShowToast("Benchmark done: " + line, false); }
            else BenchSay("Benchmark done", line, Ui.ModeColor(r.ModeIndex));
            PollRate();
            TrayTip();
        }
        void OnBenchEnded(string why) {
            benchPage.EndedEarly(why);
            if (pill == null) NewPill();
            var v = bench.Snapshot();
            bool asked = why == "stopped";
            if (asked) { pill.Off(); if (IsVisible) ShowToast("Benchmark stopped", false); }
            else {
                if (E.S.BenchPill) pill.Display(v.Window, Ink.Warn, "ended", Ink.WarnText, "", why, -1, Ink.Warn, 6);
                if (IsVisible) ShowToast("Benchmark ended: " + why, true);
                else BenchSay("Benchmark ended", why, Ink.Warn);
            }
            PollRate();
            TrayTip();
        }
        void TrayTip() {
            if (tray == null) return;
            string tip;
            var v = bench == null ? null : bench.Snapshot();   // BENCH: while a run is on, the tip is the run's phase
            if (v != null && v.Phase != BenchPhase.Idle && v.Phase != BenchPhase.Fetching)
                tip = Program.DisplayName + " · benchmark · " + (v.Phase == BenchPhase.Waiting ? "waiting for a game" : v.Paused ? "paused" : v.Phase == BenchPhase.Warming ? "warming up " + Ink.Clock(v.WarmSec)
                    : v.Phase == BenchPhase.Countdown ? "starting" : "measuring " + Ink.Clock(v.MeasuredSec) + " of " + Ink.Clock(v.MeasureTarget));
            else
            tip = Program.DisplayName + " · " + E.ModeName + " · " + E.CurrentTdp + " W budget" + (E.GuardActive ? " · thermal guard" : E.S.Fan == FanMode.Max ? " · max fan" : "");
            tip = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (tray.ToolTipText != tip) tray.ToolTipText = tip;
        }

        void Wire() {
            foreach (string n in new[] { "HeadHome", "HeadFans", "HeadKbd", "HeadSettings", "DragStrip" })
                F<Control>(n).PointerPressed += delegate(object o, PointerPressedEventArgs me) { if (LeftDown(me)) Drag(me); };
            foreach (var h in new[] { benchPage.Head, benchPage.RunsHead, benchPage.ShareHead })   // BENCH
                h.PointerPressed += delegate(object o, PointerPressedEventArgs me) { if (LeftDown(me)) Drag(me); };
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
            if (p != Page.Settings) StopListening();
            if (p == cur && pageShown) return;
            bool wasKbd = cur == Page.Keyboard;
            var old = pageShown ? pages[(int)cur] : null;
            cur = p;
            pageShown = true;
            var page = pages[(int)p];
            bool live = animate && screenshotPath == null && IsVisible && old != null && old != page;
            for (int i = 0; i < pages.Length; i++) if (pages[i] != page && !(live && pages[i] == old)) ShowPage(pages[i], false);
            pageHost.Width = p == Page.Keyboard ? KbdPageW : PageW;
            for (int i = 0; i < nav.Length; i++) nav[i].SetSelected(i == (int)p);
            if (p == Page.Keyboard) ApplyEditorState();
            else if (wasKbd) RefreshLighting();
            if (p == Page.Settings) {
                scroll.ScrollToHome();
                scrollTo = 0;
                UpdateKeyStatus();
                UpdateUpdateRow();
                txtMachine.Text = E.Hw.IsDemo && screenshotPath == null ? "simulated hardware" : (E.BiosOk ? "board " + E.Board : "firmware unavailable");
            }
            if (p == Page.Fans) RefreshFans(false);
            // the page has to be Visible before Morph measures it: a Collapsed element reports no desired size at all
            if (!live) ShowPage(page, true);
            else { Ui.StopAnimation(page, Visual.OpacityProperty); page.Opacity = 0; page.IsHitTestVisible = true; page.RenderTransform = null; page.IsVisible = true; }
            double was = Bounds.Width * Bounds.Height;
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
        static void ShowPage(Control page, bool on) {
            Ui.StopAnimation(page, Visual.OpacityProperty);
            page.Opacity = 1;
            page.IsHitTestVisible = true;
            page.RenderTransform = null;
            page.IsVisible = on;
        }

        /// <summary>The Dynamic-Island order: the old page drops out fast; the new one arrives once the frame has mostly got there
        /// when growing (it would otherwise be revealed by the moving edge), almost at once when shrinking (it fits inside the old frame).</summary>
        // the two fades overlap rather than meeting: with the frame now moving from the first frame, the incoming page
        // can start while the outgoing one is still going, and the panel is never briefly empty
        const int FadeOutMs = 110, FadeInMs = 170, GrowDelayMs = 70, ShrinkDelayMs = 25;
        void CrossFade(Control old, Control page, bool grow) {
            old.IsHitTestVisible = false;                                                 // it overlaps the new page in the host grid
            Ui.Animate(old, Visual.OpacityProperty, old.Opacity, 0, FadeOutMs, new CubicEaseOut(), 0, delegate { if (pages[(int)cur] != old) ShowPage(old, false); });
            var tt = new TranslateTransform(0, 6);
            page.RenderTransform = tt;
            int delay = grow ? GrowDelayMs : ShrinkDelayMs;
            Ui.Animate(page, Visual.OpacityProperty, 0, 1, FadeInMs, new CubicEaseOut(), delay, delegate { if (pages[(int)cur] == page) page.Opacity = 1; });
            Ui.Animate(tt, TranslateTransform.YProperty, 6, 0, 360, new SpringEase(), delay, null);
        }

        // ---------- the morph: four edge springs, one move-and-resize per rendered frame, no animation on the window itself ----------
        // Why it is built this way (see anim-report.md):
        //  * Ui.Rendering: one step per frame, and the time each step is given is the frame's.
        //  * Edges, not left+width: each edge is rounded to device pixels on its own, so a right-anchored move never wobbles the right edge.
        //  * Windows needs WM_ENTERSIZEMOVE around the morph and SetWindowPos inside it to keep old content off the
        //    new frame. Here the window is moved and sized through Avalonia; how cleanly a frame lands is up to the
        //    window manager's compositor.
        // springs in Apple's (duration, bounce) form: w0 = 2π/duration, zeta = 1 - bounce. Grow: 1.5 px of overshoot on 240 px, 90 % there
        // at 208 ms. Shrink: critically damped, 90 % at 200 ms; a window that shrinks and comes back reads as a glitch.
        const double GrowDur = 0.40, GrowBounce = 0.15, ShrinkDur = 0.32, ShrinkBounce = 0.0, MorphMaxSec = 1.2;
        bool morphing, morphGrow, morphTicking;
        TimeSpan morphLast;
        long morphWall;
        double morphAge;
        readonly double[] mx = new double[4], mv = new double[4], mt = new double[4];   // edges l, t, r, b in DIPs: position, velocity, target
        readonly int[] mpx = new int[4];                                                // last device rect sent, so an unchanged frame costs nothing

        void Morph(bool animate) {
            if (pageHost == null || dragging) return;
            double w = RailW + pageHost.Width, natural = NaturalHeight();
            if (natural < 100) return;
            var wa = WorkArea;
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
                if (Math.Abs(Bounds.Width - w) < 0.5 && Math.Abs(Bounds.Height - h) < 0.5 && Math.Abs(Left - l) < 0.5 && Math.Abs(Top - t) < 0.5) return;
                mx[0] = Left;
                mx[1] = Top;
                mx[2] = Left + Bounds.Width;
                mx[3] = Top + Bounds.Height;
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
                Ui.Rendering += MorphTick;
            }
        }
        /// <summary>What the visible page wants to be, measured with no height limit (Settings is a fixed, scrolling page). Only the
        /// current page counts: the outgoing one is still in the host while it fades.</summary>
        double NaturalHeight() {
            if (cur == Page.Settings) return SettingsH;
            if (cur == Page.Bench && BenchFit != null) return BenchFit(WorkArea.Height - 24);   // BENCH: benchPage.Fit
            var page = pages[(int)cur];
            if (page == null || pageHost.Width <= 0) return 0;
            page.Measure(new Size(pageHost.Width, double.PositiveInfinity));
            return page.DesiredSize.Height;
        }
        void StopMorph() {
            if (!morphing) return;
            morphing = false;
            Ui.Rendering -= MorphTick;
        }
        void MorphTick(object o, EventArgs e) {
            var re = e as RenderingEventArgs;
            if (re == null || morphTicking || !morphing) return;
            if (re.RenderingTime == morphLast) return;
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
            double k = Scale;
            int x0 = (int)Math.Round(l * k), y0 = (int)Math.Round(t * k), x1 = (int)Math.Round(r * k), y1 = (int)Math.Round(b * k);
            if (x0 == mpx[0] && y0 == mpx[1] && x1 == mpx[2] && y1 == mpx[3]) return;
            bool moved = x0 != mpx[0] || y0 != mpx[1];
            mpx[0] = x0;
            mpx[1] = y0;
            mpx[2] = x1;
            mpx[3] = y1;
            if (moved) Position = new PixelPoint(x0, y0);
            Width = (x1 - x0) / k;
            Height = (y1 - y0) / k;
        }

        void SetAccent(int modeIndex, bool animate) {
            Color c = Ui.ModeColor(modeIndex);
            if (!animate) { Ui.StopAnimation(accentSrc, ColorSource.ColorProperty); accentSrc.Color = c; }
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
            // The last choice hands the keyboard to Windows Dynamic Lighting; it is only listed where that exists.
            string[] lightNames = Choice.Light;
            if (!Host.HasDynamicLighting) { lightNames = new string[Choice.Light.Length - 1]; Array.Copy(Choice.Light, lightNames, lightNames.Length); }
            kbdModes = new LinkSeg(lightNames, 22, 13.5, 4, new[] { null, null, "The zone colours pulse", "Every zone through the spectrum together", "The spectrum travels across the zones", "Hand the keyboard to Windows Dynamic Lighting" });
            F<Border>("KbdModeHost").Child = kbdModes;
            if (E.Light.Inert)
                for (int i = 1; i <= 4; i++) kbdModes.SetEnabled(i, false, "This keyboard offers no lighting interface Ohman can drive; see the note below");
            if (Host.HasDynamicLighting && E.Light is McuKeyboardLighting) kbdModes.SetEnabled(5, false, "Windows Dynamic Lighting cannot drive this keyboard");
            kbdModes.Picked += delegate(int i) {
                int m = Choice.LightMode(i), fx = Choice.LightEffect(i);
                E.S.Light = m;
                E.S.LightEffect = fx;
                if (E.Light is McuKeyboardLighting) E.S.McuLightChosen = true;     // show the editor now, not after the write
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
            EventHandler<RangeBaseValueChangedEventArgs> level = delegate(object o, RangeBaseValueChangedEventArgs ev) {
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
            Ui.LeftUp(btnWinLighting, delegate { if (!Host.HasDynamicLighting) return; try { Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true }); } catch (Exception ex) { ShowToast("Cannot open Windows settings: " + ex.Message, true); } });
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
            bool add = (heldMods & KeyModifiers.Control) != 0;
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
            if (lightRow.IsPointerOver || miniNeedsFrame) { miniNeedsFrame = false; kbdMini.SetColors(f, true); }
        }
        /// <summary>Show only what the current mode can use: colour for the per-zone modes, speed and brightness for
        /// the effects that paint their own colours.</summary>
        /// <summary>The drawn keyboard for this machine. Four zones come from the layout itself; on a per-key board
        /// the keyboard says which of its lamps sits under each key, so the device decides and we do not carry a table.
        /// A Primax keyboard is drawn from its own table instead, every key it has.</summary>
        List<KeyDef> BuildLayout() {
            var mcu = E.Light as McuKeyboardLighting;
            if (mcu != null) return mcu.Layout();
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
            // A Primax keyboard nobody has asked us to light yet shows its own lighting, and no mode is ours.
            bool untouched = E.LightUntouched;
            if (untouched) m = -1;
            bool lit = m == 1, pick = lit && (fx == 0 || fx == 1), effect = lit && fx != 0;
            int lightIdx = untouched ? -1 : Choice.OfLight(m, fx);
            if (lightIdx == 5 && !Host.HasDynamicLighting) lightIdx = -1;   // a settings file carried over from Windows
            kbdModes.Select(lightIdx, IsVisible && cur == Page.Keyboard);
            selectRow.IsVisible = pick;
            colorEditor.IsVisible = pick;
            // Breathe is the one mode that is both a colour and an effect, so it gets both blocks. Its brightness
            // then comes from the effect row, and the inline one would be a second copy of the same slider.
            effectEditor.IsVisible = effect;
            effectEditor.Margin = new Thickness(0, pick ? 18 : 0, 0, 0);
            levelInline.IsVisible = pick && !effect;
            kbdInfo.IsVisible = !lit;
            btnWinLighting.IsVisible = m == 2 && Host.HasDynamicLighting;
            // Reaching here means both ways in failed: HP's firmware answers and lights nothing, and this keyboard
            // offers no HID lighting interface either. Say which, so it does not read as "per-key is unsupported".
            txtKbdInfo.Text = untouched
                ? "The keyboard is showing its own lighting; Ohman has not written to it. Pick a mode to light it from here."
                : inert && m != 2
                ? "Ohman cannot light this keyboard. HP's firmware interface answers for per-key boards but does nothing, and this keyboard offers no lighting interface of its own." + (WinLighting.KeyboardFound ? " Windows Dynamic Lighting can still light it." : "") + " Run " + Program.FileStem + " --lamps and open an issue with what it prints."
                : m == 0 ? "The backlight is off. Pick a mode to turn it back on, or press the keyboard backlight key."
                : (WinLighting.Present || E.Hw.IsDemo ? "Windows Dynamic Lighting has the keyboard. Its colours and effects come from Windows settings."
                                                      : "No Dynamic Lighting device for this keyboard was found; Windows cannot drive it.");
            kbdBig.Selectable = pick;
            kbdBig.Off = m <= 0;
            kbdBig.WindowsOwned = m == 2;
            kbdBig.Smooth = effect;
            kbdMini.Off = m <= 0;
            kbdMini.WindowsOwned = m == 2;
            kbdMini.Smooth = effect;
            kbdBig.Level = kbdMini.Level = S.LightLevel / 100.0;
            if (!pick) { kbdBig.Selected.Clear(); kbdBig.Hover.Clear(); }
            string fxName = new[] { "static", "breathe", "cycle", "wave" }[Math.Max(0, Math.Min(3, fx))];
            // Cycle and wave paint colours of their own, so the picker has nothing to set; Ohman draws them itself on a
            // four-zone keyboard, so "firmware effect" was wrong there (#87 read it as a firmware limit).
            txtKbdStatus.Text = untouched ? "per-key · keyboard's own lighting" : inert && m != 2 ? "per-key · no interface we can drive" : m == 2 ? "windows lighting" : m == 0 ? "backlight off" : fx >= 2 ? E.Light.Describe + " · " + fxName + " · own colours" : E.Light.Describe + " · " + fxName;
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
            txtLightSub.Text = E.LightUntouched ? "Keyboard's own lighting" : S.Light == 2 ? "Windows Dynamic Lighting" : S.Light == 0 ? "Off" : fxName + " · " + E.Light.Describe;
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
                curveBlock.IsVisible = f == FanMode.Auto || f == FanMode.Custom;
                maxBlock.IsVisible = f == FanMode.Max;
                manualBlock.IsVisible = f == FanMode.Manual;
                optsAuto.IsVisible = f == FanMode.Auto;
                optsCurve.IsVisible = f == FanMode.Custom;
                optsMax.IsVisible = f == FanMode.Max;
                optsManual.IsVisible = f == FanMode.Manual;
                tgEcoCool2.IsChecked = S.EcoCool;
                tgMaxCool.IsChecked = S.MaxBackWhenCool;
                tgManualLink.IsChecked = S.ManualLinked;
                tgLink.IsChecked = linked;
                for (int i = 0; i < stopAfterSeg.Count; i++) if ((int)stopAfterSeg.Tags[i] == S.MaxStopAfterMin) stopAfterSeg.Select(i, animate && IsVisible);
                if (linked || f != FanMode.Custom) curveGpu = false;
                curveWhichHost.IsVisible = f == FanMode.Custom && !linked;
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
                    if (!curveDebounce.IsEnabled && !curveView.Dragging && Volatile.Read(ref curvePending) == 0) curveView.Levels = (int[])(curveGpu ? S.Cur.GpuCurveLevels : S.Cur.CurveLevels).Clone();
                    txtCurveTitle.Text = !linked ? "Fan curve" : "CPU curve";      // unlinked, the CPU / GPU switch beside it names the fan
                    // With the CPU / GPU switch beside the title the full hint runs into it (#89), so it keeps the half
                    // that is not obvious.
                    txtCurveHint.Text = linked ? "drag a point · shift-drag moves all" : "shift-drag moves all";
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
            btnFanAction.IsVisible = link;
            txtFanRight.IsVisible = !link;
            btnFanAction.Text = S.Fan == FanMode.Auto ? "Edit as curve" : "Reset curve";
            if (S.Fan == FanMode.Max) {
                txtFanApplied.Text = "Loud" + (ShowChassis(lastBiosTemp) ? " · ambient " + lastBiosTemp + "°" : "");
                txtFanRight.Text = "Ctrl+Alt+M toggles";
            } else {
                txtFanApplied.Text = E.GuardActive ? "Thermal guard: " + (E.GuardLevel > 0 ? "at least " + E.Rpm(E.GuardLevel) : "max fan") + " until cool" : "Applied to " + E.ModeName;
                txtFanRight.Text = "CPU " + (double.IsNaN(E.CpuTemp) ? "--" : E.CpuTemp.ToString("0") + "°") + " · GPU " + (E.GpuAsleep ? "asleep" : double.IsNaN(E.GpuTemp) ? "--" : E.GpuTemp.ToString("0") + "°");
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
            hzRow.IsVisible = true;
            lowHzRow.IsVisible = true;
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
            var names = new List<string>();
            var tags = new List<object>();
            foreach (int mode in Engine.GpuModeOrder) if (E.GpuModeOffered(mode)) { names.Add(Engine.GpuModeNames[mode]); tags.Add(mode); }
            if (names.Count < 2) return;
            gfxSeg = new Seg(names.ToArray(), null, tags.ToArray(), Seg.Kind.Row);
            var host = F<Border>("GfxSegHost");
            host.Child = gfxSeg;
            // Four choices (a firmware offering Hybrid and Optimus both) do not fit beside the label: it was squeezed
            // to "Graphi". They go on their own line under it, left-aligned with the text.
            if (names.Count >= 4) {
                var g = host.Parent as Grid;
                if (g != null && g.RowDefinitions.Count == 0) {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(host, 1); Grid.SetColumn(host, 0); Grid.SetColumnSpan(host, 2);
                    host.HorizontalAlignment = HorizontalAlignment.Left;
                    host.Margin = new Thickness(0, 10, 0, 0);
                }
            }
            gfxRow.IsVisible = true;
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
        int trayTempShown = int.MinValue;
        void UpdateTrayTemp(double cpu) {
            if (tray == null) return;
            if (!E.S.TrayTemp || double.IsNaN(cpu)) { if (trayTempShown != int.MinValue) { trayTempShown = int.MinValue; try { tray.Icon = icons[E.ModeIndex]; } catch { } } return; }
            int t = (int)Math.Round(cpu);
            int key = t * 4 + E.ModeIndex;
            if (key == trayTempShown) return;
            trayTempShown = key;
            try { tray.Icon = TempIcon(t, Ui.ModeColor(E.ModeIndex)); }
            catch (Exception ex) { Log.Write("tray temp icon: " + ex.Message); }
        }
        /// <summary>The CPU temperature inside the mark: the diamond in the mode colour, the number on it.</summary>
        static WindowIcon TempIcon(int temp, Color c) {
            using (var bmp = DrawMark(c, 32, temp.ToString(CultureInfo.InvariantCulture))) return ToIcon(bmp);
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
            bool was = syncing;
            syncing = true;
            try { a(); } finally { syncing = was; }
        }
        /// <summary>Wire a switch once: the flag check and the "only when the user did it" rule live here, not in
        /// eleven copies that each have to remember them.</summary>
        void OnSwitch(ToggleButton t, Action<bool> set) {
            EventHandler<RoutedEventArgs> h = delegate { if (syncing) return; set(t.IsChecked == true); };
            t.IsCheckedChanged += h;
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
                txtUpdateTitle.Text = null;
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
            txtUpdate.Foreground = (staged == null && newer) ? (IBrush)accent : Ui.Desc;
            btnUpdate.Text = staged != null ? "Changelog" : newer && E.Downloading ? "Downloading…" : newer ? "Download" : "Check now";
            if (navUpdate != null) {
                bool want = staged != null;
                if (navUpdate.IsVisible != want) {
                    navUpdate.IsVisible = want;
                    Ui.Tip(navUpdate, staged == null ? "Update available" : "Update to " + staged + " is ready");
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
            else if (S.DriverRestartPending && !E.DriverReady) { sub = "Installed · restart to finish"; link = "Restart now"; driverState = DriverState.RestartPending; }
            else if (!installed) {
                // Where Ohman cannot install it itself, the row says how instead of offering a link that could not work.
                sub = Host.CanInstallDriver ? "Adds " + gains : "Not loaded · " + Host.InstallHint + " says why";
                link = Host.CanInstallDriver ? "Install" : null;
                driverState = DriverState.NotInstalled;
            }
            else if (!S.DriverUse) { sub = "Off · adds " + gains; driverState = DriverState.Off; }
            else if (E.DriverOutdated) { name = DriverName(); sub = " · needs " + PawnIo.MinVersion + " or newer"; link = Host.CanInstallDriver ? "Update" : null; driverState = DriverState.Outdated; }
            else if (E.DriverReady) {
                // Said as what it is doing, not as what it opened. "CPU registers" answered a question nobody asked.
                // The simulated build takes this branch too, so the preview shows the row people will actually see.
                name = DriverName();
                string does = E.Route == Engine.FanRoute.Ec ? "fan levels" : null;
                if (E.Cpu != null) does = (does != null ? does + " and " : "") + (intel ? "CPU temperature and power limits" : "CPU temperature");
                sub = " · " + (demo ? "simulated" : does ?? "open");
                driverState = DriverState.Ready;
                if (!demo && Host.CanInstallDriver && S.DriverInstalledByOhman) link = "Remove";
            } else { title = "Hardware driver not detected"; sub = E.DriverWhy; link = "Troubleshoot"; driverState = DriverState.Broken; }
            string key = title + "|" + (name ?? "") + sub + "|" + (link ?? "") + "|" + showSwitch;
            if (key != driverRowFor) {
                driverRowFor = key;
                txtDriverTitle.Text = title;
                if (name != null) DriverSubLinked(name, sub); else { txtDriverSub.Inlines.Clear(); txtDriverSub.Text = sub; }
                btnDriver.Text = link ?? "";
                btnDriver.IsVisible = link != null;
                // Not whose it is and not what it is called: an unfamiliar name in front of a decision is what
                // made the old dialog read as a warning. The row credits PawnIO by link once it is installed.
                Ui.Tip(btnDriver,
                    !Host.CanInstallDriver ? null
                    : driverState == DriverState.NotInstalled || driverState == DriverState.Outdated
                        ? "Same driver FanControl, LibreHardwareMonitor and a dozen other hardware tools install. Removable any time."
                    : driverState == DriverState.Ready
                        ? "Safe, " + Program.DisplayName + " falls back to the temperature the system reports."
                    : null);
                tgDriver.IsVisible = showSwitch;
            }
            tgDriver.IsChecked = demo || S.DriverUse;
            // The Home nudge: the same install, from where the owner is.
            string nudge = E.DriverNudge;
            bool want = nudge != null;
            if (nudge != null) {
                txtDriverNudge.Text = nudge;
                bool restart = S.DriverRestartPending;
                txtDriverNudgeSub.Text = restart ? "The driver is installed and waits for a restart" : E.CpuBlind ? "The system gives one fixed number for this CPU, so the fans hold a safe speed. The driver reads the real one." : "Your firmware refuses fan levels through BIOS commands. " + Program.DisplayName + " can set them through a driver.";
                btnDriverNudge.Text = restart ? "Restart now" : "Install driver";
                btnDriverNudge.IsVisible = restart || Host.CanInstallDriver;
                if (!restart && !Host.CanInstallDriver) txtDriverNudgeSub.Text += " It is not loaded: " + Host.InstallHint + " says why.";
            }
            if (driverBanner.IsVisible != want) { driverBanner.IsVisible = want; if (cur == Page.Home) Remeasure(cur); }
        }
        /// <summary>"PawnIO 2.2.0", and never the fourth field: nobody needs the build number of somebody else's driver.</summary>
        string DriverName() {
            Version v = E.DriverVersion;
            return v == null ? Host.DriverName : Host.DriverName + " " + (v.Build >= 0 ? v.ToString(3) : v.ToString());
        }
        /// <summary>The sub-line with the driver's name as a link to its source. Somebody who has just been asked to
        /// trust a kernel driver should be one click from its code, not one search away from whatever a search finds.</summary>
        void DriverSubLinked(string name, string rest) {
            driverLink = new Run(name) { Foreground = accent };
            txtDriverSub.Text = null;
            txtDriverSub.Inlines.Clear();
            txtDriverSub.Inlines.Add(driverLink);
            txtDriverSub.Inlines.Add(new Run(rest));
            if (driverLinkWired) return;
            driverLinkWired = true;
            // A Run takes no pointer events here, so the line watches the pointer and asks its own layout which
            // character is under it: over the name it is a link, with the hand and the underline a Hyperlink has.
            txtDriverSub.Background = Brushes.Transparent;
            txtDriverSub.PointerMoved += delegate(object o, PointerEventArgs e) { DriverLinkHover(OverDriverLink(e)); };
            txtDriverSub.PointerExited += delegate { DriverLinkHover(false); };
            txtDriverSub.PointerReleased += delegate(object o, PointerReleasedEventArgs e) { if (e.InitialPressMouseButton == MouseButton.Left && OverDriverLink(e)) OpenUrl(PawnIo.SourceUrl); };
        }
        Run driverLink;
        bool driverLinkWired, driverLinkHot;
        bool OverDriverLink(PointerEventArgs e) {
            if (driverLink == null || txtDriverSub.Inlines.Count == 0 || txtDriverSub.Inlines[0] != driverLink) return false;
            try {
                var hit = txtDriverSub.TextLayout.HitTestPoint(e.GetPosition(txtDriverSub));
                return hit.IsInside && hit.TextPosition < (driverLink.Text ?? "").Length;
            } catch { return false; }
        }
        void DriverLinkHover(bool on) {
            if (on == driverLinkHot || driverLink == null) return;
            driverLinkHot = on;
            driverLink.TextDecorations = on ? TextDecorations.Underline : null;
            txtDriverSub.Cursor = on ? Cursors.Hand : null;
            Ui.Tip(txtDriverSub, on ? PawnIo.SourceUrl : null);
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
                    try { Clipboard.SetTextAsync(d); copied = true; } catch { }
                    txtDiag.Text = d.TrimEnd();
                    txtDiag.IsVisible = true;
                    Remeasure(cur);
                    ShowToast(copied ? "Copied - paste it into a GitHub issue or Discord" : "See below", false);
                });
            });
        }
        void RestartWindows(string why) {
            if (MessageBox.Show(IsVisible ? (Window)this : null, "Restart now to finish installing " + why + "?", Program.DisplayName,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Reboot();
        }
        /// <summary>Restart the machine: logind's, which an ordinary user at the seat is allowed to ask for.</summary>
        void Reboot() {
            try { Process.Start(new ProcessStartInfo("systemctl", "reboot") { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { ShowToast("Restart failed: " + ex.Message, true); }
        }

        /// <summary>The rail button and the Settings link both end here. The row is near the bottom of a page that
        /// scrolls, so arriving at Settings without this puts the thing that was clicked for off-screen.</summary>
        void ShowUpdateRow() { ShowRow(updateRow); }
        void ShowDriverRow() { ShowRow(driverRow); }
        void ShowRow(Control row) {
            Navigate(Page.Settings, true);
            // After the page has laid out, or the row has no position yet. BringIntoView would do the smallest
            // scroll that makes it visible, which leaves it jammed against the bottom edge; this puts it a little
            // way up the page and eases there the way every other scroll in the app does.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate {
                try {
                    var content = scroll.Content as Control;
                    if (content == null) return;
                    double y = row.TranslatePoint(new Point(0, 0), content).GetValueOrDefault().Y;
                    scrollTo = Math.Max(0, Math.Min(ScrollableHeight, y - 96));
                    if (screenshotPath != null) { ScrollTo(scrollTo); return; }   // no frames to ease over before the capture
                    if (!scrolling) { scrolling = true; Ui.Rendering += ScrollTick; }
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
                if (E.PowerRefused) powerRow.IsVisible = false;      // the firmware said no; see ApplyPowerCore
                gpuRow.IsVisible = E.P.HasGpuPower;   // a later answer to 0x21 brings it in
                GpuLevel g = E.EffectiveGpu;
                string gpuName = g == GpuLevel.Max ? "GPU max" : g == GpuLevel.Boost ? "GPU boost" : "GPU base";
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
                if (gpuSeg != null) { gpuSeg.Select(S.GpuAuto ? 3 : (int)g, IsVisible && cur == Page.Settings); txtGpuSub.Text = S.GpuAuto ? "Follows the mode" : g == GpuLevel.Max ? "Custom TGP + PPAB" : g == GpuLevel.Boost ? "PPAB" : "Base TGP"; }
                RefreshLighting();
                if (cur == Page.Fans) RefreshFans(true);
                keySeg.Select(Choice.Of(S.Key), IsVisible && cur == Page.Settings);
                keyCmdRow.IsVisible = S.Key == KeyAction.Run;
                if (!txtKeyCmd.IsFocused) txtKeyCmd.Text = S.KeyCommand;
                tgLowHzBattery.IsChecked = S.LowHzOnBattery;
                tgTrayTemp.IsChecked = S.TrayTemp;
                tgBenchPill.IsChecked = S.BenchPill;
                tgLightPage.IsChecked = !S.HideLight;
                txtLightPageSub.Text = !S.HideLight && E.Light == null ? "Back when Ohman next starts" : "Turn off for a white-only backlight. Fn still switches it";
                tgGuard.IsChecked = S.Guard;
                GuardText();          // the limits, the release points and whether the chassis counts all change after start
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
                // Where the session offers no global shortcuts the line says so instead of listing keys that do nothing.
                if (!GlobalKeys.Available && screenshotPath == null) hk = "Not in this session: " + GlobalKeys.Why + (omen != null ? " · Fn+F12 " + omen : "");
                if (hk != hotkeySubFor) { hotkeySubFor = hk; txtHotkeysSub.Text = hk; if (hotkeysOpen) BuildHotkeyPanel(); }
                tgEcoBattery.IsChecked = S.EcoOnBattery;
                tgSyncPower.IsChecked = S.SyncWinPower;
                tgAutostart.IsChecked = autostart;
                demoBadge.IsVisible = E.Hw.IsDemo && screenshotPath == null;
                bool err = (!E.BiosOk || E.ReadOnly) && !E.Hw.IsDemo;
                errBanner.IsVisible = err;
                if (err) txtErr.Text = !E.BiosOk ? "BIOS interface unavailable: " + E.LastError
                    : E.Desktop ? "Desktops aren't supported (board " + E.Board + "). Read-only: nothing is written to the firmware, which stays in charge."
                    : "Unsupported laptop (board " + E.Board + "). Read-only: nothing is written to the firmware. Run " + Program.FileStem + " --support and open a GitHub issue to add it.";
                bool firstHere = E.Generic && !E.Hw.IsDemo && !S.InfoDismissed && !Platforms.Reported(E.Board);
                infoBanner.IsVisible = firstHere;
                if (firstHere) txtInfo.Text = "You're the first to try this on your laptop. If it works, tell us on Discord or Reddit and we'll mark it verified.";
                RefreshTray();
                TrayTip();
                UpdateFooter();
            } finally { syncing = wasSyncing; }
        }

        void UpdateFooter() {
            txtFoot.Foreground = Ui.Foot;
            if (E.Hw.IsDemo && screenshotPath == null) { dotHb.Fill = Ui.Brush(Ui.Warn); txtFoot.Text = "Demo · simulated hardware"; return; }
            if (!E.BiosOk) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Text = "Firmware unavailable"; return; }
            if (E.GuardActive) { dotHb.Fill = Ui.Brush(Ui.Danger); txtFoot.Foreground = Ui.Brush(Ui.Danger); txtFoot.Text = "Thermal guard · ambient " + E.GuardChassis + "°"; return; }
            double age = (DateTime.Now - E.LastHeartbeat).TotalSeconds;
            bool fresh = E.LastHeartbeat != DateTime.MinValue && age < E.S.HeartbeatSec * 2.5;
            dotHb.Fill = Ui.Brush(fresh ? Ui.Ok : Ui.Warn);
            // A firmware without the fan table reports no count; saying "-1 fans" is worse than saying nothing.
            txtFoot.Text = ShortModel() + (E.FanCount > 0 ? " · " + E.FanCount + " fans" : "");
            Ui.Tip(txtFoot, "Ohman is driving this laptop's firmware" + (fresh ? ", last refreshed " + E.LastHeartbeat.ToString("HH:mm:ss") : "; the refresh is overdue"));
        }

        bool sensorsSeen;
        void OnSensors(SensorSnapshot s) {
            E.NoteCpuSource(s);
            E.CpuTemp = s.CpuTemp;
            E.CpuTempNow = s.CpuTempNow;
            if (!double.IsNaN(s.CpuTemp)) { int d = (int)Math.Round(s.CpuTemp); if (d != cpuTipFor) { cpuTipFor = d; Ui.Tip(chipCpu, "CPU is " + d + "\u00b0 right now"); } }
            // a sleeping GPU makes no heat; its last reading is from before it slept and must not drive the fans (#95)
            E.GpuTemp = s.GpuAsleep ? double.NaN : s.GpuTemp;
            E.GpuAsleep = s.GpuAsleep;
            onBattery = s.OnBattery;
            UpdateTrayTemp(s.CpuTemp);
            if (!double.IsNaN(s.CpuTemp)) { tempTrail.Add(s.CpuTemp); if (tempTrail.Count > 8) tempTrail.RemoveAt(0); sensorsSeen = true; }
            NotePower(s);
            bench.Feed(s);              // BENCH
            benchPage.OnSensors(s);
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
            if (limits != cpuLimitsTip) { cpuLimitsTip = limits; Ui.Tip(subCpu, limits); }
            runCpuTail.Text = s.Throttle.Length > 0 ? " · " + s.Throttle : "";
            // Amber while the CPU says it is being held back, and the caption says why: that is the one word
            // the ACPI zone could never supply, and the reason the driver exists on boards with working fans.
            subCpu.Foreground = s.Throttle.Length > 0 ? Ui.Brush(Ui.Warn) : subCpuBrush;
            if (s.CpuFromDriver != cpuTipFromDriver) {
                cpuTipFromDriver = s.CpuFromDriver;
                var big = bigCpu.Parent as Control;
                if (big != null) Ui.Tip(big, s.CpuFromDriver ? "Die temperature, read from the CPU itself" : null);
            }
            subGpu.Text = "GPU" + (double.IsNaN(s.GpuLoad) ? "" : " · " + s.GpuLoad.ToString("0") + "%") + (double.IsNaN(s.GpuWatts) ? "" : " · " + s.GpuWatts.ToString("0") + " W");
            txtFootRight.Text = s.BatteryPercent >= 0 && s.BatteryPercent <= 100 ? (s.OnBattery ? "Battery " : "AC · ") + s.BatteryPercent + "%" : "";
        }
        IBrush TempBrush(double t) { return double.IsNaN(t) ? Ui.TextB : t >= E.GuardCpuHot ? Ui.Brush(Ui.Danger) : t >= E.P.Guard.WarnAt ? Ui.Brush(Ui.Warn) : Ui.TextB; }
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
                        benchPage.OnFans(f);   // BENCH
                        if (t >= 0) { lastChassis = t; if (t != chassisTipFor) { chassisTipFor = t; Ui.Tip(chipChassis, "Chassis is " + t + "\u00b0 right now"); } }
                        bigFan1.Text = Level(f[0]);
                        bigFan2.Text = Level(f[1]);
                        UpdateFanStatus();
                        if (cur == Page.Fans) UpdateMaxBlock();
                    }
                    if (t >= 0 && t != lastBiosTemp) {
                        lastBiosTemp = t;
                        GpuLevel g = E.EffectiveGpu;
                        txtHomeStatus.Text = (E.P.HasGpuPower ? (g == GpuLevel.Max ? "GPU max" : g == GpuLevel.Boost ? "GPU boost" : "GPU base") : E.P.HasPowerGain ? E.CurrentTdp + " W" : "")
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
        /// <summary>The mode flash's second line. Fans are remembered per mode, so a mode whose own fan setting is not
        /// Auto says so: a Max picked once in Performance came back with every switch to Performance, and its owner
        /// read that as the mode itself sending the fans to max (#98).</summary>
        string ModeDetail(int idx) {
            FanMode f = E.S.Modes[Math.Max(0, Math.Min(2, idx))].Fan;
            return ModeSubs[idx] + (f == FanMode.Max ? " · max fan" : f == FanMode.Manual ? " · manual fans" : "");
        }
        void CycleWithFlash() {
            int next = (E.ModeIndex + 1) % 3;
            Flash(Engine.ModeNames[next] + " mode", ModeDetail(next), next);
            ApplyModeAsync(next);
        }
        void ToggleMaxWithFlash() {
            bool on = E.S.Fan != FanMode.Max;
            Flash(on ? "Max fan" : "Fans auto", on ? "Both fans to full speed" : "Back to the firmware curve", -1);
            Bg(delegate { E.ToggleMaxFan(); });
        }

        // CORE: Windows tells the window about suspend/resume and a change of power source (SystemEvents.PowerModeChanged)
        // and about the display going off (WM_POWERBROADCAST); the window passes them to E.OnResume, E.OnPowerSource and
        // E.OnDisplay. The core has no source of those events on Linux (that is logind over D-Bus), so the two that
        // can be seen from here are worked out from what already arrives: the sensor snapshot says which supply is
        // in use, and a suspend shows as the wall clock running ahead of the monotonic one. E.OnDisplay is not called.
        bool powerSeen;
        long powerMono;
        DateTime powerWall;
        void NotePower(SensorSnapshot s) {
            long mono = Stopwatch.GetTimestamp();
            DateTime wall = DateTime.UtcNow;
            if (powerSeen) {
                double slept = (wall - powerWall).TotalSeconds - (mono - powerMono) / (double)Stopwatch.Frequency;
                if (slept > 5) {
                    Log.Write("resume: the machine slept about " + slept.ToString("0", CultureInfo.InvariantCulture) + " s");
                    WinLighting.Forget();
                    E.OnResume();
                }
                if (s.OnBattery != onBattery) {
                    bool bat = s.OnBattery;
                    onBattery = bat;
                    PollRate();
                    Bg(delegate { E.OnPowerSource(bat); });
                }
            }
            powerSeen = true;
            powerMono = mono;
            powerWall = wall;
            onBattery = s.OnBattery;
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
            // From warm-up on: waiting for a game (up to ten minutes in a launcher) reads nothing, so it costs nothing
            bool benchOn = bench != null && bench.Sampling;   // BENCH
            sensors.Bench = benchOn;
            sensors.SkipGpu = E.GpuMode == 3 || (onBattery && !IsVisible && !benchOn);
            int ms = benchOn ? 1000                                         // a benchmark keeps a sample a second
                   : IsVisible ? E.S.PollMs
                   : curveDrivesFans ? 5000
                   : E.S.TrayTemp ? (onBattery ? 10000 : 5000)
                   : (onBattery ? 30000 : 15000);
            sensors.SetInterval(ms);
            if (uiTimer == null) return;
            if (IsVisible) { if (!uiTimer.IsEnabled) uiTimer.Start(); } else uiTimer.Stop();
        }

        // ---------- window plumbing ----------
        void OnSourceInit(object o, EventArgs e) {
            var ph = TryGetPlatformHandle();
            hwnd = ph != null ? ph.Handle : IntPtr.Zero;
        }
        /// <summary>Every binding in the table, id = action + 1. One the X server refuses (another program holds it) is
        /// remembered so the panel can say so next to it rather than the shortcut silently doing nothing.</summary>
        void RegisterHotkeys() {
            if (hotkeysRegistered) return;
            if (!GlobalKeys.Available) {
                // Said once. The row in Settings carries the same sentence, so the switch does not look broken.
                if (!hotkeysNoted) { hotkeysNoted = true; Log.Write("hotkeys: not registered: " + GlobalKeys.Why); }
                return;
            }
            Hotkey[] b = E.GetHotkeys();
            for (int i = 0; i < b.Length; i++) {
                hotkeyBusy[i] = false;
                if (b[i].IsEmpty) continue;
                // Auto-repeat is dropped in GlobalKeys: holding the keys a moment too long sent the hotkey again, and
                // a toggle pressed twice is a toggle not pressed. Max fan went off and straight back on, and read as stuck on.
                if (!GlobalKeys.Register(i + 1, b[i].Mods, b[i].Vk)) { hotkeyBusy[i] = true; Log.Write("hotkey " + b[i] + " (" + HotkeyTable.Names[i] + ") not available: another program has it"); }
            }
            hotkeysRegistered = true;
        }
        void UnregisterHotkeys() {
            if (!hotkeysRegistered) return;
            for (int i = 1; i <= HotkeyTable.Count; i++) GlobalKeys.Unregister(i);
            hotkeysRegistered = false;
        }

        // ---------- customising them ----------
        /// <summary>A filled pencil to open and a tick to close, drawn rather than typed: the icon font's pencil is
        /// a hairline at this size and reads as a scratch on the row.</summary>
        static WPath PencilGlyph(bool open) {
            const string pencil = "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z";
            const string tick = "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z";
            return new WPath { Data = Geometry.Parse(open ? tick : pencil), Fill = Ui.Sub, Width = 14, Height = 14, Stretch = Stretch.Uniform };
        }
        void ToggleHotkeyPanel() {
            hotkeysOpen = !hotkeysOpen;
            if (!hotkeysOpen) StopListening();
            // A pencil to open, a tick to close: the same spot, one glyph, no words to wrap the sub-line around.
            btnHotkeys.Content = PencilGlyph(hotkeysOpen);
            Ui.Tip(btnHotkeys, hotkeysOpen ? "Done" : "Change the shortcuts: click one, then press the keys you want. Esc keeps the old one, Backspace removes it.");
            txtHotkeysSub.IsVisible = !hotkeysOpen;   // the panel is the sub-line, in full
            if (hotkeysOpen) BuildHotkeyPanel();
            hotkeyPanel.IsVisible = hotkeysOpen;
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
                var name = new WpfTextBlock { Text = HotkeyTable.Names[i], FontFamily = Ui.UiFont, FontSize = 13, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                Grid.SetRow(name, i);
                var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                Grid.SetRow(cell, i); Grid.SetColumn(cell, 1);
                if (listening == i) {
                    Border cap = KeyCap("Press keys…", accent);
                    listeningCap = (TextBlock)cap.Child;
                    Pulse(cap, true);
                    cell.Children.Add(cap);
                } else if (b[i].IsEmpty) {
                    cell.Children.Add(KeyCap("none", Ui.Desc));
                } else {
                    Border cap = KeyCap(b[i].ToString(), hotkeyBusy[i] ? Ui.Brush(Ui.Warn) : null);
                    // Amber is the whole signal; the words are on hover, where they cost no width.
                    if (hotkeyBusy[i]) { cap.BorderBrush = Ui.Brush(Ui.Warn); Ui.Tip(cap, "In use by another program, so it does nothing here. Click to pick a different one."); }
                    cell.Children.Add(cap);
                }
                Ui.LeftUp(name, delegate { StartListening(idx); });
                Ui.LeftUp(cell, delegate { StartListening(idx); });
                g.Children.Add(name);
                g.Children.Add(cell);
            }
            if (E.HotkeysCustomised) {
                var reset = new WpfTextBlock { Text = "Reset all", FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = accent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(reset, b.Length); Grid.SetColumn(reset, 1);
                Ui.LeftUp(reset, delegate { StopListening(); UnregisterHotkeys(); E.ResetHotkeys(); if (HotkeysOn) RegisterHotkeys(); BuildHotkeyPanel(); });
                g.Children.Add(reset);
            }
            hotkeyPanel.Children.Add(g);
        }
        /// <summary>The listening cap breathes between full and 45 % until it leaves the panel (a DoubleAnimation
        /// with AutoReverse and RepeatBehavior.Forever in WPF).</summary>
        void Pulse(Border cap, bool down) {
            Ui.Animate(cap, Visual.OpacityProperty, down ? 1 : 0.45, down ? 0.45 : 1, 600, null, 0, delegate { if (listening >= 0 && cap.IsAttachedToVisualTree()) Pulse(cap, !down); });
        }
        Border KeyCap(string text, IBrush fg) {
            return new Border {
                Background = Ui.Pill, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0),
                BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
                Child = new WpfTextBlock { Text = text, FontFamily = Ui.MonoFont, FontSize = 11, Foreground = fg ?? Ui.TextB }
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
            heldMods = Held(e, true);
            if (listening < 0) return;
            e.Handled = true;
            int idx = listening;
            var action = (HotkeyAction)idx;
            Key key = e.Key;
            if (key == Key.Escape) { StopListening(); return; }
            if (key == Key.Back) { E.SetHotkey(action, Hotkey.None); StopListening(); return; }
            Hotkey h;
            if (!FromKey(key, heldMods, out h)) { ShowHeldModifiers(); return; }   // a modifier on its own: show it, keep waiting
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
            KeyModifiers m = heldMods;
            if ((m & KeyModifiers.Control) != 0) mods |= Hotkey.Ctrl;
            if ((m & KeyModifiers.Alt) != 0) mods |= Hotkey.Alt;
            if ((m & KeyModifiers.Shift) != 0) mods |= Hotkey.Shift;
            if ((m & KeyModifiers.Meta) != 0) mods |= Hotkey.Win;
            if (mods == 0) { listeningCap.Text = "Press keys\u2026"; return; }
            string t = new Hotkey(mods, 'X').ToString();
            listeningCap.Text = t.Substring(0, t.Length - 1) + "\u2026";
        }

        // ---------- the thermal guard's rule, as a sentence ----------
        static string HoldText(int s) { return s % 60 == 0 && s >= 60 ? (s / 60) + " min" : s + " s"; }
        static TextBlock Word(string t) { return new WpfTextBlock { Text = t, FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Desc, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }; }
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
            // The sentence reads as "max for 60 s", and an owner set it expecting the fans back after that long (#115).
            // The time only starts once both readings are under the release points, which sit below the limits.
            // Each number named: an owner who had lowered the chassis limit could not tell that the 32° was the chassis point
            // their sensor barely reaches at idle (#127). A chassis limit not armed yet does not hold anything, so it is left out.
            txtGuardRelease.Text = "Lets go once the CPU is under " + E.GuardCpuSafe + "°" + (E.GuardChassisArmed ? " and the chassis under " + E.GuardChassisSafe + "°" : "");
            txtGuardRelease.IsVisible = guardLine.IsVisible;
        }
        /// <summary>A global hotkey was pressed (WM_HOTKEY in the WPF window's Hook). id is the action.</summary>
        void OnHotkey(int id) {
            if (id >= 0 && id <= 2) { Flash(Engine.ModeNames[id] + " mode", ModeDetail(id), id); ApplyModeAsync(id); }
            else if (id == (int)HotkeyAction.MaxFan) ToggleMaxWithFlash();
            else if (id == (int)HotkeyAction.Cycle) CycleWithFlash();
            else if (id == (int)HotkeyAction.Benchmark) ToggleBench();   // BENCH
        }

        /// <summary>From a key event: the modifiers held and the key under them, as a Hotkey (which stores a Windows
        /// virtual key on every system). False while only a modifier is down, which is the state between pressing
        /// Ctrl and pressing the letter, and for a key a Hotkey has no name for.</summary>
        static bool FromKey(Key key, KeyModifiers held, out Hotkey h) {
            h = Hotkey.None;
            uint vk = 0;
            if (key >= Key.A && key <= Key.Z) vk = (uint)('A' + (key - Key.A));
            else if (key >= Key.D0 && key <= Key.D9) vk = (uint)('0' + (key - Key.D0));
            else if (key >= Key.F1 && key <= Key.F24) vk = (uint)(0x70 + (key - Key.F1));
            else switch (key) {
                case Key.Space: vk = 0x20; break; case Key.Enter: vk = 0x0D; break; case Key.Tab: vk = 0x09; break;
                case Key.PageUp: vk = 0x21; break; case Key.PageDown: vk = 0x22; break; case Key.End: vk = 0x23; break; case Key.Home: vk = 0x24; break;
                case Key.Left: vk = 0x25; break; case Key.Up: vk = 0x26; break; case Key.Right: vk = 0x27; break; case Key.Down: vk = 0x28; break;
                case Key.Insert: vk = 0x2D; break; case Key.Delete: vk = 0x2E; break; case Key.PrintScreen: vk = 0x2C; break; case Key.Scroll: vk = 0x91; break; case Key.Pause: vk = 0x13; break;
            }
            if (vk == 0) return false;
            uint mods = 0;
            if ((held & KeyModifiers.Control) != 0) mods |= Hotkey.Ctrl;
            if ((held & KeyModifiers.Alt) != 0) mods |= Hotkey.Alt;
            if ((held & KeyModifiers.Shift) != 0) mods |= Hotkey.Shift;
            if ((held & KeyModifiers.Meta) != 0) mods |= Hotkey.Win;
            h = new Hotkey(mods, vk);
            return true;
        }

        /// <summary>Paragraphs. WPF, in the Display text mode the window uses, makes a line its ascent and its descent
        /// each rounded to a whole device pixel, and stacks wrapped lines at that pitch; Avalonia stacks them at the
        /// font's own fractional one, so the third line of a caption sat a pixel higher than on Windows. WpfTextBlock
        /// already sizes a block that way; this gives the wrapping ones the same pitch between their lines.</summary>
        void WpfLines() {
            double k = RenderScaling > 0 ? RenderScaling : 1;       // the window's: a block on a page not yet shown has no scale of its own
            foreach (var d in root.GetLogicalDescendants()) {
                var tb = d as TextBlock;
                if (tb == null || tb.TextWrapping != TextWrapping.Wrap || !double.IsNaN(tb.LineHeight)) continue;
                try {
                    var m = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch).GlyphTypeface.Metrics;
                    double asc = -m.Ascent * tb.FontSize / m.DesignEmHeight, desc = (m.Descent + m.LineGap) * tb.FontSize / m.DesignEmHeight;
                    tb.LineHeight = (Math.Round(asc * k, MidpointRounding.AwayFromZero) + Math.Round(desc * k, MidpointRounding.AwayFromZero)) / k;
                } catch { }
            }
        }
        void OnLoaded(object o, RoutedEventArgs e) {
            WpfLines();
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
            else if (page == "bench") { if (Program.BenchPose != null) bench.Pose(Program.BenchPose); Navigate(Page.Bench, false); if (Program.BenchPose != null) benchPage.Pose(Program.BenchPose); }   // BENCH
            Morph(false);
            if (Program.BenchSelfTest && E.Hw.IsDemo) BenchSelfTest();   // BENCH
            if (Program.JustUpdated) ShowToast("Updated to " + Program.Version, false);
            if (Program.FlashTest) Flash("Performance mode", ModeDetail(2), 2);
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
                    Control at = Program.StartPage == "driver" ? driverRow : Program.StartPage == "update" ? updateRow : Program.StartPage == "hotkeys" ? (Control)hotkeyPanel : Program.StartPage == "guard" || Program.StartPage == "guardrow" ? (Control)txtGuardSub.Parent : null;
                    if (at != null && cur == Page.Settings) {
                        try {
                            var content = scroll.Content as Control;
                            if (content != null) { ScrollTo(Math.Max(0, Math.Min(ScrollableHeight, at.TranslatePoint(new Point(0, 0), content).GetValueOrDefault().Y - 96))); root.UpdateLayout(); }
                        } catch { }
                    }
                    Program.Settle();                 // off-screen nothing ticks the frame clock: let the switches and pills finish moving
                    root.UpdateLayout();
                    SnapshotElement(root, screenshotPath);
                    Log.Write("screenshot saved " + screenshotPath);
                    if (osd != null && osd.IsVisible) { var f = (Control)osd.Content; f.Opacity = 1; SnapshotElement(f, screenshotPath + ".osd.png"); }
                    try { using (var bmp = DrawMark(Ui.BalColor, 256)) bmp.Save(screenshotPath + ".icon.png"); } catch { }
                    try { using (var bmp = DrawMark(Ui.PerfColor, 32, "70")) bmp.Save(screenshotPath + ".tray.png"); } catch { }
                } catch (Exception ex) { Log.Write("screenshot failed: " + ex); Console.Error.WriteLine("screenshot failed: " + ex); }
                ExitApp();
            };
            t.Start();
        }
        /// <summary>The element as a PNG at Program.ShotScale device pixels per DIP (2, the scale the Windows
        /// reference screenshots were taken at).</summary>
        static void SnapshotElement(Control el, string path) {
            el.UpdateLayout();
            double k = Program.ShotScale;
            var px = new PixelSize((int)Math.Ceiling(el.Bounds.Width * k), (int)Math.Ceiling(el.Bounds.Height * k));
            using (var rtb = new RenderTargetBitmap(px, new Vector(96 * k, 96 * k))) {
                rtb.Render(el);
                rtb.Save(path);
            }
        }

        void PlaceWindow() {
            var S = E.S;
            if (screenshotPath != null) { WindowStartupLocation = WindowStartupLocation.Manual; ShowInTaskbar = false; return; }   // rendered off-screen, out of the way
            // every screen together, in DIPs: screens left of/above the primary have negative coordinates
            double vl = 0, vt = 0, vr = 0, vb = 0;
            bool any = false;
            try {
                foreach (var sc in Screens.All) {
                    double k = sc.Scaling > 0 ? sc.Scaling : 1;
                    var r = sc.Bounds;
                    if (!any) { vl = r.X / k; vt = r.Y / k; vr = r.Right / k; vb = r.Bottom / k; any = true; }
                    else { vl = Math.Min(vl, r.X / k); vt = Math.Min(vt, r.Y / k); vr = Math.Max(vr, r.Right / k); vb = Math.Max(vb, r.Bottom / k); }
                }
            } catch { }
            if (any && S.WinX != -1 && S.WinY != -1 && S.WinX >= vl && S.WinY >= vt && S.WinX < vr - 100 && S.WinY < vb - 100) {
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
            // hidden in the tray it is a background process again: hand back what the pages allocated while it was on screen
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)delegate {
                if (IsVisible) return;
                try { GC.Collect(2, GCCollectionMode.Optimized); } catch { }
            });
        }
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
            try { GlobalKeys.Close(); } catch { }
            try { if (tray != null) { tray.IsVisible = false; tray.Dispose(); } } catch { }
            try { if (osd != null) osd.Close(); } catch { }
            try { E.Park(quietExit); } catch { }   // before Dispose: the timers must still be alive to write
            // After the fans are handed back: stopping a capture can take a few seconds, and at logoff Windows may not wait.
            try { bench.Dispose(); } catch { }   // BENCH
            try { if (pill != null) pill.Close(); } catch { }
            // Reset promises nothing is left behind: the stored runs go too
            if (resetting) { try { if (System.IO.Directory.Exists(RunStore.Root)) System.IO.Directory.Delete(RunStore.Root, true); } catch (Exception ex) { Log.Write("reset: bench folder: " + ex.Message); } }
            // Sensors first. Its thread reads the CPU's registers through a handle the engine owns, so disposing
            // the engine first left a tick in flight holding a closed handle and wrote a driver failure into the
            // log of every clean exit.
            try { sensors.Dispose(); } catch { }
            try { E.Dispose(); } catch { }
            Log.Write("exit");
            // Last, because everything above still logs. The dialog said the log goes too, so it has to.
            if (resetting) { try { Log.Delete(); } catch { } }
            Program.Shutdown();
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

        // ---------- autostart (an XDG autostart entry for this user; the core's Autostart writes and removes it) ----------
        void QueryAutostartAsync() {
            Slow(delegate {
                bool on = Autostart.IsOn();
                if (!on && E.S.FirstRun && !E.Hw.IsDemo) {              // first launch: start at login like every vendor app does; the switch turns it off
                    SetAutostart(true);
                    on = autostart;
                    if (on) Dispatcher.BeginInvoke((Action)delegate { ShowToast("Starts at login from now on (Settings to change)", false); });
                }
                Dispatcher.BeginInvoke((Action)delegate { autostart = on; syncing = true; tgAutostart.IsChecked = on; syncing = false; });
            });
        }
        void SetAutostart(bool on) {
            if (E.Hw.IsDemo) { Dispatcher.BeginInvoke((Action)delegate { ShowToast("Autostart needs real hardware", true); }); return; }
            bool ok = false;
            try { ok = Autostart.Set(on); } catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
            Log.Write("autostart " + on + " ok=" + ok);
            autostart = on && ok;
            if (!E.S.FirstRun) Dispatcher.BeginInvoke((Action)delegate { ShowToast(ok ? (on ? "Starts at login" : "Autostart removed") : "Could not change the autostart entry", !ok); });
        }
    }
}
