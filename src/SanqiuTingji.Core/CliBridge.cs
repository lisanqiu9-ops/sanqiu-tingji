using System.Diagnostics;
using System.Text;

namespace SanqiuTingji;

public enum CliInputMode
{
    /// Pass prompt directly via ProcessStartInfo.ArgumentList (replaces {prompt} or appends at end).
    Argument,
    /// Write prompt to UTF-8 standard input.
    Stdin,
    /// Write prompt to a temporary UTF-8 file and replace {prompt_file} in arguments.
    PromptFile,
}

public sealed record CliBridgePreset(
    string Id,
    string Name,
    string Executable,
    string Args,
    CliInputMode InputMode,
    string? ModelFlag,
    string Hint,
    string[] SearchNames,
    string InstallCommand = "");

public sealed record CliBridgeConfig(
    bool Enabled,
    string PresetId,
    string Command,
    string Args,
    CliInputMode InputMode,
    string? Model,
    int TimeoutSeconds = 180)
{
    public bool IsReady => Enabled && !string.IsNullOrWhiteSpace(Command);
    public int RecommendedMaxChunkChars => InputMode == CliInputMode.Argument ? 18000 : NotesBatch.MaxPromptChars;
}

public sealed record CliDetectedItem(CliBridgePreset Preset, string? ResolvedPath)
{
    public bool Installed => !string.IsNullOrEmpty(ResolvedPath);
}

public sealed record CliTestResult(bool Success, string Message, long ElapsedMs);

/// Universal local AI CLI bridge: supports Antigravity (agy), OpenAI Codex, Google Gemini CLI,
/// Claude Code CLI, and any custom local command without requiring paid HTTP API keys.
public static class CliBridge
{
    public static readonly CliBridgePreset[] Presets =
    [
        new("agy", "Antigravity (agy)", "agy",
            "--disable-slash-commands --output-format text --effort low --print {prompt}",
            CliInputMode.Argument, "--model",
            "使用本机已登录的 Antigravity 订阅，已自动匹配路径与参数。",
            ["agy.exe", "agy.cmd", "agy"],
            "请先安装并登录 Antigravity 客户端后重新扫描"),
        new("codex", "ChatGPT / Codex CLI", "codex",
            "exec --skip-git-repo-check -",
            CliInputMode.Stdin, "-m",
            "复用本机 ChatGPT Plus / Pro 订阅（codex），无需单独买 API。",
            ["codex.exe", "codex.cmd", "codex"],
            "npm i -g @openai/codex && codex"),
        new("gemini", "Google Gemini CLI", "gemini",
            "-p \"请严格按标准输入中的任务要求输出严格 JSON，不要调用任何工具。\"",
            CliInputMode.Stdin, "-m",
            "复用 Google 账号 Gemini 免费/订阅额度（gemini）。",
            ["gemini.exe", "gemini.cmd", "gemini"],
            "npm i -g @google/gemini-cli && gemini"),
        new("claude", "Claude Code CLI", "claude",
            "-p --output-format text",
            CliInputMode.Stdin, "--model",
            "复用本机 Claude 订阅（claude），无需单独买 API。",
            ["claude.exe", "claude.cmd", "claude"],
            "npm i -g @anthropic-ai/claude-code && claude"),
        new("custom", "自定义本地 CLI", "",
            "",
            CliInputMode.Stdin, null,
            "任意本地命令或脚本：支持标准输入、{prompt} 命令行参数或 {prompt_file} 临时文件占位符。",
            [],
            ""),
    ];

