using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace KardsDataExtract;

/// <summary>
/// 从 KARDS 客户端导出的蓝图资产里抽出卡牌定义。
///
/// 关键事实：卡牌是 UPrimaryDataAsset 的子类，实际数值全在 BlueprintGeneratedClass 的
/// CDO（Default__xxx_C）的序列化属性里，不在任何函数字节码里。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var cardsDir  = Arg(args, "--cards") ?? @"H:\go-cache\KardsSim\_input\Cards";
        var usmapPath = Arg(args, "--usmap") ?? @"H:\go-cache\KardsSim\_input\m.usmap";
        var outPath   = Arg(args, "--out")   ?? @"H:\go-cache\KardsSim\cards.json";
        var logicOut  = Arg(args, "--logic-out") ?? @"H:\go-cache\KardsSim\bp_logic.json";

        var usmap = new Usmap(usmapPath);
        var files = Directory.GetFiles(cardsDir, "*.uasset", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"发现 {files.Count} 个 .uasset");

        var cards = new List<CardRecord>();
        var logic = new List<CardRecord>();
        var fails = new List<(string, string)>();

        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            try
            {
                var asset = new UAsset(f, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None);
                var cdo = asset.Exports.OfType<NormalExport>()
                               .FirstOrDefault(x => x.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal));
                if (cdo is null) continue;
                var rec = Extract(asset, cdo, stem);
                if (stem.StartsWith("BP_", StringComparison.Ordinal)) logic.Add(rec);
                else cards.Add(rec);
            }
            catch (Exception ex)
            {
                fails.Add((stem, ex.GetType().Name + ": " + ex.Message));
            }
        }

        Console.WriteLine($"卡牌 {cards.Count}，蓝图 {logic.Count}，失败 {fails.Count}");
        foreach (var (n, e) in fails.Take(12)) Console.WriteLine($"  FAIL {n}: {e}");

        JsonWriter.Write(outPath, cards);
        Console.WriteLine($"写出 {outPath}");
        if (logic.Count > 0) { JsonWriter.Write(logicOut, logic); Console.WriteLine($"写出 {logicOut}"); }
        return fails.Count == 0 ? 0 : 1;
    }

    private static string Arg(string[] a, string key)
    {
        var i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static CardRecord Extract(UAsset asset, NormalExport cdo, string stem)
    {
        var r = new CardRecord { asset = stem, id = stem };
        var raw = new Dictionary<string, object>();

        foreach (var p in cdo.Data)
        {
            var name = p.Name.ToString();
            raw[name] = Value(asset, p);
        }

        r.raw = raw;
        r.name = Str(raw, "name");
        r.text = Str(raw, "text");
        r.id   = Str(raw, "id") ?? stem;
        r.type = Str(raw, "type");
        r.faction = Str(raw, "faction");
        r.rarity = Str(raw, "rarity");
        r.cardSet = Str(raw, "cardSet");
        r.spawnCardName = Str(raw, "spawnCardName");
        r.gameplayTags = StrList(raw, "gameplayTags");
        r.usedTriggers = StrList(raw, "usedTriggers") ?? new List<string>();
        r.chooseOneCards = StrList(raw, "chooseOneCards");

        foreach (var (k, v) in raw)
            if (v is bool b) r.flags[k] = b;

        return r;
    }

    private static string Str(Dictionary<string, object> raw, string k)
    {
        string best = null;
        foreach (var kv in raw)
        {
            if (!string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) continue;
            var s = kv.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
            best ??= s;
        }
        return best;
    }

    private static List<string> StrList(Dictionary<string, object> raw, string k)
    {
        List<string> fallback = null;
        foreach (var kv in raw)
        {
            if (!string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) continue;
            var got = AsList(kv.Value);
            if (got is { Count: > 0 }) return got;
            fallback ??= got;
        }
        return fallback;
    }

    private static List<string> AsList(object v)
    {
        // 注意：不能用 `is List<object>`，因为 Value() 在不同分支会产出 List<string> / Object[] /
        // List<object> / FName[] 等多种形状。统一按 IEnumerable 处理，字符串要单独先判。
        if (v is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }
        if (v is System.Collections.IEnumerable en)
            return en.Cast<object>()
                     .Select(x => x?.ToString()?.Trim())
                     .Where(x => !string.IsNullOrEmpty(x)).ToList();
        return null;
    }

    /// <summary>把属性值归一化成 JSON 友好的形状。</summary>
    private static object Value(UAsset asset, PropertyData p)
    {
        switch (p)
        {
            case BoolPropertyData b:    return b.Value;
            case IntPropertyData i:     return i.Value;
            case Int64PropertyData i64: return i64.Value;
            case FloatPropertyData f:   return Math.Round(f.Value, 4);
            case DoublePropertyData d:  return Math.Round(d.Value, 4);
            case StrPropertyData s:     return s.Value?.ToString();
            case NamePropertyData n:    return n.Value.ToString();
            case TextPropertyData t:
                var ci = t.CultureInvariantString?.ToString();
                return string.IsNullOrEmpty(ci) ? t.Value?.ToString() : ci;
            case EnumPropertyData e:
                return StripEnumPrefix(e.Value.ToString());
            case ObjectPropertyData o:
                try { return o.Value.ToImport(asset)?.ObjectName.ToString(); } catch { return null; }
            case SoftObjectPropertyData so:
                return so.Value.AssetPath.AssetName?.ToString();
            case ArrayPropertyData a:
                return a.Value.Select(x => Value(asset, x)).ToList();
            case GameplayTagContainerPropertyData g:
                return g.Value.Select(x => x.ToString()).ToList();
            case StructPropertyData st:
                if (st.Value.Count == 1 && st.Value[0] is GameplayTagContainerPropertyData inner)
                    return inner.Value.Select(x => x.ToString()).ToList();
                if (st.Value.Count == 1 && st.Value[0] is not StructPropertyData)
                    return Value(asset, st.Value[0]);
                return st.Value.ToDictionary(x => x.Name.ToString(), x => Value(asset, x));
            case BytePropertyData by:
                return by.Value.ToString();
            default:
                return p.RawValue?.ToString();
        }
    }

    /// <summary>"ERegisteredCardFunction::OnEndOfTurn" -> "OnEndOfTurn"。</summary>
    private static string StripEnumPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.LastIndexOf("::", StringComparison.Ordinal);
        return i >= 0 ? s[(i + 2)..] : s;
    }
}

public sealed class CardRecord
{
    public string asset;
    public string id;
    public string name;
    public string text;
    public string type;
    public string faction;
    public string rarity;
    public string cardSet;
    public string spawnCardName;
    public List<string> gameplayTags;
    public List<string> chooseOneCards;
    public List<string> usedTriggers = new();
    public Dictionary<string, object> flags = new();
    public Dictionary<string, object> raw = new();
}