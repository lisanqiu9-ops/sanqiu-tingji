using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SanqiuTingji.Audio;

/// Persists compact per-meeting audio (16 kHz mono AAC/M4A via Windows Media Foundation, ~14-20 MB/hour)
/// under %APPDATA%\SanqiuTingji\Sessions\<id>\audio.m4a, with waveform sampling and independent deletion
/// that preserves transcripts and notes untouched.
public static class MeetingAudioStore
{
    public const string CompressedName = "audio.m4a";
    public const string FallbackName = "audio.wav";

    private static int mfStarted;

    private static void EnsureMediaFoundation()
    {
        if (Interlocked.Exchange(ref mfStarted, 1) == 0)
            MediaFoundationApi.Startup();
    }

    public static string? FindAudioPath(SessionStore store, string sessionId)
    {
        var dir = store.Folder(sessionId);
        var m4a = Path.Combine(dir, CompressedName);
        if (File.Exists(m4a)) return m4a;
        var wav = Path.Combine(dir, FallbackName);
        if (File.Exists(wav)) return wav;
        return null;
    }

    public static long AudioBytes(SessionStore store, string sessionId)
    {
        var path = FindAudioPath(store, sessionId);
        if (path == null) return 0;
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public static (int SessionsWithAudio, long TotalBytes) StorageStats(SessionStore store)
    {
        var count = 0;
        long total = 0;
        foreach (var s in store.All)
        {
            var bytes = AudioBytes(store, s.Id);
            if (bytes > 0) { count++; total += bytes; }
        }
        return (count, total);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// Mixes up to two 16 kHz mono 16-bit PCM WAV files into one compact M4A inside the session folder.
    public static bool SaveFromChannels(SessionStore store, string sessionId, string?[] wavPaths)
    {
        var valid = wavPaths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(p => p!).ToList();
        if (valid.Count == 0) return false;
        try
        {
            var dir = store.Folder(sessionId);
            Directory.CreateDirectory(dir);
            var mixedWav = Path.Combine(dir, ".mix-tmp.wav");
            try
            {
                if (valid.Count == 1)
                    File.Copy(valid[0], mixedWav, overwrite: true);
                else
                    MixTwoMono16Wavs(valid[0], valid[1], mixedWav);

                var ok = EncodeWavToSession(mixedWav, dir);
                if (ok) store.Mutate(sessionId, s => s.HasAudio = true);
                return ok;
            }
            finally
            {
                try { if (File.Exists(mixedWav)) File.Delete(mixedWav); } catch { }
            }
        }
        catch (Exception e)
        {
            Log.Debug($"audio store: save failed {e.Message}");
            return false;
        }
    }

    /// Saves 16 kHz mono float samples (e.g. from imported audio/video) into compact M4A.
    public static bool SaveFromSamples(SessionStore store, string sessionId, float[] samples, int sampleRate = 16000)
    {
        if (samples.Length == 0) return false;
        try
        {
            var dir = store.Folder(sessionId);
            Directory.CreateDirectory(dir);
            var tempWav = Path.Combine(dir, ".import-tmp.wav");
            try
            {
                using (var writer = new WaveFileWriter(tempWav, new WaveFormat(sampleRate, 16, 1)))
                {
                    var pcm = new byte[Math.Min(samples.Length, 32768) * 2];
                    var offset = 0;
                    while (offset < samples.Length)
                    {
                        var take = Math.Min(samples.Length - offset, pcm.Length / 2);
                        for (var i = 0; i < take; i++)
                        {
                            var v = (short)Math.Clamp(samples[offset + i] * 32767f, short.MinValue, short.MaxValue);
                            pcm[2 * i] = (byte)v;
                            pcm[2 * i + 1] = (byte)(v >> 8);
                        }
                        writer.Write(pcm, 0, take * 2);
                        offset += take;
                    }
                }
                var ok = EncodeWavToSession(tempWav, dir);
                if (ok) store.Mutate(sessionId, s => s.HasAudio = true);
                return ok;
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
            }
        }
        catch (Exception e)
        {
            Log.Debug($"audio store: import save failed {e.Message}");
            return false;
        }
    }

    /// Saves an imported audio/video file's audio track as compact mono M4A inside the session folder.
    public static bool SaveFromMediaFile(SessionStore store, string sessionId, string sourceFile)
    {
        if (!File.Exists(sourceFile)) return false;
        try
        {
            var dir = store.Folder(sessionId);
            Directory.CreateDirectory(dir);
            var tempWav = Path.Combine(dir, ".import-media-tmp.wav");
            try
            {
                EnsureMediaFoundation();
                using (var reader = new MediaFoundationReader(sourceFile))
                {
                    var format = reader.WaveFormat;
                    var resampler = new Resampler(format.SampleRate, 16000);
                    var blockAlign = Math.Max(1, format.BlockAlign);
                    var bufLen = Math.Max(blockAlign, format.AverageBytesPerSecond / 2 / blockAlign * blockAlign);
                    var rawBuf = new byte[bufLen];
                    using var writer = new WaveFileWriter(tempWav, new WaveFormat(16000, 16, 1));
                    int read;
                    while ((read = reader.Read(rawBuf, 0, rawBuf.Length)) > 0)
                    {
                        var mono = resampler.Process(Resampler.ToMonoFloat(rawBuf, read, format));
                        if (mono.Length == 0) continue;
                        var pcm = new byte[mono.Length * 2];
                        for (var i = 0; i < mono.Length; i++)
                        {
                            var v = (short)Math.Clamp(mono[i] * 32767f, short.MinValue, short.MaxValue);
                            pcm[2 * i] = (byte)v;
                            pcm[2 * i + 1] = (byte)(v >> 8);
                        }
                        writer.Write(pcm, 0, pcm.Length);
                    }
                }
                var ok = EncodeWavToSession(tempWav, dir);
                if (ok) store.Mutate(sessionId, s => s.HasAudio = true);
                return ok;
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
            }
        }
        catch (Exception e)
        {
            Log.Debug($"audio store: save from media file failed {e.Message}");
            return false;
        }
    }

    private static bool EncodeWavToSession(string wavPath, string sessionDir)
    {
        var m4aPath = Path.Combine(sessionDir, CompressedName);
        var wavFallback = Path.Combine(sessionDir, FallbackName);
        try
        {
            EnsureMediaFoundation();
            using var reader = new WaveFileReader(wavPath);
            // Windows Media Foundation AAC encoder commonly accepts 44.1k/48k/24k/16k mono; try direct 48 kbps then 44.1k resample.
            try
            {
                MediaFoundationEncoder.EncodeToAac(reader, m4aPath, 48000);
            }
            catch
            {
                reader.Position = 0;
                using var resampled = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 40 };
                MediaFoundationEncoder.EncodeToAac(resampled, m4aPath, 64000);
            }
            if (File.Exists(m4aPath) && new FileInfo(m4aPath).Length > 0)
            {
                try { if (File.Exists(wavFallback)) File.Delete(wavFallback); } catch { }
                Log.Debug($"audio store: encoded {CompressedName} ({FormatSize(new FileInfo(m4aPath).Length)})");
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Debug($"audio store: AAC encode failed ({e.Message}), falling back to WAV");
        }
        File.Copy(wavPath, wavFallback, overwrite: true);
        return File.Exists(wavFallback);
    }

    private static void MixTwoMono16Wavs(string pathA, string pathB, string outputPath)
    {
        using var a = new WaveFileReader(pathA);
        using var b = new WaveFileReader(pathB);
        var format = a.WaveFormat;
        using var writer = new WaveFileWriter(outputPath, format);
        var bufA = new byte[32768];
        var bufB = new byte[32768];
        var outBuf = new byte[32768];
        while (true)
        {
            var na = a.Read(bufA, 0, bufA.Length);
            var nb = b.Read(bufB, 0, bufB.Length);
            var n = Math.Max(na, nb);
            if (n <= 0) break;
            for (var i = 0; i + 1 < n; i += 2)
            {
                int sa = i + 1 < na ? BitConverter.ToInt16(bufA, i) : 0;
                int sb = i + 1 < nb ? BitConverter.ToInt16(bufB, i) : 0;
                var sum = Math.Clamp(sa + sb, short.MinValue, short.MaxValue);
                outBuf[i] = (byte)sum;
                outBuf[i + 1] = (byte)(sum >> 8);
            }
            writer.Write(outBuf, 0, n);
        }
    }

    /// Deletes only the audio file(s) of a meeting while keeping transcript & notes intact.
    public static long DeleteAudio(SessionStore store, string sessionId)
    {
        long freed = 0;
        var dir = store.Folder(sessionId);
        foreach (var name in new[] { CompressedName, FallbackName })
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            var len = new FileInfo(p).Length;
            File.Delete(p);
            freed += len;
        }
        if (store.Get(sessionId) != null)
            store.Mutate(sessionId, s => s.HasAudio = false);
        return freed;
    }

    /// Bulk-deletes meeting audio files (all, or older than `olderThanDays`), preserving transcripts & notes.
    public static (int DeletedCount, long FreedBytes) DeleteAllAudio(SessionStore store, int? olderThanDays = null)
    {
        var cutoff = olderThanDays is { } d ? DateTime.UtcNow.AddDays(-d) : (DateTime?)null;
        var count = 0;
        long freed = 0;
        foreach (var s in store.All)
        {
            if (s.Id == store.LiveId) continue;
            if (cutoff != null && s.StartedAt.ToUniversalTime() > cutoff.Value) continue;
            if (FindAudioPath(store, s.Id) == null) continue;
            try
            {
                var bytes = DeleteAudio(store, s.Id);
                if (bytes > 0) { count++; freed += bytes; }
            }
            catch (Exception e)
            {
                Log.Debug($"audio store: delete failed for {s.Id}: {e.Message}");
            }
        }
        return (count, freed);
    }

    /// Computes normalized RMS peaks (0.14..1.0) and duration for rendering the playback waveform.
    public static (double[] Bars, TimeSpan Duration) SampleWaveform(string audioPath, int barCount = 64)
    {
        var bars = new double[barCount];
        try
        {
            using var reader = new MediaFoundationReader(audioPath);
            var duration = reader.TotalTime;
            var sp = reader.ToSampleProvider();
            var channels = sp.WaveFormat.Channels;
            var totalSamples = Math.Max(1L, (long)(duration.TotalSeconds * sp.WaveFormat.SampleRate * channels));
            var bucketSize = Math.Max(1L, totalSamples / barCount);
            var buffer = new float[16384];
            long seen = 0;
            var sums = new double[barCount];
            var counts = new long[barCount];
            int read;
            while ((read = sp.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var idx = (int)Math.Min(barCount - 1, seen / bucketSize);
                    var v = buffer[i];
                    sums[idx] += v * v;
                    counts[idx]++;
                    seen++;
                }
            }
            double maxRms = 0.001;
            for (var i = 0; i < barCount; i++)
            {
                bars[i] = counts[i] > 0 ? Math.Sqrt(sums[i] / counts[i]) : 0;
                if (bars[i] > maxRms) maxRms = bars[i];
            }
            for (var i = 0; i < barCount; i++)
                bars[i] = Math.Clamp(0.14 + 0.86 * Math.Pow(bars[i] / maxRms, 0.72), 0.14, 1.0);
            return (bars, duration);
        }
        catch (Exception e)
        {
            Log.Debug($"audio store: waveform sample failed {e.Message}");
            for (var i = 0; i < barCount; i++) bars[i] = 0.25 + 0.35 * Math.Abs(Math.Sin(i * 0.45));
            return (bars, TimeSpan.Zero);
        }
    }
}
