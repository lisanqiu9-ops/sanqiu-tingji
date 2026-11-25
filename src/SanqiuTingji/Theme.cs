using System.Windows;
using System.Windows.Media;

namespace SanqiuTingji.App;

/// Apple Liquid Glass color & material tokens for code-built views (kept in sync with App.xaml).
public static class Theme
{
    public static readonly Brush Ink = Frozen(0x14, 0x18, 0x24);
    public static readonly Brush Primary = Frozen(0x00, 0x7A, 0xFF);
    public static readonly Brush PrimarySoft = FrozenA(0x1E, 0x00, 0x7A, 0xFF);
    public static readonly Brush OnAir = Frozen(0x28, 0xCD, 0x41);
    public static readonly Brush Warn = Frozen(0xFF, 0x95, 0x00);
    public static readonly Brush Danger = Frozen(0xFF, 0x3B, 0x30);
    public static readonly Brush Grey = Frozen(0x5B, 0x64, 0x76);
    public static readonly Brush Grey2 = Frozen(0x8A, 0x93, 0xA6);
    public static readonly Brush Line = FrozenA(0x22, 0x16, 0x28, 0x4C);
    public static readonly Brush Surface = FrozenA(0xE0, 0xFF, 0xFF, 0xFF);
    public static readonly Brush SurfaceSolid = Frozen(0xFF, 0xFF, 0xFF);
    public static readonly Brush GlassCard = FrozenA(0xD8, 0xFF, 0xFF, 0xFF);
    public static readonly Brush GlassOverview = CreateOverviewGradient();
    public static readonly Brush GlassRim = CreateSpecularRim();
    public static readonly Brush GlassWindowFallback = CreateWindowFallback();
    public static readonly Brush NightFallback = Frozen(0x14, 0x18, 0x24);
    public static readonly Brush NightText = Frozen(0xF2, 0xF5, 0xFC);
    public static readonly Brush NightMuted = Frozen(0x98, 0xA2, 0xB8);
    public static readonly Brush NightRaised = FrozenA(0x28, 0xFF, 0xFF, 0xFF);

    /// Speaker colors: Apple HIG vivid system palette, readable on both light glass and dark glass.
    private static readonly Brush[] Speakers =
    [
        Frozen(0x00, 0x7A, 0xFF), Frozen(0x28, 0xA7, 0x6F), Frozen(0xAF, 0x52, 0xDE),
        Frozen(0xFF, 0x95, 0x00), Frozen(0x32, 0xAD, 0xE6), Frozen(0xFF, 0x2D, 0x55),
    ];

    public static Brush Speaker(int number) => Speakers[(number - 1) % Speakers.Length];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    private static Brush FrozenA(byte a, byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    private static Brush CreateSpecularRim()
    {
        var b = new LinearGradientBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x36, 0x8F, 0xA2, 0xC4), 90);
        b.Freeze();
        return b;
    }

    private static Brush CreateOverviewGradient()
    {
        var b = new LinearGradientBrush(
            Color.FromArgb(0xDC, 0xEB, 0xF5, 0xFF),
            Color.FromArgb(0xD4, 0xF5, 0xF9, 0xFF),
            new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    private static Brush CreateWindowFallback()
    {
        var b = new LinearGradientBrush(
            Color.FromRgb(0xEB, 0xF0, 0xF8),
            Color.FromRgb(0xF6, 0xF8, 0xFC),
            new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }
}
