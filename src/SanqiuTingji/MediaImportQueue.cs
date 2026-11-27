using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using SanqiuTingji.Audio;
using SanqiuTingji.Localization;
using SanqiuTingji.Speech;

namespace SanqiuTingji.App;

public sealed record MediaImportOptions(
    bool DistinguishVoices,
    string? GroupId = null,
    string? TemplateId = null,
    bool AutoGenerateNotes = false);

public sealed record PendingImportItem(
    string JobId,
    string DisplayName,
    bool IsRemote,
    bool DistinguishVoices,
    string? GroupId,
    string? TemplateId,
    bool AutoGenerateNotes,
    string? DurationLabel,
    DateTime? RecordedStartUtc);

public sealed record CompletedImportItem(
    string SessionId,
    string Title,
    int LineCount,
    DateTime FinishedAtUtc);

public sealed record MediaImportSnapshot(
    bool IsActive,
    string CurrentJobId,
    string CurrentName,
    string StageText,
    string DetailText,
    double Fraction,
    bool IsIndeterminate,
    int PendingCount,
    int LinesCount,
    bool CurrentDistinguishVoices,
    string? CurrentTemplateId,
    bool CurrentAutoNotes,
    DateTime? CurrentRecordedStartUtc,
    IReadOnlyList<PendingImportItem> PendingItems,
    IReadOnlyList<CompletedImportItem> RecentCompleted);

/// Non-blocking background queue for importing local audio/video files or video links.
/// Supports inspecting pending jobs, prioritizing/removing individual queued items,
/// optional auto-notes on completion, and non-disruptive batch notifications.
public sealed class MediaImportQueue
{
    public static readonly string[] SupportedExtensions =
        [".wav", ".mp3", ".m4a", ".mp4", ".wma", ".aac", ".flac", ".wmv", ".avi", ".mov", ".mkv", ".webm"];

    private sealed record ImportJob(
        string JobId,
        string Input,
        bool IsRemote,
        MediaImportOptions Options,
        string DisplayName,
        string? DurationLabel,
        DateTime? RecordedStartUtc);

    private readonly AppModel model;
    private readonly Dispatcher ui;
    private readonly object gate = new();
    private readonly List<ImportJob> pending = [];
    private readonly List<CompletedImportItem> recentCompleted = [];
    private ImportJob? activeJob;
    private bool processing;
    private int batchCompletedCount;
    private CancellationTokenSource? currentCts;

    public MediaImportSnapshot Snapshot { get; private set; } =
        new(false, "", "", "", "", 0, false, 0, 0, false, null, false, null, [], []);

    public event Action<MediaImportSnapshot>? Changed;
    /// sessionId, title, lineCount, autoOpen (true for first completion in a batch; false for subsequent items so reading isn't interrupted).
    public event Action<string, string, int, bool>? Imported;
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

    /// Quick best-effort probe for duration and original recording timestamp (never throws).
    public static (double? DurationSeconds, DateTime? RecordedStartUtc, long SizeBytes) ProbeFileMeta(string path)
    {
        long size = 0;
        double? duration = null;
        try { size = new FileInfo(path).Length; } catch { }
        try
        {
            using var reader = new MediaFoundationReader(path);
            if (reader.TotalTime.TotalSeconds > 0) duration = reader.TotalTime.TotalSeconds;
        }
        catch { }
        var recorded = MediaTime.RecordedStartUtc(path, duration ?? 0);
        return (duration, recorded, size);
    }

    public int EnqueueFiles(IEnumerable<string> paths, bool distinguishVoices) =>
        EnqueueFiles(paths, new MediaImportOptions(distinguishVoices));

    public int EnqueueFiles(IEnumerable<string> paths, MediaImportOptions options)
    {
        var valid = FilterSupportedFiles(paths);
        if (valid.Count == 0) return 0;

        var jobs = valid.Select(p =>
        {
            var (dur, rec, _) = ProbeFileMeta(p);
            var durLabel = dur is > 0 ? TranscriptExport.Clock(dur.Value) : null;
            return new ImportJob(Guid.NewGuid().ToString("N")[..8], p, false, options, Path.GetFileName(p), durLabel, rec);
        }).ToList();

        var startPump = false;
        lock (gate)
        {
            pending.AddRange(jobs);
            if (!processing)
            {
                processing = true;
                batchCompletedCount = 0;
                startPump = true;
                PublishLocked(
                    isActive: true,
                    job: jobs[0],
                    stage: "准备导入…",
                    detail: "",
                    fraction: 0,
                    indeterminate: true,
                    linesCount: 0,
                    excludeFirstPending: true);
            }
            else
            {
                PublishPendingRefreshLocked();
            }
        }
        if (startPump) _ = Task.Run(PumpAsync);
        return valid.Count;
    }

