// Headless UI preview: renders the real OmenLite controls + chrome to a PNG so the
// layout can be inspected without running the elevated app or touching hardware.
// Compile together with OmenLite.cs using /main:OmenLite.Preview.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace OmenLite {
    static class Preview {
        const int W = 360, H = 430;
        static void PaintAt(Graphics g, Painted c, int x, int y, int w, int h) {
            c.Size = new Size(w, h);
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.SetClip(new Rectangle(0, 0, w, h));
            c.Paint2(g);
            g.Restore(st);
        }

        [STAThread]
        static void Main(string[] args) {
            string outPath = args.Length > 0 ? args[0] : "preview.png";
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var rp = Draw.Round(new Rectangle(0, 0, W, H), 14)) g.SetClip(rp);

                MainForm.PaintChrome(g, W, H, "Performance", false);

                const int PAD = 20;
                var seg = new Seg(false) { Items = new[] { "Eco", "Balanced", "Performance" } }; seg.SetSilent(2);
                PaintAt(g, seg, PAD, MainForm.Y_MODE, W - 2 * PAD, MainForm.H_MODE);

                var ro = new Readout(); ro.Set(48, 34);
                PaintAt(g, ro, PAD, MainForm.Y_CARD, W - 2 * PAD, MainForm.H_CARD);

                var sl = new Slider(); sl.SetSilent(40);
                PaintAt(g, sl, PAD, MainForm.Y_SLIDER, W - 2 * PAD - 76, MainForm.H_SLIDER);
                var hold = new Pill { Caption = "HOLD" }; hold.SetSilent(false);
                PaintAt(g, hold, W - PAD - 66, MainForm.Y_SLIDER, 66, MainForm.H_SLIDER);

                int tw = (W - 2 * PAD - 16) / 2;
                var mx = new Toggle { Label = "Max Fan" }; mx.SetSilent(false);
                PaintAt(g, mx, PAD, MainForm.Y_TOGGLE, tw, MainForm.H_TOGGLE);
                var gb = new Toggle { Label = "GPU Boost" }; gb.SetSilent(true);
                PaintAt(g, gb, W - PAD - tw, MainForm.Y_TOGGLE, tw, MainForm.H_TOGGLE);

                var pw = new Seg(true) { Items = new[] { "0 W", "+5 W", "+10 W", "+15 W" } }; pw.SetSilent(0);
                PaintAt(g, pw, PAD, MainForm.Y_POWER, W - 2 * PAD, MainForm.H_POWER);

                bmp.Save(outPath, ImageFormat.Png);
            }
        }
    }
}
