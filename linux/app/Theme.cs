// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the look: the palette, the fonts that ship inside the binary, the animation curves, and the one
// accent colour every control shares. Nothing here knows about the window or the hardware.
// The Avalonia port of src\Theme.cs, kept member for member so a change there can be carried over from a diff.
//
// Differences from the WPF original
//  - Brushes. Ui.Brush(...) returns an ImmutableSolidColorBrush (Avalonia's frozen brush; it has .Color). The palette
//    fields (Card, Sunken, ...) are IBrush where WPF said Brush. Ui.Accent is a mutable SolidColorBrush as before.
//  - Fonts. UiFont and MonoFont point at the fonts embedded as AvaloniaResource (avares://ohman/fonts) from the
//    start; LoadFonts() only checks that they resolve, logs, and falls back to the system default if they do not.
//    Nothing is written to disk.
//  - Animation. Avalonia has no BeginAnimation. Ui.Glide, Ui.GlideColor and Ui.Fade keep their signatures (with
//    AvaloniaObject / AvaloniaProperty<double> / Visual for DependencyObject / DependencyProperty / UIElement) and
//    run on a small tween that writes the property once per frame. Two additions stand in for the rest of
//    BeginAnimation:
//        Ui.Animate(o, p, from, to, ms, easing, delayMs, done)   a DoubleAnimation with FillBehavior.Stop: runs
//                                                                from..to, leaves the property at `to`
//        Ui.StopAnimation(o, p)                                  BeginAnimation(p, null)
//    While a tween runs, GetValue returns the value in flight (as WPF did), so retargeting starts from there.
//    Unlike WPF there is no separate base value: reading the property mid-flight never gives the end value.
//  - Ui.Rendering is CompositionTarget.Rendering: a static event raised once per frame (60 Hz, the rate of
//    Avalonia's own render timer on X11) while anything is subscribed, with a RenderingEventArgs (declared here)
//    carrying RenderingTime. Ui.ManualFrames = true stops the timer from starting and Ui.Step(time) raises one
//    frame by hand: that pair is for headless tests.
//  - ColorSource is an AvaloniaObject. ColorSource.BeginAnimation(p, null) exists and stops a running glide,
//    nothing else. MakeBrush()/MakeGradient() return brushes bound to it, as before.
//  - SpringEase is an Avalonia Easing (Ease(t) = Ui.Spring(t)).
//  - Additions used by the ported controls and free for the window to use: Cursors (Hand, Arrow, Cross: WPF's
//    Cursors class), Ui.LeftUp(control, handler) (WPF's MouseLeftButtonUp: Avalonia captures the pointer to the
//    control that was pressed, WPF delivers the release to whatever is under it; LeftUp gives the capture back so
//    the release lands where WPF would deliver it), Ui.Tip(control, text) (control.ToolTip = text).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Ohman {

    static class Ui {
        public static ImmutableSolidColorBrush Brush(string hex) { return new ImmutableSolidColorBrush(Col(hex)); }
        public static ImmutableSolidColorBrush Brush(Color c) { return new ImmutableSolidColorBrush(c); }
        public static ImmutableSolidColorBrush Brush(Rgb c) { return Brush(Color.FromRgb(c.R, c.G, c.B)); }
        public static Color Col(string hex) { return Color.Parse(hex); }
        public static readonly Color EcoColor = Col("#2FBF8F"), BalColor = Col("#3F8CFF"), PerfColor = Col("#E2572C");
        public static readonly Color Warn = Col("#F3821D"), Danger = Col("#FF5C5C"), Ok = Col("#4AC06C");
        public static Color ModeColor(int i) { return i == 0 ? EcoColor : i == 2 ? PerfColor : BalColor; }
        /// <summary>c moved t of the way towards towards, in straight sRGB: enough for a short accent ramp.</summary>
        public static Color Mix(Color c, Color towards, double t) {
            return Color.FromRgb((byte)(c.R + (towards.R - c.R) * t), (byte)(c.G + (towards.G - c.G) * t), (byte)(c.B + (towards.B - c.B) * t));
        }
        public const string UiFontUri = "avares://ohman/fonts#IBM Plex Sans", MonoFontUri = "avares://ohman/fonts#IBM Plex Mono";
        public static FontFamily UiFont = new FontFamily(UiFontUri), MonoFont = new FontFamily(MonoFontUri);
        public static readonly IBrush Card = Brush("#161311"), Sunken = Brush("#0F0D0B"), Pill = Brush("#201C19"), Line = Brush("#2C2825"),
            TextB = Brush("#EDEAE8"), TextHi = Brush("#F4F1EF"), SegText = Brush("#A8A3A0"), Hex = Brush("#A29D99"),
            Sub = Brush("#96918D"), Desc = Brush("#908B87"), Status = Brush("#8A8581"), Axis = Brush("#847F7B"), Foot = Brush("#7E7976"), Section = Brush("#787370");
        /// <summary>The accent brush every accent-coloured thing shares; its colour follows the mode (see ColorSource).</summary>
        public static SolidColorBrush Accent = new SolidColorBrush(BalColor);

        /// <summary>IBM Plex ships inside the binary as Avalonia resources, so there is nothing to unpack: this only
        /// makes sure both families resolve, and says so in the log. The OFL text travels in the package beside the
        /// binary, as the licence asks of every copy of the font software.</summary>
        public static void LoadFonts() {
            try {
                UiFont = new FontFamily(UiFontUri);
                MonoFont = new FontFamily(MonoFontUri);
                foreach (var f in new[] { UiFont, MonoFont }) {
                    IGlyphTypeface g;
                    if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(f), out g) || g == null || g.FamilyName.IndexOf("Plex", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("font resource missing: " + f.Name);
                }
                Log.Write("fonts: embedded");
            } catch (Exception ex) {
                UiFont = FontFamily.Default;
                MonoFont = new FontFamily("monospace");
                Log.Write("fonts: " + ex.Message + " (using the system font)");
            }
        }

        // ---- the frame clock: CompositionTarget.Rendering ----

        static EventHandler rendering;
        static DispatcherTimer frames;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly RenderingEventArgs frameArgs = new RenderingEventArgs();
        /// <summary>Raised once per frame while anyone listens (WPF's CompositionTarget.Rendering). The timer stops
        /// itself on the first frame that finds nobody subscribed, so an idle window costs nothing.</summary>
        public static event EventHandler Rendering {
            add {
                rendering += value;
                if (frames == null) frames = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render, delegate { Step(clock.Elapsed); });
                if (!frames.IsEnabled && !ManualFrames) frames.Start();
            }
            remove { rendering -= value; }
        }
        /// <summary>Set before anything animates and the timer never starts: frames then come only from Step. For headless tests.</summary>
        public static bool ManualFrames;
        /// <summary>One frame at the given time. The timer calls this; a headless test calls it to move time by hand.</summary>
        public static void Step(TimeSpan now) {
            var h = rendering;
            if (h == null) { if (frames != null) frames.Stop(); return; }
            frameArgs.RenderingTime = now;
            h(null, frameArgs);
        }

        // ---- tweens: BeginAnimation ----

        sealed class Tween {
            public AvaloniaObject O;
            public AvaloniaProperty P;
            public Action<double, bool> Apply;   // the eased progress (a spring passes 1 on the way), and whether this is the last frame
            public Func<double, double> Ease;
            public double Ms, DelayMs;
            public TimeSpan Start;
            public bool Started;
            public Action Done;
        }
        static readonly List<Tween> tweens = new List<Tween>();
        static bool tweening;
        static void Run(Tween t) {
            StopAnimation(t.O, t.P);
            tweens.Add(t);
            if (!tweening) { tweening = true; Rendering += TweenFrame; }
        }
        static void TweenFrame(object o, EventArgs e) {
            TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
            foreach (var t in tweens.ToArray()) {
                if (!tweens.Contains(t)) continue;                      // stopped by something an earlier one set
                if (!t.Started) { t.Started = true; t.Start = now; }    // the clock starts on the first frame, as in WPF
                double k = ((now - t.Start).TotalMilliseconds - t.DelayMs) / t.Ms;
                if (k < 0) continue;
                if (k >= 1) {
                    tweens.Remove(t);
                    t.Apply(1, true);
                    if (t.Done != null) t.Done();
                } else t.Apply(t.Ease(k), false);
            }
            if (tweens.Count == 0 && tweening) { tweening = false; Rendering -= TweenFrame; }
        }
        static void Put(AvaloniaObject o, AvaloniaProperty<double> p, double v) { o.SetValue((AvaloniaProperty)p, v); }
        static double Linear(double t) { return t; }
        static double CubicOut(double t) { double u = 1 - t; return 1 - u * u * u; }       // CubicEase, EaseOut
        /// <summary>BeginAnimation(p, null): whatever is animating p on o lets go, and the value stays where it is.</summary>
        public static void StopAnimation(AvaloniaObject o, AvaloniaProperty p) {
            for (int i = tweens.Count - 1; i >= 0; i--) if (ReferenceEquals(tweens[i].O, o) && tweens[i].P == p) tweens.RemoveAt(i);
        }
        /// <summary>A DoubleAnimation(from, to) with FillBehavior.Stop: p runs from..to over ms after delayMs and is
        /// left at to. easing null is linear, WPF's default.</summary>
        public static void Animate(AvaloniaObject o, AvaloniaProperty<double> p, double from, double to, int ms, Easing easing, int delayMs, Action done) {
            Func<double, double> ease = Linear;
            if (easing != null) ease = easing.Ease;
            if (delayMs <= 0) Put(o, p, from);
            Run(new Tween { O = o, P = p, Ms = Math.Max(1, ms), DelayMs = delayMs, Ease = ease, Done = done,
                Apply = delegate(double k, bool last) { Put(o, p, last ? to : from + (to - from) * k); } });
        }
        public static void Animate(AvaloniaObject o, AvaloniaProperty<double> p, double from, double to, int ms, Easing easing) { Animate(o, p, from, to, ms, easing, 0, null); }

        /// <summary>Animate a double property: it runs from the value it has now to the new one and stays there.</summary>
        public static void Glide(AvaloniaObject o, AvaloniaProperty<double> p, double to, int ms, bool spring) {
            double from = (double)o.GetValue((AvaloniaProperty)p);
            if (double.IsNaN(from) || Math.Abs(from - to) < 0.5) { StopAnimation(o, p); Put(o, p, to); return; }
            Func<double, double> ease = spring ? (Func<double, double>)Spring : CubicOut;
            Run(new Tween { O = o, P = p, Ms = ms, Ease = ease, Apply = delegate(double k, bool last) { Put(o, p, last ? to : from + (to - from) * k); } });
        }
        public static void GlideColor(ColorSource src, Color to, int ms) {
            Color from = src.Color;
            if (from == to) { StopAnimation(src, ColorSource.ColorProperty); return; }
            Run(new Tween { O = src, P = ColorSource.ColorProperty, Ms = ms, Ease = CubicOut, Apply = delegate(double k, bool last) {
                src.Color = last ? to : Color.FromArgb((byte)(from.A + (to.A - from.A) * k), (byte)(from.R + (to.R - from.R) * k), (byte)(from.G + (to.G - from.G) * k), (byte)(from.B + (to.B - from.B) * k));
            } });
        }
        public static void Fade(Visual e, double to, int ms) {
            StopAnimation(e, Visual.OpacityProperty);
            double from = e.Opacity;
            if (from == to) return;
            Run(new Tween { O = e, P = Visual.OpacityProperty, Ms = ms, Ease = CubicOut, Apply = delegate(double k, bool last) { e.Opacity = last ? to : from + (to - from) * k; } });
        }
        /// <summary>One frame of a damped spring towards target. x and v carry over between frames, so retargeting mid-flight keeps
        /// the velocity (no jerk). w0 = 2π/duration, zeta = 1 - bounce, Apple's parametrisation; zeta ≥ 1 is critically damped and
        /// never passes the target. Closed form, so the step is exact for any dt.</summary>
        public static void SpringStep(ref double x, ref double v, double target, double dt, double w0, double zeta) {
            double a = x - target;
            if (zeta >= 1) {
                double b = v + w0 * a, e = Math.Exp(-w0 * dt);
                x = target + (a + b * dt) * e;
                v = (b - w0 * (a + b * dt)) * e;
            } else {
                double wd = w0 * Math.Sqrt(1 - zeta * zeta), b = (v + zeta * w0 * a) / wd;
                double e = Math.Exp(-zeta * w0 * dt), c = Math.Cos(wd * dt), s = Math.Sin(wd * dt);
                x = target + e * (a * c + b * s);
                v = e * ((wd * b - zeta * w0 * a) * c - (zeta * w0 * b + wd * a) * s);
            }
        }
        /// <summary>CSS cubic-bezier(x1,y1,x2,y2) easing at progress t (0..1), Newton-solved; the M3/Fluent curves are given in this form.
        /// Only needed if you swap the spring for a curve.</summary>
        public static double Bezier(double x1, double y1, double x2, double y2, double t) {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double u = t;
            for (int i = 0; i < 8; i++) {
                double x = 3 * u * (1 - u) * (1 - u) * x1 + 3 * u * u * (1 - u) * x2 + u * u * u - t;
                double d = 3 * (1 - u) * (1 - u) * x1 + 6 * u * (1 - u) * (x2 - x1) + 3 * u * u * (1 - x2);
                if (Math.Abs(d) < 1e-6) break;
                u -= x / d;
                if (u < 0) u = 0;
                else if (u > 1) u = 1;
            }
            return 3 * u * (1 - u) * (1 - u) * y1 + 3 * u * u * (1 - u) * y2 + u * u * u;
        }
        /// <summary>A lightly under-damped spring over t in 0..1: quick start, ~2 % overshoot, settled by the end.</summary>
        public static double Spring(double t) { const double a = 6.5, b = 5.5; return 1 - Math.Exp(-a * t) * (Math.Cos(b * t) + a / b * Math.Sin(b * t)); }
        /// <summary>Ease-out quint: fast, then a long settle, and never past the target, what a resizing window wants.</summary>
        public static double EaseOut(double t) { double u = 1 - Math.Max(0, Math.Min(1, t)); return 1 - u * u * u * u * u; }

        public static Rgb Hsl(double h, double s, double l) {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = l - c / 2, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // ---- the two input idioms the WPF code leans on ----

        /// <summary>WPF's MouseLeftButtonUp. Avalonia captures the pointer to whatever was pressed and sends the
        /// release there even after the pointer has left; WPF sends it to whatever is under the pointer. Giving the
        /// capture back on press makes the release land where WPF delivers it.</summary>
        public static void LeftUp(Control c, Action handler) {
            c.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (e.Pointer.Captured == c) e.Pointer.Capture(null); };
            c.PointerReleased += delegate(object o, PointerReleasedEventArgs e) { if (e.InitialPressMouseButton == MouseButton.Left) handler(); };
        }
        /// <summary>element.ToolTip = text; null takes the tip away.</summary>
        public static void Tip(Control c, object tip) { ToolTip.SetTip(c, tip); }
    }

    /// <summary>WPF's Cursors, as far as the controls use it.</summary>
    static class Cursors {
        public static readonly Cursor Hand = new Cursor(StandardCursorType.Hand), Arrow = new Cursor(StandardCursorType.Arrow), Cross = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>What Ui.Rendering hands its listeners: the time of the frame, as WPF's RenderingEventArgs.</summary>
    public sealed class RenderingEventArgs : EventArgs {
        public TimeSpan RenderingTime;
    }

    /// <summary>An animatable colour. Every brush made from it follows it, so one glide of this moves the whole
    /// window's accent together: the brushes are bound, not copied.</summary>
    public sealed class ColorSource : AvaloniaObject {
        public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<ColorSource, Color>("Color", Colors.Transparent);
        public static readonly StyledProperty<Color> LightProperty = AvaloniaProperty.Register<ColorSource, Color>("Light", Colors.Transparent);
        public static readonly StyledProperty<Color> DarkProperty = AvaloniaProperty.Register<ColorSource, Color>("Dark", Colors.Transparent);
        public Color Color { get { return GetValue(ColorProperty); } set { SetValue(ColorProperty, value); } }
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e) {
            base.OnPropertyChanged(e);
            if (e.Property != ColorProperty) return;
            var c = (Color)e.NewValue;
            SetValue(LightProperty, Ui.Mix(c, Colors.White, 0.26));
            SetValue(DarkProperty, Ui.Mix(c, Colors.Black, 0.30));
        }
        /// <summary>Only BeginAnimation(p, null), which is how the window stops a glide before setting a colour outright.</summary>
        public void BeginAnimation(AvaloniaProperty p, object none) { Ui.StopAnimation(this, p); }
        public SolidColorBrush MakeBrush() {
            var b = new SolidColorBrush();
            Bind(b, SolidColorBrush.ColorProperty, ColorProperty);
            return b;
        }
        /// <summary>The accent as a diagonal light-to-dark fill. The rail mark is drawn with this so it reads as a
        /// solid object rather than a flat chip; everything else in the window uses the plain accent brush.</summary>
        public LinearGradientBrush MakeGradient() {
            var g = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative) };
            var top = new GradientStop(Colors.Transparent, 0);
            var bottom = new GradientStop(Colors.Transparent, 1);
            g.GradientStops.Add(top);
            g.GradientStops.Add(bottom);
            Bind(top, GradientStop.ColorProperty, LightProperty);
            Bind(bottom, GradientStop.ColorProperty, DarkProperty);
            return g;
        }
        void Bind(AvaloniaObject o, AvaloniaProperty<Color> p, StyledProperty<Color> from) {
            o.Bind(p, this.GetObservable(from));
        }
    }

    sealed class SpringEase : Easing {
        public override double Ease(double t) { return Ui.Spring(t); }
    }

    /// <summary>Text with letter-spacing, which a TextBlock cannot do: the section labels in Settings.</summary>
}
