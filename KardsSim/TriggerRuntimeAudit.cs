using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 触发点「注册了但运行期从没真跑过」审计。
///
/// <para>
/// 静态覆盖（<see cref="TriggerAudit"/>）只证明引擎<b>发了</b>这个触发点，
/// 不证明<b>有卡接住</b>。触发源是「场上所有单位」，而一张卡只在
/// <c>CardFunctionTriggers</c> 登记过的触发点上才被调 —— 引擎发了但当时场上
/// 没有响应者，就是空跑。
/// </para>
///
/// <para>
/// 所以要在 <see cref="EngineHost"/> 里实测：每个触发点实际命中了多少次、
/// 命中了哪些卡。命中数为 0 的触发点，对应的卡在模拟里仍然是白板。
/// </para>
/// </summary>
public static class TriggerRuntimeAudit
{
    public static int Run(string[] args)
    {
        var games = 400;
        var hit = new Dictionary<Trigger, int>();
        var cards = new Dictionary<Trigger, HashSet<string>>();
        var everRegistered = new Dictionary<Trigger, HashSet<string>>();

        for (var i = 0; i < games; i++)
        {
            var g = new Engine.GameEngine(1000 + i, null, null, false);
            var h = new EngineHost(g) { TraceFire = true };
            h.OnTriggerFired = (t, asset) =>
            {
                hit[t] = hit.TryGetValue(t, out var n) ? n + 1 : 1;
                if (!cards.TryGetValue(t, out var s)) cards[t] = s = new HashSet<string>();
                if (asset is not null) s.Add(asset);
            };
            g.Host = h;

            var n = 0;
            while (!g.IsDone && n < 2000)
            {
                var legal = g.LegalActions();
                if (legal.Count == 0) break;
                g.Apply(legal[g.S.Rng.Next(legal.Count)]);
                n++;
            }
        }

        // 注册面（静态）
        var byTrigger = new Dictionary<Trigger, List<string>>();
        foreach (var kv in Generated.FnIndex.Assets)
        {
            if (!kv.Key.StartsWith("card_", StringComparison.Ordinal)) continue;
            foreach (var fn in kv.Value.Keys)
            {
                if (!Enum.TryParse<Trigger>(fn, out var t) || t == Trigger.NotAvailable) continue;
                if (!byTrigger.TryGetValue(t, out var l)) byTrigger[t] = l = new List<string>();
                l.Add(kv.Key);
            }
        }

        Console.WriteLine($"=== 触发点运行期命中（{games} 局）===");
        Console.WriteLine();
        Console.WriteLine($"{"触发点",-44} {"注册",5} {"命中",8} {"命中卡数",8}");
        Console.WriteLine(new string('-', 70));

        var never = new List<Trigger>();
        foreach (var t in byTrigger.Keys.OrderByDescending(t => hit.TryGetValue(t, out var v) ? v : 0))
        {
            var reg = byTrigger[t].Count;
            var h = hit.TryGetValue(t, out var n) ? n : 0;
            var nc = cards.TryGetValue(t, out var s) ? s.Count : 0;
            var mark = n == 0 ? "  <== 空跑" : "";
            Console.WriteLine($"{t,-44} {reg,5} {h,8} {nc,8}{mark}");
            if (n == 0) never.Add(t);
        }

        Console.WriteLine();
        Console.WriteLine($"注册了但运行期一次没命中的触发点: {never.Count} 个");
        foreach (var t in never)
            Console.WriteLine($"  {t,-42} {byTrigger[t].Count,4} 张   e.g. {string.Join(", ", byTrigger[t].Take(3))}");

        return 0;
    }
}
