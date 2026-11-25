// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the Benchmark page, the in-game pill and the share card. The page is one layout for every state: a step
// strip, the FPS beside the runs before it, a chart that flips through every metric, four sensors in a strip, and one
// action row.
// Built in code like the rest of the app's controls; every size and colour comes from the design canvas.
// The Avalonia port of src\BenchUi.cs, kept member for member so a change there can be carried over from a diff.
//
// Differences from the WPF original
//  - Types only, unless listed here: FrameworkElement is Control, Brush is IBrush, ActualWidth is Bounds.Width,
//    Visibility is IsVisible, OnRender is Render, BitmapSource is Bitmap, the Mouse* events are the Pointer* ones
//    (clicks through Ui.LeftUp), tool tips through Ui.Tip. Labels are WpfTextBlock, as in the other controls.
//  - Where the frames come from. Windows downloads PresentMon and the page's first state offers that. Linux reads
//    MangoHud, which the owner puts into the game with a launch option, so:
//      * the first state (MangoHud not installed) offers the launch option to copy instead of a download;
//      * while waiting for a game the tile says what the frame source reports (BenchView.Note: no game with
//        MangoHud running, a game without its control socket, a log that is not one row a frame), and a click on
//        it, or the "Launch option" link beside Cancel, copies the launch option. With nothing to report it reads
//        as on Windows.
//    The wording comes from FrameCapture (src\linux\Bench.cs).
//  - Stroked icons are fitted into their box by hand (Ink.Fit, an addition), because Avalonia's Stretch.Uniform
//    leaves the pen out of the sum and draws them a pen's width larger than WPF does. Same reason as Osd.Fit.
//  - SnapsToDevicePixels has no counterpart; the charts draw their hairlines on half pixels as before.
//  - IsVisibleChanged. WPF's follows the parents and Avalonia's IsVisible is the control's own flag, so the page
//    watches its own IsVisible (what the window switches when it changes page) and its window's (hidden in the
//    tray): BenchPage.Seen is WPF's IsVisible.
//  - The settings line's editor is an Avalonia TextBox; LostKeyboardFocus is LostFocus.
//  - Files. Pictures and Documents are the XDG user directories. After a save the folder is opened with xdg-open
//    (Windows opens Explorer with the file selected). SafeFile keeps its one promise that matters here, a new file
//    and never an overwrite; the link checks are gone, because they guard an elevated process writing into the
//    user's folders and Ohman's window is not elevated on Linux (a symlinked ~/Pictures is also ordinary here).
//  - Copy puts the card on the clipboard as image/png, the type X11 and Wayland programs ask for.
//  - The card. WPF renders it with TextFormattingMode.Ideal and no layout rounding. Here every element of the card
//    has layout rounding switched off and the Display-mode letter spacing the window gives its own text taken back
//    (Card.Plain), which is the same thing.
//  - BenchPill. A borderless, topmost, transparent Window that is not activated and takes no clicks, made the way
//    Osd is (see the notes there, including what a Wayland compositor may ignore). It is placed 16 pixels inside
//    the primary screen's working area: which monitor an X11 window is on is not asked. WPF puts the pill back on
//    top once a second when another topmost window has landed above it; an X11 window manager keeps "above"
//    windows in its own order and offers no such call, so the timer is gone.
//  - "●" in the Runs view is not in IBM Plex Sans. Windows takes it from Segoe UI Symbol; a fallback font here
//    would draw it another size, so the dot is a drawn disc of the size Windows shows (BenchPage.Dot).
//  - Two small TextBlock classes are added: TrimText (a WpfTextBlock that is only trimmed when it really does not
//    fit) and BlockText (WPF's LineStackingStrategy.BlockLineHeight, which Avalonia's LineHeight is not quite).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WPath = Avalonia.Controls.Shapes.Path;

namespace Ohman {

    /// <summary>The benchmark's own colours. The series are kept apart from every mode accent: amber is never
    /// Performance's orange, violet is never Balanced's blue.</summary>
    static class Ink {
        public static readonly Color Cpu = Ui.Col("#E8B34A"), Gpu = Ui.Col("#A98BF2"), Fan = Ui.Col("#A8A3A0"), Good = Ui.Col("#7FD49A"),
            Warn = Ui.Col("#F3821D"), WarnText = Ui.Col("#F3A35C"), Bin = Ui.Col("#5A5450"), Hint = Ui.Col("#6F6A66"), Ref = Ui.Col("#8A8581"), Dot = Ui.Col("#4A4541");
        public static readonly IBrush LineB = Ui.Brush("#2C2825");
        public static TextBlock Mono(double size, IBrush fg) { return new TrimText { FontFamily = Ui.MonoFont, FontSize = size, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; }
        public static TextBlock Sans(double size, IBrush fg) { return new TrimText { FontFamily = Ui.UiFont, FontSize = size, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; }
        public static Tracked Label(string t) { return new Tracked { Text = t, Size = 9.5, Tracking = 1.3, Fill = Ui.Section, VerticalAlignment = VerticalAlignment.Center }; }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string F0(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "--" : v.ToString("0", Inv); }
        public static string F1(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "--" : v.ToString("0.0", Inv); }
        public static string F2(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "--" : v.ToString("0.00", Inv); }
        public static string K(double rpm) { return double.IsNaN(rpm) ? "--" : rpm >= 1000 ? (rpm / 1000).ToString("0.0", Inv) + "k" : rpm.ToString("0", Inv); }
        /// <summary>A change in fps as the page, the Runs list and the card all write it: a sign, no decimals.</summary>
        public static string Delta(double d) { return (d >= 0 ? "+" : "−") + Math.Abs(d).ToString("0", Inv); }
        /// <summary>Its colour: grey under half a frame, green up, amber down.</summary>
        public static Color DeltaColor(double d) { return Math.Abs(d) < 0.5 ? Ref : d > 0 ? Good : WarnText; }
        public static string Clock(double s) { if (double.IsNaN(s) || s < 0) s = 0; int t = (int)Math.Round(s); return (t / 60) + ":" + (t % 60).ToString("00", Inv); }
        public static Geometry Geo(string d) { return Geometry.Parse(d); }
        /// <summary>WPF's Stretch.Uniform for a path in a square box: the outline is scaled so that it and its pen
        /// together fill the box, and centred. pen 0 for a filled glyph. The path itself is left unstretched.</summary>
        public static Geometry Fit(string d, double box, double pen) {
            var g = Geometry.Parse(d);
            Rect b = g.Bounds;
            if (b.Width <= 0 || b.Height <= 0) return g;
            double k = Math.Min((box - pen) / b.Width, (box - pen) / b.Height);
            g.Transform = new MatrixTransform(Matrix.CreateTranslation(-b.X, -b.Y) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation((box - b.Width * k) / 2, (box - b.Height * k) / 2));
            return g;
        }
        /// <summary>A stroked icon in its box, as every one on the page is made: round caps and joins.</summary>
        public static WPath Icon(string d, double box, double pen, IBrush stroke) {
            return new WPath { Data = Fit(d, box, pen), Stroke = stroke, StrokeThickness = pen, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = box, Height = box, Stretch = Stretch.None };
        }
    }

    /// <summary>A WpfTextBlock that may be trimmed with an ellipsis. WpfTextBlock rounds its width to the nearest
    /// device pixel, as WPF does, which can be a hair less than the text needs; Avalonia then lays the text out again
    /// in that width and trims a word that fitted. Text that fitted when it was measured is laid out in the room it
    /// asked for, and only text that really was given too little is trimmed. (An addition: WPF has no such case.)</summary>
    class TrimText : WpfTextBlock {
        double natural;
        protected override Size MeasureOverride(Size a) {
            var s = base.MeasureOverride(a);
            var tl = TextLayout;
            natural = tl == null ? s.Width : tl.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right;
            return s;
        }
        protected override Size ArrangeOverride(Size f) {
            if (f.Width < natural && natural - f.Width < 1) { base.ArrangeOverride(new Size(natural, f.Height)); return f; }
            return base.ArrangeOverride(f);
        }
    }

    /// <summary>A TextBlock with WPF's LineStackingStrategy.BlockLineHeight. Both systems make the line exactly
    /// LineHeight tall; WPF also moves the baseline, to the same share of the line it has in the font's own line
    /// (ascent over ascent plus descent), where Avalonia leaves it at the font's ascent. The text is drawn that much
    /// higher here; its box is the same. (An addition.)</summary>
    class BlockText : WpfTextBlock {
        protected override void RenderTextLayout(DrawingContext dc, Point origin) {
            double up = 0;
            try {
                var m = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch).GlyphTypeface.Metrics;
                double asc = -m.Ascent * FontSize / m.DesignEmHeight, desc = (m.Descent + m.LineGap) * FontSize / m.DesignEmHeight;
                if (!double.IsNaN(LineHeight) && asc + desc > 0) up = asc - LineHeight * asc / (asc + desc);
                double k = LayoutHelper.GetLayoutScale(this);
                up = Math.Round(up * k, MidpointRounding.AwayFromZero) / k;
            } catch { }
            base.RenderTextLayout(dc, new Point(origin.X, origin.Y - up));
        }
    }

    // ================================================================== small pieces ==============================
    /// <summary>A line with its area under it, gaps where a value is missing; optional dashed reference line and a
    /// pair of dashed markers around a pause.</summary>
    sealed class SeriesChart : Control {
        public double[] Values = new double[0];
        public int Slots = 100;                     // the x axis is this many values wide; a live run fills it as it goes
        public double Ref = double.NaN;
        public Color Color = Ui.BalColor;
        public double Lo = double.NaN, Hi = double.NaN;
        public double MinSpan = 10;
        public List<int> Marks = new List<int>();   // value indexes to draw a dashed pause marker before
        public SeriesChart() { Height = 74; }
        protected override Size MeasureOverride(Size a) { return new Size(double.IsInfinity(a.Width) ? 224 : a.Width, Height); }
        public void Repaint() { InvalidateVisual(); }
        public override void Render(DrawingContext dc) {
            double W = Bounds.Width, H = Bounds.Height;
            int n = Values.Length;
            if (n < 2 || W <= 0) return;
            double lo = Lo, hi = Hi;
            if (double.IsNaN(lo) || double.IsNaN(hi)) {
                lo = double.MaxValue; hi = double.MinValue;
                foreach (double v in Values) if (!double.IsNaN(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                if (!double.IsNaN(Ref)) { lo = Math.Min(lo, Ref); hi = Math.Max(hi, Ref); }
                if (lo > hi) return;
                double span = Math.Max(MinSpan, hi - lo);
                double mid = (hi + lo) / 2;
                lo = mid - span * 0.62; hi = mid + span * 0.55;
            }
            int slots = Math.Max(Slots, n);
            Func<int, double> X = delegate(int i) { return i / (double)(slots - 1) * W; };
            Func<double, double> Y = delegate(double v) { return 4 + (1 - (v - lo) / (hi - lo)) * (H - 6); };
            var stroke = new Pen(new SolidColorBrush(Color), 1.4) { LineJoin = PenLineJoin.Round };
            var fill = new SolidColorBrush(Color) { Opacity = 0.12 };
            int start = -1;
            for (int i = 0; i <= n; i++) {
                bool ok = i < n && !double.IsNaN(Values[i]) && !Marks.Contains(i);
                if (ok && start < 0) { start = i; continue; }
                if (ok) continue;
                if (start >= 0) {
                    int end = i - 1;
                    if (end > start) {
                        var line = new StreamGeometry();
                        var area = new StreamGeometry();
                        using (var c = line.Open()) {
                            c.BeginFigure(new Point(X(start), Y(Values[start])), false);
                            for (int k = start + 1; k <= end; k++) c.LineTo(new Point(X(k), Y(Values[k])));
                            c.EndFigure(false);
                        }
                        using (var c = area.Open()) {
                            c.BeginFigure(new Point(X(start), H), true);
                            for (int k = start; k <= end; k++) c.LineTo(new Point(X(k), Y(Values[k])));
                            c.LineTo(new Point(X(end), H));
                            c.EndFigure(true);
                        }
                        dc.DrawGeometry(fill, null, area);
                        dc.DrawGeometry(null, stroke, line);
                    }
                    start = i < n && !double.IsNaN(Values[i]) ? i : -1;
                }
            }
            if (!double.IsNaN(Ref) && Ref > lo && Ref < hi) {
                var dash = new Pen(new SolidColorBrush(Ink.Ref), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
                double y = Math.Round(Y(Ref)) + 0.5;
                dc.DrawLine(dash, new Point(0, y), new Point(W, y));
            }
            var mark = new Pen(new SolidColorBrush(Ink.WarnText), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            foreach (int m in Marks) { double x = Math.Round(X(m)) + 0.5; dc.DrawLine(mark, new Point(x, 4), new Point(x, H)); }
        }
    }

    /// <summary>One of the four sensors in the strip under the graph: a tracked label, the value, a thin bar. What the
    /// big tiles said underneath (peak, watts, clock) is the tooltip.</summary>
    sealed class MiniTile : Border {
        readonly Run val = new Run(), unit = new Run();
        readonly ColumnDefinition c1 = new ColumnDefinition(), c2 = new ColumnDefinition(), c3 = new ColumnDefinition();
        readonly Border b1 = new Border(), b2 = new Border();
        public MiniTile(string label) {
            Padding = new Thickness(0, 10, 10, 12);
            Background = Brushes.Transparent;
            var sp = new StackPanel();
            sp.Children.Add(Ink.Label(label));
            var big = new WpfTextBlock { FontFamily = Ui.UiFont, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB, Margin = new Thickness(0, 6, 0, 0) };
            unit.FontWeight = FontWeight.Normal; unit.Foreground = Ui.Sub; unit.FontSize = 11;
            big.Inlines.Add(val); big.Inlines.Add(unit);
            var track = new Grid { Height = 3, Margin = new Thickness(0, 7, 0, 0), ClipToBounds = true };
            track.Children.Add(new Border { Background = Ink.LineB, CornerRadius = new CornerRadius(1.5) });
            var bars = new Grid();
            bars.ColumnDefinitions.Add(c1); bars.ColumnDefinitions.Add(c2); bars.ColumnDefinitions.Add(c3);
            Grid.SetColumn(b2, 1);
            bars.Children.Add(b1); bars.Children.Add(b2);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Child = bars, ClipToBounds = true });
            sp.Children.Add(big); sp.Children.Add(track);
            Child = sp;
        }
        public void Set(string value, string u, double f1, Color k1, double f2, Color k2, string caption) {
            val.Text = value; unit.Text = u; Ui.Tip(this, string.IsNullOrEmpty(caption) ? null : caption);
            f1 = double.IsNaN(f1) ? 0 : Math.Max(0, Math.Min(1, f1)); f2 = double.IsNaN(f2) ? 0 : Math.Max(0, Math.Min(1 - f1, f2));
            c1.Width = new GridLength(f1, GridUnitType.Star); c2.Width = new GridLength(f2, GridUnitType.Star); c3.Width = new GridLength(Math.Max(0.0001, 1 - f1 - f2), GridUnitType.Star);
            b1.Background = Ui.Brush(k1); b2.Background = Ui.Brush(k2);
        }
    }

    /// <summary>This run beside the ones before it in the same setup: four slots, oldest on the left, the newest in the
    /// mode's colour. Slots with no run yet are drawn empty, so the first run and the tenth look like the same page.</summary>
    sealed class RecentBars : Control {
        double[] vals = new double[0];
        Color now = Ui.BalColor;
        bool lastIsNow;
        static Typeface face;
        static bool faced;
        const int Slots = 4;
        const double W = 22, Gap = 6, Top = 14, H = 46;
        public RecentBars() { Width = Slots * W + (Slots - 1) * Gap; Height = Top + H; }
        /// <summary>Oldest first; the last one is this run when lastIsNow. Only the newest four are drawn.</summary>
        public void Set(List<double> oldestFirst, bool lastIsNow, Color mode) {
            int skip = Math.Max(0, oldestFirst.Count - Slots);
            vals = oldestFirst.GetRange(skip, oldestFirst.Count - skip).ToArray();
            this.lastIsNow = lastIsNow; now = mode;
            InvalidateVisual();
        }
        public override void Render(DrawingContext dc) {
            if (!faced) { face = new Typeface(Ui.MonoFont, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal); faced = true; }
            double hi = 0; foreach (double v in vals) if (v > hi) hi = v;
            int empty = Slots - vals.Length;
            var dash = new Pen(Ui.Brush(Ink.Dot), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            for (int i = 0; i < Slots; i++) {
                double x = i * (W + Gap);
                if (i < empty) { dc.DrawRectangle(null, dash, new Rect(x + 0.5, Top + H - 30 + 0.5, W - 1, 29), 3, 3); continue; }
                double v = vals[i - empty];
                bool isNow = lastIsNow && i == Slots - 1;
                // from zero, so a 6 fps change looks like one; the number above each bar says it exactly
                double h = hi > 0 && v > 0 ? Math.Max(4, v / hi * H) : 4;
                dc.DrawRectangle(Ui.Brush(isNow ? now : Color.FromRgb(0x3A, 0x35, 0x31)), null, new Rect(x, Top + H - h, W, h), 3, 3);
                var t = new FormattedText(Ink.F0(v), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9.5, Ui.Brush(isNow ? "#EDEAE8" : "#A8A3A0"));
                dc.DrawText(t, new Point(Math.Round(x + W / 2 - t.Width / 2), Math.Round(Top + H - h - 3 - t.Height)));
            }
        }
    }

    /// <summary>One segment of the step strip: a thin progress line, then label and value on one line. The first two
    /// cycle through their choices on a click while nothing is running.</summary>
    sealed class StepSeg : Border {
        readonly ColumnDefinition on = new ColumnDefinition(), off = new ColumnDefinition();
        readonly Border fill = new Border { CornerRadius = new CornerRadius(1.5) };
        readonly TextBlock label, value;
        bool clickable;
        public event Action Click;
        public StepSeg() {
            Background = Brushes.Transparent;
            var sp = new StackPanel();
            var track = new Grid { Height = 3 };
            track.Children.Add(new Border { Background = Ink.LineB, CornerRadius = new CornerRadius(1.5) });
            var g = new Grid(); g.ColumnDefinitions.Add(on); g.ColumnDefinitions.Add(off); g.Children.Add(fill);
            track.Children.Add(g);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            label = Ink.Sans(12.5, Ui.Desc); value = Ink.Sans(12.5, Ui.TextB); value.Margin = new Thickness(5, 0, 0, 0);
            line.Children.Add(label); line.Children.Add(value);
            sp.Children.Add(track); sp.Children.Add(line);
            Child = sp;
            ClipToBounds = true;
            Ui.LeftUp(this, delegate { if (clickable) { var h = Click; if (h != null) h(); } });
            PointerEntered += delegate { if (clickable) value.Foreground = Ui.Accent; };
            PointerExited += delegate { if (clickable) value.Foreground = Ui.TextB; };
        }
        /// <param name="tone">"" plain, "on" the step in progress, "dim" done or not yet, "warn" paused</param>
        public void Set(string l, string v, double pct, string tone, bool click, IBrush accent) {
            label.Text = l; value.Text = v ?? "";
            pct = Math.Max(0, Math.Min(1, double.IsNaN(pct) ? 0 : pct));
            on.Width = new GridLength(pct, GridUnitType.Star); off.Width = new GridLength(Math.Max(0.0001, 1 - pct), GridUnitType.Star);
            fill.Background = tone == "warn" ? Ui.Brush(Ink.Warn) : accent;
            value.Foreground = tone == "warn" ? Ui.Brush(Ink.WarnText) : tone == "dim" ? Ui.Desc : Ui.TextB;
            clickable = click;
            Cursor = click ? Cursors.Hand : Cursors.Arrow;
            value.TextDecorations = click ? Dotted() : null;
            Ui.Tip(this, click ? "Click to change" : null);
        }
        static TextDecorationCollection dotted;
        static TextDecorationCollection Dotted() {
            if (dotted == null) {
                dotted = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline, Stroke = Ui.Foot, StrokeThickness = 1, StrokeThicknessUnit = TextDecorationUnit.Pixel,
                    StrokeDashArray = new AvaloniaList<double> { 1, 2 }, StrokeOffset = 2, StrokeOffsetUnit = TextDecorationUnit.Pixel } };
            }
            return dotted;
        }
    }

