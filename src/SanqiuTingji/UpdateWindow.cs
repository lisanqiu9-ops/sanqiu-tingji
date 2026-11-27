using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SanqiuTingji.App;

/// 发现新版本: the version jump, what's new, and 下次提醒 / 立即更新. Nothing downloads until the
/// user clicks 立即更新; then the progress shows here and the app restarts into the new version.
public sealed class UpdateWindow : Window
{
    private static UpdateWindow? open;

    private readonly AppModel model;
    private readonly Updates updates;
    private readonly StackPanel notesHost = new();
    private readonly Button later;
    private readonly Button now;
    private readonly ProgressBar bar;
    private readonly TextBlock status;
    private readonly System.Windows.Documents.Run targetRun;

    /// Shows the box for the available version (brings it back if it's already open).
    public static void ShowFor(AppModel model, Updates updates)
    {
        if (updates.AvailableVersion == null) return;
        if (open != null)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        open = new UpdateWindow(model, updates, updates.AvailableVersion, null);
        open.Closed += (_, _) => open = null;
        open.Show();
        open.Activate();
    }

    private UpdateWindow(AppModel model, Updates updates, string next, List<string>? presetNotes)
    {
        this.model = model;
        this.updates = updates;
        UiText.Set(this, "Title", () => L.T("发现新版本"));
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Icon = Icons.Mark(64, false);

        var ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x35));
        var grey = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80));

        // Title row: mark, 发现新版本, minimize, close.
        var closeIcon = new Canvas { Width = 10, Height = 10 };
        closeIcon.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1.5,1.5 L8.5,8.5 M8.5,1.5 L1.5,8.5"),
            Stroke = grey, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var close = UiText.Bind(new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Width = 28, Height = 26, Padding = new Thickness(0), ToolTip = L.T("下次提醒"),
            Content = closeIcon,
        }, ("ToolTip", () => L.T("下次提醒")));
        close.Click += (_, _) => Close();

        var minIcon = new Canvas { Width = 10, Height = 10 };
        minIcon.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,5.5 L9,5.5"),
            Stroke = grey, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var minimize = UiText.Bind(new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
            ToolTip = L.T("最小化"),
            Content = minIcon,
        }, ("ToolTip", () => L.T("最小化")));
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;

        var winControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        winControls.Children.Add(minimize);
        winControls.Children.Add(close);

        var titleRow = new DockPanel();
        DockPanel.SetDock(winControls, Dock.Right);
        titleRow.Children.Add(winControls);
        titleRow.Children.Add(new Image { Source = Icons.Mark(64, false), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        titleRow.Children.Add(UiText.Bind(new TextBlock { Text = L.T("发现新版本"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center }, ("Text", () => L.T("发现新版本"))));

        // v0.1.4 ▷ v0.1.5
        var jump = new TextBlock { FontSize = 13, Margin = new Thickness(0, 16, 0, 0), FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") };
        jump.Inlines.Add(new System.Windows.Documents.Run($"v{updates.Version}") { Foreground = grey });
        jump.Inlines.Add(new System.Windows.Documents.Run("  ▷  ") { Foreground = grey });
        targetRun = new System.Windows.Documents.Run($"v{next}") { Foreground = Theme.Primary, FontWeight = FontWeights.Bold };
        jump.Inlines.Add(targetRun);

        notesHost.Margin = new Thickness(0, 2, 8, 2);
        notesHost.Children.Add(UiText.Bind(new TextBlock { Text = L.T("正在读取更新内容…"), Foreground = grey, FontSize = 13 }, ("Text", () => L.T("正在读取更新内容…"))));
        var notes = new ScrollViewer
        {
            Content = notesHost,
            MaxHeight = 260,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed, Foreground = Theme.Primary };
        status = new TextBlock { FontSize = 12, Foreground = grey, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (model.IsListening) UiText.Set(status, "Text", () => L.T("正在记录会议：更新会先结束本场会议（记录会保存），再重启三秋听记。"));

        later = UiText.Bind(new Button { Content = L.T("下次提醒"), Style = (Style)Application.Current.FindResource("Btn"), Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0, 0, 10, 0), IsCancel = true }, ("Content", () => L.T("下次提醒")));
        later.Click += (_, _) => Close();
        now = UiText.Bind(new Button { Content = L.T("立即更新"), Style = (Style)Application.Current.FindResource("PrimaryBtn"), Padding = new Thickness(20, 7, 20, 7), IsDefault = true }, ("Content", () => L.T("立即更新")));
        now.Click += async (_, _) => await UpdateNow();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(later);
        buttons.Children.Add(now);

        var body = new StackPanel();
        body.Children.Add(titleRow);
        body.Children.Add(jump);
        body.Children.Add(notes);
        body.Children.Add(bar);
        body.Children.Add(status);
        body.Children.Add(buttons);
        Content = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 18, 22, 20), Margin = new Thickness(16),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEE)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.18, Color = Color.FromRgb(0x1B, 0x14, 0x60) },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        if (presetNotes != null) ShowNotes(presetNotes);
        else Loaded += async (_, _) => ShowNotes(await updates.Notes());
    }

    private void ShowNotes(List<string> lines)
    {
        notesHost.Children.Clear();
        if (lines.Count == 0)
        {
            notesHost.Children.Add(UiText.Bind(new TextBlock { Text = L.T("改进了使用体验，修复了一些问题。"), FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4E)) }, ("Text", () => L.T("改进了使用体验，修复了一些问题。"))));
            return;
        }
        foreach (var line in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dot = new TextBlock { Text = "•", Foreground = Theme.Primary, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = line, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4E)),
            });
            notesHost.Children.Add(row);
        }
    }

    private async Task UpdateNow()
    {
        later.IsEnabled = now.IsEnabled = false;
        UiText.Set(now, "Content", () => L.T("下载中…"));
        bar.Visibility = Visibility.Visible;
        UiText.Set(status, "Text", () => L.T("正在确认最新版本…"));
        var beforeVer = updates.AvailableVersion;
        await updates.Check();
        if (updates.AvailableVersion is { } latestVer && latestVer != beforeVer)
        {
            targetRun.Text = $"v{latestVer}";
            ShowNotes(await updates.Notes());
        }
        UiText.Set(status, "Text", () => L.T("正在下载新版本… 0%"));
        var ok = await updates.Download(p => Dispatcher.BeginInvoke(() =>
        {
            bar.Value = p;
            UiText.Set(status, "Text", () => L.F("正在下载新版本… {0}%", p));
        }));
        if (!ok)
        {
            bar.Visibility = Visibility.Collapsed;
            UiText.Set(status, "Text", () => L.T("下载失败，请检查网络后再试。"));
            UiText.Set(now, "Content", () => L.T("重试"));
            later.IsEnabled = now.IsEnabled = true;
            return;
        }
        bar.Value = 100;
        UiText.Set(status, "Text", () => L.T("下载完成，正在安装，三秋听记会自动重新打开…"));
        await Task.Delay(600);
        ((App)Application.Current).RestartToUpdate();
    }

    /// Developer snapshot: the box with sample notes, not shown.
    public static Window ForSnapshot(AppModel model, Updates updates, string next, List<string> notes) =>
        new UpdateWindow(model, updates, next, notes);
}
