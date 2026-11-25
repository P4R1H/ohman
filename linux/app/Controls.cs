// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman, Linux: the small controls the pages are built from: a letter-spaced label, the two colour strips,
// the filled segment and the underlined link row, a rail button, and the on-screen flash.
// The Avalonia port of src\Controls.cs, kept member for member so a change there can be carried over from a diff.
//
// Differences from the WPF original
//  - Types only, unless listed here: FrameworkElement is Control, Brush is IBrush, ActualWidth is Bounds.Width,
//    the Mouse* events are the Pointer* ones. Every class, member, constructor and event keeps its name and shape.
//  - Tool tips. Avalonia has no ToolTip property on a control (it is the attached ToolTip.Tip). ValueLink and NavBtn
//    keep a settable ToolTip property so `new ValueLink { ToolTip = "..." }` still reads the same; for any other
//    control use Ui.Tip(control, text).
//  - WpfTextBlock (new, declared here, free for the window to use): a TextBlock measured as WPF measures one. WPF
//    rounds a measured size to the nearest device pixel where Avalonia rounds up, and in the window's Display text
//    mode a line is round(ascent) + round(descent) device pixels. Every label in these controls is one; with a
//    plain TextBlock a segment comes out a device pixel taller or shorter than on Windows, depending on font size.
//    Widths are not corrected: Display mode also rounds every glyph's advance, which Avalonia cannot do, so a
//    label's width differs from Windows by up to about a pixel.
//  - A TextBlock in Avalonia is only hit where it has drawn, a WPF one over its whole box. The labels of LinkSeg get
//    a transparent background so the padding under the text (where the underline sits) is still part of the link.
//  - Clicks. Where WPF listened to MouseLeftButtonUp the port uses Ui.LeftUp, which also gives up Avalonia's
//    implicit pointer capture so the release goes to the element under the pointer, as in WPF.
//  - ValueLink's list is an Avalonia Popup with light dismiss. Its drop shadow is declared as in WPF and, as in WPF,
//    is clipped by the popup's own window, so it does not show.
//  - Osd. A borderless, topmost, transparent Window that is not activated when shown and does not take focus.
//      * The fade runs on the content, not on Window.Opacity: a top-level's opacity is not reliably a window
//        property on X11, and the window itself is transparent, so the result on screen is the same.
//      * Position is set in screen pixels from the primary screen's working area (WPF: SystemParameters.WorkArea).
//      * Click-through is done with the X Shape extension (an empty input region), where WPF set WS_EX_TRANSPARENT.
//        If libXext is missing the flash still shows and simply takes clicks for the second and a half it is up.
//      * Wayland: Avalonia 11 runs through XWayland there. A compositor is free to ignore "always on top" and the
//        requested position for an XWayland window, and to give it focus; nothing in Avalonia can insist. GNOME and
//        KDE honour both for XWayland clients in practice, but none of this has been run on a real display.
//      * The icon is fitted into its 18 px box by hand (Fit), because Avalonia's Stretch.Uniform leaves the pen
//        out of the sum and draws it a pen's width larger than WPF does.
//      * WS_EX_TOOLWINDOW (not in Alt-Tab) is ShowInTaskbar = false, which sets the skip-taskbar and skip-pager hints.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shape = Avalonia.Controls.Shapes.Shape;
using WPath = Avalonia.Controls.Shapes.Path;

namespace Ohman {

