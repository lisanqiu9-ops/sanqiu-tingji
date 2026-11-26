using System.IO;
using System.Windows;
using System.Windows.Threading;
using SanqiuTingji.Audio;
using SanqiuTingji.Localization;
using SanqiuTingji.Speech;

namespace SanqiuTingji.App;

public sealed record MediaImportSnapshot(
    bool IsActive,
    string CurrentName,
    string StageText,
    string DetailText,
    double Fraction,
    bool IsIndeterminate,
    int PendingCount,
    int LinesCount);

/// Non-blocking background queue for importing local audio/video files or video links.
/// Users can continue reading, editing, and generating notes while imports run; audio compression
/// runs asynchronously after transcript import so transcripts appear immediately.
public sealed class MediaImportQueue
{
    public static readonly string[] SupportedExtensions =
        [".wav", ".mp3", ".m4a", ".mp4", ".wma", ".aac", ".flac", ".wmv", ".avi", ".mov", ".mkv", ".webm"];

    private sealed record ImportJob(string Input, bool IsRemote, bool DistinguishVoices, string DisplayName);

    private readonly AppModel model;
    private readonly Dispatcher ui;
    private readonly object gate = new();
    private readonly Queue<ImportJob> queue = new();
    private bool processing;
    private CancellationTokenSource? currentCts;

    public MediaImportSnapshot Snapshot { get; private set; } =
        new(false, "", "", "", 0, false, 0, 0);

    public event Action<MediaImportSnapshot>? Changed;
    public event Action<string, string, int>? Imported; // sessionId, title, lineCount
    public event Action<string>? Failed;

    public MediaImportQueue(AppModel model, Dispatcher ui)
    {
        this.model = model;
        this.ui = ui;
    }

