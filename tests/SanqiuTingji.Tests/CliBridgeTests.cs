using SanqiuTingji;
using Xunit;

namespace SanqiuTingji.Tests;

public sealed class CliBridgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sanqiutingji-cli-test-" + Guid.NewGuid().ToString("N"));

    public CliBridgeTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ExtensionlessNpmShimResolvesToSiblingCmdToAvoidWin32_193()
    {
        var npmDir = Path.Combine(root, "npm");
        Directory.CreateDirectory(npmDir);
        var shim = Path.Combine(npmDir, "claude");
        var cmd = Path.Combine(npmDir, "claude.cmd");
        File.WriteAllText(shim, "#!/bin/sh\necho posix shim\n");
        File.WriteAllText(cmd, "@echo off\necho {\"ok\":true}\n");

        // Direct path saved by an older version self-heals to .cmd.
        var resolved = CliBridge.ResolveExecutable(shim, ["claude.exe", "claude.cmd"]);
        Assert.Equal(cmd, resolved);

        var settings = new AppSettings { CliNotesPreset = "claude", CliNotesCommand = shim, CliNotesEnabled = true };
        Assert.True(CliBridge.TryAutoConfigure(settings));
        Assert.Equal(cmd, settings.CliNotesCommand);
        Assert.Equal(cmd, CliBridge.FromSettings(settings).Command);
    }

    [Fact]
    public void StandalonePosixShellScriptWithoutSiblingIsRejected()
    {
        var shim = Path.Combine(root, "only-posix");
        File.WriteAllText(shim, "#!/bin/sh\necho nope\n");

        Assert.Null(CliBridge.NormalizeWindowsExecutable(shim));
    }

    [Fact]
    public async Task BatchScriptInPathWithSpacesRunsSilentlyThroughCmd()
    {
        var spacedDir = Path.Combine(root, "Dir With Spaces");
        Directory.CreateDirectory(spacedDir);
        var bat = Path.Combine(spacedDir, "echo-json.cmd");
        File.WriteAllText(bat, "@echo off\r\necho %~1\r\n");

        var config = new CliBridgeConfig(true, "custom", bat, "\"{\\\"ok\\\":true}\"", CliInputMode.Stdin, null, 30);
        var result = await CliBridge.TestAsync(config);
        Assert.True(result.Success, result.Message);
        Assert.Contains("ok", result.Message);
    }

    [Fact]
    public void RemovedWorkBuddyPresetIsNotListedAndMigratesCleanly()
    {
        Assert.DoesNotContain(CliBridge.Presets, p => string.Equals(p.Id, "workbuddy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("custom", CliBridge.FindPreset("workbuddy").Id);

        var settings = new AppSettings
        {
            CliNotesPreset = "workbuddy",
            CliNotesCommand = "codebuddy",
            CliNotesArgs = "-p --output-format text",
            CliNotesEnabled = true,
        };
        Assert.True(CliBridge.TryAutoConfigure(settings));
        Assert.NotEqual("workbuddy", settings.CliNotesPreset);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
