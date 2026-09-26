using System.Text.Json;

namespace KardsSim.Core;

/// <summary>卡组预设（decks.json 里的一条）。</summary>
public sealed class DeckPreset
{
    /// <summary>ASCII 短名，方便命令行/HTTP 里写（中文名在 shell 里容易出编码问题）。</summary>
    public string Key = "";
    public string Name = "";
    public string Code = "";
    public string Note = "";
    public override string ToString() => $"{Key}({Name})";
}

/// <summary>
/// 卡组预设表。CLI / HTTP 里传卡组时，除了 <c>%%</c> 开头的卡组码和逗号分隔的卡牌 id 列表，
/// 还可以直接写预设名（<c>日波炸槽</c> 或 <c>jp-pl-boom</c>）。
///
/// <para>
/// 默认从仓库根的 <c>decks.json</c> 读；分类目录里没有就报错并提示文件位置。
/// </para>
/// </summary>
public static class DeckPresets
{
    private static List<DeckPreset> _all;
    private static readonly object _lock = new();

    public static IReadOnlyList<DeckPreset> All
    {
        get
        {
            if (_all != null) return _all;
            lock (_lock)
            {
                _all ??= LoadFrom(DataFiles.Require("decks.json",
                    "它列着社区推荐卡组（名字 + 卡组码），可以照 README「卡组码」一节自己写"));
                return _all;
            }
        }
    }

    /// <summary>从指定文件重新加载（测试用）。</summary>
    public static void Load(string path)
    {
        lock (_lock) _all = LoadFrom(path);
    }

    private static List<DeckPreset> LoadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var list = new List<DeckPreset>();
        if (!doc.RootElement.TryGetProperty("decks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{path} 里没有 decks 数组");
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new DeckPreset
            {
                Key = Str(e, "key"),
                Name = Str(e, "name"),
                Code = Str(e, "code"),
                Note = Str(e, "note"),
            });
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    /// <summary>
    /// 按短名 / 中文名 / <c>#序号</c> 找预设；找不到返回 null。
    /// 序号是 1 基的，和 <c>--mode decks</c> 打印的编号一致。
    /// </summary>
    public static DeckPreset Find(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim();
        foreach (var p in All)
            if (string.Equals(p.Key, t, StringComparison.OrdinalIgnoreCase)) return p;
        foreach (var p in All)
            if (string.Equals(p.Name, t, StringComparison.Ordinal)) return p;
        var digits = t.StartsWith("#", StringComparison.Ordinal) ? t[1..] : t;
        if (int.TryParse(digits, out var idx) && idx >= 1 && idx <= All.Count) return All[idx - 1];
        return null;
    }

    /// <summary>把预设解析成卡组（带覆盖度报告）。</summary>
    public static ParsedDeck Parse(DeckPreset p) => DeckCode.Parse(p.Code);
}
