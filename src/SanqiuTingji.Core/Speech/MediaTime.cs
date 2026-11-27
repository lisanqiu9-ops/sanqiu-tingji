namespace SanqiuTingji.Speech;

/// When an imported recording was made, so its transcript shows the clock times it was said
/// rather than the moment it was imported.
public static class MediaTime
{
    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earliest = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Mp4Like = [".m4a", ".mp4", ".mov", ".m4v", ".3gp", ".3g2"];

    /// The recording's start in UTC: the MP4 / M4A / MOV header's creation time (what Explorer shows
    /// as 创建媒体日期), else the file's last write time minus its length (recorders and phones write
    /// the file while recording, and copying keeps that time). Null when neither is believable.
    public static DateTime? RecordedStartUtc(string path, double durationSeconds, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        try
        {
            if (Mp4Like.Contains(Path.GetExtension(path).ToLowerInvariant())
                && Mp4CreationUtc(path) is { } created && Believable(created, now))
                return created;
            var written = File.GetLastWriteTimeUtc(path).AddSeconds(-Math.Max(0, durationSeconds));
            if (Believable(written, now)) return WholeSeconds(written);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"import: media time unreadable ({e.GetType().Name})");
        }
        return null;
    }

    private static bool Believable(DateTime t, DateTime now) => t >= Earliest && t <= now.AddDays(1);

    private static DateTime WholeSeconds(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// The creation time in the movie header (moov/mvhd), seconds since 1904 in UTC; null when absent or zero.
    public static DateTime? Mp4CreationUtc(string path)
    {
        using var s = File.OpenRead(path);
        var moov = FindBox(s, 0, s.Length, "moov");
        if (moov == null) return null;
        var mvhd = FindBox(s, moov.Value.Body, moov.Value.End, "mvhd");
        if (mvhd == null || mvhd.Value.End - mvhd.Value.Body < 12) return null;
        s.Position = mvhd.Value.Body;
        var head = new byte[12];
        if (s.Read(head, 0, head.Length) != head.Length) return null;
        ulong seconds = head[0] == 1
            ? ((ulong)ReadUInt32(head, 4) << 32) | ReadUInt32(head, 8)
            : ReadUInt32(head, 4);
        if (seconds == 0 || seconds > (ulong)(DateTime.MaxValue - Mp4Epoch).TotalSeconds) return null;
        return Mp4Epoch.AddSeconds(seconds);
    }

    private static (long Body, long End)? FindBox(Stream s, long start, long end, string type)
    {
        var header = new byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (s.Read(header, 0, 8) != 8) return null;
            long size = ReadUInt32(header, 0);
            var name = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            long headerLength = 8;
            if (size == 1)
            {
                if (s.Read(header, 8, 8) != 8) return null;
                size = (long)(((ulong)ReadUInt32(header, 8) << 32) | ReadUInt32(header, 12));
                headerLength = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerLength || pos + size > end) return null;
            if (name == type) return (pos + headerLength, pos + size);
            pos += size;
        }
        return null;
    }

    private static uint ReadUInt32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
}