    public static bool IsSupportedFile(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static List<string> FilterSupportedFiles(IEnumerable<string> paths) =>
        paths.Where(IsSupportedFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public int EnqueueFiles(IEnumerable<string> paths, bool distinguishVoices)
    {
        var valid = FilterSupportedFiles(paths);
        if (valid.Count == 0) return 0;
        var startPump = false;
        lock (gate)
        {
            foreach (var p in valid)
                queue.Enqueue(new ImportJob(p, false, distinguishVoices, Path.GetFileName(p)));
            if (!processing)
            {
                processing = true;
                startPump = true;
            }
            PublishLocked(Snapshot.IsActive
                ? Snapshot with { PendingCount = queue.Count }
                : new MediaImportSnapshot(true, valid[0] is { } first ? Path.GetFileName(first) : "", "准备导入…", "", 0, true, Math.Max(0, queue.Count - 1), 0));
        }
        if (startPump) _ = Task.Run(PumpAsync);
        return valid.Count;
    }

    public bool EnqueueLink(string url, bool distinguishVoices)
    {
        var trimmed = url.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        var label = Uri.TryCreate(trimmed, UriKind.Absolute, out var u) ? u.Host : trimmed;
        var startPump = false;
        lock (gate)
        {
            queue.Enqueue(new ImportJob(trimmed, true, distinguishVoices, label));
            if (!processing)
            {
                processing = true;
                startPump = true;
            }
            PublishLocked(Snapshot.IsActive
                ? Snapshot with { PendingCount = queue.Count }
                : new MediaImportSnapshot(true, label, "准备下载音轨…", "", 0, true, Math.Max(0, queue.Count - 1), 0));
        }
        if (startPump) _ = Task.Run(PumpAsync);
        return true;
    }

    public void CancelCurrent()
    {
        lock (gate)
        {
            try { currentCts?.Cancel(); } catch { }
        }
    }

    public void CancelAll()
    {
        lock (gate)
        {
            queue.Clear();
            try { currentCts?.Cancel(); } catch { }
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            ImportJob job;
            int pending;
            CancellationTokenSource cts;
            lock (gate)
            {
                if (queue.Count == 0)
                {
                    processing = false;
                    PublishLocked(new MediaImportSnapshot(false, "", "", "", 0, false, 0, 0));
                    return;
                }
                job = queue.Dequeue();
                pending = queue.Count;
                cts = new CancellationTokenSource();
                currentCts = cts;
            }

            try
            {
                await RunJobAsync(job, pending, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(currentCts, cts)) currentCts = null;
                }
                cts.Dispose();
            }
        }
    }

    private async Task RunJobAsync(ImportJob job, int pendingCount, CancellationToken token)
    {
        string? tempDir = null;
        var cleanupHandledByAudioTask = false;
        try
        {
            UpdateProgress(job.DisplayName, "正在准备识别模型…", "", 0, true, 0);
            if (!await model.EnsureEngines().ConfigureAwait(false))
                throw new InvalidOperationException(L.T("请先完成识别模型下载"));
            token.ThrowIfCancellationRequested();

            var mediaPath = job.Input;
            string? title = null;
            if (job.IsRemote)
            {
                UpdateProgress(job.DisplayName, "正在下载音轨…", "首次使用会自动准备下载工具", 0, true, 0);
                tempDir = Path.Combine(Path.GetTempPath(), "SanqiuTingji-import-" + Guid.NewGuid().ToString("N"));
                var downloaded = await VideoDownload.Download(job.Input, tempDir, token).ConfigureAwait(false);
                mediaPath = downloaded.Path;
                title = downloaded.Title;
            }

            var displayName = title ?? Path.GetFileNameWithoutExtension(mediaPath);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            UpdateProgress(displayName, job.DistinguishVoices ? "正在识别并区分发言人…" : "正在快速识别音轨…", "开始解码…", 0, false, 0);

            var reporter = new Progress<MediaImportProgress>(p =>
            {
                var pct = (int)Math.Round(p.Fraction * 100);
                var elapsed = watch.Elapsed.TotalSeconds;
                var etaText = "";
                if (elapsed >= 2.5 && p.Fraction is > 0.02 and < 0.99)
                {
                    var remain = elapsed * (1.0 - p.Fraction) / p.Fraction;
                    etaText = $" · 约剩 {FormatEta(remain)}";
                }
                var detail = $"{TranscriptExport.Clock(p.ProcessedSeconds)} / {TranscriptExport.Clock(p.TotalSeconds)} · {pct}%{etaText} · {p.LinesCount} 句";
                UpdateProgress(displayName, job.DistinguishVoices ? "正在识别并区分发言人…" : "正在快速识别音轨…", detail, p.Fraction, false, p.LinesCount);
            });

            var session = await MediaImport.Recognize(
                mediaPath, model.Recognizer!, model.Models, job.DistinguishVoices, reporter, token).ConfigureAwait(false);
            if (title != null) session.Title = title;
            session.TemplateId = model.Settings.DefaultTemplateId;
            token.ThrowIfCancellationRequested();

            var importedId = model.Store.Import(session);
            var lineCount = session.Lines.Count;

            // Compress playback audio asynchronously so the user can view the transcript immediately.
            if (model.Settings.SaveMeetingAudio)
            {
                var dirToClean = tempDir;
                cleanupHandledByAudioTask = dirToClean != null;
                _ = Task.Run(() =>
                {
                    try
                    {
                        MeetingAudioStore.SaveFromMediaFile(model.Store, importedId, mediaPath);
                    }
                    finally
                    {
                        if (dirToClean != null) { try { Directory.Delete(dirToClean, true); } catch { } }
                    }
                });
            }

            RaiseUi(() => Imported?.Invoke(importedId, session.DisplayTitle, lineCount));
        }
        catch (OperationCanceledException)
        {
            RaiseUi(() => Failed?.Invoke($"已取消导入「{job.DisplayName}」"));
        }
        catch (Exception ex)
        {
            Log.Debug($"import queue: failed ({ex.GetType().Name})");
            RaiseUi(() => Failed?.Invoke($"导入「{job.DisplayName}」失败：{ex.Message}"));
        }
        finally
        {
            if (tempDir != null && !cleanupHandledByAudioTask)
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private void UpdateProgress(string name, string stage, string detail, double fraction, bool indeterminate, int linesCount)
    {
        lock (gate)
        {
            PublishLocked(new MediaImportSnapshot(true, name, stage, detail, fraction, indeterminate, queue.Count, linesCount));
        }
    }

    private void PublishLocked(MediaImportSnapshot snap)
    {
        Snapshot = snap;
        RaiseUi(() => Changed?.Invoke(snap));
    }

    private void RaiseUi(Action action)
    {
        if (ui.CheckAccess()) action();
        else ui.BeginInvoke(action);
    }

    private static string FormatEta(double seconds)
    {
        var s = Math.Max(1, (int)Math.Round(seconds));
        if (s < 60) return $"{s} 秒";
        var min = s / 60;
        var rem = s % 60;
        return rem == 0 ? $"{min} 分钟" : $"{min} 分 {rem} 秒";
    }
}
