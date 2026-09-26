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

        if (S.Current == Side.Right) S.Turn++;
        S.Current = GameState.Foe(S.Current);
        if (S.Done) return;
        BeginTurn();
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