    /// <summary>A TextBlock measured the way WPF measures one. Two things differ in a plain Avalonia TextBlock. With
    /// layout rounding on, WPF rounds a measured size to the nearest device pixel and Avalonia rounds it up. And the
    /// WPF window formats text in Display mode, where a line is its ascent and its descent each rounded to a whole
    /// device pixel (IBM Plex at 12 is 12.3 + 3.3 = 15.6, which Display mode makes 16 at 200 %, not 15.5). Left
    /// alone, a label comes out up to a pixel taller or shorter than on Windows and everything stacked on it moves
    /// by that much. The glyphs themselves are drawn the same either way.</summary>
    class WpfTextBlock : TextBlock {
        protected override Type StyleKeyOverride { get { return typeof(TextBlock); } }
        protected override Size MeasureOverride(Size a) {
            var s = base.MeasureOverride(a);          // already rounded up; the layout it made still has the true size
            var tl = TextLayout;
            if (!UseLayoutRounding || tl == null) return s;
            double k = LayoutHelper.GetLayoutScale(this);
            var pad = Padding;
            double w = Math.Round((tl.WidthIncludingTrailingWhitespace + pad.Left + pad.Right) * k, MidpointRounding.AwayFromZero) / k;
            double text = tl.Height;
            int lines = tl.TextLines.Count;
            if (double.IsNaN(LineHeight) && lines > 0) {
                var m = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch).GlyphTypeface.Metrics;
                double asc = -m.Ascent * FontSize / m.DesignEmHeight, desc = m.Descent * FontSize / m.DesignEmHeight, gap = m.LineGap * FontSize / m.DesignEmHeight;
                // only when every line is this font's own line: one that fell back to another font for a glyph keeps its height
                if (Math.Abs(text - lines * (asc + desc + gap)) < 0.01)
                    text = lines * (Math.Round(asc * k, MidpointRounding.AwayFromZero) + Math.Round((desc + gap) * k, MidpointRounding.AwayFromZero)) / k;
            }
            double h = Math.Round((text + pad.Top + pad.Bottom) * k, MidpointRounding.AwayFromZero) / k;
            return new Size(Math.Min(s.Width, w), h);
        }
    }

    sealed class Tracked : Control {
        public string Text = "";
        public double Size = 10, Tracking = 1.4;
        public IBrush Fill = Ui.Section;
        Typeface face;
        bool faced;
        void Face() { if (!faced) { face = new Typeface(Ui.MonoFont, FontStyle.Normal, FontWeight.Medium, FontStretch.Normal); faced = true; } }
        FormattedText Glyph(char c) { return new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Size, Fill); }
        protected override Size MeasureOverride(Size a) {
            Face();
            double w = 0, h = 0;
            foreach (char c in Text) { var ft = Glyph(c); w += ft.WidthIncludingTrailingWhitespace + Tracking; h = Math.Max(h, ft.Height); }
            return new Size(w, h);
        }
        public override void Render(DrawingContext dc) {
            Face();
            double x = 0;
            foreach (char c in Text) { var ft = Glyph(c); dc.DrawText(ft, new Point(x, 0)); x += ft.WidthIncludingTrailingWhitespace + Tracking; }
        }
    }

    /// <summary>A strip of colour cells: the hue row, or the shades of one hue. Click or drag to pick.</summary>
    sealed class StripPicker : Control {
        public int Cells = 36;
        public bool Shade;
        public double Hue = 210;
        public Rgb Current;
        public bool HasCurrent;
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
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < Cells; i++) {
                var c = ColorAt(i);
                double d = (c.R - Current.R) * (c.R - Current.R) + (c.G - Current.G) * (c.G - Current.G) + (c.B - Current.B) * (c.B - Current.B);
                if (d < bd) { bd = d; best = i; }
            }
            return bd <= 1200 ? best : -1;                                  // only ring a cell that really is the colour
        }
        public override void Render(DrawingContext dc) {
            double w = Bounds.Width / Cells, h = Bounds.Height;
            for (int i = 0; i < Cells; i++) dc.DrawRectangle(Ui.Brush(ColorAt(i)), null, new Rect(i * w, 0, i == Cells - 1 ? w : w + 0.7, h));    // the overlap hides the seams; the last cell has none to hide and must not spill past the strip
            int sel = Nearest();
            if (sel >= 0) dc.DrawRectangle(null, new Pen(Ui.Brush("#FCFAF9"), 2), new Rect(sel * w + 1, 1, Math.Max(1, w - 2), h - 2));
        }
        void Pick(Point p) {
            int i = Math.Max(0, Math.Min(Cells - 1, (int)(p.X / (Bounds.Width / Cells))));
            var h = Picked;
            if (h != null) h(ColorAt(i));
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e) { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; e.Pointer.Capture(this); Pick(e.GetPosition(this)); }
        protected override void OnPointerMoved(PointerEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.Captured == this) Pick(e.GetPosition(this)); }
        protected override void OnPointerReleased(PointerReleasedEventArgs e) { if (e.InitialPressMouseButton == MouseButton.Left && e.Pointer.Captured == this) e.Pointer.Capture(null); }
    }

    /// <summary>A value inside a sentence, the way a rule reads in a filter or a smart folder: the words are the
    /// caption and the value is a coloured word that opens a list on click. One font, no box, no arrows, and every
    /// value on the line behaves the same way, so nothing needs a hint.</summary>
    sealed class ValueLink : Border {
        public string[] Items = new string[0];
        public event Action<int> Changed;
        int index;
        bool editable;
        readonly TextBlock text = new WpfTextBlock { FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.TextB, VerticalAlignment = VerticalAlignment.Center };
        /// <summary>Grey and inert until the row's pencil says otherwise; then the accent, a hand, and a list on click.</summary>
        public bool Editable {
            get { return editable; }
            set { editable = value; text.Foreground = value ? Ui.Brush(Ui.BalColor) : Ui.TextB; Cursor = value ? Cursors.Hand : Cursors.Arrow; if (!value) Background = Brushes.Transparent; }
        }
        public object ToolTip { get { return Avalonia.Controls.ToolTip.GetTip(this); } set { Avalonia.Controls.ToolTip.SetTip(this, value); } }
        readonly Popup popup = new Popup { IsLightDismissEnabled = true, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
        public ValueLink() {
            // The hover pill pads the word by 4; a negative margin keeps the text itself on the line's left edge.
            Background = Brushes.Transparent; CornerRadius = new CornerRadius(4); Padding = new Thickness(4, 1, 4, 1); Margin = new Thickness(-4, 0, 0, 0);
            Cursor = Cursors.Arrow; VerticalAlignment = VerticalAlignment.Center; UseLayoutRounding = true; Child = text;
            popup.PlacementTarget = this;
            LogicalChildren.Add(popup);          // the list takes the window's styles and resources through this
            PointerEntered += delegate { if (editable) Background = Ui.Pill; };
            PointerExited += delegate { if (!popup.IsOpen) Background = Brushes.Transparent; };
            popup.Closed += delegate { Background = IsPointerOver ? Ui.Pill : Brushes.Transparent; };
            Ui.LeftUp(this, OnMouseLeftButtonUp);
        }
        public int Index { get { return index; } set { index = Math.Max(0, Math.Min(Items.Length - 1, value)); text.Text = Items.Length > 0 ? Items[index] : ""; } }
        void Set(int i) { int was = index; Index = i; if (index != was) { var h = Changed; if (h != null) h(index); } }
        void OnMouseLeftButtonUp() {
            if (!editable) return;
            var list = new StackPanel();
            Border current = null;
            for (int i = 0; i < Items.Length; i++) {
                int idx = i;
                var item = new Border { Background = i == index ? Ui.Pill : Brushes.Transparent, CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 5, 24, 5), Cursor = Cursors.Hand,
                    Child = new WpfTextBlock { Text = Items[i], FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = i == index ? Ui.TextHi : Ui.TextB } };
                item.PointerEntered += delegate { item.Background = Ui.Pill; };
                item.PointerExited += delegate { item.Background = idx == index ? Ui.Pill : Brushes.Transparent; };
                Ui.LeftUp(item, delegate { popup.IsOpen = false; Set(idx); });
                list.Children.Add(item);
                if (i == index) current = item;
            }
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200 };
            // WPF's ShadowDepth 4 at its default 315 degrees is an offset of 2.83 right and 2.83 down
            popup.Child = new Border { Background = Ui.Card, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = scroll,
                Effect = new DropShadowEffect { BlurRadius = 18, OffsetX = 2.83, OffsetY = 2.83, Opacity = 0.55, Color = Colors.Black } };
            popup.IsOpen = true;
            if (current != null) current.BringIntoView();
        }
    }

    /// <summary>A filled segment: labels in a sunken box, the accent pill slides to the selected one.</summary>
    sealed class Seg : Border {
        public enum Kind { Page, Compact, Row }
        readonly Grid grid = new Grid();
        readonly Panel cells;
        readonly Border pill;
        readonly TranslateTransform pillT = new TranslateTransform();
        readonly List<TextBlock> labels = new List<TextBlock>();
        readonly List<Border> items = new List<Border>();
        readonly List<bool> enabled = new List<bool>();
        int sel = -1;
        public readonly object[] Tags;
        public event Action<int> Picked;                      // user clicks only
        public Seg(string[] names, string[] tips, object[] tags, Kind kind) {
            Tags = tags;
            double pad = kind == Kind.Page ? 4 : 3, radius = kind == Kind.Page ? 10 : kind == Kind.Compact ? 9 : 8, inner = kind == Kind.Row ? 6 : 7;
            double size = kind == Kind.Page ? 13.5 : 12, padY = kind == Kind.Page ? 9 : kind == Kind.Compact ? 7 : 6, padX = kind == Kind.Row ? 12 : 6;
            cells = kind == Kind.Row ? (Panel)new StackPanel { Orientation = Orientation.Horizontal } : new UniformGrid { Rows = 1 };
            Background = Ui.Sunken;
            CornerRadius = new CornerRadius(radius);
            Padding = new Thickness(pad);
            pill = new Border { CornerRadius = new CornerRadius(inner), Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 40, Opacity = 0, RenderTransform = pillT };
            grid.Children.Add(pill);
            grid.Children.Add(cells);
            Child = grid;
            for (int i = 0; i < names.Length; i++) {
                int idx = i;
                var tb = new WpfTextBlock { Text = names[i], FontFamily = Ui.UiFont, FontSize = size, Foreground = Ui.SegText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var cell = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(padX, padY, padX, padY), CornerRadius = new CornerRadius(inner), Child = tb };
                if (tips != null && i < tips.Length && tips[i] != null) Ui.Tip(cell, tips[i]);
                Ui.LeftUp(cell, delegate { if (idx == sel || !enabled[idx]) return; Select(idx, true); var h = Picked; if (h != null) h(idx); });
                cell.PointerEntered += delegate { if (idx != sel && enabled[idx]) tb.Foreground = Ui.TextB; };
                cell.PointerExited += delegate { if (idx != sel && enabled[idx]) tb.Foreground = Ui.SegText; };
                labels.Add(tb);
                items.Add(cell);
                enabled.Add(true);
                cells.Children.Add(cell);
            }
            cells.SizeChanged += delegate { Place(false); };
        }
        public int Count { get { return items.Count; } }
        public void SetMono(double size) { foreach (var tb in labels) { tb.FontFamily = Ui.MonoFont; tb.FontSize = size; } }
        /// <summary>A choice this hardware cannot make: dimmed, not clickable, with the reason on hover.</summary>
        public void SetEnabled(int i, bool on, string why) {
            enabled[i] = on;
            items[i].Opacity = on ? 1 : 0.3;
            items[i].Cursor = on ? Cursors.Hand : Cursors.Arrow;
            if (!on) Ui.Tip(items[i], why);
        }
        public void Select(int i, bool animate) {
            sel = i;
            for (int j = 0; j < labels.Count; j++) { labels[j].Foreground = j == i ? Brushes.White : (items[j].IsPointerOver && enabled[j] ? Ui.TextB : Ui.SegText); labels[j].FontWeight = j == i ? FontWeight.Medium : FontWeight.Normal; }
            Place(animate);
        }
        void Place(bool animate) {
            if (sel < 0 || cells.Bounds.Width <= 0) { pill.Opacity = 0; return; }
            double x = 0, w = 0;
            if (sel < items.Count && items[sel].Bounds.Width > 0) { x = items[sel].Bounds.X; w = items[sel].Bounds.Width; }     // a cell is a direct child of cells: its bounds are in cells' space
            else { w = cells.Bounds.Width / items.Count; x = w * sel; }
            x = Math.Round(x);
            w = Math.Round(w);
            if (pill.Opacity == 0 || !animate) { Ui.StopAnimation(pill, WidthProperty); Ui.StopAnimation(pillT, TranslateTransform.XProperty); pill.Width = w; pillT.X = x; pill.Opacity = 1; return; }
            Ui.Glide(pill, WidthProperty, w, 320, true);
            Ui.Glide(pillT, TranslateTransform.XProperty, x, 320, true);
        }
    }

    /// <summary>A row of text links; the accent underline slides to the selected one.</summary>
    sealed class LinkSeg : Grid {
        readonly StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal }; readonly Border line; readonly TranslateTransform lineT = new TranslateTransform();
        readonly List<TextBlock> labels = new List<TextBlock>();
        readonly List<bool> off = new List<bool>();
        int sel = -1;
        public event Action<int> Picked;
        public LinkSeg(string[] names, double gap, double size, double under, string[] tips) {
            line = new Border { Height = 1, Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 20, Opacity = 0, RenderTransform = lineT };
            Children.Add(sp);
            Children.Add(line);
            for (int i = 0; i < names.Length; i++) {
                int idx = i;
                // the transparent background makes the whole box the link, padding included, as a WPF TextBlock is
                var tb = new WpfTextBlock { Text = names[i], FontFamily = Ui.UiFont, FontSize = size, Foreground = Ui.Desc, Cursor = Cursors.Hand, Background = Brushes.Transparent,
                    Padding = new Thickness(0, 0, 0, under), Margin = new Thickness(0, 0, i < names.Length - 1 ? gap : 0, 0), VerticalAlignment = VerticalAlignment.Center };
                if (tips != null && i < tips.Length && tips[i] != null) Ui.Tip(tb, tips[i]);
                Ui.LeftUp(tb, delegate { if (off[idx]) return; Select(idx, true); var h = Picked; if (h != null) h(idx); });
                tb.PointerEntered += delegate { if (idx != sel && !off[idx]) tb.Foreground = Ui.TextB; };
                tb.PointerExited += delegate { if (idx != sel) tb.Foreground = Ui.Desc; };
                tb.SizeChanged += delegate { Place(false); };
                labels.Add(tb);
                off.Add(false);
                sp.Children.Add(tb);
            }
            SizeChanged += delegate { Place(false); };
        }
        public void SetText(int i, string t) { if (labels[i].Text != t) labels[i].Text = t; }
        /// <summary>Grey out a choice this machine cannot do, with the reason on the tooltip.</summary>
        public void SetEnabled(int i, bool on, string why) {
            off[i] = !on;
            labels[i].Opacity = on ? 1 : 0.35;
            labels[i].Cursor = on ? Cursors.Hand : Cursors.Arrow;
            Ui.Tip(labels[i], on ? null : why);
        }
        public void Select(int i, bool animate) {
            sel = i;
            for (int j = 0; j < labels.Count; j++) labels[j].Foreground = j == i ? Ui.TextHi : (labels[j].IsPointerOver ? Ui.TextB : Ui.Desc);
            Place(animate);
        }
        void Place(bool animate) {
            if (sel < 0 || Bounds.Width <= 0 || labels[sel].Bounds.Width <= 0) { line.Opacity = 0; return; }
            var tb = labels[sel];
            Point p = tb.TranslatePoint(new Point(0, 0), this) ?? new Point(tb.Bounds.X, 0);
            double x = Math.Round(p.X), w = Math.Round(tb.Bounds.Width);
            if (line.Opacity == 0 || !animate) { Ui.StopAnimation(line, WidthProperty); Ui.StopAnimation(lineT, TranslateTransform.XProperty); line.Width = w; lineT.X = x; line.Opacity = 1; return; }
            Ui.Glide(line, WidthProperty, w, 320, true);
            Ui.Glide(lineT, TranslateTransform.XProperty, x, 320, true);
        }
    }

    /// <summary>A rail button: an icon in a 42 px square; the rail's pill slides behind the selected one.</summary>
    sealed class NavBtn : Border {
        public readonly int Index;
        readonly List<Shape> strokes = new List<Shape>(), fills = new List<Shape>();
        bool sel, accent;
        /// <summary>Draw in the accent colour even when this is not the page being shown. The update button is
        /// the only one that does: it is a notice as much as a button, and on a rail of icons the colour is the
        /// only thing that says so without being hovered.</summary>
        public bool Accent { set { accent = value; Tint(); } }
        public object ToolTip { get { return Avalonia.Controls.ToolTip.GetTip(this); } set { Avalonia.Controls.ToolTip.SetTip(this, value); } }
        public event Action<int> Clicked;
        public NavBtn(int index, string tip, string[] strokePaths, string[] fillPaths) {
            Index = index;
            Width = 42;
            Height = 42;
            CornerRadius = new CornerRadius(12);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            ToolTip = tip;
            Margin = new Thickness(0, 2, 0, 2);
            var g = new Grid { Width = 18, Height = 18 };
            foreach (string d in strokePaths) { var p = new WPath { Data = Scaled(d), StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Stretch = Stretch.None, Width = 18, Height = 18 }; strokes.Add(p); g.Children.Add(p); }
            foreach (string d in fillPaths) { var p = new WPath { Data = Scaled(d), Stretch = Stretch.None, Width = 18, Height = 18 }; fills.Add(p); g.Children.Add(p); }
            Child = g;
            Tint();
            PointerPressed += delegate(object o, PointerPressedEventArgs e) { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; e.Handled = true; var h = Clicked; if (h != null) h(Index); };
            PointerEntered += delegate { Tint(); }; PointerExited += delegate { Tint(); };
        }
        /// <summary>The 24-unit icon geometry scaled into the 18 px box (a geometry transform, so the pen stays 1.6 px).</summary>
        static Geometry Scaled(string d) {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd, Transform = new ScaleTransform(0.75, 0.75) };
            g.Children.Add(Geometry.Parse(d));
            return g;
        }
        public void SetSelected(bool on) { sel = on; Tint(); }
        void Tint() {
            var b = sel ? Ui.TextB : IsPointerOver ? Ui.Sub : accent ? (IBrush)Ui.Accent : Ui.Axis;
            foreach (var p in strokes) p.Stroke = b;
            foreach (var p in fills) p.Fill = b;
        }
    }

    /// <summary>Small on-screen flash (bottom centre) shown when the OMEN key or a hotkey changes something while the panel is hidden.</summary>
    sealed class Osd : Window {
        readonly WPath icon;
        readonly TextBlock txt, sub;
        readonly Border box;
        readonly DispatcherTimer hide;
        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XSync(IntPtr display, int discard);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr display);
        [DllImport("libXext.so.6")] static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int xOff, int yOff, IntPtr rects, int count, int op, int ordering);
        public Osd() {
            SystemDecorations = SystemDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            IsHitTestVisible = false;
            box = new Border { CornerRadius = new CornerRadius(14), Background = Ui.Brush("#F2161311"), BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 20, 12), Opacity = 0 };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            icon = new WPath { StrokeThickness = 1.8, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Width = 18, Height = 18, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt = new WpfTextBlock { FontFamily = Ui.UiFont, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextB };
            sub = new WpfTextBlock { FontFamily = Ui.UiFont, FontSize = 12.5, Foreground = Ui.Sub, Margin = new Thickness(0, 3, 0, 0) };
            col.Children.Add(txt);
            col.Children.Add(sub);
            sp.Children.Add(icon);
            sp.Children.Add(col);
            box.Child = sp;
            Content = box;
            hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            hide.Tick += delegate { hide.Stop(); Ui.Animate(box, OpacityProperty, box.Opacity, 0, 260, null, 0, delegate { if (box.Opacity < 0.05) Hide(); }); };
            Opened += delegate { PassClicks(); };   // never take a click meant for the game; not stealing focus and staying out of Alt-Tab are the properties above
        }
        /// <summary>An empty input shape: the pointer goes straight through to whatever is underneath. A second
        /// connection to the X server is enough to say so, and Avalonia does not hand out its own.</summary>
        void PassClicks() {
            try {
                var h = TryGetPlatformHandle();
                if (h == null || h.HandleDescriptor != "XID" || h.Handle == IntPtr.Zero) return;
                IntPtr d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) return;
                try { XShapeCombineRectangles(d, h.Handle, 2 /* ShapeInput */, 0, 0, IntPtr.Zero, 0, 0 /* ShapeSet */, 0); XSync(d, 0); }
                finally { XCloseDisplay(d); }
            } catch (Exception ex) { Log.Write("osd: click-through unavailable: " + ex.Message); }
        }
        /// <summary>WPF's Stretch.Uniform for a stroked path: the outline is scaled so that it and its pen together fill
        /// the box, and centred. Avalonia's own Uniform scales the outline alone to the box, which draws the icon a
        /// pen's width larger, so the fit is done here and the path is left unstretched.</summary>
        static Geometry Fit(Geometry g, double box, double pen) {
            Rect b = g.Bounds;
            if (b.Width <= 0 || b.Height <= 0) return g;
            double k = Math.Min((box - pen) / b.Width, (box - pen) / b.Height);
            g.Transform = new MatrixTransform(Matrix.CreateTranslation(-b.X, -b.Y) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation((box - b.Width * k) / 2, (box - b.Height * k) / 2));
            return g;
        }
        public void Flash(string title, string detail, Color c, string pathData) {
            txt.Text = title;
            sub.Text = detail;
            sub.IsVisible = !string.IsNullOrEmpty(detail);
            icon.Data = Fit(Geometry.Parse(pathData), 18, 1.8);
            icon.Stroke = Ui.Brush(c);
            if (!IsVisible) { box.Opacity = 0; Show(); }
            UpdateLayout();
            box.Measure(Size.Infinity);
            var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen != null) {
                var wa = screen.WorkingArea;
                double k = screen.Scaling;
                Position = new PixelPoint((int)Math.Round(wa.X + (wa.Width - box.DesiredSize.Width * k) / 2), (int)Math.Round(wa.Bottom - box.DesiredSize.Height * k - 72 * k));
            }
            Ui.Animate(box, OpacityProperty, box.Opacity, 1, 120, null);
            hide.Stop();
            hide.Start();
        }
    }
}