    public static CliBridgePreset FindPreset(string? id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[^1];

    public static IReadOnlyList<CliDetectedItem> DetectInstalled() =>
        Presets.Select(p => new CliDetectedItem(p, p.SearchNames.Length == 0 ? null : ResolveExecutable(p.Executable, p.SearchNames))).ToList();

    /// Auto-fills CLI settings on first run when an installed local CLI (e.g. agy / codex / gemini) is detected.
    public static bool TryAutoConfigure(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CliNotesCommand)) return false;
        var hit = DetectInstalled().FirstOrDefault(d => d.Installed);
        if (hit == null) return false;
        settings.CliNotesPreset = hit.Preset.Id;
        settings.CliNotesCommand = hit.ResolvedPath!;
        settings.CliNotesArgs = hit.Preset.Args;
        settings.CliNotesInputMode = hit.Preset.InputMode;
        settings.CliNotesEnabled = true;
        return true;
    }

    public static CliBridgeConfig FromSettings(AppSettings settings)
    {
        var preset = FindPreset(settings.CliNotesPreset);
        var cmd = string.IsNullOrWhiteSpace(settings.CliNotesCommand) ? preset.Executable : settings.CliNotesCommand.Trim();
        var resolved = ResolveExecutable(cmd, preset.SearchNames) ?? cmd;
        var args = settings.CliNotesArgs ?? preset.Args;
        return new CliBridgeConfig(
            settings.CliNotesEnabled,
            preset.Id,
            resolved,
            args,
            settings.CliNotesInputMode,
            string.IsNullOrWhiteSpace(settings.CliNotesModel) ? null : settings.CliNotesModel.Trim(),
            Math.Clamp(settings.CliNotesTimeoutSeconds, 30, 600));
    }

    public static INotesRunner CreateRunner(CliBridgeConfig config)
    {
        var preset = FindPreset(config.PresetId);
        return new BridgeNotesRunner(config, preset.ModelFlag);
    }

    public static async Task<CliTestResult> TestAsync(CliBridgeConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            return new CliTestResult(false, "请先选择预设或填写本地 CLI 可执行文件路径。", 0);

        var sw = Stopwatch.StartNew();
        try
        {
            var runner = CreateRunner(config with { TimeoutSeconds = Math.Min(config.TimeoutSeconds, 60) });
            var reply = await runner.RunAsync("不要调用任何工具，直接只回复一行严格JSON：{\"ok\":true}", ct);
            sw.Stop();
            var preview = reply.Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 80) preview = preview[..80] + "…";
            return new CliTestResult(true, $"✓ 连通成功（{sw.ElapsedMilliseconds / 1000.0:F1} 秒）：{preview}", sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            sw.Stop();
            return new CliTestResult(false, $"连通失败：{e.Message}", sw.ElapsedMilliseconds);
        }
    }

    public static string? ResolveExecutable(string command, string[]? extraNames = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim().Trim('"');
        if (File.Exists(trimmed)) return Path.GetFullPath(trimmed);

        var candidates = new List<string>();
        if (!Path.IsPathRooted(trimmed))
        {
            candidates.Add(trimmed);
            if (!Path.HasExtension(trimmed))
            {
                candidates.Add(trimmed + ".exe");
                candidates.Add(trimmed + ".cmd");
                candidates.Add(trimmed + ".bat");
            }
        }
        if (extraNames != null)
            foreach (var n in extraNames)
                if (!candidates.Contains(n, StringComparer.OrdinalIgnoreCase)) candidates.Add(n);

        var searchDirs = new List<string>();
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        searchDirs.AddRange(envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] extraDirs =
        [
            Path.Combine(local, "agy", "bin"),
            Path.Combine(roaming, "npm"),
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".cargo", "bin"),
            Path.Combine(programFiles, "nodejs"),
        ];
        foreach (var d in extraDirs)
            if (!string.IsNullOrWhiteSpace(d) && !searchDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                searchDirs.Add(d);

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in candidates)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// Splits a command-line argument string into tokens, respecting double and single quotes.
    public static List<string> SplitArguments(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == quote) sb.Append(raw[++i]);
                else sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private sealed class BridgeNotesRunner(CliBridgeConfig config, string? modelFlag) : INotesRunner
    {
        public async Task<string> RunAsync(string prompt, CancellationToken ct = default)
        {
            var exe = ResolveExecutable(config.Command) ?? config.Command;
            if (string.IsNullOrWhiteSpace(exe))
                throw new InvalidOperationException("未配置本地 CLI 可执行文件。");

            // Prevent agentic CLIs (like agy / codex / claude) from spawning shell sub-processes that could flash a console window.
            var effectivePrompt = config.PresetId is "agy" or "codex" or "gemini" or "claude"
                ? "IMPORTANT: Do NOT invoke any shell commands, tools, or file readers. Output the requested JSON text directly.\n\n" + prompt
                : prompt;

            string? tempFile = null;
            try
            {
                var tokens = SplitArguments(config.Args);
                if (!string.IsNullOrWhiteSpace(config.Model) && !string.IsNullOrWhiteSpace(modelFlag)
                    && !tokens.Any(t => string.Equals(t, modelFlag, StringComparison.OrdinalIgnoreCase)))
                {
                    tokens.Insert(0, config.Model);
                    tokens.Insert(0, modelFlag);
                }

                var hasPromptPlaceholder = false;
                var hasFilePlaceholder = false;
                if (config.InputMode == CliInputMode.PromptFile)
                {
                    tempFile = Path.Combine(Path.GetTempPath(), $"sanqiutingji-prompt-{Guid.NewGuid():N}.txt");
                    await File.WriteAllTextAsync(tempFile, effectivePrompt, new UTF8Encoding(false), ct);
                }

                for (var i = 0; i < tokens.Count; i++)
                {
                    if (tokens[i].Contains("{prompt}", StringComparison.Ordinal))
                    {
                        tokens[i] = tokens[i].Replace("{prompt}", effectivePrompt, StringComparison.Ordinal);
                        hasPromptPlaceholder = true;
                    }
                    if (tempFile != null && tokens[i].Contains("{prompt_file}", StringComparison.Ordinal))
                    {
                        tokens[i] = tokens[i].Replace("{prompt_file}", tempFile, StringComparison.Ordinal);
                        hasFilePlaceholder = true;
                    }
                }
                if (config.InputMode == CliInputMode.Argument && !hasPromptPlaceholder)
                    tokens.Add(effectivePrompt);
                if (config.InputMode == CliInputMode.PromptFile && tempFile != null && !hasFilePlaceholder)
                    tokens.Add(tempFile);

                var isBatch = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                var launchExe = isBatch ? "cmd.exe" : exe;
                var launchArgs = new List<string>();
                if (isBatch)
                {
                    launchArgs.Add("/d");
                    launchArgs.Add("/c");
                    launchArgs.Add(exe);
                }
                launchArgs.AddRange(tokens);

                var extraEnv = new Dictionary<string, string>
                {
                    ["NO_COLOR"] = "1",
                    ["TERM"] = "dumb",
                };
                var stdinPayload = config.InputMode == CliInputMode.Stdin ? effectivePrompt : null;
                var run = await SilentProcess.RunAsync(
                    launchExe,
                    launchArgs,
                    stdinPayload,
                    extraEnv,
                    TimeSpan.FromSeconds(config.TimeoutSeconds),
                    ct);

                if (run.ExitCode != 0)
                    throw new InvalidOperationException($"CLI 退出码 {run.ExitCode}：{(run.Stderr.Length > 0 ? run.Stderr : run.Stdout)}");
                if (run.Stdout.Length == 0)
                    throw new InvalidOperationException("本地 CLI 返回内容为空。");
                return run.Stdout;
            }
            finally
            {
                if (tempFile != null)
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }
}
