using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Engine;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 端到端复现 issue #1 的真实场景，而不是只查「函数能不能跑」。
///
/// <para>
/// 场景来自真实回放 <c>310284</c> 第 10 回合：
/// 右方 <c>7. SCHÜTZEN</c> 攻击并摧毁左方 <c>M16 HALFTRACK</c>。
/// 该卡文案是「攻击并摧毁单位时变为老兵」，变老兵后
/// <c>OnBecomingVeteran</c> 对敌方 HQ 造成 3 点伤害。
/// </para>
///
/// <para>
/// 三条缺陷叠在这条链上时，KardsSim 的表现是「HQ 一点没掉」——
/// 既不报错也不进 Unhandled，所以只有断言 HQ 数值才能发现。
/// </para>
/// </summary>
public static class ReproIssue1
{
    private static int _pass, _fail;

    public static int Run(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("=== issue #1 复现：7. SCHÜTZEN 击杀 → 变老兵 → 打 HQ 3 ===\n");

        ScenarioA();
        ScenarioB();

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 主场景：走完整的引擎战斗路径（DoAttack → DestroyCard → 触发链）。
    ///
    /// <para>
    /// 关键断言是「敌方 HQ 掉了 3」——这是整条链唯一的外部可观测结果。
    /// </para>
    /// </summary>
    private static void ScenarioA()
    {
        var g = new Engine.GameEngine(310284, null, null, false);
        var h = new EngineHost(g);
        g.Host = h;

        var schutzenDef = CardDb.Get("card_unit_7_schutzen");
        var halftrackDef = CardDb.Get("card_unit_m16_halftrack");
        if (schutzenDef is null || halftrackDef is null)
        {
            Check("卡库里能找到两张卡", false, "7_schutzen 或 m16_halftrack 缺失");
            return;
        }

        // 右方出 7. SCHÜTZEN（攻击方，4/5），左方有 HALFTRACK（2/2）当靶子
        // 两边都在前线：必须同时设 Loc.Frontline 和 OnFrontline。
        // 只加到 S.Frontline 列表、不改 Loc 的话，客户端规则库会把它们
        // 当成还在支援线（location=5/6），于是以 not_enough_range 否决攻击 ——
        // 这正是引擎 Loc 与客户端 ECardLocationEnum 两套位置语义的接缝。
        var atk = g.S.NewCard(schutzenDef, Side.Right);
        atk.Loc = Loc.Frontline;
        atk.OnFrontline = true;
        atk.EnterPlayTurn = 0;   // 更早的回合进场，本回合不受召唤失调限制
        g.S.Frontline.Add(atk);
        g.S.FrontlineOwner = Side.Right;

        var def = g.S.NewCard(halftrackDef, Side.Left);
        def.Loc = Loc.Frontline;
        def.OnFrontline = true;
        def.EnterPlayTurn = 0;
        g.S.Frontline.Add(def);

        // 让右方成为行动方，并给足行动点
        // （客户端 CanAttack 会查 not_enough_kredits，没给钱会被否掉 ——
        //  真实回放里这是第 10 回合，行动点早就够了）
        g.S.Current = Side.Right;
        g.S.Player(Side.Right).Kredits = 12;

        var hqBefore = g.S.Player(Side.Left).Hq;
        Check($"前置：7_schutzen 尚未是老兵", !atk.Veteran, $"Veteran={atk.Veteran}");
        Check($"前置：HALFTRACK 在场上", !def.Destroyed, "已被摧毁");

        // 直接走引擎的攻击路径（等价于 A35 AC）
        h.Unhandled.Clear();
        // 记录真实派发到卡上的每个触发点 —— 区分「效果没跑」和
        // 「触发了但效果体内部早退」。后者不报错、不进 Unhandled，只有这里看得见。
        h.TraceFire = true;
        h.OnTriggerFired = (t, id) => Console.WriteLine($"    [触发] {id} <- {t}");

        var targets = g.AttackTargetsFor(atk);
        var why = g.ClientCanAttack(atk, def, out var reason) ? "(放行)" : $"被否: '{reason}'";
        Check($"7_schutzen 能打到 HALFTRACK",
            targets.Contains(def.InstanceId),
            $"可选目标 [{string.Join(",", targets)}]，HALFTRACK={def.InstanceId}，{why}");

        var traceReal = new List<string>();
        h.CallTrace = n => { if (traceReal.Count < 600) traceReal.Add(n); };
        g.Apply(new GameAction
        {
            Type = ActionType.Attack,
            SourceId = atk.InstanceId,
            TargetId = def.InstanceId,
        });
        h.CallTrace = null;
        // 只看「变老兵之后」该发生的事有没有被调用到
        var mvAt = traceReal.IndexOf("MakeVeteran");
        var interesting = new HashSet<string> { "MakeVeteran", "OnBecomingVeteran", "GetOppositeSide",
            "GetLocationCardBySide", "DamageCard", "DamageHQ", "MakeCardVeteran",
            "FetchAllCardsWithEventTrigger", "OnOtherCardBecomingVeteran", "ExecuteOnOtherCardsAbilitiesChanged" };
        Console.WriteLine("    [关键链] " + string.Join(" > ",
            traceReal.Skip(mvAt < 0 ? 0 : mvAt).Where(interesting.Contains)));

        Check("HALFTRACK 被摧毁", def.Destroyed, $"Destroyed={def.Destroyed}");

        // 核心断言：两处缺陷都修好才会是 true
        Check("7_schutzen 变成老兵（(b)+(c) 都修好才成立）",
            atk.Veteran,
            $"Veteran={atk.Veteran}  attack={atk.TotalAttack} defense={atk.TotalDefense}");

        var hqAfter = g.S.Player(Side.Left).Hq;
        Check("敌方 HQ 掉了 3 点（OnBecomingVeteran 的效果真的跑了）",
            hqBefore - hqAfter == 3,
            $"HQ {hqBefore} -> {hqAfter}，期望差 3");

        // 规则库取不到某条子规则时会静默变成空操作，所以把未实现的调用打出来。
        if (h.Unhandled.Count > 0)
        {
            Console.WriteLine("    [未实现调用]");
            foreach (var kv in h.Unhandled.OrderByDescending(x => x.Value).Take(12))
                Console.WriteLine($"      {kv.Value,4}x  {kv.Key}");
        }

    }

    /// <summary>
    /// 对照场景：非战斗击杀（直接 DestroyCard）不该让 7_schutzen 变老兵，
    /// 因为文案限定「attacks and destroys」。
    ///
    /// <para>
    /// 这一条防的是「修过头」——如果我把 destroyedInCombat 恒置 true，
    /// 主场景照样过，但这条会失败。
    /// </para>
    /// </summary>
    private static void ScenarioB()
    {
        var g = new Engine.GameEngine(310285, null, null, false);
        var h = new EngineHost(g);
        g.Host = h;

        var schutzenDef = CardDb.Get("card_unit_7_schutzen");
        if (schutzenDef is null) { Check("场景B：卡库有 7_schutzen", false, "缺失"); return; }

        var atk = g.S.NewCard(schutzenDef, Side.Right);
        atk.Loc = Loc.Board;
        g.S.Player(Side.Right).Board.Add(atk);

        var victimDef = CardDb.All.FirstOrDefault(d => d.IsUnit && d.Id != schutzenDef.Id);
        var victim = g.S.NewCard(victimDef, Side.Left);
        victim.Loc = Loc.Board;
        g.S.Player(Side.Left).Board.Add(victim);

        var hqBefore = g.S.Player(Side.Left).Hq;

        // 非战斗摧毁：没有 killer、不是战斗
        g.DestroyCard(victim);

        Check("场景B：非战斗击杀不触发变老兵", !atk.Veteran,
            $"Veteran={atk.Veteran}（不该变）");
        Check("场景B：敌方 HQ 未掉血", g.S.Player(Side.Left).Hq == hqBefore,
            $"HQ {hqBefore} -> {g.S.Player(Side.Left).Hq}");
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}   {detail}"); }
    }
}
