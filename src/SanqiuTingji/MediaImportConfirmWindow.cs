using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SanqiuTingji.Audio;
using SanqiuTingji.Localization;

namespace SanqiuTingji.App;

/// Lightweight confirmation card shown when dragging audio/video files onto the main window.
/// Lets users review dropped files (with duration & original recording date), tweak key options
/// (speaker diarization, auto-notes, group, template), or opt into zero-prompt drag-drop.
public sealed class MediaImportConfirmWindow : Window
{
    private sealed record FileItem(string Path, string Name, string Ext, string Meta);

    private readonly AppModel model;
    private readonly List<FileItem> items;
    private readonly StackPanel listHost = new();
    private readonly TextBlock titleBlock = new();
    private readonly CheckBox speakersCheck = new();
    private readonly CheckBox autoNotesCheck = new();
    private readonly CheckBox skipNextCheck = new();
    private readonly ComboBox groupBox = new();
    private readonly ComboBox templateBox = new();
    private readonly Button confirmBtn = new();

    public IReadOnlyList<string> SelectedFiles => items.Select(i => i.Path).ToList();
    public MediaImportOptions SelectedOptions { get; private set; }
    public bool SkipFutureConfirm => skipNextCheck.IsChecked == true;

    public MediaImportConfirmWindow(AppModel model, IEnumerable<string> filePaths)
    {
        this.model = model;
        SelectedOptions = new MediaImportOptions(model.Settings.SpeakerVoices, null, model.Settings.DefaultTemplateId, false);
        items = filePaths.Select(BuildFileItem).ToList();

        Width = 516;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("Ground");
        Foreground = Theme.Ink;
        Icon = Icons.Mark(64, false);
        Native.ApplyLiquidGlass(this, dark: false);
        Title = $"{Brand.Name} · 导入确认";

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        titleBlock.FontSize = 17;
        titleBlock.FontWeight = FontWeights.SemiBold;
        titleBlock.Foreground = Theme.Ink;
        root.Children.Add(titleBlock);

        root.Children.Add(new TextBlock
        {
            Text = "将自动读取原录制时间（如「创建媒体日期」），后台识别不打断当前浏览",
            FontSize = 12,
            Foreground = Theme.Grey,
            Margin = new Thickness(0, 3, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // Dropped files preview list
        var listBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF8, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE9, 0xF5)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 8, 8, 2),
            Child = new ScrollViewer
            {
                MaxHeight = 168,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listHost,
            },
        };
        root.Children.Add(listBorder);

        // Parameters card
        var paramCard = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 12, 0, 0),
        };
        var paramStack = new StackPanel();

        speakersCheck.IsChecked = model.Settings.SpeakerVoices;
        speakersCheck.FontSize = 12.5;
        speakersCheck.FontWeight = FontWeights.SemiBold;
        speakersCheck.Foreground = Theme.Ink;
        speakersCheck.Content = "同时区分发言人";
        paramStack.Children.Add(speakersCheck);
        paramStack.Children.Add(new TextBlock
        {
            Text = "多人会议建议开启；单人录音或赶时间可取消，识别提速约 2～3 倍",
            FontSize = 11.5,
            Foreground = Theme.Grey,
            Margin = new Thickness(22, 2, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        var hasAi = model.ActiveCliReady || model.ActiveCloudReady;
        autoNotesCheck.IsChecked = false;
        autoNotesCheck.IsEnabled = hasAi;
        autoNotesCheck.FontSize = 12.5;
        autoNotesCheck.FontWeight = FontWeights.SemiBold;
        autoNotesCheck.Foreground = hasAi ? Theme.Ink : Theme.Grey2;
        autoNotesCheck.Content = "转写完成后自动生成纪要";
        paramStack.Children.Add(autoNotesCheck);
        paramStack.Children.Add(new TextBlock
        {
            Text = hasAi
                ? "使用当前 AI 引擎按下方所选模板自动总结（适合批量导入后离开电脑）"
                : "尚未配置本地 CLI 或云端 API，可先转写后手动生成或前往设置配置 AI 引擎",
            FontSize = 11.5,
            Foreground = Theme.Grey,
            Margin = new Thickness(22, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // Two-column selectors: Group & Template
        var selectors = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        selectors.ColumnDefinitions.Add(new ColumnDefinition());
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        selectors.ColumnDefinitions.Add(new ColumnDefinition());

        var leftCol = new StackPanel();
        leftCol.Children.Add(new TextBlock { Text = "导入到分组", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Grey, Margin = new Thickness(0, 0, 0, 5) });
        FillGroups();
        leftCol.Children.Add(groupBox);
        Grid.SetColumn(leftCol, 0);
        selectors.Children.Add(leftCol);

        var rightCol = new StackPanel();
        rightCol.Children.Add(new TextBlock { Text = "默认纪要模板", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Grey, Margin = new Thickness(0, 0, 0, 5) });
        FillTemplates();
        rightCol.Children.Add(templateBox);
        Grid.SetColumn(rightCol, 2);
        selectors.Children.Add(rightCol);

        paramStack.Children.Add(selectors);
        paramCard.Child = paramStack;
        root.Children.Add(paramCard);

        // Footer: remember toggle + Cancel / Confirm
        var footer = new DockPanel { Margin = new Thickness(0, 15, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancelBtn = new Button
        {
            Content = "取消",
            Style = (Style)FindResource("Btn"),
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        confirmBtn.Style = (Style)FindResource("PrimaryBtn");
        confirmBtn.Padding = new Thickness(18, 7, 18, 7);
        confirmBtn.IsDefault = true;
        confirmBtn.Click += (_, _) => Submit();

        btns.Children.Add(cancelBtn);
        btns.Children.Add(confirmBtn);
        DockPanel.SetDock(btns, Dock.Right);
        footer.Children.Add(btns);

        skipNextCheck.Content = "下次拖入直接按默认开始（不再弹窗）";
        skipNextCheck.FontSize = 11.5;
        skipNextCheck.Foreground = Theme.Grey;
        skipNextCheck.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(skipNextCheck);

        root.Children.Add(footer);

        Content = new Border
        {
            Background = (Brush)FindResource("SecondaryGlassBg"),
            Child = root,
        };

        RenderItems();
    }

    private static FileItem BuildFileItem(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var (dur, rec, size) = MediaImportQueue.ProbeFileMeta(path);
        var parts = new List<string>();
        if (dur is > 0) parts.Add(TranscriptExport.Clock(dur.Value));
        if (size > 0) parts.Add(MeetingAudioStore.FormatSize(size));
        if (rec is { } r) parts.Add($"原录制 {r.ToLocalTime():yyyy-MM-dd HH:mm}");
        return new FileItem(path, name, ext.Length > 0 ? ext : "AUDIO", string.Join(" · ", parts));
    }

    private void FillGroups()
    {
        groupBox.Items.Clear();
        groupBox.Items.Add(new ComboBoxItem { Content = L.T("未分组"), Tag = "" });
        foreach (var g in model.Store.Groups)
            groupBox.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Id });
        groupBox.SelectedIndex = 0;
    }

    private void FillTemplates()
    {
        templateBox.Items.Clear();
        var defId = model.Settings.DefaultTemplateId;
        ComboBoxItem? chosen = null;
        foreach (var t in model.Templates)
        {
            var label = t.Id == defId
                ? L.F("{0}（默认）", t.BuiltIn ? L.T(t.Name) : t.Name)
                : t.BuiltIn ? L.T(t.Name) : t.Name;
            var item = new ComboBoxItem { Content = label, Tag = t.Id };
            templateBox.Items.Add(item);
            if (t.Id == defId) chosen = item;
        }
        templateBox.SelectedItem = chosen ?? (templateBox.Items.Count > 0 ? templateBox.Items[0] : null);
    }

    private void RenderItems()
    {
        listHost.Children.Clear();
        titleBlock.Text = items.Count == 1
            ? "导入 1 个音视频到后台队列"
            : $"导入 {items.Count} 个音视频到后台队列";
        confirmBtn.Content = $"加入队列 ({items.Count})";
        confirmBtn.IsEnabled = items.Count > 0;

        foreach (var item in items.ToList())
        {
            var row = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var dock = new DockPanel();

            var removeBtn = new Button
            {
                Content = "×",
                Style = (Style)FindResource("GhostBtn"),
                FontSize = 14,
                Foreground = Theme.Grey2,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "移出本次导入",
            };
            var captured = item;
            removeBtn.Click += (_, _) =>
            {
                items.Remove(captured);
                if (items.Count == 0) { DialogResult = false; Close(); return; }
                RenderItems();
            };
            DockPanel.SetDock(removeBtn, Dock.Right);
            dock.Children.Add(removeBtn);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF2, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.Ext,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.Primary,
                },
            };
            DockPanel.SetDock(badge, Dock.Left);
            dock.Children.Add(badge);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (item.Meta.Length > 0)
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = item.Meta,
                    FontSize = 10.5,
                    Foreground = Theme.Grey,
                    Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            dock.Children.Add(textStack);
            row.Child = dock;
            listHost.Children.Add(row);
        }
    }

    private void Submit()
    {
        if (items.Count == 0) return;
        var groupId = (groupBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(groupId)) groupId = null;
        var templateId = (templateBox.SelectedItem as ComboBoxItem)?.Tag as string ?? model.Settings.DefaultTemplateId;
        SelectedOptions = new MediaImportOptions(
            DistinguishVoices: speakersCheck.IsChecked == true,
            GroupId: groupId,
            TemplateId: templateId,
            AutoGenerateNotes: autoNotesCheck.IsEnabled && autoNotesCheck.IsChecked == true);
        DialogResult = true;
        Close();
    }
}
