using SanqiuTingji.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SanqiuTingji.App;

/// The floating live transcript window ("实时听记"): never steals focus from the meeting app,
/// stays hidden from screen captures when configured, and minimizes cleanly to the tray while listening continues.
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
        SubBar.SizeChanged += (_, _) => FitSubBar();
        model.LineChanged += OnLine;
        // Speaker names change labels on lines already shown.
        model.Store.Changed += () => Dispatcher.BeginInvoke(() => OnLine(""));
        model.Subtitle += OnSubtitle;
        model.StateChanged += RenderTranslate;
        clock.Tick += (_, _) => Tick();
        RenderTranslate();
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnLine("");
    }

    private void RenderTranslate()
    {
        TranslateSwitch.IsChecked = model.Settings.TranslationOn;
        OnLine("");
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => model.SetTranslation(TranslateSwitch.IsChecked == true);

    public void ApplyCapturable()
    {
        Native.SetCapturable(this, !model.Settings.HideFromCaptures);
        RenderPrivacyToggle(model.Settings.HideFromCaptures);
        FitSubBar();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        model.Settings.HideFromCaptures = !model.Settings.HideFromCaptures;
        model.SaveSettings();
        ((App)Application.Current).SyncCapturable();
    }

    private static readonly Brush PrivacyOnBg = FrozenA(0x18, 0x00, 0x7A, 0xFF);
    private static readonly Brush PrivacyOnBorder = FrozenA(0x4A, 0x00, 0x7A, 0xFF);
    private static readonly Brush PrivacyOnFg = FrozenA(0xFF, 0x00, 0x66, 0xE0);
    private static readonly Brush PrivacyOffBg = FrozenA(0x0D, 0x16, 0x28, 0x4C);
    private static readonly Brush PrivacyOffBorder = FrozenA(0x1E, 0x16, 0x28, 0x4C);
    private static readonly Brush PrivacyOffFg = FrozenA(0xFF, 0x6B, 0x72, 0x80);

    private void RenderPrivacyToggle(bool hidden)
    {
        PrivacyBtn.Background = hidden ? PrivacyOnBg : PrivacyOffBg;
        PrivacyBtn.BorderBrush = hidden ? PrivacyOnBorder : PrivacyOffBorder;
        var fg = hidden ? PrivacyOnFg : PrivacyOffFg;
        SharedText.Foreground = fg;
        EyeContour.Stroke = fg;
        EyePupil.Fill = fg;
        EyeSlash.Stroke = fg;
        EyeSlash.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        PrivacyBtn.ToolTip = hidden
            ? "已开启防共享（亮起）：截图、录屏和屏幕共享时对方看不到本窗口；点击关闭"
            : "未开启防共享：当前共享屏幕时对方可见；点击亮起后对方将看不到本窗口";
    }

    private void FitSubBar()
    {
        if (SubBar.ActualWidth <= 0) return;
        SharedText.Visibility = SubBar.ActualWidth < 310 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 48);
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    /// Opened from the tray with no meeting going: waits until 开始收听.
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
        SubBar.Visibility = Visibility.Collapsed;
        LinesScroll.Visibility = Visibility.Hidden;
        ApplyPill(PillState.Idle, "未开始", DateTime.Now.ToString("HH:mm"));
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
            ? $"点「开始收听」后，{Brand.Name}会同时收听电脑里的声音和你的麦克风，实时转写并区分发言人。"
            : $"点「开始收听」后，{Brand.Name}会开始收听电脑里的声音，实时转写并区分发言人。";

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsListening) return;
        await ((App)Application.Current).ToggleListening();
    }

    public void ShowNoFocus()
    {
        ReadyPanel.Visibility = Visibility.Collapsed;
        SubBar.Visibility = Visibility.Visible;
        StartBtn.Visibility = Visibility.Collapsed;
        LinesScroll.Visibility = Visibility.Visible;
        var first = !placed;
        if (first)
        {
            PlaceTopRight();
            placed = true;
        }
        if (model.LiveSessionId != sessionId)
        {
            sessionId = model.LiveSessionId;
            startedAt = DateTime.Now;
            lines.Clear();
            UpdateLineCount();
            LivePanel.Visibility = Visibility.Collapsed;
            EndBanner.Visibility = Visibility.Collapsed;
        }
        ApplyPill(PillState.Listening, "收听中", TranscriptExport.Clock((DateTime.Now - startedAt).TotalSeconds));
        StopBtn.Visibility = Visibility.Visible;
        clock.Start();
        var wasVisible = IsVisible;
        Show();
        if (first) PlaceTopRight();
        if (!wasVisible) Motion.PopIn(PanelShell, offsetY: -10, fromScale: 0.97, ms: 230);
    }

    /// Developer snapshot: show the live transcript lines (privacy toggle off by default).
    public void Preview(string id)
    {
        sessionId = id;
        ReadyPanel.Visibility = Visibility.Collapsed;
        SubBar.Visibility = Visibility.Visible;
        LinesScroll.Visibility = Visibility.Visible;
        if (model.Store.Snapshot(id) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn, model.Meeting is { } m ? m.Shown : null);
        EmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLineCount();
        ApplyPill(PillState.Listening, "收听中", "00:58");
        RenderPrivacyToggle(hidden: false);
    }

    /// Developer snapshot: show the 对方看不到 quick toggle in its active (lit) state.
    public void PreviewShared()
    {
        RenderPrivacyToggle(hidden: true);
        UpdateLayout();
        FitSubBar();
    }

    private enum PillState { Listening, Idle, Saved }

    private static readonly Brush ListeningBg = FrozenA(0xFA, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ListeningBorder = FrozenA(0x38, 0xD9, 0x2D, 0x20);
    private static readonly Brush ListeningText = FrozenA(0xFF, 0xC8, 0x28, 0x1D);
    private static readonly Brush ListeningDot = FrozenA(0xFF, 0xD9, 0x2D, 0x20);
    private static readonly Brush ListeningHalo = FrozenA(0x2E, 0xD9, 0x2D, 0x20);

    private static readonly Brush IdleBg = FrozenA(0xE0, 0xFF, 0xFF, 0xFF);
    private static readonly Brush IdleBorder = FrozenA(0x22, 0x16, 0x28, 0x4C);
    private static readonly Brush IdleDot = FrozenA(0xFF, 0x94, 0x9E, 0xB2);
    private static readonly Brush IdleHalo = FrozenA(0x24, 0x94, 0x9E, 0xB2);

    private static readonly Brush SavedBg = FrozenA(0xF5, 0xF2, 0xFD, 0xF5);
    private static readonly Brush SavedBorder = FrozenA(0x38, 0x16, 0xA3, 0x4A);
    private static readonly Brush SavedText = FrozenA(0xFF, 0x15, 0x80, 0x3D);
    private static readonly Brush SavedDot = FrozenA(0xFF, 0x16, 0xA3, 0x4A);
    private static readonly Brush SavedHalo = FrozenA(0x2A, 0x16, 0xA3, 0x4A);

    private void ApplyPill(PillState state, string text, string? clockValue)
    {
        StateText.Text = text;
        switch (state)
        {
            case PillState.Listening:
                OnAirPill.Background = ListeningBg;
                OnAirPill.BorderBrush = ListeningBorder;
                StateText.Foreground = ListeningText;
                LiveDot.Fill = ListeningDot;
                LiveDotHalo.Fill = ListeningHalo;
                LiveDotHost.Visibility = Visibility.Visible;
                break;
            case PillState.Saved:
                OnAirPill.Background = SavedBg;
                OnAirPill.BorderBrush = SavedBorder;
                StateText.Foreground = SavedText;
                LiveDot.Fill = SavedDot;
                LiveDotHalo.Fill = SavedHalo;
                LiveDotHost.Visibility = Visibility.Visible;
                break;
            default:
                OnAirPill.Background = IdleBg;
                OnAirPill.BorderBrush = IdleBorder;
                StateText.Foreground = Theme.Grey;
                LiveDot.Fill = IdleDot;
                LiveDotHalo.Fill = IdleHalo;
                LiveDotHost.Visibility = Visibility.Visible;
                break;
        }
        var hasClock = !string.IsNullOrWhiteSpace(clockValue);
        ClockDivider.Visibility = hasClock ? Visibility.Visible : Visibility.Collapsed;
        ClockText.Visibility = hasClock ? Visibility.Visible : Visibility.Collapsed;
        if (hasClock) ClockText.Text = clockValue!;
    }

    private static Brush FrozenA(byte a, byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    public void OnStopped()
    {
        clock.Stop();
        var autoNotes = model.Ai.HasKey || model.CliGeneratingId != null || (model.Settings.AutoCliNotesOnEnd && model.CliBridgeReady);
        ApplyPill(PillState.Saved, autoNotes ? "已保存 · 整理纪要中" : "已保存", null);
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        LivePanel.Visibility = Visibility.Collapsed;
        EndBanner.Visibility = Visibility.Collapsed;
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
        UpdateLineCount();
        if (atBottom) LinesScroll.ScrollToEnd();
    }

    private void UpdateLineCount() =>
        LinesCountText.Text = lines.Count > 0 ? $"· {lines.Count} 句" : "";

    private void OnSubtitle(MeetingController.Subtitle s)
    {
        if (!IsVisible) return;
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
