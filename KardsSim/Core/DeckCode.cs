using System.Text.Json;

namespace KardsSim.Core;

/// <summary>一个卡组码的解析结果。</summary>
public sealed class ParsedDeck
{
    /// <summary>原始卡组码。</summary>
    public string Code = "";
    /// <summary>主国 / 盟国的国家字符（'1'..'9'、'a'）。</summary>
    public char MainCode, AllyCode;
    public Faction Main, Ally;
    /// <summary>HQ 的 2 字符代码与它对应的卡牌 id（老卡组码没有 HQ 段时用该国默认 HQ）。</summary>
    public string HqCode = "", HqCardId = "";
    /// <summary>HQ 是哪来的：<c>|</c> 段 / 默认值 / 从第 0 组借的。</summary>
    public string HqSource = "";

    /// <summary>展开后的卡牌 id（含重复，顺序 = 解析顺序）。</summary>
    public readonly List<string> Cards = new();
    /// <summary>卡牌 id -> 张数。</summary>
    public readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>码表里根本没有的 2 字符代码。</summary>
    public readonly List<string> UnknownCodes = new();
    /// <summary>码表里有、但本模拟器卡库里没有的卡（导出不全 / 新版本新增）。</summary>
    public readonly List<string> MissingCards = new();
    /// <summary>一个代码被多张卡共用时的选择记录（Anzac 新卡 vs 别的阵营的 _bal 平衡版）。</summary>
    public readonly List<string> Ambiguous = new();
    /// <summary>选了阵营不合法的牌（既不是主国也不是盟国/中立）。</summary>
    public readonly List<string> IllegalFaction = new();
    /// <summary><c>X_bal</c> 不在卡库里、退回基础版 <c>X</c> 的记录。</summary>
    public readonly List<string> Fallbacks = new();
    /// <summary>解析不上时的大小写近邻提示（码表版本不一致的线索）。</summary>
    public readonly List<string> Hints = new();
    /// <summary>其它可疑之处（超过 4 张、组数过多、尾字符落单…）。</summary>
    public readonly List<string> Warnings = new();

    public int Total => Cards.Count;
    public int UniqueCount => Counts.Count;
    /// <summary>39 张（真实构筑卡组的牌数，HQ 另有 1 张、不在卡组码的组里）。</summary>
    public bool Complete => UnknownCodes.Count == 0 && MissingCards.Count == 0;
    /// <summary>张数没超 4 也没打折。</summary>
    public bool Legal => Warnings.Count == 0 && Total == Rules.StandardDeckSize && Complete;
}

/// <summary>
/// KARDS 卡组码解析。
///
/// <para>
/// 格式（见 README「卡组码」一节，出处：私服协议文档 06 · 卡组与卡组码）：
/// </para>
/// <code>
/// %%21|4v3232…sU;;;~;;;|0N1b
/// ││ │└┬┘└┬┘└┬┘└┬┘    └── 第 3 段：HQ（前 2 字符是 HQ 卡的代码，后 2 字符是盟国 HQ）
/// ││ │ │  │  │  └──────── 第 2 段：最多 4 组，用 ; 分开，**组号 i = 每张算 i+1 张**
/// ││ │ └──┴─────────────   （组 0 里的每张牌算 1 张，组 3 里的算 4 张）
/// ││ └──────────────────── 主国代码 + 盟国代码，各 1 位
/// │└────────────────────── 固定前缀
/// └─────────────────────── 固定前缀
/// </code>
///
/// <para>
/// 三个坑：
/// </para>
/// <list type="number">
/// <item>卡牌代码是<b>大小写敏感</b>的：<c>0A</c> 和 <c>0a</c> 是两张不同的卡。</item>
/// <item><c>~</c> 到第 2 段结尾是客户端附加信息（备注/卡背），必须整段丢掉再切组。</item>
/// <item>卡组里有 <b>39</b> 张牌，不是 30 —— 30 是 BP_Logic CDO 里的 <c>NumCardsInDeck</c>，
///       和实测的卡组码、私服发牌（每侧 40 = HQ 1 + 手牌 4/5 + 牌库 35/34）都对不上。</item>
/// </list>
/// </summary>
public static class DeckCode
{
    /// <summary>国家代码表。1..9 是五大主国 + 四个盟国，<c>a</c> 是 OceaniaStorm 的 Anzac。</summary>
    public static readonly Dictionary<char, Faction> Countries = new()
    {
        ['1'] = Faction.Germany,
        ['2'] = Faction.Britain,
        ['3'] = Faction.Japan,
        ['4'] = Faction.Soviet,
        ['5'] = Faction.USA,
        ['6'] = Faction.France,
        ['7'] = Faction.Italy,
        ['8'] = Faction.Poland,
        ['9'] = Faction.Finland,
        ['a'] = Faction.Anzac,
    };

