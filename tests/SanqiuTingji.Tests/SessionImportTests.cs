namespace SanqiuTingji.Tests;

public sealed class SessionImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sanqiutingji-import-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportedRecordingKeepsFullDurationAndDoesNotReplaceLiveMeeting()
    {
        var store = new SessionStore(root);
        var live = store.Begin();
        var start = DateTime.UtcNow;
        var imported = new Session
        {
            Title = "Local video", StartedAt = start, EndedAt = start.AddHours(3), Renamed = true,
            Lines = [new() { Id = "line", T = 10790, End = 10800, Src = "Last words" }]
        };
        var id = store.Import(imported);
        imported.Lines.Clear();
        Assert.Equal(live.Id, store.LiveId);
        Assert.Null(store.GroupOf(id));
        var loaded = new SessionStore(root).Get(id)!;
        Assert.Equal(TimeSpan.FromHours(3), loaded.Duration);
        Assert.Single(loaded.Lines);
        Assert.Equal(10800d, loaded.Lines[0].End);
    }

    [Fact]
    public void InvalidImportDoesNotCreateRecord()
    {
        var store = new SessionStore(root);
        Assert.Throws<ArgumentException>(() => store.Import(new Session()));
        Assert.Empty(store.All);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Theory]
    [InlineData("file:///C:/private.wav")]
    [InlineData("--exec anything")]
    [InlineData("javascript:alert(1)")]
    public async Task LinkImportRejectsNonWebInputBeforeStartingTools(string input)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SanqiuTingji.Speech.VideoDownload.Download(input, root, CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV123", "HTTP Error 412: Precondition Failed", true)]
    [InlineData("https://b23.tv/abc", "Request is blocked by server (412)", true)]
    [InlineData("https://www.bilibili.com/video/BV123", "HTTP Error 404: Not Found", false)]
    [InlineData("https://bilibili.com.example.com/video", "HTTP Error 412", false)]
    [InlineData("https://example.com/video", "HTTP Error 412", false)]
    public void OnlyBilibiliRejectionRetriesWithoutProxy(string url, string error, bool expected) =>
        Assert.Equal(expected, SanqiuTingji.Speech.VideoDownload.ShouldRetryDirect(new Uri(url), error));

    [Fact]
    public void DownloadFailureShowsStatusWithoutLeakingPrivateUrl()
    {
        var message = SanqiuTingji.Speech.VideoDownload.FailureMessage("ERROR: https://private.example/?token=secret HTTP Error 412: Precondition Failed");
        Assert.Contains("412", message);
        Assert.DoesNotContain("private.example", message);
        Assert.DoesNotContain("secret", message);
    }

    [Fact]
    public void MediaTimeReadsMp4MvhdV0AndV1AfterMdat()
    {
        Directory.CreateDirectory(root);
        var target = new DateTime(2026, 10, 8, 2, 21, 0, DateTimeKind.Utc);
        var mp4Epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var secs = (ulong)(target - mp4Epoch).TotalSeconds;

        var v0File = Path.Combine(root, "rec-v0.m4a");
        WriteSyntheticMp4(v0File, version: 0, creationSeconds: secs);
        Assert.Equal(target, SanqiuTingji.Speech.MediaTime.Mp4CreationUtc(v0File));
        Assert.Equal(target, SanqiuTingji.Speech.MediaTime.RecordedStartUtc(v0File, 1131, new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc)));

        var v1File = Path.Combine(root, "rec-v1.mp4");
        WriteSyntheticMp4(v1File, version: 1, creationSeconds: secs);
        Assert.Equal(target, SanqiuTingji.Speech.MediaTime.Mp4CreationUtc(v1File));
    }

    [Fact]
    public void MediaTimeFallsBackToLastWriteTimeMinusDurationForNonMp4()
    {
        Directory.CreateDirectory(root);
        var wav = Path.Combine(root, "meeting.wav");
        File.WriteAllBytes(wav, [0, 1, 2, 3]);
        var writeUtc = new DateTime(2026, 10, 8, 2, 40, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(wav, writeUtc);

        var start = SanqiuTingji.Speech.MediaTime.RecordedStartUtc(wav, 600, new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 8, 2, 30, 0, DateTimeKind.Utc), start);
    }

    private static void WriteSyntheticMp4(string path, byte version, ulong creationSeconds)
    {
        static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] Box(string type, byte[] payload)
        {
            var len = (uint)(8 + payload.Length);
            return [.. Be32(len), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        byte[] mvhdBody = version == 1
            ? [1, 0, 0, 0, .. Be32((uint)(creationSeconds >> 32)), .. Be32((uint)creationSeconds), 0, 0, 0, 0]
            : [0, 0, 0, 0, .. Be32((uint)creationSeconds), 0, 0, 0, 0];
        var mvhd = Box("mvhd", mvhdBody);
        var moov = Box("moov", mvhd);
        var ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("M4A "));
        var mdat = Box("mdat", new byte[32]);
        File.WriteAllBytes(path, [.. ftyp, .. mdat, .. moov]);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
