// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the look: the palette, the fonts that ship inside the exe, the animation curves, and the one
// accent colour every control shares. Nothing here knows about the window or the hardware.
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

    static class Ui {
        public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush(Col(hex)); b.Freeze(); return b; }
        public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static SolidColorBrush Brush(Rgb c) { return Brush(Color.FromRgb(c.R, c.G, c.B)); }
        public static Color Col(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static readonly Color EcoColor = Col("#2FBF8F"), BalColor = Col("#3F8CFF"), PerfColor = Col("#E2572C");
        public static readonly Color Warn = Col("#F3821D"), Danger = Col("#FF5C5C"), Ok = Col("#4AC06C");
        public static Color ModeColor(int i) { return i == 0 ? EcoColor : i == 2 ? PerfColor : BalColor; }
        /// <summary>c moved t of the way towards towards, in straight sRGB: enough for a short accent ramp.</summary>
        public static Color Mix(Color c, Color towards, double t) {
            return Color.FromRgb((byte)(c.R + (towards.R - c.R) * t), (byte)(c.G + (towards.G - c.G) * t), (byte)(c.B + (towards.B - c.B) * t));
        }
        public static FontFamily UiFont = new FontFamily("Segoe UI"), MonoFont = new FontFamily("Consolas");
        public static readonly Brush Card = Brush("#161311"), Sunken = Brush("#0F0D0B"), Pill = Brush("#201C19"), Line = Brush("#2C2825"),
            TextB = Brush("#EDEAE8"), TextHi = Brush("#F4F1EF"), SegText = Brush("#A8A3A0"), Hex = Brush("#A29D99"),
            Sub = Brush("#96918D"), Desc = Brush("#908B87"), Status = Brush("#8A8581"), Axis = Brush("#847F7B"), Foot = Brush("#7E7976"), Section = Brush("#787370");
        /// <summary>The accent brush every accent-coloured thing shares; its colour follows the mode (see ColorSource).</summary>
        public static SolidColorBrush Accent = new SolidColorBrush(BalColor);

        /// <summary>IBM Plex ships inside the exe; it is written beside the app once and loaded from there (WPF wants a
        /// directory URI). The OFL goes out with it: the licence requires every copy of the font software to carry the
        /// copyright notice and the licence text, and these files are a copy wherever they land.</summary>
        public static void LoadFonts() {
            string[] names = { "IBMPlexSans-Regular.ttf", "IBMPlexSans-Medium.ttf", "IBMPlexSans-SemiBold.ttf", "IBMPlexMono-Regular.ttf", "IBMPlexMono-Medium.ttf", "OFL.txt" };
            try {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
                bool all = System.IO.Directory.Exists(dir);
                if (all) foreach (string n in names) if (!System.IO.File.Exists(System.IO.Path.Combine(dir, n))) { all = false; break; }
                if (!all) {
                    dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.AppName, "fonts");
                    System.IO.Directory.CreateDirectory(dir);
                    var asm = Assembly.GetExecutingAssembly();
                    foreach (string n in names) {
                        string path = System.IO.Path.Combine(dir, n);
                        using (var st = asm.GetManifestResourceStream("Ohman.fonts." + n)) {
                            if (st == null) throw new InvalidOperationException("font resource missing: " + n);
                            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length == st.Length) continue;
                            using (var fs = System.IO.File.Create(path)) st.CopyTo(fs);
                        }
                    }
                }
                var uri = new Uri(dir.TrimEnd('\\') + "\\");
                UiFont = new FontFamily(uri, "./#IBM Plex Sans, Segoe UI");
                MonoFont = new FontFamily(uri, "./#IBM Plex Mono, Consolas");
                Log.Write("fonts: " + dir);
            } catch (Exception ex) { Log.Write("fonts: " + ex.Message + " (using Segoe UI)"); }
        }

        /// <summary>Animate a double property: the base value is set first, the animation runs from the old value and lets go.</summary>
        public static void Glide(DependencyObject o, DependencyProperty p, double to, int ms, bool spring) {
            var a = (IAnimatable)o;
            double from = (double)o.GetValue(p);
            if (double.IsNaN(from) || Math.Abs(from - to) < 0.5) { a.BeginAnimation(p, null); o.SetValue(p, to); return; }
            o.SetValue(p, to);
            var an = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop };
            an.EasingFunction = spring ? (IEasingFunction)new SpringEase() : new CubicEase { EasingMode = EasingMode.EaseOut };
            a.BeginAnimation(p, an);
        }
        public static void GlideColor(ColorSource src, Color to, int ms) {
            Color from = src.Color;
            src.Color = to;
            if (from == to) { src.BeginAnimation(ColorSource.ColorProperty, null); return; }
            src.BeginAnimation(ColorSource.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        public static void Fade(UIElement e, double to, int ms) {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            double from = e.Opacity; e.Opacity = to;
            if (from == to) return;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
            if (t <= 0) return 0; if (t >= 1) return 1;
            double u = t;
            for (int i = 0; i < 8; i++) {
                double x = 3 * u * (1 - u) * (1 - u) * x1 + 3 * u * u * (1 - u) * x2 + u * u * u - t;
                double d = 3 * (1 - u) * (1 - u) * x1 + 6 * u * (1 - u) * (x2 - x1) + 3 * u * u * (1 - x2);
                if (Math.Abs(d) < 1e-6) break;
                u -= x / d;
                if (u < 0) u = 0; else if (u > 1) u = 1;
            }
            return 3 * u * (1 - u) * (1 - u) * y1 + 3 * u * u * (1 - u) * y2 + u * u * u;
        }
        /// <summary>A lightly under-damped spring over t in 0..1: quick start, ~2 % overshoot, settled by the end.</summary>
        public static double Spring(double t) { const double a = 6.5, b = 5.5; return 1 - Math.Exp(-a * t) * (Math.Cos(b * t) + a / b * Math.Sin(b * t)); }
        /// <summary>Ease-out quint: fast, then a long settle, and never past the target — what a resizing window wants.</summary>
        public static double EaseOut(double t) { double u = 1 - Math.Max(0, Math.Min(1, t)); return 1 - u * u * u * u * u; }

        public static Rgb Hsl(double h, double s, double l) {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = l - c / 2, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }

    /// <summary>The order of every multiple-choice control, once. The page segment, the tray submenu and the code
    /// that ticks them all index the same arrays, so reordering a choice cannot leave one of them pointing elsewhere.</summary>
    static class Choice {
        public static readonly string[] Fan = { "Auto", "Max", "Curve", "Manual" };
        public static readonly FanMode[] FanModes = { FanMode.Auto, FanMode.Max, FanMode.Custom, FanMode.Manual };
        public static int Of(FanMode m) { for (int i = 0; i < FanModes.Length; i++) if (FanModes[i] == m) return i; return 0; }

        public static readonly string[] Key = { "Cycle", "Panel", "Max fan", "Run", "Off" };
        public static readonly KeyAction[] KeyActions = { KeyAction.Cycle, KeyAction.Show, KeyAction.MaxFan, KeyAction.Run, KeyAction.Off };
        public static int Of(KeyAction a) { for (int i = 0; i < KeyActions.Length; i++) if (KeyActions[i] == a) return i; return 0; }

        // lighting is two settings (mode 0 off / 1 ours / 2 Windows, and the effect) shown as one list
        public static readonly string[] Light = { "Off", "Static", "Breathe", "Cycle", "Wave", "Windows" };
        public static int OfLight(int mode, int effect) { return mode == 2 ? 5 : mode == 0 ? 0 : 1 + Math.Max(0, Math.Min(3, effect)); }
        public static int LightMode(int index) { return index == 5 ? 2 : index == 0 ? 0 : 1; }
        public static int LightEffect(int index) { return index >= 1 && index <= 4 ? index - 1 : 0; }
    }

    /// <summary>An animatable colour. A brush bound to it cannot be frozen, so it survives being a resource (WPF freezes
    /// plain brushes in an owned dictionary and in Style setters) and every DynamicResource user follows it.</summary>
    public sealed class ColorSource : Animatable {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register("Color", typeof(Color), typeof(ColorSource), new PropertyMetadata(Colors.Transparent, OnColor));
        public static readonly DependencyProperty LightProperty = DependencyProperty.Register("Light", typeof(Color), typeof(ColorSource), new PropertyMetadata(Colors.Transparent));
        public static readonly DependencyProperty DarkProperty = DependencyProperty.Register("Dark", typeof(Color), typeof(ColorSource), new PropertyMetadata(Colors.Transparent));
        public Color Color { get { return (Color)GetValue(ColorProperty); } set { SetValue(ColorProperty, value); } }
        protected override Freezable CreateInstanceCore() { return new ColorSource(); }
        static void OnColor(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            var s = (ColorSource)d; var c = (Color)e.NewValue;
            s.SetValue(LightProperty, Ui.Mix(c, Colors.White, 0.26));
            s.SetValue(DarkProperty, Ui.Mix(c, Colors.Black, 0.30));
        }
        public SolidColorBrush MakeBrush() {
            var b = new SolidColorBrush();
            Bind(b, SolidColorBrush.ColorProperty, "Color");
            return b;
        }
        /// <summary>The accent as a diagonal light-to-dark fill. The rail mark is drawn with this so it reads as a
        /// solid object rather than a flat chip; everything else in the window uses the plain accent brush.</summary>
        public LinearGradientBrush MakeGradient() {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            var top = new GradientStop(Colors.Transparent, 0); var bottom = new GradientStop(Colors.Transparent, 1);
            g.GradientStops.Add(top); g.GradientStops.Add(bottom);
            Bind(top, GradientStop.ColorProperty, "Light"); Bind(bottom, GradientStop.ColorProperty, "Dark");
            return g;
        }
        void Bind(DependencyObject o, DependencyProperty p, string path) {
            System.Windows.Data.BindingOperations.SetBinding(o, p, new System.Windows.Data.Binding(path) { Source = this });
        }
    }

    sealed class SpringEase : EasingFunctionBase {
        public SpringEase() { EasingMode = EasingMode.EaseIn; }
        protected override double EaseInCore(double t) { return Ui.Spring(t); }
        protected override Freezable CreateInstanceCore() { return new SpringEase(); }
    }

    /// <summary>Text with letter-spacing, which a TextBlock cannot do: the section labels in Settings.</summary>
}
