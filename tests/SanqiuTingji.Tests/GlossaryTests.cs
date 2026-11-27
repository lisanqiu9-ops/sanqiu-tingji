using SanqiuTingji;
using Xunit;

namespace SanqiuTingji.Tests;

public class GlossaryTests
{
    private static Glossary Make(params GlossaryTerm[] terms)
    {
        var g = new Glossary(Path.Combine(Path.GetTempPath(), "sanqiutingji-glossary-" + Guid.NewGuid().ToString("N") + ".json"));
        foreach (var t in terms) g.Save(t);
        return g;
    }

    [Fact]
    public void AMisheardSpellingTheUserListedIsAlwaysReplaced()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静", "伊利静"] });
        Assert.Equal("我想我和伊丽静都没想到", g.Fix("我想我和毕丽静都没想到"));
        Assert.Equal("伊丽静说", g.Fix("伊利静说"));
    }

    [Fact]
    public void ASoundAlikeOfALongerTermIsCorrected()
    {
        // 惜败 heard as 洗败 / 犯船 for 帆船: three characters or more may differ by the usual blurs.
        var g = Make(new GlossaryTerm { Text = "陈佩娜" }, new GlossaryTerm { Text = "帆船赛" });
        Assert.Equal("叫陈佩娜那个女孩", g.Fix("叫陈佩纳那个女孩"));
        Assert.Equal("参加帆船赛", g.Fix("参加犯船赛"));
    }

    [Fact]
    public void ATwoCharacterTermNeedsTheSameSound()
    {
        var g = Make(new GlossaryTerm { Text = "网关" });
        Assert.Equal("网关配置", g.Fix("网管配置"));       // wang guan = wang guan
        Assert.Equal("网站很慢", g.Fix("网站很慢"));       // not alike
        Assert.Equal("往来的人", g.Fix("往来的人"));       // wang lai: no
    }

    [Fact]
    public void ExactOnlyTermsAreNeverGuessedFromTheirSound()
    {
        var g = Make(new GlossaryTerm { Text = "网关", ExactOnly = true, Misheard = ["往关"] });
        Assert.Equal("网管配置", g.Fix("网管配置"));
        Assert.Equal("网关配置", g.Fix("往关配置"));
    }

    [Fact]
    public void LatinTermsMatchWholeWordsInAnyCase()
    {
        var g = Make(new GlossaryTerm { Text = "Kubernetes", Misheard = ["酷伯奈提斯"] },
                     new GlossaryTerm { Text = "API" });
        Assert.Equal("我们用 Kubernetes 部署", g.Fix("我们用 kubernetes 部署"));
        Assert.Equal("部署到Kubernetes上", g.Fix("部署到酷伯奈提斯上"));
        Assert.Equal("the API is done", g.Fix("the api is done"));
        Assert.Equal("a rapid fix", g.Fix("a rapid fix"));
    }

    [Fact]
    public void DisabledTermsDoNothing()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"], Enabled = false });
        Assert.Equal("毕丽静", g.Fix("毕丽静"));
    }

    [Fact]
    public void FixesSayWhatChanged()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        var (text, fixes) = g.FixWithDetails("毕丽静你好");
        Assert.Equal("伊丽静你好", text);
        var f = Assert.Single(fixes);
        Assert.Equal(("毕丽静", "伊丽静", 0), (f.From, f.To, f.At));
    }

    [Fact]
    public void EverydayTextIsLeftAlone()
    {
        // A small glossary over ordinary meeting talk: nothing may change.
        var g = Make(new GlossaryTerm { Text = "网关" }, new GlossaryTerm { Text = "伊丽静" }, new GlossaryTerm { Text = "压测" },
                     new GlossaryTerm { Text = "回归测试" }, new GlossaryTerm { Text = "陈佩娜" });
        foreach (var s in new[] { "大家早，先说登录模块，昨天回归测试全部通过了。", "支付接口联调完成了大约八成，退款流程还没接上。",
                                  "我周五前改好网关配置，周末跑一轮压测。", "好，上线日期维持下周一，压测报告我来整理。",
                                  "这在一年前对我来说都是不可想象的。", "人生很多时候都不重要，过去了就过去了。" })
            Assert.Equal(s, g.Fix(s));
    }

    [Fact]
    public void TranslationsFollowTheGlossary()
    {
        var g = Make(new GlossaryTerm { Text = "网关", Translations = GlossaryTerm.ParseTranslations("gateway；ja=ゲートウェイ") },
                     new GlossaryTerm { Text = "伊丽静" });
        Assert.Equal("改好 gateway 配置", g.Pretranslate("改好 网关 配置", "en"));
        Assert.Equal("改好ゲートウェイ配置", g.Pretranslate("改好网关配置", "ja"));
        Assert.Equal(["伊丽静 (a name or term: never leave Chinese characters in the translation — spell a name in pinyin, translate a term; the same way every time)", "网关 → gateway"], g.Hints("伊丽静说网关要改", "en-US"));
        Assert.Equal(["伊丽静 (a name or term: write it the same way every time, in the target language's script)"], g.Hints("伊丽静", "ja"));
        // No Korean translation: the English one gives the meaning.
        Assert.Equal(["网关 (means \"gateway\": translate it with that meaning)"], g.Hints("网关", "ko"));
        Assert.Equal("gateway; ja=ゲートウェイ", GlossaryTerm.FormatTranslations(g.Terms[0].Translations));
    }

    [Fact]
    public void ImportAddsNewTermsAndMergesKnownOnes()
    {
        var g = Make(new GlossaryTerm { Text = "网关", Misheard = ["网管"] });
        var (added, updated) = g.Import("# 注释\n网关\t往关|网管\tgateway\t支付请求的入口\n伊丽静=毕丽静|伊利静\nKubernetes\n\n");
        Assert.Equal((2, 1), (added, updated));
        var gateway = g.Terms.Single(t => t.Text == "网关");
        Assert.Equal(["网管", "往关"], gateway.Misheard);
        Assert.Equal("gateway", gateway.Translations["en"]);
        Assert.Equal("支付请求的入口", gateway.Note);
        Assert.Equal(["毕丽静", "伊利静"], g.Terms.Single(t => t.Text == "伊丽静").Misheard);

        var again = Make();
        again.Import(g.Export());
        Assert.Equal(g.Terms.Select(t => (t.Text, string.Join("|", t.Misheard), t.Note)), again.Terms.Select(t => (t.Text, string.Join("|", t.Misheard), t.Note)));
    }

    [Fact]
    public void TheGlossaryIsKeptOnDisk()
    {
        var file = Path.Combine(Path.GetTempPath(), "sanqiutingji-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        new Glossary(file).Save(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        Assert.Equal("伊丽静", new Glossary(file).Fix("毕丽静"));
        File.Delete(file);
    }

    // ── phase two: learning from the transcript and the notes, marking what was corrected ──

    [Fact]
    public void LearningAddsATermOrItsMisheardSpelling()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"], Enabled = false });
        g.Learn("伊丽静", ["P丽静", "毕丽静"]);
        var t = g.Terms.Single();
        Assert.Equal(["毕丽静", "P丽静"], t.Misheard);
        Assert.True(t.Enabled);
        Assert.Equal("我和伊丽静", g.Fix("我和P丽静"));

        g.Learn("陈佩娜", ["陈佩纳"], "帆船世界冠军");
        Assert.Equal("帆船世界冠军", g.Terms.Single(x => x.Text == "陈佩娜").Note);
        Assert.True(g.Knows("陈佩娜", ["陈佩纳"]));
        Assert.False(g.Knows("陈佩娜", ["陈配娜"]));
        Assert.False(g.Knows("江充", []));
    }

    [Fact]
    public void ASpellingLearnedAsRightIsNoLongerMisheard()
    {
        // Added 毕丽静 as misheard by mistake, then learned it is a real name: it stops being replaced.
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        g.Learn("毕丽静", []);
        Assert.Equal("毕丽静", g.Fix("毕丽静"));
    }

    [Fact]
    public void ALineKeepsTheCorrectionsStillInIt()
    {
        var g = Make(new GlossaryTerm { Text = "惜败", Misheard = [] }, new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        var (text, fixes) = g.FixWithDetails("毕丽静说帆船一米洗败，毕丽静笑了");
        var kept = LineFix.Kept(fixes, text)!;
        Assert.Equal([("毕丽静", "伊丽静"), ("洗败", "惜败")], kept.Select(f => (f.From, f.To)));
        // Cleanup dropped the second sentence: its corrections go with it.
        Assert.Equal([("毕丽静", "伊丽静")], LineFix.Kept(fixes, "伊丽静说帆船一米")!.Select(f => (f.From, f.To)));
        Assert.Null(LineFix.Kept(fixes, "没有了"));
        var merged = LineFix.Merge(kept, [new LineFix { From = "洗败", To = "惜败" }, new LineFix { From = "犯船", To = "帆船" }])!;
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void CountingTheOccurrencesToCorrect()
    {
        Assert.Equal(2, Glossary.CountOf("毕丽静说，毕丽静笑", "毕丽静"));
        Assert.Equal(0, Glossary.CountOf("伊丽静", "毕丽静"));
        Assert.Equal(2, Glossary.CountOf("aaaa", "aa"));
    }

    // ── phase three: groups limited to templates, and the AI check ──

    [Fact]
    public void AGroupLimitedToTemplatesCountsOnlyInThoseMeetings()
    {
        var g = Make(new GlossaryTerm { Text = "网关", Misheard = ["网管"], Group = "支付项目" },
                     new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        // A group with no templates chosen works everywhere.
        Assert.Equal("网关", g.FixWithDetails("网管", "interview").Text);
        g.SetTemplates("支付项目", ["weekly", "standup"]);
        Assert.Equal("网关", g.FixWithDetails("网管", "weekly").Text);
        Assert.Equal("网管", g.FixWithDetails("网管", "interview").Text);
        Assert.Equal("网管", g.FixWithDetails("网管", null).Text);
        // 通用 terms always count.
        Assert.Equal("伊丽静", g.FixWithDetails("毕丽静", "interview").Text);
        Assert.Equal(["支付项目"], g.Groups);
        Assert.Equal(["weekly", "standup"], g.TemplatesOf("支付项目"));
        // The current meeting's template decides by default.
        g.CurrentTemplate = () => "interview";
        Assert.Equal("网管", g.Fix("网管"));
        Assert.Empty(g.In("网关配置"));
        g.CurrentTemplate = () => "weekly";
        Assert.Single(g.In("网关配置"));
    }

    [Fact]
    public void GroupsAndTheirTemplatesAreKeptAndExported()
    {
        var file = Path.Combine(Path.GetTempPath(), "sanqiutingji-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        var g = new Glossary(file);
        g.Import("网关\t网管\tgateway\t入口服务\t支付项目\n");
        g.SetTemplates("支付项目", ["weekly"]);
        var again = new Glossary(file);
        Assert.Equal("支付项目", again.Terms.Single().Group);
        Assert.Equal(["weekly"], again.TemplatesOf("支付项目"));
        Assert.Contains("\t支付项目", again.Export());
        File.Delete(file);
        File.Delete(Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-groups.json"));
    }

    [Fact]
    public void SoundAlikesTooFarToReplaceAreSuspects()
    {
        var g = Make(new GlossaryTerm { Text = "帆船" }, new GlossaryTerm { Text = "陈佩娜" }, new GlossaryTerm { Text = "网关" });
        // 犯成 (fan cheng) is not 帆船 (fan chuan): not replaced, but worth a look.
        Assert.Equal("对于犯成一米惜败", g.Fix("对于犯成一米惜败"));
        Assert.Contains(("犯成", "帆船"), g.Suspects("对于犯成一米惜败", null));
        // One character off in a longer name.
        Assert.Contains(("陈佩拉", "陈佩娜"), g.Suspects("叫陈佩拉那个女孩", null));
        // Ordinary talk raises nothing.
        Assert.Empty(g.Suspects("我周五前改好配置，周末跑一轮测试。", null));
    }

    [Fact]
    public void OnlyCorrectionsIntoGlossaryTermsAreTakenFromTheAi()
    {
        string[] terms = ["帆船", "陈佩娜"];
        var ok = GlossaryAiCheck.Accept("对于犯成一米惜败，叫陈佩拉", "对于帆船一米惜败，叫陈佩娜", terms)!;
        Assert.Equal([("犯成", "帆船"), ("陈佩拉", "陈佩娜")], ok.Select(f => (f.From, f.To)));
        // Anything else changed (here a word dropped, there punctuation): nothing is taken.
        Assert.Null(GlossaryAiCheck.Accept("对于犯成一米惜败", "帆船一米惜败", terms));
        Assert.Null(GlossaryAiCheck.Accept("对于犯成一米惜败", "对于帆船一米惜败！", terms));
        Assert.Null(GlossaryAiCheck.Accept("对于犯成一米惜败", "对于犯成一米惜败", terms));
        Assert.Null(GlossaryAiCheck.Accept("今天天气不错", "今天天气很好", terms));
        // Quotes around the answer are ignored.
        Assert.NotNull(GlossaryAiCheck.Accept("对于犯成", "「对于帆船」", terms));
    }

    [Fact]
    public void NotesGetTermsTheTranscriptStillMisspells()
    {
        // A meeting recorded before the term was added: only the misheard spelling is in it.
        var g = Make(new GlossaryTerm { Text = "陈佩娜", Misheard = ["陈佩纳"], Note = "帆船世界冠军" },
                     new GlossaryTerm { Text = "惜败" }, new GlossaryTerm { Text = "伊丽静" }, new GlossaryTerm { Text = "网关" });
        var hints = g.ForNotes("叫陈佩纳那个女孩，一米洗败。伊丽静负责联系。", null);
        Assert.Equal(["陈佩娜 (帆船世界冠军; the transcript writes it 陈佩纳)", "惜败 (the transcript writes it 洗败)", "伊丽静"], hints);
    }

    [Fact]
    public void TheAiJudgesAndOnlyAClearNoChangesAWord()
    {
        (string, string)[] suspects = [("犯成", "帆船"), ("反弹", "访谈"), ("访他", "访谈")];
        var fixes = GlossaryAiCheck.Corrections("{\"words\":[{\"n\":1,\"misheard\":true},{\"n\":2,\"misheard\":false},{\"n\":3}]}", suspects)!;
        Assert.Equal([("犯成", "帆船")], fixes.Select(f => (f.From, f.To)));
        // Text around the JSON, nonsense, numbers out of range: nothing changes.
        Assert.Single(GlossaryAiCheck.Corrections("好的：{\"words\":[{\"n\":1,\"misheard\":true},{\"n\":9,\"misheard\":true}]}", suspects)!);
        Assert.Null(GlossaryAiCheck.Corrections("不确定", suspects));
        Assert.Null(GlossaryAiCheck.Corrections("{\"words\":[{\"n\":2,\"misheard\":false}]}", suspects));
        // The prompt shows each candidate as the whole line, before and after.
        var prompt = GlossaryAiCheck.Prompt("我就去了现场采访他", [("访他", "访谈")], []);
        Assert.Contains("as heard: 我就去了现场采访他", prompt);
        Assert.Contains("changed:  我就去了现场采访谈", prompt);
    }

    [Fact]
    public void CategoriesSupportEmptyCreateRenameDeleteAndScopedImportExport()
    {
        var file = Path.Combine(Path.GetTempPath(), "sanqiutingji-cat-" + Guid.NewGuid().ToString("N") + ".json");
        var groupsFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-groups.json");
        try
        {
            var g = new Glossary(file);
            // Empty category persists across reloads
            Assert.True(g.AddGroup("电力专业"));
            Assert.Contains("电力专业", new Glossary(file).Groups);

            // Default group applies during batch import when 5th column is absent
            var (added, _) = g.Import("电力看价\t电力砍价|电力看家\t\t能源大数据场景\n专责\t专者\n贺一飞\t黑飞\t\t商务\t参会人名\n", defaultGroup: "电力专业");
            Assert.Equal(3, added);
            Assert.Equal("电力专业", g.FindByText("电力看价")?.Group);
            Assert.Equal("电力专业", g.FindByText("专责")?.Group);
            Assert.Equal("参会人名", g.FindByText("贺一飞")?.Group);

            // Scoped export only includes that category
            var powerOnly = g.Export("电力专业");
            Assert.Contains("电力看价", powerOnly);
            Assert.Contains("专责", powerOnly);
            Assert.DoesNotContain("贺一飞", powerOnly);

            // Rename category migrates terms
            Assert.True(g.RenameGroup("电力专业", "电力术语"));
            Assert.Equal("电力术语", g.FindByText("电力看价")?.Group);
            Assert.DoesNotContain("电力专业", g.Groups);

            // Learn with category assigns group
            g.Learn("迎峰度夏度冬", ["迎风度下肚东"], "项目名", "电力术语");
            Assert.Equal("电力术语", g.FindByText("迎峰度夏度冬")?.Group);

            // Deleting a category preserves terms in 通用 ("")
            var moved = g.DeleteGroup("电力术语");
            Assert.Equal(3, moved);
            Assert.Equal("", g.FindByText("电力看价")?.Group);
            Assert.DoesNotContain("电力术语", g.Groups);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(groupsFile)) File.Delete(groupsFile);
        }
    }

    [Fact]
    public void MixedLatinAndChineseTermsCorrectSoundAlikesAndRespectBoundaries()
    {
        var g = Make(
            new GlossaryTerm { Text = "EMS系统" },
            new GlossaryTerm { Text = "AGC控制" },
            new GlossaryTerm { Text = "10kV配网" });
        Assert.Equal("接入EMS系统运行", g.Fix("接入ems喜通运行"));
        Assert.Equal("投运AGC控制策略", g.Fix("投运agc孔制策略"));
        Assert.Equal("改造10kV配网线路", g.Fix("改造10kv佩王线路"));
        // ASCII boundary: "xEMS" must not trigger "EMS系统".
        Assert.Equal("xems喜通", g.Fix("xems喜通"));
    }

    [Fact]
    public void DialectToleranceFixesHenanAndRegionalAccentsWithoutHurtingTwoCharTerms()
    {
        var g = Make(
            new GlossaryTerm { Text = "继电保护" },
            new GlossaryTerm { Text = "算电协同" },
            new GlossaryTerm { Text = "柔性调控" },
            new GlossaryTerm { Text = "网关" });
        g.DialectTolerance = true;
        Assert.Equal("检查继电保护装置", g.Fix("检查基电报福装置"));   // f/h shift on 护
        Assert.Equal("推进算电协同项目", g.Fix("推进酸点雪通项目"));   // ue/ie shift on 协
        Assert.Equal("负荷柔性调控方案", g.Fix("负荷楼信跳空方案"));   // r/l shift on 柔

        // Two-character terms never use relaxed dialect vowel/consonant shifts.
        Assert.Equal("网福", g.Fix("网福"));

        g.DialectTolerance = false;
        Assert.Equal("检查基电报福装置", g.Fix("检查基电报福装置"));
    }

    [Fact]
    public void BuiltInPacksImportIdempotentlyAndKeepEverydaySpeechClean()
    {
        var file = Path.Combine(Path.GetTempPath(), "sanqiutingji-builtin-" + Guid.NewGuid().ToString("N") + ".json");
        var groupsFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-groups.json");
        try
        {
            var g = new Glossary(file);
            Assert.Equal(4, BuiltInGlossaries.All.Count);
            var (added, updated) = BuiltInGlossaries.Import(g, BuiltInGlossaries.All.Select(p => p.Id));
            Assert.True(added > 200);
            Assert.Equal(0, updated);
            foreach (var p in BuiltInGlossaries.All)
                Assert.Equal(p.TermCount, BuiltInGlossaries.CountInstalled(g, p));

            // Second import is idempotent.
            Assert.Equal((0, 0), BuiltInGlossaries.Import(g, BuiltInGlossaries.All.Select(p => p.Id)));

            // Power & Henan dialect corrections work out of the box.
            Assert.Equal("迎峰度夏期间安排专责盯紧台区线损", g.Fix("迎风度下期间安排专者盯紧太区现损"));
            Assert.Equal("推进V2G和算电协同试点", g.Fix("推进微兔G和算点协同试点"));
            Assert.Equal("中不中？搁这儿弄啥嘞，不老少人还不得劲", g.Fix("种不种？哥这儿农啥嘞，不老烧人还不得近"));

            // Everyday Mandarin sentences (including tricky near-homophones) remain untouched.
            foreach (var s in new[]
            {
                "这个问题不重，不用太紧张。",
                "他得了感冒，得好好休息两天。",
                "科研报告下周再交，十多个方案紧接着推进。",
                "他不让我低估这个风险，中了奖大家都很开心。",
                "我们周一早会先对齐需求，周五前把接口联调跑通。",
                "这批设备到货后按正常流程核对数量和外观即可。",
            })
                Assert.Equal(s, g.Fix(s));

            // ExactOnly survives export and re-import.
            var exported = g.Export("河南话常用词");
            Assert.Contains("\texact", exported);
            var g2 = Make();
            g2.Import(exported);
            Assert.True(g2.FindByText("可中")?.ExactOnly);

            // SeedMissing seeds once and respects subsequent user deletions.
            var settings = new AppSettings();
            var g3 = new Glossary(Path.Combine(Path.GetTempPath(), "sanqiutingji-seed-" + Guid.NewGuid().ToString("N") + ".json"));
            try
            {
                Assert.True(BuiltInGlossaries.SeedMissing(g3, settings));
                Assert.Equal(BuiltInGlossaries.All.Count, settings.SeededGlossaryPacks.Count);
                Assert.Contains("电力系统", g3.Groups);
                Assert.Contains("河南话常用词", g3.Groups);
                var target = g3.FindByText("算电协同")!;
                g3.Remove(target.Id);
                Assert.False(BuiltInGlossaries.SeedMissing(g3, settings));
                Assert.Null(g3.FindByText("算电协同"));
            }
            finally
            {
                // Best-effort cleanup of temp glossary files.
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "sanqiutingji-seed-*"))
                    try { File.Delete(f); } catch { }
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(groupsFile)) File.Delete(groupsFile);
        }
    }
}
