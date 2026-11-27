using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace SanqiuTingji.App;

/// Non-intrusive bottom-right corner reminder used only for background auto-update checks.
/// Never steals keyboard focus; dismissing closes it immediately, while choosing to view or update
/// hands off to the centered <see cref="UpdateWindow"/>.
public sealed class UpdateToastWindow : Window
{
    private static UpdateToastWindow? open;

    private readonly AppModel model;
    private readonly Updates updates;
    private readonly TextBlock summaryText;
    private readonly Border card;
    private readonly TranslateTransform slideTransform = new();
    private readonly bool snapshotMode;
    private bool closing;

    /// Shows the bottom-right reminder unless the full UpdateWindow is already open.
    public static void ShowFor(AppModel model, Updates updates)
    {
        if (updates.AvailableVersion == null || UpdateWindow.IsOpen) return;
        if (open != null)
        {
            open.PositionBottomRight();
            return;
        }
        open = new UpdateToastWindow(model, updates, updates.AvailableVersion, null, snapshotMode: false);
        open.Closed += (_, _) => open = null;
        open.Show();
    }

    public static void DismissIfOpen()
    {
        if (open == null) return;
        var w = open;
        open = null;
        try { w.Close(); } catch { }
    }

    private UpdateToastWindow(AppModel model, Updates updates, string next, List<string>? presetNotes, bool snapshotMode)
    {
        this.model = model;
        this.updates = updates;
        this.snapshotMode = snapshotMode;

        UiText.Set(this, "Title", () => L.T("发现新版本"));
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = !snapshotMode;
        SizeToContent = SizeToContent.Height;
        Width = 368;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        var ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x35));
        var bodyInk = new SolidColorBrush(Color.FromRgb(0x3E, 0x45, 0x57));
        var grey = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80));

        // Top row: icon + 发现新版本 + version pill + close button
        var closeIcon = new Canvas { Width = 9, Height = 9 };
        closeIcon.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1.2,1.2 L7.8,7.8 M7.8,1.2 L1.2,7.8"),
            Stroke = grey,
            StrokeThickness = 1.45,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        var closeBtn = UiText.Bind(new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            ToolTip = L.T("关闭"),
            Content = closeIcon,
            VerticalAlignment = VerticalAlignment.Center,
        }, ("ToolTip", () => L.T("关闭")));
        closeBtn.Click += (_, _) => DismissAnimated();

        var versionBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x7A, 0xFF)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"v{next}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                Foreground = Theme.Primary,
            },
        };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new Image
        {
            Source = Icons.Mark(48, false),
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleStack.Children.Add(UiText.Bind(new TextBlock
        {
            Text = L.T("发现新版本"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
        }, ("Text", () => L.T("发现新版本"))));
        titleStack.Children.Add(versionBadge);

        var header = new DockPanel();
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(titleStack);

        // Concise 1-2 line summary
        summaryText = UiText.Bind(new TextBlock
        {
            Text = L.T("新版本已就绪，点击查看更新内容"),
            FontSize = 12,
            Foreground = bodyInk,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 40,
            LineHeight = 19,
            Margin = new Thickness(0, 10, 0, 0),
        }, ("Text", () => L.T("新版本已就绪，点击查看更新内容")));

        // Bottom actions: 查看详情 (left-aligned) | 暂不更新 + 立即更新 (right)
        var detailsBtn = UiText.Bind(new Button
        {
            Content = L.T("查看详情"),
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Foreground = Theme.Primary,
            FontSize = 12,
            Padding = new Thickness(0, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        }, ("Content", () => L.T("查看详情")));
        detailsBtn.Click += (_, _) => OpenFullUpdate(startImmediately: false);

        var dismissBtn = UiText.Bind(new Button
        {
            Content = L.T("暂不更新"),
            Style = (Style)Application.Current.FindResource("Btn"),
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0),
        }, ("Content", () => L.T("暂不更新")));
        dismissBtn.Click += (_, _) => DismissAnimated();

        var updateBtn = UiText.Bind(new Button
        {
            Content = L.T("立即更新"),
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            FontSize = 12,
            Padding = new Thickness(13, 5, 13, 5),
        }, ("Content", () => L.T("立即更新")));
        updateBtn.Click += (_, _) => OpenFullUpdate(startImmediately: true);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightBtns.Children.Add(dismissBtn);
        rightBtns.Children.Add(updateBtn);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(rightBtns, Dock.Right);
        footer.Children.Add(rightBtns);
        footer.Children.Add(detailsBtn);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(summaryText);
        content.Children.Add(footer);

        card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 14, 14),
            Margin = new Thickness(14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE2, 0xEE)),
            BorderThickness = new Thickness(1),
            RenderTransform = slideTransform,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Opacity = 0.16,
                Color = Color.FromRgb(0x1B, 0x14, 0x60),
            },
            Child = content,
        };
        Content = card;

        if (presetNotes != null)
        {
            ApplySummary(presetNotes);
        }
        else if (!snapshotMode)
        {
            Loaded += async (_, _) =>
            {
                PositionBottomRight();
                AnimateIn();
                var lines = await updates.Notes();
                if (!closing) ApplySummary(lines);
            };
            SizeChanged += (_, _) => PositionBottomRight();
        }
    }

    private void ApplySummary(List<string> lines)
    {
        if (lines.Count == 0) return;
        var text = lines.Count == 1 ? lines[0] : $"{lines[0]}；{lines[1]}";
        summaryText.Text = text;
        if (!snapshotMode) PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        if (snapshotMode) return;
        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : 148;
        // Margin inside card is 14px, so 4px extra yields an 18px visual gap from work-area edges.
        Left = area.Right - w - 4;
        Top = area.Bottom - h - 4;
    }

    private void AnimateIn()
    {
        card.Opacity = 0;
        slideTransform.Y = 14;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        slideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void DismissAnimated()
    {
        if (closing) return;
        closing = true;
        if (snapshotMode) { Close(); return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        card.BeginAnimation(OpacityProperty, fade);
        slideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
    }

    private void OpenFullUpdate(bool startImmediately)
    {
        closing = true;
        Close();
        UpdateWindow.ShowFor(model, updates, startImmediately);
    }

    /// Developer snapshot: bottom-right toast with sample notes, not shown.
    public static Window ForSnapshot(AppModel model, Updates updates, string next, List<string> notes) =>
        new UpdateToastWindow(model, updates, next, notes, snapshotMode: true);
}
