using KardsSim.Core;
using KardsSim.Effects;

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
        if (u.AttackedThisTurn || u.SummonedThisTurn) return false;
        if (u.TotalAttack <= 0) return false;
        // 支援线上的单位要够得着前线
        if (!u.OnFrontline && u.Range < Rules.SupportLineAttackRange) return false;
        return AttackTargetsFor(u).Count > 0;
    }

    /// <summary>能否移动到前线。已经在前线的单位不能再「移入」前线。</summary>
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
        return !u.MovedThisTurn && !u.SummonedThisTurn;
    }

    /// <summary>
    /// 可攻击的目标。
    /// 规则：前线有敌方单位时只能打前线；否则可以打敌方支援线单位或 HQ。
    /// </summary>
    public List<int> AttackTargetsFor(Card attacker)
    {
        var res = new List<int>();
        var foe = GameState.Foe(attacker.Owner);
        var enemyFront = S.Frontline.Where(c => c.Owner == foe).ToList();

        if (enemyFront.Count > 0)
        {
            foreach (var t in enemyFront) res.Add(t.InstanceId);
            return res;    // 前线被占，HQ 与支援线打不到
        }

        if (HqExposed(foe) || S.FrontlineOwner != attacker.Owner)
            res.Add(0);    // 0 约定代表敌方 HQ

        foreach (var t in S.Player(foe).Board) res.Add(t.InstanceId);
        return res;
    }

    // ---------- 执行 ----------

    public bool Apply(GameAction a)
    {
        if (S.Done) return false;
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
            if (!c.Has(Kw.Blitz)) c.AttackedThisTurn = true;   // 非 Blitz 当回合不能打

            // 单位优先上前线；前线不可用时放支援线
            if (TryPlaceOnFrontline(c)) { }
            else { c.OnFrontline = false; p.Board.Add(c); }
        }

        FireTrigger(Trigger.OnOtherCardPlayedFromHand, c);
        FireTrigger(Trigger.OnOtherCardEnterPlay, c);

        // 部署效果
        if (c.Has(Kw.Deployment))
        {
            RunCardEffect(c, Trigger.OnDeploymentEffectTriggered);
            FireTrigger(Trigger.OnDeploymentEffectTriggered, c);
        }
        RunCardEffect(c, Trigger.NotAvailable);   // OnPlayedFromHand 效果入口

        FireTrigger(Trigger.OnOtherCardDeveloped, c);
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
        FireTrigger(Trigger.OnFrontlineOwnershipChange, c);
        FireTrigger(Trigger.OnOtherCardMoveToFrontline, c);
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

        atk.AttackedThisTurn = true;
        FireTrigger(Trigger.OnBeforeOtherCardAttacks, atk);

        if (a.TargetId == 0)
        {
            var foe = GameState.Foe(atk.Owner);
            S.Log.Line($"  {atk.Id} ({atk.TotalAttack}) attacks {foe} HQ");
            DamageHq(foe, atk.TotalAttack);
            FireTrigger(Trigger.OnOtherCardAttacks, atk);
            return true;
        }

        // 触发期间目标已消失：攻击照旧消耗，但不再结算战斗
        if (def == null || def.Destroyed || !AttackTargetsFor(atk).Contains(a.TargetId))
        {
            S.Log.Line($"    {atk.Id} attack fizzled (target gone)");
            FireTrigger(Trigger.OnOtherCardAttacks, atk);
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
        if (defDies) DestroyCard(def);
        if (atkDies) DestroyCard(atk);
        else if (dmgToAtk == 0 && dmgToDef == 0) { /* 双方都打不动 */ }

        FireTrigger(Trigger.OnOtherCardAttacks, atk);
        FireTrigger(Trigger.OnAfterOtherCardAttacks, atk);
        if (!def.Destroyed) FireTrigger(Trigger.OnOtherCardSurvivedCombat, def);
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
            if (S.Frontline.Count == 0) S.FrontlineOwner = Side.None;
            S.Log.Line($"  {u.Id} retreats to support line");
            FireTrigger(Trigger.OnOtherCardMoveFromFrontline, u);
        }
        u.MovedThisTurn = true;
        FireTrigger(Trigger.OnOtherCardLocationMoved, u);
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