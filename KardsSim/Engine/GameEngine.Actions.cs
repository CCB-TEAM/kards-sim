using KardsSim.Core;
using KardsSim.Effects;
using KardsSim.Kismet;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    // ---------- 合法动作 ----------

    /// <summary>枚举当前行动方的全部合法动作。</summary>
    public List<GameAction> LegalActions()
    {
        var res = new List<GameAction>();
        if (S.Done) return res;
        var me = S.Current;
        var p = S.Player(me);

        // 出牌
        for (var i = 0; i < p.Hand.Count; i++)
        {
            var c = p.Hand[i];
            if (c.KreditCost > p.Kredits) continue;
            if (!HasRoomFor(c)) continue;
            if (NeedsDeployTarget(c) && DeployTargets(c).Count == 0) continue;
            res.Add(new GameAction { Type = ActionType.PlayCard, HandIndex = i, SourceId = c.InstanceId });
        }

        // 攻击
        foreach (var u in S.UnitsOnBoard(me))
        {
            if (!CanAttack(u)) continue;
            foreach (var t in AttackTargetsFor(u))
                res.Add(new GameAction { Type = ActionType.Attack, SourceId = u.InstanceId, TargetId = t });
        }

        // 移动：只能提议该单位当前真正能走的方向
        foreach (var u in S.UnitsOnBoard(me))
        {
            if (CanMoveToFrontline(u))
                res.Add(new GameAction { Type = ActionType.MoveToFrontline, SourceId = u.InstanceId });
            if (CanMoveToSupport(u))
                res.Add(new GameAction { Type = ActionType.MoveToSupport, SourceId = u.InstanceId });
        }

        res.Add(new GameAction { Type = ActionType.EndTurn });
        return res;
    }

    /// <summary>出这张牌是否有位置放。location 到支援线，单位到前线或支援线。</summary>
    public bool HasRoomFor(Card c)
    {
        if (c.IsLocation) return SupportCount(c.Owner) < Rules.MaxCardsPerRow;
        if (!c.IsUnit) return true;                       // order 不占格
        if (CanEnterFrontline(c.Owner) && FrontlineCount(c.Owner) < Rules.MaxCardsPerRow) return true;
        return SupportCount(c.Owner) < Rules.MaxCardsPerRow;
    }

    public bool CanAttack(Card u)
    {
        if (u == null || u.Destroyed || !u.IsUnit) return false;
        if (u.Owner != S.Current) return false;
        if (!u.CanAct) return false;
        if (u.AttackedThisTurn) return false;
        // 召唤失调：当回合刚进场不能攻击，但 Blitz 是明确的例外 ——
        // 出牌时的 `if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;` 就是给 Blitz
        // 留口子的，这里要是无条件挡，那条豁免就变成死逻辑了。
        if (u.SummonedThisTurn && !u.Has(Kw.Blitz)) return false;
        if (u.TotalAttack <= 0) return false;
        // 支援线上的单位要够得着前线
        if (!u.OnFrontline && u.Range < Rules.SupportLineAttackRange) return false;
        return AttackTargetsFor(u).Count > 0;
    }

    /// <summary>
    /// 能否移动到前线。已经在前线的单位不能再「移入」前线。
    /// </summary>
    public bool CanMoveToFrontline(Card u)
    {
        if (!CanMoveAtAll(u)) return false;
        if (u.OnFrontline) return false;
        if (!CanEnterFrontline(u.Owner)) return false;
        return FrontlineCount(u.Owner) < Rules.MaxCardsPerRow;
    }

    /// <summary>能否从前线退回支援线。</summary>
    public bool CanMoveToSupport(Card u)
    {
        if (!CanMoveAtAll(u)) return false;
        if (!u.OnFrontline) return false;
        return SupportCount(u.Owner) < Rules.MaxCardsPerRow;
    }

    private bool CanMoveAtAll(Card u)
    {
        if (u == null || u.Destroyed || !u.IsUnit) return false;
        if (u.Owner != S.Current || !u.CanAct) return false;
        if (u.MovedThisTurn) return false;
        // 与 CanAttack 同一处例外：Blitz 当回合可以行动。
        // 客户端把这条拆在 CanAttack 的 deployment_sickness 和移动判定里，
        // 引擎集中在这里表述。
        if (u.SummonedThisTurn && !u.Has(Kw.Blitz)) return false;
        return true;
    }

    /// <summary>
    /// 可攻击的目标。
    ///
    /// <para>
    /// 分两层，<b>职责不能混</b>：
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>结构可达性</b>（这一层留在引擎）：前线被占时后排与 HQ 打不到、
    /// HQ 是否暴露。这是「棋盘结构」问题，客户端把它放在选目标 UI 的过滤里，
    /// 不在逐对判定函数内。
    /// </item>
    /// <item>
    /// <b>逐对否决权</b>（这一层交给客户端）：<c>cardsCheckFunctions.CanAttack</c>。
    /// Guard 掩护、烟幕、战斗机保护、自定义禁令、射程、召唤失调、
    /// 费用不足……全在里面。这是权威实现，引擎不再自己重写一遍。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// 之前这两层都是手写的，结果 18 条客户端规则里有 11 条没实现
    /// （Guard 整条链是空的）。现在逐对判定走客户端实现，
    /// <c>--mode rules</c> 会验证两边一致。
    /// </para>
    /// </summary>
    public List<int> AttackTargetsFor(Card attacker)
    {
        var res = new List<int>();
        var foe = GameState.Foe(attacker.Owner);
        var enemyFront = S.Frontline.Where(c => c.Owner == foe).ToList();

        if (enemyFront.Count > 0)
        {
            // 前线被占：只能打前线。但仍然要过客户端的逐对判定
            // （Guard/烟幕/自定义禁令在前线同样生效）。
            foreach (var t in enemyFront)
                if (ClientCanAttack(attacker, t)) res.Add(t.InstanceId);
            return res;    // 前线被占，HQ 与支援线打不到
        }

        if (HqExposed(foe) || S.FrontlineOwner != attacker.Owner)
        {
            if (ClientCanAttackHq(attacker)) res.Add(0);   // 0 约定代表敌方 HQ
        }

        foreach (var t in S.Player(foe).Board)
            if (ClientCanAttack(attacker, t)) res.Add(t.InstanceId);
        return res;
    }

    /// <summary>
    /// 客户端规则判定的调试出口。设为非 null 时，每次问客户端规则库都会
    /// 回调入参与结果 —— 规则库只回一个字符串原因，没有这个出口就只能靠猜。
    /// 默认 null（零开销）。
    /// </summary>
    public Action<string> Probe;

    /// <summary>
    /// 同 <see cref="ClientCanAttack(Card, Card)"/>，另外给出客户端填的
    /// <c>failReason</c>。
    ///
    /// <para>
    /// 调用约定：<c>{ 接收者, attackerCard, defenderCard, attackerKredits,
    /// currentTurn, out cardsInAttackedLocation, __WorldContext,
    /// out canAttack, out failReason, out reasonParam1 }</c>。
    /// </para>
    ///
    /// <para>
    /// 这个出口是必要的：客户端规则库对「为什么不能打」只回一个字符串，
    /// 而引擎侧看到的只是「目标列表为空」。不把原因透出来，
    /// 排查就只能靠猜 —— Guard 整条不生效那次就是这么被漏过去的。
    /// </para>
    /// </summary>
    public bool ClientCanAttack(Card atk, Card def) => ClientCanAttack(atk, def, out _);

    public bool ClientCanAttack(Card atk, Card def, out string failReason)
    {
        failReason = null;
        if (Host is null) return true;
        var fn = Generated.FnIndex.Find("cardsCheckFunctions", "CanAttack");
        if (fn is null) return true;

        try
        {
            var can = false;
            var cardsInLoc = Val.Nothing;
            var reason = Val.Nothing;
            var p1 = Val.Nothing;
            Probe?.Invoke($"CanAttack 入参: atk={atk.Id}#{atk.InstanceId} loc={Host.GetMember(Val.Ref(Host.Obj(atk)), "location")} " +
                          $"def={def?.Id}#{def?.InstanceId} loc={(def is null ? "none" : Host.GetMember(Val.Ref(Host.Obj(def)), "location").ToString())} " +
                          $"turn={S.Turn} kredits={S.Player(atk.Owner).Kredits}");
            fn(Host, Val.Nothing, new Val[]
            {
                Val.Ref(Host.Obj(atk)),
                def is null ? Val.Nothing : Val.Ref(Host.Obj(def)),
                Val.Of(S.Player(atk.Owner).Kredits),
                Val.Of(S.Turn),
                Val.Out(v => cardsInLoc = v),
                Val.Nothing,                       // __WorldContext
                Val.Out(v => can = v.AsBool()),
                Val.Out(v => reason = v),
                Val.Out(v => p1 = v),
            });
            Probe?.Invoke($"CanAttack 结果: can={can} reason='{reason.AsStr()}' p1='{p1.AsStr()}'");
            failReason = reason.AsStr();
            return can;
        }
        catch (Exception ex)
        {
            Unhandled.Add($"CanAttack 抛异常: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 攻方能否直击敌方 HQ。
    ///
    /// <para>
    /// 客户端把 HQ 也当成一张「地点卡」走同一条 <c>CanAttack</c> 判定
    /// （<c>hq_is_being_garded</c> 就是这么来的：守方 HQ 的 <c>isBeingGuarded</c> 为真 → 否决）。
    /// 但引擎把 HQ 建模成一个整数血量、没有卡对象，所以这里只判结构条件，
    /// 真正的 HQ 掩护由 <see cref="HqIsGuarded"/> 补上。
    /// </para>
    /// </summary>
    public bool ClientCanAttackHq(Card atk) => !HqIsGuarded(atk.Owner);

    /// <summary>
    /// 敌方 HQ 是否被 Guard 掩护。
    ///
    /// <para>
    /// 客户端 <c>IsLocation</c> 判定的是「目标是不是地点卡」——
    /// 引擎没有地点卡对象，所以用等价条件：<b>对方支援线上存在未被禁止的 Guard 单位</b>。
    /// 这是客户端 <c>hq_is_being_garded</c> 那一条的对应实现。
    /// </para>
    /// </summary>
    public bool HqIsGuarded(Side attacker)
    {
        var foe = GameState.Foe(attacker);
        return S.Player(foe).Board.Any(c =>
            !c.Destroyed && c.Has(Kw.Guard) &&
            Host?.Obj(c).Get("isBeingGuarded").AsBool() != true &&
            !hqGuardBypassed(c));
    }

    private static bool hqGuardBypassed(Card c) => c.Has(Kw.Covert);

    // ---------- 执行 ----------

    public bool Apply(GameAction a)
    {
        if (S.Done) return false;
        // 「正在处理一个动作」——客户端规则库用 IsActionProcess 区分
        // 「动作结算中」和「回合/布阵等非动作时刻」，两边的效果时序不同：
        // 例如 MakeVeteran 里 IsActionProcess 为真才**立刻**发 OnBecomingVeteran
        // （打 HQ 3 点），为假则改由事件循环延后发。恒返回 false 会让
        // OnBecomingVeteran 这一步被整个跳过 —— 老兵变了但 HQ 不掉血。
        ActionInProgress = true;
        try
        {
            var ok = a.Type switch
            {
                ActionType.EndTurn => DoEndTurn(),
                ActionType.PlayCard => DoPlayCard(a),
                ActionType.Attack => DoAttack(a),
                ActionType.MoveToFrontline => DoMove(a, true),
                ActionType.MoveToSupport => DoMove(a, false),
                _ => false,
            };
            if (ok) Steps++;
            return ok;
        }
        finally { ActionInProgress = false; }
    }

    /// <summary>
    /// 是否正在结算一个动作（对应客户端 <c>IsActionProcess</c>）。
    /// 规则库用它决定效果是立刻结算还是延后到事件循环。
    /// </summary>
    public bool ActionInProgress { get; private set; }

    private bool DoEndTurn()
    {
        EndTurn();
        return true;
    }

    private bool DoPlayCard(GameAction a)
    {
        var p = S.Player(S.Current);
        if (a.HandIndex < 0 || a.HandIndex >= p.Hand.Count) return false;
        var c = p.Hand[a.HandIndex];
        if (c.KreditCost > p.Kredits || !HasRoomFor(c)) return false;

        p.Hand.RemoveAt(a.HandIndex);
        p.Kredits -= c.KreditCost;
        p.KreditsSpentThisTurn += c.KreditCost;
        S.Log.Line($"  {S.Current} plays {c.Id} (-{c.KreditCost}K)");

        c.ChosenTargetId = a.TargetId;

        if (c.IsOrder)
        {
            PlayOrder(c);
        }
        else
        {
            c.Loc = Loc.Board;
            c.SummonedThisTurn = true;
            c.EnterPlayTurn = S.Turn;   // 客户端规则库靠它判召唤失调
            if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;   // 非 Blitz 当回合不能打

            // 单位优先上前线；前线不可用时放支援线
            if (TryPlaceOnFrontline(c)) { }
            else { c.OnFrontline = false; p.Board.Add(c); }
            AfterBoardChange();
        }

        FirePlayTriggers(c);
        FireEnterPlayTriggers(c);

        // 部署效果
        if (c.Has(Kw.Deployment))
        {
            RunCardEffect(c, Trigger.OnDeploymentEffectTriggered);
            FireTrigger(Trigger.OnDeploymentEffectTriggered, c);
        }
        RunCardEffect(c, Trigger.NotAvailable);   // OnPlayedFromHand 效果入口

        // Intel 不在这里发：游戏自己的 CardPlayedFromHand（BP_CardFunctions）里已经有
        //   if (cardPlayed.cipher > 0) SetCardsSeenByCipher(cipher, cardID)
        // 再发一次会翻两倍张数。那条链路由 CardPlayedFromHand 走。
        return true;
    }

    private bool TryPlaceOnFrontline(Card c)
    {
        if (!c.IsUnit) return false;
        if (!CanEnterFrontline(c.Owner)) return false;
        if (FrontlineCount(c.Owner) >= Rules.MaxCardsPerRow) return false;
        c.OnFrontline = true;
        c.Loc = Loc.Frontline;
        S.Frontline.Add(c);
        var before = S.FrontlineOwner;
        if (S.FrontlineOwner == Side.None) S.FrontlineOwner = c.Owner;
        if (before != S.FrontlineOwner) S.Log.Line($"  frontline owner: {before} -> {S.FrontlineOwner}");
        // 只有归属真的变了才发归属变更：客户端也是在 UpdateFrontlineIfNeeded 里判断的，
        // 无脑发会让「前线归属改变」类卡牌反复触发。
        if (before != S.FrontlineOwner) Fire(Trigger.OnFrontlineOwnershipChange, c);
        Fire(Trigger.OnOtherCardMoveToFrontline, c);
        FireLocationMovedTriggers(c);
        AfterBoardChange();
        return true;
    }

    private bool DoAttack(GameAction a)
    {
        var atk = S.FindCard(a.SourceId);
        if (!CanAttack(atk)) return false;
        if (!AttackTargetsFor(atk).Contains(a.TargetId)) return false;

        // 目标必须在触发之前解析。OnBeforeOtherCardAttacks 里的触发器可能把目标打掉，
        // 那时攻击已经算消耗掉了——不能回头报「非法动作」，否则合法动作枚举与执行会不一致。
        var def = a.TargetId == 0 ? null : S.FindCard(a.TargetId);

        // 「本次攻击的合法目标」必须在标记已攻击**之前**取。
        // AttackTargetsFor 走的是客户端规则库，而它的 CanAttack 里有
        // has_already_attacked 这一道门（读 attackCountThisTurn，镜像自
        // AttackedThisTurn）。先标记再复查的话，复查必然被自己刚设的标志否掉，
        // 于是每次攻击都在下面第 368 行「fizzled (target gone)」——
        // 表现是攻击永不结算、单位永远死不了，而且日志还会误报成目标消失。
        var legalTargets = AttackTargetsFor(atk);

        atk.AttackedThisTurn = true;
        FireTrigger(Trigger.OnBeforeOtherCardAttacks, atk);
        FireTargetConfirmTriggers(atk, def);

        if (a.TargetId == 0)
        {
            var foe = GameState.Foe(atk.Owner);
            S.Log.Line($"  {atk.Id} ({atk.TotalAttack}) attacks {foe} HQ");
            DamageHq(foe, atk.TotalAttack);
            FireAttackEndTriggers(atk, null);
            return true;
        }

        // 触发期间目标已消失：攻击照旧消耗，但不再结算战斗
        if (def == null || def.Destroyed || !legalTargets.Contains(a.TargetId))
        {
            S.Log.Line($"    {atk.Id} attack fizzled (target gone)");
            FireAttackEndTriggers(atk, null);
            return true;
        }

        var (dmgToDef, dmgToAtk) = CalculateDamage(atk, def);
        S.Log.Line($"  {atk.Id} ({atk.TotalAttack}) attacks {def.Id} ({def.TotalDefense}) cost={dmgToAtk}");
        S.Log.Line($"    => {atk.Id} takes {dmgToAtk}, {def.Id} takes {dmgToDef}");

        // 同时结算：先各自判定是否被摧毁，再统一处理
        var defDies = dmgToDef > 0 && dmgToDef >= def.TotalDefense;
        var atkDies = dmgToAtk > 0 && dmgToAtk >= atk.TotalDefense;

        if (dmgToDef > 0) def.Defense -= dmgToDef;
        if (dmgToAtk > 0) atk.Defense -= dmgToAtk;

        FireTrigger(Trigger.OnOtherCardDealDamage, atk);
        // 战斗击杀：killer 与 destroyedInCombat 必须传对。
        // 直译产物里 `if (!destroyedInCombat) return;` 和 `killer.cardID != self.cardID`
        // 是「攻击并摧毁单位后触发」这类效果（如 7. SCHÜTZEN 变老兵）的唯一入口条件。
        if (defDies) DestroyCard(def, killer: atk, inCombat: true);
        if (atkDies) DestroyCard(atk, killer: def, inCombat: true);
        else if (dmgToAtk == 0 && dmgToDef == 0) { /* 双方都打不动 */ }

        FireAttackEndTriggers(atk, def);
        return true;
    }

    private bool DoMove(GameAction a, bool toFront)
    {
        var u = S.FindCard(a.SourceId);
        if (toFront ? !CanMoveToFrontline(u) : !CanMoveToSupport(u)) return false;

        if (toFront)
        {
            if (u.OnFrontline) return false;
            S.Player(u.Owner).Board.Remove(u);
            if (!TryPlaceOnFrontline(u)) { S.Player(u.Owner).Board.Add(u); return false; }
        }
        else
        {
            if (!u.OnFrontline) return false;
            if (SupportCount(u.Owner) >= Rules.MaxCardsPerRow) return false;
            S.Frontline.Remove(u);
            u.OnFrontline = false;
            u.Loc = Loc.Board;
            S.Player(u.Owner).Board.Add(u);
            var hadOwner = S.FrontlineOwner;
            if (S.Frontline.Count == 0) S.FrontlineOwner = Side.None;
            S.Log.Line($"  {u.Id} retreats to support line");
            FireMoveFromFrontlineTriggers(u);
            FireRetreatTriggers(u);
        }
        u.MovedThisTurn = true;
        FireLocationMovedTriggers(u);
        AfterBoardChange();
        return true;
    }

    /// <summary>
    /// 忠实移植客户端 CalculateDamageDealt：
    ///  - 攻击方无法还手（火炮、轰炸机非战斗机/防空、休克）则反击伤害为 0
    ///  - 伤害减去目标的重甲
    ///  - 目标 TotalDefense 归零即摧毁
    /// </summary>
    public (int toDefender, int toAttacker) CalculateDamage(Card atk, Card def)
    {
        var a = atk.TotalAttack;
        var d = def.TotalAttack;

        // 目标的重甲减免
        a = Math.Max(0, a - Math.Max(0, def.HeavyArmor));

        // 攻击方能否被反击
        var canFightBack = true;
        if (atk.Type == CardType.Artillery) canFightBack = false;
        if (atk.Type == CardType.Bomber && def.Type is not (CardType.Fighter or CardType.Artillery)) canFightBack = false;
        if (atk.Pinned || atk.Suppressed) canFightBack = false;
        if (!canFightBack) d = 0;

        d = Math.Max(0, d - Math.Max(0, atk.HeavyArmor));
        return (a, d);
    }
}

/// <summary>一个具体的动作。TargetId == 0 表示敌方 HQ。</summary>
public sealed class GameAction
{
    public ActionType Type;
    public int SourceId = -1;
    public int TargetId = -1;
    public int HandIndex = -1;

    public override string ToString() => Type switch
    {
        ActionType.EndTurn => "EndTurn",
        ActionType.PlayCard => $"Play(hand={HandIndex})",
        ActionType.Attack => $"Attack({SourceId}->{(TargetId == 0 ? "HQ" : TargetId.ToString())})",
        ActionType.MoveToFrontline => $"MoveFront({SourceId})",
        ActionType.MoveToSupport => $"MoveSupport({SourceId})",
        ActionType.UseAbility => $"Ability({SourceId})",
        _ => Type.ToString(),
    };
}