    /// <summary>没有 HQ 段的老卡组码用的默认 HQ（私服文档给的官方默认值）。</summary>
    public static readonly Dictionary<Faction, string> DefaultHq = new()
    {
        [Faction.Germany] = "3v",   // card_location_berlin
        [Faction.Britain] = "0N",   // card_location_london
        [Faction.Japan] = "6l",     // card_location_changchun
        [Faction.Soviet] = "8v",    // card_location_stalingrad
        [Faction.USA] = "ce",       // card_location_cherbourg
    };

    private static Dictionary<string, string[]> _table;
    private static readonly object _lock = new();

    /// <summary>码表（2 字符代码 -> 候选卡牌 id，通常 1 个，17 个代码有 2 个候选）。</summary>
    public static Dictionary<string, string[]> Table
    {
        get
        {
            if (_table != null) return _table;
            lock (_lock)
            {
                if (_table != null) return _table;
                _table = LoadTable();
                return _table;
            }
        }
    }

    /// <summary>显式加载码表（测试与自定义路径用）。</summary>
    public static void LoadTable(string path)
    {
        lock (_lock)
        {
            _table = LoadTableFrom(path);
        }
    }

    private static Dictionary<string, string[]> LoadTable() =>
        LoadTableFrom(DataFiles.Require("deckcodes.json",
            "它可以从私服参考实现的数据表重新生成：pwsh tools/gen-deckcodes.ps1"));

