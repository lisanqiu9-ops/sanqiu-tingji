using SanqiuTingji.Audio;
using SanqiuTingji.Localization;
using NAudio.Wave;

namespace SanqiuTingji.Speech;

public sealed record MediaImportProgress(double Fraction, double ProcessedSeconds, double TotalSeconds, int LinesCount);

public static class MediaImport
{
    public static Task<Session> Recognize(string path, Recognizer recognizer, ModelStore models,
        bool distinguishVoices, IProgress<double>? progress, CancellationToken token) =>
        Recognize(path, recognizer, models, distinguishVoices,
            progress == null ? null : new Progress<MediaImportProgress>(p => progress.Report(p.Fraction)), token);

    public static async Task<Session> Recognize(string path, Recognizer recognizer, ModelStore models,
        bool distinguishVoices, IProgress<MediaImportProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double duration;
        using (var reader = new MediaFoundationReader(path)) duration = reader.TotalTime.TotalSeconds;
        if (duration <= 0) throw new InvalidOperationException(L.T("文件没有可识别的音轨"));
        using var prints = distinguishVoices ? new Voiceprints(models) : null;
        var tagger = new SpeakerTagger(() => prints);
        var result = new Session
        {
            Title = Path.GetFileNameWithoutExtension(path), Renamed = true,
            // The recording's own time, so lines and notes show when things were said.
            StartedAt = MediaTime.RecordedStartUtc(path, duration) ?? DateTime.UtcNow, HasAudio = false
        };
        result.EndedAt = result.StartedAt.AddSeconds(duration);
        using var source = new FileSource(path, realTime: false);
        using var engine = new ListenEngine(recognizer, models);
        string? failure = null;
        long lastReportTicks = 0;
        void Report(bool force = false)
        {
            if (progress == null) return;
            var now = Environment.TickCount64;
            if (!force && now - Volatile.Read(ref lastReportTicks) < 250) return;
            Volatile.Write(ref lastReportTicks, now);
            var processed = Math.Min(duration, engine.FedSeconds);
            var frac = force ? 1.0 : Math.Min(0.99, processed / duration);
            progress.Report(new MediaImportProgress(frac, processed, duration, result.Lines.Count));
        }
        engine.Failed += message => { failure = message; source.Stop(); engine.CancelInput(); };
        source.Finished += engine.CompleteInput;
        engine.Audio += (_, _) => Report();
        tagger.Merged += (from, into) =>
        {
            foreach (var line in result.Lines.Where(l => l.Voice == from)) line.Voice = into;
        };
        if (prints != null)
            engine.Turns = (samples, heard) => new TurnFinder(prints.Embed, tagger.Nearest).Points(samples, heard.SentenceEnds());
        engine.Final += segment =>
        {
            token.ThrowIfCancellationRequested();
            if (segment.Start >= duration) return;
            result.Lines.Add(new TranscriptLine
            {
                Id = Guid.NewGuid().ToString("N"), T = Math.Min(duration, segment.Start),
                End = Math.Min(duration, segment.End), Src = segment.Text, SrcLang = segment.Lang, Fixes = segment.Fixes?.ToList(),
                Voice = segment.Samples != null && SpeakerLabels.Substantive(segment.Text) ? tagger.Voice(segment.Samples) : null
            });
            Report();
        };
        engine.Start(source);
        using var registration = token.Register(() => { source.Stop(); engine.CancelInput(); });
        await engine.Completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (failure != null) throw new InvalidOperationException(failure);
        if (result.Lines.Count == 0) throw new InvalidOperationException(L.T("文件中未识别到语音"));
        Report(force: true);
        return result;
    }
}
