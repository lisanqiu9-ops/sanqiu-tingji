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
}
