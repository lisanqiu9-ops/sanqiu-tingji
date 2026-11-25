using SanqiuTingji.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SanqiuTingji.App;

/// The live notes window: subtitles as a running transcript, and the notes the AI keeps. A panel
/// that never takes focus from the meeting app and stays out of screen captures unless the user
/// lets it show (then it wears a badge).
public partial class NotesWindow : Window
{
    private readonly AppModel model;
    private readonly ObservableCollection<LineVM> lines = [];
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? sessionId;
    private DateTime startedAt;
    private MeetingEndWatch.Pending? pending;
    private bool placed;

    public NotesWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        LinesList.ItemsSource = lines;
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        SourceInitialized += (_, _) =>
        {
            Native.MakeNonActivating(this);
            ApplyCapturable();
        };
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        Header.SizeChanged += (_, _) => FitHeader();
        HeaderRight.SizeChanged += (_, _) => FitHeader();
        OnAirPill.SizeChanged += (_, _) => FitHeader();
        model.LineChanged += OnLine;
        // Speaker names (the user's or the AI's) change labels on lines already shown.
        model.Store.Changed += () => Dispatcher.BeginInvoke(() => OnLine(""));
        model.Subtitle += OnSubtitle;
        model.NotesChanged += RenderNotes;
        model.StateChanged += RenderTranslate;
        clock.Tick += (_, _) => Tick();
        RenderTranslate();
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnLine("");
        RenderNotes();
    }

    /// The translation switch follows the setting (it can change from the tray or settings too);
    /// switched off, the transcript shows only what was said.
    private void RenderTranslate()
    {
        TranslateSwitch.IsChecked = model.Settings.TranslationOn;
        OnLine("");
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => model.SetTranslation(TranslateSwitch.IsChecked == true);

    public void ApplyCapturable()
    {
        Native.SetCapturable(this, !model.Settings.HideFromCaptures);
        Shared.Visibility = model.Settings.HideFromCaptures ? Visibility.Visible : Visibility.Collapsed;
        FitHeader();
    }

    /// 对方看不到 keeps only its eye when the words would not fit beside the buttons
    /// (a narrow panel, or 开始新会议 showing); the tooltip still says what it means.
    private void FitHeader()
    {
        if (Shared.Visibility != Visibility.Visible || Header.ActualWidth <= 0) return;
        SharedText.Visibility = Visibility.Visible;
        // Measure again with the words in: without this the cached size (maybe without them) comes back.
        for (DependencyObject? d = SharedText; d != null && d != Header; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            (d as UIElement)?.InvalidateMeasure();
        HeaderLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var room = Header.ActualWidth - HeaderRight.ActualWidth - HeaderRight.Margin.Left;
        if (HeaderLeft.DesiredSize.Width > room) SharedText.Visibility = Visibility.Collapsed;    }

    /// Top right of the work area, fully on screen (shorter than the screen when it must be).
    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 48);
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    /// Opened from the tray with no meeting going: the panel waits, and nothing records until
    /// 开始会议 (unless the user chose 单击直接开始收听 in settings).
    public void ShowReady()
    {
        var first = !placed;
        if (first)
        {
            PlaceTopRight();
            placed = true;
        }
        RenderFrom();
        ReadyPanel.Visibility = Visibility.Visible;
        // Nothing behind the button: not the last meeting's transcript, not its notes.
        LinesScroll.Visibility = Visibility.Hidden;
        NotesScroll.Visibility = Visibility.Hidden;
        LiveDot.Visibility = Visibility.Collapsed;
        UiText.Set(StateText, "Text", () => L.T("未开始"));
        StateText.Foreground = Theme.Grey;
        OnAirPill.Background = IdlePillFill;
        OnAirPill.BorderBrush = IdlePillBorder;
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        clock.Stop();
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Collapsed;
        EndBanner.Visibility = Visibility.Collapsed;
        var wasVisible = IsVisible;
        Show();
        if (first) PlaceTopRight();
        if (!wasVisible) Motion.PopIn(PanelShell, offsetY: -10, fromScale: 0.97, ms: 230);
    }

    private void RenderFrom() =>
        ReadyText.Text = model.Settings.ListenMic
            ? $"点「开始会议」后，{Brand.Name}会同时收听电脑里的声音和你的麦克风，实时记录逐字稿并区分发言人。"
            : $"点「开始会议」后，{Brand.Name}会开始收听电脑里的声音，实时记录逐字稿并区分发言人。";

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsListening) return;
        await ((App)Application.Current).ToggleListening();
    }

    public void ShowNoFocus()
    {
        ReadyPanel.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Collapsed;
        LinesScroll.Visibility = TabNotes.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        NotesScroll.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var first = !placed;
        if (first)
        {
            PlaceTopRight();
            placed = true;
        }
        if (model.LiveSessionId != sessionId)
        {
            // A new meeting: start from an empty page.
            sessionId = model.LiveSessionId;
            startedAt = DateTime.Now;
            lines.Clear();
            LivePanel.Visibility = Visibility.Collapsed;
            EndBanner.Visibility = Visibility.Collapsed;
            TabLines.IsChecked = true;
        }
        LiveDot.Visibility = Visibility.Visible;
        UiText.Set(StateText, "Text", () => L.T("收听中"));
        StateText.Foreground = OnAirText;
        OnAirPill.Background = OnAirFill;
        OnAirPill.BorderBrush = OnAirBorder;
        StopBtn.Visibility = Visibility.Visible;
        clock.Start();
        RenderNotes();
        var wasVisible = IsVisible;
        Show();
        // Windows may place a tool window on its own the first time it appears: put it back.
        if (first) PlaceTopRight();
        if (!wasVisible) Motion.PopIn(PanelShell, offsetY: -10, fromScale: 0.97, ms: 230);
    }

    /// Developer snapshot: show a stored meeting's lines and notes as the live panel would.
    public void Preview(string id, bool notesTab)
    {
        sessionId = id;
        if (model.Store.Snapshot(id) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn, model.Meeting is { } m ? m.Shown : null);
        EmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        model.Notes.Show(id);
        RenderNotes();
        TabNotes.IsChecked = notesTab;
        TabLines.IsChecked = !notesTab;
        ClockText.Text = "12:34";
    }

    /// Developer snapshot: the 对方看不到 mark, as when hidden from captures.
    public void PreviewShared()
    {
        Shared.Visibility = Visibility.Visible;
        UpdateLayout();
        FitHeader();
    }

    // Light Liquid Glass pill brushes for active recording vs idle/stopped states
    private static readonly System.Windows.Media.Brush OnAirFill = FrozenA(0x1A, 0xEF, 0x44, 0x44);
    private static readonly System.Windows.Media.Brush OnAirBorder = FrozenA(0x30, 0xEF, 0x44, 0x44);
    private static readonly System.Windows.Media.Brush OnAirText = FrozenA(0xFF, 0xDC, 0x26, 0x26);
    private static readonly System.Windows.Media.Brush IdlePillFill = FrozenA(0x12, 0x16, 0x28, 0x4C);
    private static readonly System.Windows.Media.Brush IdlePillBorder = FrozenA(0x1A, 0x16, 0x28, 0x4C);

    private static System.Windows.Media.Brush FrozenA(byte a, byte r, byte g, byte b)
    {
        var x = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    public void OnStopped()
    {
        clock.Stop();
        LiveDot.Visibility = Visibility.Collapsed;
        var autoNotes = model.Ai.HasKey || model.CliGeneratingId != null || (model.Settings.AutoCliNotesOnEnd && model.CliBridgeReady);
        UiText.Set(StateText, "Text", () => autoNotes ? L.T("已结束 · 正在整理纪要") : L.T("已结束"));
        StateText.Foreground = Theme.Grey;
        OnAirPill.Background = IdlePillFill;
        OnAirPill.BorderBrush = IdlePillBorder;
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        LivePanel.Visibility = Visibility.Collapsed;
        EndBanner.Visibility = Visibility.Collapsed;
        // The notes are being rewritten over the whole meeting: show them.
        if (IsVisible && autoNotes) TabNotes.IsChecked = true;
    }

    private void Tick()
    {
        ClockText.Text = TranscriptExport.Clock((DateTime.Now - startedAt).TotalSeconds);
        if (pending != null)
        {
            var left = Math.Max(0, (int)(pending.Deadline - DateTime.Now).TotalSeconds);
            var why = pending.Reason is MeetingEndWatch.Reason.AppLeft a ? L.F("看起来 {0} 的会议已经结束", a.Name) : L.T("已经有一阵没人说话了");
            UiText.Set(EndText, "Text", () => L.F("{0}，{1} 秒后自动结束本场记录。", why, left));
        }
    }

    public void ShowEndPending(MeetingEndWatch.Pending? p)
    {
        pending = p;
        EndBanner.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        Tick();
    }

    /// A click on a speaker's name: name them for the whole meeting.
    private void Who_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineVM { Renamable: true } vm } chip || sessionId is not { } id) return;
        e.Handled = true;
        var number = vm.Number!.Value;
        var voice = vm.Voice!.Value;
        var current = vm.HasName ? vm.Who : "";
        // Screen pixels → DIPs, so the box lands under the name at any display scale.
        var at = chip.PointToScreen(new Point(0, chip.ActualHeight + 2));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target) at = target.TransformFromDevice.Transform(at);
        SpeakerNameWindow.Ask(at, number, current, name =>
        {
            model.RenameVoice(id, voice, name);
            if (model.Store.Snapshot(id) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn, model.Meeting is { } m ? m.Shown : null);
        }, hideFromCaptures: model.Settings.HideFromCaptures);
    }

    private void OnLine(string id)
    {
        if (sessionId == null) return;
        var atBottom = LinesScroll.VerticalOffset >= LinesScroll.ScrollableHeight - 30;
        if (model.Store.Snapshot(sessionId) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn, model.Meeting is { } m ? m.Shown : null);
        EmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (atBottom) LinesScroll.ScrollToEnd();
    }

    private void OnSubtitle(MeetingController.Subtitle s)
    {
        if (!IsVisible) return;
        // Only the line being said shows here; finished lines are in the list.
        if (s.Live)
        {
            var atBottom = LinesScroll.VerticalOffset >= LinesScroll.ScrollableHeight - 30;
            LivePanel.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            LiveSrc.Text = s.Partial;
            LiveDst.Text = model.Settings.TranslationOn ? s.PartialTranslation : "";
            LiveDst.Visibility = LiveDst.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UiText.Set(LiveWho, "Text", () => L.T("正在说…"));
            if (atBottom) LinesScroll.ScrollToEnd();
        }
        else
        {
            LivePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderNotes()
    {
        var n = model.Notes;
        NotesHost.Content = n.Notes.IsEmpty
            ? new System.Windows.Controls.TextBlock
            {
                FontSize = 13, Foreground = Theme.Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                Text = model.CliBridgeReady
                    ? (model.Settings.AutoCliNotesOnEnd
                        ? "会议结束后将自动通过本地 CLI 整理纪要，也可以在主界面点「生成纪要」随时生成。"
                        : "本地 CLI 已就绪，会议结束后可在主界面点「生成纪要」一键生成。")
                    : !model.Ai.HasKey ? "会议结束后可在主界面点「生成纪要」，或在「设置 › 本地 CLI」启用自动整理纪要。"
                    : model.Settings.LiveNotes ? L.T("聊上几句后，AI 会在这里整理纪要，之后大约每 45 秒刷新一次。")
                    : L.T("开会时不实时整理纪要，会议结束后会自动整理一次。"),
            }
            : NotesRenderer.Build(n.Notes, (n.ShownId is { } shown ? model.Store.Get(shown)?.StartedAt : null) ?? DateTime.Now,
                names: n.ShownId is { } named && model.Store.Snapshot(named) is { } s ? SpeakerLabels.Names(s) : null,
                localizeHeadings: model.TemplateFor(n.ShownId is { } templateSession ? model.Store.Get(templateSession) : null).BuiltIn);
        UiText.Set(NotesState, "Text", () => n.Status switch
        {
            NotesEngine.State.Updating => L.T("纪要整理中…"),
            NotesEngine.State.RateLimited => L.T("请求太多，稍后再试"),
            NotesEngine.State.InvalidKey => L.T("AI 服务的 key 不可用"),
            NotesEngine.State.Failed => L.T("纪要这次没刷新"),
            _ => "",
        });
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Waiting for 开始会议: the tabs don't uncover the last meeting behind the button.
        if (LinesScroll == null || ReadyPanel.Visibility == Visibility.Visible) return;
        var notes = TabNotes.IsChecked == true;
        LinesScroll.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
        NotesScroll.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsListening) await ((App)Application.Current).ToggleListening();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        if (model.IsListening) ((App)Application.Current).PanelHidden();
    }

    private void KeepGoing_Click(object sender, RoutedEventArgs e)
    {
        model.KeepGoing();
        ShowEndPending(null);
    }
}
