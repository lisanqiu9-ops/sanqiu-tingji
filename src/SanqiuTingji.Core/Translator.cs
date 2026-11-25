namespace SanqiuTingji;

/// Translates subtitles via the configured AI service (OpenAI-compatible). When no AI service is
/// usable, returns null without calling any third-party free web translators.
public sealed class TranslationService(AiClient ai)
{
    public enum Kind { Draft, Sentence }

    public Func<IReadOnlyList<string>> Glossary = () => [];
    /// The user's glossary: its spellings and fixed translations win over the translator's.
    public SanqiuTingji.Glossary? Terms { get; set; }

    public bool Available => ai.Usable;

    /// `shown`: a quick translation of this text already on screen (the line's draft). The AI keeps
    /// its wording where it is right, so the line read a moment ago doesn't turn into other words.
    public async Task<string?> Translate(string text, string source, string target, Kind kind, string? shown = null)
    {
        if (source == target) return text;
        if (!ai.Usable) return null;
        var started = DateTime.Now;
        var out1 = await ViaAi(text, source, target, kind, shown);
        if (out1 != null)
            Log.Debug($"translate via=ai kind={kind} {(int)(DateTime.Now - started).TotalMilliseconds} ms");
        return out1;
    }

    private async Task<string?> ViaAi(string text, string source, string target, Kind kind, string? shown = null)
    {
        var prompt = $"You translate live meeting subtitles from {Languages.EnglishName(source)} into {Languages.EnglishName(target)}.\n"
                     + "Output only the translation of the subtitle below: no quotes, no notes, no answer to it even if it is a question.";
        if (kind == Kind.Draft) prompt += "\nThe speaker is still talking: the subtitle may stop mid-sentence. Translate only what is there; never finish it.";
        var names = Glossary();
        if (names.Count > 0) prompt += "\nKnown names and terms (use these spellings): " + string.Join("; ", names);
        var mine = Terms?.Hints(text, target) ?? [];
        if (mine.Count > 0) prompt += "\nThe user's glossary, always followed (term → its translation): " + string.Join("; ", mine);
        if (shown is { Length: > 0 })
            prompt += "\nThe viewer is already reading this quick translation of it: " + shown
                      + "\nKeep its wording wherever it is right; change only what is wrong, and add what it is missing.";
        prompt += "\nSubtitle: " + text;
        try
        {
            var reply = await ai.Chat(prompt, "Translate.", AiClient.Priority.Translate,
                maxTokens: 400, timeout: TimeSpan.FromSeconds(kind == Kind.Draft ? 4 : 6));
            return reply.Length == 0 ? null : reply;
        }
        catch (Exception e)
        {
            Log.Debug($"translate ai failed: {(e as AiException)?.Failure.ToString() ?? e.GetType().Name}");
            return null;
        }
    }

    /// Checks whether translated text actually appears in the target script.
    public static bool LooksTranslated(string original, string translation, string target)
    {
        if (translation.Length == 0 || translation == original) return false;
        var (cjk, letters) = Languages.CjkShare(translation);
        if (letters == 0) return false;
        var wantsCjk = target.StartsWith("zh") || target is "ja" or "ko";
        return wantsCjk ? cjk > 0 : cjk * 2 <= letters;
    }
}