    /// <summary>The 40 px action button: a glyph and a word on the left, a quiet hint on the right.</summary>
    sealed class ActionButton : Border {
        readonly WPath glyph = new WPath { Width = 12, Height = 12, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        readonly TextBlock text = Ink.Sans(13.5, Ui.TextB), hint = Ink.Mono(10.5, Ui.Status);
        IBrush bg, hover;
        bool enabled = true;
        public event Action Click;
        public ActionButton() {
            Height = 40; CornerRadius = new CornerRadius(10); Cursor = Cursors.Hand; Padding = new Thickness(14, 0, 14, 0);
            var g = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(glyph); left.Children.Add(text);
            hint.HorizontalAlignment = HorizontalAlignment.Right; hint.Margin = new Thickness(12, 0, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(hint, 1);
            g.Children.Add(left); g.Children.Add(hint);
            Child = g;
            PointerEntered += delegate { if (enabled) Background = hover; };
            PointerExited += delegate { Background = bg; };
            Ui.LeftUp(this, delegate { if (!enabled) return; var h = Click; if (h != null) h(); });
        }
        public void Set(string t, string h, string path, bool filledGlyph, IBrush glyphBrush, bool primary, IBrush accent) {
            text.Text = t; hint.Text = h ?? "";
            glyph.IsVisible = path != null;
            if (path != null) {
                glyph.Data = Ink.Fit(path, 12, filledGlyph ? 0 : 2.2);
                glyph.Fill = filledGlyph ? glyphBrush : null;
                glyph.Stroke = filledGlyph ? null : glyphBrush;
                glyph.StrokeThickness = 2.2; glyph.StrokeLineCap = PenLineCap.Round; glyph.StrokeJoin = PenLineJoin.Round;
            }
            text.Foreground = primary ? Brushes.White : Ui.TextB;
            text.FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal;
            hint.Foreground = primary ? new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) : Ui.Status;
            bg = primary ? (IBrush)accent : Ui.Pill;
            hover = primary ? (IBrush)accent : Ui.Brush("#28231F");
            Background = IsPointerOver ? hover : bg;
            Opacity = 1;
        }
        public bool Enabled { set { enabled = value; Cursor = value ? Cursors.Hand : Cursors.Arrow; Opacity = value ? 1 : 0.5; } }
        /// <summary>The narrow button holds one thing (a count, Run again) and holds it in the middle.</summary>
        public bool Centered { set { var g = (Grid)Child; ((Control)g.Children[0]).HorizontalAlignment = value ? HorizontalAlignment.Center : HorizontalAlignment.Left; Grid.SetColumnSpan((Control)g.Children[0], value ? 2 : 1); } }
        public void Warn(string h) { hint.Text = h; hint.Foreground = Ui.Brush(Ink.WarnText); }
    }

    // ================================================================== the page ==================================
    public sealed class BenchPage : Grid {
        public const double PageH = 604;
        const string IcoPlay = "M7 4.5 L19.5 12 L7 19.5 Z", IcoDown = "M12 4 V15 M7.5 10.5 L12 15 L16.5 10.5 M5 19.5 H19", IcoList = "M4 6 H20 M4 12 H20 M4 18 H14";
        const string IcoPad = "M6.5 8.5 H17.5 A4 4 0 0 1 21.5 12.5 V14.5 A3 3 0 0 1 16.4 16.6 L15 15.2 H9 L7.6 16.6 A3 3 0 0 1 2.5 14.5 V12.5 A4 4 0 0 1 6.5 8.5 Z M7.5 11 V14 M6 12.5 H9 M15.5 12 H15.6 M17.5 13.5 H17.6";
        const string IcoTrash = "M5 7 H19 M10 7 V5 H14 V7 M7 7 L8 19 H16 L17 7 M10.5 10.5 V15.5 M13.5 10.5 V15.5";
        const string IcoPencil = "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z";
        const string IcoCopy = "M9 9 H19 V20 H9 Z M5 15 V4 H15";            // Linux: the launch option to copy, where Windows has a download
        static readonly string[] GraphNames = { "FPS", "CPU temperature", "GPU temperature", "CPU power", "GPU power", "CPU clock", "GPU load", "Fans" };

        readonly Engine E;
        readonly Bench B;
        public event Action StartClicked;                  // the window starts the run: it also has to hide itself
        public event Action<string, bool> Toast;
        public Control Head { get { return head; } }

