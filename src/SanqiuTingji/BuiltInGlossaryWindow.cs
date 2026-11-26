using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SanqiuTingji.App;

/// Picker dialog for one-click importing curated industry and dialect glossary packs.
public sealed class BuiltInGlossaryWindow : Window
{
    private readonly List<(CheckBox Box, BuiltInGlossaryPack Pack)> rows = [];
    private bool confirmed;

    public static IReadOnlyList<string>? Ask(Window owner, Glossary glossary)
    {
        var w = new BuiltInGlossaryWindow(glossary) { Owner = owner };
        w.ShowDialog();
        if (!w.confirmed) return null;
        return w.rows.Where(r => r.Box.IsChecked == true).Select(r => r.Pack.Id).ToList();
    }

    private BuiltInGlossaryWindow(Glossary glossary)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Title = "内置行业与方言词包";

        var body = new StackPanel { Width = 470 };
        body.Children.Add(new TextBlock
        {
            Text = "内置行业与方言词包",
            FontSize = 16.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Ink,
        });
        body.Children.Add(new TextBlock
        {
            Text = "勾选后按分类一键导入词库。重复导入只补充新词或新误听写法，不会覆盖你已修改的内容；导入后可在左侧分类按会议模板启用或随时删除。",
            FontSize = 12.5,
            Foreground = Theme.Grey,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14),
            LineHeight = 18,
        });

        var packs = BuiltInGlossaries.All;
        var anyMissing = packs.Any(p => BuiltInGlossaries.CountInstalled(glossary, p) < p.TermCount);

        var list = new StackPanel();
        foreach (var pack in packs)
        {
            var have = BuiltInGlossaries.CountInstalled(glossary, pack);
            var full = have >= pack.TermCount;
            var status = full ? $"已导入 ({pack.TermCount} 条)"
                : have > 0 ? $"已含 {have}/{pack.TermCount} 条"
                : $"{pack.TermCount} 条";

            var box = new CheckBox
            {
                IsChecked = anyMissing ? !full : false,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 10, 0),
            };
            rows.Add((box, pack));

            var header = new DockPanel();
            var badge = new Border
            {
                Background = new SolidColorBrush(full ? Color.FromArgb(0x20, 0x22, 0xB0, 0x7D) : Color.FromArgb(0x16, 0x00, 0x7A, 0xFF)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = status,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = full ? new SolidColorBrush(Color.FromRgb(0x16, 0x7E, 0x45)) : Theme.Primary,
                },
            };
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);
            header.Children.Add(new TextBlock
            {
                Text = pack.Name,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var info = new StackPanel();
            info.Children.Add(header);
            info.Children.Add(new TextBlock
            {
                Text = $"分类「{pack.Category}」· {pack.Description}",
                FontSize = 11.5,
                Foreground = Theme.Grey,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                LineHeight = 16.5,
            });

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(info, 1);
            rowGrid.Children.Add(box);
            rowGrid.Children.Add(info);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xF7, 0xF9, 0xFC)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0x16, 0x28, 0x4C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = rowGrid,
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is CheckBox) return;
                box.IsChecked = !(box.IsChecked == true);
            };
            list.Children.Add(card);
        }

        body.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var importBtn = new Button
        {
            Content = "立即导入",
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Padding = new Thickness(18, 6, 18, 6),
            IsDefault = true,
            IsEnabled = rows.Any(r => r.Box.IsChecked == true),
        };
        foreach (var (box, _) in rows)
        {
            box.Checked += (_, _) => importBtn.IsEnabled = rows.Any(r => r.Box.IsChecked == true);
            box.Unchecked += (_, _) => importBtn.IsEnabled = rows.Any(r => r.Box.IsChecked == true);
        }
        importBtn.Click += (_, _) => { confirmed = true; Close(); };

        var cancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.FindResource("Btn"),
            Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(importBtn);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22, 20, 22, 18),
            Margin = new Thickness(16),
            BorderBrush = Theme.GlassRim,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.20, Color = Color.FromRgb(0x0C, 0x19, 0x33) },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }
}
