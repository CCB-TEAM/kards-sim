using KardsSim.Core;
using KardsSim.Engine;

namespace KardsSim;

/// <summary>
/// <c>--mode decks</c>：卡组码审计。
///
/// <para>
/// 把 decks.json 里的推荐卡组（或命令行给的任意卡组码）逐个解析一遍，报告：
/// 主国/盟国、HQ、张数、唯一张数、一码多卡时的选择、卡库缺了哪些牌、有没有超过 4 张。
/// 训练神经网络之前先用它确认「哪些卡组这套模拟器真能打完整」——
/// 缺牌是卡牌导出不全导致的，不是引擎的 bug。
/// </para>
///
/// <code>
/// --mode decks                        审计 decks.json 里的全部预设
/// --mode decks --deck jp-pol-boom     只审一个预设（短名 / 中文名 / #序号）
/// --mode decks --code "%%38|..."      审任意卡组码（可重复）
/// --mode decks --build                额外建一局，确认引擎按 39 张发牌
/// --mode decks --strict               有卡组不完整就返回 1（CI 用）
/// </code>
/// </summary>
public static class DeckAudit
{
    public static int Run(string[] args)
    {
        var codes = ArgsOf(args, "--code").ToList();
        var decks = ArgsOf(args, "--deck").ToList();
        var build = args.Contains("--build");
        var strict = args.Contains("--strict");

        var items = new List<(string Label, ParsedDeck Deck)>();
        var failed = 0;

        if (codes.Count == 0 && decks.Count == 0)
        {
            try
            {
                Console.WriteLine($"卡组码审计：{DeckPresets.All.Count} 套预设（decks.json）");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"读不了预设卡组：{ex.Message}");
                return 2;
            }
            Console.WriteLine();
            for (var i = 0; i < DeckPresets.All.Count; i++)
            {
                var p = DeckPresets.All[i];
                items.Add(($"[{i + 1,2}] {p.Key}", TryParse(p.Code, ref failed)));
            }
        }
        else
        {
            foreach (var d in decks)
            {
                var preset = DeckPresets.Find(d);
                if (preset == null)
                {
                    Console.Error.WriteLine($"找不到预设: {d}");
                    failed++;
                    continue;
                }
                items.Add((preset.Key, TryParse(preset.Code, ref failed)));
            }
            foreach (var c in codes)
                items.Add(("--code", TryParse(c, ref failed)));
        }

        var incomplete = 0;
        var missingUnion = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, deck) in items)
        {
            if (deck == null) continue;
            Print(label, deck);
            if (!deck.Complete) incomplete++;
            foreach (var m in deck.MissingCards)
            {
                // 形如 "E1=card_unit_panther_a_zimm（A/B）"
                var name = m.Split('=')[1].Split('（')[0].Split('/')[0];
                missingUnion[name] = missingUnion.GetValueOrDefault(name) + 1;
            }
            if (build) Build(deck);
        }

        Console.WriteLine();
        var audited = items.Count(x => x.Deck != null);
        Console.WriteLine($"汇总：审计 {audited} 套；完整 {audited - incomplete} 套，缺牌 {incomplete} 套");

        if (missingUnion.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"本卡库缺的牌（并集 {missingUnion.Count} 种）。补法：把客户端完整的");
            Console.WriteLine("Content/Blueprints/Cards 重新导出到 _input/Cards，再跑 KardsDataExtract + KardsTranspiler。");
            foreach (var kv in missingUnion)
                Console.WriteLine($"  {kv.Value,3} 套用到  {kv.Key}");
        }

        if (failed > 0)
        {
            Console.Error.WriteLine($"有 {failed} 个卡组码解析失败");
            return 1;
        }
        return strict && incomplete > 0 ? 1 : 0;
    }

    private static ParsedDeck TryParse(string code, ref int failed)
    {
        try
        {
            return DeckCode.Parse(code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ✗ 解析失败: {code}");
            Console.Error.WriteLine($"    {ex.Message}");
            failed++;
            return null;
        }
    }

    private static void Print(string label, ParsedDeck d)
    {
        var hq = d.HqCardId.Length > 0 ? $"{d.HqCode}={d.HqCardId}" : "无";
        Console.WriteLine($"{label,-22} {d.Main}+{d.Ally}  HQ {hq}（{d.HqSource}）");
        Console.WriteLine($"    {d.Total,2}/{Rules.StandardDeckSize} 张，唯一 {d.UniqueCount,2} 张" +
                          (d.Complete && d.Legal ? "  ✅ 完整" : ""));
        if (d.Ambiguous.Count > 0) Console.WriteLine($"    一码多卡：{string.Join("；", d.Ambiguous)}");
        if (d.Fallbacks.Count > 0) Console.WriteLine($"    _bal 回退：{string.Join("；", d.Fallbacks)}");
        if (d.IllegalFaction.Count > 0) Console.WriteLine($"    阵营不合法：{string.Join("；", d.IllegalFaction)}");
        if (d.UnknownCodes.Count > 0) Console.WriteLine($"    码表不认：{string.Join(", ", d.UnknownCodes)}");
        if (d.MissingCards.Count > 0) Console.WriteLine($"    卡库缺牌：{string.Join("，", d.MissingCards)}");
        if (d.Hints.Count > 0) Console.WriteLine($"    提示：{string.Join("；", d.Hints)}");
        if (d.Warnings.Count > 0) Console.WriteLine($"    疑问：{string.Join("；", d.Warnings)}");
    }

    private static void Build(ParsedDeck d)
    {
        // 只建左方，右方随机：确认引擎真的按解析结果发牌（39 张 -> 手牌 4 + 牌库 35）。
        var g = new GameEngine(20260215, d.Code, null, false);
        Console.WriteLine($"    引擎发牌：牌库 {g.S.Left.Deck.Count} 张、手牌 {g.S.Left.Hand.Count} 张");
    }

    private static IEnumerable<string> ArgsOf(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == key && args[i + 1].Length > 0)
                yield return args[i + 1];
    }
}
