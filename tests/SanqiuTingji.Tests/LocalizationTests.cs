using SanqiuTingji.Localization;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SanqiuTingji.Tests;

[CollectionDefinition("Interface language", DisableParallelization = true)]
public class InterfaceLanguageCollection { }

[Collection("Interface language")]
public class LocalizationTests
{
    [Theory]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-HK", "zh-CN")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("ko-KR", "en")]
    [InlineData("de-DE", "en")]
    public void ResolvesSystemLanguage(string system, string expected) =>
        Assert.Equal(expected, L.Resolve("auto", system));

    [Fact]
    public void FormatsTranslatedMessagesWithoutChangingArguments()
    {
        Assert.Equal("Speaker 12", L.For("en", "发言人 {0}", 12));
        Assert.Equal("发言人 12", L.For("zh-CN", "发言人 {0}", 12));
    }

    [Fact]
    public void UnknownTextIsPreserved() =>
        Assert.Equal("Priya's notes", L.For("en", "Priya's notes"));

    [Fact]
    public void SwitchingLanguagePreservesContentInsideCapturedMessages()
    {
        var before = L.Language;
        const string content = "原因分析 · 用户写的会议正文";
        try
        {
            L.SetLanguage("zh-CN");
            var read = L.Capture(() => L.T("原因分析 · ") + content);
            var formatted = L.Capture(() => L.F("给「发言人 {0}」起个名字", content));
            foreach (var language in new[] { "en", "zh-CN" })
            {
                L.SetLanguage(language);
                Assert.Equal(L.For(language, "原因分析 · ") + content, read());
                Assert.Equal(L.For(language, "给「发言人 {0}」起个名字", content), formatted());
            }
        }
        finally { L.SetLanguage(before); }
    }

    [Fact]
    public void ExistingSettingsFollowSystemAndKeepOutputLanguage()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal("auto", settings.UiLanguage);
        Assert.Equal("auto", settings.Target);
        Assert.Equal("auto", settings.NotesLanguage);
    }

    [Fact]
    public void EveryLocaleHasAllMessagesAndTheSameFormatArguments()
    {
        var resources = new ResourceManager("SanqiuTingji.Localization.Strings", typeof(L).Assembly);
        var source = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var keys = source.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        foreach (var language in new[] { "en" })
        {
            var translated = resources.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!;
            Assert.NotNull(translated);
            Assert.True(keys.SetEquals(translated.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key)), language);
            foreach (var key in keys)
            {
                var text = translated.GetString(key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key}");
                if (!Regex.IsMatch(key, @"\{\d")) continue;
                CompositeFormat.Parse(text!);
                string[] Arguments(string s) => Regex.Matches(s, @"(?<!\{)\{(\d+)(?:[^{}]*)\}")
                    .Select(m => m.Groups[1].Value).Order().ToArray();
                Assert.Equal(Arguments(key), Arguments(text!));
            }
        }
    }
}
