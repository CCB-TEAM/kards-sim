using System.Text;
using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 逐卡逐触发点的强制演练（smoke）。
///
/// <para>
/// 自对弈是「随机打牌」，很多卡在有限局数里根本摸不到 —— 于是「运行期 0 命中」
/// 分不清是「分发坏了」还是「这局没抽到」。这个模式绕开对局流程：
/// 把每张卡直接造出来、放到场上、然后<b>逐个强制触发它注册的每一个触发点</b>。
/// </para>
///
/// <para>
/// 目的不是验证数值对不对（那要靠单卡测试），而是把「有没有炸 / 有没有未实现调用」
/// 这两件事在所有卡上过一次。跑得通就说明直译产物和宿主接口是自洽的。
/// </para>
/// </summary>
public static class SmokeAll
{
    public static int Run(string[] args)
    {
        var limit = int.Parse(args.FirstOrDefault(a => a.StartsWith("--limit="))?[8..] ?? "0");
        var verbose = args.Contains("--verbose");

        var host = new EngineHost(new Engine.GameEngine(12345, null, null, false))
        {
            Verbose = false,
            Logger = _ => { },
        };
        var engine = host.Engine;

        // 场上放两个对立的单位，好让「对某个目标生效」的效果有作用对象
        var left = MakeUnit(engine, Side.Left, "card_unit_panzer_iii_e");
        var right = MakeUnit(engine, Side.Right, "card_unit_t_34_cam1");
        engine.S.Player(Side.Left).Board.Add(left);
        engine.S.Player(Side.Right).Board.Add(right);
        left.Loc = Loc.Board;
        right.Loc = Loc.Board;

        var cards = Generated.FnIndex.Assets.Keys
            .Where(k => k.StartsWith("card_", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (limit > 0) cards = cards.Take(limit).ToList();

        var ran = 0; var fired = 0; var failed = 0;
        var failures = new List<string>();
        var missTotal = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var asset in cards)
        {
            var def = CardDb.Get(asset) ?? CardDb.All.FirstOrDefault(c => c.Id == asset);
            if (def is null) continue;
            ran++;

            var self = engine.S.NewCard(def, Side.Left);
            self.Loc = Loc.Board;
            engine.S.Player(Side.Left).Board.Add(self);

            foreach (var t in TriggersOf(asset))
            {
                // 每个触发点单独在 try 里跑：一张卡炸了不能带走整轮
                try
                {
                    var before = host.CallCount;
                    CardDispatch.Fire(host, self, t, right);
                    if (host.CallCount > before) fired++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failures.Count < 40)
                        failures.Add($"{asset}.{t}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            engine.S.Player(Side.Left).Board.Remove(self);
            if (self.OnFrontline) engine.S.Frontline.Remove(self);
        }

        foreach (var kv in host.Unhandled)
            missTotal[kv.Key] = kv.Value;

        Console.WriteLine($"=== 全卡强制演练 ===");
        Console.WriteLine($"卡牌: {ran} 张");
        Console.WriteLine($"触发点调用: {fired} 次真的产生了宿主调用");
        Console.WriteLine($"异常: {failed} 次");
        Console.WriteLine($"宿主调用总数: {host.CallCount}");
        Console.WriteLine($"未实现函数种类: {missTotal.Count}");
        Console.WriteLine();

        if (failures.Count > 0)
        {
            Console.WriteLine("--- 异常明细 ---");
            foreach (var f in failures) Console.WriteLine($"  {f}");
            Console.WriteLine();
        }

        if (missTotal.Count > 0)
        {
            Console.WriteLine("--- 未实现函数（按次数）---");
            foreach (var kv in missTotal.OrderByDescending(x => x.Value).Take(40))
                Console.WriteLine($"  {kv.Value,7}  {kv.Key}");
        }
        else Console.WriteLine("未实现函数: 无");

        // 未实现调用是静默的：宿主返回 Nothing，调用方当 0/false 继续跑，
        // 结果看着合理但是错的。所以这里当失败处理，不能只打印。
        return failed == 0 && missTotal.Count == 0 ? 0 : 1;
    }

    private static Card MakeUnit(Engine.GameEngine e, Side s, string id)
    {
        var def = CardDb.Get(id) ?? CardDb.All.FirstOrDefault(c => c.Type is CardType.Infantry or CardType.Tank);
        var c = e.S.NewCard(def, s);
        c.Loc = Loc.Board;
        return c;
    }

    private static IEnumerable<Trigger> TriggersOf(string asset)
    {
        if (!Generated.FnIndex.Assets.TryGetValue(asset, out var m)) yield break;
        foreach (var fn in m.Keys)
            if (Enum.TryParse<Trigger>(fn, out var t) && t != Trigger.NotAvailable)
                yield return t;
    }
}
