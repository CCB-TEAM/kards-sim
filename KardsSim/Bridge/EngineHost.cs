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

    /// <summary>统计：各来源的分派次数，便于看出瓶颈在哪。</summary>
    public readonly Dictionary<string, int> DispatchByClass = new(StringComparer.Ordinal);

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
        _byId[c.InstanceId] = k;
        _byObj[k] = c;
        return k;
    }

    public Card Card_(Val v)
    {
        if (v.O is KObj k && _byObj.TryGetValue(k, out var c)) return c;
        if (v.K == VKind.Int) return Engine.S.FindCard((int)v.AsInt());
        return null;
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

    // ===================== 分派 =====================

    /// <summary>
    /// 三级分派：GameState 原语 → BaseCardObject 原语 → 转译产物递归。
    ///
    /// 顺序很重要：原语必须在最前，否则会被转译产物的同名函数抢走（那些函数最终
    /// 又要调回原语，形成无限递归）。
    /// </summary>
    private Val? Handle(string f, Val[] a)
    {
        DispatchByClass[f] = DispatchByClass.TryGetValue(f, out var n) ? n + 1 : 1;

        // 1. 引擎状态原语（真正改 GameState 的叶子）
        if (Primitives.TryGetValue(f, out var impl)) return impl(this, a);

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

            return Dispatch(f, a, recv, rest);
        }
        finally
        {
            if (--depth <= 0) _depth.Remove(f);
            else _depth[f] = depth;
        }
    }

    /// <summary>同名调用的当前递归深度（转发壳防护用）。</summary>
    private readonly Dictionary<string, int> _depth = new(StringComparer.Ordinal);

    private Val? Dispatch(string f, Val[] a, Val recv, Val[] rest)
    {
        // 2a. cardFunction.* → 转译出来的 BP_CardFunctions（它自己还会继续 H.Call）
        if (recv.O is KObj cf && ReferenceEquals(cf, CardFunctions))
        {
            var fn = FnIndex.Find("BP_CardFunctions", f);
            if (fn is not null) return fn(this, Val.Ref(CardFunctions), rest);

            var fn2 = FnIndex.Find("BP_GameState_Battle", f);
            if (fn2 is not null) return fn2(this, Val.Ref(CardFunctions), rest);
            return null;   // 交给下一层记为未实现
        }

        // 2b. GameStateRef.* → 转译出来的 BP_GameState_Battle
        if (recv.O is KObj gs && ReferenceEquals(gs, GameStateRef))
        {
            var fn = FnIndex.Find("BP_GameState_Battle", f);
            if (fn is not null) return fn(this, Val.Ref(GameStateRef), rest);
            return null;
        }

        // 2c. 卡牌成员：先查卡自己的资产（事件与 ubergraph），再查 BP_CardFunctions
        if (recv.O is KObj co)
        {
            // 事件存根转进 ubergraph 用的是 EX_LocalFinalFunction，
            // 接收者就是这张卡自己，而函数名里带着资产名，可以直接定位。
            if (f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
            {
                var uber = FnIndex.Find(f["ExecuteUbergraph_".Length..], f);
                if (uber is not null) return uber(this, recv, rest);
            }

            var assetId = _byObj.TryGetValue(co, out var card) ? card.Id : co.Get("defId").AsStr();
            if (!string.IsNullOrEmpty(assetId))
            {
                var fn = FnIndex.Find(assetId, f);
                if (fn is not null) return fn(this, recv, rest);
            }
            var shared = FnIndex.Find("BP_CardFunctions", f);
            if (shared is not null) return shared(this, Val.Ref(CardFunctions), rest);
            return null;
        }

        // 2d. Notifier：全部 no-op（无头模拟不需要 UI 通知）
        if (recv.O is KObj nt && ReferenceEquals(nt, Notifier)) return Val.Nothing;

        // 2e. 没有接收者的静态调用：蓝图里经 GameStateRef / cardFunction 调的
        if (recv.O is null)
        {
            var fn = FnIndex.Find("BP_GameState_Battle", f);
            if (fn is not null) return fn(this, Val.Ref(GameStateRef), rest);
            var fn3 = FnIndex.Find("BP_CardFunctions", f);
            if (fn3 is not null) return fn3(this, Val.Ref(CardFunctions), rest);
        }

        return null;
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
    private static readonly Dictionary<string, Func<EngineHost, Val[], Val?>> Primitives =
        new(StringComparer.Ordinal)
        {
            // ---------- 查询 ----------
            ["IsValid"] = (h, a) => Val.Of(h.Card_(a[1]) is not null),

            ["IsLocatedOnBoard"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return Val.Of(c is not null && c.Loc is Loc.Board or Loc.Frontline);
            },

            ["IsUnit"] = (h, a) => Val.Of(h.Card_(a[0])?.IsUnit ?? false),
            ["IsInfantry"] = (h, a) => Val.Of(h.Card_(a[0])?.Type == CardType.Infantry),
            ["IsTank"] = (h, a) => Val.Of(h.Card_(a[0])?.Type == CardType.Tank),
            ["IsFighter"] = (h, a) => Val.Of(h.Card_(a[0])?.Type == CardType.Fighter),
            ["IsBomber"] = (h, a) => Val.Of(h.Card_(a[0])?.Type == CardType.Bomber),
            ["IsArtillery"] = (h, a) => Val.Of(h.Card_(a[0])?.Type == CardType.Artillery),
            ["IsOrder"] = (h, a) => Val.Of(h.Card_(a[0])?.IsOrder ?? false),
            ["IsLocation"] = (h, a) => Val.Of(h.Card_(a[0])?.IsLocation ?? false),

            ["GetCardFromID"] = (h, a) =>
            {
                var c = h.Engine.S.FindCard((int)a[1].AsInt());
                Val.TrySetOut(a[^1], c is null ? Val.Nothing : Val.Ref(h.Obj(c)));
                return Val.Nothing;
            },

            ["GetOppositeSide"] = (h, a) => Val.Of((int)GameState.Foe((Side)a[1].AsInt())),
            ["GetSide"] = (h, a) => Val.Of((int)h.Card_(a[0])!.Owner),

            // ---------- 类型 / 属性判定（全部 out 形参，无返回值） ----------
            // 签名取自 UHT：void IsAirUnit(bool& isIt) 这类。
            // 注意 out 槽在实参数组里的位置 —— 有形参的在末尾，无形参的就在 a[1]。
            ["IsVeteran"] = (h, a) => Out(a, h.Card_(a[0])?.Veteran ?? false),
            ["getHasVeteranUpgrade"] = (h, a) => Out(a, h.Card_(a[0])?.Veteran ?? false),
            ["IsAirUnit"] = (h, a) =>
            {
                var t = h.Card_(a[0])?.Type;
                return Out(a, t is CardType.Fighter or CardType.Bomber);
            },
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
            ["getAndDecryptAttack"] = (h, a) => Out(a, h.Card_(a[0])?.Attack ?? 0),
            ["getTotalAttack"] = (h, a) => Out(a, h.Card_(a[0]) is { } tc ? tc.TotalAttack : 0),
            ["getTotalHeavyArmor"] = (h, a) => Out(a, h.Card_(a[0])?.HeavyArmor ?? 0),
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
            ["HasCustomAbility"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                var ability = a[1].AsStr();
                return Out(a, c is not null && AbilityOf(h, c).Contains(ability, StringComparison.OrdinalIgnoreCase));
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
            ["getHasMobilize"] = (h, a) => Out(a, HasKw(h.Card_(a[0]), Kw.Mobilize)),

            // ---------- 位置 / 状态查询 ----------
            ["IsLocatedInDeck"] = (h, a) => Out(a, h.Card_(a[0])?.Loc == Loc.Deck),
            ["IsPinned"] = (h, a) => Out(a, h.Card_(a[0])?.Pinned ?? false),
            ["IsGotcha"] = (h, a) =>
            {
                // Gotcha（伏击/反制）是盖在场上没翻开的状态，等价于 covert + 未 reveal
                var c = h.Card_(a[0]);
                return Out(a, c is not null && c.Covert && h.Obj(c).Get("gotcha").AsBool());
            },
            ["ShouldGotchaTrigger"] = (h, a) =>
            {
                // 参数：(triggerCard, out shouldIt)。只有盖着的反制卡才响应。
                var self = h.Card_(a[0]);
                var should = self is not null && self.Covert && !self.Destroyed
                             && h.Obj(self).Get("gotcha").AsBool();
                return Out(a, should);
            },
            ["Get Is Server Config"] = (h, a) => Out(a, false),
            ["GetClientSide"] = (h, a) => Out(a, (int)h.Engine.Perspective),

            // ---------- 攻防的「解密」读法 ----------
            // 客户端把 attack/defense 加密存着防内存修改；无头模拟直接读明文。
            ["getAndDecryptDefense"] = (h, a) => Out(a, h.Card_(a[0])?.Defense ?? 0),
            ["getAndDecryptKreditBuff"] = (h, a) => Out(a, h.Card_(a[0])?.BuffCost ?? 0),
            ["setAndEncryptKreditBuff"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                if (c is not null) c.BuffCost = (int)a[1].AsInt();
                return Val.Nothing;
            },
            ["getKreditTempBuffAmount"] = (h, a) => Out(a, h.Card_(a[0])?.BuffCost ?? 0),
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
            ["GetEncryptionKey"] = (h, a) => Val.Of(0),
            ["InitStaticGameplayTags"] = (h, a) => Val.Nothing,
            ["GetStaticCampaignName"] = (h, a) => Val.Of(""),
            ["SetObjectiveCounter"] = (h, a) => Val.Nothing,
            ["NotiferStarCounterChanged"] = (h, a) => Val.Nothing,
            ["OnCreateCard"] = (h, a) => Val.Nothing,
            ["GetDataTableRowFromName"] = (h, a) =>
            {
                // 数据表行：无头模拟不加载 DataTable，返回空结构而不是 null，
                // 否则调用方解引用会炸。
                return Val.Nothing;
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
            ["GetStaticCard"] = (h, a) => Val.Nothing,

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
            ["IsActionProcess"] = (h, a) => Out(a, false),   // 表现层动画状态，无头模拟恒 false
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

            // ---------- 三选一：真实的决策点，交给可插拔的决策器 ----------
            // 蓝图的 WhichChooseOne 返回 EnumChooseOneCardBeingPlayed（Card_0 / Card_1），
            // 卡自己再按这个值分支。恒返回 0 是确定性的，但等于砍掉了一个动作维度 ——
            // 训练时 AI 学不到「该选哪支」。所以留成钩子，由后端决定。
            ["WhichChooseOne"] = (h, a) => Out(a, h.ChooseOne(a.Length > 0 ? h.Card_(a[0]) : null)),

            // ---------- 费用 / 老兵版本的读取 ----------
            ["getAndDecryptKredit"] = (h, a) => Out(a, h.Card_(a[0])?.KreditCost ?? 0),
            ["getStaticVeteranUpgrade"] = (h, a) =>
            {
                // 老兵版本的静态卡对象：数据里没有独立资产，返回 none 让调用方走原版分支
                Val.TrySetOut(a[^1], Val.Nothing);
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

            ["getTotalDefense"] = (h, a) =>
            {
                var c = h.Card_(a[0]);
                return c is null ? Val.Of(0) : Val.Of(c.TotalDefense);
            },
            ["getAttackTempBuffAmount"] = (h, a) => Val.Of(h.Card_(a[0])?.BuffAttack ?? 0),

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

            ["DamageCard"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null) h.Engine.DamageCard(c, (int)a[3].AsInt());
                return Val.Nothing;
            },

            ["HealCard"] = (h, a) =>
            {
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
            ["RemovePin"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null && c.Pinned)
                {
                    c.Pinned = false;
                    h.Engine.FirePinTriggers(c, false);
                }
                return Val.Nothing;
            },
            ["SuppressUnit"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null && !c.Suppressed)
                {
                    c.Suppressed = true;
                    h.Engine.FireSuppressTriggers(c);
                }
                return Val.Nothing;
            },

            ["DestroyCard"] = (h, a) =>
            {
                var c = h.Card_(a[1]);
                if (c is not null) h.Engine.DestroyCard(c);
                return Val.Nothing;
            },

            // ---------- 红币 ----------
            ["getKreditBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Kredits),
            ["setKreditBySide"] = (h, a) =>
            {
                h.Engine.S.Player((Side)a[1].AsInt()).Kredits = (int)a[2].AsInt();
                return Val.Nothing;
            },
            ["getKreditSlotBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).KreditSlots),
            ["setKreditSlotBySide"] = (h, a) =>
            {
                h.Engine.S.Player((Side)a[1].AsInt()).KreditSlots = (int)a[2].AsInt();
                return Val.Nothing;
            },
            ["getMaxPossibleKredits"] = (h, a) => Val.Of(Rules.MaxKredits),

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
            ["GetLocationCardBySide"] = (h, a) =>
            {
                var s = (Side)a[1].AsInt();
                var loc = (Loc)a[2].AsInt();
                var c = loc switch
                {
                    Loc.Board => h.Engine.S.Player(s).Board.FirstOrDefault(),
                    Loc.Frontline => h.Engine.S.Frontline.FirstOrDefault(x => x.Owner == s),
                    Loc.Hand => h.Engine.S.Player(s).Hand.FirstOrDefault(),
                    _ => null,
                };
                Val.TrySetOut(a[^1], c is null ? Val.Nothing : Val.Ref(h.Obj(c)));
                return Val.Nothing;
            },

            ["GetAllCardInBattle"] = (h, a) =>
            {
                var arr = new KArr(h.Engine.S.AllOnBoard().Select(c => Val.Ref(h.Obj(c))));
                Val.TrySetOut(a[^1], Val.Ref(arr));
                return Val.Nothing;
            },

            // ---------- 抽牌 ----------
            ["DrawCardsFromDeckBySide"] = (h, a) =>
            {
                h.Engine.DrawTo(h.Engine.S.Player((Side)a[1].AsInt()), (int)a[2].AsInt());
                return Val.Nothing;
            },
            ["GetDeckSizeBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Deck.Count),
            ["GetHandSizeBySide"] = (h, a) =>
                Val.Of(h.Engine.S.Player((Side)a[1].AsInt()).Hand.Count),

            // ---------- 回合 ----------
            ["GetTurnNumber"] = (h, a) => Val.Of(h.Engine.S.Turn),
            ["GetFrontlineOwnerSide"] = (h, a) => Val.Of((int)h.Engine.S.FrontlineOwner),
            ["IsSideActive"] = (h, a) => Val.Of(h.Engine.S.Current == (Side)a[1].AsInt()),

            // ---------- 卡牌自状态（JSON_* / PersistCustomFields） ----------
            // 蓝图用它们把「本卡自己的附加状态」挂在卡上。这里直接映射到 KObj 字段，
            // 与客户端行为等价，且不需要真的序列化成 JSON。
            ["JSON_GetInt"] = (h, a) =>
                Val.Of(h.Card_(a[0]) is { } c ? h.Obj(c).Get("j_" + a[1].AsStr()).AsInt() : 0),
            ["JSON_SetInt"] = (h, a) =>
            {
                if (h.Card_(a[0]) is { } c) h.Obj(c).Set("j_" + a[1].AsStr(), Val.Of((int)a[2].AsInt()));
                return Val.Nothing;
            },
            ["JSON_GetBool"] = (h, a) =>
                Val.Of(h.Card_(a[0]) is { } c && h.Obj(c).Get("j_" + a[1].AsStr()).AsBool()),
            ["JSON_SetBool"] = (h, a) =>
            {
                if (h.Card_(a[0]) is { } c) h.Obj(c).Set("j_" + a[1].AsStr(), Val.Of(a[2].AsBool()));
                return Val.Nothing;
            },
            ["JSON_Clear"] = (h, a) =>
            {
                if (h.Card_(a[0]) is { } c)
                {
                    var k = h.Obj(c);
                    foreach (var key in k.Fields.Keys.Where(x => x.StartsWith("j_", StringComparison.Ordinal)).ToList())
                        k.Fields.Remove(key);
                }
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
        if (seen <= 0) return Val.Nothing;

        // 触发者是哪一方，就翻另一方的牌
        var src = h.Engine.S.FindCard(instigatorId);
        var viewer = src?.Owner ?? h.Engine.Perspective;
        var foeHand = h.Engine.S.Player(GameState.Foe(viewer)).Hand;
        if (foeHand.Count == 0) return Val.Nothing;

        var n = Math.Min(seen, foeHand.Count);
        // 洗一份下标再取前 n 个：直接洗牌会打乱对手手牌顺序（那是可见信息，不能动）
        var idx = Enumerable.Range(0, foeHand.Count).ToList();
        h.Engine.S.Rng.Shuffle(idx);
        foreach (var i in idx.Take(n))
            h.Obj(foeHand[i]).Set("seenByCipher", Val.Of(true));

        h.Engine.FireIntelTriggers(src, n);
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