        // ---- the dashboard ----
        readonly Grid head = new Grid { Background = Brushes.Transparent };
        readonly TextBlock title = new TrimText { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Ellipse statusDot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock status = MaxW(Ink.Mono(11, Ui.Status), 230);
        static TextBlock MaxW(TextBlock t, double w) { t.MaxWidth = w; return t; }
        readonly StepSeg[] steps = { new StepSeg(), new StepSeg(), new StepSeg() };
        readonly Grid meta = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        readonly Border settingsBtn = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        readonly TextBlock settingsText = Ink.Sans(12.5, Ui.SegText);
        readonly TextBox settingsBox = new TextBox();
        readonly Grid board = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        readonly Border chartTile = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 10, 0, 12) };
        readonly TextBlock chartLabel = Ink.Sans(14, Ui.TextB);
        string graphTag = "", ftAs = "", ftBs = "";       // the line under the graph: a tag ("live", "settling"), then frame time or the graph's own note
        readonly StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock chartValue = Ink.Sans(14, Ui.TextB);
        readonly Run bigVal = new Run(), bigUnit = new Run();
        readonly Run subAv = new Run(), subAt = new Run();
        readonly SeriesChart chart = new SeriesChart();
        // the hero: the run's FPS and lows, and how it sits against the runs before it in the same setup
        readonly Border heroTile = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 0, 12) };
        readonly TextBlock heroBig = new BlockText { FontSize = 56, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB, LineHeight = 58 };
        readonly Run heroFps = new Run(), heroUnit = new Run(" fps");
        readonly TextBlock heroSub = Ink.Mono(11, Ui.Foot);
        readonly Run heroLow = new Run(), heroLow01 = new Run();
        readonly RecentBars recent = new RecentBars();
        readonly TextBlock heroDelta = Ink.Mono(10.5, Ui.Foot);
        readonly TextBlock ftLine = Ink.Mono(10.5, Ui.Foot);
        readonly Border waitTile = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, 0, 1), Height = 98 };
        readonly MiniTile tCpu = new MiniTile("CPU"), tGpu = new MiniTile("GPU"), tPow = new MiniTile("POWER"), tFan = new MiniTile("FANS");
        // ---- the bottom ----
        readonly Grid bottom = new Grid();
        readonly ActionButton main = new ActionButton(), side = new ActionButton();
        readonly Grid footer = new Grid();
        readonly TextBlock footL = Ink.Mono(11, Ui.Foot);
        readonly StackPanel footLinks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        readonly Grid dash = new Grid();
        readonly Grid runsView = new Grid();
        readonly Grid shareView = new Grid();

        // ---- what is shown ----
        int gi = -1;                                      // the graph on show; -1 = the state's own default
        string giFor = "";
        BenchRun shown;                                   // a finished run on screen (the one just done, or one picked from Runs)
        BenchRun lastRun;                                 // the newest stored run, dimmed behind Start
        List<BenchRun> runs;                              // every stored run, newest first; null = not read yet
        string note = "";                                 // why the last run ended without a result, until the next one
        readonly List<SensorSnapshot> ring = new List<SensorSnapshot>();
        readonly List<int[]> fanRing = new List<int[]>();
        int[] fans;
        SensorSnapshot latest;
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool showRuns;
        string runsGame;

        public BenchPage(Engine e, Bench b) {
            E = e; B = b;
            Margin = new Thickness(24, 24, 24, 20);
            IsVisible = false;
            Ui.Tip(settingsBtn, "The game's own settings for this run: click to edit");
            BuildDash();
            BuildRuns();
            Children.Add(dash);
            Children.Add(runsView);
            runsView.IsVisible = false;
            BuildShare();
            Children.Add(shareView);
            shareView.IsVisible = false;
            tick.Tick += delegate { if (Seen && !showRuns) Render(); };
            PropertyChanged += delegate(object o, AvaloniaPropertyChangedEventArgs pe) { if (pe.Property == IsVisibleProperty) SeenChanged(); };
        }
        /// <summary>WPF's IsVisible: the page is the one on show and its window is on screen. Hidden in the tray the
        /// page draws nothing and its timer is stopped, and it draws afresh the moment the window comes back.</summary>
        bool Seen { get { return IsVisible && (host == null || host.IsVisible); } }
        TopLevel host;
        void SeenChanged() { if (Seen) { tick.Start(); Render(); } else { tick.Stop(); CommitSettings(); } }
        void HostChanged(object o, AvaloniaPropertyChangedEventArgs pe) { if (pe.Property == IsVisibleProperty) SeenChanged(); }
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
            base.OnAttachedToVisualTree(e);
            host = e.Root as TopLevel;
            if (host != null) host.PropertyChanged += HostChanged;
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
            if (host != null) host.PropertyChanged -= HostChanged;
            host = null;
            base.OnDetachedFromVisualTree(e);
        }

        // ---------- inputs from the window ----------
        public void OnSensors(SensorSnapshot s) {
            latest = s;
            ring.Add(s); if (ring.Count > 100) ring.RemoveAt(0);
        }
        public void OnFans(int[] f) { if (f == null || f.Length < 2) return; fans = f; fanRing.Add(f); if (fanRing.Count > 100) fanRing.RemoveAt(0); }
        public void Flip(int d) {
            if (showRuns) return;
            int n = GraphNames.Length;
            if (shareOpen) { if (E.S.ShareLayout != 1) { shareGraph = ((shareGraph + d) % n + n) % n; Render(); } return; }
            CommitSettings(); gi = ((Cur() + d) % n + n) % n; Render();
        }
        public void Finished(BenchRun r) { shown = r; lastRun = r; runs = null; note = ""; gi = -1; showRuns = false; shareOpen = false; Render(); }
        /// <summary>A run has started, from the page, Run again or the hotkey. The window is usually hidden by then, so
        /// Render will not get to reset the views: whatever was open (a result, Runs, Share) belongs to before.</summary>
        public void RunStarted() { CommitSettings(); shown = null; showRuns = false; shareOpen = false; note = ""; }
        /// <summary>The run-stopping key for the hints, from the window, which knows whether the system granted it.</summary>
        public Func<Hotkey> StopKey;
        Hotkey StopHotkey() { var f = StopKey; return f != null ? f() : Hotkey.None; }
        public void EndedEarly(string why) { note = why == "stopped" ? "" : why; Render(); }
        /// <summary>The page's height for a work area of avail DIPs: the designed 604 where it fits, shorter where it
        /// does not (the board then scrolls, the buttons stay). Returns the height with the margins.</summary>
        public double Fit(double avail) {
            Height = double.NaN;
            Measure(new Size(398, double.PositiveInfinity));
            double natural = DesiredSize.Height, cap = Math.Max(360, avail);   // 360: the floor Morph uses for the window
            // Held at its own height, not left to stretch: the window grows into place over a few frames, and a page
            // sized by the window meanwhile gave the board less than it needs, so its scrollbar flashed on the way in.
            // Pinned, it lays out once at full size and the clipping host reveals it, the way the other pages arrive.
            if (natural <= cap) { Height = Math.Max(0, natural - Margin.Top - Margin.Bottom); return natural; }
            Height = cap - Margin.Top - Margin.Bottom;                          // too tall for this screen: the board scrolls, the buttons stay
            return cap;
        }
        /// <summary>The page's shape changed (another state, Runs opened or closed): the window should follow.</summary>
        public event Action Resized;
        string shape = "";
        void Shaped(string now) { if (now == shape) return; shape = now; var h = Resized; if (h != null) h(); }
        /// <summary>Screenshot aid, with Bench.Pose: "result" shows the newest stored run, "runs" the list.</summary>
        public void Pose(string state) {
            if (!E.Hw.IsDemo && !E.S.NoPersist) return;          // a screenshot aid: never on a real, saving instance
            if (state.StartsWith("result") || state.StartsWith("share")) { var all = Runs(); if (all.Count > 0) shown = all[0]; }
            int pg; if (state.StartsWith("result") && int.TryParse(state.Substring(6), out pg)) { giFor = "Result"; gi = pg; }   // result7: that graph
            if (state.StartsWith("share")) { shareOpen = true; shareGraph = 0; int l; if (int.TryParse(state.Substring(5), out l)) E.S.ShareLayout = l; }
            if (state == "runs") showRuns = true;
            Render();
        }

        // ---------- building ----------
        void BuildDash() {
            dash.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dash.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dash.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dash.RowDefinitions.Add(new RowDefinition());
            dash.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            dash.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // header
            title.FontFamily = Ui.UiFont;
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(statusDot); right.Children.Add(status);
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(right, 1); right.Margin = new Thickness(12, 0, 0, 0);
            head.Children.Add(title); head.Children.Add(right);
            dash.Children.Add(head);
            // strip
            var strip = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            for (int i = 0; i < 3; i++) {
                strip.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(steps[i], i);
                steps[i].Margin = new Thickness(i == 0 ? 0 : 2, 0, i == 2 ? 0 : 2, 0);
                strip.Children.Add(steps[i]);
            }
            steps[0].Click += delegate { B.SetChoices((B.WarmChoice + 1) % 3, B.MeasureChoice); Render(); };
            steps[1].Click += delegate { B.SetChoices(B.WarmChoice, (B.MeasureChoice + 1) % 3); Render(); };
            Grid.SetRow(strip, 1);
            dash.Children.Add(strip);
            // result meta: the game settings line and the change against the last comparable run
            var sl = new StackPanel { Orientation = Orientation.Horizontal };
            var pencil = new WPath { Data = Ink.Fit(IcoPencil, 11, 0), Fill = Ui.Foot, Width = 11, Height = 11, Stretch = Stretch.None, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sl.Children.Add(settingsText); sl.Children.Add(pencil);
            settingsBtn.Child = sl;
            Ui.LeftUp(settingsBtn, delegate { EditSettings(); });
            settingsBox.FontFamily = Ui.UiFont; settingsBox.FontSize = 12.5; settingsBox.Foreground = Ui.TextB; settingsBox.CaretBrush = Ui.TextB;
            settingsBox.Background = Ui.Sunken; settingsBox.BorderThickness = new Thickness(0); settingsBox.Padding = new Thickness(6, 2, 6, 2); settingsBox.MaxLength = 80;
            settingsBox.MinHeight = 0; settingsBox.SelectionBrush = Ui.Accent;
            settingsBox.IsVisible = false; settingsBox.Margin = new Thickness(-6, -3, 12, -3);
            settingsBox.KeyDown += delegate(object o, KeyEventArgs ke) { if (ke.Key == Key.Enter) { CommitSettings(); ke.Handled = true; } else if (ke.Key == Key.Escape) { settingsBox.IsVisible = false; settingsBtn.IsVisible = true; ke.Handled = true; } };
            settingsBox.LostFocus += delegate { CommitSettings(); };
            meta.Children.Add(settingsBtn); meta.Children.Add(settingsBox);
            Grid.SetRow(meta, 2);
            dash.Children.Add(meta);
            // the board: the hero, the graph (or the wait tile), the four sensors in one strip
            for (int i = 0; i < 3; i++) board.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BuildHero();
            board.Children.Add(heroTile);
            BuildChart();
            Grid.SetRow(chartTile, 1); board.Children.Add(chartTile);
            BuildWait();
            Grid.SetRow(waitTile, 1); board.Children.Add(waitTile);
            var strip4 = new Grid { };
            var stripLine = new Border { Child = strip4 };
            MiniTile[] mts = { tCpu, tGpu, tPow, tFan };
            for (int i = 0; i < 4; i++) { strip4.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetColumn(mts[i], i); strip4.Children.Add(mts[i]); }
            Grid.SetRow(stripLine, 2); board.Children.Add(stripLine);
            var top = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
            Grid.SetRowSpan(top, 3);
            board.Children.Add(top);
            var boardScroll = new ScrollViewer { Content = board, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(boardScroll, 3);
            dash.Children.Add(boardScroll);
            // bottom: two buttons, or a footer line with links
            bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            side.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(side, 1); side.Centered = true;
            bottom.Children.Add(main); bottom.Children.Add(side);
            main.Click += OnMain; side.Click += OnSide;
            var fb = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(footL); footer.Children.Add(footLinks);
            fb.Child = footer;
            var bot = new Grid(); bot.Children.Add(bottom); bot.Children.Add(fb);
            footerBox = fb;
            Grid.SetRow(bot, 5);
            dash.Children.Add(bot);
        }
        Border footerBox;

        void BuildChart() {
            var sp = new StackPanel();
            var hdr = new Grid { Height = 26 };
            var lab = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chartValue.FontWeight = FontWeight.SemiBold; chartValue.Margin = new Thickness(8, 0, 0, 0);
            chartValue.Inlines.Add(bigVal); chartValue.Inlines.Add(bigUnit);
            bigUnit.FontWeight = FontWeight.Normal; bigUnit.Foreground = Ui.Sub; bigUnit.FontSize = 12;
            subAt.Foreground = Ui.Desc;
            lab.Children.Add(chartLabel); lab.Children.Add(chartValue);
            hdr.Children.Add(lab);
            var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < GraphNames.Length; i++) dots.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(3, 0, 3, 0), Fill = Ui.Brush(Ink.Dot) });
            nav.Children.Add(dots);
            nav.Children.Add(Arrow("M15 5 L8 12 L15 19", -1, "Previous graph (Left arrow)"));
            nav.Children.Add(Arrow("M9 5 L16 12 L9 19", 1, "Next graph (Right arrow)"));
            hdr.Children.Add(nav);
            sp.Children.Add(hdr);
            chart.Height = 96; chart.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(chart);
            ftLine.Margin = new Thickness(0, 9, 0, 0);
            sp.Children.Add(ftLine);
            chartTile.Child = sp;
        }
        void BuildHero() {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            heroBig.FontFamily = Ui.UiFont;
            heroUnit.FontSize = 15; heroUnit.FontWeight = FontWeight.Normal; heroUnit.Foreground = Ui.Sub;
            heroBig.Inlines.Add(heroFps); heroBig.Inlines.Add(heroUnit);
            heroSub.Margin = new Thickness(0, 6, 0, 0);
            heroSub.Inlines.Add(heroLow); heroSub.Inlines.Add(new Run(" 1% low · "));
            heroSub.Inlines.Add(heroLow01); heroSub.Inlines.Add(new Run(" 0.1%"));
            left.Children.Add(heroBig); left.Children.Add(heroSub);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
            recent.HorizontalAlignment = HorizontalAlignment.Right;
            heroDelta.HorizontalAlignment = HorizontalAlignment.Right; heroDelta.Margin = new Thickness(0, 6, 0, 0);
            right.Children.Add(recent); right.Children.Add(heroDelta);
            Grid.SetColumn(right, 1);
            g.Children.Add(left); g.Children.Add(right);
            heroTile.Child = g;
        }
        /// <summary>The hero's numbers: FPS and the two lows, and the bars of the runs before this one in the same setup
        /// (the same game, mode, resolution and power source) with what changed. r is the run on show; live, setup is the
        /// running one (null before it has a game) and note says what the live number is.</summary>
        void Hero(string fps, string low1, string low01, BenchRun r, BenchRun setup, double liveFps, Color mode, string note) {
            heroFps.Text = fps; heroLow.Text = low1; heroLow01.Text = low01; heroFps.Foreground = Ui.TextB;
            heroLow.Foreground = Ui.Brush(mode); heroLow01.Foreground = Ui.TextB;
            BenchRun key = r ?? setup;
            var before = new List<double>(Earlier(key));
            BenchRun prev = r != null ? Previous(r) : null;
            if (r != null) before.Add(r.AvgFps);
            else if (liveFps > 0) before.Add(liveFps);
            recent.Set(before, r != null || liveFps > 0, r != null ? Ui.ModeColor(r.ModeIndex) : mode);
            string text, rest = ""; Color c = Ink.Ref; bool own = false;
            if (r == null) { text = note; }
            else if (prev != null) { double d = r.AvgFps - prev.AvgFps; text = Ink.Delta(d) + " fps"; rest = " vs " + prev.Day; c = Ink.DeltaColor(d); own = true; }
            else {
                bool modeBefore = false;
                foreach (var o in Runs()) if (o.Id != r.Id && string.Equals(o.Exe, r.Exe, StringComparison.OrdinalIgnoreCase) && o.ModeIndex == r.ModeIndex && o.Started < r.Started) { modeBefore = true; break; }
                text = modeBefore ? "first in this setup" : "first " + Engine.ModeNames[r.ModeIndex] + " run";
                c = Ui.Col("#EDEAE8"); own = true;
            }
            string k = text + "|" + rest + "|" + c;
            if (k == heroDeltaKey) return;                   // Render runs every second; the line only changes with the run
            heroDeltaKey = k;
            heroDelta.Inlines.Clear();
            heroDelta.Inlines.Add(new Run(text) { Foreground = own ? Ui.Brush(c) : Ui.Foot });
            if (rest.Length > 0) heroDelta.Inlines.Add(new Run(rest) { Foreground = Ui.Foot });
        }
        string heroDeltaKey = "";
        // the earlier runs of one setup, oldest first, kept until the run on show or the list of runs changes
        List<double> earlier = new List<double>();
        string earlierFor = "";
        List<BenchRun> earlierFrom;
        List<double> Earlier(BenchRun key) {
            var all = Runs();
            string id = key == null ? "" : key.Id + "|" + key.Exe + "|" + key.ModeIndex + "|" + key.ResW + "x" + key.ResH + "|" + key.OnAc;
            if (id == earlierFor && ReferenceEquals(all, earlierFrom)) return earlier;
            earlierFor = id; earlierFrom = all;
            earlier = new List<double>();
            if (key != null)
                for (int i = all.Count - 1; i >= 0; i--) { var o = all[i]; if (o.Id != key.Id && o.Started < key.Started && key.SameSetup(o)) earlier.Add(o.AvgFps); }
            return earlier;
        }
        /// <summary>The running benchmark as a run that has not been saved yet: enough for SameSetup, started now.</summary>
        static BenchRun LiveSetup(BenchView v) {
            if (string.IsNullOrEmpty(v.Exe)) return null;
            return new BenchRun { Id = "live", Exe = v.Exe, ModeIndex = v.Mode, ResW = v.ResW, ResH = v.ResH, OnAc = v.OnAc, Started = DateTime.MaxValue };
        }
        string graphNote = "";
        void FtLine(string a, string b) { ftAs = a; ftBs = b; }
        string underKey = "";
        /// <summary>The line under the graph. On FPS it is the run's frame time; on the others, the graph's own note
        /// (peak, how fast it is settling, the other fan), so the header can stay one name and one number.</summary>
        void UnderLine(int g0) {
            string k = g0 + "|" + graphTag + "|" + ftAs + "|" + ftBs + "|" + subAv.Text + "|" + subAt.Text + "|" + graphNote + "|" + subAv.Foreground;
            if (k == underKey) return;                       // the same line as a second ago: nothing to lay out again
            underKey = k;
            ftLine.Inlines.Clear();
            if (graphTag.Length > 0) ftLine.Inlines.Add(new Run(graphTag + "  ") { Foreground = Ui.Accent });
            if (g0 == 0 && ftAs.Length > 0) {
                ftLine.Inlines.Add(new Run("frame time "));
                ftLine.Inlines.Add(new Run(ftAs) { Foreground = Ui.TextB });
                ftLine.Inlines.Add(new Run(ftBs));
                return;
            }
            if (Str(subAv.Text).Length > 0) ftLine.Inlines.Add(new Run(subAv.Text + " ") { Foreground = subAv.Foreground });
            if (Str(subAt.Text).Length > 0) ftLine.Inlines.Add(new Run(subAt.Text));
            if (graphNote.Length > 0) ftLine.Inlines.Add(new Run((Str(subAv.Text).Length + Str(subAt.Text).Length > 0 ? " · " : "") + graphNote));
        }
        static string Str(string s) { return s ?? ""; }      // an Avalonia Run starts with no text at all, a WPF one with ""
        Border Arrow(string d, int dir, string tip) {
            var p = Ink.Icon(d, 11, 2.6, Ui.SegText);
            var b = new Border { Width = 28, Height = 26, CornerRadius = new CornerRadius(7), Background = Ui.Pill, Child = p, Cursor = Cursors.Hand, Margin = new Thickness(dir < 0 ? 8 : 4, 0, 0, 0) };
            Ui.Tip(b, tip);
            b.PointerEntered += delegate { b.Background = Ui.Brush("#28231F"); };
            b.PointerExited += delegate { b.Background = Ui.Pill; };
            Ui.LeftUp(b, delegate { Flip(dir); });
            return b;
        }
        TextBlock waitTitle, waitHint;                     // Linux: what they say follows the frame source (see Render)
        void BuildWait() {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pad = Ink.Icon(IcoPad, 30, 1.5, Ui.Accent); pad.Margin = new Thickness(0, 0, 14, 0);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Ink.Sans(14, Ui.TextB)); ((TextBlock)col.Children[0]).Text = FrameCapture.WaitTitle;
            var l2 = Ink.Sans(12.5, Ui.Sub); l2.Text = FrameCapture.WaitHint; l2.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(l2);
            sp.Children.Add(pad); sp.Children.Add(col);
            waitTile.Child = sp;
            waitTitle = (TextBlock)col.Children[0]; waitHint = l2;
            waitTile.Background = Brushes.Transparent;
            Ui.LeftUp(waitTile, delegate { if (Str(B.Snapshot().Note).Length > 0) CopyLaunch(); });
        }
        /// <summary>Linux: the launch option that loads MangoHud with a game, onto the clipboard.</summary>
        void CopyLaunch() {
            try {
                var top = TopLevel.GetTopLevel(this);
                if (top == null || top.Clipboard == null) throw new InvalidOperationException("no clipboard");
                top.Clipboard.SetTextAsync(FrameCapture.LaunchOption);
                var h = Toast; if (h != null) h("Copied: paste it into the game's launch options", false);
            } catch (Exception ex) { var h = Toast; if (h != null) h("Could not copy: " + ex.Message, true); }
        }

        // ---------- the Runs view ----------
        readonly TextBlock runsTotal = Ink.Mono(11, Ui.Status);
        readonly WrapPanel gameLinks = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        readonly TextBlock bestBig = new BlockText(), lastBig = new BlockText();
        readonly TextBlock bestSub = Ink.Sans(12.5, Ui.Sub), lastSub = Ink.Sans(12.5, Ui.Sub);
        readonly RunBars runBars = new RunBars();
        readonly StackPanel runRows = new StackPanel();
        void BuildRuns() {
            runsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            runsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            runsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            runsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            runsView.RowDefinitions.Add(new RowDefinition());
            runsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hdr = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var back = new Border { Width = 24, Height = 24, Margin = new Thickness(-6, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = Ink.Icon("M15 5 L8 12 L15 19", 12, 2.4, Ui.SegText) };
            Ui.Tip(back, "Back");
            // on the press, and handled: the header is a drag strip, and DragMove would swallow the release
            back.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (!e.GetCurrentPoint(back).Properties.IsLeftButtonPressed) return; e.Handled = true; showRuns = false; Render(); };
            var t = new WpfTextBlock { Text = "Runs", FontFamily = Ui.UiFont, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(back); left.Children.Add(t);
            runsTotal.HorizontalAlignment = HorizontalAlignment.Right;
            hdr.Children.Add(left); hdr.Children.Add(runsTotal);
            hdr.Background = Brushes.Transparent;
            runsHead = hdr;
            runsView.Children.Add(hdr);
            Grid.SetRow(gameLinks, 1); runsView.Children.Add(gameLinks);
            var tiles = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            tiles.ColumnDefinitions.Add(new ColumnDefinition()); tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false }; Grid.SetColumnSpan(line, 2); Grid.SetRowSpan(line, 2);
            tiles.Children.Add(BigTile("BEST", bestBig, bestSub, true, 0));
            tiles.Children.Add(BigTile("LAST", lastBig, lastSub, false, 1));
            var bars = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 10, 0, 8), Child = runBars };
            Grid.SetRow(bars, 1); Grid.SetColumnSpan(bars, 2);
            tiles.Children.Add(bars); tiles.Children.Add(line);
            Grid.SetRow(tiles, 2); runsView.Children.Add(tiles);
            runsView.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
            runsView.RowDefinitions[4].Height = new GridLength(18);
            var rowScroll = new ScrollViewer { Content = runRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(rowScroll, 3); runsView.Children.Add(rowScroll);
            var fb = new Border { BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
            var fg = new Grid();
            var key = Ink.Mono(11, Ui.Foot); key.Text = "fps avg / 1% low";
            var export = Ink.Mono(11, Ui.Accent); export.Text = "Export CSV"; export.Cursor = Cursors.Hand; export.HorizontalAlignment = HorizontalAlignment.Right; export.Background = Brushes.Transparent;
            Ui.Tip(export, "Every run as a spreadsheet, in Documents/Ohman");
            Ui.LeftUp(export, delegate { ExportCsv(); });
            fg.Children.Add(key); fg.Children.Add(export);
            fb.Child = fg;
            Grid.SetRow(fb, 5); runsView.Children.Add(fb);
        }
        Control runsHead;
        public Control RunsHead { get { return runsHead; } }
        static Border BigTile(string label, TextBlock big, TextBlock sub, bool left, int col) {
            var sp = new StackPanel();
            big.FontFamily = Ui.UiFont; big.FontSize = 42; big.FontWeight = FontWeight.SemiBold; big.Foreground = Ui.TextB; big.LineHeight = 46;
            sp.Children.Add(big);
            sub.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(sub);
            var b = new Border { Child = sp, BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, left ? 1 : 0, 1), Padding = left ? new Thickness(0, 14, 16, 16) : new Thickness(16, 14, 0, 16) };
            Grid.SetColumn(b, col);
            return b;
        }

        // ---------- actions ----------
        void OnMain() {
            CommitSettings();
            if (B.Snapshot().Phase != BenchPhase.Idle) return;
            string what = main.Tag as string;
            if (what == "fetch") { if (FrameCapture.CanFetch) B.Fetch(); else CopyLaunch(); }
            else if (what == "share") { shareOpen = true; shareGraph = Cur(); Render(); }
            else if (what == "start") { var h = StartClicked; if (h != null) h(); }
        }
        void OnSide() {
            CommitSettings();
            if (B.Snapshot().Phase != BenchPhase.Idle) return;
            string what = side.Tag as string;
            if (what == "again") { var h = StartClicked; if (h != null) h(); }
            else if (what == "back") { shown = null; showRuns = true; Render(); }
            else { showRuns = true; runsGame = null; Render(); }
        }
        bool IsLatest(BenchRun r) { var l = LastRun(); return r != null && l != null && r.Id == l.Id; }

        void EditSettings() {
            if (shown == null) return;
            settingsBox.Text = shown.Settings;
            settingsBtn.IsVisible = false;
            settingsBox.IsVisible = true;
            settingsBox.Focus();
            settingsBox.SelectAll();
        }
        void CommitSettings() {
            if (!settingsBox.IsVisible) return;
            settingsBox.IsVisible = false;
            settingsBtn.IsVisible = true;
            if (shown == null) return;
            string t = (settingsBox.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (t == shown.Settings) return;
            shown.Settings = t;
            var full = RunStore.Full(shown);
            full.Settings = t;
            if (full.HasSamples) { try { RunStore.Save(full); } catch (Exception ex) { Log.Write("run settings: " + ex.Message); } }
            else Log.Write("run settings: " + shown.Id + " could not be read in full; the file is left as it was");
            E.S.SetGameLine(shown.Exe, t);
            try { E.S.Save(); } catch { }
            runs = null;
            Render();
        }

        // ---------- the Share view: the card as it will be, its layout, its graph, copy or save ----------
        bool shareOpen;
        int shareGraph;
        string shareKey = "";
        Bitmap shareBmp;
        readonly Image sharePreview = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock shareSize = Ink.Mono(11, Ui.Status), shareGraphName = Ink.Sans(13, Ui.TextB);
        LinkSeg shareLayout;
        Control shareGraphRow, shareHead;
        readonly ActionButton copyBtn = new ActionButton(), saveBtn = new ActionButton();
        public Control ShareHead { get { return shareHead; } }

        void BuildShare() {
            for (int i = 0; i < 5; i++) shareView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shareView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            shareView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // header: back, title, the size it will be
            var hdr = new Grid { Background = Brushes.Transparent };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var back = new Border { Width = 24, Height = 24, Margin = new Thickness(-6, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = Ink.Icon("M15 5 L8 12 L15 19", 12, 2.4, Ui.SegText) };
            Ui.Tip(back, "Back");
            back.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (!e.GetCurrentPoint(back).Properties.IsLeftButtonPressed) return; e.Handled = true; shareOpen = false; Render(); };
            var t = new WpfTextBlock { Text = "Share", FontFamily = Ui.UiFont, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(back); left.Children.Add(t);
            shareSize.HorizontalAlignment = HorizontalAlignment.Right;
            hdr.Children.Add(left); hdr.Children.Add(shareSize);
            shareHead = hdr;
            shareView.Children.Add(hdr);
            // the card, drawn by the same code that saves it
            var well = new Border { Background = Ui.Sunken, CornerRadius = new CornerRadius(10), Height = 330, Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14),
                Child = new Border { BorderBrush = Ui.Brush("#34302C"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = sharePreview } };   // the edge shows the card's shape
            Grid.SetRow(well, 1); shareView.Children.Add(well);
            // Layout, the way Home says Fans: a word on the left, the choices on the right
            var lr = new Grid { Margin = new Thickness(0, 22, 0, 0) };
            var ll = Ink.Sans(14, Ui.TextB); ll.Text = "Layout";
            shareLayout = new LinkSeg(Card.Layouts, 18, 13, 3, new[] { "The numbers and one graph, 1080 x 1350", "All eight graphs, 1080 x 1350", "The numbers and one graph, 1080 x 1080" });
            shareLayout.HorizontalAlignment = HorizontalAlignment.Right; shareLayout.VerticalAlignment = VerticalAlignment.Center;
            shareLayout.Picked += delegate(int i) { E.S.ShareLayout = i; try { E.S.Save(); } catch { } Render(); };
            lr.Children.Add(ll); lr.Children.Add(shareLayout);
            Grid.SetRow(lr, 2); shareView.Children.Add(lr);
            // Graph: which of the eight goes on the card (Graphs has all of them)
            var gr = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            var gl = Ink.Sans(14, Ui.TextB); gl.Text = "Graph";
            var gp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            shareGraphName.Margin = new Thickness(0, 0, 4, 0);
            gp.Children.Add(shareGraphName);
            gp.Children.Add(ShareArrow("M15 5 L8 12 L15 19", -1));
            gp.Children.Add(ShareArrow("M9 5 L16 12 L9 19", 1));
            gr.Children.Add(gl); gr.Children.Add(gp);
            shareGraphRow = gr;
            Grid.SetRow(gr, 3); shareView.Children.Add(gr);
            // Copy and Save, the action row every state uses
            var bot = new Grid();
            bot.ColumnDefinitions.Add(new ColumnDefinition()); bot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            saveBtn.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(saveBtn, 1); saveBtn.Centered = true;
            bot.Children.Add(copyBtn); bot.Children.Add(saveBtn);
            copyBtn.Click += delegate { CopyCard(); };
            saveBtn.Click += delegate { SaveCard(); };
            Grid.SetRow(bot, 6); shareView.Children.Add(bot);
        }
        Border ShareArrow(string d, int dir) {
            var p = Ink.Icon(d, 11, 2.6, Ui.SegText);
            var b = new Border { Width = 28, Height = 26, CornerRadius = new CornerRadius(7), Background = Ui.Pill, Child = p, Cursor = Cursors.Hand, Margin = new Thickness(dir < 0 ? 8 : 4, 0, 0, 0) };
            b.PointerEntered += delegate { b.Background = Ui.Brush("#28231F"); };
            b.PointerExited += delegate { b.Background = Ui.Pill; };
            Ui.LeftUp(b, delegate { int n = RunGraph.Names.Length; shareGraph = ((shareGraph + dir) % n + n) % n; Render(); });
            return b;
        }
        void RenderShare() {
            IBrush acc = Ui.Accent;
            int layout = Math.Max(0, Math.Min(2, E.S.ShareLayout));
            shareLayout.Select(layout, Seen);
            shareGraphRow.IsVisible = layout != 1;
            shareGraphName.Text = RunGraph.Names[shareGraph];
            Size sz = Card.SizeOf(layout);
            shareSize.Text = sz.Width + " × " + sz.Height + " PNG";
            copyBtn.Set("Copy", "to the clipboard", null, false, acc, true, acc);
            saveBtn.Set("Save", null, null, false, acc, false, acc);
            string key = shown.Id + "|" + layout + "|" + shareGraph + "|" + shown.Settings;
            if (key == shareKey && shareBmp != null) return;
            try {
                var full = RunStore.Full(shown);
                shareBmp = Card.Render(full, Previous(full), layout, shareGraph);
                sharePreview.Source = shareBmp;
                shareKey = key;
            } catch (Exception ex) { Log.Write("share preview: " + ex); shareBmp = null; sharePreview.Source = null; shareKey = ""; }
        }
        static byte[] Png(Bitmap b) { var ms = new MemoryStream(); b.Save(ms); return ms.ToArray(); }
        void CopyCard() {
            if (shareBmp == null) return;
            try {
                var top = TopLevel.GetTopLevel(this);
                if (top == null || top.Clipboard == null) throw new InvalidOperationException("no clipboard");
                var data = new DataObject();
                data.Set("image/png", Png(shareBmp));
                top.Clipboard.SetDataObjectAsync(data);
                var h = Toast; if (h != null) h("Copied: paste it anywhere", false);
            }
            catch (Exception ex) { Log.Write("share: clipboard " + ex.Message); var h = Toast; if (h != null) h("Could not copy: " + ex.Message, true); }
        }
        void SaveCard() {
            if (shareBmp == null || shown == null) return;
            try {
                string dir = System.IO.Path.Combine(UserFolder(Environment.SpecialFolder.MyPictures), Program.AppName);
                string path = SafeFile.WriteNew(dir, Clean(shown.Game) + " " + shown.Local.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture), ".png", Png(shareBmp));
                Log.Write("share card saved: " + Support.Scrub(path));
                Reveal(dir);
                var h = Toast; if (h != null) h("Saved to " + Short(dir), false);
            } catch (Exception ex) { Log.Write("share: " + ex); var h = Toast; if (h != null) h("Could not save: " + ex.Message, true); }
        }
        /// <summary>Show the folder a file was saved in, in the desktop's own file manager.</summary>
        static void Reveal(string dir) {
            try {
                string tool = Host.Find("xdg-open");
                if (tool == null) return;
                var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(dir);
                using (Process.Start(psi)) { }
            } catch { }
        }
        /// <summary>A known folder as a full path; an empty answer would otherwise turn into a folder next to wherever
        /// Ohman was started from. Not verified: a home that has no Pictures or Documents folder yet (nothing made
        /// one) would get an empty answer, and the folder is made on the way to writing the file anyway.</summary>
        static string UserFolder(Environment.SpecialFolder f) {
            string d = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(d) || !System.IO.Path.IsPathRooted(d)) throw new IOException("the system did not say where that folder is");
            return d;
        }
        /// <summary>The last two parts of a folder, for a toast: "Pictures/Ohman".</summary>
        static string Short(string dir) {
            var parts = dir.TrimEnd('/').Split('/');
            return parts.Length >= 2 ? parts[parts.Length - 2] + "/" + parts[parts.Length - 1] : dir;
        }
        static string Clean(string s) { foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, ' '); return s.Trim().Length == 0 ? "run" : s.Trim(); }

        void ExportCsv() {
            try {
                var all = Runs();
                string dir = System.IO.Path.Combine(UserFolder(Environment.SpecialFolder.MyDocuments), Program.AppName);
                var enc = new System.Text.UTF8Encoding(true);
                byte[] pre = enc.GetPreamble(), body = enc.GetBytes(RunStore.Csv(all)), data = new byte[pre.Length + body.Length];
                Buffer.BlockCopy(pre, 0, data, 0, pre.Length); Buffer.BlockCopy(body, 0, data, pre.Length, body.Length);
                string path = SafeFile.WriteNew(dir, "runs " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture), ".csv", data);
                Reveal(dir);
                var h = Toast; if (h != null) h(all.Count + " runs saved to " + Short(dir), false);
            } catch (Exception ex) { var h = Toast; if (h != null) h("Export failed: " + ex.Message, true); }
        }

        // ---------- data ----------
        List<BenchRun> Runs() { if (runs == null) runs = RunStore.List(); return runs; }
        BenchRun Previous(BenchRun r) {
            foreach (var o in Runs()) if (o.Id != r.Id && o.Started < r.Started && r.SameSetup(o)) return o;
            return null;
        }
        BenchRun LastRun() {
            if (lastRun == null) { var all = Runs(); if (all.Count > 0) lastRun = all[0]; }
            return lastRun;
        }

        int Cur() { return gi < 0 ? 0 : gi; }

        // ---------- rendering: one method, every state ----------
        public void Render() {
            if (!Seen) return;
            var v = B.Snapshot();
            bool idle = v.Phase == BenchPhase.Idle || v.Phase == BenchPhase.Fetching;
            if (!idle) { CommitSettings(); showRuns = false; shown = null; note = ""; }
            if (shown == null || showRuns) shareOpen = false;
            shareView.IsVisible = shareOpen;
            if (shareOpen) { dash.IsVisible = false; runsView.IsVisible = false; RenderShare(); Shaped(E.S.ShareLayout == 1 ? "share-all" : "share"); return; }
            dash.IsVisible = !showRuns;
            runsView.IsVisible = showRuns;
            if (showRuns) { RenderRuns(); Shaped("runs" + runRows.Children.Count); return; }
            IBrush acc = Ui.Accent;
            string state = !idle ? v.Phase.ToString() : shown != null ? "Result" : !B.ToolReady ? "First" : "Ready";
            if (state != giFor) { giFor = state; gi = state == "Result" || state == "Measuring" || state == "Finishing" || (state == "Ready" && LastRun() != null) ? 0 : 1; }
            for (int i = 0; i < dots.Children.Count; i++) ((Ellipse)dots.Children[i]).Fill = i == gi ? acc : Ui.Brush(Ink.Dot);

            // header
            if (state == "Result") { title.Text = shown.Game; Status(Engine.ModeNames[shown.ModeIndex] + (shown.Res.Length > 0 ? " · " + shown.Res + (shown.Hz > 0 ? " " + shown.Hz + " Hz" : "") : ""), Ui.ModeColor(shown.ModeIndex)); }
            else if (v.Phase == BenchPhase.Waiting) { title.Text = "Benchmark"; Status("waiting for a game", Ui.Accent.Color); }
            else if (!idle) {
                title.Text = v.Game;
                if (v.Paused) Status("paused", Ink.Warn);
                else if (v.Phase == BenchPhase.Warming) Status("warming up " + Ink.Clock(v.WarmSec), Ui.Accent.Color);
                else if (v.Phase == BenchPhase.Countdown) Status("measuring in " + Math.Max(1, Math.Ceiling(v.CountLeft)), Ui.Accent.Color);
                else Status("measuring · " + Ink.Clock(v.MeasureTarget - v.MeasuredSec) + " left", Ui.Accent.Color);
            } else {
                title.Text = "Benchmark";
                var lr = LastRun();
                if (note.Length > 0) Status("ended · " + note, Ink.Warn);
                else Status(lr == null ? "no runs yet" : "last · " + lr.Game, Color.FromArgb(0, 0, 0, 0));
            }

            // strip
            string wv = Bench.WarmNames[v.WarmChoice], mv = Bench.MeasureNames[v.MeasureChoice];
            if (idle && state != "Result") {
                steps[0].Set("warm up", wv, 0, "", true, acc);
                steps[1].Set("measure", mv, 0, "", true, acc);
                steps[2].Set("card", "", 0, "dim", false, acc);
            } else if (state == "Result") {
                steps[0].Set("warmed", Ink.Clock(shown.WarmSec), 1, "dim", false, acc);
                steps[1].Set(shown.Ended.Length > 0 ? "measured" : "measured", Ink.Clock(shown.MeasuredSec), 1, "dim", false, acc);
                steps[2].Set("card", "ready", 1, "on", false, acc);
            } else if (v.Phase == BenchPhase.Waiting) {
                steps[0].Set("warm up", "waiting", 0, "on", false, acc);
                steps[1].Set("measure", mv, 0, "", false, acc);
                steps[2].Set("card", "", 0, "dim", false, acc);
            } else if (v.Phase == BenchPhase.Warming || v.Phase == BenchPhase.Countdown) {
                bool cd = v.Phase == BenchPhase.Countdown;
                double cap = v.WarmChoice == 1 ? 120 : 300;
                steps[0].Set(cd ? "warmed" : v.Paused ? "paused" : "warm up", Ink.Clock(v.WarmSec), cd ? 1 : v.WarmSec / cap, v.Paused && !cd ? "warn" : cd ? "dim" : "on", false, acc);
                steps[1].Set("measure", cd ? "in " + Math.Max(1, Math.Ceiling(v.CountLeft)) : mv, 0, cd && v.Paused ? "warn" : "", false, acc);
                steps[2].Set("card", "", 0, "dim", false, acc);
            } else {
                steps[0].Set("warmed", Ink.Clock(v.WarmSec), 1, "dim", false, acc);
                steps[1].Set(v.Paused ? "paused" : "measuring", Ink.Clock(v.MeasuredSec), v.MeasuredSec / Math.Max(1, v.MeasureTarget), v.Paused ? "warn" : "on", false, acc);
                steps[2].Set("card", v.Phase == BenchPhase.Finishing ? "making" : "", 0, "dim", false, acc);
            }

            // meta (result only)
            meta.IsVisible = state == "Result";
            if (state == "Result") {
                if (!settingsBox.IsVisible) {
                    settingsText.Text = shown.Settings.Length > 0 ? shown.Settings : "Add the game's settings";
                    settingsText.Foreground = shown.Settings.Length > 0 ? Ui.SegText : Ui.Foot;
                }
            }

            // board
            bool waiting = v.Phase == BenchPhase.Waiting;
            chartTile.IsVisible = !waiting;
            waitTile.IsVisible = waiting;
            if (waiting) {
                // Linux: the frame source says what it is waiting for; with nothing to say the tile reads as on Windows
                string said = Str(v.Note);
                waitTitle.Text = said.Length > 0 ? said : FrameCapture.WaitTitle;
                waitHint.Text = said.Length > 0 ? "Start the game with MangoHud: click for the launch option" : FrameCapture.WaitHint;
                waitTile.Cursor = said.Length > 0 ? Cursors.Hand : Cursors.Arrow;
                Ui.Tip(waitTile, said.Length > 0 ? FrameCapture.LaunchOption : null);
            }
            BenchRun data = state == "Result" ? RunStore.Full(shown) : state == "Ready" ? RunStore.Full(LastRun()) : null;
            if (state == "Result") shown = data;
            if (state == "Ready" && data != null) lastRun = data;
            board.Opacity = state == "Ready" && data != null ? 0.55 : 1;
            if (data != null) RunBoard(data); else LiveBoard(v);

            // bottom
            RenderBottom(state, v);
            SyncLinks();
            Shaped(state + (meta.IsVisible ? "+meta" : "") + (waiting ? "+wait" : ""));
        }

        void Status(string t, Color dot) {
            status.Text = t;
            status.Foreground = dot == Ink.Warn ? Ui.Brush(Ink.WarnText) : Ui.Status;
            statusDot.IsVisible = dot.A != 0;
            statusDot.Fill = Ui.Brush(dot);
        }

        void RenderBottom(string state, BenchView v) {
            bool buttons = state == "First" || state == "Ready" || state == "Result" || state == "Fetching";
            bottom.IsVisible = buttons;
            footerBox.IsVisible = !buttons;
            linksWant.Clear();
            IBrush acc = Ui.Accent;
            if (state == "First" || state == "Fetching") {
                // Linux: nothing to download. MangoHud comes from the distribution and goes into the game by a launch
                // option, so the button hands that over instead (OnMain: "fetch" copies it when nothing can be fetched).
                Grid.SetColumnSpan(main, 2); side.IsVisible = false;
                main.Set("Copy launch option", "", IcoCopy, false, acc, false, acc);
                Ui.Tip(main, "Frame times come from MangoHud, the open-source overlay (MIT). Install it from your distribution, then start the game with: " + FrameCapture.LaunchOption);
                main.Enabled = true;
                main.Warn(v.Error.Length > 0 ? (v.Error.Length > 34 ? v.Error.Substring(0, 34) + "…" : v.Error) : FrameCapture.Missing);
                main.Tag = "fetch";
                return;
            }
            Grid.SetColumnSpan(main, 1); side.IsVisible = true; main.Enabled = true; Ui.Tip(main, null);
            if (state == "Ready") {
                Hotkey hk = StopHotkey();
                main.Set("Start", !hk.IsEmpty ? hk.ToString() : "", IcoPlay, true, acc, false, acc);
                if (v.Error.Length > 0) main.Warn(v.Error.Length > 34 ? v.Error.Substring(0, 34) + "…" : v.Error);
                main.Tag = "start";
                int n = Runs().Count;
                side.Set(n.ToString(CultureInfo.InvariantCulture), null, IcoList, false, Ui.SegText, false, acc);
                Ui.Tip(side, "Past runs");
                side.Enabled = n > 0;
                side.Tag = "runs";
                return;
            }
            if (state == "Result") {
                main.Set("Share card", "pick a layout", null, false, acc, true, acc);
                main.Tag = "share";
                bool latest = IsLatest(shown);
                side.Set(latest ? "Run again" : "Back", null, null, false, acc, false, acc);
                Ui.Tip(side, null);
                side.Enabled = true;
                side.Tag = latest ? "again" : "back";
                return;
            }
            // running: a line and links
            if (v.Phase == BenchPhase.Waiting) {
                Hotkey hk = StopHotkey();
                footL.Text = !hk.IsEmpty ? hk + " in the game stops it" : "Ohman stays in the tray meanwhile";
                if (Str(v.Note).Length > 0) Link("Launch option", delegate { CopyLaunch(); });      // Linux: see the wait tile
                Link("Cancel", delegate { B.Stop(); });
            } else if (v.Phase == BenchPhase.Warming) {
                footL.Text = v.WarmChoice == 0 ? "until temperatures settle · cap 5:00" : "counts only while the game is in front";
                Link("Skip", delegate { B.SkipWarm(); });
                Link("Stop", delegate { B.Stop(); });
            } else if (v.Phase == BenchPhase.Countdown) {
                footL.Text = "hands on the game";
                Link("Stop", delegate { B.Stop(); });
            } else {
                footL.Text = v.Paused ? Ink.Clock(v.MeasuredSec) + " of " + Ink.Clock(v.MeasureTarget) + " · resumes in the game" : v.Frames.ToString("N0", CultureInfo.InvariantCulture) + " frames so far";
                if (v.Phase == BenchPhase.Measuring) Link("Stop", delegate { B.Stop(); });
            }
        }
        // The footer's links, rebuilt only when the set changes: the page renders every second, and a link replaced
        // under the pointer loses its hover and its click.
        readonly List<string> linksWant = new List<string>();
        string linksHave = "";
        readonly Dictionary<string, Action> linkActions = new Dictionary<string, Action>();
        void Link(string t, Action a) { linksWant.Add(t); linkActions[t] = a; }
        void SyncLinks() {
            string key = string.Join("|", linksWant.ToArray());
            if (key == linksHave) return;
            linksHave = key;
            footLinks.Children.Clear();
            foreach (string name in linksWant) {
                string n = name;
                var l = Ink.Mono(11, Ui.Accent); l.Text = n; l.Cursor = Cursors.Hand; l.Margin = new Thickness(18, 0, 0, 0); l.Background = Brushes.Transparent;
                Ui.LeftUp(l, delegate { Action act; if (linkActions.TryGetValue(n, out act)) act(); });
                footLinks.Children.Add(l);
            }
        }

        // ---------- the board from a stored run ----------
        void RunBoard(BenchRun r) {
            var s = r.Samples;
            int n = s.Count;
            Func<Func<BenchSample, double>, double[]> col = delegate(Func<BenchSample, double> g) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = g(s[i]); return a; };
            int g0 = Cur();
            chartLabel.Text = GraphNames[g0]; graphTag = "";
            chart.Slots = Math.Max(2, n); chart.Ref = double.NaN; chart.Marks.Clear(); chart.Lo = chart.Hi = double.NaN;
            int rpl = r.RpmPerLevel;
            switch (g0) {
                case 0:
                    Graph(col(b => b.Fps), Ui.ModeColor(r.ModeIndex), 10, Ink.F0(r.AvgFps), "", r.Low1, double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1), "1% low",
                        double.IsNaN(r.Low01) ? "0.1% low —" : Ink.F0(r.Low01) + " 0.1% low");
                    chart.Ref = r.Low1; subAv.Foreground = Ui.Brush(Ui.ModeColor(r.ModeIndex)); break;
                case 1: Graph(col(b => b.CpuT), Ink.Cpu, 6, Ink.F0(r.CpuTAvg), "°C", r.CpuTMax, Ink.F0(r.CpuTMax), "peak", Ink.F0(r.CpuTEnd) + " at the end"); chart.Ref = r.CpuTMax; break;
                case 2: Graph(col(b => b.GpuT), Ink.Gpu, 6, Ink.F0(r.GpuTAvg), "°C", r.GpuTMax, Ink.F0(r.GpuTMax), "peak", Ink.F0(r.GpuTEnd) + " at the end"); chart.Ref = r.GpuTMax; break;
                case 3: Graph(col(b => b.CpuW), Ink.Cpu, 8, Ink.F0(r.CpuWAvg), " W", double.NaN, Ink.F0(r.CpuWMax), "peak", ""); break;
                case 4: Graph(col(b => b.GpuW), Ink.Gpu, 8, Ink.F0(r.GpuWAvg), " W", double.NaN, Ink.F0(r.GpuWMax), "peak", double.IsNaN(r.PowerLimitedPct) ? "" : "power limit " + Ink.F0(r.PowerLimitedPct) + "%"); break;
                case 5: Graph(col(b => b.CpuMhz / 1000), Ink.Cpu, 0.4, Ink.F1(r.CpuMhzAvg / 1000), " GHz", double.NaN, Ink.F1(r.CpuMhzMax / 1000), "peak", ""); break;
                case 6: Graph(col(b => b.GpuLoad), Ink.Gpu, 10, Ink.F0(r.GpuLoadAvg), "%", double.NaN, Ink.F1(r.GpuMhzAvg / 1000), "GHz", ""); break;
                default: {
                    var fx = RunGraph.Of(r, 7);
                    Func<double, string> fv = delegate(double lv) { double x = RunStore.FanValue(r, lv); return rpl > 0 ? Ink.K(x) : Ink.F0(x); };
                    Graph(fx.Values, Ink.Fan, fx.MinSpan, rpl > 0 ? Ink.K(RunStore.FanValue(r, Math.Max(Nz(r.Fan1Avg), Nz(r.Fan2Avg)))) : fx.Big, rpl > 0 ? "" : "%", double.NaN,
                        fv(r.Fan1Avg), "CPU fan", fv(r.Fan2Avg) + " GPU fan");
                    break;
                }
            }
            if (g0 != 0) subAv.Foreground = Ui.TextB;
            chart.Repaint();
            // frame time
            FtLine(Ink.F1(r.FtP50) + " ms", " · p99 " + Ink.F1(r.FtP99) + " · stutter " + Ink.F1(r.StutterPct) + "%");
            Hero(Ink.F0(r.AvgFps), double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1), double.IsNaN(r.Low01) ? "—" : Ink.F0(r.Low01), r, null, double.NaN, Ui.ModeColor(r.ModeIndex), "");
            UnderLine(g0);
            Tiles(r.CpuTAvg, r.CpuTMax, r.CpuWAvg, r.CpuMhzAvg, r.GpuTAvg, r.GpuTMax, r.GpuWAvg, r.GpuMhzAvg, r.FpsPerWatt,
                Nz(r.Fan1Avg), Nz(r.Fan2Avg), true, r.RpmPerLevel, r.FanCeiling);
        }

        void Graph(double[] values, Color c, double minSpan, string big, string unit, double refv, string sa, string sat, string s2) {
            chart.Values = values; chart.Color = c; chart.MinSpan = minSpan;
            bigVal.Text = big; bigUnit.Text = unit; subAv.Text = sa; subAt.Text = sat; graphNote = (s2 ?? "").Trim();
        }

        // ---------- the board live: idle sensors, warm-up, measuring ----------
        void LiveBoard(BenchView v) {
            int g0 = Cur();
            chartLabel.Text = GraphNames[g0];
            graphTag = v.Phase == BenchPhase.Warming && g0 == 1 ? "settling" : v.Phase == BenchPhase.Idle || v.Phase == BenchPhase.Fetching || v.Phase == BenchPhase.Waiting ? "live" : "";
            chart.Marks.Clear(); chart.Ref = double.NaN; chart.Lo = chart.Hi = double.NaN; chart.Slots = 100;
            bool measuring = v.Phase == BenchPhase.Measuring || v.Phase == BenchPhase.Finishing;
            var live = v.Live ?? new BenchSample[0];
            var s = latest;
            int rpl = E.P.RpmPerLevel, ceil = E.P.Curve.Ceiling;
            Func<int, double> rpm = delegate(int lv) { return lv < 0 ? double.NaN : rpl > 0 ? lv * rpl : 100.0 * lv / Math.Max(1, ceil); };
            Func<Func<SensorSnapshot, double>, double[]> ringOf = delegate(Func<SensorSnapshot, double> g) { var a = new double[ring.Count]; for (int i = 0; i < ring.Count; i++) a[i] = g(ring[i]); return a; };
            Func<Func<BenchSample, double>, double[]> liveOf = delegate(Func<BenchSample, double> g) { var a = new double[live.Length]; for (int i = 0; i < live.Length; i++) a[i] = g(live[i]); return a; };
            Func<double[], double> peak = delegate(double[] a) { double m = double.NaN; foreach (double x in a) if (!double.IsNaN(x) && (double.IsNaN(m) || x > m)) m = x; return m; };
            Func<double[], double> lastOf = delegate(double[] a) { for (int i = a.Length - 1; i >= 0; i--) if (!double.IsNaN(a[i])) return a[i]; return double.NaN; };
            double[] vals;
            switch (g0) {
                case 0:
                    vals = measuring ? liveOf(b => b.Fps) : new double[0];
                    if (measuring) {
                        var so = SoFar();
                        Graph(vals, Ui.Accent.Color, 10, so == null ? Ink.F0(v.LiveFps) : Ink.F0(so.AvgFps), "", double.NaN,
                            so == null || double.IsNaN(so.Low1) ? "" : Ink.F0(so.Low1), so == null || double.IsNaN(so.Low1) ? "average so far" : "1% low", so == null || double.IsNaN(so.Low1) ? "" : "so far");
                        if (so != null) chart.Ref = so.Low1;
                    } else Graph(vals, Ui.Accent.Color, 10, v.Phase == BenchPhase.Warming || v.Phase == BenchPhase.Countdown ? Ink.F0(v.LiveFps) : "--", "", double.NaN,
                        "", v.Phase == BenchPhase.Warming || v.Phase == BenchPhase.Countdown ? "now, not counted" : "needs a game", "");
                    subAv.Foreground = Ui.Accent;
                    break;
                case 1:
                    if (v.Phase == BenchPhase.Warming || v.Phase == BenchPhase.Countdown) {
                        vals = v.WarmTemps ?? new double[0];
                        chart.Slots = Math.Max(100, vals.Length);
                        double sl = v.WarmSlope;
                        Graph(vals, Ink.Cpu, 6, Ink.F0(lastOf(vals)), "°C", double.NaN, double.IsNaN(sl) ? "" : sl.ToString("+0.0;−0.0", CultureInfo.InvariantCulture) + "°", double.IsNaN(sl) ? "settling" : "a minute",
                            double.IsNaN(sl) ? "" : Math.Abs(sl) < 1.0 ? "settled" : Math.Abs(sl) < 2.5 ? "nearly settled" : sl > 0 ? "still rising" : "still falling");
                    } else {
                        vals = measuring ? liveOf(b => b.CpuT) : ringOf(x => x.CpuTemp);
                        Graph(vals, Ink.Cpu, 6, Ink.F0(lastOf(vals)), "°C", double.NaN, Ink.F0(peak(vals)), "peak", s == null || double.IsNaN(s.CpuWatts) ? "" : Ink.F0(s.CpuWatts) + " W now");
                    }
                    break;
                case 2: vals = measuring ? liveOf(b => b.GpuT) : ringOf(x => x.GpuTemp); Graph(vals, Ink.Gpu, 6, Ink.F0(lastOf(vals)), "°C", double.NaN, Ink.F0(peak(vals)), "peak", ""); break;
                case 3: vals = measuring ? liveOf(b => b.CpuW) : ringOf(x => x.CpuWatts); Graph(vals, Ink.Cpu, 8, Ink.F0(lastOf(vals)), " W", double.NaN, Ink.F0(peak(vals)), "peak", s == null || double.IsNaN(s.Pl1) ? "" : "limit " + Ink.F0(s.Pl1) + " W"); break;
                case 4: vals = measuring ? liveOf(b => b.GpuW) : ringOf(x => x.GpuWatts); Graph(vals, Ink.Gpu, 8, Ink.F0(lastOf(vals)), " W", double.NaN, Ink.F0(peak(vals)), "peak", ""); break;
                case 5: vals = measuring ? liveOf(b => b.CpuMhz / 1000) : ringOf(x => x.CpuMhz / 1000); Graph(vals, Ink.Cpu, 0.4, Ink.F1(lastOf(vals)), " GHz", double.NaN, Ink.F1(peak(vals)), "peak", ""); break;
                case 6: vals = measuring ? liveOf(b => b.GpuLoad) : ringOf(x => x.GpuLoad); Graph(vals, Ink.Gpu, 10, Ink.F0(lastOf(vals)), "%", double.NaN, s == null ? "--" : Ink.F1(s.GpuMhz / 1000), "GHz", ""); break;
                default: {
                    if (measuring) vals = liveOf(b => rpm(Math.Max(b.Fan1, b.Fan2)));
                    else { vals = new double[fanRing.Count]; for (int i = 0; i < fanRing.Count; i++) vals[i] = rpm(Math.Max(fanRing[i][0], fanRing[i][1])); }
                    double fa = fans == null ? double.NaN : rpm(fans[0]), f2 = fans == null ? double.NaN : rpm(fans[1]);
                    Func<double, string> fk = delegate(double x) { return rpl > 0 ? Ink.K(x) : Ink.F0(x); };
                    Graph(vals, Ink.Fan, rpl > 0 ? 400 : 8, fk(lastOf(vals)), rpl > 0 ? "" : "%", double.NaN, fk(fa), "CPU fan", fk(f2) + " GPU fan");
                    break;
                }
            }
            if (measuring) chart.Slots = Math.Max(2, v.MeasureTarget);
            if (g0 != 0) subAv.Foreground = Ui.TextB;
            chart.Repaint();
            // frame time
            bool warming = v.Phase == BenchPhase.Warming || v.Phase == BenchPhase.Countdown;
            if (measuring) {
                var so = SoFar();
                if (so == null) FtLine("", "");
                else FtLine(Ink.F1(so.P50) + " ms", " · p99 " + Ink.F1(so.P99) + " · stutter " + Ink.F1(so.StutterPct) + "%");
                // under 30 frames there is no average yet: the number is the rate right now, grey like the warm-up's
                double avg = so == null ? v.LiveFps : so.AvgFps;
                Hero(Ink.F0(avg), so == null || double.IsNaN(so.Low1) ? "--" : Ink.F0(so.Low1), so == null || double.IsNaN(so.Low01) ? "--" : Ink.F0(so.Low01), null, LiveSetup(v),
                    so == null ? double.NaN : avg, Ui.Accent.Color, so == null ? "counting frames" : "so far");
            } else {
                soFar = null; soFarFt = null; soFarAt = 0;
                FtLine("", "");
                string note = warming ? "warming up, not counted" : v.Phase == BenchPhase.Waiting ? "waiting for a game" : "your runs show up here";
                Hero(warming && v.LiveFps > 0 ? Ink.F0(v.LiveFps) : "--", "--", "--", null, LiveSetup(v), double.NaN, Ui.Accent.Color, note);
            }
            heroFps.Foreground = measuring && SoFar() != null ? Ui.TextB : Ui.Sub;      // not counted yet, so not the run's number
            UnderLine(g0);
            // tiles from the latest reading
            int f1 = fans == null ? -1 : fans[0], fan2 = fans == null ? -1 : fans[1];
            if (s == null) Tiles(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, f1, fan2, false);
            else Tiles(s.CpuTemp, double.NaN, s.CpuWatts, s.CpuMhz, s.GpuTemp, double.NaN, s.GpuWatts, s.GpuMhz, double.NaN, f1, fan2, false);
        }

        /// <summary>The run's numbers so far: the same FrameStats the result uses, over every frame measured yet (the
        /// result keeps only the main swap chain; this is the live view). Worked out once a render, at most once a second.</summary>
        List<double> soFarFt;
        FrameStats soFar;
        long soFarAt;                                   // Stopwatch ticks: a clock change must not freeze the numbers
        FrameStats SoFar() {
            long now = Stopwatch.GetTimestamp();
            if (soFar != null && soFarAt != 0 && (now - soFarAt) < Stopwatch.Frequency * 8 / 10) return soFar;
            soFarAt = now;
            soFarFt = B.FrameTimes();
            soFar = soFarFt.Count >= 30 ? FrameStats.Of(soFarFt) : null;
            return soFar;
        }

        void Tiles(double ct, double cmax, double cw, double cmhz, double gt, double gmax, double gw, double gmhz, double fpw, double fan1, double fan2, bool run) {
            Tiles(ct, cmax, cw, cmhz, gt, gmax, gw, gmhz, fpw, fan1, fan2, run, E.P.RpmPerLevel, E.P.Curve.Ceiling);
        }
        static double Nz(double v) { return double.IsNaN(v) ? -1 : v; }
        void Tiles(double ct, double cmax, double cw, double cmhz, double gt, double gmax, double gw, double gmhz, double fpw, double fan1, double fan2, bool run, int rpl, int ceil) {
            Func<double, string> ghz = delegate(double m) { return double.IsNaN(m) || m <= 0 ? "" : (m / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " GHz"; };
            Func<double, string> watts = delegate(double w) { return double.IsNaN(w) ? "" : Ink.F0(w) + " W"; };
            Func<string[], string> join = delegate(string[] parts) { var l = new List<string>(); foreach (string x in parts) if (!string.IsNullOrEmpty(x)) l.Add(x); return string.Join(" · ", l.ToArray()); };
            // a run says peak and watts; live says watts and clock: the same words the Home page uses, in its order
            tCpu.Set(Ink.F0(ct), "°C", ct / 100, Ink.Cpu, 0, Ink.Cpu, run ? join(new[] { "CPU", double.IsNaN(cmax) ? "" : "peak " + Ink.F0(cmax), watts(cw) }) : join(new[] { "CPU", watts(cw), ghz(cmhz) }));
            string g = run ? join(new[] { "GPU", double.IsNaN(gmax) ? "" : "peak " + Ink.F0(gmax), watts(gw) }) : join(new[] { "GPU", watts(gw), ghz(gmhz) });
            tGpu.Set(Ink.F0(gt), "°C", gt / 100, Ink.Gpu, 0, Ink.Gpu, g == "GPU" ? "GPU · not read" : g);
            double total = double.IsNaN(cw) ? gw : double.IsNaN(gw) ? cw : cw + gw;
            double scale = Math.Max(150, double.IsNaN(total) ? 0 : total * 1.15);
            tPow.Set(Ink.F0(total), " W", (double.IsNaN(cw) ? 0 : cw) / scale, Ink.Cpu, (double.IsNaN(gw) ? 0 : gw) / scale, Ink.Gpu,
                run && !double.IsNaN(fpw) ? "CPU + GPU · " + Ink.F2(fpw) + " fps/W" : "CPU " + Ink.F0(cw) + " + GPU " + Ink.F0(gw));
            double top = Math.Max(fan1, fan2);       // a run passes its averages unrounded, so the tile and the card agree
            if (rpl > 0) tFan.Set(top < 0 ? "--" : (top * rpl).ToString("0", CultureInfo.InvariantCulture), " rpm", top < 0 ? 0 : top / (double)Math.Max(1, ceil), Ink.Fan, 0, Ink.Fan,
                "Fans · of " + (ceil * rpl).ToString(CultureInfo.InvariantCulture) + " rpm");
            else tFan.Set(top < 0 ? "--" : Ink.F0(100.0 * top / Math.Max(1, ceil)), "%", top < 0 ? 0 : top / (double)Math.Max(1, ceil), Ink.Fan, 0, Ink.Fan, "Fans · of top speed");
        }

        // ---------- the Runs view ----------
        void RenderRuns() {
            var all = Runs();
            var gamesSeen = new List<string>();
            foreach (var r in all) if (!gamesSeen.Contains(r.Game)) gamesSeen.Add(r.Game);
            runsTotal.Text = all.Count + (all.Count == 1 ? " run" : " runs") + " · " + gamesSeen.Count + (gamesSeen.Count == 1 ? " game" : " games");
            if (runsGame == null || !gamesSeen.Contains(runsGame)) runsGame = gamesSeen.Count > 0 ? gamesSeen[0] : null;
            gameLinks.Children.Clear();
            foreach (string g in gamesSeen) {
                string gg = g;
                bool sel = g == runsGame;
                var t = Ink.Sans(13, sel ? Ui.TextHi : Ui.Desc); t.Text = g; t.MaxWidth = 170;
                var b = new Border { Child = t, BorderBrush = sel ? (IBrush)Ui.Accent : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Margin = new Thickness(0, 0, 18, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                Ui.LeftUp(b, delegate { runsGame = gg; Render(); });
                gameLinks.Children.Add(b);
            }
            var mine = new List<BenchRun>();
            foreach (var r in all) if (r.Game == runsGame) mine.Add(r);
            if (mine.Count == 0) { bestBig.Text = lastBig.Text = "--"; bestSub.Text = lastSub.Text = ""; runRows.Children.Clear(); runBars.Set(new List<BenchRun>()); return; }
            BenchRun best = mine[0]; foreach (var r in mine) if (r.AvgFps > best.AvgFps) best = r;
            BenchRun last = mine[0];
            SetBig(bestBig, bestSub, best, "Best", " · " + best.Day, null);
            var prev = Previous(last);
            SetBig(lastBig, lastSub, last, "Last", "", prev == null ? null : (double?)(last.AvgFps - prev.AvgFps));
            runBars.Set(mine);
            runRows.Children.Clear();
            foreach (var r in mine) runRows.Children.Add(Row(r));   // the list scrolls
        }
        /// <summary>The "● " that leads a line of the Runs view: a 10 px bullet and a space in WPF. The bullet is not in
        /// IBM Plex, and whatever font the system falls back to draws it another size, so it is drawn here: a
        /// disc the size Windows shows, taking the room the two characters take there.</summary>
        static Inline Dot(Color c) {
            return new InlineUIContainer(new Ellipse { Width = 4, Height = 4, Fill = Ui.Brush(c), Margin = new Thickness(0.5, 0, 4, 2.5) }) { BaselineAlignment = BaselineAlignment.Baseline };
        }
        void SetBig(TextBlock big, TextBlock sub, BenchRun r, string what, string tail, double? d) {
            big.Inlines.Clear();
            big.Inlines.Add(new Run(Ink.F0(r.AvgFps)));
            big.Inlines.Add(new Run(" fps") { FontSize = 15, FontWeight = FontWeight.Normal, Foreground = Ui.Sub });
            sub.Inlines.Clear();
            sub.Inlines.Add(Dot(Ui.ModeColor(r.ModeIndex)));
            sub.Inlines.Add(new Run(what + " · "));
            sub.Inlines.Add(new Run(double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1)) { Foreground = Ui.TextB });
            sub.Inlines.Add(new Run(" low" + tail));
            if (d.HasValue) {
                sub.Inlines.Add(new Run(" · "));
                sub.Inlines.Add(new Run(Ink.Delta(d.Value) + " fps") { Foreground = Ui.Brush(Ink.DeltaColor(d.Value)) });
            }
        }
        Control Row(BenchRun r) {
            var g = new Grid { Height = 46 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var nums = new WpfTextBlock { FontFamily = Ui.UiFont, VerticalAlignment = VerticalAlignment.Center };
            nums.Inlines.Add(new Run(Ink.F0(r.AvgFps)) { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB });
            nums.Inlines.Add(new Run(" / " + (double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1))) { FontSize = 12.5, Foreground = Ui.Status });
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var l1 = new TrimText { FontFamily = Ui.UiFont, FontSize = 13, Foreground = Ui.TextB, TextTrimming = TextTrimming.CharacterEllipsis };
            l1.Inlines.Add(Dot(Ui.ModeColor(r.ModeIndex)));
            l1.Inlines.Add(new Run(r.Mode));
            l1.Inlines.Add(new Run(" · " + r.Day) { Foreground = Ui.Foot });
            var l2 = Ink.Mono(10.5, Ui.Foot);
            l2.Inlines.Add(new Run(Ink.Clock(r.MeasuredSec) + (r.Res.Length > 0 ? " · " + r.Res : "")));
            if (r.Flag.Length > 0) l2.Inlines.Add(new Run(" · " + r.Flag) { Foreground = Ui.Brush(Ink.WarnText) });
            l2.Margin = new Thickness(0, 2, 0, 0);
            mid.Children.Add(l1); mid.Children.Add(l2);
            var prev = Previous(r);
            var d = Ink.Mono(11, Ui.Foot); d.HorizontalAlignment = HorizontalAlignment.Right;
            if (prev != null) { double dv = r.AvgFps - prev.AvgFps; d.Text = Ink.Delta(dv); d.Foreground = Ui.Brush(Ink.DeltaColor(dv)); }
            Grid.SetColumn(mid, 1); Grid.SetColumn(d, 2);
            g.Children.Add(nums); g.Children.Add(mid); g.Children.Add(d);
            // Delete (#85): a bin on hover where the delta sits; one click asks, the second deletes. No dialog over a game.
            var binIcon = Ink.Icon(IcoTrash, 14, 1.6, Ui.Foot);
            var binAsk = Ink.Mono(11, Ui.Brush(Ink.WarnText)); binAsk.Text = "delete"; binAsk.IsVisible = false;
            var binBox = new Grid(); binBox.Children.Add(binIcon); binBox.Children.Add(binAsk);
            var bin = new Border { Child = binBox, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 8, 0, 8), IsVisible = false };
            Ui.Tip(bin, "Delete this run");
            bool armed = false;
            int armedAt = 0;
            bin.PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (e.Pointer.Captured == bin) e.Pointer.Capture(null); };
            bin.PointerReleased += delegate(object o, PointerReleasedEventArgs e) {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                e.Handled = true;                               // the row under it would open the run
                if (!armed) { armed = true; armedAt = Environment.TickCount; binIcon.IsVisible = false; binAsk.IsVisible = true; Ui.Tip(bin, "Click again to delete"); return; }
                if (unchecked(Environment.TickCount - armedAt) < 500) return;   // a double-click is one click, not the answer to the ask
                var h = Toast;
                if (RunStore.Delete(r)) {
                    if (shown != null && shown.Id == r.Id) shown = null;
                    if (lastRun != null && lastRun.Id == r.Id) lastRun = null;
                    runs = null;
                    if (h != null) h("Run deleted", false);
                }
                else if (h != null) h("Could not delete this run", true);
                Render();
            };
            Grid.SetColumn(bin, 1); Grid.SetColumnSpan(bin, 2); g.Children.Add(bin);   // "delete" is wider than the delta column
            var b = new Border { Child = g, BorderBrush = Ink.LineB, BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.PointerEntered += delegate { b.Background = Ui.Brush("#1B1815"); d.IsVisible = false; bin.IsVisible = true; };
            b.PointerExited += delegate {
                b.Background = Brushes.Transparent; d.IsVisible = true; bin.IsVisible = false;
                armed = false; binIcon.IsVisible = true; binAsk.IsVisible = false; Ui.Tip(bin, "Delete this run");
            };
            Ui.LeftUp(b, delegate { CommitSettings(); shown = r; showRuns = false; gi = 0; giFor = "Result"; Render(); });
            Ui.Tip(b, r.Local.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + (r.Settings.Length > 0 ? " · " + r.Settings : ""));
            return b;
        }
    }

    /// <summary>One bar per run of a game, oldest on the left, in the colour of the mode it ran in; the newest at
    /// full strength, the average written on top, the day under it.</summary>
    sealed class RunBars : Control {
        List<BenchRun> runs = new List<BenchRun>();
        static Typeface face;
        static bool faced;
        public RunBars() { Height = 78; }
        public void Set(List<BenchRun> newestFirst) {
            runs = new List<BenchRun>();
            for (int i = Math.Min(newestFirst.Count, 7) - 1; i >= 0; i--) runs.Add(newestFirst[i]);
            InvalidateVisual();
        }
        protected override Size MeasureOverride(Size a) { return new Size(double.IsInfinity(a.Width) ? 350 : a.Width, 78); }
        public override void Render(DrawingContext dc) {
            double W = Bounds.Width;
            if (!faced) { face = new Typeface(Ui.MonoFont, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal); faced = true; }
            dc.DrawLine(new Pen(Ink.LineB, 1), new Point(0, 62.5), new Point(W, 62.5));
            int n = runs.Count;
            if (n == 0) return;
            double hi = 0; foreach (var r in runs) hi = Math.Max(hi, r.AvgFps);
            hi *= 1.12;
            double slot = W / Math.Max(n, 5), w = Math.Min(34, slot - 14);
            for (int i = 0; i < n; i++) {
                var r = runs[i];
                double h = hi > 0 && r.AvgFps > 0 ? Math.Min(50, r.AvgFps / hi * 50) : 0, x = i * slot + (slot - w) / 2;
                var b = new SolidColorBrush(Ui.ModeColor(r.ModeIndex)) { Opacity = i == n - 1 ? 1 : 0.6 };
                dc.DrawRectangle(b, null, new Rect(x, 62 - h, w, h), 3, 3);
                var v = new FormattedText(Ink.F0(r.AvgFps), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, Ui.Brush("#C9C4C0"));
                dc.DrawText(v, new Point(Math.Round(x + w / 2 - v.Width / 2), Math.Round(62 - h - 5 - v.Height)));
                var d = new FormattedText(r.Day, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, Ui.Foot);
                dc.DrawText(d, new Point(Math.Round(x + w / 2 - d.Width / 2), 65));
            }
        }
    }

    // ================================================================== the pill ==================================
    /// <summary>The small pill over the game: the phase and the time. Click-through, never activated, kept on top.
    /// It shows while warming up, goes away a couple of seconds before measuring (a window over a borderless game can
    /// cost frames), and comes back for the result. Over a game that owns the whole screen nothing may show at all,
    /// so the start and end also have a sound.</summary>
    sealed class BenchPill : Window {
        readonly Border mark = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(1), RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = new RotateTransform(45), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock phase = Ink.Mono(11, Ui.SegText), value = Ink.Mono(11, Ui.TextB), extra = Ink.Mono(11, Ui.Status);
        readonly ColumnDefinition on = new ColumnDefinition(), off = new ColumnDefinition();
        readonly Border fill = new Border(), track;
        readonly DispatcherTimer hideAt;
        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XSync(IntPtr display, int discard);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr display);
        [DllImport("libXext.so.6")] static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int xOff, int yOff, IntPtr rects, int count, int op, int ordering);
        public BenchPill() {
            SystemDecorations = SystemDecorations.None; TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            Focusable = false; IsHitTestVisible = false; CanResize = false; SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
            var sp = new StackPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            value.Margin = new Thickness(8, 0, 0, 0); extra.Margin = new Thickness(8, 0, 0, 0); extra.MaxWidth = 300;
            row.Children.Add(mark); row.Children.Add(phase); row.Children.Add(value); row.Children.Add(extra);
            sp.Children.Add(new Border { Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromArgb(0xD1, 0x0F, 0x0D, 0x0B)), Padding = new Thickness(10, 0, 12, 0), Child = row });
            var g = new Grid(); g.ColumnDefinitions.Add(on); g.ColumnDefinitions.Add(off); g.Children.Add(fill);
            track = new Border { Height = 2, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Child = g, ClipToBounds = true };
            sp.Children.Add(track);
            Content = sp;
            // its own window, which WPF formats with the font's own advances: no Display-mode spacing here
            foreach (var t in new[] { phase, value, extra }) t.LetterSpacing = 0;
            // Never steal focus from the game, never appear in the task switcher (the properties above), let every
            // click through to the game.
            Opened += delegate { PassClicks(); };
            hideAt = new DispatcherTimer();
            hideAt.Tick += delegate { hideAt.Stop(); Off(); };
        }
        /// <summary>An empty input shape: the pointer goes straight through to the game. As Osd does it.</summary>
        void PassClicks() {
            try {
                var h = TryGetPlatformHandle();
                if (h == null || h.HandleDescriptor != "XID" || h.Handle == IntPtr.Zero) return;
                IntPtr d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) return;
                try { XShapeCombineRectangles(d, h.Handle, 2 /* ShapeInput */, 0, 0, IntPtr.Zero, 0, 0 /* ShapeSet */, 0); XSync(d, 0); }
                finally { XCloseDisplay(d); }
            } catch (Exception ex) { Log.Write("pill: click-through unavailable: " + ex.Message); }
        }
        /// <param name="progress">0..1, or below 0 for no bar</param>
        public void Display(IntPtr game, Color dot, string ph, Color phColor, string val, string ex, double progress, Color bar, int seconds) {
            mark.Background = Ui.Brush(dot);
            phase.Text = ph; phase.Foreground = Ui.Brush(phColor);
            value.Text = val ?? ""; value.IsVisible = !string.IsNullOrEmpty(val);
            extra.Text = ex ?? ""; extra.IsVisible = !string.IsNullOrEmpty(ex);
            track.IsVisible = progress >= 0;
            progress = Math.Max(0, Math.Min(1, progress));
            on.Width = new GridLength(progress, GridUnitType.Star); off.Width = new GridLength(Math.Max(0.0001, 1 - progress), GridUnitType.Star);
            fill.Background = Ui.Brush(bar);
            bool was = IsVisible;
            if (!was) { Position = new PixelPoint(-10000, -10000); Show(); }
            Place(game);
            hideAt.Stop();
            if (seconds > 0) { hideAt.Interval = TimeSpan.FromSeconds(seconds); hideAt.Start(); }
        }
        void Place(IntPtr game) {
            var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null) { Position = new PixelPoint(16, 16); return; }
            var wa = screen.WorkingArea;
            Position = new PixelPoint(wa.X + 16, wa.Y + 16);                   // physical pixels, as on Windows
        }
        public void Off() { hideAt.Stop(); if (IsVisible) Hide(); }
        public bool Timed { get { return hideAt.IsEnabled; } }
    }

    // ================================================================== the share card ============================
    /// <summary>One of a stored run's eight graphs, as the cards draw it: the series, its colour, its headline and
    /// the words that go with it. The same eight the result page flips through, in the same order.</summary>
    sealed class RunGraph {
        public string Name, Big, Unit, Peak;
        public double[] Values;
        public Color Color;
        public double Ref = double.NaN, MinSpan = 6;
        public static readonly string[] Names = { "FPS", "CPU temperature", "GPU temperature", "CPU power", "GPU power", "CPU clock", "GPU load", "Fans" };
        public static RunGraph Of(BenchRun r, int g) {
            var s = r.Samples;
            Func<Func<BenchSample, double>, double[]> col = delegate(Func<BenchSample, double> f) { var a = new double[s.Count]; for (int i = 0; i < s.Count; i++) a[i] = f(s[i]); return a; };
            int rpl = r.RpmPerLevel;
            Func<int, double> rpm = delegate(int lv) { return lv < 0 ? double.NaN : rpl > 0 ? lv * rpl : 100.0 * lv / Math.Max(1, r.FanCeiling); };
            var x = new RunGraph { Name = Names[g] };
            switch (g) {
                case 0: x.Values = col(b => b.Fps); x.Color = Ui.ModeColor(r.ModeIndex); x.Big = Ink.F0(r.AvgFps); x.Unit = " fps"; x.Peak = double.IsNaN(r.Low1) ? "" : Ink.F0(r.Low1) + " 1% low"; x.Ref = r.Low1; x.MinSpan = 10; break;
                case 1: x.Values = col(b => b.CpuT); x.Color = Ink.Cpu; x.Big = Ink.F0(r.CpuTAvg); x.Unit = "°"; x.Peak = "peak " + Ink.F0(r.CpuTMax) + "°"; x.Ref = r.CpuTMax; break;
                case 2: x.Values = col(b => b.GpuT); x.Color = Ink.Gpu; x.Big = Ink.F0(r.GpuTAvg); x.Unit = "°"; x.Peak = "peak " + Ink.F0(r.GpuTMax) + "°"; x.Ref = r.GpuTMax; break;
                case 3: x.Values = col(b => b.CpuW); x.Color = Ink.Cpu; x.Big = Ink.F0(r.CpuWAvg); x.Unit = " W"; x.Peak = "peak " + Ink.F0(r.CpuWMax) + " W"; x.MinSpan = 8; break;
                case 4: x.Values = col(b => b.GpuW); x.Color = Ink.Gpu; x.Big = Ink.F0(r.GpuWAvg); x.Unit = " W"; x.Peak = "peak " + Ink.F0(r.GpuWMax) + " W"; x.MinSpan = 8; break;
                case 5: x.Values = col(b => b.CpuMhz / 1000); x.Color = Ink.Cpu; x.Big = Ink.F1(r.CpuMhzAvg / 1000); x.Unit = " GHz"; x.Peak = "peak " + Ink.F1(r.CpuMhzMax / 1000) + " GHz"; x.MinSpan = 0.4; break;
                case 6: x.Values = col(b => b.GpuLoad); x.Color = Ink.Gpu; x.Big = Ink.F0(r.GpuLoadAvg); x.Unit = "%"; x.Peak = Ink.F1(r.GpuMhzAvg / 1000) + " GHz"; x.MinSpan = 10; break;
                default: {
                    x.Values = col(b => rpm(Math.Max(b.Fan1, b.Fan2))); x.Color = Ink.Fan; x.MinSpan = rpl > 0 ? 400 : 8;
                    double top = Math.Max(double.IsNaN(r.Fan1Avg) ? -1 : r.Fan1Avg, double.IsNaN(r.Fan2Avg) ? -1 : r.Fan2Avg);
                    x.Big = top < 0 ? "--" : rpl > 0 ? Ink.F0(top * rpl) : Ink.F0(100 * top / Math.Max(1, r.FanCeiling));
                    x.Unit = rpl > 0 ? " rpm" : "%";
                    x.Peak = rpl > 0 && r.FanCeiling > 0 ? "of " + (r.FanCeiling * rpl).ToString(CultureInfo.InvariantCulture) + " rpm" : "";
                    break;
                }
            }
            return x;
        }
    }

    /// <summary>The share cards, drawn from the stored run: what was measured, on what, and anything that would make
    /// the number not mean what it seems to. Never the user's or the PC's name. Three layouts: Summary (the numbers
    /// and one graph, 1080 x 1350), Graphs (all eight, 1080 x 1350) and Square (1080 x 1080).</summary>
    static class Card {
        public static readonly string[] Layouts = { "Summary", "Graphs", "Square" };
        public static Size SizeOf(int layout) { return layout == 2 ? new Size(1080, 1080) : new Size(1080, 1350); }
        static readonly IBrush Bg = Ui.Brush("#0F0D0B"), Panel = Ui.Brush("#161311"), Text = Ui.Brush("#EDEAE8"), Sub = Ui.Brush("#96918D"), Dim = Ui.Brush("#A8A3A0"), Line = Ui.Brush("#2C2825");

        public static Bitmap Render(BenchRun r, BenchRun prev) { return Render(r, prev, 0, 0); }
        public static Bitmap Render(BenchRun r, BenchRun prev, int layout, int graph) {
            layout = Math.Max(0, Math.Min(2, layout));
            graph = Math.Max(0, Math.Min(RunGraph.Names.Length - 1, graph));
            Size size = SizeOf(layout);
            var root = new Grid { Width = size.Width, Height = size.Height, Background = Bg };
            var dock = new DockPanel { Margin = new Thickness(layout == 1 ? 64 : 72), LastChildFill = true };
            root.Children.Add(dock);
            if (layout == 0) Summary(dock, r, prev, graph);
            else if (layout == 1) Graphs(dock, r, prev);
            else Square(dock, r, prev, graph);
            Plain(root);
            root.Measure(size);
            root.Arrange(new Rect(size));
            var rtb = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
            rtb.Render(root);
            return rtb;
        }
        /// <summary>WPF draws the card with TextFormattingMode.Ideal and without layout rounding: sizes are whatever
        /// the fonts say, to a fraction of a pixel. Here that means rounding off on every element, and the letter
        /// spacing the window gives its own (Display-mode) text taken back.</summary>
        static void Plain(Control c) {
            c.UseLayoutRounding = false;
            var tb = c as TextBlock;
            if (tb != null) tb.LetterSpacing = 0;
            var p = c as Panel;
            if (p != null) foreach (Control k in p.Children) Plain(k);
            var d = c as Decorator;
            if (d != null && d.Child != null) Plain(d.Child);
        }

        // ---------- Summary: the numbers, one graph of your choosing, six more numbers ----------
        static void Summary(DockPanel dock, BenchRun r, BenchRun prev, int graph) {
            var acc = Ui.Brush(Ui.ModeColor(r.ModeIndex));
            Top(dock, Header(r, prev, 72, 26, true));
            var nums = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 40, 0, 0) };
            nums.Children.Add(Big(Ink.F0(r.AvgFps), 232, Text, "AVERAGE FPS"));
            nums.Children.Add(Big(double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1), 108, acc, "1% LOW"));
            nums.Children.Add(Big(double.IsNaN(r.Low01) ? "—" : Ink.F0(r.Low01), 108, Dim, "0.1% LOW"));
            Top(dock, nums);
            Top(dock, ChartPanel(r, RunGraph.Of(r, graph), 196, true, new Thickness(0, 40, 0, 0)));
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 40, 0, 0) };
            grid.Children.Add(Pair(Ink.F0(r.CpuTAvg), "°", double.IsNaN(r.CpuTMax) ? "" : "peak " + Ink.F0(r.CpuTMax) + "°", "CPU temperature"));
            grid.Children.Add(Pair(Ink.F0(r.GpuTAvg), "°", double.IsNaN(r.GpuTMax) ? "" : "peak " + Ink.F0(r.GpuTMax) + "°", "GPU temperature"));
            grid.Children.Add(Pair(Ink.F0(r.TotalW), " W", "", "CPU + GPU power"));
            grid.Children.Add(Pair(Ink.F2(r.FpsPerWatt), "", "", "Frames per watt"));
            grid.Children.Add(Pair(Ink.F1(r.StutterPct), "%", "", "Time in stutters"));
            var fans = RunGraph.Of(r, 7);
            grid.Children.Add(Pair(fans.Big, fans.Unit == "%" ? "%" : " rpm", "", "Fans, average"));
            Top(dock, grid);
            dock.Children.Add(Footer(r, false));
        }

        // ---------- Graphs: FPS large with its numbers in it, then CPU on the left, GPU on the right, fans across ----------
        static void Graphs(DockPanel dock, BenchRun r, BenchRun prev) {
            var acc = Ui.Brush(Ui.ModeColor(r.ModeIndex));
            Top(dock, Header(r, prev, 60, 24, true));
            var foot = Footer(r, true);
            DockPanel.SetDock(foot, Dock.Bottom); dock.Children.Add(foot);
            var grid = new Grid { Margin = new Thickness(0, 30, 0, 24) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 5; i++) {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(i == 0 ? 2.4 : 1, GridUnitType.Star) });
                if (i < 4) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            }
            // FPS: the graph that matters, larger, built like the others so the type is the same everywhere
            var fp = Mini(RunGraph.Of(r, 0), "1% low " + (double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1)) + " · 0.1% low " + (double.IsNaN(r.Low01) ? "—" : Ink.F0(r.Low01)) + " · stutter " + Ink.F1(r.StutterPct) + "%", true);
            Grid.SetColumnSpan(fp, 3); grid.Children.Add(fp);
            // the rest, small: CPU down the left, GPU down the right, the fans across the bottom
            for (int g = 1; g < 8; g++) {
                var x = RunGraph.Of(r, g);
                var p = Mini(x, x.Peak, false);
                Grid.SetRow(p, ((g + 1) / 2) * 2);
                if (g == 7) Grid.SetColumnSpan(p, 3); else Grid.SetColumn(p, g % 2 == 1 ? 0 : 2);
                grid.Children.Add(p);
            }
            dock.Children.Add(grid);
        }
        static Border Mini(RunGraph x, string sub, bool labels) {
            var p = new Border { Background = Panel, CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 12, 22, 14) };
            var dp = new DockPanel();
            var head = new Grid();
            var hl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hl.Children.Add(T(x.Name, 19, Text, false));
            if (sub.Length > 0) { var pk = T(" · " + sub, 17, Sub, false); hl.Children.Add(pk); }
            head.Children.Add(hl);
            var v = new TextBlock { FontFamily = Ui.UiFont, FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            v.Inlines.Add(new Run(x.Big)); v.Inlines.Add(new Run(x.Unit) { FontSize = x.Unit.StartsWith("°") ? 22 : 18, FontWeight = FontWeight.Normal, Foreground = Sub });
            head.Children.Add(v);
            DockPanel.SetDock(head, Dock.Top); dp.Children.Add(head);
            dp.Children.Add(new CardChart { Graph = x, Labels = labels, Margin = new Thickness(0, labels ? 12 : 6, 0, 0) });
            p.Child = dp;
            return p;
        }

        // ---------- Square: the two numbers people compare, one graph, made for a feed ----------
        static void Square(DockPanel dock, BenchRun r, BenchRun prev, int graph) {
            var acc = Ui.Brush(Ui.ModeColor(r.ModeIndex));
            Top(dock, Header(r, prev, 64, 24, false));
            var nums = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 36, 0, 0) };
            nums.Children.Add(Big(Ink.F0(r.AvgFps), 220, Text, "AVERAGE FPS"));
            nums.Children.Add(Big(double.IsNaN(r.Low1) ? "—" : Ink.F0(r.Low1), 120, acc, "1% LOW"));
            Top(dock, nums);
            var foot = Footer(r, false);
            DockPanel.SetDock(foot, Dock.Bottom); dock.Children.Add(foot);
            var panel = ChartPanel(r, RunGraph.Of(r, graph), 0, false, new Thickness(0, 36, 0, 28));
            dock.Children.Add(panel);
        }

        // ---------- pieces ----------
        static void Top(DockPanel d, Control e) { DockPanel.SetDock(e, Dock.Top); d.Children.Add(e); }
        static Grid Header(BenchRun r, BenchRun prev, double gameSize, double setupSize, bool chips) {
            var acc = Ui.Brush(Ui.ModeColor(r.ModeIndex));
            var head = new Grid();
            var hl = new StackPanel();
            var modeLine = new StackPanel { Orientation = Orientation.Horizontal };
            modeLine.Children.Add(Diamond(14, acc, 12));
            modeLine.Children.Add(T(Engine.ModeNames[r.ModeIndex] + " mode" + (chips ? "" : " · " + r.Local.ToString("d MMM yyyy", CultureInfo.InvariantCulture)), 22, Sub, false));
            hl.Children.Add(modeLine);
            var game = T(r.Game, gameSize, Text, true); game.Margin = new Thickness(0, 8, 0, 0); game.TextTrimming = TextTrimming.CharacterEllipsis; game.MaxWidth = 760; game.HorizontalAlignment = HorizontalAlignment.Left;
            hl.Children.Add(game);
            var setup = new List<string>();
            if (r.ResLong.Length > 0) setup.Add(r.ResLong);
            if (r.Hz > 0) setup.Add(r.Hz + " Hz");
            if (r.Settings.Length > 0) setup.Add(r.Settings);
            var st = T(string.Join(" · ", setup.ToArray()), setupSize, Sub, false); st.Margin = new Thickness(0, 6, 0, 0); st.TextTrimming = TextTrimming.CharacterEllipsis; st.MaxWidth = 936; st.HorizontalAlignment = HorizontalAlignment.Left;
            if (setup.Count > 0) hl.Children.Add(st);
            if (chips) {
                var c = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
                c.Children.Add(Chip(r.Local.ToString("d MMM yyyy", CultureInfo.InvariantCulture), Dim));
                if (prev != null) {
                    double d = r.AvgFps - prev.AvgFps;
                    c.Children.Add(Chip(Ink.Delta(d) + " fps vs " + prev.Day, Ui.Brush(Ink.DeltaColor(d))));
                }
                foreach (string f in r.Flags) c.Children.Add(Chip(f, Ui.Brush(Ink.WarnText)));
                hl.Children.Add(c);
            } else if (r.Flags.Count > 0) {
                var fl = M(string.Join(" · ", r.Flags.ToArray()), 19, Ui.Brush(Ink.WarnText)); fl.Margin = new Thickness(0, 10, 0, 0); fl.TextTrimming = TextTrimming.CharacterEllipsis; fl.MaxWidth = 936; fl.HorizontalAlignment = HorizontalAlignment.Left;
                hl.Children.Add(fl);
            }
            head.Children.Add(hl);
            var mark = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
            mark.Children.Add(Diamond(18, acc, 12)); mark.Children.Add(T("ohman", 30, Text, true));
            head.Children.Add(mark);
            return head;
        }
        static Border ChartPanel(BenchRun r, RunGraph x, double height, bool legend, Thickness margin) {
            var panel = new Border { Background = Panel, CornerRadius = new CornerRadius(20), Padding = new Thickness(32, 26, 32, 26), Margin = margin };
            var dp = new DockPanel();
            var ph = new Grid();
            string over = x.Name + " over " + Ink.Clock(r.MeasuredSec) + " measured" + (r.WarmSec >= 1 ? ", after " + Ink.Clock(r.WarmSec) + " warm-up" : "");
            ph.Children.Add(T(over, 22, Text, false));
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (x.Name == "FPS") {
                right.Children.Add(new Border { Width = 20, Height = 3, Background = new SolidColorBrush(x.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                right.Children.Add(T("FPS", 18, Sub, false));
                right.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(0, 1.5), EndPoint = new Point(20, 1.5), Stroke = Sub, StrokeThickness = 3, StrokeDashArray = new AvaloniaList<double> { 1.2, 1.2 }, Width = 20, Height = 3, Margin = new Thickness(22, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                right.Children.Add(T("1% low", 18, Sub, false));
            } else {
                var t = M("avg " + x.Big + x.Unit.Trim() + (x.Peak.Length > 0 ? " · " + x.Peak : ""), 18, Sub);
                t.VerticalAlignment = VerticalAlignment.Center;
                right.Children.Add(t);
            }
            if (!legend && x.Name == "FPS") right.IsVisible = true;
            ph.Children.Add(right);
            DockPanel.SetDock(ph, Dock.Top); dp.Children.Add(ph);
            var chart = new CardChart { Graph = x, Labels = true, Margin = new Thickness(0, 16, 0, 0) };
            if (height > 0) chart.Height = height;
            dp.Children.Add(chart);
            panel.Child = dp;
            return panel;
        }
        static Border Footer(BenchRun r, bool span) {
            var foot = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            var fb = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1.5, 0, 0), Padding = new Thickness(0, 24, 0, 0), Child = foot, VerticalAlignment = VerticalAlignment.Bottom };
            var fl = new StackPanel();
            var m1 = new List<string>(); foreach (string s in new[] { r.Model, r.Gpu, r.Cpu }) if (!string.IsNullOrEmpty(s)) m1.Add(s);
            var m2 = new List<string> { r.OnAc ? "AC" : "battery" };
            if (span) m2.Add(Ink.Clock(r.MeasuredSec) + " measured");
            if (r.GpuDriver.Length > 0) m2.Add("driver " + r.GpuDriver);
            m2.Add(r.Frames.ToString("N0", CultureInfo.InvariantCulture) + " frames");
            m2.Add("Ohman " + r.App);
            var a1 = M(string.Join(" · ", m1.ToArray()), 17, Sub); a1.TextTrimming = TextTrimming.CharacterEllipsis; a1.MaxWidth = 700; a1.HorizontalAlignment = HorizontalAlignment.Left;
            var a2 = M(string.Join(" · ", m2.ToArray()), 17, Sub); a2.Margin = new Thickness(0, 8, 0, 0); a2.TextTrimming = TextTrimming.CharacterEllipsis; a2.MaxWidth = 700; a2.HorizontalAlignment = HorizontalAlignment.Left;
            fl.Children.Add(a1); fl.Children.Add(a2);
            foot.Children.Add(fl);
            var site = M("ohmanapp.github.io", 17, Sub); site.HorizontalAlignment = HorizontalAlignment.Right; site.VerticalAlignment = VerticalAlignment.Bottom;
            foot.Children.Add(site);
            return fb;
        }
        static TextBlock T(string s, double size, IBrush b, bool semi) { return new TextBlock { Text = s, FontFamily = Ui.UiFont, FontSize = size, Foreground = b, FontWeight = semi ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center }; }
        static TextBlock M(string s, double size, IBrush b) { return new TextBlock { Text = s, FontFamily = Ui.MonoFont, FontSize = size, Foreground = b }; }
        static Border Diamond(double s, IBrush b, double gap) { return new Border { Width = s, Height = s, CornerRadius = new CornerRadius(3), Background = b, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = new RotateTransform(45), Margin = new Thickness(2, 0, gap, 0), VerticalAlignment = VerticalAlignment.Center }; }
        static Border Chip(string s, IBrush fg) { return new Border { Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 12, 8), Child = M(s, 19, fg) }; }
        static StackPanel Big(string v, double size, IBrush b, string label) {
            var sp = new StackPanel { Margin = new Thickness(0, 0, size > 150 ? 56 : 44, 0), VerticalAlignment = VerticalAlignment.Bottom };
            sp.Children.Add(new TextBlock { Text = v, FontFamily = Ui.UiFont, FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = b, Margin = new Thickness(0, -0.26 * size, 0, -0.2 * size) });
            sp.Children.Add(new Tracked { Text = label, Size = size > 150 ? 18 : 16, Tracking = 3, Fill = Sub, Margin = new Thickness(4, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
            return sp;
        }
        static StackPanel Pair(string v, string unit, string extra, string label) {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 24, 30) };
            var t = new TextBlock { FontFamily = Ui.UiFont, FontSize = 46, FontWeight = FontWeight.SemiBold, Foreground = Text };
            t.Inlines.Add(new Run(v));
            if (unit.Length > 0) t.Inlines.Add(new Run(unit) { FontSize = unit.StartsWith("°") ? 30 : 24, FontWeight = FontWeight.Normal, Foreground = Sub });
            sp.Children.Add(t);
            sp.Children.Add(new TextBlock { Text = label + (extra.Length > 0 ? " · " + extra : ""), FontFamily = Ui.UiFont, FontSize = 20, Foreground = Sub, Margin = new Thickness(0, 4, 0, 0) });
            return sp;
        }

        /// <summary>A graph second by second, its area, a dashed reference (the 1% low, or the peak), and three round
        /// grid lines with their values on the right when there is room for them.</summary>
        sealed class CardChart : Control {
            public RunGraph Graph;
            public bool Labels = true;
            protected override Size MeasureOverride(Size a) {
                return new Size(double.IsInfinity(a.Width) ? 872 : a.Width, double.IsNaN(Height) ? (double.IsInfinity(a.Height) ? 196 : a.Height) : Height);
            }
            public override void Render(DrawingContext dc) {
                double W = Bounds.Width - (Labels ? 56 : 0), H = Bounds.Height - 4;
                if (W <= 10 || H <= 10) return;
                var v = Graph.Values;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (double x in v) if (!double.IsNaN(x)) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
                if (!double.IsNaN(Graph.Ref)) { lo = Math.Min(lo, Graph.Ref); hi = Math.Max(hi, Graph.Ref); }
                if (lo > hi) return;
                double span = Math.Max(Graph.MinSpan, hi - lo), step = Nice(span / 2);
                double g0 = Math.Floor(lo / step) * step, g2 = g0 + 3 * step;
                while (g2 < hi) { step = Nice(step * 1.5); g0 = Math.Floor(lo / step) * step; g2 = g0 + 3 * step; }
                Func<double, double> Y = delegate(double val) { return H - (val - g0) / (g2 - g0) * (H - (Labels ? 20 : 8)); };
                var grid = new Pen(Line, 1.5);
                var face = new Typeface(Ui.MonoFont, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
                if (!Labels) {
                    double pad = Math.Max(Graph.MinSpan, hi - lo) * 0.12, mid = (lo + hi) / 2, half = Math.Max(Graph.MinSpan, hi - lo) / 2 + pad;
                    g0 = mid - half; g2 = mid + half;
                    Y = delegate(double val) { return H - (val - g0) / (g2 - g0) * H; };
                    dc.DrawLine(grid, new Point(0, H + 0.5), new Point(W, H + 0.5));
                }
                for (int i = 0; i <= 3 && Labels; i++) {
                    double gv = g0 + i * step, y = Math.Round(Y(gv)) + 0.5;
                    dc.DrawLine(grid, new Point(0, y), new Point(W, y));
                    if (i == 0) continue;
                    var ft = new FormattedText(gv.ToString(step < 1 ? "0.0" : "0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 17, Sub);
                    dc.DrawText(ft, new Point(W + 12, y - ft.Height / 2));
                }
                int n = v.Length;
                if (n >= 2) {
                    var line = new StreamGeometry(); var area = new StreamGeometry();
                    bool started = false;
                    using (var c = line.Open()) {
                        for (int i = 0; i < n; i++) { if (double.IsNaN(v[i])) continue; var p = new Point(i / (double)(n - 1) * W, Y(v[i])); if (!started) { c.BeginFigure(p, false); started = true; } else c.LineTo(p); }
                        if (started) c.EndFigure(false);
                    }
                    started = false;
                    using (var c = area.Open()) {
                        double lastX = 0;
                        for (int i = 0; i < n; i++) { if (double.IsNaN(v[i])) continue; double x = i / (double)(n - 1) * W; if (!started) { c.BeginFigure(new Point(x, H), true); started = true; } c.LineTo(new Point(x, Y(v[i]))); lastX = x; }
                        if (started) { c.LineTo(new Point(lastX, H)); c.EndFigure(true); }
                    }
                    dc.DrawGeometry(new SolidColorBrush(Graph.Color) { Opacity = 0.12 }, null, area);
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Graph.Color), Labels ? 2.2 : 2) { LineJoin = PenLineJoin.Round }, line);
                }
                if (!double.IsNaN(Graph.Ref)) {
                    double y = Math.Round(Y(Graph.Ref)) + 0.5;
                    dc.DrawLine(new Pen(Sub, 2) { DashStyle = new DashStyle(new double[] { 3.5, 3.5 }, 0) }, new Point(0, y), new Point(W, y));
                }
            }
            static double Nice(double x) {
                double p = Math.Pow(10, Math.Floor(Math.Log10(x))), m = x / p;
                return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * p;
            }
        }
    }

    /// <summary>Files written into the user's own folders. On Windows the process writing them is elevated, and this
    /// class is there to stop a link in those folders from sending an elevated write somewhere else. Ohman's window
    /// is an ordinary process of the user's on Linux, so what remains is the one promise: a new file only, never an
    /// overwrite.</summary>
    static class SafeFile {
        /// <summary>Write data to dir/stem+ext, or dir/stem (2)+ext and so on when that exists. Returns the path.</summary>
        public static string WriteNew(string dir, string stem, string ext, byte[] data) {
            dir = System.IO.Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            for (int n = 1; n < 100; n++) {
                string path = System.IO.Path.Combine(dir, n == 1 ? stem + ext : stem + " (" + n + ")" + ext);
                FileStream fs;
                try { fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None); }
                catch (IOException) { if (File.Exists(path) || Directory.Exists(path)) continue; throw; }
                using (fs) fs.Write(data, 0, data.Length);
                return path;
            }
            throw new IOException("too many files named " + stem);
        }
    }
}
