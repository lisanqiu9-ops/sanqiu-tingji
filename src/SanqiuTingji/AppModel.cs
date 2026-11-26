using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Threading;
using SanqiuTingji.Audio;
using SanqiuTingji.Speech;

namespace SanqiuTingji.App;

/// The product name shown to users. Kept in one place: it is not "Cicada"/"知了", which belong to
/// the macOS project this one follows.
public static class Brand
{
    public static string Name => "三秋听记";
    public static string Tagline => "本地会议转写与智能纪要";
}

/// Wires the engines together and owns the windows. Engines raise events on worker threads; every
/// handler here hops to the UI thread first.
public sealed class AppModel
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public SessionStore Store { get; } = new();
    public AiClient Ai { get; } = new();
    public ModelStore Models { get; } = new();
    public TranslationService Translator { get; }
    public NotesEngine Notes { get; }
    public MeetingController? Meeting { get; private set; }
    public Recognizer? Recognizer { get; private set; }
    /// The user's own notes templates (built-in ones are in `NoteTemplates.BuiltIn`).
    public List<NoteTemplate> MyTemplates { get; } = NoteTemplates.LoadMine();
    /// The user's glossary: recognition is corrected against it, translations and notes follow it.
    public Glossary Glossary { get; } = new();
    /// Background media import queue (non-blocking, multi-file, with async audio compression).
    public MediaImportQueue Imports { get; }
    /// Static read-only notice feed (supports Cloudflare primary domain + GitHub fallback).
    public NoticeService Notices { get; }
    private System.Threading.Timer? noticeTimer;

    public IReadOnlyList<NoteTemplate> Templates => NoteTemplates.All(MyTemplates);

    /// The template a meeting's notes follow: its own pick, else the default.
    public NoteTemplate TemplateFor(Session? s) => NoteTemplates.Find(s?.TemplateId ?? Settings.DefaultTemplateId, MyTemplates);

    public void SaveTemplates()
    {
        NoteTemplates.SaveMine(MyTemplates);
        // A deleted default falls back to the general template.
        if (Templates.All(t => t.Id != Settings.DefaultTemplateId))
        {
            Settings.DefaultTemplateId = NoteTemplates.DefaultId;
            Settings.Save();
        }
        StateChanged?.Invoke();
    }

    public event Action? StateChanged;
    public event Action<MeetingController.Subtitle>? Subtitle;
    public event Action<string>? LineChanged;
    public event Action? NotesChanged;
    public event Action<string>? Toast;
    public event Action<MeetingEndWatch.Pending?>? EndPending;

    private readonly Dispatcher ui = Application.Current.Dispatcher;
    private Task? loading;

    public bool IsListening => Meeting?.IsListening == true;
    public string? LiveSessionId => Meeting?.SessionId;
    public string? CliGeneratingId { get; private set; }
    public string? CliGeneratingStatus { get; private set; }
    public DateTime? CliGeneratingStartedAt { get; private set; }
    public bool CliBridgeReady => CliBridge.FromSettings(Settings).IsReady;
    public bool ActiveCliReady => Settings.AiMode == AiEngineMode.LocalCli && CliBridgeReady;
    public bool ActiveCloudReady => Settings.AiMode == AiEngineMode.CloudApi && Ai.HasKey;
    private CancellationTokenSource? cliNotesCts;

    public async Task<NotesImportResult> GenerateNotesViaCloudAsync(string sessionId)
    {
        if (!Ai.HasKey || Ai.Current == null)
            return new NotesImportResult(false, "尚未配置在线云端 API Key，请先在「设置 › AI 引擎」填写并验证。");
        var session = Store.Snapshot(sessionId);
        if (session == null || session.Lines.Count == 0)
            return new NotesImportResult(false, "该会议暂无逐字稿。");

        CliGeneratingId = sessionId;
        CliGeneratingStartedAt = DateTime.UtcNow;
        CliGeneratingStatus = $"正在通过云端 API（{Ai.Describe}）总结纪要…";
        Ui(() => NotesChanged?.Invoke());
        try
        {
            await Notes.Update(sessionId, final: true);
            if (Notes.Status is NotesEngine.State.Idle && Store.Snapshot(sessionId)?.Notes is { IsEmpty: false } n)
            {
                Say($"纪要已生成：{n.Topics.Count} 个议题，{n.Todos.Count} 项待办");
                return new NotesImportResult(true, "已通过云端 API 生成纪要", n, n.Topics.Count, n.Conclusions.Count, n.Todos.Count, n.Terms.Count);
            }
            var reason = Notes.Status switch
            {
                NotesEngine.State.InvalidKey => "云端 API Key 无效或无权限，请在「设置 › AI 引擎」核对。",
                NotesEngine.State.RateLimited => "云端 API 触发限流，请稍后再试。",
                NotesEngine.State.NoKey => "尚未配置云端 API Key。",
                _ => "云端 API 返回失败或超时，请检查网络、模型名与额度。",
            };
            return new NotesImportResult(false, reason);
        }
        catch (Exception e)
        {
            Log.Debug($"cloud notes: failed {e.Message}");
            return new NotesImportResult(false, $"云端 API 生成失败：{e.Message}");
        }
        finally
        {
            CliGeneratingId = null;
            CliGeneratingStatus = null;
            CliGeneratingStartedAt = null;
            Ui(() => NotesChanged?.Invoke());
        }
    }

    public void CancelCliNotes()
    {
        if (CliGeneratingId != null)
        {
            CliGeneratingStatus = "正在取消本地 CLI 任务…";
            Ui(() => NotesChanged?.Invoke());
        }
        try { cliNotesCts?.Cancel(); } catch { }
    }

    public async Task<NotesImportResult> GenerateNotesViaCliAsync(string sessionId, CancellationToken ct = default)
    {
        var config = CliBridge.FromSettings(Settings);
        if (!config.IsReady)
            return new NotesImportResult(false, "未启用本地 CLI，请先在「设置 › 本地 CLI」中配置。");

        var session = Store.Snapshot(sessionId);
        if (session == null || session.Lines.Count == 0)
            return new NotesImportResult(false, "该会议暂无逐字稿。");

        CancelCliNotes();
        cliNotesCts?.Dispose();
        cliNotesCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cliNotesCts.Token;

        CliGeneratingId = sessionId;
        CliGeneratingStartedAt = DateTime.UtcNow;
        var presetName = CliBridge.FindPreset(config.PresetId).Name;
        CliGeneratingStatus = $"正在通过 {presetName} 静默整理纪要…";
        Ui(() => NotesChanged?.Invoke());
        try
        {
            var runner = CliBridge.CreateRunner(config);
            var template = TemplateFor(session);
            var result = await NotesWorkshop.GenerateWithRunnerAsync(
                Store, sessionId, runner, template, Glossary,
                language: "Simplified Chinese",
                maxChunkChars: config.RecommendedMaxChunkChars,
                onProgress: msg =>
                {
                    CliGeneratingStatus = msg;
                    Ui(() => NotesChanged?.Invoke());
                },
                ct: token);
            if (result.Success)
            {
                Notes.Show(sessionId);
                Say($"纪要已生成：{result.TopicCount} 个议题，{result.TodoCount} 项待办");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return new NotesImportResult(false, "已取消本次纪要整理。");
        }
        catch (Exception e)
        {
            Log.Debug($"cli notes: failed {e.Message}");
            return new NotesImportResult(false, $"本地 CLI 生成失败：{e.Message}");
        }
        finally
        {
            CliGeneratingId = null;
            CliGeneratingStatus = null;
            CliGeneratingStartedAt = null;
            Ui(() => NotesChanged?.Invoke());
        }
    }

    public static IReadOnlyList<string> SystemLanguages()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray(); }
        catch { return [System.Globalization.CultureInfo.CurrentUICulture.Name]; }
    }

    public AppModel(bool preview = false)
    {
        L.SetLanguage(Settings.UiLanguage);
        Imports = new MediaImportQueue(this, ui);
        Notices = new NoticeService(Settings, () => App.Updates.Version);
        // Recover any unfinalized PCM recordings left behind if the previous run crashed or lost power.
        if (!preview && System.IO.Directory.Exists(Paths.Recordings))
        {
            _ = Task.Run(() =>
            {
                var report = MeetingAudioStore.RecoverInterruptedRecordings(Store, Paths.Recordings);
                if (report.RecoveredCount > 0)
                    Ui(() => Toast?.Invoke($"已自动恢复 {report.RecoveredCount} 场异常中断会议的原始录音"));
            });
        }
        if (!preview)
        {
            // Quietly check announcements 10 s after launch, then every 6 h.
            noticeTimer = new System.Threading.Timer(_ => _ = RefreshNoticesQuietlyAsync(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromHours(6));
        }
        Ai.Provider = Settings.ActiveProvider;
        Ai.Refresh();
        Translator = new TranslationService(Ai);
        // Never auto-call cloud AI mid-meeting: notes are generated on explicit user trigger (or optional end-of-meeting).
        Notes = new NotesEngine(Ai, Store) { Template = TemplateFor, Terms = Glossary, Live = () => false };
        Translator.Terms = Glossary;
        Glossary.DialectTolerance = Settings.GlossaryDialectTolerance;
        // Groups limited to some templates count only in those meetings: the one being heard now,
        // else (imports, the try-it box) the default template.
        Glossary.CurrentTemplate = () => LiveSessionId is { } live ? TemplateFor(Store.Get(live)).Id : Settings.DefaultTemplateId;
        Ai.Changed += () => Ui(() => StateChanged?.Invoke());
        Notes.Changed += () => Ui(() => NotesChanged?.Invoke());
        if (!preview)
        {
            if (CliBridge.TryAutoConfigure(Settings)) Settings.Save();
            if (Ai.State == AiClient.KeyState.Unchecked) _ = Ai.Check();
            if (Models.Ready) loading = Task.Run(Load);
        }
    }

    private void Ui(Action a)
    {
        if (ui.CheckAccess()) a();
        else ui.BeginInvoke(a);
    }

    /// Loads the recognizer (~1 s for the 230 MB model) and builds the engines that need it.
    private void Load()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rec = new Recognizer(Models) { Fix = text => Settings.GlossaryOn ? Glossary.FixWithDetails(text) : (text, []) };
        var listen = new ListenEngine(rec, Models);
        // Voiceprints load on first use; without the model, lines just stay 对方.
        Voiceprints? prints = null;
        var printsFailed = false;
        Voiceprints? Prints()
        {
            if (prints != null || printsFailed) return prints;
            try { prints = new Voiceprints(Models); }
            catch (Exception e)
            {
                printsFailed = true;
                Log.Debug($"voiceprint model failed to load: {e.Message}");
            }
            return prints;
        }
        var tagger = new SpeakerTagger(Prints);
        // A long stretch holding two people (no pause between them) is cut where the voice changes.
        listen.Turns = (samples, heard) =>
        {
            if (!Settings.SpeakerVoices || Prints() is not { } p) return [];
            var finder = new TurnFinder(p.Embed, tagger.Nearest);
            return finder.Points(samples, heard.SentenceEnds());
        };
        var meeting = new MeetingController(Settings, Store, Translator, Notes, listen, SystemLanguages,
            tagger, new SpeakerNamer(Ai, Store));
        meeting.SubtitleChanged += s => Ui(() => Subtitle?.Invoke(s));
        meeting.LineChanged += id => Ui(() => LineChanged?.Invoke(id));
        meeting.StateChanged += () => Ui(() => StateChanged?.Invoke());
        meeting.Error += m => Ui(() => Toast?.Invoke(m));
        meeting.EndPending += p => Ui(() => EndPending?.Invoke(p));
        meeting.Redo = (slice, lines, step) => Prints() is { } vp ? MeetingRedo.Run(slice, lines, vp.Embed, rec.Recognize, step) : null;
        meeting.CheckLine = CheckLine;
        meeting.Redone += (id, relabelled, split) => Ui(() => Toast?.Invoke(
            L.F("已用整场录音重新校对了发言人：{0} 句有调整{1}，纪要会随之更新。", relabelled + split, (split > 0 ? $"（其中 {split} 句拆成了两个人）" : ""))));
        Recognizer = rec;
        Meeting = meeting;
        Log.Debug($"app: recognizer loaded in {watch.ElapsedMilliseconds} ms");
    }

    /// Makes sure the engines exist: false when the models still need downloading.
    public async Task<bool> EnsureEngines()
    {
        if (Meeting != null) return true;
        if (!Models.Ready) return false;
        loading ??= Task.Run(Load);
        try { await loading; }
        catch (Exception e)
        {
            loading = null;
            Log.Debug($"app: recognizer failed {e.Message}");
            Toast?.Invoke(L.T("识别模型加载失败：") + e.Message);
            return false;
        }
        return Meeting != null;
    }

    /// Developer switch (--feed): listen to this audio file instead of the PC, at real speed.
    public string? FeedFile { get; set; }
    /// Developer switch (--feed-mic): the microphone channel from this file too (with --feed), e.g. the
    /// video as a microphone hears it from the speakers.
    public string? FeedMicFile { get; set; }

    public async Task<bool> StartListening()
    {
        if (IsListening) return true;
        if (!await EnsureEngines()) return false;
        IAudioSource source;
        if (FeedFile != null)
            source = FeedMicFile != null ? new BothEnds(new FileSource(FeedFile), new FileSource(FeedMicFile)) : new FileSource(FeedFile);
        else
        {
            // Both ends of the meeting, each recognized on its own: the far side on PC speakers
            // and the local microphone can talk at once.
            var mic = Settings.ListenMic && DefaultDevices.Mics().Count > 0 ? new MicSource(ListenMicId) : null;
            source = new BothEnds(new SteadySource(new LoopbackSource()), mic);
        }
        Meeting!.Start(source);
        StateChanged?.Invoke();
        return true;
    }

    /// The room's microphone: the one chosen, else the current system default microphone.
    public string? ListenMicId() =>
        Settings.ListenMicId is { } chosen && DefaultDevices.Active(chosen) ? chosen : DefaultDevices.DefaultMicId();

    public void StopListening()
    {
        var finishedId = LiveSessionId;
        Meeting?.Stop();
        StateChanged?.Invoke();
        if (finishedId != null && Settings.AutoCliNotesOnEnd && ActiveCliReady)
        {
            var snap = Store.Snapshot(finishedId);
            if (snap is { Lines.Count: > 0 })
                _ = GenerateNotesViaCliAsync(finishedId);
        }
    }

    public void KeepGoing() => Meeting?.KeepGoing();

    /// The translation switch (panel, tray menu, settings): one place, so they all agree.
    public void SetTranslation(bool on)
    {
        if (Settings.TranslationOn == on) return;
        Settings.TranslationOn = on;
        Settings.Save();
        if (on) Meeting?.TranslationResumed();
        StateChanged?.Invoke();
    }

    /// Who said a line of the live meeting ("发言人 2", a name, or 发言人) and their number, if shown.
    public (string Who, int? Number) SpeakerOf(string? lineId)
    {
        if (lineId == null || LiveSessionId is not { } id || Store.Snapshot(id) is not { } s) return (L.T("发言人"), null);
        var line = s.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return (L.T("发言人"), null);
        var numbers = SpeakerLabels.Numbers(s.Lines);
        var names = SpeakerLabels.Names(s);
        int? number = numbers.TryGetValue(lineId, out var n) ? n : null;
        var label = line.Speaker == "me" ? L.T("我") : number is { } speaker
            ? names.GetValueOrDefault(speaker) ?? L.F("发言人 {0}", speaker) : L.T("发言人");
        return (label, number);
    }

    /// The glossary's AI check: only for a line with words that sound close to a term, and only
    /// corrections into the glossary's spellings are taken.
    private async Task<List<LineFix>?> CheckLine(string text)
    {
        if (!Settings.GlossaryAiCheck || !Settings.GlossaryOn || !Ai.Usable) return null;
        var template = Glossary.CurrentTemplate();
        var suspects = Glossary.Suspects(text, template);
        if (suspects.Count == 0) return null;
        var terms = Glossary.ActiveTerms(template);
        var reply = await Ai.Chat(GlossaryAiCheck.Prompt(text, suspects, terms), "Judge each word.", AiClient.Priority.Notes,
            json: true, maxTokens: 300, timeout: TimeSpan.FromSeconds(20), temperature: 0);
        var fixes = GlossaryAiCheck.Corrections(reply, suspects);
        Log.Debug($"glossary: AI check on {suspects.Count} suspect(s), {(fixes == null ? "no change taken" : $"{fixes.Count} correction(s)")}");
        return fixes;
    }

    /// A short message at the corner of the screen.
    public void Say(string message) => Ui(() => Toast?.Invoke(message));

    /// A misheard word written right everywhere in one meeting: each line holding it is corrected
    /// (and marked, as glossary corrections are), and optionally the generated notes too.
    /// Returns how many places changed.
    public int CorrectMeeting(string sessionId, string heard, string right, bool includeNotes = false)
    {
        if (heard.Length == 0 || heard == right) return 0;
        var changed = new List<TranscriptLine>();
        var count = 0;
        var notesTouched = false;
        Store.Mutate(sessionId, s =>
        {
            foreach (var l in s.Lines)
            {
                if (!l.Src.Contains(heard)) continue;
                count += Glossary.CountOf(l.Src, heard);
                l.Src = l.Src.Replace(heard, right);
                l.Fixes = LineFix.Merge(l.Fixes, [new LineFix { From = heard, To = right }]);
                changed.Add(l);
            }
            if (includeNotes && s.Notes != null)
            {
                var nCount = ReplaceInNotes(s.Notes, heard, right);
                if (nCount > 0) { count += nCount; notesTouched = true; }
            }
        });
        foreach (var l in changed.Where(l => l.Dst != null && Settings.TranslationOn))
        {
            var line = l;
            _ = Task.Run(async () =>
            {
                var dst = await Translator.Translate(line.Src, line.SrcLang, line.DstLang, TranslationService.Kind.Sentence);
                if (dst != null) Store.Mutate(sessionId, _ => line.Dst = dst);
            });
        }
        if (notesTouched) Ui(() => NotesChanged?.Invoke());
        return count;
    }

    /// Updates a term inside a meeting's notes (in-place edit) and optionally replaces old spellings
    /// across both transcript lines and generated notes. Returns total replacements made.
    public int UpdateMeetingTerm(string sessionId, string oldName, string newName, string newNote, IReadOnlyList<string> newHeard, bool replaceInMeeting)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return 0;
        var heardList = newHeard.Select(h => h.Trim()).Where(h => h.Length > 0 && h != newName).Distinct().ToList();
        var replacements = new List<string>();
        if (replaceInMeeting)
        {
            if (oldName.Length > 0 && oldName != newName) replacements.Add(oldName);
            foreach (var h in heardList)
                if (!replacements.Contains(h)) replacements.Add(h);
        }
        var total = 0;
        Store.Mutate(sessionId, s =>
        {
            if (s.Notes != null)
            {
                var target = s.Notes.Terms.FirstOrDefault(t => t.Name == oldName);
                if (target != null)
                {
                    target.Name = newName;
                    target.Note = newNote.Trim();
                    target.Heard = heardList;
                }
                foreach (var from in replacements)
                    total += ReplaceInNotes(s.Notes, from, newName);
            }
            if (replaceInMeeting)
            {
                foreach (var from in replacements)
                {
                    foreach (var l in s.Lines)
                    {
                        if (!l.Src.Contains(from)) continue;
                        total += Glossary.CountOf(l.Src, from);
                        l.Src = l.Src.Replace(from, newName);
                        l.Fixes = LineFix.Merge(l.Fixes, [new LineFix { From = from, To = newName }]);
                    }
                }
            }
        });
        Ui(() => NotesChanged?.Invoke());
        return total;
    }

    private static int ReplaceInNotes(MeetingNotes notes, string from, string to)
    {
        if (from.Length == 0 || from == to) return 0;
        var count = 0;
        string Sub(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains(from)) return text;
            count += Glossary.CountOf(text, from);
            return text.Replace(from, to);
        }

        if (notes.Title != null) notes.Title = Sub(notes.Title);
        notes.Summary = Sub(notes.Summary);
        foreach (var topic in notes.Topics)
        {
            topic.Title = Sub(topic.Title);
            topic.Summary = Sub(topic.Summary);
            for (var i = 0; i < topic.Points.Count; i++) topic.Points[i] = Sub(topic.Points[i]);
            if (topic.Fishbone != null)
            {
                topic.Fishbone.Question = Sub(topic.Fishbone.Question);
                foreach (var b in topic.Fishbone.Branches)
                {
                    b.Label = Sub(b.Label);
                    b.Detail = Sub(b.Detail);
                }
            }
        }
        void SubItems(List<NoteItem> items)
        {
            foreach (var it in items)
            {
                it.Text = Sub(it.Text);
                if (it.Owner != null) it.Owner = Sub(it.Owner);
            }
        }
        SubItems(notes.Conclusions);
        SubItems(notes.Todos);
        if (notes.Sections != null)
            foreach (var sec in notes.Sections) SubItems(sec.Items);
        return count;
    }

    /// Names a voice in a meeting; every view of its notes redraws with the name.
    public void RenameVoice(string sessionId, int voice, string name)
    {
        UiWatch.Time("rename voice", () =>
        {
            Store.SetVoiceName(sessionId, voice, name);
            NotesChanged?.Invoke();
        });
    }

    /// Names a merged speaker group (one or more raw voices) atomically.
    public void RenameVoices(string sessionId, IEnumerable<int> voices, string name)
    {
        UiWatch.Time("rename voices", () =>
        {
            Store.SetVoiceNames(sessionId, voices, name);
            NotesChanged?.Invoke();
        });
    }

    public void SaveSettings()
    {
        Settings.Save();
        StateChanged?.Invoke();
    }

    public async Task RefreshNoticesQuietlyAsync()
    {
        var important = await Notices.RefreshAsync().ConfigureAwait(false);
        // Never interrupt a live meeting with a toast; the bell badge lights up quietly instead.
        if (important.Count > 0 && !IsListening)
            Say($"重要通知：{important[0].Title}");
    }

    public void Shutdown()
    {
        noticeTimer?.Dispose();
        Imports.CancelAll();
        CancelCliNotes();
        if (IsListening) Meeting!.Stop();
        Store.Flush();
    }
}
