using SanqiuTingji.Localization;
using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SanqiuTingji.App;

/// New versions come primarily from the China-direct Cloudflare domain (tingji.liwu.dpdns.org),
/// falling back automatically to GitHub Releases if unreachable. Checked a little after start and
/// every six hours; nothing is downloaded until the user clicks 立即更新 in the 发现新版本 box.
public sealed class Updates
{
    public const string Repo = "https://github.com/lisanqiu9-ops/sanqiu-tingji";
    public const string MirrorBase = "https://tingji.liwu.dpdns.org";
    public const string PrimaryFeedUrl = MirrorBase + "/releases";

    private readonly UpdateManager? manager;
    private UpdateInfo? available;
    private UpdateInfo? ready;
    private bool checking;

    public event Action? Changed;

    public Updates()
    {
        try
        {
            var primary = new SimpleWebSource(PrimaryFeedUrl, new FastFeedDownloader());
            var fallback = new GithubSource(Repo, null, false);
            manager = new UpdateManager(new FallbackUpdateSource(primary, fallback));
        }
        catch (Exception e)
        {
            Log.Debug($"updates: unavailable {e.Message}");
        }
    }

    public bool Installed => manager?.IsInstalled == true;

    public string Version => manager?.CurrentVersion?.ToString()
                             ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// A newer version found by the last check (not downloaded yet, perhaps).
    public string? AvailableVersion => available?.TargetFullRelease.Version.ToString();

    /// Downloaded and waiting to be applied.
    public string? ReadyVersion => ready?.TargetFullRelease.Version.ToString();

    private Func<string> status = () => "";
    public string Status => status();
    private void SetStatus(Func<string> message) => status = L.Capture(message);

