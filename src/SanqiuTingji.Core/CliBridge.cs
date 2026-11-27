using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

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

/// Universal local AI CLI bridge: supports Antigravity (agy), WorkBuddy / CodeBuddy, OpenAI Codex,
/// Google Gemini CLI, Claude Code CLI, and any custom local command without requiring paid HTTP API keys.
public static class CliBridge
{
    private static readonly string[] WindowsExecExtensions = [".exe", ".cmd", ".bat", ".com"];
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".cjs", ".ps1"];

    public static readonly CliBridgePreset[] Presets =
    [
        new("agy", "Antigravity (agy)", "agy",
            "--disable-slash-commands --output-format text --effort low --print {prompt}",
            CliInputMode.Argument, "--model",
            "使用本机已登录的 Antigravity 订阅，已自动匹配路径与参数。",
            ["agy.exe", "agy.cmd", "agy.bat"],
            "请先安装并登录 Antigravity 客户端后重新扫描"),
        new("workbuddy", "WorkBuddy / CodeBuddy", "codebuddy",
            "-p --output-format text",
            CliInputMode.Stdin, "--model",
            "复用本机已登录的腾讯 WorkBuddy 桌面端或 CodeBuddy CLI 额度，免配 API Key。",
            ["codebuddy.exe", "codebuddy.cmd", "codebuddy.bat"],
            "安装并登录 WorkBuddy 客户端，或执行 npm i -g @tencent-ai/codebuddy-code"),
        new("codex", "ChatGPT / Codex CLI", "codex",
            "exec --skip-git-repo-check -",
            CliInputMode.Stdin, "-m",
            "复用本机 ChatGPT Plus / Pro 订阅（codex），无需单独买 API。",
            ["codex.exe", "codex.cmd", "codex.bat"],
            "npm i -g @openai/codex && codex"),
        new("gemini", "Google Gemini CLI", "gemini",
            "-p \"请严格按标准输入中的任务要求输出严格 JSON，不要调用任何工具。\"",
            CliInputMode.Stdin, "-m",
            "复用 Google 账号 Gemini 免费/订阅额度（gemini）。",
            ["gemini.exe", "gemini.cmd", "gemini.bat"],
            "npm i -g @google/gemini-cli && gemini"),
        new("claude", "Claude Code CLI", "claude",
            "-p --output-format text",
            CliInputMode.Stdin, "--model",
            "复用本机 Claude 订阅（claude），无需单独买 API。",
            ["claude.exe", "claude.cmd", "claude.bat"],
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
        Presets.Select(p => new CliDetectedItem(p, ResolvePresetExecutable(p))).ToList();

    public static string? ResolvePresetExecutable(CliBridgePreset preset)
    {
        if (preset.Id == "custom") return null;
        if (string.Equals(preset.Id, "workbuddy", StringComparison.OrdinalIgnoreCase))
            return ResolveWorkBuddyEntry() ?? ResolveExecutable(preset.Executable, preset.SearchNames);
        return ResolveExecutable(preset.Executable, preset.SearchNames);
    }

    /// Auto-fills CLI settings on first run, and self-heals broken extensionless npm shims (Win32=193)
    /// saved by older versions (e.g. `...\npm\claude` -> `...\npm\claude.cmd`).
    public static bool TryAutoConfigure(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CliNotesCommand))
        {
            var preset = FindPreset(settings.CliNotesPreset);
            var healed = ResolveCommandForPreset(settings.CliNotesCommand, preset);
            if (!string.IsNullOrWhiteSpace(healed)
                && !string.Equals(healed, settings.CliNotesCommand.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase))
            {
                settings.CliNotesCommand = healed;
                return true;
            }
            return false;
        }
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
        var resolved = ResolveCommandForPreset(cmd, preset) ?? cmd;
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

    private static string? ResolveCommandForPreset(string command, CliBridgePreset preset)
    {
        var resolved = ResolveExecutable(command, preset.SearchNames);
        if (resolved != null) return resolved;
        if (string.Equals(preset.Id, "workbuddy", StringComparison.OrdinalIgnoreCase))
            return ResolveWorkBuddyEntry();
        return null;
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

    /// Resolves a CLI executable on Windows, always preferring `.exe` / `.cmd` / `.bat` over
    /// extensionless POSIX shell shims (which npm places alongside `.cmd` and cause Win32=193).
    public static string? ResolveExecutable(string command, string[]? extraNames = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim().Trim('"');

        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            var normalized = NormalizeWindowsExecutable(trimmed);
            if (normalized != null) return normalized;
        }

        var candidates = BuildCandidateNames(trimmed, extraNames);
        var searchDirs = BuildSearchDirs();

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in candidates)
            {
                var full = Path.Combine(dir, name);
                var normalized = NormalizeWindowsExecutable(full);
                if (normalized != null) return normalized;
            }
        }
        return null;
    }

    /// Ensures a file path is actually launchable on Windows. If given an extensionless npm shim
    /// like `...\npm\claude`, redirects to the sibling `.exe` / `.cmd` / `.bat` when present,
    /// and rejects non-PE text scripts (e.g. `#!/bin/sh`) that would fail with Win32=193.
    internal static string? NormalizeWindowsExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        var ext = Path.GetExtension(full);
        if (ext.Length > 0)
        {
            if (File.Exists(full) && (IsWindowsExecExt(ext) || IsScriptExt(ext)))
                return full;
            return null;
        }

        // Extensionless path: check Windows executable siblings first (.exe -> .cmd -> .bat -> .com).
        foreach (var winExt in WindowsExecExtensions)
        {
            var sibling = full + winExt;
            if (File.Exists(sibling)) return sibling;
        }

        // Only accept an extensionless file if it is a real PE binary ("MZ" header), never a #!/bin/sh text shim.
        if (File.Exists(full) && IsPeBinary(full))
            return full;

        return null;
    }

    private static bool IsWindowsExecExt(string ext) =>
        WindowsExecExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

    private static bool IsScriptExt(string ext) =>
        ScriptExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

    private static bool IsPeBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b0 = fs.ReadByte();
            var b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch { return false; }
    }

    private static List<string> BuildCandidateNames(string trimmed, string[]? extraNames)
    {
        var list = new List<string>();
        void Add(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                list.Add(name);
        }

        var baseName = Path.IsPathRooted(trimmed) ? Path.GetFileName(trimmed) : trimmed;
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var ext = Path.GetExtension(baseName);
            if (ext.Length > 0)
            {
                Add(baseName);
            }
            else
            {
                foreach (var winExt in WindowsExecExtensions) Add(baseName + winExt);
                Add(baseName);
            }
        }

        if (extraNames != null)
        {
            // Keep Windows executable extensions ahead of any extensionless fallback names.
            foreach (var n in extraNames.Where(x => IsWindowsExecExt(Path.GetExtension(x)) || IsScriptExt(Path.GetExtension(x))))
                Add(n);
            foreach (var n in extraNames.Where(x => string.IsNullOrEmpty(Path.GetExtension(x))))
                Add(n);
        }
        return list;
    }

    private static List<string> BuildSearchDirs()
    {
        var dirs = new List<string>();
        void AddDir(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            var clean = d.Trim().Trim('"');
            if (clean.Length > 0 && !dirs.Contains(clean, StringComparer.OrdinalIgnoreCase))
                dirs.Add(clean);
        }

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddDir(part);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        AddDir(Path.Combine(local, "agy", "bin"));
        AddDir(Path.Combine(local, "codebuddy", "bin"));
        AddDir(Path.Combine(roaming, "npm"));
        AddDir(Path.Combine(local, "pnpm"));
        AddDir(Path.Combine(profile, ".local", "bin"));
        AddDir(Path.Combine(profile, ".claude", "local"));
        AddDir(Path.Combine(profile, ".volta", "bin"));
        AddDir(Path.Combine(profile, ".bun", "bin"));
        AddDir(Path.Combine(profile, "scoop", "shims"));
        AddDir(Path.Combine(profile, ".cargo", "bin"));
        AddDir(Path.Combine(programFiles, "nodejs"));
        AddDir(Environment.GetEnvironmentVariable("NVM_SYMLINK"));
        AddDir(Environment.GetEnvironmentVariable("PNPM_HOME"));
        return dirs;
    }

    /// Locates Tencent WorkBuddy's embedded CodeBuddy CLI (like DSH's workbuddy bridge) or
    /// standalone CodeBuddy installations on Windows without requiring manual path entry.
    public static string? ResolveWorkBuddyEntry()
    {
        var envBin = Environment.GetEnvironmentVariable("WORKBUDDY_BIN");
        if (!string.IsNullOrWhiteSpace(envBin))
        {
            var trimmedEnv = envBin.Trim().Trim('"');
            if (NormalizeWindowsExecutable(trimmedEnv) is { } direct) return direct;
            if (Directory.Exists(trimmedEnv) && FindCodeBuddyInsideRoot(trimmedEnv) is { } fromEnvDir) return fromEnvDir;
        }

        foreach (var root in GetWorkBuddyInstallDirs())
        {
            if (FindCodeBuddyInsideRoot(root) is { } found)
                return found;
        }
        return null;
    }

    private static IEnumerable<string> GetWorkBuddyInstallDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var clean = p.Trim().Trim('"');
            if (File.Exists(clean)) clean = Path.GetDirectoryName(clean) ?? "";
            if (clean.Length > 0 && Directory.Exists(clean) && seen.Add(clean)) list.Add(clean);
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Add(Path.Combine(pf, "WorkBuddy"));
        Add(Path.Combine(local, "Programs", "WorkBuddy"));
        Add(Path.Combine(local, "WorkBuddy"));
        Add(Path.Combine(pf86, "WorkBuddy"));
        Add(Path.Combine(local, "codebuddy"));
        Add(Path.Combine(profile, ".workbuddy"));

        // Also inspect Windows Uninstall registry entries so custom install drives (e.g. D:\WorkBuddy) are detected.
        foreach (var regDir in ReadRegistryInstallDirs("WorkBuddy", "CodeBuddy"))
            Add(regDir);

        return list;
    }

    private static IEnumerable<string> ReadRegistryInstallDirs(params string[] keywords)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;

        string[] subKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        RegistryKey[] roots = [Registry.CurrentUser, Registry.LocalMachine];

        foreach (var root in roots)
        {
            foreach (var subKeyPath in subKeys)
            {
                try
                {
                    using var key = root.OpenSubKey(subKeyPath);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(name);
                            var display = sub?.GetValue("DisplayName") as string ?? "";
                            if (!keywords.Any(k => display.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            if (sub?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                                result.Add(loc);
                            else if (sub?.GetValue("DisplayIcon") is string icon && !string.IsNullOrWhiteSpace(icon))
                            {
                                var comma = icon.IndexOf(',');
                                var cleanIcon = (comma >= 0 ? icon[..comma] : icon).Trim().Trim('"');
                                if (File.Exists(cleanIcon))
                                    result.Add(Path.GetDirectoryName(cleanIcon)!);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return result;
    }

    internal static string? FindCodeBuddyInsideRoot(string root)
    {
        if (!Directory.Exists(root)) return null;

        // Fast direct checks for known WorkBuddy / CodeBuddy layouts.
        string[] knownRelative =
        [
            @"bin\codebuddy.exe",
            @"bin\codebuddy.cmd",
            @"codebuddy.exe",
            @"codebuddy.cmd",
            @"resources\app.asar.unpacked\cli\dist\codebuddy.js",
            @"resources\app.asar.unpacked\cli\bin\codebuddy.js",
            @"resources\app.asar.unpacked\cli\codebuddy.js",
            @"resources\app.asar.unpacked\node_modules\@tencent-ai\codebuddy-code\cli.js",
            @"resources\app.asar.unpacked\node_modules\@tencent-ai\codebuddy-code\dist\codebuddy.js",
            @"resources\cli\dist\codebuddy.js",
            @"resources\cli\codebuddy.js",
        ];
        foreach (var rel in knownRelative)
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return p;
        }

        // Bounded scan under `resources` so minor path shifts between WorkBuddy versions still work.
        var resources = Path.Combine(root, "resources");
        if (Directory.Exists(resources))
            return FindFileBounded(resources, ["codebuddy.exe", "codebuddy.cmd", "codebuddy.js"], maxDepth: 5);

        return null;
    }

    private static string? FindFileBounded(string dir, string[] fileNames, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((dir, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            foreach (var name in fileNames)
            {
                var candidate = Path.Combine(current, name);
                if (File.Exists(candidate)) return candidate;
            }
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    var folderName = Path.GetFileName(sub);
                    if (folderName.Equals("locales", StringComparison.OrdinalIgnoreCase)
                        || folderName.Equals("icons", StringComparison.OrdinalIgnoreCase)
                        || folderName.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
        }
        return null;
    }

    /// Finds an enclosing Electron host executable (WorkBuddy.exe / CodeBuddy.exe) so embedded
    /// `codebuddy.js` runs via `ELECTRON_RUN_AS_NODE=1` even on machines without standalone Node.js.
    internal static string? FindElectronHostForScript(string scriptPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(scriptPath));
            for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (var host in new[] { "WorkBuddy.exe", "CodeBuddy.exe" })
                {
                    var exe = Path.Combine(dir, host);
                    if (File.Exists(exe)) return exe;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
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

    internal static (string LaunchExe, List<string> LaunchArgs, Dictionary<string, string> ExtraEnv) BuildLaunchSpec(
        string resolvedExe, IReadOnlyList<string> tokens)
    {
        var extraEnv = new Dictionary<string, string>
        {
            ["NO_COLOR"] = "1",
            ["TERM"] = "dumb",
        };
        var launchArgs = new List<string>();

        if (resolvedExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || resolvedExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            launchArgs.Add("/d");
            launchArgs.Add("/s");
            launchArgs.Add("/c");
            launchArgs.Add(resolvedExe);
            launchArgs.AddRange(tokens);
            return ("cmd.exe", launchArgs, extraEnv);
        }

        if (resolvedExe.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || resolvedExe.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || resolvedExe.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            var electronHost = FindElectronHostForScript(resolvedExe);
            string runtimeExe;
            if (electronHost != null)
            {
                runtimeExe = electronHost;
                extraEnv["ELECTRON_RUN_AS_NODE"] = "1";
            }
            else
            {
                runtimeExe = ResolveExecutable("node", ["node.exe"])
                    ?? throw new InvalidOperationException("未找到 Node.js 运行环境，无法执行该 JS 脚本。请安装 Node.js 或选择 WorkBuddy 内置路径。");
            }
            launchArgs.Add(resolvedExe);
            launchArgs.AddRange(tokens);
            return (runtimeExe, launchArgs, extraEnv);
        }

        if (resolvedExe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            launchArgs.Add("-NoProfile");
            launchArgs.Add("-ExecutionPolicy");
            launchArgs.Add("Bypass");
            launchArgs.Add("-File");
            launchArgs.Add(resolvedExe);
            launchArgs.AddRange(tokens);
            return ("powershell.exe", launchArgs, extraEnv);
        }

        launchArgs.AddRange(tokens);
        return (resolvedExe, launchArgs, extraEnv);
    }

    private sealed class BridgeNotesRunner(CliBridgeConfig config, string? modelFlag) : INotesRunner
    {
        public async Task<string> RunAsync(string prompt, CancellationToken ct = default)
        {
            var preset = FindPreset(config.PresetId);
            var exe = ResolveCommandForPreset(config.Command, preset) ?? config.Command;
            if (string.IsNullOrWhiteSpace(exe))
                throw new InvalidOperationException("未配置本地 CLI 可执行文件。");

            // Prevent agentic CLIs from spawning shell sub-processes or tool calls.
            var effectivePrompt = config.PresetId is "agy" or "workbuddy" or "codex" or "gemini" or "claude"
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

                var (launchExe, launchArgs, extraEnv) = BuildLaunchSpec(exe, tokens);
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
