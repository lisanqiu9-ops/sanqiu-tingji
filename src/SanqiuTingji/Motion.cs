using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SanqiuTingji.App;

/// Unified motion language for 三秋听记: Apple-inspired restrained durations, GPU-composited
/// transforms/opacity only, interruptible transitions, and automatic reduced-motion compliance.
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static Duration Fast => DurationOf(140);
    public static Duration Standard => DurationOf(220);
    public static Duration Spring => DurationOf(230);
    public static Duration Emphasized => DurationOf(300);

    public static IEasingFunction EaseOut { get; } = CreateCubic(EasingMode.EaseOut);
    public static IEasingFunction EaseInOut { get; } = CreateCubic(EasingMode.EaseInOut);
    public static IEasingFunction SpringSoft { get; } = CreateBack(0.18);
    public static IEasingFunction Sine { get; } = CreateSine();

    private static Duration DurationOf(int ms) => new(TimeSpan.FromMilliseconds(Enabled ? ms : 0));

    private static CubicEase CreateCubic(EasingMode mode)
    {
        var e = new CubicEase { EasingMode = mode };
        e.Freeze();
        return e;
    }

    private static BackEase CreateBack(double amplitude)
    {
        var e = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };
        e.Freeze();
        return e;
    }

    private static SineEase CreateSine()
    {
        var e = new SineEase { EasingMode = EasingMode.EaseInOut };
        e.Freeze();
        return e;
    }

    /// Gentle fade + upward settle for newly revealed content (interruptible without snapping to 0).
    public static void Reveal(UIElement el, double offsetY = 6, int ms = 210)
    {
        if (!Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            if (EnsureTranslate(el) is { } t0) { t0.BeginAnimation(TranslateTransform.YProperty, null); t0.Y = 0; }
            return;
        }
        var d = new Duration(TimeSpan.FromMilliseconds(ms));
        var startOpacity = el.Opacity is > 0.05 and < 0.98 ? el.Opacity : 0.0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(startOpacity, 1, d) { EasingFunction = EaseOut });
        var tx = EnsureTranslate(el);
        tx.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, d) { EasingFunction = EaseOut });
    }

    /// Subtle fade-only transition for switching sessions without shifting layout.
    public static void FadeIn(UIElement el, int ms = 160)
    {
        if (!Enabled) { el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; return; }
        var startOpacity = el.Opacity is > 0.05 and < 0.98 ? el.Opacity : 0.0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(startOpacity, 1, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = EaseOut });
    }

    /// Soft fade-out followed by Visibility.Collapsed (clears HoldEnd clock to prevent locked Opacity).
    public static void FadeOut(UIElement el, int ms = 170)
    {
        if (!Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = Visibility.Collapsed;
            return;
        }
        var anim = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = EaseOut };
        anim.Completed += (_, _) =>
        {
            if (el.Opacity <= 0.02)
            {
                el.Visibility = Visibility.Collapsed;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
            }
        };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// Soft scale + fade entrance for floating panels, dialogs and in-app glass toasts.
    public static void PopIn(FrameworkElement el, double offsetY = -8, double fromScale = 0.96, int ms = 220)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var (scale, tx) = EnsureScaleTranslate(el);
        if (!Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tx.BeginAnimation(TranslateTransform.YProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
            tx.Y = 0;
            return;
        }
        var d = new Duration(TimeSpan.FromMilliseconds(ms));
        var startOpacity = el.Visibility == Visibility.Visible && el.Opacity is > 0.05 and < 0.98 ? el.Opacity : 0.0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(startOpacity, 1, d) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = EaseOut });
        tx.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, d) { EasingFunction = EaseOut });
    }

    /// Moves a liquid-glass pill thumb smoothly across a segmented track without overshooting edges.
    public static void MoveSegmentedThumb(Border thumb, FrameworkElement track, FrameworkElement target, bool animate)
    {
        if (target.ActualWidth <= 0 || track.ActualWidth <= 0) return;
        var pos = target.TranslatePoint(new Point(0, 0), track);
        var tx = EnsureTranslate(thumb);
        thumb.Visibility = Visibility.Visible;
        if (!animate || !Enabled)
        {
            thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
            tx.BeginAnimation(TranslateTransform.XProperty, null);
            thumb.Width = target.ActualWidth;
            thumb.Height = target.ActualHeight;
            tx.X = pos.X;
            return;
        }
        thumb.Height = target.ActualHeight;
        thumb.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(target.ActualWidth, Standard) { EasingFunction = EaseOut });
        tx.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(pos.X, Standard) { EasingFunction = EaseOut });
    }

    private static TranslateTransform EnsureTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform existing && !existing.IsFrozen) return existing;
        var tx = new TranslateTransform();
        el.RenderTransform = tx;
        return tx;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureScaleTranslate(UIElement el)
    {
        if (el.RenderTransform is TransformGroup { Children.Count: 2 } grp
            && grp.Children[0] is ScaleTransform s && !s.IsFrozen
            && grp.Children[1] is TranslateTransform t && !t.IsFrozen)
            return (s, t);
        var scale = new ScaleTransform(1, 1);
        var tx = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(tx);
        el.RenderTransform = group;
        return (scale, tx);
    }
}
