using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SanqiuTingji.App;

/// 三秋听记's Apple Liquid Glass mark: translucent sapphire-cyan squircle with top specular glass sheen,
/// inner rim refraction, three balanced crystalline voice-wave bars ("三" + listening), and an AI note spark.
public static class Icons
{
    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush Solid(string hex, double opacity = 1)
    {
        var b = new SolidColorBrush(Hex(hex)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Geometry Path(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// A four-pointed crystalline spark centred on (x, y).
    private static Geometry Spark(double x, double y, double r)
    {
        var k = r * 0.22;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string P(double a, double b) => string.Format(inv, "{0:0.###},{1:0.###}", a, b);
        return Path($"M{P(x, y - r)} C{P(x + k, y - k)} {P(x + k, y - k)} {P(x + r, y)} " +
                    $"C{P(x + k, y + k)} {P(x + k, y + k)} {P(x, y + r)} " +
                    $"C{P(x - k, y + k)} {P(x - k, y + k)} {P(x - r, y)} " +
                    $"C{P(x - k, y - k)} {P(x - k, y - k)} {P(x, y - r)} Z");
    }

    public static BitmapSource Mark(int size, bool live)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 100.0, size / 100.0));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 100, 100), 24, 24));

            // Deep sapphire-to-sky Apple Liquid Glass gradient base
            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0.08, 0.0),
                EndPoint = new Point(0.92, 1.0),
            };
            bg.GradientStops.Add(new GradientStop(Hex("#36A8FF"), 0.0));
            bg.GradientStops.Add(new GradientStop(Hex("#007AFF"), 0.52));
            bg.GradientStops.Add(new GradientStop(Hex("#0051D5"), 1.0));
            dc.DrawRectangle(bg, null, new Rect(0, 0, 100, 100));

            // Ambient caustic glow in lower-left for liquid depth
            var caustic = new RadialGradientBrush(Color.FromArgb(105, 130, 232, 255), Color.FromArgb(0, 130, 232, 255))
            {
                Center = new Point(0.24, 0.85),
                GradientOrigin = new Point(0.24, 0.85),
                RadiusX = 0.62,
                RadiusY = 0.52,
            };
            dc.DrawRectangle(caustic, null, new Rect(0, 0, 100, 100));

            // Upper glass specular dome reflection
            var sheen = new LinearGradientBrush(Color.FromArgb(118, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawEllipse(sheen, null, new Point(50, 12), 62, 34);

            // Floating frosted glass inner lens badge
            var lensBg = new LinearGradientBrush(Color.FromArgb(88, 255, 255, 255), Color.FromArgb(34, 255, 255, 255), 90);
            var lensRim = new LinearGradientBrush(Color.FromArgb(190, 255, 255, 255), Color.FromArgb(50, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(lensBg, new Pen(lensRim, 1.8), new Rect(17, 20, 66, 60), 20, 20);

            // Three balanced crystalline voice-wave bars ("三" + audio listening)
            var barShadow = Solid("#003A99", 0.24);
            var barFill = new LinearGradientBrush(Colors.White, Color.FromArgb(232, 235, 246, 255), 90);
            (double x, double y, double w, double h)[] bars =
            [
                (31, 41, 8.5, 20),
                (45.75, 31, 8.5, 40),
                (60.5, 38, 8.5, 26),
            ];
            foreach (var (x, y, w, h) in bars)
            {
                dc.DrawRoundedRectangle(barShadow, null, new Rect(x, y + 1.8, w, h), w / 2, w / 2);
                dc.DrawRoundedRectangle(barFill, null, new Rect(x, y, w, h), w / 2, w / 2);
            }

            // Subtle AI notes spark at upper-right of the glass lens
            dc.DrawGeometry(Solid("#FFF6C4", 0.96), null, Spark(74, 29, 7.2));

            // Specular outer glass rim
            var outerRim = new LinearGradientBrush(Color.FromArgb(170, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(null, new Pen(outerRim, 2.2), new Rect(1.1, 1.1, 97.8, 97.8), 23, 23);

            if (live)
            {
                // On-air recording indicator: luminous Apple red dot with frosted halo
                dc.DrawEllipse(Solid("#FFFFFF", 0.92), null, new Point(81, 19), 15, 15);
                dc.DrawEllipse(Solid("#FF3B30"), null, new Point(81, 19), 10.5, 10.5);
            }
            dc.Pop();
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static readonly Dictionary<bool, System.Drawing.Icon> TrayIcons = new();

    public static System.Drawing.Icon Tray(bool live)
    {
        if (TrayIcons.TryGetValue(live, out var cached)) return cached;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Mark(32, live)));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(ms);
        var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        TrayIcons[live] = icon;
        return icon;
    }

    /// Writes the mark as a multi-size .ico (16–256px PNG entries) for exe and shortcuts.
    public static void SaveIco(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var pngs = sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Mark(size, false)));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }
}
