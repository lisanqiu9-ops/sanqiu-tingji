using SanqiuTingji.Audio;

namespace SanqiuTingji.Tests;

public sealed class MeetingAudioStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sanqiutingji-audio-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void SavesCompactAudioAndDeletesAudioWithoutTouchingTranscriptOrNotes()
    {
        var store = new SessionStore(root);
        var s = store.Begin();
        store.Mutate(s.Id, x =>
        {
            x.Lines.Add(new TranscriptLine { Id = "l1", T = 1.2, Src = "测试保存录音并独立删除" });
            x.Notes = new MeetingNotes { Summary = "保留纪要摘要" };
        });
        store.End();

        // 1 second of 16 kHz sine wave
        var samples = Enumerable.Range(0, 16000).Select(i => (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / 16000.0))).ToArray();
        Assert.True(MeetingAudioStore.SaveFromSamples(store, s.Id, samples));
        var audioPath = MeetingAudioStore.FindAudioPath(store, s.Id);
        Assert.NotNull(audioPath);
        Assert.True(File.Exists(audioPath));
        Assert.True(store.Get(s.Id)!.HasAudio);
        Assert.True(MeetingAudioStore.AudioBytes(store, s.Id) > 0);

        var (bars, _) = MeetingAudioStore.SampleWaveform(audioPath!, 32);
        Assert.Equal(32, bars.Length);

        var freed = MeetingAudioStore.DeleteAudio(store, s.Id);
        Assert.True(freed > 0);
        Assert.Null(MeetingAudioStore.FindAudioPath(store, s.Id));
        var after = store.Get(s.Id)!;
        Assert.False(after.HasAudio);
        Assert.Single(after.Lines);
        Assert.Equal("保留纪要摘要", after.Notes?.Summary);
    }

    [Fact]
    public void ResamplerDecodes24BitAnd8BitPcmCorrectly()
    {
        // 24-bit signed little-endian PCM (e.g., Apple Lossless ALAC voice memo at 48 kHz mono)
        var fmt24 = new NAudio.Wave.WaveFormat(48000, 24, 1);
        byte[] raw24 =
        [
            0x00, 0x00, 0x00,       // 0.0
            0xFF, 0xFF, 0x7F,       // +max (~ +1.0)
            0x00, 0x00, 0x80,       // -min (-1.0)
            0x00, 0x00, 0x40,       // +0.5
        ];
        var mono24 = Resampler.ToMonoFloat(raw24, raw24.Length, fmt24);
        Assert.Equal(4, mono24.Length);
        Assert.Equal(0f, mono24[0], 5);
        Assert.InRange(mono24[1], 0.9999f, 1.0f);
        Assert.Equal(-1f, mono24[2], 5);
        Assert.Equal(0.5f, mono24[3], 5);

        // 8-bit unsigned PCM stereo
        var fmt8 = new NAudio.Wave.WaveFormat(16000, 8, 2);
        byte[] raw8 = [128, 128, 255, 128, 0, 0];
        var mono8 = Resampler.ToMonoFloat(raw8, raw8.Length, fmt8);
        Assert.Equal(3, mono8.Length);
        Assert.Equal(0f, mono8[0], 4);
        Assert.InRange(mono8[1], 0.49f, 0.50f);
        Assert.Equal(-1f, mono8[2], 4);
    }

    private static void SimulateCrashZeroHeaderSizes(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        // Zero RIFF size (offset 4) and the 4-byte size right after the "data" chunk tag.
        Array.Clear(bytes, 4, 4);
        var dataIdx = -1;
        for (var i = 12; i + 8 <= Math.Min(bytes.Length, 256); i++)
        {
            if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
            {
                dataIdx = i;
                break;
            }
        }
        Assert.True(dataIdx >= 36, "Expected a 'data' chunk header in generated WAV");
        Array.Clear(bytes, dataIdx + 4, 4);
        File.WriteAllBytes(wavPath, bytes);
    }

    [Fact]
    public void RepairsTruncatedWavHeaderLeftByCrash()
    {
        Directory.CreateDirectory(root);
        var wav = Path.Combine(root, "crashed.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            var pcm = new byte[16000 * 2]; // 1 second of 16 kHz mono PCM
            for (var i = 0; i < 16000; i++)
            {
                var v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
                pcm[2 * i] = (byte)v;
                pcm[2 * i + 1] = (byte)(v >> 8);
            }
            writer.Write(pcm, 0, pcm.Length);
        }

        // Simulate a crash before Dispose(): zero out RIFF chunk size and data chunk size.
        SimulateCrashZeroHeaderSizes(wav);

        Assert.True(MeetingAudioStore.RepairTruncatedWavHeader(wav));
        using var reader = new NAudio.Wave.WaveFileReader(wav);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void RecoversInterruptedRecordingsForExistingAndMissingSessions()
    {
        var store = new SessionStore(Path.Combine(root, "Sessions"));
        var existing = store.Begin();
        store.Mutate(existing.Id, x => x.Lines.Add(new TranscriptLine { Id = "l1", T = 1.0, Src = "崩前已保存的逐字稿" }));
        store.End();

        var recDir = Path.Combine(root, "Recordings");
        Directory.CreateDirectory(recDir);

        static void WriteUnfinalizedWav(string path, int seconds)
        {
            using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            {
                var pcm = new byte[16000 * 2 * seconds];
                for (var i = 0; i < 16000 * seconds; i++)
                {
                    var v = (short)(6000 * Math.Sin(2 * Math.PI * 330 * i / 16000.0));
                    pcm[2 * i] = (byte)v;
                    pcm[2 * i + 1] = (byte)(v >> 8);
                }
                writer.Write(pcm, 0, pcm.Length);
            }
            SimulateCrashZeroHeaderSizes(path);
        }

        // Case 1: Existing session missing its audio.m4a
        WriteUnfinalizedWav(Path.Combine(recDir, existing.Id + ".wav"), 1);
        // Case 2: Crash before session.json was ever written (orphan recording)
        const string orphanId = "20261126-143000";
        WriteUnfinalizedWav(Path.Combine(recDir, orphanId + ".wav"), 1);

        var report = MeetingAudioStore.RecoverInterruptedRecordings(store, recDir);
        Assert.Equal(2, report.RecoveredCount);
        Assert.True(store.Get(existing.Id)!.HasAudio);
        Assert.NotNull(MeetingAudioStore.FindAudioPath(store, existing.Id));

        var orphan = store.Get(orphanId);
        Assert.NotNull(orphan);
        Assert.Contains("已恢复", orphan!.Title);
        Assert.True(orphan.HasAudio);
        Assert.NotNull(MeetingAudioStore.FindAudioPath(store, orphanId));
        Assert.False(Directory.Exists(recDir));
    }

    [Fact]
    public void RecognizerDefaultThreadsStaysWithinSafeBounds()
    {
        Assert.InRange(SanqiuTingji.Speech.Recognizer.DefaultThreads, 4, 8);
    }
}
