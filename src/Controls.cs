// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the small controls the pages are built from: a letter-spaced label, the two colour strips,
// the filled segment and the underlined link row, a rail button, and the on-screen flash.
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

    sealed class Tracked : FrameworkElement {
        public string Text = ""; public double Size = 10, Tracking = 1.4; public Brush Fill = Ui.Section;
        Typeface face;
        void Face() { if (face == null) face = new Typeface(Ui.MonoFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal); }
        FormattedText Glyph(char c) { return new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Size, Fill, 1.0); }
        protected override Size MeasureOverride(Size a) {
            Face(); double w = 0, h = 0;
            foreach (char c in Text) { var ft = Glyph(c); w += ft.WidthIncludingTrailingWhitespace + Tracking; h = Math.Max(h, ft.Height); }
            return new Size(w, h);
        }
        protected override void OnRender(DrawingContext dc) {
            Face(); double x = 0;
            foreach (char c in Text) { var ft = Glyph(c); dc.DrawText(ft, new Point(x, 0)); x += ft.WidthIncludingTrailingWhitespace + Tracking; }
        }
    }

    /// <summary>A strip of colour cells: the hue row, or the shades of one hue. Click or drag to pick.</summary>
    sealed class StripPicker : FrameworkElement {
        public int Cells = 36; public bool Shade; public double Hue = 210;
        public Rgb Current; public bool HasCurrent;
        public event Action<Rgb> Picked;
        public StripPicker() { Cursor = Cursors.Cross; }
        protected override Size MeasureOverride(Size a) { return new Size(double.IsInfinity(a.Width) ? 200 : a.Width, 26); }
        public void Repaint() { InvalidateVisual(); }
        public Rgb ColorAt(int i) {
            if (!Shade) return Ui.Hsl(i * 360.0 / Cells, 0.85, 0.55);
            double t = Cells <= 1 ? 0 : i / (double)(Cells - 1);
            return Ui.Hsl(Hue, 0.28 + t * 0.6, 0.95 - t * 0.76);
        }
        int Nearest() {
            if (!HasCurrent) return -1;
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < Cells; i++) {
                var c = ColorAt(i); double d = (c.R - Current.R) * (c.R - Current.R) + (c.G - Current.G) * (c.G - Current.G) + (c.B - Current.B) * (c.B - Current.B);
                if (d < bd) { bd = d; best = i; }
            }
            return bd <= 1200 ? best : -1;                                  // only ring a cell that really is the colour
        }
        protected override void OnRender(DrawingContext dc) {
            double w = ActualWidth / Cells, h = ActualHeight;
            for (int i = 0; i < Cells; i++) dc.DrawRectangle(Ui.Brush(ColorAt(i)), null, new Rect(i * w, 0, w + 0.7, h));
            int sel = Nearest();
            if (sel >= 0) dc.DrawRectangle(null, new Pen(Ui.Brush("#FCFAF9"), 2), new Rect(sel * w + 1, 1, Math.Max(1, w - 2), h - 2));
        }
        void Pick(Point p) {
            int i = Math.Max(0, Math.Min(Cells - 1, (int)(p.X / (ActualWidth / Cells))));
            var h = Picked; if (h != null) h(ColorAt(i));
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Pick(e.GetPosition(this)); }
        protected override void OnMouseMove(MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured) Pick(e.GetPosition(this)); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) ReleaseMouseCapture(); }
    }

    /// <summary>A filled segment: labels in a sunken box, the accent pill slides to the selected one.</summary>
    sealed class Seg : Border {
        public enum Kind { Page, Compact, Row }
        readonly Grid grid = new Grid(); readonly Panel cells; readonly Border pill; readonly TranslateTransform pillT = new TranslateTransform();
        readonly List<TextBlock> labels = new List<TextBlock>(); readonly List<Border> items = new List<Border>(); readonly List<bool> enabled = new List<bool>();
        int sel = -1; public readonly object[] Tags;
        public event Action<int> Picked;                      // user clicks only
        public Seg(string[] names, string[] tips, object[] tags, Kind kind) {
            Tags = tags;
            double pad = kind == Kind.Page ? 4 : 3, radius = kind == Kind.Page ? 10 : kind == Kind.Compact ? 9 : 8, inner = kind == Kind.Row ? 6 : 7;
            double size = kind == Kind.Page ? 13.5 : 12, padY = kind == Kind.Page ? 9 : kind == Kind.Compact ? 7 : 6, padX = kind == Kind.Row ? 12 : 6;
            cells = kind == Kind.Row ? (Panel)new StackPanel { Orientation = Orientation.Horizontal } : new UniformGrid { Rows = 1 };
            Background = Ui.Sunken; CornerRadius = new CornerRadius(radius); Padding = new Thickness(pad);
            pill = new Border { CornerRadius = new CornerRadius(inner), Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 40, Opacity = 0, RenderTransform = pillT };
            grid.Children.Add(pill); grid.Children.Add(cells); Child = grid;
            for (int i = 0; i < names.Length; i++) {
                int idx = i;
                var tb = new TextBlock { Text = names[i], FontFamily = Ui.UiFont, FontSize = size, Foreground = Ui.SegText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var cell = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(padX, padY, padX, padY), CornerRadius = new CornerRadius(inner), Child = tb };
                if (tips != null && i < tips.Length && tips[i] != null) cell.ToolTip = tips[i];
                cell.MouseLeftButtonUp += delegate { if (idx == sel || !enabled[idx]) return; Select(idx, true); var h = Picked; if (h != null) h(idx); };
                cell.MouseEnter += delegate { if (idx != sel && enabled[idx]) tb.Foreground = Ui.TextB; };
                cell.MouseLeave += delegate { if (idx != sel && enabled[idx]) tb.Foreground = Ui.SegText; };
                labels.Add(tb); items.Add(cell); enabled.Add(true); cells.Children.Add(cell);
            }
            cells.SizeChanged += delegate { Place(false); };
        }
        public int Count { get { return items.Count; } }
        public void SetMono(double size) { foreach (var tb in labels) { tb.FontFamily = Ui.MonoFont; tb.FontSize = size; } }
        /// <summary>A choice this hardware cannot make: dimmed, not clickable, with the reason on hover.</summary>
        public void SetEnabled(int i, bool on, string why) {
            enabled[i] = on; items[i].Opacity = on ? 1 : 0.3; items[i].Cursor = on ? Cursors.Hand : Cursors.Arrow;
            if (!on) items[i].ToolTip = why;
        }
        public void Select(int i, bool animate) {
            sel = i;
            for (int j = 0; j < labels.Count; j++) { labels[j].Foreground = j == i ? Brushes.White : (items[j].IsMouseOver && enabled[j] ? Ui.TextB : Ui.SegText); labels[j].FontWeight = j == i ? FontWeights.Medium : FontWeights.Normal; }
            Place(animate);
        }
        void Place(bool animate) {
            if (sel < 0 || cells.ActualWidth <= 0) { pill.Opacity = 0; return; }
            double x = 0, w = 0;
            if (sel < items.Count && items[sel].ActualWidth > 0) { x = items[sel].TranslatePoint(new Point(0, 0), cells).X; w = items[sel].ActualWidth; }
            else { w = cells.ActualWidth / items.Count; x = w * sel; }
            x = Math.Round(x); w = Math.Round(w);
            if (pill.Opacity == 0 || !animate) { pill.BeginAnimation(WidthProperty, null); pillT.BeginAnimation(TranslateTransform.XProperty, null); pill.Width = w; pillT.X = x; pill.Opacity = 1; return; }
            Ui.Glide(pill, WidthProperty, w, 320, true); Ui.Glide(pillT, TranslateTransform.XProperty, x, 320, true);
        }
    }

    /// <summary>A row of text links; the accent underline slides to the selected one.</summary>
    sealed class LinkSeg : Grid {
        readonly StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal }; readonly Border line; readonly TranslateTransform lineT = new TranslateTransform();
        readonly List<TextBlock> labels = new List<TextBlock>(); readonly List<bool> off = new List<bool>(); int sel = -1;
        public event Action<int> Picked;
        public LinkSeg(string[] names, double gap, double size, double under, string[] tips) {
            line = new Border { Height = 1, Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 20, Opacity = 0, RenderTransform = lineT };
            Children.Add(sp); Children.Add(line);
            for (int i = 0; i < names.Length; i++) {
                int idx = i;
                var tb = new TextBlock { Text = names[i], FontFamily = Ui.UiFont, FontSize = size, Foreground = Ui.Desc, Cursor = Cursors.Hand,
                    Padding = new Thickness(0, 0, 0, under), Margin = new Thickness(0, 0, i < names.Length - 1 ? gap : 0, 0), VerticalAlignment = VerticalAlignment.Center };
                if (tips != null && i < tips.Length && tips[i] != null) tb.ToolTip = tips[i];
                tb.MouseLeftButtonUp += delegate { if (off[idx]) return; Select(idx, true); var h = Picked; if (h != null) h(idx); };
                tb.MouseEnter += delegate { if (idx != sel && !off[idx]) tb.Foreground = Ui.TextB; };
                tb.MouseLeave += delegate { if (idx != sel) tb.Foreground = Ui.Desc; };
                tb.SizeChanged += delegate { Place(false); };
                labels.Add(tb); off.Add(false); sp.Children.Add(tb);
            }
            SizeChanged += delegate { Place(false); };
        }
        public void SetText(int i, string t) { if (labels[i].Text != t) labels[i].Text = t; }
        /// <summary>Grey out a choice this machine cannot do, with the reason on the tooltip.</summary>
        public void SetEnabled(int i, bool on, string why) {
            off[i] = !on;
            labels[i].Opacity = on ? 1 : 0.35;
            labels[i].Cursor = on ? Cursors.Hand : Cursors.Arrow;
            labels[i].ToolTip = on ? null : why;
        }
        public void Select(int i, bool animate) {
            sel = i;
            for (int j = 0; j < labels.Count; j++) labels[j].Foreground = j == i ? Ui.TextHi : (labels[j].IsMouseOver ? Ui.TextB : Ui.Desc);
            Place(animate);
        }
        void Place(bool animate) {
            if (sel < 0 || ActualWidth <= 0 || labels[sel].ActualWidth <= 0) { line.Opacity = 0; return; }
            var tb = labels[sel]; Point p = tb.TranslatePoint(new Point(0, 0), this);
            double x = Math.Round(p.X), w = Math.Round(tb.ActualWidth);
            if (line.Opacity == 0 || !animate) { line.BeginAnimation(WidthProperty, null); lineT.BeginAnimation(TranslateTransform.XProperty, null); line.Width = w; lineT.X = x; line.Opacity = 1; return; }
            Ui.Glide(line, WidthProperty, w, 320, true); Ui.Glide(lineT, TranslateTransform.XProperty, x, 320, true);
        }
    }

    /// <summary>A rail button: an icon in a 42 px square; the rail's pill slides behind the selected one.</summary>
    sealed class NavBtn : Border {
        public readonly int Index; readonly List<Shape> strokes = new List<Shape>(), fills = new List<Shape>(); bool sel;
        public event Action<int> Clicked;
        public NavBtn(int index, string tip, string[] strokePaths, string[] fillPaths) {
            Index = index; Width = 42; Height = 42; CornerRadius = new CornerRadius(12); Background = Brushes.Transparent; Cursor = Cursors.Hand; ToolTip = tip; Margin = new Thickness(0, 2, 0, 2);
            var g = new Grid { Width = 18, Height = 18 };
            foreach (string d in strokePaths) { var p = new WPath { Data = Scaled(d), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.None, Width = 18, Height = 18 }; strokes.Add(p); g.Children.Add(p); }
            foreach (string d in fillPaths) { var p = new WPath { Data = Scaled(d), Stretch = Stretch.None, Width = 18, Height = 18 }; fills.Add(p); g.Children.Add(p); }
            Child = g; Tint();
            MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; var h = Clicked; if (h != null) h(Index); };
            MouseEnter += delegate { Tint(); }; MouseLeave += delegate { Tint(); };
        }
        /// <summary>The 24-unit icon geometry scaled into the 18 px box (a geometry transform, so the pen stays 1.6 px).</summary>
        static Geometry Scaled(string d) {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd, Transform = new ScaleTransform(0.75, 0.75) };
            g.Children.Add(Geometry.Parse(d)); g.Freeze(); return g;
        }
        public void SetSelected(bool on) { sel = on; Tint(); }
        void Tint() {
            var b = sel ? Ui.TextB : (IsMouseOver ? Ui.Sub : Ui.Axis);
            foreach (var p in strokes) p.Stroke = b;
            foreach (var p in fills) p.Fill = b;
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
            var box = new Border { CornerRadius = new CornerRadius(14), Background = Ui.Brush("#F2161311"), BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 20, 12) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            icon = new WPath { StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 18, Height = 18, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt = new TextBlock { FontFamily = Ui.UiFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.TextB };
            sub = new TextBlock { FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Sub, Margin = new Thickness(0, 3, 0, 0) };
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
}
