using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SanqiuTingji.App;

public partial class MainWindow
{
    private void InitNotices()
    {
        model.Notices.Changed += OnNoticesOrUpdatesChanged;
        App.Updates.Changed += OnNoticesOrUpdatesChanged;
        RefreshBellBadge();
    }

    private void DisposeNotices()
    {
        model.Notices.Changed -= OnNoticesOrUpdatesChanged;
        App.Updates.Changed -= OnNoticesOrUpdatesChanged;
    }

    private void OnNoticesOrUpdatesChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshBellBadge();
            if (NoticePopup.IsOpen) RenderNoticePopover();
        });
    }

    private void RefreshBellBadge()
    {
        if (BellDot == null) return;
        var hasUpdate = App.Updates.AvailableVersion != null;
        var unread = model.Notices.UnreadCount;
        BellDot.Visibility = hasUpdate || unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellBtn.ToolTip = hasUpdate
            ? $"发现新版本 v{App.Updates.AvailableVersion}"
            : unread > 0 ? $"消息与更新（{unread} 条未读）" : "消息与更新";
    }

    private void Bell_Click(object sender, RoutedEventArgs e)
    {
        if (NoticePopup.IsOpen)
        {
            NoticePopup.IsOpen = false;
            return;
        }
        RenderNoticePopover();
        NoticePopup.IsOpen = true;
        _ = model.Notices.RefreshAsync();
    }

    private void NoticePopup_Closed(object? sender, EventArgs e)
    {
        // Once the user has opened and dismissed the popover, mark currently visible announcements read.
        // An available software update keeps the red dot lit until installed.
        if (model.Notices.UnreadCount > 0)
            model.Notices.MarkAllRead();
        RefreshBellBadge();
    }

    private void NoticeMarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        model.Notices.MarkAllRead();
        RenderNoticePopover();
        RefreshBellBadge();
    }

    private async void NoticeCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        NoticeCheckBtn.IsEnabled = false;
        NoticeCheckBtn.Content = "检查中…";
        try
        {
            var found = await App.Updates.Check();
            _ = model.Notices.RefreshAsync();
            RenderNoticePopover();
            RefreshBellBadge();
            if (found)
            {
                NoticePopup.IsOpen = false;
                UpdateWindow.ShowFor(model, App.Updates);
            }
            else if (!string.IsNullOrWhiteSpace(App.Updates.Status))
            {
                ShowGlassToast(App.Updates.Status);
            }
        }
        finally
        {
            NoticeCheckBtn.IsEnabled = true;
            NoticeCheckBtn.Content = "检查更新";
        }
    }

    private void RenderNoticePopover()
    {
        NoticeListHost.Children.Clear();
        var updates = App.Updates;
        var hasUpdate = updates.AvailableVersion != null;
        var items = model.Notices.Active;
        NoticeMarkAllBtn.Visibility = model.Notices.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 1. Pinned update card when a newer version is available
        if (hasUpdate)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7A, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var dock = new DockPanel();
            var updateBtn = new Button
            {
                Content = "立即更新",
                Style = (Style)FindResource("PrimaryBtn"),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            updateBtn.Click += (_, _) =>
            {
                NoticePopup.IsOpen = false;
                UpdateWindow.ShowFor(model, updates);
            };
            DockPanel.SetDock(updateBtn, Dock.Right);
            dock.Children.Add(updateBtn);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = $"发现新版本 v{updates.AvailableVersion}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Primary,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"当前版本 v{updates.Version}，点击查看更新内容并一键升级",
                FontSize = 11.5,
                Foreground = Theme.Grey,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
            dock.Children.Add(info);
            card.Child = dock;
            NoticeListHost.Children.Add(card);
        }

        // 2. Announcement cards
        foreach (var notice in items)
        {
            var unread = !model.Notices.IsRead(notice.Id);
            var isImportant = string.Equals(notice.Level, "important", StringComparison.OrdinalIgnoreCase);
            var card = new Border
            {
                Background = unread
                    ? new SolidColorBrush(Color.FromRgb(0xF6, 0xF9, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                BorderBrush = isImportant
                    ? new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0x7A, 0x00))
                    : new SolidColorBrush(Color.FromArgb(0x18, 0x16, 0x28, 0x4C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var stack = new StackPanel();
            var top = new DockPanel();
            if (!string.IsNullOrWhiteSpace(notice.Date))
            {
                var dateBlock = new TextBlock
                {
                    Text = notice.Date,
                    FontSize = 10.5,
                    Foreground = Theme.Grey2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(dateBlock, Dock.Right);
                top.Children.Add(dateBlock);
            }
            var titleWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (unread)
            {
                titleWrap.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = isImportant ? Theme.Warn : Theme.Primary,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            titleWrap.Children.Add(new TextBlock
            {
                Text = notice.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                TextWrapping = TextWrapping.Wrap,
            });
            top.Children.Add(titleWrap);
            stack.Children.Add(top);

            if (!string.IsNullOrWhiteSpace(notice.Body))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = notice.Body,
                    FontSize = 11.5,
                    Foreground = Theme.Grey,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Margin = new Thickness(0, 5, 0, 0),
                });
            }

            if (!string.IsNullOrWhiteSpace(notice.Url))
            {
                var captured = notice;
                var linkBtn = new Button
                {
                    Content = "查看详情 ↗",
                    Style = (Style)FindResource("GhostBtn"),
                    Foreground = Theme.Primary,
                    FontSize = 11.5,
                    Padding = new Thickness(0, 4, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                linkBtn.Click += (_, _) =>
                {
                    model.Notices.MarkRead(captured.Id);
                    NoticePopup.IsOpen = false;
                    Native.Open(captured.Url!);
                };
                stack.Children.Add(linkBtn);
            }

            card.Child = stack;
            NoticeListHost.Children.Add(card);
        }

        // 3. Clean empty state when up-to-date and no notices
        if (!hasUpdate && items.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 14, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock
            {
                Text = $"✓ 当前已是最新版 v{updates.Version}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            empty.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(updates.Status) ? "暂无新公告，有新版或重要提醒时小铃铛会亮红点提示" : updates.Status,
                FontSize = 11.5,
                Foreground = Theme.Grey,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 5, 8, 0),
            });
            NoticeListHost.Children.Add(empty);
        }
    }
}
