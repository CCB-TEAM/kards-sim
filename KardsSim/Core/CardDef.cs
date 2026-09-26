using System.Text.Json;

namespace KardsSim.Core;

/// <summary>卡牌的静态定义，直接从客户端 CDO 提取，对局中不变。</summary>
public sealed class CardDef
{
    public string Id;
    public string Asset;
    public string Name;
    public string Text;
    public CardType Type;
    public Faction Faction;
    public Rarity Rarity;
    public string CardSet;
    public string FlavorText;

    public int Kredits;
    public int Attack;
    public int Defense;
    public int Range = 1;
    public int OperationCost;
    public int HeavyArmor;

    /// <summary>
    /// Intel（情报）值：打出本卡时随机翻开对手手牌的张数。
    /// 客户端字段名是 <c>cipher</c>，沿用同一个名字语义才对得上直译产物。
    /// </summary>
    public int Cipher;

    public Kw Keywords;
    public List<Trigger> Triggers = new();
    public List<string> Tags = new();
    public List<string> SpawnCardNames = new();
    public List<string> ChooseOneCards = new();

    /// <summary>CDO 原始属性，未归一化。转译效果代码时要用到。</summary>
    public Dictionary<string, JsonElement> Raw = new();

    public bool Has(Kw k) => (Keywords & k) != 0;

    /// <summary>
    /// 老兵版本的卡 id（没有则 null）。
    ///
    /// <para>
    /// 判定依据是 <c>spawnCardName</c> 里以 <c>_vet</c> 结尾、且卡库里真实存在的项。
    /// 注意这个字段是<b>分号分隔的多值</b>（还会列天气卡之类），不是老兵专用 ——
    /// 直接当成单个老兵名会错（236 张有值，只有 41 张是真老兵升级）。
    /// </para>
    ///
    /// <para>
    /// 这个属性是 <c>getHasVeteranUpgrade</c> 的正确语义：<b>「能否升级成老兵」</b>，
    /// 而不是「已经是老兵」。混淆两者会让 <c>MakeVeteran</c> 的守卫
    /// <c>!IsVeteran &amp;&amp; getHasVeteranUpgrade</c> 恒为假，老兵机制整条失效。
    /// </para>
    /// </summary>
    public string VeteranUpgradeId { get; private set; }

    /// <summary>是否有可用的老兵升级。</summary>
    public bool HasVeteranUpgrade => VeteranUpgradeId is not null;

    /// <summary>在卡库加载完成后解析老兵版本（需要全局 id 表，所以不能在解析单卡时做）。</summary>
    public void ResolveVeteranUpgrade()
    {
        VeteranUpgradeId = null;
        foreach (var n in SpawnCardNames)
        {
            // 排除自指：有 4 张 _vet 卡的 spawnCardName 写的是它自己
            // （card_unit_hurricane_mk_ii_c_trop_vet 等），
            // 不排除的话它们会被当成「还能再升一次」，全池计数从 41 变 45。
            if (string.Equals(n, Id, StringComparison.Ordinal)) continue;
            if (n.EndsWith("_vet", StringComparison.Ordinal) && CardDb.Get(n) is not null)
            {
                VeteranUpgradeId = n;
                return;
            }
        }
    }

    /// <summary>单位才能攻击/被攻击；location 与 order 不能。</summary>
    public bool IsUnit => Type is CardType.Infantry or CardType.Tank or CardType.Fighter
                              or CardType.Bomber or CardType.Artillery or CardType.Gotcha;

    public bool IsOrder => Type == CardType.Order;
    public bool IsLocation => Type == CardType.Location;
    public bool IsOnBoard => IsUnit || IsLocation;
}

public static class CardDb
{
    public static readonly Dictionary<string, CardDef> ById = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<CardDef> All = new();
    /// <summary>UI 显示名（如 "HAMPSHIRE REGIMENT"）→ 定义，用于解析 spawnCardName / 卡面文案。</summary>
    public static readonly Dictionary<string, CardDef> ByTitle = new(StringComparer.OrdinalIgnoreCase);

    public static CardDef Get(string id) => id != null && ById.TryGetValue(id, out var d) ? d : null;