    private static Dictionary<string, string[]> LoadTableFrom(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var codes = doc.RootElement.GetProperty("codes");
        // 必须用 Ordinal（大小写敏感）：0A 与 0a 是两张不同的卡。
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var p in codes.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var v in p.Value.EnumerateArray()) list.Add(v.GetString());
                map[p.Name] = list.ToArray();
            }
            else
            {
                map[p.Name] = new[] { p.Value.GetString() };
            }
        }
        return map;
    }

    public static bool IsDeckCode(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.StartsWith("%%", StringComparison.Ordinal);

    /// <summary>某个字符对应的阵营；不是合法国家字符时返回 null。</summary>
    public static Faction? CountryOf(char c) => Countries.TryGetValue(c, out var f) ? f : null;

    /// <summary>
    /// 解析卡组码。格式错误抛 <see cref="FormatException"/>；
    /// 卡库里缺牌、代码不认这类「数据问题」不抛异常，记在结果的列表里（调用方决定怎么处理）。
    /// </summary>
    public static ParsedDeck Parse(string code)
    {
        if (!IsDeckCode(code))
            throw new FormatException($"卡组码必须以 %% 开头：{code}");
        if (CardDb.All.Count == 0)
            throw new InvalidOperationException("解析卡组码前必须先 CardDb.Load(...)");

        var r = new ParsedDeck { Code = code };
        var parts = code[2..].Split('|');
        if (parts.Length is < 2 or > 3)
            throw new FormatException($"卡组码应该有 2~3 段（用 | 分），实际 {parts.Length} 段：{code}");

        var countries = parts[0];
        if (countries.Length != 2)
            throw new FormatException($"国家代码段必须是 2 个字符，实际 \"{countries}\"：{code}");
        if (CountryOf(countries[0]) is not { } main)
            throw new FormatException($"不认识的主国代码 '{countries[0]}'：{code}");
        if (CountryOf(countries[1]) is not { } ally)
            throw new FormatException($"不认识的盟国代码 '{countries[1]}'：{code}");
        r.MainCode = countries[0];
        r.AllyCode = countries[1];
        r.Main = main;
        r.Ally = ally;
        if (main == ally) r.Warnings.Add($"主国与盟国相同（{main}）");

        // ~ 到段尾是客户端附加信息，先整段丢掉再切组 —— 否则 ~ 后面的空组会被算成 5/6/7 张。
        var body = parts[1];
        var tilde = body.IndexOf('~');
        if (tilde >= 0) body = body[..tilde];

        var groups = body.Split(';');
        if (groups.Length > 4)
            r.Warnings.Add($"有 {groups.Length} 组，超过 4 组（第 1 段里 ; 的个数不对？）");

        // HQ：先定，再解析卡 —— 老写法把 HQ 代码塞在第 0 组开头，
        // 那 2 个字符必须先摘掉，否则会被当成一张牌多算一次。
        if (parts.Length == 3 && parts[2].Length >= 2)
        {
            r.HqCode = parts[2][..2];
            r.HqSource = "段";
        }
        else if (DefaultHq.TryGetValue(main, out var def))
        {
            r.HqCode = def;
            r.HqSource = "默认";
            if (groups.Length > 0 && groups[0].StartsWith(def, StringComparison.Ordinal))
            {
                groups[0] = groups[0][def.Length..];
                r.HqSource = "第 0 组";
            }
        }
        else
        {
            r.Warnings.Add($"主国 {main} 没有默认 HQ，卡组码里也没有 HQ 段");
        }

        for (var g = 0; g < groups.Length; g++)
        {
            var copies = g + 1;
            if (copies > 4)
            {
                r.Warnings.Add($"第 {g} 组按规则要算 {copies} 张（> 4）");
                copies = 4;
            }
            var s = groups[g];
            if (s.Length % 2 != 0)
                r.Warnings.Add($"第 {g} 组长度是奇数（{s.Length}），最后一个字符落单被丢掉");
            for (var j = 0; j + 1 < s.Length; j += 2)
            {
                var codeId = s.Substring(j, 2);
                var cardId = Pick(codeId, main, ally, r);
                if (cardId == null) continue;
                r.Cards.Add(cardId);
                r.Counts[cardId] = r.Counts.GetValueOrDefault(cardId) + 1;
                // 同组内同一张牌出现多次 = 多张（客户端就是这么写 4 张的），组号只决定倍数
                for (var n = 1; n < copies; n++)
                {
                    r.Cards.Add(cardId);
                    r.Counts[cardId] = r.Counts.GetValueOrDefault(cardId) + 1;
                }
            }
        }

        foreach (var kv in r.Counts)
            if (kv.Value > 4) r.Warnings.Add($"同一张卡超过 4 张：{kv.Key} = {kv.Value}");

        FinishHq(r);
        return r;
    }

    /// <summary>把已经定好的 HQ 代码翻成卡牌 id（只作元数据，HQ 不参与对局）。</summary>
    private static void FinishHq(ParsedDeck r)
    {
        if (r.HqCode.Length == 0) return;
        var cands = Table.GetValueOrDefault(r.HqCode);
        if (cands == null)
        {
            r.Warnings.Add($"HQ 代码 {r.HqCode} 不在码表里");
            return;
        }
        r.HqCardId = cands.FirstOrDefault(x => CardDb.Get(x) != null) ?? cands[0];
        if (CardDb.Get(r.HqCardId) == null)
            r.Warnings.Add($"HQ {r.HqCode}={r.HqCardId} 不在本卡库里（HQ 只作元数据，不影响对局）");
    }

    /// <summary>
    /// 把一个 2 字符代码变成卡牌 id。
    ///
    /// <para>
    /// 步骤：卡库里有哪个候选 → 阵营合法的优先 → <c>_bal</c> 退回基础版 → 缺牌记录。
    /// 「阵营合法」很关键：<c>xX</c> 同时是 <c>card_unit_dingo_armored_car</c>(Anzac)
    /// 和 <c>card_unit_lancaster_bal</c>(Britain)，出现在澳系卡组里显然是前者。
    /// </para>
    /// </summary>
    private static string Pick(string codeId, Faction main, Faction ally, ParsedDeck r)
    {
        var cands = Table.GetValueOrDefault(codeId);
        if (cands == null || cands.Length == 0)
        {
            r.UnknownCodes.Add(codeId);
            AddCaseHint(codeId, main, ally, r);
            return null;
        }

        var present = cands.Where(x => CardDb.Get(x) != null).ToList();
        var legal = present.Where(x => IsLegal(x, main, ally)).ToList();
        if (legal.Count > 0)
        {
            if (present.Count > 1) r.Ambiguous.Add($"{codeId}->{legal[0]}（候选 {string.Join('/', present)}）");
            return legal[0];
        }
        if (present.Count > 0)
        {
            r.IllegalFaction.Add($"{codeId}={present[0]}（{CardDb.Get(present[0]).Faction}，既不是 {main} 也不是 {ally}）");
            return present[0];
        }

        // 卡库里一张候选都没有：平衡版 _bal 退回同名基础卡（新版本的 _bal 卡本卡库没有）。
        // 只在基础版阵营也合法时才换 —— 把日本的牌塞进英美卡组比「缺一张牌」更糟。
        foreach (var c in cands)
        {
            if (!c.EndsWith("_bal", StringComparison.Ordinal)) continue;
            var baseId = c[..^4];
            if (CardDb.Get(baseId) == null || !IsLegal(baseId, main, ally)) continue;
            r.Fallbacks.Add($"{codeId}: {c} -> {baseId}（基础版）");
            return baseId;
        }

        r.MissingCards.Add($"{codeId}={string.Join('/', cands)}");
        return null;
    }

    /// <summary>
    /// 代码在码表里查不到时，看看另外 3 种大小写写法里有没有「卡库里有、阵营也合法」的卡。
    ///
    /// <para>
    /// 卡牌代码大小写敏感，所以这不能当自动纠错用（那是猜卡组）。
    /// 它只用来指出「码表版本对不上」：<c>%%</c> 里的代码在新版客户端被改名/重新分配过。
    /// 「码表有、只是卡库缺这张牌」不算这种情况，那种缺牌另有报告，别拿大小写近邻来误导。
    /// </para>
    /// </summary>
    private static void AddCaseHint(string codeId, Faction main, Faction ally, ParsedDeck r)
    {
        if (codeId.Length != 2) return;
        foreach (var variant in CaseVariants(codeId))
        {
            if (variant == codeId) continue;
            var cands = Table.GetValueOrDefault(variant);
            if (cands == null) continue;
            foreach (var c in cands)
            {
                if (CardDb.Get(c) == null || !IsLegal(c, main, ally)) continue;
                r.Hints.Add($"{codeId} 不在码表里，但大小写近邻 {variant}={c} 在卡库且阵营合法（码表与客户端版本可能不一致）");
                return;
            }
        }
    }

    private static IEnumerable<string> CaseVariants(string s)
    {
        for (var mask = 0; mask < 4; mask++)
        {
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if ((mask & (1 << i)) != 0)
                    chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            yield return new string(chars);
        }
    }

    private static bool IsLegal(string cardId, Faction main, Faction ally)
    {
        var f = CardDb.Get(cardId).Faction;
        return f == main || f == ally || f == Faction.Neutral;
    }
}
