using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SanqiuTingji.Localization;

namespace SanqiuTingji;

/// Editable metadata header for the formal tabular meeting minutes (《会议纪要》) export.
public sealed record FormalMinutesMeta(
    string MeetingName,
    string MeetingTime,
    string Location = "",
    string Attendees = "",
    string Host = "",
    string Recorder = "",
    string RecordDate = "")
{
    private static readonly string[] ChineseWeekdays = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

    public static FormalMinutesMeta FromSession(Session session)
    {
        var start = session.StartedAt.ToLocalTime();
        var end = session.EndedAt?.ToLocalTime() ?? start.Add(session.Duration);
        var weekday = ChineseWeekdays[(int)start.DayOfWeek];
        var timeRange = end > start.AddMinutes(1)
            ? $"{start:yyyy年M月d日}{weekday}，{start:HH:mm}-{end:HH:mm}"
            : $"{start:yyyy年M月d日}{weekday}，{start:HH:mm}";

        var namedSpeakers = SpeakerLabels.Names(session)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new FormalMinutesMeta(
            MeetingName: session.DisplayTitle,
            MeetingTime: timeRange,
            Location: "",
            Attendees: namedSpeakers.Count > 0 ? string.Join("、", namedSpeakers) : "",
            Host: "",
            Recorder: "",
            RecordDate: start.ToString("yyyy.MM.dd"));
    }
}

public static class WordExport
{
    private const string FormalTitleFont = "方正小标宋_GBK";
    private const string FormalBodyFont = "仿宋_GB2312";
    private const string FormalLineSpacing = "580"; // 29pt exact line height, matching standard official minutes
    private static readonly string[] FormalGridWidths = ["1403", "860", "2127", "1984", "2977"]; // Total = 9351 dxa

