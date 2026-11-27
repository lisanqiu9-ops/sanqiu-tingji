using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SanqiuTingji.Tests;

public sealed class WordExportTests
{
    [Fact]
    public void WordDocumentPreservesNotesTranscriptNamesAndTodoTable()
    {
        var session = new Session
        {
            Title = "项目评审 <&> 日本語 한국어", Renamed = true, StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow.AddMinutes(5), VoiceNames = new() { ["7"] = "王敏" },
            Lines = [new() { Id = "a", Src = "请在周五前完成检查。", Voice = 7 },
                new() { Id = "b", Src = "收到，今天开始测试。", Dst = "Testing starts today.", Voice = 7 }],
            Notes = new MeetingNotes
            {
                Summary = "发言人1确认本周完成。",
                Topics = [new() { Title = "发布安排", Summary = "检查进度", Points = ["周五完成"],
                    Fishbone = new() { Question = "为什么延期", Branches = [new() { Label = "依赖", Detail = "等待确认" }] } }],
                Sections = [new() { Title = "待办事项", Style = "todo", Items = [new() { Text = "执行测试", Owner = "王敏", Due = "11月13日" }] }],
                Terms = [new() { Name = "SanqiuTingji", Note = "会议助手", Heard = ["会意"] }]
            }
        };
        using var stream = new MemoryStream(WordExport.Create(session));
        using var document = WordprocessingDocument.Open(stream, false);
        AssertValid(document);
        var body = document.MainDocumentPart!.Document!.Body!;
        Assert.Contains(session.Title, body.InnerText);
        Assert.Contains("王敏确认本周完成", body.InnerText);
        Assert.Contains("Testing starts today.", body.InnerText);
        Assert.Contains("等待确认", body.InnerText);
        Assert.Contains("会意", body.InnerText);
        Assert.Contains("王敏", body.Elements<Table>().Single().InnerText);
        Assert.Equal(2, body.Elements<Table>().Single().Elements<TableRow>().Count());
        Assert.NotEmpty(body.Descendants<NumberingProperties>());
        Assert.Equal("Title", body.Elements<Paragraph>().First().ParagraphProperties!.ParagraphStyleId!.Val);
    }

    [Fact]
    public void OldMeetingWithoutNotesOrVoiceDataExports()
    {
        var session = new Session { Title = "旧会议", StartedAt = DateTime.UtcNow, Lines = [new() { Src = "原文\n下一行" }] };
        using var stream = new MemoryStream(WordExport.Create(session));
        using var document = WordprocessingDocument.Open(stream, false);
        AssertValid(document);
        Assert.Contains("原文", document.MainDocumentPart!.Document!.InnerText);
        Assert.Single(document.MainDocumentPart.Document.Descendants<Break>());
    }

    [Fact]
    public void FormalMinutesWordMatchesOfficialTabularLayoutAndExcludesTranscript()
    {
        var start = new DateTime(2026, 11, 27, 2, 0, 0, DateTimeKind.Utc); // 10:00 UTC+8
        var session = new Session
        {
            Title = "负荷预测讨论会",
            Renamed = true,
            StartedAt = start,
            EndedAt = start.AddMinutes(90),
            VoiceNames = new() { ["1"] = "王登政", ["2"] = "高璐" },
            Lines =
            [
                new() { Id = "l1", Src = "逐字稿原始口语内容不应出现在正式纪要中。", Voice = 1 },
            ],
            Notes = new MeetingNotes
            {
                Summary = "会议明确了设备级负荷预测的数据深度要求与近期分工。",
                Topics =
                [
                    new()
                    {
                        Title = "负荷预测业务汇报答疑",
                        Summary = "讨论人工智能曲线预测与气象数据接入需求。",
                        Points =
                        [
                            "王登政：常规可控电源发电出力模拟运行功能可以开展。",
                            "高璐：后续颗粒度更细的气象实况接入后再升级算法精度。",
                        ],
                    },
                ],
                Sections =
                [
                    new()
                    {
                        Title = "决议与结论",
                        Style = "list",
                        Items = [new() { Text = "下周出具完整设备负荷预测方案" }],
                    },
                    new()
                    {
                        Title = "待办事项",
                        Style = "todo",
                        Items = [new() { Text = "排查高压用户负荷曲线数据问题", Owner = "建新", Due = "12月12日" }],
                    },
                ],
            },
        };
        var meta = new FormalMinutesMeta(
            MeetingName: "负荷预测讨论会",
            MeetingTime: "2026年11月27日星期五，10:00-11:30",
            Location: "腾讯会议",
            Attendees: "国网发展部：王登政\n项目组相关人员",
            Host: "高璐",
            Recorder: "王玉帅",
            RecordDate: "2026.11.27");

        using var stream = new MemoryStream(WordExport.CreateFormalMinutes(session, meta));
        using var document = WordprocessingDocument.Open(stream, false);
        AssertValid(document);

        var body = document.MainDocumentPart!.Document!.Body!;
        var allText = body.InnerText;
        Assert.Contains("会议纪要", allText);
        Assert.Contains("负荷预测讨论会", allText);
        Assert.Contains("腾讯会议", allText);
        Assert.Contains("王登政", allText);
        Assert.Contains("一、负荷预测业务汇报答疑：", allText);
        Assert.Contains("二、决议与结论：", allText);
        Assert.Contains("排查高压用户负荷曲线数据问题", allText);
        Assert.Contains("责任人：建新；完成时限：12月12日", allText);
        Assert.Contains("王玉帅", allText);
        Assert.Contains("2026.11.27", allText);
        Assert.DoesNotContain("逐字稿原始口语内容不应出现在正式纪要中", allText);

        var table = body.Elements<Table>().Single();
        Assert.Equal(12, table.Elements<TableRow>().Count());
        Assert.Equal(5, table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Count());
        Assert.Contains(document.MainDocumentPart.FooterParts, f => f.Footer!.InnerText.Contains("PAGE"));

        // Verify NotesOnlyMarkdown / NotesOnlyPlainText exclude raw transcript, and PlainText omits Markdown syntax.
        var mdOnly = TranscriptExport.NotesOnlyMarkdown(session, meta);
        Assert.Contains("# 会议纪要", mdOnly);
        Assert.Contains("## 督办事项", mdOnly);
        Assert.DoesNotContain("逐字稿原始口语内容不应出现在正式纪要中", mdOnly);

        var txtOnly = TranscriptExport.NotesOnlyPlainText(session, meta);
        Assert.Contains("【会议议程】", txtOnly);
        Assert.Contains("【督办事项】", txtOnly);
        Assert.DoesNotContain("##", txtOnly);
        Assert.DoesNotContain("**", txtOnly);
        Assert.DoesNotContain("逐字稿原始口语内容不应出现在正式纪要中", txtOnly);

        var fullTxt = TranscriptExport.PlainText(session);
        Assert.Contains("【纪要】", fullTxt);
        Assert.Contains("【逐字稿】", fullTxt);
        Assert.Contains("逐字稿原始口语内容不应出现在正式纪要中", fullTxt);
        Assert.DoesNotContain("##", fullTxt);
        Assert.DoesNotContain("**", fullTxt);
    }

    private static void AssertValid(WordprocessingDocument document)
    {
        var errors = new OpenXmlValidator().Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Description + " " + e.Path?.XPath)));
    }
}
