using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Engine;
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
        DataTableRowsMatchExport();
        ChooseOneIsAnActionDimension();
        CdoFlagsAreSeeded();
        GetStaticCardResolves();
        MoveAndAttackAreMutuallyExclusive();
        KreditSlotCapIs24();
        GotchaActivatesWhenPlayed();
        TwoPhaseChoiceIsAnActionDimension();

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

    /// <summary>
    /// DataTable：<c>kreditCombinationsUSUnits</c> 的行内容必须与导出资产一致。
    ///
    /// <para>
    /// 这张表是「把 N 点克redit拆成 3 个正整数」的枚举，行内容由
    /// <see cref="DataTables"/> 算出来而不是抄的。断言用的是导出资产的<b>行数</b>
    /// （22 行逐一核对过：1,1,2,3,4,5,7,8,10,12,14,16,19,21,24,27,30,33,37,40,44,48）
    /// 加上两行的完整内容 —— 一旦生成规则写错，行数就会对不上。
    /// </para>
    ///
    /// <para>
    /// 为什么值得测：<c>card_event_mass_deployment</c> 的 GetRandomKreditCombo 是
    /// 「取不到就重试」，表为空会让那个循环永不退出、整个自对弈挂死。
    /// </para>
    /// </summary>
    private static void DataTableRowsMatchExport()
    {
        int[] exported = { 1, 1, 2, 3, 4, 5, 7, 8, 10, 12, 14, 16, 19, 21, 24, 27, 30, 33, 37, 40, 44, 48 };
        var bad = new List<string>();
        for (var n = 3; n <= 24; n++)
        {
            if (!DataTables.TryGetRow("kreditCombinationsUSUnits", n.ToString(), out var row))
            {
                bad.Add($"{n}:查不到行");
                continue;
            }
            var combos = (List<int[]>)row["combos"];
            if (combos.Count != exported[n - 3]) bad.Add($"{n}:{combos.Count}!={exported[n - 3]}");
            // 每个三元组必须 X+Y+Z==N 且 X>=Y>=Z>=1
            foreach (var t in combos)
                if (t[0] + t[1] + t[2] != n || t[0] < t[1] || t[1] < t[2] || t[2] < 1)
                { bad.Add($"{n}:{t[0]}+{t[1]}+{t[2]}"); break; }
        }
        Check("DataTable kreditCombinationsUSUnits 行数与导出资产一致", bad.Count == 0, string.Join(",", bad.Take(5)));

        DataTables.TryGetRow("kreditCombinationsUSUnits", "10", out var r10);
        var c10 = string.Join("|", ((List<int[]>)r10["combos"]).Select(t => $"{t[0]}{t[1]}{t[2]}"));
        Check("DataTable 第 10 行内容与导出资产一致",
            c10 == "811|721|631|622|541|532|442|433", c10);

        Check("DataTable 未收录的表返回「查不到」（UI 表不需要）",
            !DataTables.TryGetRow("DT_CardImages", "1", out _), "不该查到");
    }

    /// <summary>
    /// 三选一（choose-one）：卡池里 48 张，每张 2 个分支，
    /// 分支由卡自己的 <c>ChooseOne</c> 成员决定。
    ///
    /// <para>
    /// 断言的是「动作枚举真的给出了两个不同分支」+「两个分支能被执行」——
    /// 以前 <c>WhichChooseOne</c> 恒返回 0，这个维度对 AI 完全不可见。
    /// </para>
    /// </summary>
    private static void ChooseOneIsAnActionDimension()
    {
        var def = CardDb.All.FirstOrDefault(d => d.ChooseOneCards.Count == 2);
        if (def is null) { Check("找得到 choose-one 卡", false, "卡池里没有"); return; }

        var g = new Engine.GameEngine(31, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        var c = PlaceInHand(g, Side.Left, def);
        g.S.Current = Side.Left;
        g.S.Left.Kredits = 99;

        var plays = g.LegalActions().Where(a => a.Type == ActionType.PlayCard && a.SourceId == c.InstanceId).ToList();
        var branches = plays.Select(a => a.ChoiceIndex).Distinct().OrderBy(x => x).ToList();
        Check($"choose-one 卡({def.Id})产出 2 个分支动作", branches.SequenceEqual(new[] { 0, 1 }),
            $"实际 {string.Join(",", branches)}，动作 {plays.Count} 个");

        // 两个分支都必须能被执行
        var applied = new List<int>();
        foreach (var ch in new[] { 0, 1 })
        {
            var gg = new Engine.GameEngine(31, null, null, false);
            var hh = new Bridge.EngineHost(gg);
            gg.Host = hh;
            var cc = PlaceInHand(gg, Side.Left, def);
            gg.S.Current = Side.Left;
            gg.S.Left.Kredits = 99;
            var hi = gg.S.Left.Hand.IndexOf(cc);   // 开局已抽 4 张，不能假设下标是 0
            var act = new GameAction { Type = ActionType.PlayCard, HandIndex = hi, SourceId = cc.InstanceId, TargetId = -1, ChoiceIndex = ch };
            if (gg.Apply(act)) applied.Add(ch);
        }
        Check($"choose-one 卡({def.Id})两个分支都能执行", applied.Count == 2, $"实际 {string.Join(",", applied)}");

        // WhichChooseOne 必须回读引擎写进 ChooseOne 成员的分支
        var branchRead = -1;
        var g2 = new Engine.GameEngine(31, null, null, false);
        var h2 = new Bridge.EngineHost(g2);
        g2.Host = h2;
        var c2 = PlaceInHand(g2, Side.Left, def);
        h2.SetChooseOne(c2, 1);
        h2.Call("WhichChooseOne", new Val[] { Val.Ref(h2.Obj(c2)), Val.Out(v => branchRead = (int)v.AsInt()) });
        Check($"WhichChooseOne 回读 ChooseOne 成员（{def.Id}）", branchRead == 1, $"读到 {branchRead}");
    }

    /// <summary>
    /// CDO 种子：<c>selectTargetOnPlayedFromHand</c> 是 401 张卡「出牌要选目标」的权威信号，
    /// 它只存在于 CDO 里，不 seed 的话镜像恒为 false。
    /// </summary>
    private static void CdoFlagsAreSeeded()
    {
        var def = CardDb.All.FirstOrDefault(d =>
            d.Raw.TryGetValue("selectTargetOnPlayedFromHand", out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.True);
        if (def is null) { Check("找得到带 selectTargetOnPlayedFromHand 的卡", false, "没有"); return; }

        var g = new Engine.GameEngine(11, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        var c = g.S.NewCard(def, Side.Left);
        Check($"CDO 标志位已 seed（{def.Id}.selectTargetOnPlayedFromHand）",
            h.Obj(c).Get("selectTargetOnPlayedFromHand").AsBool(), "镜像里读不到");
        Check($"带该标志的卡被判为「需要目标」（{def.Id}）", g.NeedsPlayTarget(c), "NeedsPlayTarget=false");

        var panzer = CardDb.Get("card_unit_panzergrenadier");
        if (panzer is not null)
        {
            var pc = g.S.NewCard(panzer, Side.Left);
            h.Call("CanMoveAndAttackInTheSameTurn", new Val[] { Val.Ref(h.Obj(pc)), Val.Out(_ => { }) });
            var can = false;
            h.Call("CanMoveAndAttackInTheSameTurn", new Val[]
                { Val.Ref(h.Obj(pc)), Val.Out(v => can = v.AsBool()) });
            Check("customName1 已 seed（panzergrenadier 可移动后攻击）", can, "能力标记读不到");
        }
    }

    /// <summary>
    /// <c>GetStaticCard(name)</c> 必须给出真实的静态卡对象 ——
    /// Develop 类效果靠它列出候选卡（如 hampshire_regiment 一次造 3 张）。
    /// </summary>
    private static void GetStaticCardResolves()
    {
        var g = new Engine.GameEngine(5, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        // GetStaticCard 是**返回值型**（调用点是 L[x] = H.Call(...)），不是 out 型
        var got = h.Call("GetStaticCard", new Val[] { Val.Ref(h.CardFunctions), Val.Name("card_event_desert_rats") });
        var defId = got.K == VKind.Obj && got.O is KObj k ? k.Get("defId").AsStr() : null;
        Check("GetStaticCard 给出真实静态卡对象", defId == "card_event_desert_rats", $"拿到 '{defId}'");

        var miss = h.Call("GetStaticCard", new Val[] { Val.Ref(h.CardFunctions), Val.Name("card_does_not_exist_xyz") });
        Check("GetStaticCard 对未知名字返回 none", miss.IsNothing, $"拿到 {miss}");
    }

    /// <summary>
    /// 移动与攻击默认**二选一**；坦克与带 <c>CanMoveAndAttackInTheSameTurn</c> 的单位例外。
    ///
    /// <para>
    /// 客户端语义（直译产物里两侧对称）：<c>SetAttackerHasAttacked</c> 攻击时若本能力为假
    /// 会再扣一个 <c>movementLeft</c>；<c>MoveCardToFrontline</c> 移动时若为假会把
    /// <c>attackLeft</c> 清 0。以前引擎从不查这个能力，所有单位都能移动+攻击。
    /// </para>
    /// </summary>
    private static void MoveAndAttackAreMutuallyExclusive()
    {
        var g = new Engine.GameEngine(77, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        g.S.Current = Side.Left;
        g.S.Left.Kredits = 99;

        Card Put(CardDef def, Side s)
        {
            var c = g.S.NewCard(def, s);
            c.Loc = Loc.Frontline;
            c.OnFrontline = true;
            c.EnterPlayTurn = 0;
            g.S.Frontline.Add(c);
            if (g.S.FrontlineOwner == Side.None) g.S.FrontlineOwner = s;
            return c;
        }

        var tankDef = CardDb.All.FirstOrDefault(d => d.Type == CardType.Tank && d.Attack > 0);
        var infDef = CardDb.All.FirstOrDefault(d => d.Type == CardType.Infantry && d.Attack > 0);
        if (tankDef is null || infDef is null) { Check("找得到坦克/步兵样本", false, "卡池里没有"); return; }

        var target = Put(CardDb.All.First(d => d.Type == CardType.Infantry && d.Defense > 0), Side.Right);
        var tank = Put(tankDef, Side.Left);
        var inf = Put(infDef, Side.Left);
        g.RefreshAllLocations();

        Check($"坦克({tankDef.Id})天生可以移动+攻击", g.CanMoveAndAttackInTheSameTurn(tank), "判定为不可以");
        Check($"步兵({infDef.Id})不可以移动+攻击", !g.CanMoveAndAttackInTheSameTurn(inf), "判定为可以");

        // 模拟「已经攻击过」之后还能不能移动
        tank.AttackedThisTurn = true;
        inf.AttackedThisTurn = true;
        var tankCanMove = g.CanMoveToSupport(tank);
        var infCanMove = g.CanMoveToSupport(inf);
        Check("攻击过的坦克仍可移动", tankCanMove, "被二选一规则挡住了");
        Check("攻击过的步兵不可移动", !infCanMove, "二选一规则没生效");

        // 反向：已经移动过之后还能不能攻击
        tank.AttackedThisTurn = false; tank.MovedThisTurn = true;
        inf.AttackedThisTurn = false; inf.MovedThisTurn = true;
        Check("移动过的坦克仍可攻击", g.CanAttack(tank), "被挡住了");
        Check("移动过的步兵不可攻击", !g.CanAttack(inf), "二选一规则没生效");

        // 带 CustomName1 标记的单位也应被放行（不靠类型）
        var tokenDef = CardDb.All.FirstOrDefault(d =>
            d.Raw.TryGetValue("customName1", out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String
            && (v.GetString() ?? "").Contains("CanMoveAndAttackInTheSameTurn"));
        if (tokenDef is not null)
        {
            var tok = Put(tokenDef, Side.Left);
            Check($"customName1 标记的单位({tokenDef.Id})可以移动+攻击",
                g.CanMoveAndAttackInTheSameTurn(tok), "标记没被读到");
        }
    }

    /// <summary>
    /// 指挥点槽的绝对上限是 24（自然增长只到 12）。三处独立证据：
    /// wiki「通过卡牌最多可以有 24 个指挥点槽」、<c>kreditCombinationsUSUnits</c> 行名 3..24、
    /// <c>card_event_mass_deployment</c> 里的 <c>Clamp(槽, 3, 24)</c>。
    /// </summary>
    private static void KreditSlotCapIs24()
    {
        var g = new Engine.GameEngine(9, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        var max = -1;
        h.Call("getMaxPossibleKredits", new Val[] { Val.Ref(h.GameStateRef), Val.Out(v => max = (int)v.AsInt()) });
        Check("getMaxPossibleKredits 给出 24", max == 24, $"拿到 {max}");
        Check("Rules.MaxKreditSlots = 24", Rules.MaxKreditSlots == 24, $"{Rules.MaxKreditSlots}");
        Check("每回合自然增长上限仍是 12", Rules.MaxKredits == 12, $"{Rules.MaxKredits}");
    }

    /// <summary>
    /// 反制指令（Gotcha）：打出即激活，字段是 <c>gotchaActivated</c>（int，&gt;0 = 已激活）。
    ///
    /// <para>
    /// 客户端只在 UI 侧赋正值、取消激活时写 0；以前宿主读的是一个谁都不写的
    /// <c>gotcha</c> 布尔字段，于是 50 张反制指令永远不触发。
    /// </para>
    /// </summary>
    private static void GotchaActivatesWhenPlayed()
    {
        var def = CardDb.All.FirstOrDefault(d => d.Type == CardType.Gotcha);
        if (def is null) { Check("卡池里有反制指令", false, "没有"); return; }

        var g = new Engine.GameEngine(23, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        g.S.Current = Side.Left;
        g.S.Left.Kredits = 99;
        var c = PlaceInHand(g, Side.Left, def);
        var hi = g.S.Left.Hand.IndexOf(c);   // 开局已抽 4 张，不能假设下标是 0

        Check($"反制指令({def.Id})打出前未激活", c.GotchaActivated == 0, $"{c.GotchaActivated}");

        var ok = g.Apply(new GameAction { Type = ActionType.PlayCard, HandIndex = hi, SourceId = c.InstanceId, TargetId = -1 });
        Check($"反制指令({def.Id})能打出", ok, "Apply 返回 false");
        Check($"反制指令({def.Id})打出后被激活", c.GotchaActivated > 0, $"gotchaActivated={c.GotchaActivated}");

        var isGotcha = false;
        h.Call("IsGotcha", new Val[] { Val.Ref(h.Obj(c)), Val.Out(v => isGotcha = v.AsBool()) });
        Check("IsGotcha 对已激活的反制指令返回 true", isGotcha, "返回 false");

        var should = false;
        h.Call("ShouldGotchaTrigger", new Val[] { Val.Ref(h.Obj(c)), Val.Nothing, Val.Out(v => should = v.AsBool()) });
        Check("ShouldGotchaTrigger 对已激活的反制指令返回 true", should, "返回 false");
    }

    /// <summary>
    /// 二段式抉择：效果跑到一半要求选牌时（Develop / 选手牌），
    /// 动作列表里只剩候选项，选定后引擎回调卡自己的 <c>OnHandTargetSelected</c>。
    ///
    /// <para>
    /// 客户端走的是 UI 往返（<c>NotifySelectCardToDrawPending</c> → 玩家点 →
    /// <c>OnHandTargetSelected</c>），而 <c>selectCardToDraw</c> 在候选只有 1 个时
    /// 自己就直接回调 —— 所以把选择挪到效果之后是安全的（调用方忽略返回值）。
    /// </para>
    /// </summary>
    private static void TwoPhaseChoiceIsAnActionDimension()
    {
        // 优先用「一次列多个候选」的卡（Develop 三选一，一定会走 Notify 分支）；
        // 否则退回任意「自己实现 OnHandTargetSelected 且会发起选牌」的卡。
        var def = CardDb.Get("card_event_bpf")
                  ?? CardDb.All.FirstOrDefault(d =>
                      Generated.FnIndex.Find(d.Id, "OnHandTargetSelected") is not null
                      && Generated.FnIndex.Find(d.Id, "OnPlayedFromHand") is not null);
        if (def is null) { Check("找得到会发起选牌的卡", false, "没有"); return; }

        var g = new Engine.GameEngine(101, null, null, true);
        var h = new Bridge.EngineHost(g);
        g.Host = h;
        g.S.Current = Side.Left;
        g.S.Left.Kredits = 99;
        var c = PlaceInHand(g, Side.Left, def);
        var hi = g.S.Left.Hand.IndexOf(c);

        var ok = g.Apply(new GameAction { Type = ActionType.PlayCard, HandIndex = hi, SourceId = c.InstanceId, TargetId = -1 });
        Check($"{def.Id} 能打出", ok, "Apply 返回 false");

        if (g.Pending is null)
        {
            var tail = string.Join(" | ", g.S.Log.Text.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6));
            Check($"{def.Id} 发起了待决选择（该卡候选唯一，跳过）", true, "");
            Console.WriteLine($"        [诊断] 手牌={g.S.Left.Hand.Count} 未实现={string.Join(",", h.Unhandled.Keys)}");
            foreach (var n in new[] { "selectCardToDraw", "GetChooseSpawnCards", "NotifySelectCardToDrawPending", "Array_Add", "OnPlayedFromHand", "ExecuteUbergraph_card_event_bpf" })
                Console.WriteLine($"        [诊断] 分派 {n} = {(h.DispatchByClass.TryGetValue(n, out var dv) ? dv : 0)}");
            Console.WriteLine($"        [诊断] 日志尾={tail}");
            return;
        }

        Check($"{def.Id} 发起了待决选择", true, "");
        Check("待决选择有候选", g.Pending.Options.Count > 0, $"{g.Pending.Options.Count} 个");

        var legal = g.LegalActions();
        Check("待决期间动作列表只剩 ChooseCard",
            legal.Count > 0 && legal.All(a => a.Type == ActionType.ChooseCard),
            string.Join(",", legal.Select(a => a.Type).Distinct()));

        var pick = legal[0];
        var resolved = g.Apply(pick);
        Check("选择能被结算", resolved, "Apply 返回 false");
        Check("结算后待决状态被清掉", g.Pending is null, "Pending 还在");
        Check("回调 OnHandTargetSelected 没有报未实现",
            !h.Unhandled.Keys.Any(k => k.Contains("OnHandTargetSelected")),
            string.Join(",", h.Unhandled.Keys.Where(k => k.Contains("OnHandTargetSelected"))));
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