    /// Formal tabular meeting minutes (《会议纪要》, AI summary only, without raw transcript),
    /// matching standard Chinese enterprise / State Grid minutes layout.
    public static byte[] CreateFormalMinutes(Session session, FormalMinutesMeta? meta = null)
    {
        meta ??= FormalMinutesMeta.FromSession(session);
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                new Style(new StyleName { Val = "Normal" },
                    new StyleParagraphProperties(new Justification { Val = JustificationValues.Both }),
                    new StyleRunProperties(
                        new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "宋体", ComplexScript = "Times New Roman" },
                        new FontSize { Val = "21" }))
                { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                new Style(new StyleName { Val = "footer" }, new BasedOn { Val = "Normal" },
                    new StyleParagraphProperties(new Justification { Val = JustificationValues.Center }),
                    new StyleRunProperties(new FontSize { Val = "18" }, new FontSizeComplexScript { Val = "18" }))
                { Type = StyleValues.Paragraph, StyleId = "Footer" });

            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Footer" }, new Justification { Val = JustificationValues.Center }),
                    new Run(new RunProperties(new FontSize { Val = "18" }), new FieldChar { FieldCharType = FieldCharValues.Begin }),
                    new Run(new RunProperties(new FontSize { Val = "18" }), new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(new RunProperties(new FontSize { Val = "18" }), new FieldChar { FieldCharType = FieldCharValues.Separate }),
                    new Run(new RunProperties(new FontSize { Val = "18" }), Text("- 1 -")),
                    new Run(new RunProperties(new FontSize { Val = "18" }), new FieldChar { FieldCharType = FieldCharValues.End })));
            footerPart.Footer.Save();
            var footerId = main.GetIdOfPart(footerPart);

            // Top document title: 会议纪要 (centered, 方正小标宋_GBK 18pt bold)
            body.Append(new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { Line = FormalLineSpacing, LineRule = LineSpacingRuleValues.Exact },
                    new Justification { Val = JustificationValues.Center }),
                FormalRun("会议纪要", FormalTitleFont, "36", bold: true)));
            body.Append(new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "80", Line = "240", LineRule = LineSpacingRuleValues.Auto })));

            var table = new Table(
                new TableProperties(
                    new TableWidth { Width = "9351", Type = TableWidthUnitValues.Dxa },
                    new TableJustification { Val = TableRowAlignmentValues.Center },
                    new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 },
                        new LeftBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 },
                        new BottomBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 },
                        new RightBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 },
                        new InsideVerticalBorder { Val = BorderValues.Single, Color = "000000", Size = 4, Space = 0 }),
                    new TableCellMarginDefault(
                        new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                        new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                        new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                        new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa })),
                new TableGrid(FormalGridWidths.Select(w => new GridColumn { Width = w }).ToArray()));

            // Rows 1-5: basic metadata (2-col label + 3-col value)
            table.Append(FormalMetaRow("会议名称", meta.MeetingName));
            table.Append(FormalMetaRow("会议时间", meta.MeetingTime));
            table.Append(FormalMetaRow("会议地点", meta.Location));
            table.Append(FormalMetaRow("参会人员", meta.Attendees, minHeight: 1100));
            table.Append(FormalMetaRow("会议主持人", meta.Host));

            var notes = session.Notes is { IsEmpty: false } written
                ? SpeakerLabels.WithNames(written, SpeakerLabels.Names(session))
                : null;

            // Rows 6-7: 会议议程
            table.Append(FormalBannerRow("会议议程"));
            table.Append(FormalFullContentRow(BuildAgendaParagraphs(notes), minHeight: 1000));

            // Rows 8-9: 会议纪要
            table.Append(FormalBannerRow("会议纪要"));
            table.Append(FormalFullContentRow(BuildMinutesParagraphs(notes), minHeight: 1200));

            // Rows 10-11: 督办事项
            table.Append(FormalBannerRow("督办事项"));
            table.Append(FormalFullContentRow(BuildSupervisionParagraphs(notes), minHeight: 620));

            // Row 12: 记录人 | xxx | 记录时间 | yyyy.MM.dd
            table.Append(new TableRow(
                new TableRowProperties(new TableRowHeight { Val = 520, HeightType = HeightRuleValues.AtLeast }),
                FormalCell(1403, 1, centerV: true, FormalLabelParagraph("记录人")),
                FormalCell(2987, 2, centerV: true, FormalBodyParagraph(meta.Recorder, JustificationValues.Center)),
                FormalCell(1984, 1, centerV: true, FormalLabelParagraph("记录时间")),
                FormalCell(2977, 1, centerV: true, FormalBodyParagraph(meta.RecordDate, JustificationValues.Center))));

            body.Append(table);
            body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto })));
            body.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1418, Right = 1274, Bottom = 1134, Left = 709, Header = 851, Footer = 567, Gutter = 567 },
                new PageNumberType { Format = NumberFormatValues.NumberInDash }));
        }
        return stream.ToArray();
    }

    /// Full archive Word document (notes + complete timestamped transcript + optional translations).
    public static byte[] Create(Session session, bool includeTranslation = true)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                new Style(new StyleName { Val = "Normal" },
                    new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "288", LineRule = LineSpacingRuleValues.Auto }),
                    new StyleRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" }, new FontSize { Val = "22" }))
                { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                HeadingStyle("Title", "32", null), HeadingStyle("Heading1", "28", 0), HeadingStyle("Heading2", "24", 1));
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(new Level(new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "360", Hanging = "240" })) { LevelIndex = 0 }) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });

            body.Append(Paragraph(session.DisplayTitle, "Title"));
            var start = session.StartedAt.ToLocalTime();
            string At(double seconds) => NotesPrompt.ClockAt(start, seconds);
            body.Append(Paragraph($"{start:yyyy-MM-dd HH:mm} · {L.T("时长")} {TranscriptExport.Clock(session.Duration.TotalSeconds)}"));
            if (session.Notes is { IsEmpty: false } written)
            {
                var notes = SpeakerLabels.WithNames(written, SpeakerLabels.Names(session));
                body.Append(Paragraph(L.T("纪要"), "Heading1"));
                if (notes.Summary.Length > 0) body.Append(Paragraph(notes.Summary));
                foreach (var topic in notes.Topics)
                {
                    body.Append(Paragraph($"{At(topic.Start)} {topic.Title}", "Heading2"));
                    if (!string.IsNullOrEmpty(topic.Summary)) body.Append(Paragraph(topic.Summary));
                    for (var i = 0; i < topic.Points.Count; i++)
                        body.Append(Bullet(topic.Points[i] + (topic.PointTime(i) is { } time ? $"（{At(time)}）" : "")));
                    if (topic.Fishbone is { } fishbone)
                    {
                        body.Append(Paragraph(L.T("原因分析") + " · " + fishbone.Question));
                        foreach (var branch in fishbone.Branches) body.Append(Bullet(branch.Label + " · " + branch.Detail));
                    }
                }
                foreach (var section in notes.Parts().Where(p => p.Items.Count > 0))
                {
                    body.Append(Paragraph(section.Title, "Heading2"));
                    if (section.Style == "todo") body.Append(TodoTable(section.Items));
                    else foreach (var item in section.Items)
                    {
                        var extra = new[] { item.Owner, item.Due }.Where(x => !string.IsNullOrEmpty(x)).ToList();
                        body.Append(Bullet(item.Text + (extra.Count > 0 ? $"（{string.Join("，", extra)}）" : "")));
                    }
                }
                if (notes.Terms.Count > 0)
                {
                    body.Append(Paragraph(L.T("人名与术语"), "Heading2"));
                    foreach (var term in NoteTerm.Ordered(notes.Terms))
                        body.Append(Bullet(term.Name + " · " + term.Note + (term.Heard.Count > 0 ? $"（{L.T("识别成")} {string.Join("、", term.Heard)}）" : "")));
                }
            }
            body.Append(Paragraph(L.T("逐字稿"), "Heading1"));
            var numbers = SpeakerLabels.Numbers(session.Lines);
            var names = SpeakerLabels.Names(session);
            foreach (var line in session.Lines)
            {
                var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }),
                    new Run(new RunProperties(new Bold()), Text($"{At(line.T)} {SpeakerLabels.Who(line, numbers, names)}  ")),
                    TextRun(line.Src));
                var hasTranslation = includeTranslation && !string.IsNullOrEmpty(line.Dst);
                if (hasTranslation) paragraph.ParagraphProperties!.Append(new KeepNext());
                body.Append(paragraph);
                if (hasTranslation) body.Append(Paragraph(line.Dst!));
            }
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 567, Footer = 567, Gutter = 0 }));
        }
        return stream.ToArray();
    }

    // ── Formal minutes helpers ──

    private static TableRow FormalMetaRow(string label, string value, uint? minHeight = null)
    {
        var row = new TableRow();
        if (minHeight is { } h)
            row.Append(new TableRowProperties(new TableRowHeight { Val = h, HeightType = HeightRuleValues.AtLeast }));
        row.Append(FormalCell(2263, 2, centerV: true, FormalLabelParagraph(label)));

        var lines = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var paragraphs = lines.Length > 0
            ? lines.Select(line => FormalBodyParagraph(line, JustificationValues.Left)).ToArray()
            : [FormalBodyParagraph("", JustificationValues.Left)];
        row.Append(FormalCell(7088, 3, centerV: lines.Length <= 1, paragraphs));
        return row;
    }

    private static TableRow FormalBannerRow(string title) =>
        new(FormalCell(9351, 5, centerV: true, FormalLabelParagraph(title)));

    private static TableRow FormalFullContentRow(IReadOnlyList<Paragraph> paragraphs, uint? minHeight = null)
    {
        var row = new TableRow();
        if (minHeight is { } h)
            row.Append(new TableRowProperties(new TableRowHeight { Val = h, HeightType = HeightRuleValues.AtLeast }));
        var content = paragraphs.Count > 0 ? paragraphs.ToArray() : [FormalBodyParagraph("", JustificationValues.Left)];
        row.Append(FormalCell(9351, 5, centerV: false, content));
        return row;
    }

    private static TableCell FormalCell(int widthDxa, int gridSpan, bool centerV, params Paragraph[] paragraphs)
    {
        var props = new TableCellProperties(new TableCellWidth { Width = widthDxa.ToString(), Type = TableWidthUnitValues.Dxa });
        if (gridSpan > 1) props.Append(new GridSpan { Val = gridSpan });
        if (centerV) props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
        var cell = new TableCell(props);
        foreach (var p in paragraphs) cell.Append(p);
        return cell;
    }

    private static Paragraph FormalLabelParagraph(string text) =>
        new(
            new ParagraphProperties(
                new SnapToGrid { Val = false },
                new SpacingBetweenLines { Line = FormalLineSpacing, LineRule = LineSpacingRuleValues.Exact },
                new Justification { Val = JustificationValues.Center }),
            FormalRun(text, FormalBodyFont, "32", bold: true));

    private static Paragraph FormalBodyParagraph(string text, JustificationValues align, bool bold = false, bool firstLineIndent = false)
    {
        var pPr = new ParagraphProperties(
            new SnapToGrid { Val = false },
            new SpacingBetweenLines { Line = FormalLineSpacing, LineRule = LineSpacingRuleValues.Exact });
        if (firstLineIndent)
            pPr.Append(new Indentation { FirstLine = "560", FirstLineChars = 200 });
        pPr.Append(new Justification { Val = align });
        var p = new Paragraph(pPr);
        if (!string.IsNullOrEmpty(text))
            p.Append(FormalRun(text, FormalBodyFont, "28", bold));
        return p;
    }

    /// Formats a body item where a short speaker/subject prefix before "：" is emphasized in bold,
    /// mirroring official meeting minutes like "王登政：除分布式电源外……".
    private static Paragraph FormalPointParagraph(string text, bool firstLineIndent = true)
    {
        var pPr = new ParagraphProperties(
            new SnapToGrid { Val = false },
            new SpacingBetweenLines { Line = FormalLineSpacing, LineRule = LineSpacingRuleValues.Exact });
        if (firstLineIndent)
            pPr.Append(new Indentation { FirstLine = "560", FirstLineChars = 200 });
        pPr.Append(new Justification { Val = JustificationValues.Both });
        var p = new Paragraph(pPr);

        var colon = text.IndexOf('：');
        if (colon is >= 2 and <= 16 && colon < text.Length - 1)
        {
            var head = text[..(colon + 1)];
            var rest = text[(colon + 1)..];
            p.Append(FormalRun(head, FormalBodyFont, "28", bold: true));
            p.Append(FormalRun(rest, FormalBodyFont, "28", bold: false));
        }
        else
        {
            p.Append(FormalRun(text, FormalBodyFont, "28", bold: false));
        }
        return p;
    }

    private static Run FormalRun(string value, string font, string halfPtSize, bool bold)
    {
        var rPr = new RunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font });
        if (bold)
        {
            rPr.Append(new Bold());
            rPr.Append(new BoldComplexScript());
        }
        rPr.Append(new FontSize { Val = halfPtSize });
        rPr.Append(new FontSizeComplexScript { Val = halfPtSize });
        var run = new Run(rPr);
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            run.Append(Text(lines[i]));
        }
        return run;
    }

    private static List<Paragraph> BuildAgendaParagraphs(MeetingNotes? notes)
    {
        var list = new List<Paragraph>();
        if (notes == null) return list;

        if (notes.Topics.Count > 0)
        {
            for (var i = 0; i < notes.Topics.Count; i++)
            {
                var title = notes.Topics[i].Title.Trim();
                if (title.Length > 0)
                    list.Add(FormalBodyParagraph($"{i + 1}、{title}", JustificationValues.Left, bold: false, firstLineIndent: true));
            }
        }
        else if (!string.IsNullOrWhiteSpace(notes.Summary))
        {
            list.Add(FormalBodyParagraph($"1、{notes.Summary.Trim()}", JustificationValues.Left, bold: false, firstLineIndent: true));
        }
        return list;
    }

    private static List<Paragraph> BuildMinutesParagraphs(MeetingNotes? notes)
    {
        var list = new List<Paragraph>();
        if (notes == null) return list;

        if (!string.IsNullOrWhiteSpace(notes.Summary))
            list.Add(FormalBodyParagraph(notes.Summary.Trim(), JustificationValues.Both, bold: false, firstLineIndent: true));

        var sectionIndex = 0;
        foreach (var topic in notes.Topics)
        {
            sectionIndex++;
            var heading = $"{ChineseSectionNum(sectionIndex)}、{topic.Title.Trim()}";
            if (!heading.EndsWith('：') && !heading.EndsWith(':')) heading += "：";
            list.Add(FormalBodyParagraph(heading, JustificationValues.Left, bold: true, firstLineIndent: false));

            if (!string.IsNullOrWhiteSpace(topic.Summary))
                list.Add(FormalPointParagraph(topic.Summary.Trim(), firstLineIndent: true));

            for (var i = 0; i < topic.Points.Count; i++)
            {
                var pt = topic.Points[i].Trim();
                if (pt.Length > 0)
                    list.Add(FormalPointParagraph($"{i + 1}）{pt}", firstLineIndent: true));
            }

            if (topic.Fishbone is { } fishbone)
            {
                list.Add(FormalPointParagraph($"原因分析：{fishbone.Question}", firstLineIndent: true));
                foreach (var b in fishbone.Branches)
                    list.Add(FormalPointParagraph($"{b.Label}：{b.Detail}", firstLineIndent: true));
            }
        }

        // Append non-todo structured sections (e.g. 决议与结论 / 风险与问题 / 反馈) into the minutes body.
        foreach (var part in notes.Parts().Where(p => p.Style != "todo" && p.Items.Count > 0))
        {
            sectionIndex++;
            var heading = $"{ChineseSectionNum(sectionIndex)}、{part.Title.Trim()}：";
            list.Add(FormalBodyParagraph(heading, JustificationValues.Left, bold: true, firstLineIndent: false));
            for (var i = 0; i < part.Items.Count; i++)
            {
                var item = part.Items[i];
                var extra = new[] { item.Owner, item.Due }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                var text = $"{i + 1}）{item.Text.Trim()}{(extra.Count > 0 ? $"（{string.Join("，", extra)}）" : "")}";
                list.Add(FormalPointParagraph(text, firstLineIndent: true));
            }
        }

        return list;
    }

    private static List<Paragraph> BuildSupervisionParagraphs(MeetingNotes? notes)
    {
        var list = new List<Paragraph>();
        if (notes == null) return list;

        var todos = notes.Parts()
            .Where(p => p.Style == "todo")
            .SelectMany(p => p.Items)
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .ToList();

        for (var i = 0; i < todos.Count; i++)
        {
            var item = todos[i];
            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Owner)) tags.Add($"责任人：{item.Owner.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Due)) tags.Add($"完成时限：{item.Due.Trim()}");
            var suffix = tags.Count > 0 ? $"（{string.Join("；", tags)}）" : "";
            list.Add(FormalPointParagraph($"{i + 1}、{item.Text.Trim()}{suffix}", firstLineIndent: true));
        }
        return list;
    }

    internal static string ChineseSectionNum(int n)
    {
        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];
        if (n <= 10) return digits[n];
        if (n < 20) return "十" + digits[n - 10];
        if (n < 100) return digits[n / 10] + "十" + (n % 10 == 0 ? "" : digits[n % 10]);
        return n.ToString();
    }

    // ── Full archive Word helpers ──

    private static Style HeadingStyle(string id, string size, int? level)
    {
        var properties = new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" });
        if (level is { } value) properties.Append(new OutlineLevel { Val = value });
        return new Style(new StyleName { Val = id }, new BasedOn { Val = "Normal" }, properties,
            new StyleRunProperties(new Bold(), new Color { Val = "1E2235" }, new FontSize { Val = size }))
        { Type = StyleValues.Paragraph, StyleId = id };
    }

    private static Text Text(string value) => new(value) { Space = SpaceProcessingModeValues.Preserve };

    private static Run TextRun(string value)
    {
        var run = new Run();
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            run.Append(Text(lines[i]));
        }
        return run;
    }

    private static Paragraph Paragraph(string value, string style = "Normal") =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), TextRun(value));

    private static Paragraph Bullet(string value)
    {
        var paragraph = Paragraph(value);
        paragraph.ParagraphProperties!.Append(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }));
        return paragraph;
    }

    private static Table TodoTable(IReadOnlyList<NoteItem> items)
    {
        var table = new Table(new TableProperties(new TableWidth { Width = "9600", Type = TableWidthUnitValues.Dxa },
            new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" })),
            new TableGrid(new GridColumn { Width = "5200" }, new GridColumn { Width = "1600" }, new GridColumn { Width = "2800" }));
        var header = TableRow([L.T("事项"), L.T("负责人"), L.T("截止时间")]);
        header.PrependChild(new TableRowProperties(new TableHeader()));
        foreach (var cell in header.Elements<TableCell>())
        {
            cell.TableCellProperties!.Append(new Shading { Fill = "ECEEFC", Val = ShadingPatternValues.Clear });
            cell.Descendants<Run>().First().PrependChild(new RunProperties(new Bold()));
        }
        table.Append(header);
        foreach (var item in items) table.Append(TableRow([item.Text, item.Owner ?? "—", item.Due ?? "—"]));
        return table;
    }

    private static TableRow TableRow(string[] values) => new(values.Select((value, index) =>
        new TableCell(new TableCellProperties(new TableCellWidth { Width = new[] { "5200", "1600", "2800" }[index], Type = TableWidthUnitValues.Dxa }), Paragraph(value))));
}
