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
}
