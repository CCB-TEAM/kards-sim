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

    public EngineHost(GameEngine engine)
    {
        Engine = engine;
        Globals["GameStateRef"] = Val.Ref(GameStateRef);
        GameStateRef.Set("cardFunction", Val.Ref(CardFunctions));
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
                if (c is not null) c.Defense = Math.Min(c.MaxDefense, c.Defense + (int)a[3].AsInt());
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