    public bool EnqueueLink(string url, bool distinguishVoices) =>
        EnqueueLink(url, new MediaImportOptions(distinguishVoices));

    public bool EnqueueLink(string url, MediaImportOptions options)
    {
        var trimmed = url.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        var label = Uri.TryCreate(trimmed, UriKind.Absolute, out var u) ? u.Host : trimmed;
        var job = new ImportJob(Guid.NewGuid().ToString("N")[..8], trimmed, true, options, label, null, null);
        var startPump = false;
        lock (gate)
        {
            pending.Add(job);
            if (!processing)
            {
                processing = true;
                batchCompletedCount = 0;
                startPump = true;
                PublishLocked(
                    isActive: true,
                    job: job,
                    stage: "准备下载音轨…",
                    detail: "",
                    fraction: 0,
                    indeterminate: true,
                    linesCount: 0,
                    excludeFirstPending: true);
            }
            else
            {
                PublishPendingRefreshLocked();
            }
        }
        if (startPump) _ = Task.Run(PumpAsync);
        return true;
    }

    public bool PrioritizePending(string jobId)
    {
        lock (gate)
        {
            var idx = pending.FindIndex(j => j.JobId == jobId);
            if (idx <= 0) return false;
            var item = pending[idx];
            pending.RemoveAt(idx);
            pending.Insert(0, item);
            PublishPendingRefreshLocked();
            return true;
        }
    }

    public bool RemovePending(string jobId)
    {
        lock (gate)
        {
            var idx = pending.FindIndex(j => j.JobId == jobId);
            if (idx < 0) return false;
            pending.RemoveAt(idx);
            PublishPendingRefreshLocked();
            return true;
        }
    }

