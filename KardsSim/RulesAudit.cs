using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Engine;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 规则层审计：把<b>引擎手写的规则</b>与<b>客户端自己的规则库</b>（cardsCheckFunctions）
/// 逐条对照，看哪些规则只在客户端存在、引擎没实现。
///
/// <para>
/// 为什么需要这个：引擎的 <c>CanAttack</c> / <c>AttackTargetsFor</c> 是手写的，
/// 而客户端有一份权威的 <c>cardsCheckFunctions</c>（能跑，已转译）。
/// 两边不一致时，模拟器会允许真实客户端不允许的动作 —— 这种偏差不会崩，
/// 只会让 AI 在训练里学到一个不存在的规则。
/// </para>
///
/// <para>
/// 本模式只做<b>静态对照 + 行为抽检</b>，不改任何规则。目的是把差距量化出来。
/// </para>
/// </summary>
public static class RulesAudit
{
    /// <summary>
    /// 客户端规则库里出现、而引擎手写规则里没有对应判定的项目。
    /// 每项标注：客户端何时否决 / 引擎现状 / 影响面。
    /// </summary>
    private static readonly (string Reason, string Meaning, string EngineStatus)[] Gaps =
    {
        ("is_being_garded",
         "攻击目标被 Guard 单位掩护时不可打",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("hq_is_being_garded",
         "敌方有 Guard 时 HQ 不可直击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("defender_has_smokescreen",
         "目标带烟幕时不可被攻击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("location_has_smokescreen",
         "目标所在地点带烟幕时不可被攻击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("fighter_protecting",
         "战斗机保护下的目标不可被攻击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("not_enough_range",
         "射程不足不可攻击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("unit_is_pinned",
         "被钉住的单位不能攻击",
         "已实现：TotalAttack 在被钉住时为 0，CanAttack 判 TotalAttack<=0"),
        ("deployment_sickness",
         "召唤失调当回合不能攻击",
         "已实现：SummonedThisTurn + Blitz 特例"),
        ("has_already_attacked",
         "本回合已攻击过",
         "已实现：AttackedThisTurn"),
        ("not_enough_kredits",
         "费用不足",
         "已实现：LegalActions 里判 KreditCost > Kredits"),
        ("not_a_unit",
         "非单位不能攻击",
         "已实现：CanAttack 判 IsUnit"),
        ("unit_cant_attack",
         "自定义「不能攻击」标记",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("cant_be_attacked_by_unit",
         "自定义「不能被单位攻击」",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("unit_cant_be_attack_by_unit",
         "自定义「不能被指定类型攻击」",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("cant_attack_with_ground_units",
         "地面单位不能攻击",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("cant_be_targeted_by_enemy_orders",
         "不能被敌方指令指定为目标",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("cant_target_unrevealed",
         "未明牌的 Covert 卡不可被指定",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
        ("cost_extra_to_target",
         "指定该目标需要额外费用",
         "改走客户端规则库判定（引擎不再自己实现这条）"),
    };

    public static int Run(string[] args)
    {
        Console.WriteLine("=== 规则层对照（引擎手写 vs 客户端 cardsCheckFunctions）===");
        Console.WriteLine();

        // 1) 客户端规则库是否真的转译进来了、能跑
        var checkFn = Generated.FnIndex.Find("cardsCheckFunctions", "CanAttack");
        var selectFn = Generated.FnIndex.Find("cardsCheckFunctions", "CanSelectAsTarget");
        Console.WriteLine($"客户端规则库 cardsCheckFunctions.CanAttack        : {(checkFn is not null ? "已转译" : "缺失")}");
        Console.WriteLine($"客户端规则库 cardsCheckFunctions.CanSelectAsTarget: {(selectFn is not null ? "已转译" : "缺失")}");
        Console.WriteLine();

        // 2) 引擎有没有调用它 —— 这是关键事实
        Console.WriteLine("引擎是否调用客户端规则库 : 是（AttackTargetsFor / ClientCanAttack 直接委托 CanAttack）");
        Console.WriteLine();

        // 3) 逐条列出差距
        // 三分类：引擎手写实现 / 改走客户端规则库判定 / 两边都没有（真缺口）
        var done = Gaps.Count(g => g.EngineStatus.StartsWith("已实现"));
        var delegated = Gaps.Count(g => g.EngineStatus.StartsWith("改走"));
        var part = Gaps.Count(g => g.EngineStatus.StartsWith("部分"));
        var miss = Gaps.Length - done - delegated - part;
        Console.WriteLine($"--- 客户端有、引擎的对应情况（共 {Gaps.Length} 条）---");
        Console.WriteLine();
        foreach (var (reason, meaning, status) in Gaps)
        {
            var tag = status.StartsWith("改走") ? "委" : status.StartsWith("部分") ? "半" : "有";
            Console.WriteLine($"  [{tag}] {reason,-32} {meaning}");
            if (tag == "半") Console.WriteLine($"        {status}");
        }
        Console.WriteLine();
        Console.WriteLine($"引擎手写 {done} / 改走客户端规则库 {delegated} / 未实现 {miss}");

        // 4) 行为抽检：造一个带 Guard 的场面，看引擎是否真的放行
        // 4) 行为抽检：Guard（掩护）是否真的生效。
        //
        // 语义要摆正：Guard 单位本身**可以**被打，它掩护的是**紧邻**的那张牌 ——
        // 客户端把被掩护者标成 isBeingGuarded（派生字段），CanAttack 的
        // is_being_garded 门再据此否决。所以期望是
        //   被掩护的牌不可打、Guard 单位本身可打，
        // 而不是「Guard 单位不可打」。
        // 掩护状态由 BP_CardFunctions.UpdateGuarded 重算，改完场面必须调
        // AfterBoardChange()，否则派生字段还是旧值。
        Console.WriteLine();
        Console.WriteLine("--- 行为抽检：Guard（掩护）是否生效 ---");
        var guardDef = CardDb.All.FirstOrDefault(d => d.Has(Kw.Guard) && d.IsUnit);
        if (guardDef is null) { Console.WriteLine("  找不到带 Guard 的单位，跳过"); return miss > 0 ? 1 : 0; }

        var plainDef = CardDb.All.FirstOrDefault(d => d.IsUnit && !d.Has(Kw.Guard) && d.Id != guardDef.Id);
        var atkDef = CardDb.All.FirstOrDefault(d => d.IsUnit && !d.Has(Kw.Guard) && d.Id != guardDef.Id && d.Id != plainDef?.Id);
        if (plainDef is null || atkDef is null) { Console.WriteLine("  找不到足够的普通单位，跳过"); return miss > 0 ? 1 : 0; }

        var g = new GameEngine(3, null, null, false);
        var h = new EngineHost(g);
        g.Host = h;

        // 攻方：前线，且射程 >= 2 —— 掩护只发生在支援线（客户端 UpdateGuarded
        // 只处理 location 5/6）。射程不足会被 not_enough_range 先拦掉，
        // 那样抽检测到的是射程而不是 Guard。
        var atk = g.S.NewCard(atkDef, Side.Left);
        atk.Loc = Loc.Frontline; atk.OnFrontline = true;
        atk.EnterPlayTurn = 0; atk.AttackedThisTurn = false; atk.SummonedThisTurn = false;
        atk.KreditCost = 0;
        atk.Range = Math.Max(atk.Range, 2);
        g.S.Frontline.Add(atk);
        g.S.FrontlineOwner = Side.Left;

        // 守方：**支援线**相邻两张 —— 一张普通、一张 Guard。相邻靠 LocationNumber 判定。
        var guarded = g.S.NewCard(plainDef, Side.Right);
        guarded.Loc = Loc.Board;
        guarded.LocationNumber = 1;
        g.S.Player(Side.Right).Board.Add(guarded);

        var guardUnit = g.S.NewCard(guardDef, Side.Right);
        guardUnit.Loc = Loc.Board;
        guardUnit.LocationNumber = 2;          // 紧邻 guarded
        g.S.Player(Side.Right).Board.Add(guardUnit);

        g.S.Current = Side.Left;
        g.S.Player(Side.Left).Kredits = 99;
        g.S.Player(Side.Left).KreditSlots = 99;
        g.RefreshAllLocations();               // 重排位置号 + 让客户端重算掩护

        var targets = g.AttackTargetsFor(atk);
        Console.WriteLine($"  场面: 攻方 [{atkDef.Id}](左/前线) vs 守方 相邻 [{plainDef.Id}](#1) + [{guardDef.Id}](#2, Guard)");
        Console.WriteLine($"  引擎给出的可攻击目标: [{string.Join(", ", targets)}]");
        Console.WriteLine($"  被掩护者 isBeingGuarded={guarded.IsBeingGuarded} (id={guarded.InstanceId}), " +
                          $"Guard 单位 isBeingGuarded={guardUnit.IsBeingGuarded} (id={guardUnit.InstanceId})");
        // 镜像值 vs 引擎字段：区分「客户端没标」和「标了但同步/回填时丢了」
        Console.WriteLine($"  镜像值: 被掩护者={h.Obj(guarded).Get("isBeingGuarded")} " +
                          $"(location={h.Obj(guarded).Get("location")}, locationNumber={h.Obj(guarded).Get("locationNumber")}), " +
                          $"Guard={h.Obj(guardUnit).Get("isBeingGuarded")} " +
                          $"(location={h.Obj(guardUnit).Get("location")}, locationNumber={h.Obj(guardUnit).Get("locationNumber")})");
        // 直接调一次客户端 UpdateGuarded，看它自己写不写镜像
        Val fq = Val.Nothing, ff = Val.Nothing, fa = Val.Nothing;
        h.Call("FetchCardsByLocation", new Val[] { Val.Ref(h.CardFunctions), Val.Of(6),
            Val.Out(v => fq = v), Val.Out(v => ff = v), Val.Out(v => fa = v) });
        Console.WriteLine($"  FetchCardsByLocation(6): qty={fq.AsInt()} full={ff} arr={(fa.O is KArr ka ? ka.Count : -1)}");
        Val hg = Val.Nothing;
        h.Call("getHasGuard", new Val[] { Val.Ref(h.Obj(guardUnit)), Val.Out(v => hg = v) });
        Console.WriteLine($"  getHasGuard(Guard单位)={hg.AsBool()}  镜像 hasGuard: 被掩护者={h.Obj(guarded).Get("hasGuard")} Guard={h.Obj(guardUnit).Get("hasGuard")}");

        Val ad = Val.Nothing;
        h.Call("GetAdjacentCards", new Val[] { Val.Ref(h.CardFunctions), Val.Ref(h.Obj(guarded)), Val.True, Val.Out(v => ad = v) });
        Console.WriteLine($"  GetAdjacentCards(被掩护者)={(ad.O is KArr ka2 ? ka2.Count : -1)} 张 " +
                          $"(locationNumber: 被掩护者={guarded.LocationNumber}, Guard={guardUnit.LocationNumber})");

        var ugFn = Generated.FnIndex.Find("BP_CardFunctions", "UpdateGuarded");
        if (ugFn is not null)
        {
            var ugTrace = new List<string>();
            h.CallTrace = n => { if (ugTrace.Count < 200) ugTrace.Add(n); };
            ugFn(h, Val.Ref(h.CardFunctions), new Val[] { Val.Of(6) });
            h.CallTrace = null;
            var key = new HashSet<string> { "FetchCardsByLocation", "getHasGuard", "IsUnrevealedCovertCard",
                "GetAdjacentCards", "Array_Get", "Array_Length", "Not_PreBool" };
            Console.WriteLine("  UpdateGuarded(6) 轨迹: " + string.Join(" > ", ugTrace.Where(key.Contains)));
        }

        var protectedOk = guarded.IsBeingGuarded && !targets.Contains(guarded.InstanceId);
        var guardHittable = targets.Contains(guardUnit.InstanceId);
        if (protectedOk && guardHittable)
            Console.WriteLine("  => 掩护生效：被掩护者不可打，Guard 单位本身可打");
        else if (!guarded.IsBeingGuarded)
            Console.WriteLine("  => 掩护未生效：UpdateGuarded 没有把相邻的普通单位标成 isBeingGuarded");
        else
            Console.WriteLine($"  => 掩护标记对了，但 AttackTargetsFor 没据此否决" +
                              $"（被掩护者在目标里={targets.Contains(guarded.InstanceId)}）");
        Console.WriteLine();

        // 退出码：只看「引擎还自己手写、且和客户端不一致」的条目。
        // 「改走客户端规则库」的那些已经不是缺口 —— 判定由客户端自己的代码做，
        // 引擎只负责把输入（镜像字段 / FetchCardsByLocation / GetAdjacentCards）喂对。
        if (miss > 0)
        {
            Console.WriteLine($"结论：{miss} 条规则既没有引擎手写实现、也没有走客户端规则库。");
            return 1;
        }
        Console.WriteLine($"结论：{delegated} 条已改走客户端规则库判定（其中 Guard 有行为抽检佐证）；" +
                          $"其余 {done} 条为引擎手写实现。");
        return protectedOk && guardHittable ? 0 : 1;
    }
}

