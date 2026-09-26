using KardsSim.Core;
using KardsSim.Engine;
using KardsSim.Generated;
using KardsSim.Kismet;

namespace KardsSim.Bridge;

/// <summary>
/// 直译产物 ↔ 游戏引擎 的桥。
///
/// <para>
/// **为什么不是把 226 个宿主 API 重新实现一遍**：<c>cardFunction.*</c> 的 292 个函数本身
/// 也是蓝图，已经被转译成 C# 了（BP_CardFunctions.g.cs）。所以这里的做法是
/// <b>把调用转回去</b>：<c>cardFunction.DamageCard(...)</c> → 调用转译出来的
/// <c>BP_CardFunctions.DamageCard(host, cardFunctionObj, args)</c>，它会继续做 H.Call，
/// 最终落到少数几个真正碰游戏状态的原语上。
/// </para>
///
/// <para>
/// 于是「要手写多少」从 226 个降到了几十个原语 —— 那些直接改 GameState 的叶子函数。
/// 其余全自动。
/// </para>
/// </summary>
public sealed class EngineHost : Host
{
    public readonly GameEngine Engine;

    /// <summary>Card.InstanceId ↔ KObj 双向映射（蓝图侧到处用 cardID 找人）。</summary>
    private readonly Dictionary<int, KObj> _byId = new();
    private readonly Dictionary<KObj, Card> _byObj = new();

    /// <summary>cardFunction 单例（对应蓝图里的 BP_CardFunctions 实例）。</summary>
    public readonly KObj CardFunctions = new("BP_CardFunctions_C");
    /// <summary>GameStateRef 单例（对应 BP_GameState_Battle）。</summary>
    public readonly KObj GameStateRef = new("BP_GameState_Battle_C");
    /// <summary>CardFunctionsNotifier 单例（Notify* 的接收者，全部 no-op 但要有实体）。</summary>
    public readonly KObj Notifier = new("U_CardFunctionsNotifier_C");

    /// <summary>
    /// BP_Logic 单例（游戏模式对象）。
    ///
    /// <para>
    /// 客户端规则库要经它取 <c>GameStateRef</c> 和 <c>myRealSide</c>：
    /// <c>CanAttack</c> 的「地面单位不能攻击」判定就是
    /// <c>GetLogic() -> IsThereGameplayRestriction(GameStateRef, myRealSide, 4)</c>。
    /// 少了它整条判定落空，那一步会因「拿不到限制状态」而静默走偏。
    /// </para>
    /// </summary>
    public readonly KObj Logic = new("BP_Logic_C");

    /// <summary>
    /// HQ 的合成镜像对象（每边一个）。
    ///
    /// <para><b>为什么需要</b>：引擎把 HQ 建模成 <c>GameState.Player(side).Hq</c> 一个整数，
    /// 没有卡对象；而客户端把 HQ 当成场上的一张「位置卡」——
    /// <c>GetLocationCardBySide(side)</c> 取它、<c>DamageCard(hqCard, n, …)</c> 打它。
    /// 穿透伤害（多余伤害转打 HQ）和「变老兵时打敌方 HQ 3 点」都走这条路径。
    /// 缺了这两个对象，那些效果会静默变成空操作：不报错、不进 Unhandled，
    /// 只是 HQ 永远不掉血。</para>
    ///
    /// <para>标记字段 <c>isHq</c> 让 <c>DamageCard</c> 把伤害路由回
    /// <c>Engine.DamageHq</c>。</para>
    /// </summary>
    public readonly KObj HqLeft = new("UBaseCardObject_HQ");

    /// <summary>HQ 的合成镜像对象（右方）。见 <see cref="HqLeft"/>。</summary>
    public readonly KObj HqRight = new("UBaseCardObject_HQ");

    /// <summary>按阵营取 HQ 的合成对象（客户端 ECardLocationEnum: 5=HQLeft 6=HQRight）。</summary>
    public KObj HqOf(Side s) => s == Side.Left ? HqLeft : HqRight;

    /// <summary>这个镜像对象是不是合成的 HQ。</summary>
    public bool IsHqObj(KObj k) => ReferenceEquals(k, HqLeft) || ReferenceEquals(k, HqRight);

    /// <summary>合成 HQ 对象对应的阵营。</summary>
    public Side HqSideOf(KObj k) => ReferenceEquals(k, HqLeft) ? Side.Left : Side.Right;

    /// <summary>
    /// 按**客户端位置枚举**取该位置上的卡。
    ///
    /// <para>两套位置语义必须在这里对齐：引擎用 <c>Loc</c>（Board/Frontline/Hand…）
    /// 加 <c>Owner</c>，客户端用 <c>ECardLocationEnum</c>（5=HQLeft 6=HQRight
    /// 7=Frontline 3/4=Hand 1/2=Deck 8=Discard）。
    /// <c>FetchCardsByLocation</c> 与 <c>GetAdjacentCards</c> 都按客户端枚举问，
    /// 所以这个映射只能有一份，写两处迟早对不上。</para>
    /// </summary>
    public List<Card> CardsInClientLocation(int loc)
    {
        var side = loc is 1 or 3 or 5 ? Side.Left : Side.Right;
        var p = Engine.S.Player(side);
        return loc switch
        {
            1 or 2 => p.Deck,
            3 or 4 => p.Hand,
            5 or 6 => p.Board,
            7 => Engine.S.Frontline,
            8 => p.Discard,
            _ => new List<Card>(),
        };
    }

    /// <summary>统计：各来源的分派次数，便于看出瓶颈在哪。</summary>
    public readonly Dictionary<string, int> DispatchByClass = new(StringComparer.Ordinal);

    /// <summary>
    /// 每次分派前的调用轨迹钩子。排查「效果跑了但什么都没发生」时用：
    /// 直译产物里的守卫都是静默 return，只有把真实的调用顺序打出来，
    /// 才能看出它是在哪一道门提前退出的。默认不接。
    /// </summary>
    public Action<string> CallTrace;

    /// <summary>
    /// 三选一（WhichChooseOne）的决策器，返回 0 或 1。
    ///
    /// <para>
    /// 这是蓝图里少数真正需要「选择」的地方：卡的逻辑按返回值分两支。
    /// 默认取第 0 支，保证同一 seed 可复现；训练时由 AI 后端接管这个钩子，
    /// 否则这个动作维度对模型就是不可见的。
    /// </para>
    /// </summary>
    public Func<Card, int> ChooseOne = _ => 0;

    public EngineHost(GameEngine engine)
    {
        Engine = engine;
        Globals["GameStateRef"] = Val.Ref(GameStateRef);

        // 两个单例必须互相知道对方。蓝图里的转发壳就是这么写的：
        //   BP_CardFunctions.GetClientSide  ->  GameStateRef.GetClientSide
        // 少了 CardFunctions 这一侧的反向指针，转发壳里的接收者会算成 none，
        // 分派器只好回落到「没有接收者」的兜底分支，又找回 BP_CardFunctions 的
        // 同名壳 —— 无限递归到栈溢出。
        GameStateRef.Set("cardFunction", Val.Ref(CardFunctions));
        CardFunctions.Set("GameStateRef", Val.Ref(GameStateRef));
        CardFunctions.Set("CardFunctionsNotifier", Val.Ref(Notifier));
        CardFunctions.Set("GameState_Battle", Val.Ref(GameStateRef));

        // BP_Logic 是「游戏模式」，规则库经它拿 GameStateRef 和当前阵营。
        // 镜像成引擎侧的权威对象，规则库读到的就是真实状态。
        Logic.Set("GameStateRef", Val.Ref(GameStateRef));
        Logic.Set("cardFunction", Val.Ref(CardFunctions));
        Logic.Set("myRealSide", Val.Of((int)engine.S.Current));

        // HQ 合成对象：标记 + 阵营 + 客户端位置枚举（5=HQLeft 6=HQRight）。
        // location 必须给对 —— 规则库会拿它做位置比较（Guard / 射程判定）。
        HqLeft.Set("isHq", Val.True);
        HqLeft.Set("side", Val.Of((int)Side.Left));
        HqLeft.Set("location", Val.Of(5));
        HqLeft.Set("cardID", Val.Of(0));
        HqRight.Set("isHq", Val.True);
        HqRight.Set("side", Val.Of((int)Side.Right));
        HqRight.Set("location", Val.Of(6));
        HqRight.Set("cardID", Val.Of(0));

        RuleDispatch = Handle;
        Logger = _ => { };
    }

    // ===================== 对象映射 =====================

    public KObj Obj(Card c)
    {
        if (c is null) return null;
        if (_byId.TryGetValue(c.InstanceId, out var k)) return k;
        k = new KObj("BaseCardObject_C", c.InstanceId);
        k.Set("cardID", Val.Of(c.InstanceId));
        k.Set("side", Val.Of((int)c.Owner));
        k.Set("cardFunction", Val.Ref(CardFunctions));
        k.Set("GameStateRef", Val.Ref(GameStateRef));
        k.Set("CardFunctionsNotifier", Val.Ref(Notifier));
        k.Set("defId", Val.Of(c.Id ?? ""));
        // 卡的 name 就是它的资产名（cards.json 里 id == name），蓝图大量按名字判卡
        //（Develop 的候选列表就是 GetMember(item,"name")；天气卡判定也是拿 name 去比集合）。
        // 不写的话读出来是空值，候选列表变成一串空串。
        k.Set("name", Val.Of(c.Id ?? ""));
        SeedCdoFields(k, c.Def);
        _byId[c.InstanceId] = k;
        _byObj[k] = c;
        return k;
    }

    /// <summary>
    /// 把 CDO 上的实例变量种子灌进镜像。
    ///
    /// <para>
    /// 客户端把大量「卡牌自身的静态属性」直接初始化在 CDO 里，蓝图读的是实例变量
    /// （<c>H.GetMember(card, "…")</c>）。不 seed 的话它们全是空值。实测影响面：
    /// </para>
    /// <list type="bullet">
    /// <item><c>selectTargetOnPlayedFromHand</c> —— <b>401 张卡</b>用它表示
    ///   「出牌时要指定目标」，是「需要目标」的权威信号；</item>
    /// <item><c>customName1</c> / <c>customName</c> —— 62 张卡的数据驱动能力标记
    ///   （如 <c>CanMoveAndAttackInTheSameTurn</c>）；</item>
    /// <item>其余 bool / int / string 型 CDO 字段（阵营、名称、文案、费用修正等）。</item>
    /// </list>
    ///
    /// <para>
    /// 只灌标量：嵌套结构 / 数组在这里没有蓝图侧的读取者，灌进去也只是占内存。
    /// 引擎权威的字段（attack/defense/location/关键字…）会在每次读成员时被
    /// <see cref="RefreshMirror"/> 覆盖，所以重复灌不会有害。
    /// </para>
    /// </summary>
    private static void SeedCdoFields(KObj k, CardDef def)
    {
        if (def?.Raw is null) return;
        foreach (var kv in def.Raw)
        {
            switch (kv.Value.ValueKind)
            {
                case System.Text.Json.JsonValueKind.True: k.Set(kv.Key, Val.True); break;
                case System.Text.Json.JsonValueKind.False: k.Set(kv.Key, Val.False); break;
                case System.Text.Json.JsonValueKind.Number:
                    if (kv.Value.TryGetInt32(out var n)) k.Set(kv.Key, Val.Of(n));
                    break;
                case System.Text.Json.JsonValueKind.String:
                    var s = kv.Value.GetString();
                    if (!string.IsNullOrEmpty(s)) k.Set(kv.Key, Val.Of(s));
                    break;
            }
        }
    }

    public Card Card_(Val v)
    {
        if (v.O is KObj k && _byObj.TryGetValue(k, out var c)) return c;
        if (v.K == VKind.Int) return Engine.S.FindCard((int)v.AsInt());
        return null;
    }

    /// <summary>
    /// 从镜像对象上取数值，**静态卡也认**。
    ///
    /// <para>
    /// 静态卡（<see cref="ObjStatic"/> 造的，如老兵版本）没有对应的 <see cref="Card"/> 实体，
    /// <see cref="Card_"/> 对它返回 null。如果所有读数值的原语都走 <c>Card_</c>，
    /// 老兵版本就会读出一堆 0 —— <c>MakeVeteran</c> 会把攻防清成 0，
    /// 而且不报错。
    /// </para>
    ///
    /// <para>
    /// 所以数值读取统一走这里：先按实体卡取，取不到就退回镜像字段本身。
    /// </para>
    /// </summary>
    private static int Num(Val v, string field, int dflt = 0)
    {
        if (v.K == VKind.Nothing) return dflt;
        var f = v.O is KObj k ? k.Get(field) : Val.Nothing;
        return f.K == VKind.Nothing ? dflt : (int)f.AsInt();
    }

    private static bool Flag(Val v, string field)
    {
        if (v.K == VKind.Nothing) return false;
        var f = v.O is KObj k ? k.Get(field) : Val.Nothing;
        return f.AsBool();
    }

    private int _staticSeq = -1;

    /// <summary>
    /// 造一个「静态卡」镜像对象：只当数值来源，不代表场上的实体
    /// （典型用途是老兵版本卡：<c>MakeVeteran</c> 从它身上抄攻防与关键字）。
    ///
    /// <para>
    /// 用负数 instanceId 起号，保证不与真实卡（&gt;0）撞上 ——
    /// 撞上的话 <see cref="Card_"/> 会把静态卡解析成场上的另一张牌。
    /// </para>
    /// </summary>
    public KObj ObjStatic(CardDef def)
    {
        if (def is null) return null;
        if (_staticCache.TryGetValue(def.Id, out var cached)) return cached;

        var k = new KObj("BaseCardObject_C", _staticSeq--);
        k.Set("cardID", Val.Of(k.Id));
        k.Set("side", Val.Of(0));
        k.Set("cardFunction", Val.Ref(CardFunctions));
        k.Set("GameStateRef", Val.Ref(GameStateRef));
        k.Set("CardFunctionsNotifier", Val.Ref(Notifier));
        k.Set("defId", Val.Of(def.Id));
        k.Set("name", Val.Of(def.Id));
        k.Set("attack", Val.Of(def.Attack));
        k.Set("defense", Val.Of(def.Defense));
        k.Set("kredits", Val.Of(def.Kredits));
        k.Set("operationCost", Val.Of(def.OperationCost));
        k.Set("heavyArmor", Val.Of(def.HeavyArmor));
        k.Set("range", Val.Of(def.Range));
        k.Set("type", Val.Of(ClientType(def.Type)));
        k.Set("rarity", Val.Of((int)def.Rarity));
        k.Set("customName", Val.Of(""));
        SeedCdoFields(k, def);
        WriteKeywords(k, def.Keywords);
        _staticCache[def.Id] = k;
        _staticById[k.Id] = k;
        return k;
    }

    private readonly Dictionary<string, KObj> _staticCache = new(StringComparer.Ordinal);

    /// <summary>静态卡的负数 instanceId → KObj。用于让 <c>GetCardFromID</c> 找回静态卡。</summary>
    private readonly Dictionary<int, KObj> _staticById = new();

