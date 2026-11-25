using System.Diagnostics;
using System.Text;

namespace SanqiuTingji;

/// One ready-to-paste prompt unit produced by the no-API notes workshop.
public sealed record NotesPromptChunk(int Index, int Total, int StartLine, int EndLine, string Prompt);

/// A complete prompt package for generating meeting notes with external subscription chats or local CLIs.
public sealed record NotesPromptPackage(
    string SessionId,
    string TemplateId,
    string TemplateName,
    string Language,
    IReadOnlyList<NotesPromptChunk> Chunks)
{
    public bool IsChunked => Chunks.Count > 1;
}

/// Result of importing a raw model reply into a session's structured MeetingNotes.
public sealed record NotesImportResult(
    bool Success,
    string Message,
    MeetingNotes? Notes = null,
    int TopicCount = 0,
    int ConclusionCount = 0,
    int TodoCount = 0,
    int TermCount = 0);

/// Pluggable runner for automating no-API notes generation (e.g., local subscription CLI or local bridge).
public interface INotesRunner
{
    Task<string> RunAsync(string prompt, CancellationToken ct = default);
}

/// Executes a local command that reads the prompt from UTF-8 stdin and writes the model reply to UTF-8 stdout.
public sealed class CliNotesRunner(string fileName, string arguments = "", TimeSpan? timeout = null) : INotesRunner
{
    private readonly TimeSpan runTimeout = timeout ?? TimeSpan.FromMinutes(3);

    public async Task<string> RunAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("未配置本地 CLI 命令。");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动命令：{fileName}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(runTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token);
        await process.StandardInput.FlushAsync(linked.Token);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"本地 CLI 执行超时（>{(int)runTimeout.TotalSeconds}s）。");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"本地 CLI 退出码 {process.ExitCode}：{(stderr.Length > 0 ? stderr : stdout)}");
        if (stdout.Length == 0)
            throw new InvalidOperationException("本地 CLI 返回内容为空。");
        return stdout;
    }
}

/// No-API meeting notes workshop: builds template/glossary-aware prompts (with chunking for long meetings),
/// parses external subscription replies back into session.json, and exports clean Chinese Word/Markdown files.
public static class NotesWorkshop
{
    private const int DefaultChunkLimit = NotesBatch.MaxPromptChars;
    private const int ChunkReplyReserve = 12000;
    private const string ChunkOutputInstruction =
        "\nKeep the entire JSON under 4500 characters (hard limit 6000), including all keys. Use short phrases and fewer items as needed, retaining every template section and original evidence ids.\n";
    private const string MergeInstruction =
        "\nOrdered partial notes (JSON), earliest first. Merge these into full meeting notes. Preserve original L evidence ids and seconds from the original meeting start, never rebase times. Preserve decisions, owners, deadlines and every template section.\n";

