using System.Text;

namespace SanqiuTingji;

/// Metadata for a built-in industry or dialect glossary pack embedded in the app.
public sealed record BuiltInGlossaryPack(
    string Id,
    string Name,
    string Category,
    string Description,
    string ResourceName,
    int TermCount);

/// Curated, false-positive-checked glossary packs (power grid, new energy & compute-power synergy,
/// bidding & engineering, Henan dialect high-frequency spoken words) embedded as resources so users
/// can enable them in one click without slowing down or polluting unrelated meetings.
public static class BuiltInGlossaries
{
    private sealed record PackSpec(string Id, string Name, string Category, string Description, string ResourceName);

    private static readonly PackSpec[] Specs =
    [
        new("power-grid",
            "电力系统与电网调度",
            "电力系统",
            "涵盖调度运行、变配电设备、继电保护、线损电价与 SCADA/EMS/AGC 等核心缩写",
            "SanqiuTingji.Glossaries.power-grid.txt"),
        new("new-energy-compute",
            "新能源、储能与算电协同",
            "新能源与算电协同",
            "涵盖负荷柔性调控、车网互动 V2G、虚拟电厂、电化学储能、绿电溯源与算电协同 PUE",
            "SanqiuTingji.Glossaries.new-energy-compute.txt"),
        new("bidding-engineering",
            "招投标与电力数字化工程",
            "招投标与工程",
            "涵盖技术规范书、型式试验、CMA/CNAS、数模评审、贯通率、数据中台与实训考评",
            "SanqiuTingji.Glossaries.bidding-engineering.txt"),
        new("henan-dialect",
            "河南话高频口语与易错词",
            "河南话常用词",
            "精选河南话会议及日常高频词与常见识别误听（如中不中、弄啥嘞、得劲、不老少、拾掇等）",
            "SanqiuTingji.Glossaries.henan-dialect.txt"),
    ];

    private static readonly Lazy<IReadOnlyList<BuiltInGlossaryPack>> CachedAll = new(() =>
        Specs.Select(s => new BuiltInGlossaryPack(s.Id, s.Name, s.Category, s.Description, s.ResourceName, ParseSpellings(ReadResource(s.ResourceName)).Count))
            .ToList());

    public static IReadOnlyList<BuiltInGlossaryPack> All => CachedAll.Value;

    public static string ReadContent(BuiltInGlossaryPack pack) => ReadResource(pack.ResourceName);

    public static int CountInstalled(Glossary glossary, BuiltInGlossaryPack pack)
    {
        var existing = glossary.Terms.Select(t => t.Text).ToHashSet();
        return ParseSpellings(ReadResource(pack.ResourceName)).Count(existing.Contains);
    }

    /// Imports selected built-in packs idempotently into their respective categories.
    public static (int Added, int Updated) Import(Glossary glossary, IEnumerable<string> packIds)
    {
        var ids = packIds.ToHashSet();
        int totalAdded = 0, totalUpdated = 0;
        foreach (var pack in All)
        {
            if (!ids.Contains(pack.Id)) continue;
            var content = ReadResource(pack.ResourceName);
            if (content.Length == 0) continue;
            var (added, updated) = glossary.Import(content, defaultGroup: pack.Category);
            totalAdded += added;
            totalUpdated += updated;
        }
        return (totalAdded, totalUpdated);
    }

    /// Auto-imports any built-in packs that have not yet been seeded for this user,
    /// without overwriting custom edits or re-adding terms the user later deleted.
    public static bool SeedMissing(Glossary glossary, AppSettings settings)
    {
        settings.SeededGlossaryPacks ??= [];
        var missing = All.Where(p => !settings.SeededGlossaryPacks.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();
        if (missing.Count == 0) return false;
        Import(glossary, missing);
        settings.SeededGlossaryPacks.AddRange(missing);
        return true;
    }

    internal static IReadOnlyList<string> ParseSpellings(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var tab = line.IndexOf('\t');
            var spelling = (tab >= 0 ? line[..tab] : line).Trim();
            if (spelling.Length > 0) list.Add(spelling);
        }
        return list;
    }

    private static string ReadResource(string resourceName)
    {
        using var stream = typeof(BuiltInGlossaries).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
