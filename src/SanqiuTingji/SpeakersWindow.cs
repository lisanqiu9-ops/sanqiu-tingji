using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SanqiuTingji.App;

/// Merged speaker manager: voices renamed to the same name automatically combine into one person
/// with a unified color badge, while retaining raw voice chips for one-click lossless unmerging.
public sealed class SpeakersWindow : Window
{
    public string SessionId { get; }
    private readonly AppModel model;
    private readonly TextBox search;
    private readonly StackPanel list = new();
    private readonly TextBlock count;
    private readonly TextBlock mergedNote;
    private readonly TextBlock empty;
    private readonly List<GroupCard> cards = [];
    private bool closed;

    private sealed class GroupCard(int[] voices, string initialName, TextBox nameBox, FrameworkElement view, string searchText)
    {
        public int[] Voices = voices;
        public string Saved = initialName;
        public TextBox NameBox = nameBox;
        public FrameworkElement View = view;
        public string SearchText = searchText;
    }

    public SpeakersWindow(AppModel model, string sessionId)
    {
        SessionId = sessionId;
        this.model = model;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinWidth = 440;
        MinHeight = 280;
        MaxHeight = SystemParameters.WorkArea.Height * 0.74;
        Background = (Brush)FindResource("Ground");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = Icons.Mark(64, false);
        Native.ApplyLiquidGlass(this, dark: false);
        UiText.Set(this, "Title", () => $"{Brand.Name} · 管理发言人");

        var root = new DockPanel { Margin = new Thickness(22, 20, 22, 16) };
        var heading = new StackPanel();
        DockPanel.SetDock(heading, Dock.Top);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        count = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink };
        mergedNote = new TextBlock { FontSize = 12, Foreground = Theme.Grey, Margin = new Thickness(10, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom };
        titleRow.Children.Add(count);
        titleRow.Children.Add(mergedNote);
        heading.Children.Add(titleRow);

        heading.Children.Add(UiText.Bind(new TextBlock
        {
            FontSize = 12, Foreground = Theme.Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14)
        }, ("Text", () => L.T("输入相同姓名回车即自动合并为同一人（人数与色标同步统一）；点声纹标签「×」可无损拆回。"))));

        search = UiText.Bind(new TextBox { Style = (Style)FindResource("Field"), Margin = new Thickness(0, 0, 0, 14) },
            ("Tag", () => L.T("搜索发言人或姓名")));
        search.TextChanged += (_, _) => Filter();
        heading.Children.Add(search);
        root.Children.Add(heading);

        var close = UiText.Bind(new Button
        {
            Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(22, 6, 22, 6)
        }, ("Content", () => L.T("完成")));
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        empty = UiText.Bind(new TextBlock { Foreground = Theme.Grey, Margin = new Thickness(0, 20, 0, 0), Visibility = Visibility.Collapsed },
            ("Text", () => L.T("没有匹配的发言人")));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = new Border
        {
            Background = (Brush)FindResource("SecondaryGlassBg"),
            Child = root,
        };

        RefreshSpeakers();
        model.Store.Changed += StoreChanged;
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        Closing += (_, _) => CommitAllPending();
        Closed += (_, _) => { closed = true; model.Store.Changed -= StoreChanged; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void StoreChanged() => Dispatcher.BeginInvoke(new Action(() => { if (!closed) RefreshSpeakers(); }));
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshSpeakers();

    private void CommitAllPending()
    {
        foreach (var card in cards.ToList()) SaveCard(card);
    }

    private void RefreshSpeakers()
    {
        if (closed) return;
        var session = model.Store.Snapshot(SessionId) ?? new Session();
        var rawCount = SpeakerLabels.VoiceNumbers(session.Lines).Count;
        var groups = SpeakerLabels.Groups(session);
        var namedNames = groups.Where(g => g.IsNamed).Select(g => g.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        list.Children.Clear();
        cards.Clear();
        list.Children.Add(empty);

        UiText.Set(count, "Text", () => L.F("发言人 · {0} 人", groups.Count));
        mergedNote.Text = rawCount > groups.Count ? $"（由 {rawCount} 个声纹段合并）" : "";
        mergedNote.Visibility = rawCount > groups.Count ? Visibility.Visible : Visibility.Collapsed;

        foreach (var group in groups)
        {
            var card = BuildGroupCard(group, namedNames);
            cards.Add(card);
            list.Children.Add(card.View);
        }
        Filter();
    }

    private GroupCard BuildGroupCard(SpeakerLabels.SpeakerGroup group, List<string> existingNames)
    {
        var accent = Theme.Speaker(group.ColorNumber);
        var isMerged = group.Members.Count > 1;
        var border = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(isMerged ? Color.FromArgb(0xEE, 0xF4, 0xF9, 0xFF) : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            BorderBrush = isMerged ? new SolidColorBrush(Color.FromArgb(0x52, 0x00, 0x7A, 0xFF)) : (Brush)FindResource("GlassRim"),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatarText = group.IsNamed && group.DisplayName.Length > 0
            ? group.DisplayName[..1]
            : group.ColorNumber.ToString();
        var avatar = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(10),
            Background = accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = avatarText, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        top.Children.Add(avatar);

        var initialName = group.IsNamed ? group.DisplayName : "";
        var nameBox = UiText.Bind(new TextBox
        {
            Style = (Style)FindResource("Field"),
            Text = initialName,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        }, ("Tag", () => L.F("发言人 {0}（输入姓名或合并到已有人员）", group.ColorNumber)));
        if (group.Evidence != null) nameBox.ToolTip = group.Evidence;
        Grid.SetColumn(nameBox, 1);
        top.Children.Add(nameBox);

        var countBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0x16, 0x28, 0x4C)),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{group.LineCount} 句", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Grey,
            },
        };
        Grid.SetColumn(countBadge, 2);
        top.Children.Add(countBadge);
        stack.Children.Add(top);