    /// <summary>按 id、asset 名或 title 查找，容忍卡面文案里的大写写法。</summary>
    public static CardDef Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim();
        if (ById.TryGetValue(t, out var d)) return d;
        if (ByTitle.TryGetValue(t, out d)) return d;
        var norm = Norm(t);
        if (ByTitle.TryGetValue(norm, out d)) return d;
        foreach (var c in All)
            if (Norm(c.Name) == norm || Norm(c.Id) == norm) return c;
        return null;
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    public static void Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var d = Parse(e);
            All.Add(d);
            ById[d.Id] = d;
            if (!string.IsNullOrWhiteSpace(d.Name)) ByTitle[d.Name] = d;
        }
        // 老兵版本要在全表建好之后才能解析（要查目标 id 是否真实存在）
        foreach (var d in All) d.ResolveVeteranUpgrade();
    }

    private static CardDef Parse(JsonElement e)
    {
        var d = new CardDef
        {
            Asset = S(e, "asset"),
            Id = S(e, "id"),
            Name = S(e, "name"),
            Text = S(e, "text"),
            CardSet = S(e, "cardSet"),
            Type = ParseType(S(e, "type")),
            Faction = ParseEnum<Faction>(S(e, "faction")),
            Rarity = ParseEnum<Rarity>(S(e, "rarity")),
        };
        if (string.IsNullOrEmpty(d.Id)) d.Id = d.Asset;

        if (e.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in raw.EnumerateObject()) d.Raw[p.Name] = p.Value.Clone();
            d.Kredits = I(raw, "kredits");
            d.Attack = I(raw, "attack");
            d.Defense = I(raw, "defense");
            d.Range = raw.TryGetProperty("range", out var rg) ? Math.Max(0, rg.GetInt32()) : 1;
            d.OperationCost = I(raw, "operationCost");
            d.HeavyArmor = I(raw, "heavyArmor");
            d.Cipher = I(raw, "cipher");
            d.FlavorText = Str(raw, "flavorText");
        }

        if (e.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Object)
            d.Keywords = ParseKeywords(fl);

        if (e.TryGetProperty("usedTriggers", out var tg) && tg.ValueKind == JsonValueKind.Array)
            foreach (var t in tg.EnumerateArray())
            {
                var n = t.GetString();
                if (!string.IsNullOrEmpty(n) && Enum.TryParse<Trigger>(n, true, out var tv)) d.Triggers.Add(tv);
            }

        if (e.TryGetProperty("gameplayTags", out var gt) && gt.ValueKind == JsonValueKind.Array)
            foreach (var t in gt.EnumerateArray())
            {
                var n = t.GetString();
                if (n != null) d.Tags.Add(n);
            }

        // extraHelpBubbleTypes 是「卡面标签」，Intel 这类关键字只在这里出现
        // （gameplayTags 绝大多数卡是 null）。漏掉它 HasIntel 就永远是 false。
        if (raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("extraHelpBubbleTypes", out var hb)
            && hb.ValueKind == JsonValueKind.Array)
            foreach (var t in hb.EnumerateArray())
            {
                var n = t.GetString();
                if (n != null && !d.Tags.Contains(n)) d.Tags.Add(n);
            }

        var spawn = S(e, "spawnCardName");
        if (!string.IsNullOrEmpty(spawn))
            d.SpawnCardNames.AddRange(spawn.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(x => x.Trim()).Where(x => x.Length > 0));

        if (e.TryGetProperty("chooseOneCards", out var co) && co.ValueKind == JsonValueKind.Array)
            foreach (var t in co.EnumerateArray())
            {
                var n = t.GetString();
                if (!string.IsNullOrEmpty(n)) d.ChooseOneCards.Add(n.Trim());
            }

        return d;
    }

    private static Kw ParseKeywords(JsonElement flags)
    {
        var k = Kw.None;
        foreach (var p in flags.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.True) continue;
            var n = p.Name;
            if (n.Length > 3 && n.StartsWith("has", StringComparison.OrdinalIgnoreCase)) n = n[3..];
            k |= n.ToLowerInvariant() switch
            {
                "guard" => Kw.Guard,
                "blitz" => Kw.Blitz,
                "smokescreen" => Kw.Smokescreen,
                "ambush" => Kw.Ambush,
                "mobilize" => Kw.Mobilize,
                "heavyarmor" or "armor" => Kw.HeavyArmor,
                "deployment" => Kw.Deployment,
                "veteran" => Kw.Veteran,
                "fury" => Kw.Fury,
                "covert" => Kw.Covert,
                "bond" => Kw.Bond,
                "develop" => Kw.Develop,
                "forecast" => Kw.Forecast,
                "scrying" => Kw.Scrying,
                "salvage" => Kw.Salvage,
                _ => Kw.None,
            };
        }
        return k;
    }

    private static CardType ParseType(string s) => (s ?? "").ToLowerInvariant() switch
    {
        "infantry" => CardType.Infantry,
        "tank" => CardType.Tank,
        "fighter" => CardType.Fighter,
        "bomber" => CardType.Bomber,
        "artillery" => CardType.Artillery,
        "location" => CardType.Location,
        "gotcha" => CardType.Gotcha,
        _ => CardType.Order,
    };

    private static T ParseEnum<T>(string s) where T : struct
        => Enum.TryParse<T>(s, true, out var v) ? v : default;

    private static string S(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int I(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}