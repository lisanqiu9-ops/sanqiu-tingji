using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SanqiuTingji.App;

/// The app's own confirmation box, in its look (the system MessageBox is grey and dated): a card with
/// an icon, a title, one or two lines of explanation, and two buttons. Esc cancels, Enter confirms.
public sealed class Dialog : Window
{
    private bool confirmed;

    /// Asks a yes/no question over `owner`. `danger`: the action can't be undone (red button).
    public static bool Confirm(Window? owner, string title, string message, string confirm = "确定", bool danger = false)
    {
        var d = new Dialog(title, message, confirm, danger) { Owner = owner?.IsVisible == true ? owner : null };
        if (d.Owner == null) d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d.confirmed;
    }

    private Dialog(string title, string message, string confirm, bool danger)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Title = title;

        var accent = danger ? Color.FromRgb(0xFF, 0x3B, 0x30) : Color.FromRgb(0x00, 0x7A, 0xFF);
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B)),
            Child = new TextBlock
            {
                Text = danger ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18, Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), Width = 300 };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x35)),
        });
        text.Children.Add(new TextBlock
        {
            Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B)),
        });

        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var ok = new Button
        {
            Content = confirm, Style = (Style)Application.Current.FindResource(danger ? "DangerBtn" : "PrimaryBtn"),
            Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
        };
        ok.Click += (_, _) => { confirmed = true; Close(); };
        var cancel = UiText.Bind(new Button
        {
            Content = L.T("取消"), Style = (Style)Application.Current.FindResource("Btn"), Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(0, 0, 8, 0), IsCancel = true,
        }, ("Content", () => L.T("取消")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(buttons);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(18), Padding = new Thickness(24, 22, 24, 20), Margin = new Thickness(18),
            BorderBrush = Theme.GlassRim, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.22, Color = Color.FromRgb(0x0C, 0x19, 0x33) },
            Child = body,
        };
        Content = card;
        // Drag it by the card, like any window.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) =>
        {
            Motion.PopIn(card, offsetY: 6, fromScale: 0.96, ms: 180);
            ok.Focus();
        };
    }

    /// Developer snapshot: the box as it would open.
    public static Window ForSnapshot(string title, string message, string confirm, bool danger) => new Dialog(title, message, confirm, danger);
}
