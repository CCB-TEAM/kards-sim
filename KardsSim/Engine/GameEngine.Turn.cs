using KardsSim.Core;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    /// <summary>
    /// 开始一回合：刷新克redit 槽、抽牌、重置本回合标记、触发 OnStartofTurn。
    /// 槽位规则（已从 BP_Logic 核实）：每回合 +1，上限 12；克redit 每回合重置为槽位数。
    /// </summary>
    private void BeginTurn()
    {
        var p = S.Player(S.Current);
        p.KreditSlots = Math.Min(Rules.MaxKredits, p.KreditSlots + 1);
        p.Kredits = p.KreditSlots;
        p.KreditsSpentThisTurn = 0;

        foreach (var u in S.UnitsOnBoard(S.Current))
        {
            u.AttackedThisTurn = false;
            u.SummonedThisTurn = false;
            u.MovedThisTurn = false;
        }

        S.Log.Line($"--- turn {S.Turn} ({S.Current}) kredits={p.Kredits}/{p.KreditSlots} ---");

        FireTurnStartTriggers();
        DrawTo(p, 1);
    }

    /// <summary>结束当前方回合并交给对手；连续两方都结束则回合数 +1。</summary>
    private void EndTurn()
    {
        var p = S.Player(S.Current);
        FireTurnEndTriggers();
        // 回合结束清除临时状态
        foreach (var u in S.UnitsOnBoard(S.Current))
        {
            u.Pinned = false;
            u.Suppressed = false;
        }
        // 「本回合 +N 攻击」要在这里过期。
        //
        // 客户端把这类加成记在 GameStateRef.BuffsToRemoveEndOfTurn（buffType=0 = 攻击），
        // 回合结束时 ExecuteEndOfTurnQueue 走完队列后调 RemoveBuffsEndOfTurn：
        // 对每个登记过的来源取 getCardsBuffedByThisCard(来源) 再逐张撤销。
        // 那条链在无头模拟里跑不到（ExecuteEndOfTurnEvents 没有任何调用者），
        // 所以引擎自己在这一步做同样的事 —— 见 ExpireTempBuffs()。
        ExpireTempBuffs();

        if (S.Current == Side.Right) S.Turn++;
        S.Current = GameState.Foe(S.Current);
        if (S.Done) return;
        BeginTurn();
    }

    /// <summary>
    /// 本回合登记过的「临时加成」来源（<c>AddAttackUntilEndOfTurn</c> 会登记）。
    ///
    /// <para>
    /// 客户端登记的是 <c>(buffType, instigatorID)</c> 对，只有 buffType 0（攻击）存在
    /// 对应的 API。这里只留 instanceId —— 「谁加的」就够撤销了，
    /// 撤销走 <see cref="Card.AttackBuffBySource"/> 那本账（按来源精确扣）。
    /// </para>
    /// </summary>
    private readonly HashSet<int> _tempAttackBuffSources = new();

    /// <summary>
    /// 登记一个「本回合有效」的攻击加成来源（宿主原语 AddBuffsToRemoveEndOfTurn 调它）。
    /// </summary>
    public void RegisterTempAttackBuff(int instigatorId)
    {
        if (instigatorId > 0) _tempAttackBuffSources.Add(instigatorId);
    }

    /// <summary>
    /// 撤销所有登记过的临时攻击加成，并清空登记表。
    ///
    /// <para>
    /// 两个方向都要顾到：<b>加过的牌</b>要扣掉那一份（可能已经退回手牌 / 进弃牌堆，
    /// 所以扫全场而不只是场上），以及<b>登记表本身</b>要清空 —— 不清的话下一回合结束时
    /// 会再撤一次（幂等，但如果那个来源后来又给了永久加成，就会被误撤）。
    /// </para>
    /// </summary>
    public void ExpireTempBuffs()
    {
        if (_tempAttackBuffSources.Count == 0) return;
        foreach (var c in S.AllCards())
        {
            var hit = false;
            foreach (var src in _tempAttackBuffSources)
                if (c.AttackBuffBySource.ContainsKey(src)) { c.AttackBuffBySource.Remove(src); hit = true; }
            if (!hit) continue;
            var total = c.BuffAttackFromNoSource;
            foreach (var v in c.AttackBuffBySource.Values) total += v;
            c.BuffAttack = total;
            // 镜像字段也要跟上：客户端规则库读的是 attackBuff。
            if (Host is not null) Host.WriteMirrorAttackBuff(c);
        }
        _tempAttackBuffSources.Clear();
    }

    /// <summary>
    /// 抽 n 张牌。牌库空时改为疲劳伤害（每次递增）。
    /// 手牌上限 9：超出则爆牌进弃牌堆（burn）。
    /// </summary>
    public void DrawTo(PlayerState p, int n)
    {
        for (var i = 0; i < n; i++)
        {
            if (p.Deck.Count == 0)
            {
                p.Fatigue++;
                S.Log.Line($"  {p.Side} deck empty, fatigue {p.Fatigue}");
                FireFatigueTriggers();
                DamageHq(p.Side, p.Fatigue);
                if (S.Done) return;
                continue;
            }
            var c = p.Deck[0];
            p.Deck.RemoveAt(0);
            c.Loc = Loc.Hand;
            if (p.Hand.Count >= Rules.MaxCardsOnHand)
            {
                // 手牌已满：这张牌直接烧掉
                c.Loc = Loc.Discard;
                p.Discard.Add(c);
                S.Log.Line($"  {p.Side} hand full, burned {c.Id}");
                FireDiscardedTriggers(c);
                continue;
            }
            p.Hand.Add(c);
            S.Log.Line($"  {p.Side} draws {c.Id}");
            FireDrawnTriggers(c);
            if (S.Done) return;
        }
    }

    /// <summary>
    /// 对 HQ 造成伤害。HQ 防御降到 0 即败北。
    /// </summary>
    public void DamageHq(Side target, int amount)
    {
        if (amount <= 0) return;
        var p = S.Player(target);
        p.Hq -= amount;
        S.Log.Line($"  {target} HQ takes {amount} -> {p.Hq}");
        FireTrigger(Trigger.OnOtherCardReceiveDamage, null);
        if (p.Hq <= 0)
        {
            p.Hq = 0;
            Finish(GameState.Foe(target));
        }
    }

    private void Finish(Side winner)
    {
        S.Done = true;
        S.Winner = winner;
        S.Log.Line($"=== {winner} wins (turn {S.Turn}) ===");
    }

    /// <summary>
    /// 对一张场上牌造成伤害。伤害 >= TotalDefense 即摧毁。
    /// 已与客户端 CalculateDamageDealt 的语义对齐。
    /// </summary>
    public void DamageCard(Card c, int amount, bool lethal = false, Card killer = null, bool inCombat = false)
    {
        if (c == null || c.Destroyed || amount <= 0 && !lethal) return;
        FireDamageModifyTriggers(c);      // 结算前让卡牌改伤害
        c.Defense -= amount;
        S.Log.Line($"    {c.Id} takes {amount} -> {c.Defense}");
        FireDamageTakenTriggers(c);
        var dies = lethal && amount > 0 || c.TotalDefense <= 0;
        if (dies) DestroyCard(c, killer, inCombat);
    }

    /// <summary>摧毁一张牌：进弃牌堆，清空其占位并触发相关事件。</summary>
    public void DestroyCard(Card c, Card killer = null, bool inCombat = false)
    {
        if (c == null || c.Destroyed) return;

        // 客户端销毁流程的第一步是 ExecuteOnBeforeLeaveBoardOrOwnerEvents，
        // 此时牌还在原位置。这一步必须在标 Destroyed / 移出行**之前**跑，
        // 否则卡自己的离场逻辑里 GetCardsToTheLeft(self) 恒空（详见该方法说明）。
        FireBareLeaveBoardOrOwner(c);

        c.Destroyed = true;
        FireBeforeDestroyTriggers(c, killer, inCombat);

        var p = S.Player(c.Owner);
        bool removed;
        if (c.OnFrontline)
        {
            removed = S.Frontline.Remove(c);
            if (S.Frontline.Count == 0) S.FrontlineOwner = Side.None;
        }
        else removed = p.Board.Remove(c);

        if (removed)
        {
            c.Loc = Loc.Discard;
            c.OnFrontline = false;
            p.Discard.Add(c);
            S.Log.Line($"    {c.Id} destroyed");
        }
        if (S.Frontline.Count == 0) S.FrontlineOwner = Side.None;
        FireDestroyTriggers(c, killer, inCombat);   // OtherCardDestroyed + DestructionEffect + LeaveBoard(before/after)
        AfterBoardChange();       // 有人离场 → 掩护范围变了
    }
}