    public int ClearPending()
    {
        lock (gate)
        {
            var removed = pending.Count;
            if (removed == 0) return 0;
            pending.Clear();
            PublishPendingRefreshLocked();
            return removed;
        }
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
            pending.Clear();
            try { currentCts?.Cancel(); } catch { }
            PublishPendingRefreshLocked();
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            ImportJob job;
            CancellationTokenSource cts;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    processing = false;
                    activeJob = null;
                    batchCompletedCount = 0;
                    PublishLocked(false, null, "", "", 0, false, 0);
                    return;
                }
                job = pending[0];
                pending.RemoveAt(0);
                activeJob = job;
                cts = new CancellationTokenSource();
                currentCts = cts;
            }

            try
            {
                await RunJobAsync(job, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(currentCts, cts)) currentCts = null;
                    if (ReferenceEquals(activeJob, job)) activeJob = null;
                }
                cts.Dispose();
            }
        }
    }

    private async Task RunJobAsync(ImportJob job, CancellationToken token)
    {
        string? tempDir = null;
        var cleanupHandledByAudioTask = false;
        try
        {
            UpdateProgress(job, "正在准备识别模型…", "", 0, true, 0);
            if (!await model.EnsureEngines().ConfigureAwait(false))
                throw new InvalidOperationException(L.T("请先完成识别模型下载"));
            token.ThrowIfCancellationRequested();

            var mediaPath = job.Input;
            string? title = null;
            if (job.IsRemote)
            {
                UpdateProgress(job, "正在下载音轨…", "首次使用会自动准备下载工具", 0, true, 0);
                tempDir = Path.Combine(Path.GetTempPath(), "SanqiuTingji-import-" + Guid.NewGuid().ToString("N"));
                var downloaded = await VideoDownload.Download(job.Input, tempDir, token).ConfigureAwait(false);
                mediaPath = downloaded.Path;
                title = downloaded.Title;
            }

            var displayName = title ?? Path.GetFileNameWithoutExtension(mediaPath);
            var activeForProgress = job with { DisplayName = displayName };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            UpdateProgress(activeForProgress, job.Options.DistinguishVoices ? "正在识别并区分发言人…" : "正在快速识别音轨…", "开始解码…", 0, false, 0);

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
                UpdateProgress(activeForProgress, job.Options.DistinguishVoices ? "正在识别并区分发言人…" : "正在快速识别音轨…", detail, p.Fraction, false, p.LinesCount);
            });

            var session = await MediaImport.Recognize(
                mediaPath, model.Recognizer!, model.Models, job.Options.DistinguishVoices, reporter, token).ConfigureAwait(false);
            if (title != null) session.Title = title;
            if (job.IsRemote)
            {
                // A downloaded video's file times say when it was published or fetched, not a meeting: date it now.
                var length = (session.EndedAt ?? session.StartedAt) - session.StartedAt;
                session.StartedAt = DateTime.UtcNow;
                session.EndedAt = session.StartedAt + length;
            }
            session.CreatedAt = DateTime.UtcNow;
            var chosenTemplate = !string.IsNullOrWhiteSpace(job.Options.TemplateId)
                ? job.Options.TemplateId
                : model.Settings.DefaultTemplateId;
            session.TemplateId = chosenTemplate;
            token.ThrowIfCancellationRequested();

            var importedId = model.Store.Import(session);
            if (!string.IsNullOrWhiteSpace(job.Options.GroupId)
                && model.Store.Groups.Any(g => g.Id == job.Options.GroupId))
            {
                try { model.Store.MoveToGroup(importedId, job.Options.GroupId); }
                catch { }
            }

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

            bool autoOpen;
            lock (gate)
            {
                batchCompletedCount++;
                autoOpen = batchCompletedCount == 1;
                recentCompleted.Insert(0, new CompletedImportItem(importedId, session.DisplayTitle, lineCount, DateTime.UtcNow));
                if (recentCompleted.Count > 5) recentCompleted.RemoveAt(recentCompleted.Count - 1);
            }

            if (job.Options.AutoGenerateNotes)
            {
                RaiseUi(() =>
                {
                    if (model.ActiveCliReady) _ = model.GenerateNotesViaCliAsync(importedId);
                    else if (model.ActiveCloudReady) _ = model.GenerateNotesViaCloudAsync(importedId);
                });
            }

            RaiseUi(() => Imported?.Invoke(importedId, session.DisplayTitle, lineCount, autoOpen));
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

    private void UpdateProgress(ImportJob job, string stage, string detail, double fraction, bool indeterminate, int linesCount)
    {
        lock (gate)
        {
            PublishLocked(true, job, stage, detail, fraction, indeterminate, linesCount);
        }
    }

    private void PublishPendingRefreshLocked()
    {
        var pendingView = pending.Select(ToPendingItem).ToList();
        var doneView = recentCompleted.ToList();
        var next = Snapshot with
        {
            PendingCount = pendingView.Count,
            PendingItems = pendingView,
            RecentCompleted = doneView,
        };
        Snapshot = next;
        RaiseUi(() => Changed?.Invoke(next));
    }

    private void PublishLocked(
        bool isActive,
        ImportJob? job,
        string stage,
        string detail,
        double fraction,
        bool indeterminate,
        int linesCount,
        bool excludeFirstPending = false)
    {
        var sourcePending = excludeFirstPending && pending.Count > 0 ? pending.Skip(1) : pending;
        var pendingView = sourcePending.Select(ToPendingItem).ToList();
        var doneView = recentCompleted.ToList();
        var snap = new MediaImportSnapshot(
            isActive,
            job?.JobId ?? "",
            job?.DisplayName ?? "",
            stage,
            detail,
            fraction,
            indeterminate,
            pendingView.Count,
            linesCount,
            job?.Options.DistinguishVoices ?? false,
            job?.Options.TemplateId,
            job?.Options.AutoGenerateNotes ?? false,
            job?.RecordedStartUtc,
            pendingView,
            doneView);
        Snapshot = snap;
        RaiseUi(() => Changed?.Invoke(snap));
    }

    private static PendingImportItem ToPendingItem(ImportJob j) => new(
        j.JobId,
        j.DisplayName,
        j.IsRemote,
        j.Options.DistinguishVoices,
        j.Options.GroupId,
        j.Options.TemplateId,
        j.Options.AutoGenerateNotes,
        j.DurationLabel,
        j.RecordedStartUtc);

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