        // Second row: raw voice chips (with one-click unmerge × when merged) + quick-merge suggestions
        var subRow = new WrapPanel { Margin = new Thickness(40, 8, 0, 0), Orientation = Orientation.Horizontal };
        subRow.Children.Add(new TextBlock
        {
            Text = isMerged ? "已合并声纹：" : "原始声纹：",
            FontSize = 11, Foreground = Theme.Grey, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 2),
        });

        foreach (var (voiceId, rawNum) in group.Members)
        {
            var chipPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chipPanel.Children.Add(new TextBlock
            {
                Text = $"原声纹 {rawNum}", FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center,
            });
            if (isMerged)
            {
                var splitBtn = new Button
                {
                    Content = "×",
                    FontSize = 12,
                    Padding = new Thickness(4, 0, 2, 0),
                    Margin = new Thickness(2, -1, 0, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Theme.Grey,
                    Cursor = Cursors.Hand,
                    ToolTip = $"将「原声纹 {rawNum}」拆回独立发言人",
                };
                var targetVoice = voiceId;
                splitBtn.Click += (_, _) => model.RenameVoice(SessionId, targetVoice, "");
                chipPanel.Children.Add(splitBtn);
            }
            subRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x7A, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x7A, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(8, 2, isMerged ? 5 : 8, 2),
                Margin = new Thickness(0, 0, 6, 2),
                Child = chipPanel,
            });
        }

        // Quick-merge chips for unnamed speakers so users can merge with one click without typing typos
        if (!group.IsNamed && existingNames.Count > 0)
        {
            var mergeTargets = existingNames.Take(4).ToList();
            subRow.Children.Add(new TextBlock
            {
                Text = "快捷合并：",
                FontSize = 11, Foreground = Theme.Grey, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 2),
            });
            var voicesForMerge = group.Members.Select(m => m.Voice).ToArray();
            foreach (var targetName in mergeTargets)
            {
                var pick = new Button
                {
                    Content = "→ " + targetName,
                    Style = (Style)FindResource("GhostBtn"),
                    FontSize = 11,
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 0, 4, 2),
                    Foreground = Theme.Primary,
                    ToolTip = $"将此声纹合并到「{targetName}」",
                };
                var captured = targetName;
                pick.Click += (_, _) => model.RenameVoices(SessionId, voicesForMerge, captured);
                subRow.Children.Add(pick);
            }
        }

        stack.Children.Add(subRow);
        border.Child = stack;

        var voiceIds = group.Members.Select(m => m.Voice).ToArray();
        var searchBlob = string.Join(" ", group.DisplayName,
            string.Join(" ", group.Members.Select(m => $"发言人 {m.Number} 原声纹 {m.Number}")));
        var card = new GroupCard(voiceIds, initialName, nameBox, border, searchBlob);

        nameBox.LostKeyboardFocus += (_, _) => SaveCard(card);
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            SaveCard(card);
            Keyboard.ClearFocus();
            e.Handled = true;
        };
        return card;
    }

    private void SaveCard(GroupCard card)
    {
        var value = card.NameBox.Text.Trim();
        if (value == card.Saved) return;
        card.Saved = value;
        model.RenameVoices(SessionId, card.Voices, value);
    }

    private void Filter()
    {
        var query = search.Text.Trim();
        foreach (var card in cards)
        {
            var visible = card.NameBox.IsKeyboardFocusWithin || query.Length == 0
                || card.NameBox.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
            card.View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        empty.Visibility = cards.All(c => c.View.Visibility == Visibility.Collapsed) ? Visibility.Visible : Visibility.Collapsed;
    }
}
