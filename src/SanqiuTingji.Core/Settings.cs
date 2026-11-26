using System.Text.Json;

namespace SanqiuTingji;

public enum DisplayMode { Notes, Subtitles }

/// Which AI backend generates meeting notes when triggered: local subscribed CLI bridge or online cloud API.
public enum AiEngineMode { LocalCli, CloudApi }

/// Every setting, saved as %APPDATA%\SanqiuTingji\settings.json. Never holds an API key.
public sealed class AppSettings
{
    /// The AI services set up (notes, sentence translation, speaker names), and the one in use.
    public List<AiProvider> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }

    public AiProvider? ActiveProvider() =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault();

    /// "auto" follows the system languages; otherwise a target code ("zh-CN", "en"…).
    public string Target { get; set; } = "auto";
    public DisplayMode Display { get; set; } = DisplayMode.Notes;
    /// Interface only; subtitle and notes languages keep their own settings.
    public string UiLanguage { get; set; } = "auto";
    /// Listen to the microphone too, beside what the PC plays: my own words, or a room round one
    /// mic. On by default, as a meeting app records both ends.
    public bool ListenMic { get; set; } = true;
    /// The microphone listened to (an endpoint id); null: my real one, never the virtual cable.
    public string? ListenMicId { get; set; }
    /// Keep the live panel and subtitles out of screenshots, recordings and screen shares (with a
    /// badge). Off by default: people screenshot the panel. (Replaces NotesCapturable, whose saved
    /// "false" would otherwise keep hiding it.)
    public bool HideFromCaptures { get; set; }
    /// Translate the subtitles. Off by default: many meetings only need the words and the notes,
    /// and with it off nothing is sent to a translator.
    public bool TranslationOn { get; set; }
    /// Tell the other side's voices apart (发言人 1、发言人 2…). On by default.
    public bool SpeakerVoices { get; set; } = true;
    /// After a meeting, check who said what again over its whole recording (kept until then). On by default.
    public bool RedoSpeakers { get; set; } = true;
    /// Keep a compact M4A recording inside each meeting folder for synchronized playback (deletable independently).
    public bool SaveMeetingAudio { get; set; } = true;
    /// Notes updated every ~45 s while the meeting runs; off by default for local/no-API workflow.
    public bool LiveNotes { get; set; }
    /// Automatically check GitHub Releases for newer versions of SanqiuTingji. On by default.
    /// (Replaces legacy DisableAutoUpdate so v1.0.1 users upgrading to v1.0.2 get updates enabled.)
    public bool AutoUpdateEnabled { get; set; } = true;
    /// Notice ids already read by the user in the bell notification center.
    public List<string> ReadNoticeIds { get; set; } = [];
    /// Include translated lines when exporting Word/Markdown. Off by default for Chinese-only meetings.
    public bool IncludeTranslationInExport { get; set; }
    /// Optional default folder for exporting meeting minutes and transcripts.
    public string? DefaultExportDirectory { get; set; }
    /// Active AI engine mode (LocalCli = local subscribed CLI bridge, CloudApi = online OpenAI-compatible API).
    public AiEngineMode AiMode { get; set; } = AiEngineMode.LocalCli;
    /// Automatically run the configured AI engine right after a meeting stops (off by default: manual trigger).
    public bool AutoCliNotesOnEnd { get; set; }
    /// Enable automatic meeting notes generation via a local AI CLI (agy / codex / gemini / claude / custom).
    public bool CliNotesEnabled { get; set; }
    /// Selected CLI preset id ("agy", "codex", "gemini", "claude", "custom").
    public string CliNotesPreset { get; set; } = "agy";
    /// Local CLI executable name or full path.
    public string? CliNotesCommand { get; set; }
    /// Arguments passed to the local CLI executable.
    public string? CliNotesArgs { get; set; }
    /// How the prompt is delivered to the local CLI.
    public CliInputMode CliNotesInputMode { get; set; } = CliInputMode.Argument;
    /// Optional model override passed to the local CLI.
    public string? CliNotesModel { get; set; }
    /// Timeout in seconds for each local CLI notes request.
    public int CliNotesTimeoutSeconds { get; set; } = 180;
    /// Correct recognized text against the glossary (its misheard spellings and sound-alikes). On by default.
    public bool GlossaryOn { get; set; } = true;
    /// Tolerance for dialect / accent pronunciation shifts (e.g. Henan dialect uo/e, f/h, r/l, ei/ui) on 3+ char terms. On by default.
    public bool GlossaryDialectTolerance { get; set; } = true;
    /// When a line has words that sound close to a glossary term, ask the AI whether they were
    /// misheard. Off by default: it uses the user's AI quota.
    public bool GlossaryAiCheck { get; set; }
    public double SubtitleFontSize { get; set; } = 26;
    /// The notes template new meetings use (built-in id or one of the user's).
    public string DefaultTemplateId { get; set; } = NoteTemplates.DefaultId;
    /// "auto": the notes follow the subtitles' target language.
    public string NotesLanguage { get; set; } = "auto";

    public bool FollowMeetingApps { get; set; } = true;
    public MeetingEndWatch.Silence SilenceEnd { get; set; } = MeetingEndWatch.Silence.Prompt;
    public int SilenceMinutes { get; set; } = 10;

    public bool LaunchAtLogin { get; set; }
    /// A click on the tray icon starts recording at once. Off by default: the click opens the live
    /// panel, and recording starts with its 开始会议 button, so nothing is recorded by accident.
    public bool TrayClickStarts { get; set; }
    public bool OnboardingDone { get; set; }

    private static string File => Path.Combine(Paths.Roaming, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File), Json.Options) ?? new();
        }
        catch
        {
            Log.Debug("settings: unreadable, using defaults");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Roaming);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json.Options));
        }
        catch
        {
            Log.Debug("settings: save failed");
        }
    }
}
