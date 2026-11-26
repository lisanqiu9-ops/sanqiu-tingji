namespace SanqiuTingji.Tests;

public sealed class NoticeServiceTests
{
    [Fact]
    public void FiltersNoticesByVersionAndExpiryAndTracksReadState()
    {
        const string json = """
        [
          {
            "id": "n-active",
            "title": "新功能上线",
            "body": "支持增量更新与小铃铛提醒",
            "date": "2026-11-26",
            "level": "important",
            "minVersion": "1.0.0",
            "maxVersion": "1.0.9",
            "expiresAt": "2027-01-01"
          },
          {
            "id": "n-expired",
            "title": "过期公告",
            "body": "不应显示",
            "date": "2026-01-01",
            "expiresAt": "2026-06-01"
          },
          {
            "id": "n-old-only",
            "title": "仅推旧版",
            "body": "仅针对 1.0.1 及以下",
            "date": "2026-11-25",
            "maxVersion": "1.0.1"
          },
          {
            "id": "n-future-only",
            "title": "仅推新版",
            "body": "仅针对 2.0.0+",
            "date": "2026-11-27",
            "minVersion": "2.0.0"
          }
        ]
        """;

        var parsed = NoticeService.ParseAndFilter(json, "1.0.4", new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(parsed);
        Assert.Equal("n-active", parsed[0].Id);

        var settings = new AppSettings();
        var svc = new NoticeService(settings, () => "1.0.4", []);
        Assert.False(svc.IsRead("n-active"));
        settings.ReadNoticeIds.Add("n-active");
        Assert.True(svc.IsRead("n-active"));
    }

    [Fact]
    public void HandlesMalformedJsonGracefully()
    {
        var parsed = NoticeService.ParseAndFilter("{not valid json", "1.0.4", DateTime.UtcNow);
        Assert.Empty(parsed);
    }

    [Fact]
    public void PrioritizesCloudflareMirrorWithGithubFallback()
    {
        Assert.True(NoticeService.DefaultFeedUrls.Length >= 2);
        Assert.Equal("https://tingji.liwu.dpdns.org/notices.json", NoticeService.DefaultFeedUrls[0]);
        Assert.Contains(NoticeService.DefaultFeedUrls, u => u.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }
}