    /// <summary>
    /// 把关键字位写成蓝图读的 <c>has*</c> 布尔字段。
    /// 直译产物不做位运算，它读的就是这些具名字段，所以必须逐项写出来。
    /// </summary>
    private static void WriteKeywords(KObj k, Kw kw)
    {
        k.Set("hasGuard", Val.Of(kw.HasFlag(Kw.Guard)));
        k.Set("hasBlitz", Val.Of(kw.HasFlag(Kw.Blitz)));
        k.Set("hasSmokescreen", Val.Of(kw.HasFlag(Kw.Smokescreen)));
        k.Set("hasAmbush", Val.Of(kw.HasFlag(Kw.Ambush)));
        k.Set("hasMobilize", Val.Of(kw.HasFlag(Kw.Mobilize)));
        k.Set("hasFury", Val.Of(kw.HasFlag(Kw.Fury)));
        k.Set("hasCovert", Val.Of(kw.HasFlag(Kw.Covert)));
        k.Set("hasVeteran", Val.Of(kw.HasFlag(Kw.Veteran)));
        k.Set("hasDeployment", Val.Of(kw.HasFlag(Kw.Deployment)));
        k.Set("hasHeavyArmor", Val.Of(kw.HasFlag(Kw.HeavyArmor)));
        // Shock / Immune 也是 CDO 的 has* 字段，且直译产物里有直接读镜像的地方
        // （`H.GetMember(card, "hasShock")`），漏写会让 Shock 判定恒假。
        k.Set("hasShock", Val.Of(kw.HasFlag(Kw.Shock)));
        k.Set("hasImmune", Val.Of(kw.HasFlag(Kw.Immune)));
        k.Set("hasPincer", Val.Of(kw.HasFlag(Kw.Pincer)));
    }

    /// <summary>
    /// 镜像字段名 → 关键字位。用于把蓝图对 <c>has*</c> 的写入落到卡牌实体上。
    ///
    /// <para>
    /// <b>为什么必须拦</b>：蓝图给单位加关键字是直接写镜像成员的
    /// （<c>H.SetMember(card, "hasShock", true)</c>）。而 <see cref="RefreshMirror"/>
    /// 每次读成员都会用实体关键字回填镜像 —— 只写镜像的话，下一次读就被覆盖掉，
    /// 表现是「给了 Shock/Ambush，但一点效果都没有」，且不报错。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Kw> KeywordFields = new(StringComparer.Ordinal)
    {
        ["hasGuard"] = Kw.Guard,
        ["hasBlitz"] = Kw.Blitz,
        ["hasSmokescreen"] = Kw.Smokescreen,
        ["hasAmbush"] = Kw.Ambush,
        ["hasMobilize"] = Kw.Mobilize,
        ["hasFury"] = Kw.Fury,
        ["hasCovert"] = Kw.Covert,
        ["hasDeployment"] = Kw.Deployment,
        ["hasHeavyArmor"] = Kw.HeavyArmor,
        ["hasShock"] = Kw.Shock,
        ["hasImmune"] = Kw.Immune,
        ["hasPincer"] = Kw.Pincer,
    };

    /// <summary>
    /// 把引擎的位置翻译成客户端的 <c>ECardLocationEnum</c>。
    ///
    /// <para>
    /// 两套枚举<b>不是一回事</b>，这是接客户端规则时最容易搞错的地方：
    /// 引擎的 <see cref="Loc"/> 只说「在哪」（Board/Hand/Deck…），
    /// 而客户端的 location 把<b>哪一侧、哪一行</b>一起编码进去了：
    /// </para>
    ///
    /// <code>
    /// 0 NotAvailable  1 Deck_Left   2 Deck_Right
    /// 3 Hand_Left     4 Hand_Right
    /// 5 Board_HQLeft  6 Board_HQRight  7 Board_Frontline
    /// 8 Discard       9 Deck
    /// </code>
    ///
    /// <para>
    /// 客户端规则库靠这个值区分敌我行与前线，写错会让规则整体判错。
    /// </para>
    /// </summary>
    public static int ClientLocation(Card c)
    {
        if (c is null || c.Destroyed) return 0;   // NotAvailable
        var left = c.Owner == Core.Side.Left;
        return c.Loc switch
        {
            Core.Loc.Deck => left ? 1 : 2,
            Core.Loc.Hand => left ? 3 : 4,
            Core.Loc.Frontline => 7,
            Core.Loc.Board => left ? 5 : 6,
            Core.Loc.Discard => 8,
            _ => 0,
        };
    }

    /// <summary>
    /// 把引擎的 <see cref="CardType"/> 翻译成客户端的 <c>ETypeEnum</c> 数值。
    ///
    /// <para>
    /// 两套编号不一样，而直译产物里读镜像 <c>type</c> 的地方按客户端编号比较 ——
    /// 例如规则库 <c>cardsCheckFunctions</c> 里写的是 <c>type == 4</c>（客户端 fighter），
    /// 而我们的 <c>CardType.Bomber</c> 恰好也是 4。直接写 <c>(int)c.Type</c> 的话，
    /// 「战斗机保护」这条规则会被套到轰炸机身上，且不报错。
    /// </para>
    /// </summary>
    public static int ClientType(CardType t) => t switch
    {
        CardType.Location => 1,
        CardType.Order => 2,
        CardType.Tank => 3,
        CardType.Fighter => 4,
        CardType.Bomber => 5,
        CardType.Infantry => 6,
        CardType.Artillery => 7,
        CardType.AntiAir => 8,
        CardType.Gotcha => 11,
        _ => 0,
    };

    /// <summary>
    /// 客户端规则库要读的字段里，有一部分引擎侧才是权威（位置、Guard 掩护状态、
    /// 本回合攻击次数…）。这里按需回填，避免镜像里的值过期。
    /// </summary>
    private void RefreshMirror(KObj k)
    {
        // BP_Logic 的 myRealSide 也要跟着当前行动方走，
        // 否则规则库会按「上一回合的阵营」判限制（IsThereGameplayRestriction 用它）。
        if (ReferenceEquals(k, Logic)) k.Set("myRealSide", Val.Of((int)Engine.S.Current));
        if (!_byObj.TryGetValue(k, out var c)) return;
        k.Set("location", Val.Of(ClientLocation(c)));
        k.Set("locationNumber", Val.Of(c.LocationNumber));
        k.Set("isBeingGuarded", Val.Of(c.IsBeingGuarded));

        // 客户端规则库读的其余卡面字段。它们原本只在 Obj() 里写一次，
        // 之后的增益/关键字变更不会反映过去 —— 而未回填的字段读出来是 none，
        // 规则库把它当 0/false 用，判定就静默走偏（不是报错）。
        // 所以凡是规则库会读的，都要在这里按引擎权威值回填。
        k.Set("range", Val.Of(c.Range));
        k.Set("type", Val.Of(ClientType(c.Type)));
        k.Set("attack", Val.Of(c.Attack));
        k.Set("defense", Val.Of(c.Defense));
        k.Set("kredits", Val.Of(c.KreditCost));
        k.Set("heavyArmor", Val.Of(c.HeavyArmor));
        k.Set("enterPlayOnTurn", Val.Of(c.EnterPlayTurn));
        k.Set("side", Val.Of((int)c.Owner));

        // 行动次数：引擎侧用两个布尔建模，客户端读的是计数/标志位，这里换算过去。
        // 规则库的 has_already_attacked 读 attackCountThisTurn，
        // 卡牌逻辑的移动/攻击检查读 attackLeft / movementLeft。
        k.Set("attackCountThisTurn", Val.Of(c.AttackedThisTurn ? 1 : 0));
        k.Set("hasAttackedThisTurn", Val.Of(c.AttackedThisTurn));
        k.Set("hasEverAttacked", Val.Of(c.AttackedThisTurn));
        k.Set("attackLeft", Val.Of(c.AttackedThisTurn ? 0 : 1));
        k.Set("movementLeft", Val.Of(c.MovedThisTurn ? 0 : 1));
        // 反制指令的激活序号（>0 = 已激活），客户端拿它当 activeGotchas 的键。
        k.Set("gotchaActivated", Val.Of(c.GotchaActivated));
        // 必须用 c.Keywords（实体当前值），不能用 c.Def.Keywords（卡面静态值）：
        // 关键字会被效果动态增删（CampaignAddGuard / 给单位 Shock …），
        // 用静态值回填会把刚加上的关键字冲掉。
        WriteKeywords(k, c.Keywords);
    }

    /// <summary>
    /// 把蓝图对 <c>has*</c> 关键字的写入落到卡牌实体的 <see cref="Card.Keywords"/> 上，
    /// 其余成员仍走镜像。见 <see cref="KeywordFields"/> 的说明。
    /// </summary>
    public override void SetMember(Val obj, string property, Val v)
    {
        var o = Resolve(obj);
        if (o is not null && KeywordFields.TryGetValue(property, out var kw) && _byObj.TryGetValue(o, out var c))
        {
            if (v.AsBool()) c.Keywords |= kw; else c.Keywords &= ~kw;
            WriteKeywords(o, c.Keywords);
            return;
        }
        base.SetMember(obj, property, v);
    }

    /// <summary>
    /// 把蓝图的原地属性写回引擎对象。蓝图的 attack/defense 是「基础值 + buff」，
    /// 而引擎把它们分开存（Attack 基础值、BuffAttack 临时加成），所以这里要分开处理。
    /// </summary>
    public void SyncBack(KObj k)
    {
        if (!_byObj.TryGetValue(k, out var c)) return;
        if (k.Has("attack")) c.Attack = (int)k.Get("attack").AsInt();
        if (k.Has("defense")) c.Defense = (int)k.Get("defense").AsInt();
        if (k.Has("kredits")) c.KreditCost = (int)k.Get("kredits").AsInt();
    }

    // ===================== 出牌管线 =====================

    /// <summary>
    /// 打出卡牌的效果入口：调卡牌自己的 <c>OnPlayedFromHand(targetCard)</c>。
    ///
    /// <para>
    /// <b>为什么必须单独接一个入口</b>：卡牌效果的主入口叫 <c>OnPlayedFromHand</c>
    /// （1638 张卡里 942 张有它），但它不是 <c>ERegisteredCardFunction</c> 的成员，
    /// 所以按 Trigger 枚举名找函数的触发分发永远找不到它 —— 引擎原来把它写成
    /// <c>RunCardEffect(c, Trigger.NotAvailable)</c>，而 <c>CardDispatch.Fire</c>
    /// 对 <c>NotAvailable</c> 是直接 return false，于是这一步是空操作。
    /// </para>
    ///
    /// <para>
    /// 客户端走的是 <c>BP_CardFunctions.CardPlayedFromHand</c> → <c>OnPlayedFromHand</c>；
    /// 无头模拟里出牌流程是引擎自己实现的，就必须自己补上这一步，
    /// 否则所有 order（691 张）与部署效果（241 张）都不执行 —— 而且是静默的。
    /// </para>
    /// </summary>
    public bool PlayCardFromHand(Card played, int targetCardId)
    {
        if (played?.Id is null) return false;
        var fn = FnIndex.Find(played.Id, "OnPlayedFromHand");
        if (fn is null) return false;

        var target = targetCardId > 0 ? Engine.S.FindCard(targetCardId) : null;
        SetCurrentTarget(played, target);
        ResetBudget();
        try
        {
            fn(this, Val.Ref(Obj(played)), new Val[] { target is null ? Val.Nothing : Val.Ref(Obj(target)) });
            SyncBack(Obj(played));
            return true;
        }
        catch (EffectBudgetException) { NoteBudgetTrip(); return true; }
        finally { SetCurrentTarget(played, null); }
    }

    /// <summary>
    /// 设置「本次出牌选中的目标」。客户端把选择存在卡上的 <c>currentTarget</c>，
    /// 卡牌逻辑经 <c>GetTargetedCard</c> 读它 —— 出牌前必须写对，
    /// 否则需要目标的卡会把目标读成空。
    /// </summary>
    public void SetCurrentTarget(Card c, Card target)
    {
        var k = Obj(c);
        if (k is not null) k.Set("currentTarget", target is null ? Val.Nothing : Val.Ref(Obj(target)));
    }

    /// <summary>这张卡有没有自己的出牌合法性判定（有就说明它可能需要目标）。</summary>
    public bool HasCanPlayFromHand(Card c) => c?.Id is not null && FnIndex.Find(c.Id, "CanPlayFromHand") is not null;

    /// <summary>
    /// 问卡牌自己「打向这个目标合不合法」（客户端 <c>CanPlayFromHand</c>）。
    ///
    /// <para>
    /// 判定完全交给卡牌自己的实现（例如 ambush 要求目标是单位），引擎不重写一份 ——
    /// 与攻击目标判定走 <c>cardsCheckFunctions.CanAttack</c> 是同一原则。
    /// 卡上没有这个函数时返回 true（表示不需要目标）。
    /// </para>
    /// </summary>
    public bool CanPlayFromHand(Card c, Card target, out string reason)
    {
        reason = null;
        if (c?.Id is null) return false;
        var fn = FnIndex.Find(c.Id, "CanPlayFromHand");
        if (fn is null) return true;

        var k = Obj(c);
        var prev = k.Get("currentTarget");
        SetCurrentTarget(c, target);
        ResetBudget();
        try
        {
            var can = false;
            var why = Val.Nothing;
            fn(this, Val.Ref(k), new Val[]
            {
                Val.Out(v => can = v.AsBool()),
                Val.Out(v => why = v),
                Val.Out(_ => { }), Val.Out(_ => { }), Val.Out(_ => { }),
            });
            reason = why.AsStr();
            return can;
        }
        catch (EffectBudgetException) { NoteBudgetTrip(); return false; }
        catch (Exception ex)
        {
            var key = $"CanPlayFromHand 抛异常: {ex.GetType().Name}";
            Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
            return true;
        }
        finally { k.Set("currentTarget", prev); }
    }

