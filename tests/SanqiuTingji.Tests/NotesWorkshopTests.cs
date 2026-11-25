using DocumentFormat.OpenXml.Packaging;
using SanqiuTingji;
using Xunit;

namespace SanqiuTingji.Tests;

public sealed class NotesWorkshopTests
{
    private static Session SampleSession(int count = 4, bool withTranslation = true)
    {
        var lines = new List<TranscriptLine>();
        for (var i = 0; i < count; i++)
        {
            lines.Add(new TranscriptLine
            {
                Id = $"l{i + 1}",
                T = i * 30,
                End = i * 30 + 20,
                Speaker = i % 2 == 0 ? "them" : "me",
                Voice = i % 2 == 0 ? 1 : null,
                Src = i switch
                {
                    0 => "今天主要讨论算电协同项目一期接口联调进度。",
                    1 => "我们下周三前把负荷监测数据表发给测试团队。",
                    2 => "好的，张三负责跟进文档评审，注意把网关参数补齐。",
                    _ => $"第{i + 1}条讨论记录：围绕需求边界与里程碑节点继续细化并确认交付标准。",
                },
                Dst = withTranslation ? $"English line {i + 1}" : null,
                SrcLang = "zh",
                DstLang = "en",
            });
        }

        return new Session
        {
            Id = "sess001",
            Title = "会议 11月25日 14:00",
            StartedAt = new DateTime(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc),
            EndedAt = new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc),
            Lines = lines,
            VoiceNames = new Dictionary<string, string> { ["1"] = "张三" },
        };
    }

    [Fact]
    public void BuildPackage_SinglePromptIncludesTemplateSpeakerNamesAndGlossary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sanqiutingji-workshop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var glossary = new Glossary(Path.Combine(dir, "glossary.json"));
            glossary.Save(new GlossaryTerm { Text = "算电协同", Misheard = ["算电写同"], Note = "核心项目" });
            var session = SampleSession();
            var tpl = NoteTemplates.Find("weekly", []);

            var pkg = NotesWorkshop.BuildPackage(session, tpl, glossary);

            Assert.False(pkg.IsChunked);
            Assert.Single(pkg.Chunks);
            Assert.Contains("项目周会", pkg.Chunks[0].Prompt);
            Assert.Contains("张三", pkg.Chunks[0].Prompt);
            Assert.Contains("算电协同", pkg.Chunks[0].Prompt);
            Assert.Contains("return JSON only", pkg.Chunks[0].Prompt);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildPackage_LongSessionSplitsIntoOrderedChunks()
    {
        var session = SampleSession(count: 120, withTranslation: false);
        var pkg = NotesWorkshop.BuildPackage(session, NoteTemplates.BuiltIn[0], maxChunkChars: 7000);

        Assert.True(pkg.IsChunked);
        Assert.True(pkg.Chunks.Count >= 2);
        Assert.Equal(1, pkg.Chunks[0].StartLine);
        Assert.Equal(session.Lines.Count, pkg.Chunks[^1].EndLine);
        for (var i = 1; i < pkg.Chunks.Count; i++)
            Assert.Equal(pkg.Chunks[i - 1].EndLine + 1, pkg.Chunks[i].StartLine);
    }

    [Fact]
    public void ImportReply_ParsesFencedMarkdownJsonAndSavesSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sanqiutingji-workshop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SessionStore(dir);
            var created = store.Begin();
            store.Mutate(created.Id, s =>
            {
                s.StartedAt = new DateTime(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);
                s.Lines = SampleSession().Lines;
                s.VoiceNames = new Dictionary<string, string> { ["1"] = "张三" };
            });

            var rawReply = """
                好的，这是按要求生成的会议纪要：
                ```json
                {
                  "title": "算电协同接口联调会",
                  "summary": "明确算电协同项目一期接口联调进度与数据表交付计划。",
                  "topics": [
                    {
                      "start": 0,
                      "title": "接口联调与文档评审",
                      "summary": "对齐联调进度并明确责任人。",
                      "points": [
                        {"text": "下周三前交付负荷监测数据表", "evidence": ["L2"]}
                      ],
                      "evidence": ["L1", "L2"]
                    }
                  ],
                  "conclusions": [
                    {"text": "接口文档补齐网关参数后进入评审", "evidence": ["L3"]}
                  ],
                  "todos": [
                    {"text": "提交负荷监测数据表给测试团队", "owner": "你", "due": "12月2日（周三）", "evidence": ["L2"]}
                  ],
                  "terms": [
                    {"name": "算电协同", "note": "业务项目", "heard": []}
                  ]
                }
                ```
                """;

            var result = NotesWorkshop.ImportReply(store, created.Id, rawReply, NoteTemplates.BuiltIn[0]);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.TopicCount);
            Assert.Equal(1, result.ConclusionCount);
            Assert.Equal(1, result.TodoCount);
            Assert.Equal(1, result.TermCount);

            var reloaded = new SessionStore(dir).Snapshot(created.Id);
            Assert.NotNull(reloaded?.Notes);
            Assert.Equal("算电协同接口联调会", reloaded!.Notes!.Title);
            Assert.True(reloaded.Notes.Final);
            Assert.Equal(4, reloaded.Notes.Covered);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExportToDirectory_ChineseOnlyExcludesTranslationInMarkdownAndDocx()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sanqiutingji-workshop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = SampleSession(withTranslation: true);
            var mdPath = NotesWorkshop.ExportToDirectory(session, dir, "md", includeTranslation: false);
            var docxPath = NotesWorkshop.ExportToDirectory(session, dir, "docx", includeTranslation: false);

            var md = File.ReadAllText(mdPath);
            Assert.Contains("算电协同项目一期接口联调进度", md);
            Assert.DoesNotContain("English line 1", md);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("算电协同项目一期接口联调进度", text);
            Assert.DoesNotContain("English line 1", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GenerateWithRunnerAsync_RunsMockRunnerAndSavesNotes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sanqiutingji-workshop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SessionStore(dir);
            var created = store.Begin();
            store.Mutate(created.Id, s => s.Lines = SampleSession().Lines);

            var runner = new StubRunner("""
                {"title":"接口联调周会","summary":"对齐一期联调计划。","topics":[],"conclusions":[{"text":"按期联调","evidence":["L1"]}],"todos":[],"terms":[]}
                """);

            var result = await NotesWorkshop.GenerateWithRunnerAsync(store, created.Id, runner, NoteTemplates.BuiltIn[0]);
            Assert.True(result.Success, result.Message);
            Assert.Equal("接口联调周会", store.Snapshot(created.Id)!.Notes!.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CliBridge_SplitArgumentsRespectsQuotes()
    {
        var tokens = CliBridge.SplitArguments("--disable-slash-commands -p \"hello world\" --model 'gemini-2.5-pro'");
        Assert.Equal(["--disable-slash-commands", "-p", "hello world", "--model", "gemini-2.5-pro"], tokens);
    }

    [Fact]
    public async Task CliBridge_RunnerSupportsArgumentStdinAndPromptFileModes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sanqiu-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var argScript = Path.Combine(dir, "echo-arg.ps1");
            File.WriteAllText(argScript, "param([string]$p)\nWrite-Output $p");
            var argRunner = CliBridge.CreateRunner(new CliBridgeConfig(
                true, "custom", "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{argScript}\" {{prompt}}",
                CliInputMode.Argument, null, 30));
            var argOut = await argRunner.RunAsync("{\"ok\":\"arg\"}");
            Assert.Contains("arg", argOut);

            var stdinScript = Path.Combine(dir, "echo-stdin.ps1");
            File.WriteAllText(stdinScript, "[Console]::In.ReadToEnd()");
            var stdinRunner = CliBridge.CreateRunner(new CliBridgeConfig(
                true, "custom", "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{stdinScript}\"",
                CliInputMode.Stdin, null, 30));
            var stdinOut = await stdinRunner.RunAsync("{\"ok\":\"stdin\"}");
            Assert.Contains("stdin", stdinOut);

            var fileScript = Path.Combine(dir, "echo-file.ps1");
            File.WriteAllText(fileScript, "param([string]$f)\nGet-Content -Raw -Encoding UTF8 $f");
            var fileRunner = CliBridge.CreateRunner(new CliBridgeConfig(
                true, "custom", "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{fileScript}\" {{prompt_file}}",
                CliInputMode.PromptFile, null, 30));
            var fileOut = await fileRunner.RunAsync("{\"ok\":\"file\"}");
            Assert.Contains("file", fileOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class StubRunner(string reply) : INotesRunner
    {
        public Task<string> RunAsync(string prompt, CancellationToken ct = default) => Task.FromResult(reply);
    }
}
