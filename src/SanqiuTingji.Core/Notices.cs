using System.Net.Http;
using System.Text.Json;

namespace SanqiuTingji;

public sealed class AppNotice
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Date { get; set; } = "";
    /// "info" (quiet bell badge only) or "important" (gentle toast when not listening).
    public string Level { get; set; } = "info";
    public string? Url { get; set; }
    public string? MinVersion { get; set; }
    public string? MaxVersion { get; set; }
    public string? ExpiresAt { get; set; }
}

/// Pulls lightweight, read-only static announcements from a prioritized list of URLs
/// (ready for a Cloudflare custom domain primary URL with GitHub Raw as fallback).
/// Never uploads user data; silently skips when offline.
public sealed class NoticeService
{
    /// Primary + fallback feed URLs. Put your Cloudflare custom domain first once deployed.
    public static readonly string[] DefaultFeedUrls =
    [
        "https://raw.githubusercontent.com/lisanqiu9-ops/sanqiu-tingji/feat/local-custom/notices.json",
    ];

    private readonly AppSettings settings;
    private readonly Func<string> currentVersion;
    private readonly IReadOnlyList<string> feedUrls;
    private readonly object gate = new();
    private List<AppNotice> active = [];
    private bool refreshing;

    public event Action? Changed;

    public NoticeService(AppSettings settings, Func<string> currentVersion, IReadOnlyList<string>? feedUrls = null)
    {
        this.settings = settings;
        this.currentVersion = currentVersion;
        this.feedUrls = feedUrls is { Count: > 0 } ? feedUrls : DefaultFeedUrls;
    }

    public IReadOnlyList<AppNotice> Active
    {
        get { lock (gate) return active.ToList(); }
    }

    public bool IsRead(string id) =>
        settings.ReadNoticeIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    public int UnreadCount
    {
        get
        {
            lock (gate) return active.Count(n => !IsRead(n.Id));
        }
    }

    public List<AppNotice> UnreadImportant()
    {
        lock (gate)
            return active.Where(n => !IsRead(n.Id) && string.Equals(n.Level, "important", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void MarkRead(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!IsRead(id))
        {
            settings.ReadNoticeIds.Add(id);
            TrimReadHistory();
            settings.Save();
            Changed?.Invoke();
        }
    }

    public void MarkAllRead()
    {
        var changed = false;
        lock (gate)
        {
            foreach (var n in active)
            {
                if (!IsRead(n.Id))
                {
                    settings.ReadNoticeIds.Add(n.Id);
                    changed = true;
                }
            }
        }
        if (changed)
        {
            TrimReadHistory();
            settings.Save();
            Changed?.Invoke();
        }
    }

    private void TrimReadHistory()
    {
        if (settings.ReadNoticeIds.Count > 100)
            settings.ReadNoticeIds.RemoveRange(0, settings.ReadNoticeIds.Count - 100);
    }

    /// Fetches the first reachable feed URL; returns newly discovered unread important notices if any.
    public async Task<List<AppNotice>> RefreshAsync(CancellationToken ct = default)
    {
        if (refreshing) return [];
        refreshing = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SanqiuTingji-Notices/1.0");
            string? json = null;
            foreach (var url in feedUrls)
            {
                try
                {
                    json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json)) break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug($"notices: source unavailable ({ex.GetType().Name})");
                }
            }
            if (string.IsNullOrWhiteSpace(json)) return [];

            var filtered = ParseAndFilter(json, currentVersion(), DateTime.UtcNow);
            lock (gate) active = filtered;
            Changed?.Invoke();
            return UnreadImportant();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        finally
        {
            refreshing = false;
        }
    }

    public static List<AppNotice> ParseAndFilter(string json, string appVersion, DateTime utcNow)
    {
        List<AppNotice>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<AppNotice>>(json, Json.Options);
        }
        catch
        {
            return [];
        }
        if (raw == null || raw.Count == 0) return [];

        var cur = TryParseVersion(appVersion);
        var result = new List<AppNotice>();
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title)) continue;
            if (!string.IsNullOrWhiteSpace(item.ExpiresAt)
                && DateTime.TryParse(item.ExpiresAt, out var exp)
                && exp.Date < utcNow.Date)
                continue;
            if (cur != null)
            {
                if (TryParseVersion(item.MinVersion) is { } min && cur < min) continue;
                if (TryParseVersion(item.MaxVersion) is { } max && cur > max) continue;
            }
            result.Add(item);
        }
        return result.OrderByDescending(n => n.Date ?? "", StringComparer.Ordinal).ToList();
    }

    private static Version? TryParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Trim().TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : null;
    }
}
