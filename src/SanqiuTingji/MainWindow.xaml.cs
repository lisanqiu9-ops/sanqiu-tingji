using SanqiuTingji.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SanqiuTingji.App;

/// The meeting records: every meeting with its notes and bilingual transcript. Rename, write the
/// notes again, export to Markdown, delete.
public partial class MainWindow : Window
{
    private readonly AppModel model;
    private readonly ObservableCollection<LineVM> lines = [];
    private string? selected;
    private bool refreshingList;
    private int searchRevision;
    /// A search hit: in the notes, a range of one of their text boxes; in the transcript (whose lines are
    /// only made while in view), the line, which of its two texts, and which hit in that text.
    private sealed record SearchMatch(SelectableText? Box, System.Windows.Documents.TextRange? Range, int Line = -1, bool InDst = false, int Nth = 0)
    {
        public bool Notes => Box != null;
    }
    private readonly List<SearchMatch> searchMatches = [];
    /// What the transcript's lines highlight as they come into view.
    private string highlighted = "";
    private int searchIndex = -1;
    private string appliedQuery = "";
    private string? appliedSession;
    private readonly System.Windows.Threading.DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    /// While a meeting runs the store changes several times a second (each line, each translation).
    /// Redrawing the list and the details for each kept the window busy most of the time and every
    /// click waited (issue #1): the list follows at most once a second, and not while minimised.
    private readonly System.Windows.Threading.DispatcherTimer listTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime listShown;
    private bool listPending;
    /// What the notes on show were built from: unchanged, they aren't built again.
    private string? notesKey;
    private int glossaryRevision;
    private readonly System.Windows.Threading.DispatcherTimer cliTickTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly System.Windows.Threading.DispatcherTimer cliDoneTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private bool cliAnimRunning;

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        Native.ApplyLiquidGlass(this, dark: false);
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshList(); };
        listTimer.Tick += (_, _) => { listTimer.Stop(); RefreshList(); };
        cliTickTimer.Tick += (_, _) => UpdateCliProgressClock();
        cliDoneTimer.Tick += (_, _) => HideCliDoneBanner();
        toastTimer.Tick += (_, _) => HideGlassToast();
        InitAudioPlayback();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
        Closed += (_, _) => { DisposeAudioPlayback(); searchTimer.Stop(); listTimer.Stop(); cliTickTimer.Stop(); cliDoneTimer.Stop(); toastTimer.Stop(); StopCliMotion(); };
        StateChanged += (_, _) =>
        {
            UpdateMaximizeChrome();
            if (WindowState != WindowState.Minimized && listPending) RefreshList();
        };
        Loaded += (_, _) =>
        {
            UpdateMaximizeChrome();
            UpdateScopeThumb(animate: false);
            UpdateTabThumb(animate: false);
        };
        UiText.Set(this, "Title", () => $"{Brand.Name} · 会议记录");
        Icon = Icons.Mark(64, false);
        Logo.Source = Icons.Mark(52, false);
        AppName.Text = Brand.Name;
        // Roomy by default, but never bigger than the screen it opens on.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.92);
        Height = Math.Min(Height, area.Height * 0.92);
        LinesList.ItemsSource = lines;
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        model.Store.Changed += () => Dispatcher.BeginInvoke(RequestList);
        model.NotesChanged += RefreshSelectedDetails;
        // 加入词库 buttons in the notes show what the glossary knows.
        model.Glossary.Changed += () => Dispatcher.BeginInvoke(() => { glossaryRevision++; RefreshSelectedDetails(); });
        Refresh();
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    public void Refresh()
    {
        AppName.Text = Brand.Name;
        UiText.Set(ListenBtn, "Content", () => model.IsListening ? L.T("● 收听中 · 点击结束") : L.T("开始收听"));
        ListenBtn.Style = (Style)FindResource(model.IsListening ? "OnAirBtn" : "PrimaryBtn");
        TopLivePill.Visibility = model.IsListening ? Visibility.Visible : Visibility.Collapsed;
        RequestList();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeChrome()
    {
        if (RootShell == null) return;
        var max = WindowState == WindowState.Maximized;
        var b = SystemParameters.WindowResizeBorderThickness;
        RootShell.Margin = max ? new Thickness(b.Left + 2, b.Top + 2, b.Right + 2, b.Bottom + 2) : new Thickness(0);
        if (MaxGlyph != null) MaxGlyph.Text = max ? "\uE923" : "\uE922";
    }

    public void ShowGlassToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.BeginInvoke(() =>
        {
            toastTimer.Stop();
            GlassToastText.Text = message;
            GlassToast.Visibility = Visibility.Visible;
            Motion.PopIn(GlassToast, offsetY: 10, fromScale: 0.96, ms: 200);
            toastTimer.Start();
        });
    }

    private void HideGlassToast()
    {
        toastTimer.Stop();
        if (GlassToast.Visibility == Visibility.Visible) Motion.FadeOut(GlassToast, ms: 180);
    }

    private void ScopeTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScopeThumb(animate: false);
    private void TabTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabThumb(animate: false);

    private void UpdateScopeThumb(bool animate)
    {
        if (ScopeThumb == null || ScopeTrack == null) return;
        RadioButton? active = ActiveScope.IsChecked == true ? ActiveScope : AllScope.IsChecked == true ? AllScope : ArchivedScope;
        if (active != null) Motion.MoveSegmentedThumb(ScopeThumb, ScopeTrack, active, animate);
    }

    private void UpdateTabThumb(bool animate)
    {
        if (TabThumb == null || TabTrack == null) return;
        RadioButton active = TabNotes.IsChecked == true ? TabNotes
            : TabLines.IsChecked == true ? TabLines : TabAudio;
        Motion.MoveSegmentedThumb(TabThumb, TabTrack, active, animate);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    /// The list and details again, soon: now if they were last drawn over a second ago, else once
    /// that second is up; when the window is minimised, as it comes back.
    private void RequestList()
    {
        listPending = true;
        if (WindowState == WindowState.Minimized || listTimer.IsEnabled) return;
        var wait = listShown + listTimer.Interval - DateTime.Now;
        if (wait <= TimeSpan.Zero) RefreshList();
        else
        {
            listTimer.Interval = wait;
            listTimer.Start();
        }
    }

    private void Live_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowLive();

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

    private void RefreshList()
    {
        listTimer.Stop();
        listTimer.Interval = TimeSpan.FromSeconds(1);
        listPending = false;
        listShown = DateTime.Now;
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = model.Store.All
            .Where(InLibraryScope)
            .Where(s => SessionSearch.Matches(s, q) || (q.Length > 0 && DisplayTitle(s).Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new SessionRow(s.Id, (s.Id == model.LiveSessionId ? "● " : "") + DisplayTitle(s),
                L.F("{0:MM-dd HH:mm} · {1} · {2} 句", s.StartedAt.ToLocalTime(), Minutes(s), s.Lines.Count))
                {
                    Archived = model.Store.IsArchived(s.Id),
                    HasAudio = SanqiuTingji.Audio.MeetingAudioStore.FindAudioPath(model.Store, s.Id) != null,
                })
            .ToList();
        var keep = selected;
        refreshingList = true;
        // Only counts and titles changed (a meeting running): those rows change in place. A new tree
        // would build every row again and drop the scroll position.
        if (!UpdateMeetingTree(rows, q)) BuildMeetingTree(rows, q);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderLibraryEmpty(q);
        var shown = treeGroups.SelectMany(g => g.Children).ToList();
        var row = shown.FirstOrDefault(r => r.Id == keep) ?? shown.FirstOrDefault(r => r.Id == rows.FirstOrDefault()?.Id);
        if (row == null)
        {
            selected = null;
            lines.Clear();
            refreshingList = false;
            searchRevision++;
            ClearHighlights();
            searchMatches.Clear();
            SearchNavigation.Visibility = Visibility.Collapsed;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }
        // A new list drops the selection; selecting the same meeting again keeps its tab.
        row.IsSelected = true;
        refreshingList = false;
        ShowDetail(row.Id, keepTab: true);
    }

    private static string Minutes(Session s)
    {
        var m = (int)Math.Round(s.Duration.TotalMinutes);
        return m < 1 ? L.T("不到 1 分钟") : L.F("{0} 分钟", m);
    }

    private void Session_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!refreshingList && e.NewValue is SessionRow r) UiWatch.Time("open meeting", () => ShowDetail(r.Id, keepTab: false));
    }

    private void ShowDetail(string id, bool keepTab)
    {
        var s = model.Store.Get(id);
        if (s == null) { Detail.Visibility = Visibility.Collapsed; return; }
        var switched = selected != id;
        if (switched) LibraryError.Visibility = Visibility.Collapsed;
        selected = id;
        Detail.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        if (!TitleBox.IsKeyboardFocused) TitleBox.Text = DisplayTitle(s);
        UiText.Set(MetaText, "Text", () => L.F("{0:yyyy年M月d日 HH:mm} · 时长 {1} · {2} 句{3}", s.StartedAt.ToLocalTime(), Minutes(s), s.Lines.Count, (s.Id == model.LiveSessionId ? " · 正在记录" : "")) + LibraryMeta(s.Id));
        if (switched) lines.Clear();
        if (model.Store.Snapshot(id) is { } snap)
        {
            LineVM.Sync(lines, snap);
            RenderSpeakers(snap);
            RefreshAudioPane(snap, switched);
        }
        RenderTemplateBox(s);
        var generating = model.Notes.GeneratingId == id || model.CliGeneratingId == id;
        var cliBusy = model.CliGeneratingId == id;
        var hasLines = s.Lines.Count > 0;
        ExportBtn.Visibility = hasLines ? Visibility.Visible : Visibility.Collapsed;
        NotesTools.Visibility = hasLines ? Visibility.Visible : Visibility.Collapsed;
        UpdateCliProgressBanner(cliBusy);
        UiText.Set(NotesBtn, "Content", () => generating ? "生成中…" : s.Notes is { IsEmpty: false } ? "重新生成" : "生成纪要");
        NotesBtn.IsEnabled = !generating && s.Id != model.LiveSessionId;
        WorkshopBtn.IsEnabled = !generating && s.Id != model.LiveSessionId;
        var tabWillSwitch = !keepTab && switched && ((s.Notes is { IsEmpty: false }) != (TabNotes.IsChecked == true));
        if (switched && !tabWillSwitch) Motion.FadeIn(Detail, ms: 160);
        UiText.Set(NotesBtn, "ToolTip", () => cliBusy ? (model.CliGeneratingStatus ?? "正在通过本地 CLI 整理纪要…")
            : generating ? L.T("AI 正在通读整场会议…")
            : s.Notes?.UpdatedAt is { } at ? L.F("纪要更新于 {0:M月d日 HH:mm}", at.ToLocalTime())
            : model.CliBridgeReady ? $"通过本地 CLI（{CliBridge.FindPreset(model.Settings.CliNotesPreset).Name}）自动生成纪要"
            : "打开免 API 纪要工坊");
        var names = SpeakerLabels.Names(s);
        var builtIn = model.TemplateFor(s).BuiltIn;
        var key = string.Join("|", s.Id, L.Language, model.Ai.HasKey, model.CliBridgeReady, cliBusy, model.Settings.CliNotesPreset, glossaryRevision, builtIn,
            string.Join(",", names.OrderBy(n => n.Key).Select(n => $"{n.Key}={n.Value}")),
            s.Notes is { IsEmpty: false } ? System.Text.Json.JsonSerializer.Serialize(s.Notes) : "");
        if (key == notesKey && NotesHost.Content != null) { FinishDetail(s, keepTab, switched); return; }
        notesKey = key;
        var emptyNotesHint = model.CliBridgeReady
            ? $"这场会议还没有纪要。点右上角「生成纪要」将自动调用本地 CLI（{CliBridge.FindPreset(model.Settings.CliNotesPreset).Name}）生成，也可以点「手工工坊」手动复制粘贴。"
            : model.Ai.HasKey ? L.T("这场会议还没有纪要，点右上角「生成纪要」。")
            : "这场会议还没有纪要。可在「设置 › 本地 CLI」启用自动调用，或点右上角「生成纪要」打开免 API 手工工坊。";
        NotesHost.Content = s.Notes is { IsEmpty: false } n
            ? NotesRenderer.Build(n, s.StartedAt, 14, cards: true, names: names, localizeHeadings: builtIn,
                termCard: BuildTermCard)
            : cliBusy
                ? BuildNotesSkeleton()
                : new Border
                {
                    Style = (Style)FindResource("Panel"),
                    Child = new TextBlock
                    {
                        Style = (Style)FindResource("Helper"), FontSize = 13, Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap,
                        Text = emptyNotesHint,
                    },
                };
        FinishDetail(s, keepTab, switched);
    }

    private void FinishDetail(Session s, bool keepTab, bool switched)
    {
        if (!keepTab && switched)
        {
            if (s.Notes is { IsEmpty: false }) TabNotes.IsChecked = true;
            else TabLines.IsChecked = true;
        }
        QueueSearch();
    }

    private SpeakersWindow? speakersWindow;

    private void RenderSpeakers(Session session)
    {
        var groups = SpeakerLabels.Groups(session);
        SpeakersPanel.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UiText.Set(SpeakersPreview, "Text", () => string.Join("、", groups.Take(3)
            .Select(g => g.IsNamed ? g.DisplayName : L.F("发言人 {0}", g.ColorNumber)))
            + (groups.Count > 3 ? $" 等" : ""));
        UiText.Set(SpeakersCount, "Text", () => $"({groups.Count}人)");
    }

    private void Speakers_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        if (speakersWindow is { IsVisible: true } open && open.SessionId == selected)
        {
            open.Activate();
            return;
        }
        speakersWindow?.Close();
        speakersWindow = new SpeakersWindow(model, selected) { Owner = this };
        speakersWindow.Show();
    }

    public void ShowTab(bool notes)
    {
        TabNotes.IsChecked = notes;
        TabLines.IsChecked = !notes;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (NotesPane == null || AudioPane == null) return;
        var notes = TabNotes.IsChecked == true;
        var linesOn = TabLines.IsChecked == true;
        var audio = TabAudio.IsChecked == true;
        NotesPane.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        LinesScroll.Visibility = linesOn ? Visibility.Visible : Visibility.Collapsed;
        AudioPane.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabThumb(animate: IsLoaded);
        if (audio && selected != null && model.Store.Snapshot(selected) is { } snap)
            RefreshAudioPane(snap, sessionSwitched: false);
        if (IsLoaded)
        {
            if (notes) Motion.Reveal(NotesHost, offsetY: 5, ms: 170);
            else if (linesOn) Motion.FadeIn(LinesScroll, ms: 140);
            else Motion.FadeIn(AudioPane, ms: 140);
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var hasText = SearchBox.Text.Length > 0;
        if (SearchHint != null) SearchHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        if (ClearSearchBtn != null) ClearSearchBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0) RefreshList();
        else searchTimer.Start();
    }

    private void QueueSearch()
    {
        var revision = ++searchRevision;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (revision != searchRevision) return;
            var query = SearchBox.Text.Trim();
            var relocate = query != appliedQuery || selected != appliedSession;
            var previousIndex = searchIndex;
            if (query.Length == 0)
            {
                ClearHighlights();
                searchMatches.Clear();
                searchIndex = -1;
                appliedQuery = query;
                appliedSession = selected;
                SearchNavigation.Visibility = Visibility.Collapsed;
                return;
            }
            searchMatches.Clear();
            searchIndex = -1;
            foreach (var box in SelectableText.Descendants(NotesHost))
                foreach (var range in box.Highlight(query)) searchMatches.Add(new SearchMatch(box, range));
            // The transcript is searched in its lines' text, not in boxes: most of them aren't made.
            for (var i = 0; i < lines.Count; i++)
            {
                AddHits(i, lines[i].Src, false);
                if (lines[i].DstVisibility == Visibility.Visible) AddHits(i, lines[i].Dst ?? "", true);
            }
            void AddHits(int line, string text, bool dst)
            {
                var n = 0;
                for (var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                    searchMatches.Add(new SearchMatch(null, null, line, dst, n++));
            }
            highlighted = query;
            foreach (var box in SelectableText.Descendants(LinesList)) box.Highlight(query);
            appliedQuery = query;
            appliedSession = selected;
            SearchNavigation.Visibility = Visibility.Visible;
            if (searchMatches.Count > 0 && relocate) MoveSearch(1);
            else
            {
                searchIndex = searchMatches.Count == 0 ? -1 : Math.Clamp(previousIndex, 0, searchMatches.Count - 1);
                SearchCount.Text = searchMatches.Count == 0 ? "0 / 0" : $"{searchIndex + 1} / {searchMatches.Count}";
            }
        }));
    }

    private void MoveSearch(int direction)
    {
        if (searchMatches.Count == 0) return;
        searchIndex = (searchIndex + direction + searchMatches.Count) % searchMatches.Count;
        var match = searchMatches[searchIndex];
        ShowTab(match.Notes);
        UpdateLayout();
        var (box, range) = match.Notes ? (match.Box, match.Range) : LineHit(match);
        if (box != null)
        {
            var rect = range?.Start.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward) ?? Rect.Empty;
            if (rect.IsEmpty) box.BringIntoView();
            else box.BringIntoView(rect);
        }
        SearchCount.Text = $"{searchIndex + 1} / {searchMatches.Count}";
    }

    /// A transcript hit's box and range: its line is brought into view first, which makes it.
    private (SelectableText? Box, System.Windows.Documents.TextRange? Range) LineHit(SearchMatch match)
    {
        if (match.Line < 0 || match.Line >= lines.Count) return (null, null);
        if (FindChild<VirtualizingStackPanel>(LinesList) is { } panel) panel.BringIndexIntoViewPublic(match.Line);
        LinesList.UpdateLayout();
        if (LinesList.ItemContainerGenerator.ContainerFromIndex(match.Line) is not DependencyObject item) return (null, null);
        var boxes = SelectableText.Descendants(item).ToList();
        var box = match.InDst ? boxes.ElementAtOrDefault(1) : boxes.FirstOrDefault();
        var ranges = box?.Highlight(highlighted);
        return (box, ranges?.ElementAtOrDefault(match.Nth));
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// A transcript line coming into view takes the search's highlight.
    private void LineBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (highlighted.Length > 0 && sender is SelectableText box) box.Highlight(highlighted);
    }

    private void ClearHighlights()
    {
        highlighted = "";
        foreach (var box in searchMatches.Where(m => m.Box != null).Select(m => m.Box!).Distinct()) box.Highlight("");
        foreach (var box in SelectableText.Descendants(LinesList)) box.Highlight("");
    }

    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => UiWatch.Time("search move", () => MoveSearch(-1));
    private void NextMatch_Click(object sender, RoutedEventArgs e) => UiWatch.Time("search move", () => MoveSearch(1));

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (searchTimer.IsEnabled)
        {
            searchTimer.Stop();
            RefreshList();
            e.Handled = true;
            return;
        }
        MoveSearch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).ToggleListening();
        Refresh();
    }

    private bool fillingTemplates;

    /// The meeting's template: its own pick, else the default (marked so).
    private void RenderTemplateBox(Session s)
    {
        if (TemplateBox.IsDropDownOpen) return;
        fillingTemplates = true;
        TemplateBox.Items.Clear();
        var chosen = model.TemplateFor(s).Id;
        foreach (var t in model.Templates)
        {
            var item = UiText.Bind(new ComboBoxItem
            {
                Content = t.Id == model.Settings.DefaultTemplateId ? L.F("{0}（默认）", t.BuiltIn ? L.T(t.Name) : t.Name) : t.BuiltIn ? L.T(t.Name) : t.Name, Tag = t.Id, ToolTip = t.BuiltIn ? L.T(t.Description) : t.Description,
            }, ("Content", () => t.Id == model.Settings.DefaultTemplateId ? L.F("{0}（默认）", t.BuiltIn ? L.T(t.Name) : t.Name) : t.BuiltIn ? L.T(t.Name) : t.Name));
            TemplateBox.Items.Add(item);
            if (t.Id == chosen) TemplateBox.SelectedItem = item;
        }
        fillingTemplates = false;
    }

    /// A new template for this meeting: rewrite its notes in that shape (a live meeting picks it up
    /// at its next update).
    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (fillingTemplates || selected == null || TemplateBox.SelectedItem is not ComboBoxItem { Tag: string tid }) return;
        var s = model.Store.Get(selected);
        if (s == null || model.TemplateFor(s).Id == tid) return;
        model.Store.SetTemplate(selected, tid == model.Settings.DefaultTemplateId ? null : tid);
        if (selected == model.LiveSessionId)
        {
            UiText.Set(NotesBtn, "ToolTip", () => L.T("下一次更新起按新模板整理"));
            model.Notes.UpdateNow();
            return;
        }
        if (model.Ai.HasKey && s.Lines.Count > 0)
        {
            model.Notes.Generate(selected);
            ShowDetail(selected, keepTab: true);
            TabNotes.IsChecked = true;
        }
    }

    private void Glossary_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenGlossary();

    /// Way B: Interactive AI-extracted term card that expands IN PLACE for editing & glossary save (NO modal!).
    /// Wrapped in ContentControl so SelectableText.MakeSelectable leaves interactive inputs intact.
    private UIElement BuildTermCard(NoteTerm t)
    {
        var host = new ContentControl { Margin = new Thickness(0, 0, 8, 8) };
        host.Content = BuildTermDisplayView(t, host);
        return host;
    }

    private UIElement BuildTermDisplayView(NoteTerm t, ContentControl host)
    {
        var known = model.Glossary.FindByText(t.Name);
        var inGlossary = known != null && (t.Heard.Count == 0 || model.Glossary.Knows(t.Name, t.Heard));

        var stack = new StackPanel();

        // Top row: Bold Name + optional Category badge + Note + compact ✎ edit button
        var top = new DockPanel();
        var editBtn = new Button
        {
            Content = "✎",
            Style = (Style)FindResource("GhostBtn"),
            FontSize = 11.5,
            Foreground = Theme.Grey2,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, -2, -4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "就地修改并入库",
        };
        DockPanel.SetDock(editBtn, Dock.Right);
        editBtn.Click += (_, _) => host.Content = BuildTermInlineEditor(t, host);
        top.Children.Add(editBtn);

        var nameRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 310 };
        nameRow.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center });
        if (known != null && known.Group.Length > 0)
        {
            nameRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x7A, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = known.Group, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary },
            });
        }
        if (t.Note.Length > 0)
            nameRow.Children.Add(new TextBlock { Text = t.Note, FontSize = 12, Foreground = Theme.Grey, Margin = new Thickness(6, 1, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });
        top.Children.Add(nameRow);
        stack.Children.Add(top);

        if (t.Heard.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = L.T("识别成 ") + string.Join("、", t.Heard),
                FontSize = 11, Foreground = Theme.Grey2, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            });

        if (inGlossary)
        {
            stack.Children.Add(new TextBlock { Text = L.T("✓ 已在词库"), FontSize = 11, Foreground = Theme.OnAir, Margin = new Thickness(0, 4, 0, 0) });
        }
        else
        {
            var addBtn = new Button
            {
                Content = L.T("＋ 加入词库"),
                Style = (Style)FindResource("GhostBtn"),
                FontSize = 11,
                Foreground = Theme.Primary,
                Padding = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-2, 2, 0, 0),
                ToolTip = t.Heard.Count > 0
                    ? L.F("以后识别成「{0}」时自动改成「{1}」", string.Join("、", t.Heard), t.Name)
                    : $"将「{t.Name}」直接加入词库",
            };
            addBtn.Click += (_, _) =>
            {
                model.Glossary.Learn(t.Name, t.Heard, t.Note, known?.Group ?? "");
                var fixedCount = 0;
                if (selected is { } sid)
                    foreach (var h in t.Heard)
                        fixedCount += model.CorrectMeeting(sid, h, t.Name, includeNotes: true);
                ShowGlassToast(fixedCount > 0
                    ? $"「{t.Name}」已加入词库，并同步修正本场 {fixedCount} 处"
                    : L.F("「{0}」已加入词库", t.Name));
            };
            stack.Children.Add(addBtn);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0x16, 0x28, 0x4C)),
            BorderBrush = Theme.GlassRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            MinWidth = 220,
            Child = stack,
        };
    }

    private UIElement BuildTermInlineEditor(NoteTerm t, ContentControl host)
    {
        var known = model.Glossary.FindByText(t.Name);
        var originalName = t.Name;
        var initialHeard = t.Heard.Concat(known?.Misheard ?? []).Distinct().ToList();

        var nameBox = new TextBox { Text = t.Name, Style = (Style)FindResource("Field"), Width = 126, Padding = new Thickness(8, 4, 8, 4), FontSize = 12.5, Tag = "正确写法" };
        var noteBox = new TextBox { Text = t.Note.Length > 0 ? t.Note : (known?.Note ?? ""), Style = (Style)FindResource("Field"), Width = 180, Padding = new Thickness(8, 4, 8, 4), FontSize = 12.5, Tag = "说明（选填）" };
        var catBox = new ComboBox { IsEditable = true, Width = 112, FontSize = 12, Text = known?.Group ?? "", ToolTip = "留空为通用词条，或输入新分类" };
        foreach (var g in model.Glossary.Groups) catBox.Items.Add(g);

        var heardBox = new TextBox
        {
            Text = string.Join("，", initialHeard),
            Style = (Style)FindResource("Field"),
            Width = 260,
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12.5,
            Tag = "误识词，多个用逗号分隔",
        };
        var autoAddedOld = false;
        nameBox.LostFocus += (_, _) =>
        {
            var cur = nameBox.Text.Trim();
            if (!autoAddedOld && cur.Length > 0 && cur != originalName && originalName.Length > 0)
            {
                var list = GlossaryTerm.ParseList(heardBox.Text);
                if (!list.Contains(originalName))
                {
                    list.Add(originalName);
                    heardBox.Text = string.Join("，", list);
                }
                autoAddedOld = true;
            }
        };

        var syncChk = new CheckBox { Content = "同步修正本场", IsChecked = true, FontSize = 11.5, Foreground = Theme.Grey, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("Btn"), FontSize = 11.5, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        var saveBtn = new Button { Content = "保存入库", Style = (Style)FindResource("PrimaryBtn"), FontSize = 11.5, Padding = new Thickness(12, 4, 12, 4) };

        cancelBtn.Click += (_, _) => host.Content = BuildTermDisplayView(t, host);
        saveBtn.Click += (_, _) =>
        {
            var newName = nameBox.Text.Trim();
            if (newName.Length == 0) { nameBox.Focus(); return; }
            var newNote = noteBox.Text.Trim();
            var newGroup = (catBox.Text ?? "").Trim();
            var newHeard = GlossaryTerm.ParseList(heardBox.Text);
            if (newName != originalName && originalName.Length > 0 && !newHeard.Contains(originalName))
                newHeard.Add(originalName);
            newHeard = newHeard.Where(h => h != newName).Distinct().ToList();

            model.Glossary.Learn(newName, newHeard, newNote, newGroup);
            var replaced = 0;
            if (selected is { } sid)
                replaced = model.UpdateMeetingTerm(sid, originalName, newName, newNote, newHeard, syncChk.IsChecked == true);
            notesKey = "";
            RefreshSelectedDetails();
            var catHint = newGroup.Length > 0 ? $"（分类：{newGroup}）" : "";
            ShowGlassToast(replaced > 0
                ? $"「{newName}」已保存入库{catHint}，并修正本场 {replaced} 处"
                : $"「{newName}」已保存入库{catHint}");
        };

        static TextBlock Lbl(string text, double left = 0) => new()
        {
            Text = text, FontSize = 11.5, Foreground = Theme.Grey2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 5, 0),
        };

        var row1 = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row1.Children.Add(Lbl("正确写法"));
        row1.Children.Add(nameBox);
        row1.Children.Add(Lbl("说明", 10));
        row1.Children.Add(noteBox);
        row1.Children.Add(Lbl("分类", 10));
        row1.Children.Add(catBox);

        var row2 = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row2.Children.Add(Lbl("误识词"));
        row2.Children.Add(heardBox);
        row2.Children.Add(syncChk);
        row2.Children.Add(cancelBtn);
        row2.Children.Add(saveBtn);

        var body = new StackPanel();
        body.Children.Add(row1);
        body.Children.Add(row2);

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = Theme.Primary,
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.10, Color = Color.FromRgb(0x00, 0x51, 0xD5) },
            Child = body,
        };
        card.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        return card;
    }

    /// Way A: Right-click on a transcript line opens the in-place correction strip directly below that line (NO modal!).
    private void Src_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not SelectableText box) return;
        var vm = box.DataContext as LineVM;
        var picked = box.Selection.Text.Trim();
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = L.T("复制"), Command = ApplicationCommands.Copy, CommandTarget = box });
        var usable = picked.Length is > 0 and <= 30 && !picked.Contains('\n');
        var fix = new MenuItem
        {
            Header = usable ? L.F("原地改正「{0}」并加入词库", picked) : "原地纠偏这一句并加入词库",
            IsEnabled = vm != null,
        };
        fix.Click += (_, _) => { if (vm != null) OpenLineInlineEdit(vm, usable ? picked : ""); };
        menu.Items.Add(fix);
        box.ContextMenu = menu;
    }

    private void LineInlineEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LineVM vm })
            OpenLineInlineEdit(vm, "");
    }

    private void OpenLineInlineEdit(LineVM target, string prefillHeard)
    {
        foreach (var l in lines)
            if (!ReferenceEquals(l, target) && l.IsEditing) l.IsEditing = false;

        if (target.IsEditing && string.IsNullOrEmpty(prefillHeard))
        {
            target.IsEditing = false;
            return;
        }

        target.EditHeard = prefillHeard;
        target.EditRight = prefillHeard;
        var known = prefillHeard.Length > 0 ? model.Glossary.FindByText(prefillHeard) : null;
        target.EditGroup = known?.Group ?? "";
        target.EditReplaceAll = true;
        var count = prefillHeard.Length > 0 && selected is { } id && model.Store.Get(id) is { } s
            ? s.Lines.Sum(l => Glossary.CountOf(l.Src, prefillHeard))
            : 0;
        target.EditMatchHint = count > 0
            ? $"同步替换本场逐字稿与纪要中的 {count} 处旧写法"
            : "同步替换本场逐字稿与纪要中的旧写法";
        target.IsEditing = true;
    }

    private void InlineCategoryCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var current = combo.Text;
        combo.Items.Clear();
        foreach (var g in model.Glossary.Groups) combo.Items.Add(g);
        combo.Text = current;
    }

    private void LineInlineCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LineVM vm })
            vm.IsEditing = false;
    }

    private void LineInlineSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineVM vm } || selected is not { } id) return;
        var heard = (vm.EditHeard ?? "").Trim();
        var right = (vm.EditRight ?? "").Trim();
        var group = (vm.EditGroup ?? "").Trim();
        if (right.Length == 0)
        {
            ShowGlassToast("请先填写正确写法");
            return;
        }

        if (heard.Length == 0 || heard == right)
        {
            model.Glossary.Learn(right, [], group: group);
            vm.IsEditing = false;
            ShowGlassToast(group.Length > 0 ? $"「{right}」已加入词库（分类：{group}）" : $"「{right}」已加入词库");
            return;
        }

        model.Glossary.Learn(right, [heard], group: group);
        var changed = vm.EditReplaceAll ? model.CorrectMeeting(id, heard, right, includeNotes: true) : 0;
        vm.IsEditing = false;
        notesKey = "";
        RefreshSelectedDetails();
        var catHint = group.Length > 0 ? $"（分类：{group}）" : "";
        ShowGlassToast(changed > 0
            ? $"「{right}」已入库{catHint}，同步改正本场 {changed} 处"
            : $"「{right}」已入库{catHint}");
    }

    private void Templates_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).OpenTemplates(selected is { } id ? model.TemplateFor(model.Store.Get(id)).Id : null);

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        var id = selected;
        if (model.CliBridgeReady)
        {
            TabNotes.IsChecked = true;
            HideCliDoneBanner();
            var result = await model.GenerateNotesViaCliAsync(id);
            notesKey = "";
            RefreshList();
            if (selected == id)
            {
                ShowDetail(id, keepTab: true);
                if (result.Success)
                {
                    AnimateNotesReveal();
                    ShowCliDoneBanner($"纪要已生成 · {result.TopicCount} 个议题 · {result.TodoCount} 项待办 · {result.TermCount} 个术语");
                }
                else if (!result.Message.Contains("已取消"))
                {
                    if (Dialog.Confirm(this, "本地 CLI 生成失败", result.Message + "\n\n是否改用「手工工坊」复制提示词？", "打开手工工坊"))
                        OpenWorkshop(id);
                }
            }
            else if (result.Success)
            {
                var title = model.Store.Get(id)?.DisplayTitle ?? "后台会议";
                ShowGlassToast($"「{title}」纪要已生成");
            }
            else if (!result.Message.Contains("已取消"))
            {
                var title = model.Store.Get(id)?.DisplayTitle ?? "后台会议";
                ShowGlassToast($"「{title}」纪要生成失败，请切回查看");
            }
            return;
        }
        if (!model.Ai.HasKey)
        {
            OpenWorkshop(id);
            return;
        }
        model.Notes.Generate(id);
        ShowDetail(id, keepTab: true);
    }

    private void CliCancel_Click(object sender, RoutedEventArgs e)
    {
        CliCancelBtn.IsEnabled = false;
        CliStatusText.Text = "正在取消本地 CLI 任务…";
        model.CancelCliNotes();
    }

    private void UpdateCliProgressBanner(bool busy)
    {
        if (CliProgressBanner == null) return;
        if (busy)
        {
            CliProgressBanner.Visibility = Visibility.Visible;
            CliCancelBtn.IsEnabled = true;
            UpdateCliProgressClock();
            if (!cliTickTimer.IsEnabled) cliTickTimer.Start();
            StartCliMotion();
        }
        else
        {
            CliProgressBanner.Visibility = Visibility.Collapsed;
            cliTickTimer.Stop();
            StopCliMotion();
        }
    }

    private void UpdateCliProgressClock()
    {
        if (model.CliGeneratingId == null) return;
        CliStatusText.Text = model.CliGeneratingStatus ?? "正在通过本地 CLI 静默整理纪要…";
        var elapsed = model.CliGeneratingStartedAt is { } at ? DateTime.UtcNow - at : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        CliElapsedText.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    private void StartCliMotion()
    {
        if (cliAnimRunning || !Motion.Enabled) return;
        cliAnimRunning = true;
        var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(720))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        CliPulseDot.BeginAnimation(OpacityProperty, pulse);

        var width = Math.Max(420, NotesPane.ActualWidth > 0 ? NotesPane.ActualWidth : 760);
        var sweep = new DoubleAnimation(-130, width + 40, TimeSpan.FromMilliseconds(1350))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        CliSweepTransform.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void StopCliMotion()
    {
        if (!cliAnimRunning) return;
        cliAnimRunning = false;
        CliPulseDot.BeginAnimation(OpacityProperty, null);
        CliPulseDot.Opacity = 1;
        CliSweepTransform.BeginAnimation(TranslateTransform.XProperty, null);
        CliSweepTransform.X = -130;
    }

    private void AnimateNotesReveal() => Motion.Reveal(NotesHost, offsetY: 8, ms: 220);

    private void ShowCliDoneBanner(string text)
    {
        cliDoneTimer.Stop();
        CliDoneText.Text = text;
        CliDoneBanner.Visibility = Visibility.Visible;
        Motion.FadeIn(CliDoneBanner, ms: 180);
        cliDoneTimer.Start();
    }

    private void HideCliDoneBanner()
    {
        cliDoneTimer.Stop();
        if (CliDoneBanner != null && CliDoneBanner.Visibility == Visibility.Visible)
            Motion.FadeOut(CliDoneBanner, ms: 200);
    }

    private UIElement BuildNotesSkeleton()
    {
        static Border Bar(double width, double height, double bottom = 10) => new()
        {
            Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(height / 2), Margin = new Thickness(0, 0, 0, bottom),
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF5)),
        };

        Border Card(params UIElement[] items)
        {
            var sp = new StackPanel();
            foreach (var i in items) sp.Children.Add(i);
            return new Border { Style = (Style)FindResource("PanelFlat"), Child = sp };
        }

        var root = new StackPanel();
        root.Children.Add(Card(Bar(68, 11, 12), Bar(480, 14, 8), Bar(360, 14, 0)));
        root.Children.Add(Card(Bar(92, 11, 14), Bar(220, 16, 12), Bar(420, 12, 8), Bar(390, 12, 8), Bar(310, 12, 0)));
        root.Children.Add(Card(Bar(78, 11, 12), Bar(340, 12, 8), Bar(270, 12, 0)));
        if (Motion.Enabled)
        {
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.92, 0.45, TimeSpan.FromMilliseconds(950))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        return root;
    }

    private void Workshop_Click(object sender, RoutedEventArgs e)
    {
        if (selected != null) OpenWorkshop(selected);
    }

    private void OpenWorkshop(string id)
    {
        if (NotesWorkshopWindow.ShowFor(this, model, id))
        {
            notesKey = "";
            RefreshList();
            ShowDetail(id, keepTab: true);
            TabNotes.IsChecked = true;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, extension) in new[] { ("Word 文档 (.docx)", ".docx"), ("Markdown (.md)", ".md"), ("文本 (.txt)", ".txt") })
        {
            var item = UiText.Bind(new MenuItem(), ("Header", () => L.T(label)));
            item.Click += (_, _) => ExportTo(extension);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ExportTo(string extension)
    {
        if (selected == null || model.Store.Snapshot(selected) is not { } s) return;
        var name = string.Concat(s.DisplayTitle.Split(Path.GetInvalidFileNameChars()));
        var filter = extension == ".docx" ? L.T("Word 文档 (.docx)") : extension == ".md" ? "Markdown (.md)" : L.T("文本 (.txt)");
        var dlg = new SaveFileDialog { FileName = name + extension, Filter = filter + "|*" + extension, DefaultExt = extension, AddExtension = true };
        if (!string.IsNullOrWhiteSpace(model.Settings.DefaultExportDirectory) && Directory.Exists(model.Settings.DefaultExportDirectory))
            dlg.InitialDirectory = model.Settings.DefaultExportDirectory;
        if (dlg.ShowDialog(this) != true) return;
        var temporary = dlg.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (extension == ".docx") File.WriteAllBytes(temporary, WordExport.Create(s, model.Settings.IncludeTranslationInExport));
            else File.WriteAllText(temporary, TranscriptExport.Markdown(s, model.Settings.IncludeTranslationInExport));
            File.Move(temporary, dlg.FileName, overwrite: true);
            ShowGlassToast($"已导出：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"export: failed {error.GetType().Name}");
            Dialog.Confirm(this, L.T("导出失败"), L.T("请检查文件是否正被占用，以及保存位置是否可写。"), L.T("确定"));
        }
        finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private void DeleteSession(string id)
    {
        if (id == model.Store.LiveId || model.Store.Get(id) is not { } session) return;
        if (!Dialog.Confirm(this, L.F("删除「{0}」？", session.DisplayTitle), L.T("逐字稿和纪要会一起删掉，删除后无法恢复。"), L.T("删除"), danger: true)) return;
        if (loadedAudioSessionId == id) StopAndCloseAudio();
        LibraryAction(() => model.Store.Delete(id));
        if (selected == id && model.Store.Get(id) == null) selected = null;
        RefreshList();
    }

    private void HeaderToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NotesTools == null) return;
        var compact = e.NewSize.Width < 760;
        Grid.SetRow(NotesTools, compact ? 1 : 0);
        Grid.SetColumn(NotesTools, compact ? 0 : 1);
        Grid.SetColumnSpan(NotesTools, compact ? 2 : 1);
        NotesTools.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        NotesTools.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private void Title_LostFocus(object sender, RoutedEventArgs e) => SaveTitle();

    private void Title_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveTitle();
        Keyboard.ClearFocus();
    }

    private void SaveTitle()
    {
        if (selected == null || model.Store.Get(selected) is not { } s) return;
        var t = TitleBox.Text.Trim();
        if (t.Length > 0 && t != DisplayTitle(s)) model.Store.Rename(selected, t);
    }

    private static string DisplayTitle(Session session)
    {
        if ((session.Renamed ?? !session.Title.StartsWith("会议 ")) || L.Language == "zh-CN") return session.DisplayTitle;
        var date = session.StartedAt.ToLocalTime();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(L.Language);
        return !string.IsNullOrEmpty(session.Notes?.Title)
            ? date.ToString("m", culture) + " " + session.Notes.Title
            : L.F("会议 {0}", date.ToString("g", culture));
    }
}
