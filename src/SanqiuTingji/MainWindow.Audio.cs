using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SanqiuTingji.Audio;

namespace SanqiuTingji.App;

/// Synchronized transcript item for the Audio Playback tab.
public sealed class PlaybackLineVM : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public double StartSec { get; init; }
    public string Timestamp { get; init; } = "00:00:00";
    public string SeekTip { get; init; } = "点击跳转到此处播放";
    public string Who { get; init; } = "";
    public Brush WhoBrush { get; init; } = Theme.Grey2;
    public string Text { get; init; } = "";

    private bool isCurrent;
    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (isCurrent == value) return;
            isCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private readonly MediaPlayer audioPlayer = new();
    private readonly DispatcherTimer audioTick = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly ObservableCollection<PlaybackLineVM> playbackLines = [];
    private string? loadedAudioSessionId;
    private string? loadedAudioPath;
    private bool audioOpened;
    private double[] waveformBars = [];
    private TimeSpan audioDuration = TimeSpan.Zero;
    private bool audioPlaying;
    private bool audioFollow = true;
    private double audioSpeed = 1.0;
    private int activePlaybackIndex = -1;
    private bool scrubbingWave;
    private int waveformRevision;

    private void InitAudioPlayback()
    {
        AudioLinesList.ItemsSource = playbackLines;
        audioTick.Tick += (_, _) => TickAudioPlayback();
        audioPlayer.MediaOpened += (_, _) =>
        {
            if (audioPlayer.NaturalDuration.HasTimeSpan)
            {
                audioDuration = audioPlayer.NaturalDuration.TimeSpan;
                UpdateAudioTimeUI();
                RenderWaveform();
            }
            // Guard: never auto-play on media open unless the user explicitly started playback.
            if (!audioPlaying) audioPlayer.Pause();
            else audioPlayer.SpeedRatio = audioSpeed;
        };
        audioPlayer.MediaEnded += (_, _) =>
        {
            audioPlaying = false;
            audioTick.Stop();
            audioPlayer.Pause();
            audioPlayer.Position = TimeSpan.Zero;
            UpdatePlayButtonUI();
            UpdateAudioTimeUI();
            RenderWaveform();
            HighlightPlaybackIndex(-1, scroll: false);
        };
    }

    private void DisposeAudioPlayback()
    {
        StopAndCloseAudio();
        audioTick.Stop();
    }

    private void StopAndCloseAudio()
    {
        audioPlaying = false;
        audioOpened = false;
        audioTick.Stop();
        try
        {
            audioPlayer.Stop();
            audioPlayer.Close();
        }
        catch { }
        loadedAudioPath = null;
        loadedAudioSessionId = null;
        audioDuration = TimeSpan.Zero;
        activePlaybackIndex = -1;
        UpdatePlayButtonUI();
    }

    /// Opens the underlying MediaPlayer lazily only when the user actively plays or seeks.
    private bool EnsureAudioOpened()
    {
        if (loadedAudioPath == null) return false;
        if (audioOpened) return true;
        audioPlayer.Open(new Uri(loadedAudioPath));
        audioOpened = true;
        return true;
    }

    private void RefreshAudioPane(Session session, bool sessionSwitched)
    {
        if (AudioPane == null) return;
        var audioPath = MeetingAudioStore.FindAudioPath(model.Store, session.Id);
        var hasAudio = audioPath != null && session.Id != model.LiveSessionId;

        if (TabAudioDot != null) TabAudioDot.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;

        if (sessionSwitched && loadedAudioSessionId != session.Id)
            StopAndCloseAudio();

        AudioAvailablePanel.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
        AudioEmptyPanel.Visibility = hasAudio ? Visibility.Collapsed : Visibility.Visible;

        if (!hasAudio)
        {
            AudioEmptyTitle.Text = session.Id == model.LiveSessionId
                ? "会议正在录制中，结束后自动生成可回放录音"
                : "本场会议暂无原始录音";
            AudioEmptyHint.Text = session.Id == model.LiveSessionId
                ? "停止收听后会自动将本场音频压缩保存为轻量 M4A 文件，届时可在这里回放并与逐字稿双向定位。"
                : "录音可能已被手动清理，或录制时未开启「保存会议录音」。所有逐字稿与会议纪要均完整保留。";
            playbackLines.Clear();
            return;
        }

        // Populate synchronized transcript lines with merged speaker colors and real meeting clock times
        var numbers = SpeakerLabels.Numbers(session.Lines);
        var names = SpeakerLabels.Names(session);
        var colors = SpeakerLabels.ColorNumbers(session);
        var startLocal = session.StartedAt.ToLocalTime();
        playbackLines.Clear();
        foreach (var l in session.Lines)
        {
            var num = numbers.TryGetValue(l.Id, out var n) ? n : (int?)null;
            var colorNum = num is { } raw && colors.TryGetValue(raw, out var cn) ? cn : num;
            var who = l.Speaker == "me" ? "我"
                : num is { } sp && names.TryGetValue(sp, out var nm) && nm.Length > 0 ? nm
                : num is { } sp2 ? $"发言人 {sp2}" : "发言人";
            var brush = l.Speaker == "me" ? Theme.Primary
                : colorNum is { } c ? Theme.Speaker(c) : Theme.Grey2;
            var offsetText = TranscriptExport.Clock(l.T);
            var realClock = startLocal.AddSeconds(Math.Max(0, l.T)).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            playbackLines.Add(new PlaybackLineVM
            {
                Id = l.Id,
                StartSec = l.T,
                Timestamp = realClock,
                SeekTip = $"音频位置 {offsetText} · 点击跳转到此处播放",
                Who = who,
                WhoBrush = brush,
                Text = l.Src,
            });
        }

        var sizeText = MeetingAudioStore.FormatSize(MeetingAudioStore.AudioBytes(model.Store, session.Id));
        var ext = System.IO.Path.GetExtension(audioPath!).TrimStart('.').ToUpperInvariant();
        AudioMetaBadge.Text = $"{ext} · {sizeText}";

        if (loadedAudioPath != audioPath || loadedAudioSessionId != session.Id)
        {
            // Load metadata & waveform ONLY — never open or auto-play MediaPlayer on session select.
            loadedAudioSessionId = session.Id;
            loadedAudioPath = audioPath;
            audioOpened = false;
            audioPlaying = false;
            audioDuration = session.Duration > TimeSpan.Zero ? session.Duration : TimeSpan.Zero;
            UpdatePlayButtonUI();
            UpdateAudioTimeUI();

            var rev = ++waveformRevision;
            waveformBars = Enumerable.Range(0, 64).Select(i => 0.22 + 0.30 * Math.Abs(Math.Sin(i * 0.45))).ToArray();
            RenderWaveform();
            _ = Task.Run(() => MeetingAudioStore.SampleWaveform(audioPath!, 64)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (rev != waveformRevision) return;
                    waveformBars = t.Result.Bars;
                    if (t.Result.Duration > TimeSpan.Zero) audioDuration = t.Result.Duration;
                    UpdateAudioTimeUI();
                    RenderWaveform();
                });
            });
        }
        else
        {
            RenderWaveform();
        }
    }

    private void AudioPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioOpened()) return;
        if (audioPlaying)
        {
            audioPlayer.Pause();
            audioPlaying = false;
            audioTick.Stop();
        }
        else
        {
            audioPlaying = true;
            audioPlayer.SpeedRatio = audioSpeed;
            audioPlayer.Play();
            audioTick.Start();
        }
        UpdatePlayButtonUI();
    }

    private void AudioSkipBack_Click(object sender, RoutedEventArgs e) => SeekRelative(-10);
    private void AudioSkipForward_Click(object sender, RoutedEventArgs e) => SeekRelative(10);

    private void SeekRelative(double deltaSeconds)
    {
        if (!EnsureAudioOpened()) return;
        var total = audioDuration.TotalSeconds;
        var next = Math.Clamp(audioPlayer.Position.TotalSeconds + deltaSeconds, 0, Math.Max(0, total - 0.1));
        SeekToSeconds(next, autoPlay: audioPlaying);
    }

    private void SeekToSeconds(double seconds, bool autoPlay)
    {
        if (!EnsureAudioOpened()) return;
        var clamped = Math.Max(0, seconds);
        if (audioDuration.TotalSeconds > 0) clamped = Math.Min(clamped, Math.Max(0, audioDuration.TotalSeconds - 0.05));
        audioPlayer.Position = TimeSpan.FromSeconds(clamped);
        if (autoPlay)
        {
            audioPlaying = true;
            audioPlayer.SpeedRatio = audioSpeed;
            audioPlayer.Play();
            audioTick.Start();
            UpdatePlayButtonUI();
        }
        else if (!audioPlaying)
        {
            audioPlayer.Pause();
        }
        UpdateAudioTimeUI();
        RenderWaveform();
        SyncHighlightForTime(clamped, scroll: true);
    }

    private void JumpToAudioAt(double seconds)
    {
        if (selected == null) return;
        if (MeetingAudioStore.FindAudioPath(model.Store, selected) == null)
        {
            ShowGlassToast("本场会议没有可播放的原始录音");
            return;
        }
        TabAudio.IsChecked = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => SeekToSeconds(seconds, autoPlay: true)));
    }

    private void TranscriptTime_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LineVM vm })
            JumpToAudioAt(vm.Seconds);
    }

    private void AudioLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaybackLineVM vm })
            SeekToSeconds(vm.StartSec, autoPlay: true);
    }

    private void AudioSpeed_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string raw } || !double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var spd)) return;
        audioSpeed = spd;
        if (audioOpened && audioPlaying) audioPlayer.SpeedRatio = audioSpeed;
    }

    private void AudioFollow_Changed(object sender, RoutedEventArgs e)
    {
        audioFollow = AudioFollowToggle.IsChecked == true;
    }

    private void TickAudioPlayback()
    {
        if (loadedAudioPath == null) return;
        var sec = audioPlayer.Position.TotalSeconds;
        UpdateAudioTimeUI();
        RenderWaveform();
        SyncHighlightForTime(sec, scroll: audioFollow);
    }

    private void SyncHighlightForTime(double seconds, bool scroll)
    {
        if (playbackLines.Count == 0) return;
        var idx = -1;
        for (var i = 0; i < playbackLines.Count; i++)
        {
            if (playbackLines[i].StartSec <= seconds + 0.25) idx = i;
            else break;
        }
        HighlightPlaybackIndex(idx, scroll);
    }

    private void HighlightPlaybackIndex(int index, bool scroll)
    {
        if (index == activePlaybackIndex) return;
        if (activePlaybackIndex >= 0 && activePlaybackIndex < playbackLines.Count)
            playbackLines[activePlaybackIndex].IsCurrent = false;
        activePlaybackIndex = index;
        if (activePlaybackIndex >= 0 && activePlaybackIndex < playbackLines.Count)
        {
            playbackLines[activePlaybackIndex].IsCurrent = true;
            if (scroll) AudioLinesList.ScrollIntoView(playbackLines[activePlaybackIndex]);
        }
    }

    private void UpdatePlayButtonUI()
    {
        if (AudioPlayGlyph == null) return;
        AudioPlayGlyph.Text = audioPlaying ? "\uE769" : "\uE768";
    }

    private void UpdateAudioTimeUI()
    {
        if (AudioCurrentTime == null || AudioTotalTime == null) return;
        var pos = audioOpened ? audioPlayer.Position : TimeSpan.Zero;
        AudioCurrentTime.Text = FormatClock(pos);
        AudioTotalTime.Text = "/ " + FormatClock(audioDuration);
    }

    private static string FormatClock(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderWaveform();

    private void WaveformCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (loadedAudioPath == null || audioDuration.TotalSeconds <= 0) return;
        scrubbingWave = true;
        WaveformCanvas.CaptureMouse();
        SeekByWaveformPoint(e.GetPosition(WaveformCanvas).X);
    }

    private void WaveformCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!scrubbingWave) return;
        SeekByWaveformPoint(e.GetPosition(WaveformCanvas).X);
    }

    private void WaveformCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!scrubbingWave) return;
        scrubbingWave = false;
        WaveformCanvas.ReleaseMouseCapture();
        SeekByWaveformPoint(e.GetPosition(WaveformCanvas).X);
    }

    private void SeekByWaveformPoint(double x)
    {
        var w = WaveformCanvas.ActualWidth;
        if (w <= 0 || audioDuration.TotalSeconds <= 0) return;
        var ratio = Math.Clamp(x / w, 0, 1);
        SeekToSeconds(ratio * audioDuration.TotalSeconds, autoPlay: audioPlaying);
    }

    private void RenderWaveform()
    {
        if (WaveformCanvas == null) return;
        var w = WaveformCanvas.ActualWidth;
        var h = WaveformCanvas.ActualHeight;
        if (w <= 4 || h <= 4 || waveformBars.Length == 0) return;

        WaveformCanvas.Children.Clear();
        var n = waveformBars.Length;
        var gap = 3.0;
        var barW = Math.Max(2.5, (w - gap * (n - 1)) / n);
        var ratio = audioOpened && audioDuration.TotalSeconds > 0 ? Math.Clamp(audioPlayer.Position.TotalSeconds / audioDuration.TotalSeconds, 0, 1) : 0;
        var playX = ratio * w;

        // Pick speaker color per bar based on nearest transcript timestamp
        Brush BrushForBar(int barIdx, bool played)
        {
            var baseBrush = Theme.Primary;
            if (playbackLines.Count > 0 && audioDuration.TotalSeconds > 0)
            {
                var sec = (barIdx + 0.5) / n * audioDuration.TotalSeconds;
                for (var i = playbackLines.Count - 1; i >= 0; i--)
                    if (playbackLines[i].StartSec <= sec) { baseBrush = playbackLines[i].WhoBrush; break; }
            }
            if (baseBrush is SolidColorBrush sc)
            {
                var c = sc.Color;
                return new SolidColorBrush(played ? c : Color.FromArgb(0x42, c.R, c.G, c.B));
            }
            return played ? Theme.Primary : new SolidColorBrush(Color.FromArgb(0x42, 0x00, 0x7A, 0xFF));
        }

        for (var i = 0; i < n; i++)
        {
            var x = i * (barW + gap);
            var barH = Math.Max(5, waveformBars[i] * (h - 6));
            var played = x + barW * 0.5 <= playX;
            var rect = new Rectangle
            {
                Width = barW,
                Height = barH,
                RadiusX = Math.Min(3, barW / 2),
                RadiusY = Math.Min(3, barW / 2),
                Fill = BrushForBar(i, played),
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, h - barH);
            WaveformCanvas.Children.Add(rect);
        }

        // Playhead line
        var line = new Rectangle
        {
            Width = 2.5,
            Height = h,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Fill = Theme.Primary,
        };
        Canvas.SetLeft(line, Math.Clamp(playX - 1.25, 0, w - 2.5));
        Canvas.SetTop(line, 0);
        WaveformCanvas.Children.Add(line);
    }

    private void ExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || MeetingAudioStore.FindAudioPath(model.Store, selected) is not { } src) return;
        var session = model.Store.Get(selected);
        var ext = System.IO.Path.GetExtension(src);
        var dialog = new SaveFileDialog
        {
            FileName = CleanFileName(session != null ? DisplayTitle(session) : selected) + ext,
            Filter = ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ? "M4A 音频|*.m4a|所有文件|*.*" : "WAV 音频|*.wav|所有文件|*.*",
            InitialDirectory = Directory.Exists(model.Settings.DefaultExportDirectory) ? model.Settings.DefaultExportDirectory : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(src, dialog.FileName, overwrite: true);
            ShowGlassToast("原声录音已导出");
        }
        catch (Exception ex)
        {
            ShowGlassToast("导出录音失败：" + ex.Message);
        }
    }

    private void DeleteAudio_Click(object sender, RoutedEventArgs e)
    {
        if (selected != null) DeleteCurrentAudio(selected);
    }

    private void DeleteCurrentAudio(string sessionId)
    {
        var bytes = MeetingAudioStore.AudioBytes(model.Store, sessionId);
        if (bytes <= 0) return;
        if (!Dialog.Confirm(this, "仅删除本场录音？",
                $"将删除本场会议的原始录音（{MeetingAudioStore.FormatSize(bytes)}），释放磁盘空间。\n\n文字逐字稿、发言人标注和会议纪要会完整保留。",
                "删除录音", danger: true)) return;
        if (loadedAudioSessionId == sessionId) StopAndCloseAudio();
        try
        {
            var freed = MeetingAudioStore.DeleteAudio(model.Store, sessionId);
            ShowGlassToast($"已删除录音（释放 {MeetingAudioStore.FormatSize(freed)}），纪要与逐字稿已保留");
            if (selected == sessionId && model.Store.Get(sessionId) is { } s)
                RefreshAudioPane(s, sessionSwitched: false);
        }
        catch (Exception ex)
        {
            ShowGlassToast("删除录音失败：" + ex.Message);
        }
    }

    private static string CleanFileName(string raw)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "会议录音" : cleaned;
    }
}