    /// Checks whether there is a newer version (mirror first, GitHub fallback). Downloads nothing.
    public async Task<bool> Check()
    {
        if (manager == null || !Installed)
        {
            SetStatus(() => L.T("当前不是安装版（开发版），不能自动更新"));
            Changed?.Invoke();
            return false;
        }
        if (checking) return available != null;
        checking = true;
        SetStatus(() => L.T("正在检查更新…"));
        Changed?.Invoke();
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            available = info ?? available;
            SetStatus(() => info != null ? L.F("发现新版本 {0}", AvailableVersion) : available != null ? L.F("发现新版本 {0}", AvailableVersion) : L.T("已是最新版本"));
            if (info != null) Log.Debug($"updates: {AvailableVersion} available");
            return available != null;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: check failed {e.Message}");
            SetStatus(() => L.T("检查更新失败，请检查网络后再试"));
            return false;
        }
        finally
        {
            checking = false;
            Changed?.Invoke();
        }
    }

    /// Downloads the available version, reporting 0–100. True when it's ready to apply.
    public async Task<bool> Download(Action<int> progress)
    {
        if (manager == null || available == null) return false;
        if (ready?.TargetFullRelease.Version == available.TargetFullRelease.Version) return true;
        try
        {
            var shown = -1;
            await manager.DownloadUpdatesAsync(available, percent =>
            {
                // Velopack reports often; pass on only whole-percent moves.
                if (percent == shown) return;
                shown = percent;
                progress(percent);
            });
            ready = available;
            SetStatus(() => L.F("新版本 {0} 已下载", ReadyVersion));
            Log.Debug($"updates: {ReadyVersion} downloaded");
            return true;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: download failed {e.Message}");
            SetStatus(() => L.T("下载失败，请检查网络后再试"));
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// What's new in the available version, as short lines: from the package's embedded notes first,
    /// then from the China-direct mirror, and finally from the GitHub Releases API.
    public async Task<List<string>> Notes()
    {
        if (available == null) return [];
        var lines = Bullets(available.TargetFullRelease.NotesMarkdown);
        if (lines.Count > 0) return lines;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SanqiuTingji-Updater");

        try
        {
            var md = await http.GetStringAsync($"{MirrorBase}/releases/notes/v{AvailableVersion}.md");
            var mirrorLines = Bullets(md);
            if (mirrorLines.Count > 0) return mirrorLines;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: mirror notes unavailable ({e.GetType().Name})");
        }

        try
        {
            var json = await http.GetStringAsync($"https://api.github.com/repos/lisanqiu9-ops/sanqiu-tingji/releases/tags/v{AvailableVersion}");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("body", out var body) ? Bullets(body.GetString()) : [];
        }
        catch (Exception e)
        {
            Log.Debug($"updates: notes unavailable {e.Message}");
            return [];
        }
    }

    /// The "- …" lines of the notes, leaving out download/upgrade instructions and what follows "---";
    /// trailing links go, other links keep their text, and inline markdown backticks/bold markers are stripped.
    internal static List<string> Bullets(string? markdown)
    {
        var outp = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return outp;
        var section = "";
        static bool SkipSection(string s) =>
            s is "下载" or "下载安装" or "下载与升级" or "如何更新" or "首次使用提示";
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "---") break;
            if (line.StartsWith("#")) section = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") && !SkipSection(section))
            {
                var text = System.Text.RegularExpressions.Regex.Replace(line[2..],
                    @"\s*\[[^\]]+\]\([^)]+\)(\s*·\s*\[[^\]]+\]\([^)]+\))*\s*$", "");
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
                outp.Add(text.Replace("**", "").Replace("`", ""));
            }
        }
        return outp;
    }

    /// Applies the downloaded version and starts it. The caller has already stopped the meeting.
    public void ApplyAndRestart()
    {
        if (manager == null || ready == null) return;
        manager.ApplyUpdatesAndRestart(ready.TargetFullRelease);
    }

    /// On quit: apply a version the user already downloaded, once we have exited.
    public void ApplyOnExit()
    {
        if (manager == null || ready == null) return;
        try { manager.WaitExitThenApplyUpdates(ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { Log.Debug($"updates: apply on exit failed {e.Message}"); }
    }

    /// Keeps feed checks snappy (8s) so fallback to GitHub never stalls, while preserving full package download timeouts.
    internal sealed class FastFeedDownloader : IFileDownloader
    {
        private readonly HttpClientFileDownloader inner = new();

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            inner.DownloadBytes(url, headers, Math.Min(timeout, 8.0 / 60.0));

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            inner.DownloadString(url, headers, Math.Min(timeout, 8.0 / 60.0));
    }

    /// Tries the China-direct mirror source first; transparently falls back to GitHub Releases on network/feed failure.
    internal sealed class FallbackUpdateSource : IUpdateSource
    {
        private readonly IUpdateSource primary;
        private readonly IUpdateSource fallback;
        private IUpdateSource active;

        public FallbackUpdateSource(IUpdateSource primary, IUpdateSource fallback)
        {
            this.primary = primary;
            this.fallback = fallback;
            active = primary;
        }

        public async Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger,
            string? appId,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
        {
            try
            {
                var feed = await primary.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
                if (feed?.Assets is { Length: > 0 })
                {
                    active = primary;
                    return feed;
                }
            }
            catch (Exception e)
            {
                Log.Debug($"updates: primary source unavailable ({e.GetType().Name}: {e.Message}), falling back to GitHub");
            }

            active = fallback;
            return await fallback.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        }

        public async Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken = default)
        {
            if (ReferenceEquals(active, primary))
            {
                try
                {
                    await primary.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Debug($"updates: primary download failed ({e.GetType().Name}: {e.Message}), retrying via GitHub");
                    var ghFeed = await fallback.GetReleaseFeed(logger, releaseEntry.PackageId, "win").ConfigureAwait(false);
                    var matched = ghFeed.Assets?.FirstOrDefault(a =>
                        string.Equals(a.FileName, releaseEntry.FileName, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        active = fallback;
                        await fallback.DownloadReleaseEntry(logger, matched, localFile, progress, cancelToken).ConfigureAwait(false);
                        return;
                    }
                    throw;
                }
            }

            await fallback.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);
        }
    }
}
