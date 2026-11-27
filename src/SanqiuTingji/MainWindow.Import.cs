using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SanqiuTingji.App;

public partial class MainWindow
{
    private string? queuePopupStructureKey;
    private ProgressBar? popupActiveBar;
    private TextBlock? popupActiveDetail;

    private void InitMediaImport()
    {
        model.Imports.Changed += OnImportProgressChanged;
        model.Imports.Imported += OnMediaImported;
        model.Imports.Failed += OnMediaImportFailed;
        OnImportProgressChanged(model.Imports.Snapshot);
    }

    private void DisposeMediaImport()
    {
        model.Imports.Changed -= OnImportProgressChanged;
        model.Imports.Imported -= OnMediaImported;
        model.Imports.Failed -= OnMediaImportFailed;
    }

    private void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        if (ImportQueuePopup != null) ImportQueuePopup.IsOpen = false;
        var window = new MediaImportWindow(model) { Owner = this };
        window.ShowDialog();
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        model.Imports.CancelCurrent();
    }

    private void ImportTaskCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (ImportQueuePopup == null) return;
        if (ImportQueuePopup.IsOpen)
        {
            ImportQueuePopup.IsOpen = false;
            return;
        }
        queuePopupStructureKey = null;
        RenderImportQueuePopover(model.Imports.Snapshot);
        ImportQueuePopup.IsOpen = true;
    }

    private void ImportQueuePopup_Opened(object? sender, EventArgs e)
    {
        if (ImportQueueHintState != null) ImportQueueHintState.Text = "已展开";
    }

    private void ImportQueuePopup_Closed(object? sender, EventArgs e)
    {
        if (ImportQueueHintState != null) ImportQueueHintState.Text = "展开 ▸";
        popupActiveBar = null;
        popupActiveDetail = null;
    }

    private void ClearPendingImports_Click(object sender, RoutedEventArgs e)
    {
        var n = model.Imports.ClearPending();
        if (n > 0) ShowGlassToast($"已清空 {n} 个排队中的导入任务");
    }

    private void OnImportProgressChanged(MediaImportSnapshot snap)
    {
        if (ImportTaskCard == null) return;
        if (!snap.IsActive)
        {
            ImportTaskCard.Visibility = Visibility.Collapsed;
            if (ImportQueuePopup?.IsOpen == true && snap.RecentCompleted.Count == 0)
                ImportQueuePopup.IsOpen = false;
            else if (ImportQueuePopup?.IsOpen == true)
                RenderImportQueuePopover(snap);
            return;
        }
        ImportTaskCard.Visibility = Visibility.Visible;
        ImportTaskTitle.Text = snap.CurrentName;
        ImportTaskStage.Text = snap.StageText;
        ImportTaskDetail.Text = snap.DetailText;
        ImportTaskDetail.Visibility = string.IsNullOrWhiteSpace(snap.DetailText) ? Visibility.Collapsed : Visibility.Visible;
        ImportTaskBar.IsIndeterminate = snap.IsIndeterminate;
        if (!snap.IsIndeterminate) ImportTaskBar.Value = snap.Fraction;
        if (snap.PendingCount > 0)
        {
            ImportQueueBadgeText.Text = $"队列 +{snap.PendingCount} ▸";
            ImportQueueBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ImportQueueBadge.Visibility = Visibility.Collapsed;
        }

        if (ImportQueuePopup?.IsOpen == true)
            RenderImportQueuePopover(snap);
    }

    private void RenderImportQueuePopover(MediaImportSnapshot snap)
    {
        if (ImportQueueListHost == null) return;
        var activeCount = snap.IsActive ? 1 : 0;
        ImportQueueSummaryText.Text = snap.RecentCompleted.Count > 0
            ? $"进行中 {activeCount} · 排队中 {snap.PendingCount} · 本次已完成 {snap.RecentCompleted.Count}"
            : $"进行中 {activeCount} · 排队中 {snap.PendingCount}";
        ImportClearPendingBtn.Visibility = snap.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Only rebuild visual cards when queue membership/order changes so buttons stay easy to click.
        var structKey = string.Join("|",
            snap.IsActive,
            snap.CurrentJobId,
            snap.CurrentName,
            string.Join(",", snap.PendingItems.Select(p => p.JobId)),
            string.Join(",", snap.RecentCompleted.Select(c => c.SessionId)));

        if (structKey == queuePopupStructureKey && popupActiveBar != null && popupActiveDetail != null)
        {
            popupActiveBar.IsIndeterminate = snap.IsIndeterminate;
            if (!snap.IsIndeterminate) popupActiveBar.Value = snap.Fraction;
            popupActiveDetail.Text = string.IsNullOrWhiteSpace(snap.DetailText)
                ? snap.StageText
                : $"{snap.StageText} · {snap.DetailText}";
            return;
        }

        queuePopupStructureKey = structKey;
        popupActiveBar = null;
        popupActiveDetail = null;
        ImportQueueListHost.Children.Clear();

        // 1. Currently running job
        if (snap.IsActive)
        {
            ImportQueueListHost.Children.Add(MakeQueueSectionLabel("正在识别"));
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xB7, 0xD7, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11, 10, 11, 10),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var stack = new StackPanel();
            var top = new DockPanel();
            var stopBtn = new Button
            {
                Content = "停止",
                Style = (Style)FindResource("Btn"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0x3B, 0x3B)),
                Padding = new Thickness(9, 2, 9, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            stopBtn.Click += (_, _) => model.Imports.CancelCurrent();
            DockPanel.SetDock(stopBtn, Dock.Right);
            top.Children.Add(stopBtn);
            top.Children.Add(new TextBlock
            {
                Text = snap.CurrentName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(top);

            var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            tags.Children.Add(MakeQueueTag(snap.CurrentDistinguishVoices ? "区分发言人" : "快速模式", primary: snap.CurrentDistinguishVoices));
            if (!string.IsNullOrWhiteSpace(snap.CurrentTemplateId))
            {
                var tpl = NoteTemplates.Find(snap.CurrentTemplateId, model.MyTemplates);
                tags.Children.Add(MakeQueueTag(tpl.Name, primary: false));
            }
            if (snap.CurrentAutoNotes) tags.Children.Add(MakeQueueTag("自动出纪要", primary: true));
            if (snap.CurrentRecordedStartUtc is { } rec)
                tags.Children.Add(MakeQueueTag($"原时间 {rec.ToLocalTime():MM-dd HH:mm}", primary: false));
            stack.Children.Add(tags);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Height = 5,
                Margin = new Thickness(0, 8, 0, 0),
                IsIndeterminate = snap.IsIndeterminate,
                Value = snap.IsIndeterminate ? 0 : snap.Fraction,
            };
            var detail = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(snap.DetailText) ? snap.StageText : $"{snap.StageText} · {snap.DetailText}",
                FontSize = 10.5,
                Foreground = Theme.Grey,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            popupActiveBar = bar;
            popupActiveDetail = detail;
            stack.Children.Add(bar);
            stack.Children.Add(detail);
            card.Child = stack;
            ImportQueueListHost.Children.Add(card);
        }

        // 2. Pending jobs
        if (snap.PendingItems.Count > 0)
        {
            ImportQueueListHost.Children.Add(MakeQueueSectionLabel($"排队等待中 ({snap.PendingItems.Count})"));
            for (var i = 0; i < snap.PendingItems.Count; i++)
            {
                var item = snap.PendingItems[i];
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF6)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(9, 8, 9, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var dock = new DockPanel();

                var ops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                if (i > 0)
                {
                    var topBtn = new Button
                    {
                        Content = "↑ 优先",
                        Style = (Style)FindResource("Btn"),
                        FontSize = 10.5,
                        Padding = new Thickness(7, 2.5, 7, 2.5),
                        Margin = new Thickness(0, 0, 4, 0),
                        ToolTip = "调整到下一个识别",
                    };
                    var capturedId = item.JobId;
                    topBtn.Click += (_, _) => model.Imports.PrioritizePending(capturedId);
                    ops.Children.Add(topBtn);
                }
                var delBtn = new Button
                {
                    Content = "移除",
                    Style = (Style)FindResource("Btn"),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0x3B, 0x3B)),
                    Padding = new Thickness(7, 2.5, 7, 2.5),
                    ToolTip = "从等待队列中移除",
                };
                var removeId = item.JobId;
                delBtn.Click += (_, _) => model.Imports.RemovePending(removeId);
                ops.Children.Add(delBtn);
                DockPanel.SetDock(ops, Dock.Right);
                dock.Children.Add(ops);

                var idxBadge = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xEF, 0xF8)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 10.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0x5D, 0x78)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                DockPanel.SetDock(idxBadge, Dock.Left);
                dock.Children.Add(idxBadge);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = item.DisplayName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Ink,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var metaWrap = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
                if (!string.IsNullOrEmpty(item.DurationLabel))
                    metaWrap.Children.Add(new TextBlock { Text = item.DurationLabel, FontSize = 10.5, Foreground = Theme.Grey, Margin = new Thickness(0, 1, 6, 0) });
                metaWrap.Children.Add(MakeQueueTag(item.DistinguishVoices ? "区分发言人" : "快速模式", primary: item.DistinguishVoices));
                if (item.AutoGenerateNotes) metaWrap.Children.Add(MakeQueueTag("自动出纪要", primary: true));
                info.Children.Add(metaWrap);
                dock.Children.Add(info);

                row.Child = dock;
                ImportQueueListHost.Children.Add(row);
            }
        }
        else if (snap.IsActive)
        {
            ImportQueueListHost.Children.Add(new TextBlock
            {
                Text = "当前没有其他排队任务，可继续拖入文件或点右上角「＋ 继续添加」。",
                FontSize = 11.5,
                Foreground = Theme.Grey2,
                Margin = new Thickness(2, 0, 2, 10),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // 3. Recently completed items in this session
        if (snap.RecentCompleted.Count > 0)
        {
            ImportQueueListHost.Children.Add(MakeQueueSectionLabel("本次已完成"));
            foreach (var done in snap.RecentCompleted)
            {
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xFB, 0xF6)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0xF0, 0xDA)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 7, 8, 7),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var dock = new DockPanel();
                var openBtn = new Button
                {
                    Content = "查看 ↗",
                    Style = (Style)FindResource("GhostBtn"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Primary,
                    Padding = new Thickness(8, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var sessionId = done.SessionId;
                openBtn.Click += (_, _) =>
                {
                    ImportQueuePopup.IsOpen = false;
                    OpenImportedSession(sessionId);
                };
                DockPanel.SetDock(openBtn, Dock.Right);
                dock.Children.Add(openBtn);

                var check = new Border
                {
                    Width = 17,
                    Height = 17,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromRgb(0x28, 0xCD, 0x41)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "✓",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                DockPanel.SetDock(check, Dock.Left);
                dock.Children.Add(check);

                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = done.Title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x26)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = $"共 {done.LineCount} 句 · {done.FinishedAtUtc.ToLocalTime():HH:mm} 完成",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x78, 0x5B)),
                });
                dock.Children.Add(stack);
                row.Child = dock;
                ImportQueueListHost.Children.Add(row);
            }
        }
    }

    private static TextBlock MakeQueueSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeights.Bold,
        Foreground = Theme.Grey2,
        Margin = new Thickness(2, 2, 0, 6),
    };

    private static Border MakeQueueTag(string text, bool primary) => new()
    {
        Background = primary
            ? new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x7A, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF8)),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        Margin = new Thickness(0, 0, 5, 2),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary ? Theme.Primary : new SolidColorBrush(Color.FromRgb(0x5C, 0x68, 0x80)),
        },
    };

    private void OnMediaImported(string id, string title, int lineCount, bool autoOpen)
    {
        if (autoOpen)
        {
            OpenImportedSession(id);
            ShowGlassToast($"已导入「{title}」· 共 {lineCount} 句（原声录音后台保存中）");
        }
        else
        {
            // Keep the user's current meeting view undisturbed when subsequent queued items finish.
            RefreshList();
            ShowGlassToast($"后台已完成导入「{title}」· 共 {lineCount} 句（可在队列中点查看）");
        }
    }

    private void OpenImportedSession(string id)
    {
        if (model.Store.Get(id) == null) return;
        searchTimer.Stop();
        refreshingList = true;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        archiveScope = 0;
        if (ActiveScope.IsChecked != true) ActiveScope.IsChecked = true;
        refreshingList = false;

        var targetGroup = model.Store.GroupOf(id) ?? "";
        foreach (var group in treeGroups)
            if (group.Id == targetGroup) group.IsExpanded = true;
        ShowDetail(id, keepTab: false, forceSwitch: true);
        RefreshList();
        RevealRow(id);
    }

    /// Scrolls the library to a meeting's row once its container exists: an imported recording
    /// may land anywhere in the list depending on sort mode and group.
    private void RevealRow(string id)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            foreach (var group in treeGroups)
            {
                if (group.Children.FirstOrDefault(r => r.Id == id) is not { } row) continue;
                if (SessionList.ItemContainerGenerator.ContainerFromItem(group) is not System.Windows.Controls.TreeViewItem groupItem) return;
                groupItem.IsExpanded = true;
                groupItem.UpdateLayout();
                if (groupItem.ItemContainerGenerator.ContainerFromItem(row) is System.Windows.Controls.TreeViewItem rowItem)
                    rowItem.BringIntoView();
                return;
            }
        });
    }

    private void OnMediaImportFailed(string message)
    {
        ShowGlassToast(message);
    }

    // MARK: Whole-window Drag & Drop Import

    private void MainWindow_DragEnter(object sender, DragEventArgs e) => HandleMainDrag(e);

    private void MainWindow_DragOver(object sender, DragEventArgs e) => HandleMainDrag(e);

    private void MainWindow_DragLeave(object sender, DragEventArgs e)
    {
        // Hide overlay only when cursor leaves the outer window bounds.
        var pt = e.GetPosition(this);
        if (pt.X <= 1 || pt.Y <= 1 || pt.X >= ActualWidth - 1 || pt.Y >= ActualHeight - 1)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void HandleMainDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var raw = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var valid = MediaImportQueue.FilterSupportedFiles(raw);
        if (valid.Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlayTitle.Text = valid.Count == 1
                ? $"松开导入「{Path.GetFileName(valid[0])}」"
                : $"松开批量导入 {valid.Count} 个音视频文件";
            DropOverlaySub.Text = model.Settings.SkipDropImportConfirm
                ? "松开后直接按默认配置进入后台队列 · 左侧任务卡可查看与管理全部队列"
                : "松开后弹出轻量参数确认卡（区分发言人 / 分组 / 模板），按 Enter 快速入队";
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlayTitle.Text = "暂不支持此文件格式";
            DropOverlaySub.Text = "请拖入 WAV、MP3、M4A、MP4、FLAC、MOV、MKV、WEBM 等音视频文件";
            DropOverlay.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        var raw = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var valid = MediaImportQueue.FilterSupportedFiles(raw);
        if (valid.Count == 0)
        {
            ShowGlassToast("未检测到可导入的音视频文件（支持 WAV / MP3 / M4A / MP4 / FLAC 等）");
            return;
        }

        IReadOnlyList<string> filesToQueue = valid;
        var options = new MediaImportOptions(model.Settings.SpeakerVoices, null, model.Settings.DefaultTemplateId, false);
        if (!model.Settings.SkipDropImportConfirm)
        {
            var confirm = new MediaImportConfirmWindow(model, valid) { Owner = this };
            if (confirm.ShowDialog() != true) return;
            filesToQueue = confirm.SelectedFiles;
            options = confirm.SelectedOptions;
            if (confirm.SkipFutureConfirm)
            {
                model.Settings.SkipDropImportConfirm = true;
                model.SaveSettings();
            }
        }

        var count = model.Imports.EnqueueFiles(filesToQueue, options);
        if (count > 0)
            ShowGlassToast(count == 1
                ? $"已加入后台导入：{Path.GetFileName(filesToQueue[0])}"
                : $"已加入后台导入队列：共 {count} 个文件（点左侧任务卡可查看全部）");
    }
}