    /// <summary>
    /// 通知蓝图「这张牌即将从手牌移到 newLocation」。
    ///
    /// <para>
    /// 客户端在 <c>CardLocationMoved</c> 的第一步调 <c>SetRightLeftMostWhenPlayed</c>，
    /// 记下「打出时是不是最左/最右」，有 10 张卡依赖这个标记
    /// （"Deployment: … if deployed from left-most in hand" 之类）。
    /// 引擎自己实现了出牌流程，就得自己补这次通知。
    /// </para>
    ///
    /// <para>
    /// <b>必须在牌离开手牌之前调</b>：那个函数的第一道守卫是
    /// <c>card.location == 3/4</c>（还在自己手里），移出手牌之后再调永远不成立。
    /// </para>
    /// </summary>
    public void NotifyCardLocationChanged(Card c, int newLocation)
    {
        if (c?.Id is null) return;
        var fn = FnIndex.Find("BP_CardFunctions", "SetRightLeftMostWhenPlayed");
        if (fn is null) return;
        try { fn(this, Val.Ref(CardFunctions), new Val[] { Val.Ref(Obj(c)), Val.Of(newLocation) }); }
        catch (EffectBudgetException) { NoteBudgetTrip(); }
        catch (Exception ex)
        {
            var key = $"SetRightLeftMostWhenPlayed 抛异常: {ex.GetType().Name}";
            Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }

    /// <summary>
    /// 结算一次「选牌」回调：调发起卡自己的
    /// <c>OnHandTargetSelected(handTargetCardID, instigatorID)</c>。
    ///
    /// <para>
    /// 这是客户端 UI 在玩家点完之后做的事，无头模拟里由引擎代劳
    /// （见 <see cref="Engine.PendingChoice"/>）。回调里可能再发起下一次选择
    /// （例如「Develop 两次」），所以调用方不能假设一次就结束。
    /// </para>
    /// </summary>
    public bool ResolveHandTargetSelected(Card source, int chosenCardId)
    {
        if (source?.Id is null) return false;
        var fn = FnIndex.Find(source.Id, "OnHandTargetSelected");
        if (fn is null)
        {
            // 这张卡没有自己的回调。和 Handle 里同一套判据：
            // 「某个资产定义过这个名字」→ 只是这张卡没实现（蓝图里调用未实现的
            // BlueprintImplementableEvent 就是空操作），不该记成宿主缺口。
            // 客户端这类卡（例如 card_unit_hampshire_regiment）的 Develop 结果是**UI 直接做的**，
            // 无头模拟里没有 UI，所以效果不落地 —— 这是已知偏差，不是崩溃。
            if (!KnownAssetFunctions.Contains("OnHandTargetSelected"))
            {
                var k1 = "OnHandTargetSelected 无任何实现";
                Unhandled[k1] = Unhandled.TryGetValue(k1, out var c1) ? c1 + 1 : 1;
            }
            return false;
        }

        ResetBudget();
        try
        {
            fn(this, Val.Ref(Obj(source)), new Val[] { Val.Of(chosenCardId), Val.Of(0) });
            SyncBack(Obj(source));
            return true;
        }
        catch (EffectBudgetException) { NoteBudgetTrip(); return true; }
        catch (Exception ex)
        {
            var key = $"OnHandTargetSelected 抛异常: {ex.GetType().Name}";
            Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
            return true;
        }
    }

    /// <summary>
    /// 卡上有没有 <c>CanMoveAndAttackInTheSameTurn</c> 能力标记。
    ///
    /// <para>
    /// 标记由卡自己用 <c>CustomName1Add</c> 写（token 字符串直接取自
    /// <c>card_event_lightning_conquest_new</c> 与 BP_CardFunctions 的调用点，不是猜的），
    /// 也可能直接初始化在 CDO 的 <c>customName1</c> 里
    /// （例如 <c>card_unit_panzergrenadier</c>）。
    /// </para>
    /// </summary>
    public bool HasMoveAndAttackToken(Card c)
    {
        if (c?.Id is null) return false;
        var v = Obj(c).Get("customName1").AsStr();
        return v.Contains("CanMoveAndAttackInTheSameTurn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 设置本次出牌的「三选一」分支。必须在效果体执行之前写 ——
    /// 卡牌逻辑是同步的，没有挂起/恢复机制，所以选择只能是**出牌前**的决策。
    /// </summary>
    public void SetChooseOne(Card c, int branch)
    {
        var k = Obj(c);
        if (k is not null) k.Set("ChooseOne", Val.Of(branch));
    }

    /// <summary>这张卡是不是「三选一」卡（卡池里 48 张，每张 2 个分支）。</summary>
    public static int ChooseOneCount(Card c) => c?.Def?.ChooseOneCards?.Count ?? 0;

    /// <summary>打出带 Intel 的卡时翻对手手牌并通知响应者。
    ///
    /// <para>
    /// 客户端在 <c>CardPlayedFromHand</c> 里做这件事，而那条链路引擎不走
    /// （<c>CardFunctionTriggers</c> 从没被填充过），所以引擎必须自己补一次。
    /// 不做的话 Intel 卡与 <c>OnIntelTriggered</c> 响应者在对局里全是死的。
    /// </para>
    /// </summary>
    public void ApplyIntel(Card played)
    {
        if (played is null || played.Cipher <= 0) return;
        Intel(this, new Val[] { Val.Nothing, Val.Of(played.Cipher), Val.Of(played.InstanceId) });
    }

    /// <summary>
    /// 用客户端自己的 <c>CalculateDamageDealt</c> 结算一次战斗伤害。
    ///
    /// <para>
    /// 引擎原来手写了一份 <c>GameEngine.CalculateDamage</c>，但那份没有 Shock、
    /// 伏击、免疫，也不走伤害修正触发（id=37）—— 而客户端公式就在转译产物里，
    /// 直接调它才是忠实的。返回 false 表示拿不到客户端实现，调用方回退手写版本。
    /// </para>
    /// </summary>
    public bool TryComputeCombatDamage(Card atk, Card def,
        out int toDefender, out int toAttacker, out bool defenderDies, out bool attackerDies)
    {
        toDefender = toAttacker = 0; defenderDies = attackerDies = false;
        if (atk is null || def is null) return false;
        var fn = FnIndex.Find("BP_CardFunctions", "CalculateDamageDealt");
        if (fn is null) return false;

        try
        {
            toDefender = One(fn, atk, def, true, out defenderDies);
            toAttacker = One(fn, def, atk, false, out attackerDies);
            return true;
        }
        catch (EffectBudgetException) { NoteBudgetTrip(); return false; }
        catch (Exception ex)
        {
            var key = $"CalculateDamageDealt 抛异常: {ex.GetType().Name}";
            Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
            return false;
        }

        int One(Func<IHost, Val, Val[], Val> f, Card dealer, Card reciever, bool dealerIsAttacker, out bool dies)
        {
            var dmg = 0; var dead = false;
            f(this, Val.Ref(CardFunctions), new Val[]
            {
                Val.Ref(Obj(dealer)),        // damageDealerCard
                Val.Ref(Obj(reciever)),      // damageRecieverCard
                Val.Of(dealerIsAttacker),    // damageDealerIsAttacker
                Val.False,                   // ignoreAmbush
                Val.False,                   // ignoreHeavyArmor
                Val.True,                    // applyBeforeAttackBuffs（与 BP_Logic 的调用一致）
                Val.Out(v => dmg = (int)v.AsInt()),
                Val.Out(v => dead = v.AsBool()),
                Val.Out(_ => { }),           // damageRecieverKilledBeforeAattack
                Val.Out(_ => { }),           // wasShockAttack
            });
            dies = dead;
            return dmg;
        }
    }

    // ===================== 分派 =====================

    /// <summary>
    /// 读成员前先把引擎侧的权威值回填进镜像 —— 客户端的规则库
    /// （<c>cardsCheckFunctions</c>）读的就是这些字段，不回填它会一直读到空值，
    /// 表现是规则静默失效（Guard 就是这么被发现整条不生效的）。
    /// </summary>
    public override Val GetMember(Val obj, string property)
    {
        var o = Resolve(obj);
        if (o is null) return Val.Nothing;
        RefreshMirror(o);
        return o.Get(property);
    }

    /// <summary>
    /// 三级分派：GameState 原语 → BaseCardObject 原语 → 转译产物递归。
    ///
    /// 顺序很重要：原语必须在最前，否则会被转译产物的同名函数抢走（那些函数最终
    /// 又要调回原语，形成无限递归）。
    /// </summary>
    private Val? Handle(string f, Val[] a)
    {
        // 单次动作的调用预算。卡牌逻辑里有「循环重试直到找到组合」这类循环，
        // 一旦它依赖的数据（DataTable / 平台服务）在无头模拟里不存在，循环就永不退出 ——
        // 整个自对弈会挂死。超预算时抛异常把控制流拉回入口点，由那里记账并继续。
        if (++_calls > CallBudget) { BudgetTripped = true; throw new EffectBudgetException(f); }

        DispatchByClass[f] = DispatchByClass.TryGetValue(f, out var n) ? n + 1 : 1;
        CallTrace?.Invoke(f);

        // 1. 引擎状态原语（真正改 GameState 的叶子）
        //
        // 极少数名字同时存在于两张表里，且**接收者不同**：一个是卡上的原生
        // 成员函数，一个是规则库的导出静态函数。例如 CanOtherCardBeTargetted：
        //   卡版（8 个实参）：{ card, out,out,out,out, targettingCard, targetCard, byPlayFromHand }
        //   库版（9 个实参）：{ 库, cardTargetting, targetCard, byPlayFromHand, __WorldContext, out,out,out,out }
        // 只按名字查表会把库版调用错路由到卡版实现上。所以这类名字按实参个数
        // 分流：个数对不上就放行给下面的接收者分派（最终到规则库）。
        if (Primitives.TryGetValue(f, out var impl))
        {
            if (!PrimitiveArity.TryGetValue(f, out var arity) || a.Length == arity) return impl(this, a);
        }

        // 2. 接收者判定
        var recv = a.Length > 0 ? a[0] : Val.Nothing;

        // 调用约定的差异必须在这里抹平：
        // H.Call 发出来的实参数组是 {接收者, 真实参数...}，
        // 而直译产物的函数体按 args[0] 开始绑定自己的形参（接收者单独走 self 形参）。
        // 所以转进去之前要去掉第 0 位；out 回写也跟着一起对齐。
        var rest = a.Length > 0 ? a[1..] : a;

        // 重入防护：同一个函数名在栈上连续出现 8 次就判定为转发壳死循环。
        // 蓝图的转发壳（A.Foo 里调 B.Foo，B 里又没有 Foo 于是落回 A.Foo）
        // 在静态分派下会无限递归；栈溢出会把整个进程带走，比记一笔未实现糟糕得多。
        _depth.TryGetValue(f, out var depth);
        depth++;
        _depth[f] = depth;
        try
        {
            if (depth > 8)
            {
                const string key = "(转发壳递归, 已截断)";
                Unhandled[key] = Unhandled.TryGetValue(key, out var d) ? d + 1 : 1;
                if (Verbose) Log($"转发壳递归截断: {f}");
                return null;   // 记为未处理，交给上层继续
            }

            var r = Dispatch(f, a, recv, rest);

            // 卡牌原生事件的默认体优先于「这张卡没实现」的判定：
            // 没覆盖不等于没实现，默认体必须执行（见 CardEventDefaults）。
            if (r is null && CardEventDefaults.TryGetValue(f, out var dflt)) return dflt(this, a);

            // Dispatch 返回 null 表示没人接。但要区分两件完全不同的事：
            //
            //   (a) 这个名字压根不是任何蓝图函数 → 真的缺宿主实现，必须报出来；
            //   (b) 名字是某个资产的函数，只是**这个接收者**（某张卡）没实现它
            //       —— 蓝图里调用一个没实现的 BlueprintImplementableEvent 本来就是空操作。
            //
            // 不分的话 (b) 会淹没 (a)：例如 GainKreditSlot 会对场上每张卡调
            // OnAfterExtraKreditSlotGain，而只有少数卡实现了它 —— 每张没实现的卡
            // 都记一笔「未实现宿主调用」，smoke 因此常年刷红，真正的缺口反而看不见。
            if (r is null && KnownAssetFunctions.Contains(f)) return Val.Nothing;
            return r;
        }
        finally
        {
            if (--depth <= 0) _depth.Remove(f);
            else _depth[f] = depth;
        }
    }

    /// <summary>同名调用的当前递归深度（转发壳防护用）。</summary>
    private readonly Dictionary<string, int> _depth = new(StringComparer.Ordinal);

    /// <summary>
    /// 单次效果调用的宿主调用预算（见 <see cref="Handle"/> 的说明）。
    /// 典型动作在千次量级，20 万是很宽的界 —— 只有死循环会碰到它。
    /// </summary>
    public int CallBudget = 200_000;

    private long _calls;

    /// <summary>本次效果调用是否因超预算被中断。</summary>
    public bool BudgetTripped { get; private set; }

    /// <summary>进入一个顶层效果调用前重置预算。</summary>
    public void ResetBudget() { _calls = 0; BudgetTripped = false; }

    /// <summary>记一次「效果超预算被中断」。这类中断意味着有卡牌效果跑不完，必须留痕。</summary>
    public void NoteBudgetTrip()
    {
        const string key = "(效果调用超预算, 已中断)";
        Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    /// <summary>
    /// 所有直译资产定义过的函数名并集。
    /// 用来区分「真的缺宿主实现」和「这个接收者没实现该事件」，见 <see cref="Handle"/>。
    /// </summary>
    private static readonly HashSet<string> KnownAssetFunctions = BuildKnownAssetFunctions();

    private static HashSet<string> BuildKnownAssetFunctions()
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in FnIndex.Assets)
            foreach (var n in kv.Value.Keys) s.Add(n);
        return s;
    }

    /// <summary>
    /// 「卡牌原生事件」的默认实现 —— 卡没有覆盖这个函数时该返回什么。
    ///
    /// <para>
    /// 这类函数是 <c>BlueprintNativeEvent</c>：C++ 侧有默认函数体，蓝图**可选**覆盖。
    /// 宿主在卡没覆盖时返回 <c>Nothing</c> 是错的 —— 那等于把默认体丢掉。
    /// 最典型的是 <c>OnCardDealDamage_ModifyDamageDealt</c>：24 张卡覆盖了它，
    /// 其余 1614 张没有；默认体是「不修改伤害」，返回 Nothing 会让伤害变成 0，
    /// 于是**所有战斗都不掉血**。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Func<EngineHost, Val[], Val?>> CardEventDefaults =
        new(StringComparer.Ordinal)
        {
            // void OnCardDealDamage_ModifyDamageDealt(UBaseCardObject* toCard, int32 damage,
            //        bool fromAttack, bool fromFight, int32& newDamage)
            // 实参（含接收者）：a[0]=接收者(这张卡) a[1]=toCard a[2]=damage
            //                  a[3]=fromAttack a[4]=fromFight a[5]=out newDamage
            ["OnCardDealDamage_ModifyDamageDealt"] = (h, a) =>
            {
                Val.TrySetOut(a[^1], a.Length > 2 ? a[2] : Val.Nothing);
                return Val.Nothing;
            },
        };

    /// <summary>
    /// 规则库兜底：<c>cardsCheckFunctions</c> 是一组**全局静态**导出函数，
    /// 蓝图里既可能不带接收者调，也可能拿 <c>cardFunction</c> 当接收者调。
    /// 所以 2a/2b/2c 在自己查不到时，必须**先问它再放弃** ——
    /// 否则规则库的导出函数只在「接收者为空」这一种情况下可达，
    /// 表现为 CanAttack 能跑、但它内部以 cardFunction 为接收者调的
    /// CanOtherCardBeTargetted 静默失效（原因字符串为空、canAttack=false）。
    ///
    /// <c>FnIndex.Find</c> 只命中规则库真实导出的那几个名字，不会抢别的函数。
    /// </summary>
    private Val? RuleLibraryFallback(string f, Val[] rest)
    {
        var rule = FnIndex.Find("cardsCheckFunctions", f);
        // 必须写成显式 if —— 不能写成 `rule is null ? null : rule(...)`。
        // Val 是 struct，那个三元表达式的类型会被推断成 Val（不是 Val?），
        // 于是 null 变成 default(Val) 也就是 Val.Nothing，HasValue=true。
        // 后果是「未命中」被当成「已处理并返回 none」，Host.Call 不再往下走内建表，
        // 所有非原语的内建函数（Not_PreBool / Array_Length / SelectInt / Conv_* …）
        // 全部静默失效。曾因此让 MakeVeteran 的 !IsVeteran 变成空值而整条老兵链断掉。
        if (rule is null) return null;
        return rule(this, Val.Ref(CardFunctions), rest);
    }

    /// <summary>
    /// 调一张卡自己的资产函数，并在返回后把镜像写回实体。
    ///
    /// <para>
    /// <b>为什么必须 SyncBack</b>：卡牌逻辑对 <c>attack</c>/<c>defense</c>/<c>kredits</c>
    /// 是直接写镜像成员的，而 <see cref="RefreshMirror"/> 每次读成员都会用实体值回填镜像。
    /// 调用点若不同步回实体，这次写入会在下一次读时被冲掉 ——
    /// 经 <c>CardDispatch.Fire</c> 进来的触发有做同步，而蓝图侧直接
    /// <c>H.Call("OnOtherCardXxx", …)</c> 进来的这条路径以前漏了。
    /// </para>
    /// </summary>
    private Val RunCardFn(Func<IHost, Val, Val[], Val> fn, KObj card, Val recv, Val[] rest)
    {
        var r = fn(this, recv, rest);
        SyncBack(card);
        return r;
    }

    private Val? Dispatch(string f, Val[] a, Val recv, Val[] rest)
    {

        // 2a. cardFunction.* → 转译出来的 BP_CardFunctions（它自己还会继续 H.Call）
        if (recv.O is KObj cf && ReferenceEquals(cf, CardFunctions))
        {
            var fn = FnIndex.Find("BP_CardFunctions", f);
            if (fn is not null) return fn(this, Val.Ref(CardFunctions), rest);

            var fn2 = FnIndex.Find("BP_GameState_Battle", f);
            if (fn2 is not null) return fn2(this, Val.Ref(CardFunctions), rest);
            return RuleLibraryFallback(f, rest);
        }

        // 2b. GameStateRef.* → 转译出来的 BP_GameState_Battle
        if (recv.O is KObj gs && ReferenceEquals(gs, GameStateRef))
        {
            var fn = FnIndex.Find("BP_GameState_Battle", f);
            if (fn is not null) return fn(this, Val.Ref(GameStateRef), rest);
            return RuleLibraryFallback(f, rest);
        }

        // 2c. 卡牌成员：先查卡自己的资产（事件与 ubergraph），再查 BP_CardFunctions
        if (recv.O is KObj co)
        {
            // 事件存根转进 ubergraph 用的是 EX_LocalFinalFunction，
            // 接收者就是这张卡自己，而函数名里带着资产名，可以直接定位。
            if (f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
            {
                var uber = FnIndex.Find(f["ExecuteUbergraph_".Length..], f);
                if (uber is not null) return RunCardFn(uber, co, recv, rest);
            }

            var assetId = _byObj.TryGetValue(co, out var card) ? card.Id : co.Get("defId").AsStr();
            if (!string.IsNullOrEmpty(assetId))
            {
                var fn = FnIndex.Find(assetId, f);
                if (fn is not null) return RunCardFn(fn, co, recv, rest);
            }
            var shared = FnIndex.Find("BP_CardFunctions", f);
            if (shared is not null) return shared(this, Val.Ref(CardFunctions), rest);
            return RuleLibraryFallback(f, rest);
        }

        // 2d. Notifier：全部 no-op（无头模拟不需要 UI 通知）
        if (recv.O is KObj nt && ReferenceEquals(nt, Notifier)) return Val.Nothing;

        // 2d. UtilityFunctions.* —— 蓝图里以「库引用」当接收者的静态工具函数
        //（GetLogic / GetGameState / FetchAllCardsWithEventTrigger …）。
        // 接收者由 EX_ObjectConst 生成，是 VKind.Obj 里装的一个字符串（不是 VKind.Str），
        // 所以用 AsStr() 取名字，并在 2e 的 null 分支之前接住。
        if (recv.O is string libName && libName.EndsWith("UtilityFunctions", StringComparison.Ordinal))
        {
            var uf = FnIndex.Find(libName, f) ?? FnIndex.Find("UtilityFunctions", f);
            if (uf is not null) return uf(this, Val.Ref(Logic), rest);
            return null;
        }

        // 2e. 没有接收者的静态调用：蓝图里经 GameStateRef / cardFunction 调的
        if (recv.O is null)
        {
            // 客户端的规则库是一组静态函数（<c>cardsCheckFunctions</c>），
            // 在蓝图里不带接收者直接调。不在这里接住的话，
            // CanAttack 内部调的 CanSelectAsTarget 会静默变成空操作 ——
            // 表现是「原因字符串为空、canAttack 为 false」，很难定位。
            var rule = FnIndex.Find("cardsCheckFunctions", f);
            if (rule is not null) return rule(this, Val.Ref(CardFunctions), rest);

            // UtilityFunctions 那一族是静态工具（GetLogic / GetGameState …），
            // 同样不带接收者调。
            var util = FnIndex.Find("UtilityFunctions", f);
            if (util is not null) return util(this, Val.Ref(Logic ?? GameStateRef), rest);

            var fn = FnIndex.Find("BP_GameState_Battle", f);
            if (fn is not null) return fn(this, Val.Ref(GameStateRef), rest);
            var fn3 = FnIndex.Find("BP_CardFunctions", f);
            if (fn3 is not null) return fn3(this, Val.Ref(CardFunctions), rest);
        }

        // 2f. 最后兜底：客户端规则库（cardsCheckFunctions）是**全局静态**函数集，
        //     蓝图里既可能不带接收者调（走 2e），也可能拿 cardFunction 当接收者调
        //     （走 2a）。2a/2b/2c 找不到时若直接 return null，规则库的导出函数就
        //     只在「接收者为空」这一种情况下可达 —— 表现是 CanAttack 能跑，但它
        //     内部以 cardFunction 为接收者调的 CanOtherCardBeTargetted 静默失效。
        //     FnIndex.Find 只命中规则库真实导出的那几个名字，不会抢别的函数。
        return RuleLibraryFallback(f, rest);
    }

    // ===================== 引擎原语 =====================

    /// <summary>引擎原语表里的所有函数名（给完备性审计用）。</summary>
    public static IReadOnlyCollection<string> PrimitiveNames => Primitives.Keys.ToList();

    /// <summary>
    /// 把引擎原语表暴露成只读名字集合。审计要判断「一个调用目标有没有地方接」，
    /// 原语是其中一类接收方，所以需要能问到它的键。
    /// </summary>

    /// <summary>
    /// 真正接触 GameState 的叶子函数。名字取自实测调用频次最高的那批
    /// （见 _transpile-report.txt），按「改状态」优先收录。
    /// 不在表里的调用会被记为未实现，不会静默变成空操作。
    /// </summary>
    /// <summary>
    /// 少数「同名不同重载」的原语：值是该原语接受的实参个数（含接收者）。
    ///
    /// 存在的理由是 <c>CanOtherCardBeTargetted</c> 这类名字：它在 UHT 里是
    /// <c>UBaseCardObject</c> 的原生成员函数，规则库 <c>cardsCheckFunctions</c> 里
    /// 又导出过一个同名静态函数，两者接收者与参数表都不同。只按名字查原语表
    /// 会把库版调用错路由成卡版，所以这里登记卡版的实参个数，
    /// 对不上的实参个数放行给接收者分派。
    /// </summary>
    private static readonly Dictionary<string, int> PrimitiveArity = new(StringComparer.Ordinal)
    {
        ["CanOtherCardBeTargetted"] = 8,
    };

    private static readonly Dictionary<string, Func<EngineHost, Val[], Val?>> Primitives =
        new(StringComparer.Ordinal)
        {
            // ---------- 查询 ----------
            // 注意：这一族在 UHT 里几乎都是 `void X(bool& out)` 纯 out 形参。
            // 写成 `return Val.Of(...)` 的话调用方读的是 out 槽，恒为 false。
            // 客户端规则库的第一步就是 `if (!IsValid(target)) return;`，
            // 所以这里错一个，整条规则就静默走空分支（can=false、reason 为空）。
            // IsValid 是 KismetSystemLibrary 的**按值返回**函数
            //（调用点是 `L[x] = H.Call("IsValid", {库, 对象})`，没有 out 实参）。
            // 所以这里必须 return 值，不能写 out 槽 —— 两者混了就会恒 false。
            ["IsValid"] = (h, a) => Val.Of(h.Card_(a[1]) is not null
                                           || a[1].O is KObj ko && (ko.ClassName.Length > 0 || ko.Fields.Count > 0)),

            ["IsLocatedOnBoard"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Out(a, c is not null && c.Loc is Loc.Board or Loc.Frontline);
            },

            // 下面这几个在 UHT 里都是 `void X(bool& isIt)` —— 纯 out 形参，没有返回值。
            // 曾经写成 `Val.Of(...)` 直接返回，但调用方读的是 out 槽
            // （`CallFunc_IsUnit_isIt`），于是永远读到空值 false。
            // 影响面很大：客户端 CanAttack 的第一步就是
            // `if (!IsUnit(attackerCard)) -> not_a_unit`，
            // 所以整个规则库对所有单位都会回「不能打」。
            //
            // 判断依据是**调用点形态**，不是名字：
            //   out 型   -> `H.Call("X", { ..., Val.Out(...) })`  必须写 out 槽
            //   返回型   -> `L[x] = H.Call("X", { ... })`         必须 return
            // 两者搞反都会静默恒 false。IsValid 就属于返回型（KismetSystemLibrary）。
            ["IsUnit"] = (h, a) => Out(a, h.Card_(a[0])?.IsUnit ?? false),
            ["IsInfantry"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.Infantry),
            ["IsTank"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.Tank),
            ["IsFighter"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.Fighter),
            ["IsBomber"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.Bomber),
            ["IsArtillery"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.Artillery),
            ["IsOrder"] = (h, a) => Out(a, h.Card_(a[0])?.IsOrder ?? false),
            ["IsLocation"] = (h, a) => Out(a, h.Card_(a[0])?.IsLocation ?? false),

            ["GetCardFromID"] = (h, a) =>
            {
                var id = (int)a[1].AsInt();
                var c = h.Engine.S.FindCard(id);
                // 静态卡（GetStaticCard 造的，负数 id）也要能按 id 找回来 ——
                // Develop / 选手牌的第二段回调就是拿着候选卡的 id 回来的。
                var v = c is not null ? Val.Ref(h.Obj(c))
                      : id < 0 && h._staticById.TryGetValue(id, out var sk) ? Val.Ref(sk)
                      : Val.Nothing;
                Val.TrySetOut(a[^1], v);
                return Val.Nothing;
            },

            // UHT: void GetOppositeSide(ESideEnum& oppositeSide)
        // 是 BaseCardObject 的成员，用 this 的阵营取反 —— a[0] 是卡，a[1] 是 out 槽。
        ["GetOppositeSide"] = (h, a) =>
        {
            var s = (Side)(h.Card_(a[0])?.Owner ?? Side.None);
            return Out(a, (int)GameState.Foe(s));
        },
            ["GetSide"] = (h, a) => Val.Of((int)h.Card_(a[0])!.Owner),

            // ---------- 类型 / 属性判定（全部 out 形参，无返回值） ----------
            // 签名取自 UHT：void IsAirUnit(bool& isIt) 这类。
            // 注意 out 槽在实参数组里的位置 —— 有形参的在末尾，无形参的就在 a[1]。
            // IsVeteran 读两处：引擎权威位 + 客户端写进 JSON 的那份
            //（MakeVeteran 用 JSON_SetBool 写 "veteran"，见那里的说明）。
            // 只读一处会在两条写入路径之间漏掉状态。
            ["IsVeteran"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Out(a, c is not null && (c.Veteran || h.Obj(c).Get("j_veteran").AsBool()));
            },
            // getHasVeteranUpgrade 问的是「能不能升级成老兵」，不是「已经是老兵」。
            // 以前错读成 Veteran，导致 MakeVeteran 的守卫
            //   !IsVeteran(card) && getHasVeteranUpgrade(card)
            // 变成 !Veteran && Veteran —— 恒为假，老兵机制整条失效。
            ["getHasVeteranUpgrade"] = (h, a) => Out(a, h.Card_(a[0])?.Def?.HasVeteranUpgrade ?? false),
            ["IsAirUnit"] = (h, a) =>
            {
                var t = h.Card_(a[0])?.Type;
                return Out(a, t is CardType.Fighter or CardType.Bomber);
            },
            // void IsAntiAir(bool& isIt) —— UHT 原生。客户端 ETypeEnum 里有 antiair，
            // 但当前卡池 1906 张里没有任何一张是这个类型，所以实际恒为 false；
            // 仍然按类型判，数据一变就自动生效。
            ["IsAntiAir"] = (h, a) => Out(a, h.Card_(a[0])?.Type == CardType.AntiAir),
            ["IsGroundUnit"] = (h, a) =>
            {
                var t = h.Card_(a[0])?.Type;
                return Out(a, t is CardType.Infantry or CardType.Tank or CardType.Artillery);
            },
            ["IsLocatedInHand"] = (h, a) => Out(a, h.Card_(a[0])?.Loc == Loc.Hand),
            ["IsSameSideUnit"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Out(a, c is not null && c.Owner == (Side)a[1].AsInt());
            },
            ["IsUnrevealedCovertCard"] = (h, a) =>
            {
                // 隐蔽牌在未揭示前对对手不可见。引擎目前没有独立的「已揭示」位，
                // 用 Covert 近似；这是已知偏差，不静默当成 false。
                var c = h.Card_(a[0]);
                return Out(a, c?.Covert == true);
            },
            ["getHasAlpine"] = (h, a) => Out(a, h.Card_(a[0])?.Has(Kw.Guard) == true),
            ["getHasGameplayTag"] = (h, a) =>
            {
                // getHasGameplayTag(inputTag, out hasTag)
                var c = h.Card_(a[0]);
                var tag = a[1].AsStr();
                return Out(a, c is not null && TagMatch(c, tag));
            },
            ["CustomName2HasAttribute"] = (h, a) =>
            {
                // 自定义卡名后缀形如 "name_attr1_attr2"，判断是否含某个属性标记。
                var c = h.Card_(a[0]);
                var attr = a[1].AsStr();
                var name = h.Obj(c)?.Get("customName").AsStr() ?? "";
                return Out(a, name.Contains(attr, StringComparison.OrdinalIgnoreCase));
            },
            // getAndDecrypt* 系列：既要认场上的实体卡，也要认静态卡（老兵版本）。
        // 实体卡走 Card_ 拿实时值，静态卡退回镜像字段。
        ["getAndDecryptAttack"] = (h, a) => Out(a, h.Card_(a[0]) is { } ac ? ac.Attack : Num(a[0], "attack")),
            ["getTotalAttack"] = (h, a) => Out(a, h.Card_(a[0]) is { } tc ? tc.TotalAttack : 0),
            ["getTotalHeavyArmor"] = (h, a) => Out(a, h.Card_(a[0]) is { } hc ? hc.HeavyArmor : Num(a[0], "heavyArmor")),
            ["IsDamaged"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Out(a, c is not null && c.Defense < c.MaxDefense);
            },
            ["IsExile"] = (h, a) => Out(a, false),   // 放逐：引擎暂未建模，恒 false
            ["HasBond"] = (h, a) => Out(a, h.Card_(a[0])?.Has(Kw.Bond) == true),
            ["GetCombatKeywords"] = (h, a) =>
            {
                // GetCombatKeywords(out TArray<ECombatKeyword> Keywords, out int32 numberOfKeywords)
                var kws = new List<Val>();
                var c = h.Card_(a[0]);
                if (c is not null)
                {
                    if (c.Has(Kw.Fury)) kws.Add(Val.Of("Fury"));
                    if (c.Has(Kw.Guard)) kws.Add(Val.Of("Guard"));
                    if (c.Has(Kw.Ambush)) kws.Add(Val.Of("Ambush"));
                    if (c.Has(Kw.Blitz)) kws.Add(Val.Of("Blitz"));
                }
                // 两个 out 槽：数组在前、个数在后
                Val.TrySetOut(a[^2], Val.Ref(new KArr(kws)));
                Val.TrySetOut(a[^1], Val.Of(kws.Count));
                return Val.Nothing;
            },
            // UBaseCardObject::CanBeTargetted —— 卡牌自身的「可被指定为目标」判定。
            //
            // 布局（UHT: void CanBeTargetted(bool& canIt, FString& Reason,
            //        FString& reasonParam1, FString& reasonParam2,
            //        UBaseCardObject* targettingCard, bool byPlayFromHand)）：
            //   a[0]=接收者(被指定的卡) a[1]=out canIt a[2]=out Reason
            //   a[3]=out reasonParam1 a[4]=out reasonParam2
            //   a[5]=targettingCard a[6]=byPlayFromHand
            // 注意：四个 out 槽漏写任何一个，调用方都会读到空值 ——
            // 它的调用点拿 canIt 去和 isSuppressed 做 OR，读不到就等于
            // 「任何卡都不能被指定」，整条选目标链路静默失败。
            //
            // **诚实标注**：这是 BlueprintNativeEvent，真实实现编译在游戏二进制里，
            // 随附的 H:\kards\Source 是只有接口、函数体全空的桩（BaseCardObject.cpp:596
            // 的 CanBeTargetted_Implementation 就是空体）。所以下面这套判定是**按
            // 调用点语义 + 卡面属性词汇推断**的，不是原样移植。默认放行，只对能在
            // 数据里找到依据的限制拦截；发现新的限制条件时应在此处补充。
            ["CanBeTargetted"] = (h, a) =>
            {
                var target = h.Card_(a[0]);
                var by = h.Card_(a[5]);
                var canIt = true;
                var reason = "";
                var p1 = "";
                var p2 = "";

                if (target is not null)
                {
                    // 卡面属性 cantBeTargetedByEnemyOrder：敌方指令牌不能指定它。
                    // （cards.json 里 5 张，串名与规则库的
                    //   cant_be_targeted_by_enemy_orders 对应。）
                    if (by is not null && by.IsOrder && by.Owner != target.Owner
                        && AbilityOf(h, target).Contains("cantBeTargetedByEnemyOrder", StringComparison.OrdinalIgnoreCase))
                    {
                        canIt = false;
                        reason = "cant_be_targeted_by_enemy_orders";
                        p1 = by.Def?.Id ?? "";
                    }
                    // 被抑制的卡没有防御性能力，由调用方 `isSuppressed || canIt` 放行，
                    // 这里不重复拦截。
                }

                Val.TrySetOut(a[1], Val.Of(canIt));
                Val.TrySetOut(a[2], Val.Of(reason));
                Val.TrySetOut(a[3], Val.Of(p1));
                Val.TrySetOut(a[4], Val.Of(p2));
                return Val.Nothing;
            },
            // UBaseCardObject::CanOtherCardBeTargetted —— **卡版**（8 个实参）：
            //   a[0]=接收者(这张卡) a[1..4]=out canIt/Reason/p1/p2
            //   a[5]=targettingCard a[6]=targetCard a[7]=byPlayFromHand
            // 规则库里同名函数（9 个实参）另走接收者分派，见 PrimitiveArity 的说明。
            //
            // 调用点在 CanOtherCardBeTargetted（库版）的循环里：遍历「注册了事件
            // 触发点 2」的卡，任何一张返回 false 就整体否决（L1005 直接跳失败）。
            // 所以这是个**否决位**，默认必须放行，否则所有目标都会被否掉。
            //
            // **诚实标注**：这是 BlueprintNativeEvent，真实实现编译在游戏二进制里
            //（随附 Source 是函数体全空的桩，BaseCardObject.cpp:590 就是空体），
            // 没有任何蓝图覆盖它（Generated 里查不到定义）。因此这里实现为
            // 「不否决」的默认行为，是按调用点语义推断的，不是原样移植。
            ["CanOtherCardBeTargetted"] = (h, a) =>
            {
                Val.TrySetOut(a[1], Val.True);
                Val.TrySetOut(a[2], Val.Of(""));
                Val.TrySetOut(a[3], Val.Of(""));
                Val.TrySetOut(a[4], Val.Of(""));
                return Val.Nothing;
            },
            // 按**客户端位置枚举**取该位置上的所有卡。
            //
            // 实参（含接收者）：a[0]=接收者 a[1]=location
            //   a[2]=out QtyInLocation a[3]=out isLocationFull a[4]=out AllCardsInLocation
            //   a[5]=out FirstCard a[6]=out fetchedCardsIDs
            //
            // **注意是 5 个 out，不是 3 个**。曾经按「数组在 a[^1]」写，结果写进了
            // fetchedCardsIDs，而调用方读的是 AllCardsInLocation —— 拿到空数组，
            // 遍历恒不执行。所以这里按 a[^5..^1] 全部填满，不再依赖个数假设。
            //
            // 为什么必须有：客户端的 UpdateGuarded（掩护重算）就是靠它遍历
            // 支援线上的卡，再按 locationNumber ±1 找相邻的 Guard 单位。
            // 缺了它掩护状态永远算不出来，而且不报错、不进 Unhandled。
            // （UHT 里没有它，是蓝图侧的原生实现，所以只能在这里按语义补。）
            ["FetchCardsByLocation"] = (h, a) =>
            {
                var list = h.CardsInClientLocation((int)a[1].AsInt());
                var vals = list.Select(c => Val.Ref(h.Obj(c))).ToList();
                if (a.Length >= 7)
                {
                    Val.TrySetOut(a[^5], Val.Of(list.Count));
                    Val.TrySetOut(a[^4], Val.False);
                    Val.TrySetOut(a[^3], Val.Ref(new KArr(vals)));
                    Val.TrySetOut(a[^2], vals.Count > 0 ? vals[0] : Val.Nothing);
                    Val.TrySetOut(a[^1], Val.Ref(new KArr(list.Select(c => Val.Of(c.InstanceId)))));
                }
                else
                {
                    // 老的三 out 形态（少数调用点）
                    Val.TrySetOut(a[^3], Val.Of(list.Count));
                    Val.TrySetOut(a[^2], Val.False);
                    Val.TrySetOut(a[^1], Val.Ref(new KArr(vals)));
                }
                return Val.Nothing;
            },
            // UHT: void GetAdjacentCards(UBaseCardObject* card, bool includeCovertCards,
            //                            TArray<UBaseCardObject*>& adjacentCards)
            // 布局：a[0]=接收者 a[1]=card a[2]=includeCovertCards a[3]=out
            //
            // 相邻 = 同一客户端位置内、locationNumber 相差 1。
            // 这是掩护（Guard）判定的核心：UpdateGuarded 对每张**没有** Guard 的卡
            // 调它找邻居，邻居里有 Guard 就把这张卡标成 isBeingGuarded。
            // 缺了它这个查找恒为空，掩护永远算不出来，而且不报错、不进 Unhandled。
            ["GetAdjacentCards"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                var includeCovert = a[2].AsBool();
                var res = new List<Val>();
                if (c is not null)
                {
                    foreach (var o in h.CardsInClientLocation(ClientLocation(c)))
                    {
                        if (ReferenceEquals(o, c)) continue;
                        if (Math.Abs(o.LocationNumber - c.LocationNumber) != 1) continue;
                        if (!includeCovert && o.Covert) continue;
                        res.Add(Val.Ref(h.Obj(o)));
                    }
                }
                Val.TrySetOut(a[^1], Val.Ref(new KArr(res)));
                return Val.Nothing;
            },
            ["HasCustomAbility"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                var ability = a[1].AsStr();
                return Out(a, c is not null && AbilityOf(h, c).Contains(ability, StringComparison.OrdinalIgnoreCase));
            },

            // ---------- 原生「不能攻击 / 不能被攻击」判定 ----------
            // 这四个是 C++ 原生（BaseCardObject.h 里有声明、没有蓝图实现），
            // 语义是读卡上的自定义能力标记。单卡可以用它们给自己加禁令，
            // 例如「本回合不能攻击」「不能被地面单位攻击」。
            //
            // 客户端规则库 CanAttack 直接调它们，缺了就会把该否决的攻击放过去。
            ["HasCantAttack"] = (h, a) =>
            {
                // HasCantAttack(out bool hasIt)
                var c = h.Card_(a[0]);
                return Out(a, c is not null && HasAbilityToken(h, c, "cantAttack"));
            },
            ["HasCantAttackType"] = (h, a) =>
            {
                // HasCantAttackType(const FString& cardType, out bool hasIt)
                var c = h.Card_(a[0]);
                var type = a[1].AsStr();
                return Out(a, c is not null && HasAbilityToken(h, c, "cantAttack" + type));
            },
            ["HasCantBeAttackedBy"] = (h, a) =>
            {
                // HasCantBeAttackedBy(const FString& unitType, out bool hasIt)
                var c = h.Card_(a[0]);
                var type = a[1].AsStr();
                return Out(a, c is not null && HasAbilityToken(h, c, "cantBeAttackedBy" + type));
            },
            ["HasAttackLeft"] = (h, a) =>
            {
                // HasAttackLeft(out bool doesIt)：本回合还能不能再攻击。
                // 客户端是 attackCountThisTurn < maxAttack；引擎单次攻击模型下
                // 等价于「本回合没攻击过」，Fury 之类多次攻击的卡由字段驱动。
                var c = h.Card_(a[0]);
                if (c is null) return Out(a, false);
                var max = (int)h.Obj(c).Get("maxAttack").AsInt();
                if (max <= 0) max = 1;
                var used = c.Has(Kw.Fury) ? 2 : 1;
                return Out(a, max - (c.AttackedThisTurn ? 1 : 0) - (used - 1) > 0);
            },
            ["HasCustomAbilityFromCard"] = (h, a) =>
            {
                // HasCustomAbilityFromCard(ability, giverID, out doesIt)
                var c = h.Card_(a[0]);
                var ability = a[1].AsStr();
                return Out(a, c is not null && AbilityOf(h, c).Contains(ability, StringComparison.OrdinalIgnoreCase));
            },
            ["CustomName2Add"] = (h, a) =>
            {
                // 往自定义名后缀上追加一个属性标记，逗号分隔。
                var c = h.Card_(a[0]);
                if (c is not null)
                {
                    var k = h.Obj(c);
                    var cur = k.Get("customName").AsStr();
                    k.Set("customName", Val.Of(cur.Length == 0 ? a[1].AsStr() : cur + "," + a[1].AsStr()));
                }
                return Val.Nothing;
            },
            ["HasCampaignUpgrade"] = (h, a) => Out(a, false),   // 战役升级：无头模拟里恒为未升级
            ["GetCampaignStrategy"] = (h, a) => Out(a, 0),
            ["GetEnumeratorUserFriendlyName"] = (h, a) => Val.Of(a.Length > 1 ? a[1].AsStr() : ""),

            // ---------- 纸牌属性（Has* 系列，客户端来自卡牌 CDO 的 tag/关键字）----------
            // 注意这些是「卡牌定义上的静态属性」，不是场上单位的临时状态。
            ["getHasFury"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Fury)),
            ["getHasGuard"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Guard)),
            ["getHasBlitz"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Blitz)),
            ["getHasSmokescreen"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Smokescreen)),
            ["getHasAmbush"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Ambush)),
            // Shock 在客户端是卡上的 hasShock 标志位（31 张卡带它，也会被效果动态赋予）。
            // 它在 CalculateDamageDealt 里决定「命中后压制目标」，缺了整条 Shock 机制失效。
            ["getHasShock"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Shock)),
            // Immune：CDO 里没有静态标志位，只能被效果赋予，所以读实体关键字即可。
            ["getHasImmune"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Immune)),
            // Pincer（钳形）：卡上带 hasPincer 就算「有钳形效果」，15 张卡带它。
            ["hasActivePincerEffect"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Pincer)),
            ["getHasMobilize"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Mobilize)),

            // ---------- 位置 / 状态查询 ----------
            ["IsLocatedInDeck"] = (h, a) => Out(a, h.Card_(a[0])?.Loc == Loc.Deck),
            ["IsPinned"] = (h, a) => Out(a, h.Card_(a[0])?.Pinned ?? false),
            ["IsGotcha"] = (h, a) =>
            {
                // 反制指令：盖在场上、已激活。客户端用 gotchaActivated（int，>0 = 已激活）
                // 当 activeGotchas 映射的排序键 —— 以前宿主读的是一个谁都不写的
                // "gotcha" 布尔字段，于是 50 张反制指令永远不触发。
                var c = h.Card_(a[0]);
                return Out(a, c is not null && c.GotchaActivated > 0);
            },
            ["ShouldGotchaTrigger"] = (h, a) =>
            {
                // 参数：(triggerCard, out shouldIt)。只有已激活且还在场上的反制卡才响应。
                var self = h.Card_(a[0]);
                var should = self is not null && self.GotchaActivated > 0 && !self.Destroyed;
                return Out(a, should);
            },
            ["Get Is Server Config"] = (h, a) => Out(a, false),
            ["GetClientSide"] = (h, a) => Out(a, (int)h.Engine.Perspective),

            // ---------- 攻防的「解密」读法 ----------
            // 客户端把 attack/defense 加密存着防内存修改；无头模拟直接读明文。
            ["getAndDecryptDefense"] = (h, a) => Out(a, h.Card_(a[0]) is { } dc ? dc.Defense : Num(a[0], "defense")),
            // setAndEncryptDefense(card, value, key1, key2, frameCount) —— 客户端把防御加密存，
            // 无头模拟直接写明文。缺了它「把防御设为 N」这类效果会静默失效。
            ["setAndEncryptDefense"] = (h, a) =>
            {
                if (h.Card_(a[0]) is { } c)
                {
                    c.Defense = Math.Max(0, (int)a[1].AsInt());
                    c.MaxDefense = Math.Max(c.MaxDefense, c.Defense);
                }
                return Val.Nothing;
            },
            ["getAndDecryptKreditBuff"] = (h, a) => Out(a, h.Card_(a[0])?.BuffCost ?? 0),
            ["setAndEncryptKreditBuff"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                if (c is not null) c.BuffCost = (int)a[1].AsInt();
                return Val.Nothing;
            },
            ["getKreditTempBuffAmount"] = (h, a) => Out(a, h.Card_(a[0])?.BuffCost ?? 0),
            // setAndEncryptKredit(card, value, key1, key2, frameCount) —— 客户端加密存，
            // 无头模拟直接写明文。缺了它「把费用设为 N」这类效果会静默失效。
            ["setAndEncryptKredit"] = (h, a) =>
            {
                if (h.Card_(a[0]) is { } c) c.KreditCost = Math.Max(0, (int)a[1].AsInt());
                return Val.Nothing;
            },
            // OnAfterKreditCostChanged(cardToChangeRef, instigatorID) —— 费用变更后通知
            // 响应者（触发点 id=45）。引擎侧对应的包装没人调用，所以在这里直接发。
            ["OnAfterKreditCostChanged"] = (h, a) =>
            {
                h.Engine.Fire(Trigger.OnOtherCardKreditCostChanged, h.Card_(a[0]));
                return Val.Nothing;
            },
            // IsChooseOneCard(card, out isIt) —— 是不是「三选一」卡（卡池里 48 张）。
            ["IsChooseOneCard"] = (h, a) => Out(a, h.Card_(a[0])?.Def?.ChooseOneCards.Count > 0),
            ["getTotalKreditCost"] = (h, a) =>
                Out(a, Math.Max(0, h.Card_(a[0])?.KreditCost ?? 0)),
            ["getCardsBuffedByThisCard"] = (h, a) =>
            {
                var me = h.Card_(a[0])?.InstanceId ?? -1;
                var ids = h.Engine.S.AllOnBoard()
                    .Where(c => h.Obj(c).Get("buffedBy").AsInt() == me)
                    .Select(c => Val.Of(c.InstanceId));
                Val.TrySetOut(a[^1], Val.Ref(new KArr(ids)));
                return Val.Nothing;
            },

            // ---------- 战役 / 加密 / 静态数据：无头模拟里是空实现 ----------
            ["InitializeEncryption"] = (h, a) => Val.Nothing,
            // void GetEncryptionKey(FString& OutKey)：调用点读 out 槽。
            ["GetEncryptionKey"] = (h, a) => { Val.TrySetOut(a[^1], Val.Of("")); return Val.Of(""); },
            ["InitStaticGameplayTags"] = (h, a) => Val.Nothing,
            // void GetStaticCampaignName(FString cardName, FString& campaignName)：out 槽。
            ["GetStaticCampaignName"] = (h, a) => { Val.TrySetOut(a[^1], Val.Of("")); return Val.Of(""); },

            // ---------- 枚举助手 / 平台判定 ----------
            // void GetEnumeratorValueFromIndex(UEnum* Enum, uint8 Index, uint8& ReturnValue)
            // 这里用到的两个枚举（ECombatKeyword / EGameplayRestrictions，见
            // H:\kards\Source\*\.h）都是 0 起、无空洞的连续枚举，所以显示下标就是枚举值。
            // 换到有空洞的枚举上要补表，不能继续当恒等。
            ["GetEnumeratorValueFromIndex"] = (h, a) => a.Length > 2 ? a[2] : Val.Nothing,
            // 无头模拟不是编辑器。
            ["IsEditor"] = (h, a) => Val.False,
            // void BranchOnProviderWithEditorTestSupport(bool& Branches, bool bTestSupport)
            // 平台/编辑器测试分支：无头模拟走「否」。
            ["BranchOnProviderWithEditorTestSupport"] = (h, a) => Out(a, false),
            ["SetObjectiveCounter"] = (h, a) => Val.Nothing,
            ["NotiferStarCounterChanged"] = (h, a) => Val.Nothing,
            ["OnCreateCard"] = (h, a) => Val.Nothing,
            // bool GetDataTableRowFromName(DataTable*, FName RowName, FTableRowBase& OutRow)
            //
            // 无头模拟不加载 UDataTable，所以行内容由 Core.DataTables 提供。
            // 这张表必须真的有内容：card_event_mass_deployment 的 GetRandomKreditCombo
            // 是「随机取一个组合，取不到就重试」，返回空行会让那个循环永不退出。
            // 只被 UI / 平台函数引用的表返回 false，那是正确行为。
            ["GetDataTableRowFromName"] = (h, a) =>
            {
                var table = a.Length > 1 ? a[1].AsStr() : null;
                var rowName = a.Length > 2 ? a[2].AsStr() : null;
                var found = Core.DataTables.TryGetRow(table, rowName, out var row);
                Val.TrySetOut(a[^1], found ? RowToObj(row) : Val.Nothing);
                return Val.Of(found);
            },

            // ---------- 随机 / 时间 ----------
            // 客户端的两条随机流分别服务「表现层随机」和「逻辑层随机」。
            // 逻辑层必须走引擎自己的 RNG，否则同一 seed 的复现性就没了。
            ["RandomIntegerInRangeFromStream"] = (h, a) =>
            {
                var lo = (int)a[^3].AsInt();
                var hi = (int)a[^2].AsInt();
                if (hi < lo) (lo, hi) = (hi, lo);
                return Out(a, lo + (int)(h.Engine.S.Rng.NextULong() % (ulong)(hi - lo + 1)));
            },
            ["GetFrameCount"] = (h, a) => Val.Of(h.Engine.Steps),

            // ---------- 生成 / 造牌 ----------
            ["SpawnCardOnBattlefield"] = (h, a) =>
            {
                // 签名（去掉接收者后从 0 起）：
                //   0 side, 1 Frontline, 2 card_name, 3 spawnerID, 4 campaignName(Ref,文本),
                //   5 NewGiveBlitz, 6 locationNumber, 7 salvageFaction,
                //   8 NewMakeVeteran, 9 forceGoldCard, 10 (out) spawnedCardID
                // 签名: SpawnCardOnBattlefield(side, Frontline, card_name, spawnerID,
                //          campaignName, NewGiveBlitz, locationNumber, salvageFaction,
                //          NewMakeVeteran, forceGoldCard, int32& spawnedCardID)
                // a[0] 是接收者，实参整体后移一位。
                var side = (Side)a[1].AsInt();
                var name = a[3].AsStr();
                var spawned = h.Engine.SpawnByName(side, name);
                if (spawned is not null)
                {
                    if (a[2].AsBool()) h.Engine.TryPlaceOnFrontlinePublic(spawned);
                    if (a[6].AsBool()) h.Obj(spawned).Set("blitz", Val.Of(true));
                    if (a[9].AsBool()) spawned.Veteran = true;
                }
                Val.TrySetOut(a[^1], Val.Of(spawned?.InstanceId ?? 0));
                return Val.Nothing;
            },
            ["SpawnObject"] = (h, a) => Val.Nothing,   // 表现层 Actor，无头模拟忽略
            // void CopyData(UBaseCardObject* toCard) —— 把本卡数据拷到另一个卡对象。
            // 唯一调用点是 ConstructAndCopyCard：先 SpawnObject 造一个**表现层 Actor**，
            // 再 CopyData 把数据灌进去。无头模拟没有 Actor（SpawnObject 就是 no-op），
            // 所以这里也显式登记为 no-op，免得它混进「未实现调用」里污染完备性指标。
            ["CopyData"] = (h, a) => Val.Nothing,

            // void NotifySelectCardToDrawPending(U_CardFunctionsNotifier* notifier, int32 selectingCardID,
            //        bool, TArray<UBaseCardObject*> candidates, bool isEffect)
            //
            // 客户端这里只是「通知 UI 把候选列出来」，真正的效果在玩家点完之后由 UI 回调
            // OnHandTargetSelected 执行（调用方忽略 selectCardToDraw 的返回值，所以延后是安全的）。
            // 无头模拟没有 UI，所以宿主把它转成引擎的待决选择：动作列表里只剩候选项，
            // 选定后引擎再回调 OnHandTargetSelected。见 GameEngine.PendingChoice。
            ["NotifySelectCardToDrawPending"] = (h, a) =>
            {
                var src = h.Engine.S.FindCard(a.Length > 1 ? (int)a[1].AsInt() : 0);
                var opts = new List<ChoiceOption>();
                foreach (var v in AsArr(a.Length > 3 ? a[3] : Val.Nothing)?.Items ?? new List<Val>())
                {
                    // 候选数组有两种载荷，两条路都要认：
                    //  - 卡对象（牌库/占卜那条路，K2Node_MakeArray 装的是 UBaseCardObject*）
                    //  - 卡名字符串（Develop 那条路，装的是 GetMember(item,"name")）
                    if (v.O is KObj k)
                    {
                        var id = k.Get("defId").AsStr();
                        opts.Add(new ChoiceOption
                        {
                            CardId = (int)k.Get("cardID").AsInt(),
                            Name = id,
                            Label = string.IsNullOrEmpty(id) ? k.ToString() : id,
                        });
                    }
                    else
                    {
                        var nm = v.AsStr();
                        if (string.IsNullOrEmpty(nm)) continue;
                        opts.Add(new ChoiceOption { CardId = 0, Name = nm, Label = nm });
                    }
                }
                if (src is not null)
                    h.Engine.RaiseChoice(src, "selectCardToDraw", opts, a.Length > 4 && a[4].AsBool());
                return Val.Nothing;
            },
            // UBaseCardObject* GetStaticCard(FName cardName) —— 造一张「静态卡」对象：
            // 只作数值与身份来源，不代表场上的实体。Develop 类效果靠它列出候选卡
            //（例如 card_unit_hampshire_regiment 的 GetChooseSpawnCards 会连造 3 张）。
            // 以前返回 Nothing，于是候选列表恒为空、Develop 永远选不出东西。
            ["GetStaticCard"] = (h, a) =>
            {
                var def = Core.CardDb.Resolve(a.Length > 1 ? a[1].AsStr() : null);
                return def is null ? Val.Nothing : Val.Ref(h.ObjStatic(def));
            },

            // ---------- 自定义名后缀的增删 ----------
            ["CustomName1Add"] = (h, a) => { SuffixAdd(h, a, "customName1"); return Val.Nothing; },
            ["CustomName1Remove"] = (h, a) => { SuffixRemove(h, a, "customName1"); return Val.Nothing; },
            ["CustomName2Remove"] = (h, a) => { SuffixRemove(h, a, "customName"); return Val.Nothing; },

            // ---------- 阵营 / 情报 / 关键字 ----------
            ["GetFactionEnum"] = (h, a) =>
            {
                var s = a[1].AsStr();
                if (string.IsNullOrEmpty(s)) return Val.Of(0);
                var def = CardDb.Resolve(s);
                return Val.Of((int)(def?.Faction ?? Faction.Neutral));
            },
            ["HasIntel"] = (h, a) => Out(a, (h.Card_(a[0])?.Cipher ?? 0) > 0),
            ["getIntel"] = (h, a) => Out(a, h.Card_(a[0])?.Cipher ?? 0),
            ["AddIntelToCard"] = (h, a) =>
            {
                // 签名: AddIntelToCard(int32 CardID, int32 instigatorID, int32 amount, int32& qqq)
                // a[0] 是接收者，实参从 a[1] 起。
                var c = h.Engine.S.FindCard((int)a[1].AsInt());
                if (c is null) return Val.Nothing;
                c.Cipher = Math.Clamp(c.Cipher + (int)a[3].AsInt(), 0, 9);
                Val.TrySetOut(a[^1], Val.Of(c.Cipher));
                return Val.Nothing;
            },
            // 「是否正在结算一个动作」。**不能恒 false**：规则库用它决定
            // 效果是立刻结算还是延后到事件循环。恒 false 会让 MakeVeteran 里
            // 「立刻发 OnBecomingVeteran」那一步被整段跳过 ——
            // 老兵变了，但本该打敌方 HQ 的 3 点没打。
            ["IsActionProcess"] = (h, a) => Out(a, h.Engine.ActionInProgress),
            ["SetCardsSeenByCipher"] = (h, a) => Intel(h, a),
            ["RemoveGameplayTag"] = (h, a) =>
            {
                // GameplayTags 是卡的标签容器；引擎建模成 Cards 之外的字符串集合。
                var c = h.Card_(a[0]);
                var tag = a.Length > 2 ? a[2].AsStr() : "";   // (receiver, tags, tag)
                var tags = h.Obj(c)?.Get("gameplayTags");
                if (tags?.O is KArr arr && tag.Length > 0)
                    arr.Items.RemoveAll(v => v.AsStr() == tag);
                return Val.Of(true);
            },

            // ---------- 三选一：真实的决策点 ----------
            // 卡牌把选择存在自己的 ChooseOne 成员上（card_event_strategic_focus 就是这么
            // 读的：Switch(GetMember(self,"ChooseOne"), [(0,IsGroundUnit),(1,IsAirUnit)])），
            // 引擎在出牌前把它写进去，所以这里优先读成员。
            // 成员没写时退回可插拔决策器（默认 0），保证不接 AI 时也确定可复现。
            ["WhichChooseOne"] = (h, a) =>
            {
                var c = a.Length > 0 ? h.Card_(a[0]) : null;
                if (c is not null)
                {
                    var v = h.Obj(c).Get("ChooseOne");
                    if (v.K != VKind.Nothing) return Out(a, (int)v.AsInt());
                }
                return Out(a, h.ChooseOne(c));
            },

            // ---------- 费用 / 老兵版本的读取 ----------
            ["getAndDecryptKredit"] = (h, a) => Out(a, h.Card_(a[0])?.KreditCost ?? 0),
            ["getStaticVeteranUpgrade"] = (h, a) =>
            {
                // 老兵版本的静态卡对象。MakeVeteran 从它身上读攻/防/重甲/关键字，
                // 再写回当前卡 —— 所以这里必须真的给出那张卡，返回 none 会让
                // 升级后的数值全变成 0。
                var c = h.Card_(a[0]);
                var vid = c?.Def?.VeteranUpgradeId;
                var def = vid is null ? null : Core.CardDb.Get(vid);
                if (def is null) { Val.TrySetOut(a[^1], Val.Nothing); return Val.Nothing; }

                // 静态卡：不属于任何一方，只是数值来源，用独立 instanceId 避免与场上实体混淆
                var vk = h.ObjStatic(def);
                Val.TrySetOut(a[^1], Val.Ref(vk));
                return Val.Nothing;
            },
            // MakeCardVeteran(notifier, card) —— 名字直译就是「把这张卡变成老兵」。
            // 客户端这里做的是通知/表现；引擎侧的权威位是 Card.Veteran
            //（MakeVeteran 另有一处 JSON_SetBool(card,"veteran",true) 也会同步它）。
            // 显式实现而不是留空，免得它一直挂在「未实现调用」里。
            ["MakeCardVeteran"] = (h, a) =>
            {
                if (h.Card_(a.Length > 1 ? a[1] : a[0]) is { } c) c.Veteran = true;
                return Val.Nothing;
            },
            ["GetEmptyText"] = (h, a) => Val.Ref(new KObj("Text")),

            // ---------- 战役升级 ----------
            // 战役卡的「升级」直接改本卡的静态属性。无头模拟里照做，
            // 否则战役卡会完全变成白板。
            ["CampaignAddAttack"] = (h, a) => { StatAdd(h, a, "attack"); return Val.Nothing; },
            ["CampaignAddDefense"] = (h, a) => { StatAdd(h, a, "defense"); return Val.Nothing; },
            ["CampaignIncreaseHeavyArmor"] = (h, a) => { StatAdd(h, a, "heavyArmor"); return Val.Nothing; },
            ["CampaignAddKreditCost"] = (h, a) =>
            {
                // 签名: CampaignAddKreditCost(int32 Value, bool secondUpgrade, bool Clear)
                var c = h.Card_(a[0]);
                if (c is null) return Val.Nothing;
                if (a[3].AsBool()) c.KreditCost = (int)a[1].AsInt();          // Clear
                else c.KreditCost += (int)a[1].AsInt();
                return Val.Nothing;
            },
            ["CampaignSetOpCost"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                if (c is not null) h.Obj(c).Set("operationCost", Val.Of((int)a[1].AsInt()));
                return Val.Nothing;
            },
            ["CampaignAddOpCost"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                if (c is not null)
                    h.Obj(c).Set("operationCost", Val.Of(h.Obj(c).Get("operationCost").AsInt() + a[1].AsInt()));
                return Val.Nothing;
            },
            ["CampaignAddAmbush"] = (h, a) => { KwAdd(h, a, Kw.Ambush); return Val.Nothing; },
            ["CampaignAddBlitz"] = (h, a) => { KwAdd(h, a, Kw.Blitz); return Val.Nothing; },
            ["CampaignAddFury"] = (h, a) => { KwAdd(h, a, Kw.Fury); return Val.Nothing; },
            ["CampaignAddGuard"] = (h, a) => { KwAdd(h, a, Kw.Guard); return Val.Nothing; },
            ["CampaignAddMobilize"] = (h, a) => { KwAdd(h, a, Kw.Mobilize); return Val.Nothing; },
            ["CampaignAddSmokescreen"] = (h, a) => { KwAdd(h, a, Kw.Smokescreen); return Val.Nothing; },
            ["CampaignRemoveMobilize"] = (h, a) => { KwRemove(h, a, Kw.Mobilize); return Val.Nothing; },
            ["CampaignRemoveSmokescreen"] = (h, a) => { KwRemove(h, a, Kw.Smokescreen); return Val.Nothing; },

            // ---------- 直接生成到手牌 ----------
            ["SpawnCardInHandBySide"] = (h, a) =>
            {
                // 签名: SpawnCardInHandBySide(side, card_name, spawnerID, ...)
                var side = (Side)a[1].AsInt();
                var name = a[2].AsStr();
                var c = h.Engine.SpawnByName(side, name);
                Val.TrySetOut(a[^1], Val.Of(c?.InstanceId ?? 0));
                return Val.Nothing;
            },

            // CustomName1 / CustomName2 是两段独立的后缀，各自判定
            ["CustomName1HasAttribute"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                var attr = a[1].AsStr();
                var name = h.Obj(c)?.Get("customName1").AsStr() ?? "";
                return Out(a, name.Contains(attr, StringComparison.OrdinalIgnoreCase));
            },
            ["GetCustomName2Attributes"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                var name = h.Obj(c)?.Get("customName").AsStr() ?? "";
                var parts = name.Length == 0
                    ? Array.Empty<string>()
                    : name.Split(',', StringSplitOptions.RemoveEmptyEntries);
                Val.TrySetOut(a[^1], Val.Ref(new KArr(parts.Select(x => Val.Of(x)))));
                return Val.Nothing;
            },
            ["GetSupportLineBySide"] = (h, a) =>
            {
                // 签名：static ECardLocationEnum GetSupportLineBySide(ESideEnum, const UObject* WorldContextObject)
                // 尾参是静态库惯例的 WorldContextObject，真正的实参是 a[1]。
                // 支援线每个阵营一张，编号见 ECardLocationEnum（Left=5, Right=6）。
                var s = (Side)a[1].AsInt();
                return Val.Of(s == Side.Left ? 5 : 6);
            },

            // UHT: void getTotalDefense(int32& totalDefense)
            // 调用方读 out 槽，直接 return 会让它恒为 0。
            ["getTotalDefense"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Out(a, c is null ? 0 : c.TotalDefense);
            },
            // UtilityFunctions.GetGameState(__WorldContext, out GameState)
            // 规则库用它取对局状态来遍历「注册了某触发点的卡」。
            // 缺了它 CanSelectAsTarget 的整段判定拿不到状态、静默早退
            //（返回 can=false 且原因为空 —— 不报错，只是永远选不中目标）。
            // 布局：a[0]=接收者 a[1]=__WorldContext a[2]=out
            ["GetGameState"] = (h, a) =>
            {
                Val.TrySetOut(a[^1], Val.Ref(h.GameStateRef));
                return Val.Nothing;
            },

            // UtilityFunctions.GetLogic 的第一步：取游戏模式对象。
            // 无头模拟里没有 UWorld，但游戏模式就是我们的 BP_Logic 单例 ——
            // 直接把它交出去，规则库就能拿到 GameStateRef / myRealSide。
            // 返回 none 会让 GetLogic 判 DynamicCast 失败、回空值，
            // 于是 CanAttack 的「地面单位不能攻击」判定拿不到限制状态。
            ["GetGameMode"] = (h, a) => Val.Ref(h.Logic),
            ["ReportError"] = (h, a) => Val.Nothing,   // UI/日志，无头忽略

            // UHT: void getTotalOperationCost(int32& totalOperationCost)
            // 客户端 CanAttack 用它判 not_enough_kredits。缺了会让所有攻击被否
            // （can=false 且 reason 为空 —— 因为判据本身没算出来）。
            // 引擎侧的实际费用 = 卡面费用 + 本回合的费用调整。
            ["getTotalOperationCost"] = (h, a) =>
                Out(a, h.Card_(a[0]) is { } oc ? oc.KreditCost + oc.BuffCost : Num(a[0], "operationCost")),

            // OnAfterOperationCostChanged(cardToChangeRef, instigatorID) —— 客户端在
            // ChangeOperationCost 末尾调它，是「操作费用已变更」触发点(id=10)的分派入口。
            // 引擎侧的包装 FireKreditCostChangedTriggers 是个没人调用的死函数，
            // 所以这里直接把触发点发出去，否则 37 张注册了它的卡永远是白板。
            ["OnAfterOperationCostChanged"] = (h, a) =>
            {
                h.Engine.Fire(Trigger.OnAfterOtherCardOperactionCostChanged, h.Card_(a[0]));
                return Val.Nothing;
            },

            // UHT: void getAttackTempBuffAmount(int32 instigatorID, int32& tempAmount)
            // 实参是 { 卡, instigatorID, out }。引擎只建模一个总加成，不分来源，
            // 所以不区分 instigatorID。这里同样必须写 out 槽而不是直接返回。
            ["getAttackTempBuffAmount"] = (h, a) => Out(a, h.Card_(a[0])?.BuffAttack ?? 0),

            ["isBuffedByCard"] = (h, a) =>
            {
                // isBuffedByCard(instigatorID, out isBuffed)：判断本卡是否被指定来源 buff 过。
                // 引擎目前只记总额，没有按来源记账，所以按「总额非零」近似。
                var c = h.Card_(a[0]);
                Val.TrySetOut(a[^1], Val.Of((c?.BuffAttack ?? 0) != 0));
                return Val.Nothing;
            },

            // ---------- 改数值 ----------
            ["ChangeAttack"] = (h, a) =>
            {
                // ChangeAttack(card, instigatorID, amount, ChangeType, skipAction, out qqq)
                var c = h.Card_(a[1]);
                var amount = (int)a[3].AsInt();
                var ct = (int)a[4].AsInt();
                if (c is not null)
                {
                    c.BuffAttack = ct switch
                    {
                        2 => amount,             // SetValue
                        4 => 0,                  // tempBuffRemove
                        _ => c.BuffAttack + amount,
                    };
                    h.Obj(c).Set("attackBuff", Val.Of(c.BuffAttack));
                }
                Val.TrySetOut(a[^1], Val.True);
                return Val.Nothing;
            },

            ["ChangeDefense"] = (h, a) =>
            {
                // HQ 是合成镜像对象，没有 Card 实体：ChangeDefense(hqCard, …) 必须路由回
                // PlayerState.Hq，否则「HQ 防御 +N」这类效果静默变成空操作
                // （card_event_aans 的「Your HQ gets +3 defense」就是）。
                if (a[1].O is KObj hq && h.IsHqObj(hq))
                {
                    var hs = h.HqSideOf(hq);
                    var hp = h.Engine.S.Player(hs);
                    hp.Hq = Math.Max(0, hp.Hq + (int)a[3].AsInt());
                    hq.Set("defense", Val.Of(hp.Hq));
                    hq.Set("maxDefense", Val.Of(Rules.HqDefense));
                    Val.TrySetOut(a[^1], Val.True);
                    return Val.Nothing;
                }
                var c = h.Card_(a[1]);
                if (c is not null)
                {
                    var amount = (int)a[3].AsInt();
                    c.Defense = Math.Max(0, c.Defense + amount);
                    c.MaxDefense = Math.Max(c.MaxDefense, c.Defense);
                }
                Val.TrySetOut(a[^1], Val.True);
                return Val.Nothing;
            },

            // UHT: void DamageCard(card, amount, damagerCardID, isRedirected,
            //                      fromFight, isFightDefenderDamage, bool& targetDestroyed)
            // 布局：a[0]=接收者 a[1]=card a[2]=amount a[3]=damagerCardID
            //       a[4]=isRedirected a[5]=fromFight a[6]=isFightDefenderDamage a[7]=out
            ["DamageCard"] = (h, a) =>
            {
                // HQ 的合成对象不是卡，必须在这里路由回 DamageHq ——
                // 否则 DamageCard(hqCard, …) 静默变成空操作。
                if (a[1].O is KObj hq && h.IsHqObj(hq))
                {
                    var hs = h.HqSideOf(hq);
                    var hqAmount = (int)a[2].AsInt();
                    h.Engine.DamageHq(hs, hqAmount);
                    hq.Set("defense", Val.Of(h.Engine.S.Player(hs).Hq));
                    Val.TrySetOut(a[^1], Val.Of(h.Engine.S.Player(hs).Hq <= 0));
                    return Val.Nothing;
                }
                var c = h.Card_(a[1]);
                if (c is null || c.Destroyed || c.Defense <= 0) { Val.TrySetOut(a[^1], Val.False); return Val.Nothing; }
                var amount = (int)a[2].AsInt();
                // fromFight 决定了 destroyedInCombat，而它正是「攻击并摧毁」类
                // 效果（如 7. SCHÜTZEN 变老兵）的前置条件 —— 不能省。
                var killer = h.Card_(a[3]);
                h.Engine.DamageCard(c, amount, lethal: false,
                    killer: killer, inCombat: a[5].AsBool());
                Val.TrySetOut(a[^1], Val.Of(c.Destroyed));
                return Val.Nothing;
            },

            ["HealCard"] = (h, a) =>
            {
                // 同 ChangeDefense：HQ 的治疗也要路由回 PlayerState.Hq。
                if (a[1].O is KObj hq && h.IsHqObj(hq))
                {
                    var hs = h.HqSideOf(hq);
                    var hp = h.Engine.S.Player(hs);
                    hp.Hq = Math.Min(Rules.HqDefense, hp.Hq + (int)a[3].AsInt());
                    hq.Set("defense", Val.Of(hp.Hq));
                    return Val.Nothing;
                }
                var c = h.Card_(a[1]);
                if (c is not null)
                {
                    h.Engine.FireRepairTriggers(c);
                    var before = c.Defense;
                    c.Defense = Math.Min(c.MaxDefense, c.Defense + (int)a[3].AsInt());
                    if (c.Defense != before) h.Engine.FireDefenseChangedTriggers(c);
                }
                return Val.Nothing;
            },

            // ---------- 钉住 / 压制 ----------
            ["PinUnit"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null && !c.Pinned)
                {
                    c.Pinned = true;
                    h.Engine.FirePinTriggers(c, true);
                }
                return Val.Nothing;
            },
            // UHT: void RemovePin(UBaseCardObject* card, int32& qqq)
            // 布局：a[0]=接收者 a[1]=card a[2]=out。漏写 out 会让调用方
            // 以为操作失败（它们通常无条件继续，但布尔结果会被记进状态）。
            ["RemovePin"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null && c.Pinned)
                {
                    c.Pinned = false;
                    h.Engine.FirePinTriggers(c, false);
                }
                return Out(a, c is not null);
            },
            // UHT: void SuppressUnit(int32 CardID, int32 instigatorID, bool& qqq)
            // 注意第一个实参是 cardID（整数），不是卡对象。
            ["SuppressUnit"] = (h, a) =>
            {
                var c = h.Engine.S.FindCard((int)a[1].AsInt());
                if (c is not null && !c.Suppressed)
                {
                    c.Suppressed = true;
                    h.Engine.FireSuppressTriggers(c);
                }
                return Out(a, c is not null);
            },

            ["DestroyCard"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null) h.Engine.DestroyCard(c);
                return Val.Nothing;
            },

            // ---------- 红币 ----------
            // 布局：a[0]=接收者 a[1]=side a[2]=out。
            // 全部 23 个调用点都传 out 槽，所以必须写 out 而不是 return ——
            // 直接 return 会让调用方读到空值，「还差多少行动点」永远算不出来，
            // CanSelectAsTarget 会以 not_enough_kredits_to_target 拒掉一切目标。
            ["getKreditBySide"] = (h, a) =>
            {
                Val.TrySetOut(a[^1], Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Kredits));
                return Val.Nothing;
            },
            ["setKreditBySide"] = (h, a) =>
            {
                h.Engine.S.Player((Side)a[1].AsInt()).Kredits = (int)a[2].AsInt();
                return Val.Nothing;
            },
            ["getKreditSlotBySide"] = (h, a) =>
            {
                Val.TrySetOut(a[^1], Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).KreditSlots));
                return Val.Nothing;
            },
            ["setKreditSlotBySide"] = (h, a) =>
            {
                h.Engine.S.Player((Side)a[1].AsInt()).KreditSlots = (int)a[2].AsInt();
                return Val.Nothing;
            },
            ["getMaxPossibleKredits"] = (h, a) =>
            {
                // UHT 形态是 void getMaxPossibleKredits(int32& outputMax)：调用点读 out 槽。
                // 原来写成 return，调用方拿到 Nothing → Clamp(x, 0, 0) = 0，
                // 于是「获得一个克redit槽」这类效果会把槽位清成 0。
                //
                // 值必须是**绝对上限 24**，不是每回合自然增长的上限 12：
                // ChangeKreditSlotsBySide 用这个值 clamp「卡牌加槽」。
                Val.TrySetOut(a[^1], Val.Of(Rules.MaxKreditSlots));
                return Val.Of(Rules.MaxKreditSlots);
            },

            // ---------- HQ ----------
            ["GetHqDefense"] = (h, a) => Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Hq),
            ["DamageHQ"] = (h, a) =>
            {
                h.Engine.DamageHq((Side)a[1].AsInt(), (int)a[2].AsInt());
                return Val.Nothing;
            },

            // ---------- 场地 ----------
            ["GetCardsOnBoardBySide"] = (h, a) =>
            {
                var s = (Side)a[1].AsInt();
                var arr = new KArr(h.Engine.S.UnitsOnBoard(s).Select(c => Val.Ref(h.Obj(c))));
                Val.TrySetOut(a[^1], Val.Ref(arr));
                return Val.Nothing;
            },
            // UHT: void GetLocationCardBySide(UBaseCardObject*& card,
            //                                 int32& locationCardID, ESideEnum side)
            //
            // 布局：a[0]=接收者 a[1]=out card a[2]=out locationCardID a[3]=side
            //
            // 之前把 a[1] 当成 side、a[2] 当成位置、只写 a[^1]（也就是那个 side 实参），
            // 三个位置全错：两个 out 槽没写，还把传入的 side 覆盖掉了。
            // 依赖它的效果（变老兵打 HQ 3 点、穿透伤害转打 HQ）因此全部空转。
            //
            // 这里返回的是**HQ 的合成镜像对象**（引擎把 HQ 存成整数，见 HqLeft 的说明）。
            ["GetLocationCardBySide"] = (h, a) =>
            {
                var s = (Side)a[3].AsInt();
                var hq = h.HqOf(s);
                hq.Set("defense", Val.Of(h.Engine.S.Player(s).Hq));
                hq.Set("maxDefense", Val.Of(Rules.HqDefense));
                Val.TrySetOut(a[1], Val.Ref(hq));
                Val.TrySetOut(a[2], Val.Of(0));
                return Val.Nothing;
            },

            ["GetAllCardInBattle"] = (h, a) =>
            {
                var arr = new KArr(h.Engine.S.AllOnBoard().Select(c => Val.Ref(h.Obj(c))));
                Val.TrySetOut(a[^1], Val.Ref(arr));
                return Val.Nothing;
            },

            // ---------- 抽牌 ----------
            // UHT: void DrawCardsFromDeckBySide(instigatorID, side, NumCards, cardSeen,
            //                                   OpponentDraw, TArray<int32>& cardsIDs, drawDelay)
            // 布局：a[0]=接收者 a[1]=instigatorID a[2]=side a[3]=NumCards
            //       a[4]=cardSeen a[5]=OpponentDraw a[6]=out cardsIDs a[7]=drawDelay
            // out 槽以前漏了：调用方拿不到抽到的牌 id，依赖它的效果会静默空转。
            ["DrawCardsFromDeckBySide"] = (h, a) =>
            {
                var side = (Side)a[2].AsInt();
                var n = (int)a[3].AsInt();
                var before = h.Engine.S.Player(side).Hand.Count;
                h.Engine.DrawTo(h.Engine.S.Player(side), n);
                var hand = h.Engine.S.Player(side).Hand;
                var ids = new List<Val>();
                for (var i = Math.Min(before, hand.Count); i < hand.Count; i++)
                    ids.Add(Val.Of(hand[i].InstanceId));
                Val.TrySetOut(a[^1], Val.Ref(new KArr(ids)));
                return Val.Nothing;
            },
            ["GetDeckSizeBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Deck.Count),            ["GetHandSizeBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Hand.Count),

            // void DrawSpecificCardFromDeckBySide(int32 instigatorID, int32 CardID, ESideEnum side, bool cardSeen)
            //
            // 「把指定的那张卡抽到某方手里」——也就是 Develop 类效果的通用落点
            //（selectCardToDraw 在候选唯一时先调它，再回调 OnHandTargetSelected）。
            // 正数 cardID = 牌库里的实体卡；负数 = GetStaticCard 造的静态卡，按 defId 现造一张。
            ["DrawSpecificCardFromDeckBySide"] = (h, a) =>
            {
                var side = (Side)a[3].AsInt();
                var id = (int)a[2].AsInt();
                var p = h.Engine.S.Player(side);

                var existing = h.Engine.S.FindCard(id);
                if (existing is not null)
                {
                    if (existing.Loc == Loc.Hand) return Val.Nothing;
                    p.Deck.Remove(existing);
                    p.Discard.Remove(existing);
                    existing.Loc = Loc.Hand;
                    if (p.Hand.Count < Rules.MaxCardsOnHand) p.Hand.Add(existing);
                    else { existing.Loc = Loc.Discard; p.Discard.Add(existing); }
                    h.Engine.FireTrigger(Trigger.OnOtherCardDrawnFromDeck, existing);
                    return Val.Nothing;
                }

                if (id < 0 && h._staticById.TryGetValue(id, out var sk))
                {
                    var defId = sk.Get("defId").AsStr();
                    if (!string.IsNullOrEmpty(defId)) h.Engine.SpawnByName(side, defId);
                }
                return Val.Nothing;
            },

            // ---------- 回合 ----------
            // UHT: void GetTurnNumber(int32& TurnNumber)（CardFunctionsStub）
        ["GetTurnNumber"] = (h, a) => Out(a, h.Engine.S.Turn),
            ["GetFrontlineOwnerSide"] = (h, a) =>
                Out(a, (int)h.Engine.S.FrontlineOwner),
            // UHT: void IsSideActive(ESideEnum SideToCheck, bool& Active)
        // 实参是 { self, side, out }，所以比较值在 a[1]。
        ["IsSideActive"] = (h, a) => Out(a, h.Engine.S.Current == (Side)a[1].AsInt()),

            // ---------- 卡牌自状态（JSON_* / PersistCustomFields） ----------
            // 蓝图用它们把「本卡自己的附加状态」挂在卡上。这里直接映射到 KObj 字段，
            // 与客户端行为等价，且不需要真的序列化成 JSON。
            // UHT: void JSON_GetInt(UBaseCardObject* card, const FString& VariableName,
            //                      int32& Value, bool& Found)
            // 布局：a[0]=接收者 a[1]=card a[2]=VariableName a[3]=out Value a[4]=out Found
            // 以前只 return 了值，两个 out 槽都没写 —— 调用方读的是 out，
            // 于是所有 JSON 计数器（倒计时、回合标记）都读到 0。
            ["JSON_GetInt"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                var f = c is null ? Val.Nothing : h.Obj(c).Get("j_" + a[2].AsStr());
                Val.TrySetOut(a[^2], f.K == VKind.Nothing ? Val.Of(0) : Val.Of((int)f.AsInt()));
                Val.TrySetOut(a[^1], Val.Of(f.K != VKind.Nothing));
                return Val.Nothing;
            },
            // UHT: void JSON_GetBool(card, VariableName, bool& Value, bool& Found)
            ["JSON_GetBool"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                var f = c is null ? Val.Nothing : h.Obj(c).Get("j_" + a[2].AsStr());
                Val.TrySetOut(a[^2], Val.Of(f.AsBool()));
                Val.TrySetOut(a[^1], Val.Of(f.K != VKind.Nothing));
                return Val.Nothing;
            },
            // UHT: void JSON_SetInt(card, VariableName, int32 Value, bool& Found)
            ["JSON_SetInt"] = (h, a) =>
            {
                if (h.Card_(a[1]) is { } c) h.Obj(c).Set("j_" + a[2].AsStr(), Val.Of((int)a[3].AsInt()));
                Val.TrySetOut(a[^1], Val.True);
                return Val.Nothing;
            },
            // UHT: void JSON_SetBool(card, VariableName, bool Value, bool& Found)
            ["JSON_SetBool"] = (h, a) =>
            {
                if (h.Card_(a[1]) is { } c)
                {
                    var key = a[2].AsStr();
                    var v = a[3].AsBool();
                    h.Obj(c).Set("j_" + key, Val.Of(v));
                    // 老兵状态有两份：客户端把它存进卡的 JSON（MakeVeteran 里
                    // JSON_SetBool(card,"veteran",true)），引擎自己的权威位是 Card.Veteran。
                    // 不同步的话「变老兵」会只写进 JSON，而引擎侧的 IsVeteran /
                    // 老兵换用属性表全都读不到 —— 表现为效果跑完但卡没变。
                    if (string.Equals(key, "veteran", StringComparison.OrdinalIgnoreCase)) c.Veteran = v;
                }
                Val.TrySetOut(a[^1], Val.True);
                return Val.Nothing;
            },
            // UHT: void JSON_Clear(card, const FString& VariableName, bool& Found)
            // 清的是「指定那个键」，不是清空整张卡的所有 j_ 键。
            // 布局：a[0]=接收者 a[1]=card a[2]=VariableName a[3]=out Found
            ["JSON_Clear"] = (h, a) =>
            {
                var found = false;
                if (h.Card_(a[1]) is { } c)
                    found = h.Obj(c).Fields.Remove("j_" + a[2].AsStr());
                Val.TrySetOut(a[^1], Val.Of(found));
                return Val.Nothing;
            },
            ["PersistCustomFields"] = (h, a) => Val.Nothing,

            // ---------- JSON 值转换 ----------
            // 蓝图用 JSON 值类型在中转自定义字段。模拟器里这些字段就存在 KObj.Fields，
            // 不需要真的序列化，转换可以直接透传（数组则包成 KArr）。
            ["Conv_JsonObjectToJsonValue"] = (h, a) => Val.Ref(new KObj("JsonValue")),
            ["Conv_JsonValueToArray"] = (h, a) =>
            {
                Val.TrySetOut(a[^2], Val.Ref(new KArr()));
                Val.TrySetOut(a[^1], Val.Of(0));
                return Val.Nothing;
            },
            ["JsonHasTypedField"] = (h, a) => Out(a, false),
            // JsonMakeInt / JsonMakeString(Value) -> JsonValue：值直接透传
            //（模拟器里 JSON 值就是 KObj.Fields 里的一项，不需要真的装箱）。
            ["JsonMakeInt"] = (h, a) => a.Length > 1 ? a[1] : Val.Nothing,
            ["JsonMakeString"] = (h, a) => a.Length > 1 ? a[1] : Val.Nothing,
            ["JsonMakeBool"] = (h, a) => a.Length > 1 ? a[1] : Val.Nothing,
            ["JsonMakeFloat"] = (h, a) => a.Length > 1 ? a[1] : Val.Nothing,
            // GetStaticText(cardName, out text) —— 静态卡面文案，从 cards.json 取。
            ["GetStaticText"] = (h, a) =>
            {
                var txt = Core.CardDb.Resolve(a.Length > 1 ? a[1].AsStr() : null)?.Text ?? "";
                Val.TrySetOut(a[^1], Val.Of(txt));
                return Val.Of(txt);
            },
            // CanMoveAndAttackInTheSameTurn(card, out canIt) —— 能力标记由卡自己
            // 用 CustomName1Add("CanMoveAndAttackInTheSameTurn") 写上/去掉
            //（见 BP_CardFunctions 与 card_event_lightning_conquest_new 的调用点，
            // token 字符串就是从这里取的，不是猜的）。CDO 里也可能直接初始化。
            ["CanMoveAndAttackInTheSameTurn"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                var has = c is not null && h.Obj(c).Get("customName1").AsStr()
                              .Contains("CanMoveAndAttackInTheSameTurn", StringComparison.OrdinalIgnoreCase);
                return Out(a, has);
            },
            ["JsonMakeArray"] = (h, a) => Val.Ref(new KObj("JsonValue")),
            ["JsonMakeField"] = (h, a) => Val.Ref(new KObj("JsonValue")),
            ["JsonHasField"] = (h, a) => Val.False,
            ["JsonRemoveField"] = (h, a) => Val.Nothing,
        };

    // ===================== 原语辅助 =====================

    /// <summary>
    /// 处理「void F(..., T&amp; out x)」这形态：把结果写进最后一个实参槽并返回 none。
    ///
    /// UE 的蓝图 out 参数在字节码里就是一个普通实参槽，宿主必须显式回写；
    /// 只 return 值的话调用点拿不到（它读的是那个槽）。
    /// </summary>
    private static Val? Out(Val[] a, bool result)
    {
        Val.TrySetOut(a[^1], Val.Of(result));
        return Val.Nothing;
    }

    private static Val? Out(Val[] a, int value)
    {
        Val.TrySetOut(a[^1], Val.Of(value));
        return Val.Nothing;
    }

    /// <summary>卡牌定义上是否带某个关键字（与场上临时状态无关）。</summary>
    private static bool HasKw(Card c, Kw k) => c is not null && c.Has(k);

    /// <summary>
    /// DataTable 行 → 蓝图对象。值类型按形状映射：
    /// <c>List&lt;int[]&gt;</c> 变成 <c>KArr&lt;Vec3&gt;</c>（对应 FVector 数组，蓝图会对它 BreakVector）。
    /// </summary>
    private static Val RowToObj(Dictionary<string, object> row)
    {
        var o = new KObj("DataTableRow");
        foreach (var kv in row)
        {
            switch (kv.Value)
            {
                case List<int[]> vecs:
                    o.Set(kv.Key, Val.Ref(new KArr(vecs.Select(v =>
                        Val.Ref(new Vec3(v[0], v[1], v[2]))))));
                    break;
                case int i: o.Set(kv.Key, Val.Of(i)); break;
                case string s: o.Set(kv.Key, Val.Of(s)); break;
                case null: o.Set(kv.Key, Val.Nothing); break;
                default: o.Set(kv.Key, Val.Ref(kv.Value)); break;
            }
        }
        return Val.Ref(o);
    }

    // ---------- Intel（情报）----------

    /// <summary>
    /// Intel 的完整语义：打出带 Intel n 的卡时，随机把对手 n 张手牌标成「已明牌」，
    /// 然后通知所有注册了 <c>OnIntelTriggered</c> 的卡。
    ///
    /// <para>
    /// 客户端把「已明牌」记在卡的 <c>seenByCipher</c> 上，用 <c>SetCardsSeenByCipher</c>
    /// 写入；这里是它的宿主实现。玩家自己的手牌永远是明的，所以只翻对手的。
    /// </para>
    /// <para>
    /// 参数（含 a[0] 接收者）：a[1] 数量, a[2] 触发者ID。数量为 0 或对手手牌为空时什么都不做。
    /// </para>
    /// </summary>
    private static Val? Intel(EngineHost h, Val[] a)
    {
        var seen = (int)a[1].AsInt();
        var instigatorId = (int)a[2].AsInt();
        // 调用点形态是 void SetCardsSeenByCipher(int32 cipher, int32 instigatorID, ... , bool& qqq)，
        // 末尾那个 out 槽必须回写，否则调用方读到空值（会当成「翻牌失败」）。
        if (seen <= 0) { Val.TrySetOut(a[^1], Val.True); return Val.Nothing; }

        // 触发者是哪一方，就翻另一方的牌
        var src = h.Engine.S.FindCard(instigatorId);
        var viewer = src?.Owner ?? h.Engine.Perspective;
        var foeHand = h.Engine.S.Player(GameState.Foe(viewer)).Hand;
        if (foeHand.Count == 0) { Val.TrySetOut(a[^1], Val.True); return Val.Nothing; }

        var n = Math.Min(seen, foeHand.Count);
        // 洗一份下标再取前 n 个：直接洗牌会打乱对手手牌顺序（那是可见信息，不能动）
        var idx = Enumerable.Range(0, foeHand.Count).ToList();
        h.Engine.S.Rng.Shuffle(idx);
        foreach (var i in idx.Take(n))
            h.Obj(foeHand[i]).Set("seenByCipher", Val.Of(true));

        h.Engine.FireIntelTriggers(src, n);
        Val.TrySetOut(a[^1], Val.True);
        return Val.Nothing;
    }

    // ---------- 自定义名后缀（逗号分隔的标记串）----------

    private static void SuffixAdd(EngineHost h, Val[] a, string field)
    {
        var c = h.Card_(a[0]);
        if (c is null) return;
        var k = h.Obj(c);
        var tag = a[1].AsStr();
        var cur = k.Get(field).AsStr();
        if (tag.Length > 0 && cur.Split(',').Contains(tag)) return;   // 幂等
        k.Set(field, Val.Of(cur.Length == 0 ? tag : cur + "," + tag));
    }

    private static void SuffixRemove(EngineHost h, Val[] a, string field)
    {
        var c = h.Card_(a[0]);
        if (c is null) return;
        var k = h.Obj(c);
        var tag = a[1].AsStr();
        var kept = k.Get(field).AsStr()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x != tag);
        k.Set(field, Val.Of(string.Join(",", kept)));
    }

    // ---------- 战役升级：直接改静态属性 ----------

    /// <summary>CampaignAdd{Attack,Defense} / CampaignIncreaseHeavyArmor 共用的加法。</summary>
    private static void StatAdd(EngineHost h, Val[] a, string which)
    {
        var c = h.Card_(a[0]);
        if (c is null) return;
        var v = (int)a[1].AsInt();
        switch (which)
        {
            case "attack": c.Attack += v; break;
            case "defense": c.Defense += v; c.MaxDefense += v; break;
            case "heavyArmor": c.HeavyArmor += v; break;
        }
    }

    private static void KwAdd(EngineHost h, Val[] a, Kw k)
    {
        if (h.Card_(a[0]) is { } c) c.Keywords |= k;
    }

    private static void KwRemove(EngineHost h, Val[] a, Kw k)
    {
        if (h.Card_(a[0]) is { } c) c.Keywords &= ~k;
    }

    /// <summary>
    /// 卡牌的「自定义能力」字符串集合。
    /// 蓝图用 CustomName2Add 往自定义名后缀里追加标记，能力判定就读这个后缀。
    /// </summary>
    private static string AbilityOf(EngineHost h, Card c)
    {
        var k = h.Obj(c);
        return k.Get("customName").AsStr() + "," + (c.Id ?? "");
    }

    /// <summary>
    /// 卡片自定义能力标记的匹配。原生判定（HasCantAttack 等）内部就是这个逻辑：
    /// 自定义名里带某个 token 就算有该能力。这里统一成一处，避免各写各的。
    /// </summary>
    private static bool HasAbilityToken(EngineHost h, Card c, string token)
        => !string.IsNullOrEmpty(token)
           && AbilityOf(h, c).Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GameplayTag 匹配。完整的 tag 语义要 GameplayTags 子系统，
    /// 模拟器里退化成「名字前缀匹配」：<c>Unit.Infantry</c> 能匹配 <c>Unit.Infantry.Guard</c>。
    /// </summary>
    private static bool TagMatch(Card c, string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        var k = c.Keywords;
        // 已知的常用 tag 直接映射到关键字位，避免只靠字符串猜。
        if (tag.Contains("Guard", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Guard);
        if (tag.Contains("Blitz", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Blitz);
        if (tag.Contains("Smokescreen", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Smokescreen);
        if (tag.Contains("Ambush", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Ambush);
        if (tag.Contains("Veteran", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Veteran);
        if (tag.Contains("Covert", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Covert);
        if (tag.Contains("Fury", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Fury);
        if (tag.Contains("Mobilize", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.Mobilize);
        if (tag.Contains("HeavyArmor", StringComparison.OrdinalIgnoreCase)) return k.HasFlag(Kw.HeavyArmor);
        if (tag.Contains("Infantry", StringComparison.OrdinalIgnoreCase)) return c.Type == CardType.Infantry;
        if (tag.Contains("Tank", StringComparison.OrdinalIgnoreCase)) return c.Type == CardType.Tank;
        if (tag.Contains("Artillery", StringComparison.OrdinalIgnoreCase)) return c.Type == CardType.Artillery;
        if (tag.Contains("Fighter", StringComparison.OrdinalIgnoreCase)) return c.Type == CardType.Fighter;
        if (tag.Contains("Bomber", StringComparison.OrdinalIgnoreCase)) return c.Type == CardType.Bomber;
        return false;
    }
}

/// <summary>
/// 单次效果调用超出宿主调用预算时抛出，用来把死循环的控制流拉回入口点。
///
/// <para>
/// 卡牌逻辑里有「循环重试直到找到组合」这类循环（例如
/// <c>card_event_mass_deployment</c> 的 <c>GetRandomKreditCombo</c>），
/// 一旦它依赖的数据在无头模拟里不存在（DataTable 没有加载），循环就永不退出，
/// 整个自对弈会挂死。入口点接住它、记一笔、继续下一局。
/// </para>
/// </summary>
public sealed class EffectBudgetException : Exception
{
    public EffectBudgetException(string func)
        : base($"效果调用超出预算，已在 {func} 处中断") { }
}