    public static NotesPromptPackage BuildPackage(
        Session session,
        NoteTemplate? template = null,
        Glossary? glossary = null,
        string language = "Simplified Chinese",
        int maxChunkChars = DefaultChunkLimit)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Lines.Count == 0)
            throw new InvalidOperationException("该会议暂无逐字稿，无法生成纪要提示词。");

        template ??= NoteTemplates.BuiltIn[0];
        var lines = session.Lines;
        var names = SpeakerLabels.Names(session);
        var glossarySuffix = BuildGlossarySuffix(lines, template.Id, glossary);

        var singlePrompt = NotesPrompt.Build(new MeetingNotes(), lines, 0, language, final: true, names, template, session.StartedAt)
            + glossarySuffix
            + $"\n\nTask: Write the final meeting notes in {language} and return JSON only.";

        if (singlePrompt.Length <= maxChunkChars)
        {
            return new NotesPromptPackage(
                session.Id, template.Id, template.Name, language,
                [new NotesPromptChunk(1, 1, 1, lines.Count, singlePrompt)]);
        }

        var skeleton = NotesPrompt.Build(new MeetingNotes(), lines, 0, language, final: true, names, template, session.StartedAt, "")
            + ChunkOutputInstruction + glossarySuffix;
        var capacity = Math.Max(4000, maxChunkChars - skeleton.Length - 200);
        var numbers = SpeakerLabels.Numbers(lines);

        var rawChunks = new List<(int StartLine, int EndLine, string Transcript)>();
        var buffer = new StringBuilder();
        var chunkStart = 1;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var who = FormatSpeaker(line, numbers, names);
            var formatted = $"[L{i + 1} {TranscriptExport.Clock(line.T)} {who}] {line.Src}";
            if (buffer.Length > 0 && buffer.Length + formatted.Length + 1 > capacity)
            {
                rawChunks.Add((chunkStart, i, buffer.ToString()));
                buffer.Clear();
                chunkStart = i + 1;
            }
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(formatted);
        }
        if (buffer.Length > 0) rawChunks.Add((chunkStart, lines.Count, buffer.ToString()));

        var chunks = new List<NotesPromptChunk>(rawChunks.Count);
        for (var i = 0; i < rawChunks.Count; i++)
        {
            var (startLine, endLine, transcript) = rawChunks[i];
            var prompt = NotesPrompt.Build(new MeetingNotes(), lines, 0, language, final: true, names, template, session.StartedAt, transcript)
                + ChunkOutputInstruction
                + glossarySuffix
                + $"\n\nTask: This is part {i + 1} of {rawChunks.Count} (lines L{startLine}-L{endLine}). Write partial meeting notes in {language} and return JSON only.";
            chunks.Add(new NotesPromptChunk(i + 1, rawChunks.Count, startLine, endLine, prompt));
        }

        return new NotesPromptPackage(session.Id, template.Id, template.Name, language, chunks);
    }

    public static string BuildMergePrompt(
        Session session,
        IReadOnlyList<string> partialReplies,
        NoteTemplate? template = null,
        Glossary? glossary = null,
        string language = "Simplified Chinese")
    {
        ArgumentNullException.ThrowIfNull(session);
        if (partialReplies.Count == 0)
            throw new InvalidOperationException("请至少提供一段分块纪要 JSON。");

        template ??= NoteTemplates.BuiltIn[0];
        var names = SpeakerLabels.Names(session);
        var glossarySuffix = BuildGlossarySuffix(session.Lines, template.Id, glossary);
        var cleaned = partialReplies.Select(ExtractJsonText).ToList();
        var joined = MergeInstruction + string.Join("\n\n", cleaned);

        return NotesPrompt.Build(new MeetingNotes(), session.Lines, 0, language, final: true, names, template, session.StartedAt, joined)
            + glossarySuffix
            + $"\n\nTask: Merge all partial notes above into the final meeting notes in {language} and return JSON only.";
    }

    public static NotesImportResult ParseReply(
        Session session,
        string rawReply,
        NoteTemplate? template = null,
        string language = "Simplified Chinese")
    {
        ArgumentNullException.ThrowIfNull(session);
        template ??= NoteTemplates.BuiltIn[0];
        if (session.Lines.Count == 0)
            return new NotesImportResult(false, "该会议没有逐字稿，无法校验纪要证据行。");
        if (string.IsNullOrWhiteSpace(rawReply))
            return new NotesImportResult(false, "回复内容为空。");

        var jsonText = ExtractJsonText(rawReply);
        var times = session.Lines.Select(l => l.T).ToList();
        var parsed = NotesPrompt.Parse(jsonText, times, session.Lines[0].T, template);
        if (parsed == null)
            return new NotesImportResult(false, "未能解析出有效的纪要 JSON，请确认回复包含完整的大括号 JSON 对象。");

        if (!NotesPrompt.Written(parsed, language))
            return new NotesImportResult(false, $"解析出的纪要语言与目标语言（{language}）不符，请检查模型回复。");

        if (session.Notes is { IsEmpty: false } existing)
            NotesPrompt.CarryTimes(existing, parsed);

        if (string.IsNullOrWhiteSpace(parsed.Title))
            parsed.Title = session.Notes?.Title;

        parsed.Covered = session.Lines.Count;
        parsed.UpdatedAt = DateTime.UtcNow;
        parsed.Final = true;

        return new NotesImportResult(
            true,
            $"已解析纪要：{parsed.Topics.Count} 个议题，{parsed.Conclusions.Count} 条结论，{parsed.Todos.Count} 项待办，{parsed.Terms.Count} 个术语。",
            parsed,
            parsed.Topics.Count,
            parsed.Conclusions.Count,
            parsed.Todos.Count,
            parsed.Terms.Count);
    }

    public static NotesImportResult ImportReply(
        SessionStore store,
        string sessionId,
        string rawReply,
        NoteTemplate? template = null,
        string language = "Simplified Chinese")
    {
        var snapshot = store.Snapshot(sessionId);
        if (snapshot == null)
            return new NotesImportResult(false, $"未找到会议记录：{sessionId}");

        var result = ParseReply(snapshot, rawReply, template, language);
        if (!result.Success || result.Notes == null) return result;

        store.SetNotes(sessionId, result.Notes);
        store.SaveNow(sessionId);
        return result;
    }

    /// Runs the full prompt package (including chunked map/reduce for long meetings) via a local runner.
    public static async Task<NotesImportResult> GenerateWithRunnerAsync(
        SessionStore store,
        string sessionId,
        INotesRunner runner,
        NoteTemplate? template = null,
        Glossary? glossary = null,
        string language = "Simplified Chinese",
        int maxChunkChars = DefaultChunkLimit,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var snapshot = store.Snapshot(sessionId);
        if (snapshot == null)
            return new NotesImportResult(false, $"未找到会议记录：{sessionId}");

        template ??= NoteTemplates.BuiltIn[0];
        var pkg = BuildPackage(snapshot, template, glossary, language, maxChunkChars);
        if (!pkg.IsChunked)
        {
            onProgress?.Invoke("正在调用本地 CLI 整理纪要…");
            var reply = await runner.RunAsync(pkg.Chunks[0].Prompt, ct);
            return ImportReply(store, sessionId, reply, template, language);
        }

        var level = new List<string>(pkg.Chunks.Count);
        foreach (var chunk in pkg.Chunks)
        {
            onProgress?.Invoke($"长会议分段整理中（第 {chunk.Index}/{chunk.Total} 段）…");
            var partReply = await runner.RunAsync(chunk.Prompt, ct);
            var check = ParseReply(snapshot, partReply, template, language);
            if (!check.Success)
                return new NotesImportResult(false, $"第 {chunk.Index}/{chunk.Total} 段纪要解析失败：{check.Message}");
            level.Add(ExtractJsonText(partReply));
        }

        var round = 1;
        while (level.Count > 1)
        {
            var nextLevel = new List<string>();
            for (var i = 0; i < level.Count; i += 2)
            {
                onProgress?.Invoke($"正在合并分段纪要（第 {round} 轮）…");
                var group = level.Skip(i).Take(2).ToList();
                var mergePrompt = BuildMergePrompt(snapshot, group, template, glossary, language);
                var mergedReply = await runner.RunAsync(mergePrompt, ct);
                if (level.Count == 2)
                    return ImportReply(store, sessionId, mergedReply, template, language);
                var check = ParseReply(snapshot, mergedReply, template, language);
                if (!check.Success)
                    return new NotesImportResult(false, $"分段合并失败：{check.Message}");
                nextLevel.Add(ExtractJsonText(mergedReply));
            }
            level = nextLevel;
            round++;
        }

        return ImportReply(store, sessionId, level[0], template, language);
    }

    public static string ExportToDirectory(
        Session session,
        string directory,
        string format = "docx",
        bool includeTranslation = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(directory);
        var ext = format.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "md" or "markdown" => ".md",
            "txt" or "text" => ".txt",
            _ => ".docx",
        };
        var datePrefix = session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd");
        var safeTitle = SanitizeFileName(session.DisplayTitle);
        var target = Path.Combine(directory, $"{datePrefix}_{safeTitle}{ext}");
        var temp = target + ".tmp";
        try
        {
            if (ext == ".docx")
                File.WriteAllBytes(temp, WordExport.Create(session, includeTranslation));
            else
                File.WriteAllText(temp, TranscriptExport.Markdown(session, includeTranslation), new UTF8Encoding(false));

            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// Strips optional Markdown code fences around JSON replies returned by web subscription chats.
    public static string ExtractJsonText(string raw)
    {
        var text = raw.Trim();
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var lineEnd = text.IndexOf('\n', fenceStart + 3);
            var fenceEnd = lineEnd >= 0 ? text.IndexOf("```", lineEnd + 1, StringComparison.Ordinal) : -1;
            if (lineEnd >= 0 && fenceEnd > lineEnd)
                text = text[(lineEnd + 1)..fenceEnd].Trim();
        }
        var a = text.IndexOf('{');
        var b = text.LastIndexOf('}');
        return a >= 0 && b > a ? text[a..(b + 1)] : text;
    }

    private static string BuildGlossarySuffix(IReadOnlyList<TranscriptLine> lines, string templateId, Glossary? glossary)
    {
        if (glossary == null) return "";
        var mine = glossary.ForNotes(string.Join("\n", lines.Select(l => l.Src)), templateId);
        return mine.Count == 0
            ? ""
            : "\n\nThe user's glossary — always write these exactly so; a word that sounds like one of them is that word: " + string.Join("; ", mine);
    }

    private static string FormatSpeaker(TranscriptLine line, IReadOnlyDictionary<string, int> numbers, IReadOnlyDictionary<int, string> names) =>
        line.Speaker == "me" ? "你"
            : numbers.TryGetValue(line.Id, out var n) ? (names.TryGetValue(n, out var name) ? name : $"发言人{n}")
            : numbers.Count == 0 ? "对方" : "未识别";

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "会议记录" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
