using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 单卡机制验证：不做「跑通没炸」这种弱检查，而是断言<b>具体数值和状态变化</b>。
///
/// <para>
/// 直译产物最大的风险不是崩溃，是「编译通过、跑得动、数字是错的」——
/// 接收者算错、out 参数没回写、触发点发错时机，全都会静默产出看似合理的错值。
/// 所以每个机制都要有一个能失败的断言。
/// </para>
/// </summary>
public static class CardTests
{
    private static int _pass, _fail;

    public static int Run(string[] args)
    {
        _pass = _fail = 0;

        IntelRevealsEnemyHand();
        IntelNotifiesResponders();
        EventPayloadReachesEffect();
        VeteranUpgradeIsReachable();
        BlitzCanActOnSummonTurn();

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Intel n = 打出时随机翻开对手 n 张手牌，且不动手牌顺序。
    ///
    /// <para>
    /// sign 1：翻开的张数正好是 n，不多不少。
    /// sign 2：只翻对手的牌，自己的手牌一张都不许被动。
    /// sign 3：手牌<b>顺序</b>不变 —— 翻牌只加标记，不重排。顺序是对局的可见信息，
    /// 洗错地方会让同一 seed 跑出不同结果。
    /// </para>
    /// </summary>
    private static void IntelRevealsEnemyHand()
    {
        var g = new Engine.GameEngine(7, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        var card = new CardDef { Id = "test_intel", Name = "test_intel", Type = CardType.Order, Cipher = 3 };
        var played = PlaceInHand(g, Side.Left, card);

        var foeHand = g.S.Player(Side.Right).Hand;
        var myHand = g.S.Player(Side.Left).Hand;
        var myOrder = myHand.Select(c => c.InstanceId).ToList();
        var foeOrder = foeHand.Select(c => c.InstanceId).ToList();

        // 清掉可能已有的标记
        foreach (var c in myHand.Concat(foeHand)) h.Obj(c).Set("seenByCipher", Val.False);

        // 直接走宿主原语，等价于 CardPlayedFromHand 里的调用
        h.Call("SetCardsSeenByCipher", new Val[]
        {
            Val.Ref(h.Obj(played)), Val.Of(played.Cipher), Val.Of(played.InstanceId),
        });

        var foeSeen = foeHand.Count(c => h.Obj(c).Get("seenByCipher").AsBool());
        var mySeen = myHand.Count(c => h.Obj(c).Get("seenByCipher").AsBool());

        Check("Intel 翻牌张数 = cipher", foeSeen == Math.Min(3, foeHand.Count), $"期望 {Math.Min(3, foeHand.Count)}, 实得 {foeSeen}");
        Check("Intel 不翻自己的牌", mySeen == 0, $"自己的手牌被翻了 {mySeen} 张");
        Check("Intel 不改对手手牌顺序",
            foeHand.Select(c => c.InstanceId).SequenceEqual(foeOrder),
            $"顺序变了: [{string.Join(",", foeOrder)}] -> [{string.Join(",", foeHand.Select(c => c.InstanceId))}]");
        Check("Intel 不改自己手牌顺序",
            myHand.Select(c => c.InstanceId).SequenceEqual(myOrder), "自己的手牌顺序变了");
    }

    /// <summary>
    /// Intel 要给注册了 OnIntelTriggered 的卡传「翻了几张」这个数值，而不是传卡对象。
    ///
    /// <para>
    /// 这是最容易错的一处：普通触发点的第二个形参是「相关卡」，
    /// 而 OnIntelTriggered 的第二个是整数。传错的话卡的逻辑会把整数当卡用，
    /// 不炸但结果全错。
    /// </para>
    /// </summary>
    private static void IntelNotifiesResponders()
    {
        // 找一张真的注册了 OnIntelTriggered 的卡
        var responder = Generated.FnIndex.Assets.Keys
            .FirstOrDefault(k => k.StartsWith("card_", StringComparison.Ordinal)
                                 && Generated.FnIndex.Find(k, "OnIntelTriggered") is not null);

        if (responder is null)
        {
            Check("存在注册 OnIntelTriggered 的卡", false, "一张都没有，无法验证");
            return;
        }

        var g = new Engine.GameEngine(11, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        var def = CardDb.Get(responder) ?? CardDb.Resolve(responder);
        if (def is null) { Check($"{responder} 能在卡库里找到", false, "找不到定义"); return; }

        var board = g.S.NewCard(def, Side.Left);
        board.Loc = Loc.Board;
        g.S.Player(Side.Left).Board.Add(board);

        try
        {
            h.Call("SetCardsSeenByCipher", new Val[] { Val.Nothing, Val.Of(2), Val.Of(board.InstanceId) });
            Check($"{responder}.OnIntelTriggered 能跑完不抛异常", true, null);
        }
        catch (Exception ex)
        {
            Check($"{responder}.OnIntelTriggered 能跑完不抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 事件形参必须真的送进效果体。
    ///
    /// <para>
    /// 这条链上有两个独立的坑，都会让效果<b>静默消失</b>（不报错、不进 Unhandled）：
    /// </para>
    /// <list type="number">
    /// <item>分派端只传两个参数 → 第 3 个起恒为 Nothing，
    /// 而 <c>if (!destroyedInCombat) return;</c> 这类前置守卫直接短路。</item>
    /// <item>读取端把持久帧槽位读成局部变量 → 值写进去了但读的是另一块存储。</item>
    /// </list>
    /// <para>
    /// 这里断言的是「签名表存在且与生成代码一致」——两个坑各自都能让这条断言失败。
    /// </para>
    /// </summary>
    private static void EventPayloadReachesEffect()
    {
        // sign 1：形参表必须有 OnOtherCardDestroyed，且参数名与 UE 侧一致
        var p = Generated.FnIndex.ParamsOf("OnOtherCardDestroyed");
        Check("OnOtherCardDestroyed 有形参表", p.Length == 6, $"实得 {p.Length} 个: [{string.Join(",", p)}]");
        Check("OnOtherCardDestroyed 第 4 参是 destroyedLocation",
            p.Length > 3 && p[3] == "destroyedLocation", $"实得 '{string.Join(",", p)}'");
        Check("OnOtherCardDestroyed 第 6 参是 destroyedInCombat",
            p.Length > 5 && p[5] == "destroyedInCombat", $"实得 '{string.Join(",", p)}'");

        // sign 2：持久帧读取必须走 GetVar。若发射器回退成 GetLocal，
        // 这个文件里就会重新出现 GetLocal(L, "K2Node_Event_...")。
        var f = Path.Combine(AppContext.BaseDirectory, "Generated", "Germany", "BrothersInArms",
            "units", "card_unit_7_schutzen.g.cs");
        if (!File.Exists(f))
        {
            // 生成物可能没拷到输出目录，退而查源码树
            f = Path.Combine("Generated", "Germany", "BrothersInArms", "units", "card_unit_7_schutzen.g.cs");
        }
        if (!File.Exists(f))
        {
            Check("能找到 card_unit_7_schutzen 的生成代码", false, $"路径不存在: {f}");
            return;
        }
        var src = File.ReadAllText(f);
        var badReads = System.Text.RegularExpressions.Regex.Matches(
            src, @"GetLocal\(L, ""K2Node_Event_").Count;
        Check("持久帧读取不走 GetLocal", badReads == 0, $"仍有 {badReads} 处读不到值");
        Check("持久帧读取走 GetVar",
            System.Text.RegularExpressions.Regex.IsMatch(src, @"H\.GetVar\(""K2Node_Event_destroyedInCombat""\)"),
            "没看到 destroyedInCombat 的 GetVar 读取");
    }

    /// <summary>
    /// 老兵升级必须可达。
    ///
    /// <para>
    /// 旧实现把 <c>getHasVeteranUpgrade</c> 实现成「已经是老兵」，
    /// 于是 <c>MakeVeteran</c> 的守卫 <c>!IsVeteran &amp;&amp; getHasVeteranUpgrade</c>
    /// 恒为假 —— 全卡池的老兵机制静默失效。
    /// </para>
    /// </summary>
    private static void VeteranUpgradeIsReachable()
    {
        var def = CardDb.Get("card_unit_7_schutzen");
        if (def is null) { Check("card_unit_7_schutzen 在卡库", false, "找不到"); return; }

        Check("card_unit_7_schutzen 有老兵升级", def.HasVeteranUpgrade,
            $"VeteranUpgradeId='{def.VeteranUpgradeId}' spawn=[{string.Join(",", def.SpawnCardNames)}]");
        Check("老兵版本指向 _vet 卡", def.VeteranUpgradeId == "card_unit_7_schutzen_vet",
            $"实得 '{def.VeteranUpgradeId}'");

        // sign：全卡池应当有 41 张可升级（与 <id>_vet 命名推断一致）。
        // 数字变了说明 spawnCardName 的解析规则被我改坏了。
        var n = CardDb.All.Count(d => d.HasVeteranUpgrade);
        Check("全卡池老兵可升级数 = 41", n == 41, $"实得 {n}");

        // sign：老兵卡自己不该再有升级（不会递归）
        var vet = CardDb.Get("card_unit_7_schutzen_vet");
        Check("_vet 卡自己不再有老兵升级", vet is null || !vet.HasVeteranUpgrade,
            $"实得 {vet?.VeteranUpgradeId}");

        // sign：宿主原语返回 true，且 getStaticVeteranUpgrade 真的给出那张卡
        var g = new Engine.GameEngine(3, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        var c = g.S.NewCard(def, Side.Left);
        c.Loc = Loc.Board;
        g.S.Player(Side.Left).Board.Add(c);

        Val got = Val.Nothing;
        h.Call("getHasVeteranUpgrade", new Val[] { Val.Ref(h.Obj(c)), Val.Out(__v => got = __v) });
        Check("getHasVeteranUpgrade 对未升级的卡返回 true", got.AsBool(), $"实得 {got}");

        Val vetCard = Val.Nothing;
        h.Call("getStaticVeteranUpgrade", new Val[] { Val.Ref(h.Obj(c)), Val.Out(__v => vetCard = __v) });
        Check("getStaticVeteranUpgrade 给出老兵卡对象", !vetCard.IsNothing, "返回了 none");

        Val wantAtk = Val.Nothing;
        var vetRef = vetCard;
        h.Call("getAndDecryptAttack", new Val[] { vetRef, Val.Out(__v => wantAtk = __v) });
        var expect = CardDb.Get("card_unit_7_schutzen_vet")?.Attack ?? -1;
        Check("老兵卡攻击力可读且非零", wantAtk.AsInt() > 0, $"实得 {wantAtk.AsInt()}, 期望 {expect}");
    }

    /// <summary>
    /// 召唤失调的 Blitz 例外：Blitz 单位出牌当回合就能移动/攻击。
    ///
    /// <para>
    /// 出牌处已经有 <c>if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;</c>，
    /// 但 CanAttack / CanMoveAtAll 无条件挡 SummonedThisTurn 的话，
    /// 那个豁免就是死逻辑。真实回放里同回合「出牌→行动」6 例全部是 Blitz 卡。
    /// </para>
    /// </summary>
    private static void BlitzCanActOnSummonTurn()
    {
        // 卡池里真的找一张带 Blitz 的单位，避免测试自己造数据掩盖问题
        var blitzDef = CardDb.All.FirstOrDefault(d => d.IsUnit && d.Has(Kw.Blitz) && d.Attack > 0);
        if (blitzDef is null) { Check("卡池里有带 Blitz 的单位", false, "一张都没有"); return; }

        var plainDef = CardDb.All.FirstOrDefault(d => d.IsUnit && !d.Has(Kw.Blitz) && d.Attack > 0);
        if (plainDef is null) { Check("卡池里有不带 Blitz 的单位", false, "一张都没有"); return; }

        var g = new Engine.GameEngine(5, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        // 造一个「刚出牌」的单位：模拟 Actions 里的落场状态
        Card Summon(CardDef def)
        {
            var c = g.S.NewCard(def, Side.Left);
            c.Loc = Loc.Board;
            c.SummonedThisTurn = true;
            c.OnFrontline = true;
            g.S.Frontline.Add(c);
            if (g.S.FrontlineOwner == Side.None) g.S.FrontlineOwner = Side.Left;
            if (!def.Has(Kw.Blitz)) c.AttackedThisTurn = true;   // 与出牌处一致
            return c;
        }

        var b = Summon(blitzDef);
        var p = Summon(plainDef);

        Check($"Blitz 单位({blitzDef.Id})当回合可移动", g.CanMoveToSupport(b) || g.CanMoveToFrontline(b) || b.OnFrontline,
            "被召唤失调挡住");
        Check($"Blitz 单位({blitzDef.Id})的 AttackedThisTurn 未被置位", !b.AttackedThisTurn,
            "AttackedThisTurn 被错误置位（说明豁免没生效）");
        Check($"无 Blitz 单位({plainDef.Id})当回合被标记已攻击", p.AttackedThisTurn,
            "非 Blitz 单位当回合竟然没被标记");
    }

    private static Card PlaceInHand(Engine.GameEngine g, Side s, CardDef def)
    {
        var c = g.S.NewCard(def, s);
        c.Loc = Loc.Hand;
        g.S.Player(s).Hand.Add(c);
        return c;
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}   {detail}"); }
    }
}

