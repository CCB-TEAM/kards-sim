using KardsSim.Core;
using KardsSim.Engine;

namespace KardsSim.Ai;

/// <summary>
/// 状态 → 定长向量，以及动作 ↔ 下标映射。
///
/// 设计目标：让任意 AI 后端（Python/ONNX/远端 HTTP）只要拿到这个向量 + 合法动作掩码
/// 就能训练，不需要理解 KARDS 规则细节。
/// </summary>
public static class Encoder
{
    public const int PerRow = 5;         // 每行最多 5 张
    public const int Rows = 4;           // 己方前线/己方支援/敌方前线/敌方支援
    public const int CardFeatures = 12;  // 每张牌的编码宽度
    public const int Globals = 24;

    public const int StateSize = Rows * PerRow * CardFeatures + Globals;
    /// <summary>动作空间：自家 5 手牌出牌 + 己方 10 个单位 × (11 目标 + 2 移动) + EndTurn。</summary>
    public const int ActionSize = 5 + 10 * 13 + 1;

    public static float[] Encode(GameEngine g)
    {
        var v = new float[StateSize];
        var me = g.S.Current;
        var foe = GameState.Foe(me);
        var i = 0;

        // 4 行 × 5 格
        i = WriteRow(v, i, g.S.Frontline.Where(c => c.Owner == me));
        i = WriteRow(v, i, g.S.Player(me).Board);
        i = WriteRow(v, i, g.S.Frontline.Where(c => c.Owner == foe));
        i = WriteRow(v, i, g.S.Player(foe).Board);

        var mp = g.S.Player(me);
        var fp = g.S.Player(foe);
        v[i++] = mp.Hq / (float)Rules.HqDefense;
        v[i++] = fp.Hq / (float)Rules.HqDefense;
        v[i++] = mp.Kredits / (float)Rules.MaxKredits;
        v[i++] = mp.KreditSlots / (float)Rules.MaxKredits;
        v[i++] = fp.KreditSlots / (float)Rules.MaxKredits;
        v[i++] = mp.Hand.Count / (float)Rules.MaxCardsOnHand;
        v[i++] = fp.Hand.Count / (float)Rules.MaxCardsOnHand;
        v[i++] = mp.Deck.Count / (float)Rules.DeckSize;
        v[i++] = fp.Deck.Count / (float)Rules.DeckSize;
        v[i++] = mp.Discard.Count / (float)Rules.DeckSize;
        v[i++] = fp.Discard.Count / (float)Rules.DeckSize;
        v[i++] = g.S.Turn / 40f;
        v[i++] = g.S.Current == Side.Left ? 1f : 0f;
        v[i++] = g.S.FrontlineOwner == me ? 1f : (g.S.FrontlineOwner == Side.None ? 0.5f : 0f);
        v[i++] = g.S.UnitsOnBoard(me).Count() / (float)(Rules.MaxCardsPerRow * 2);
        v[i++] = g.S.UnitsOnBoard(foe).Count() / (float)(Rules.MaxCardsPerRow * 2);
        v[i++] = mp.Fatigue / 10f;
        v[i++] = fp.Fatigue / 10f;
        // 手牌平均费用（粗略的手牌质量信号）
        v[i++] = mp.Hand.Count > 0 ? (float)(mp.Hand.Average(c => c.KreditCost) / (double)Rules.MaxKredits) : 0f;
        v[i++] = fp.Hand.Count > 0 ? (float)(fp.Hand.Average(c => c.KreditCost) / (double)Rules.MaxKredits) : 0f;
        v[i++] = g.S.UnitsOnBoard(me).Sum(c => c.TotalAttack) / 30f;
        v[i++] = g.S.UnitsOnBoard(foe).Sum(c => c.TotalAttack) / 30f;
        v[i++] = g.S.UnitsOnBoard(me).Sum(c => c.TotalDefense) / 60f;
        v[i++] = g.S.UnitsOnBoard(foe).Sum(c => c.TotalDefense) / 60f;

        return v;
    }

    private static int WriteRow(float[] v, int i, IEnumerable<Card> cards)
    {
        var n = 0;
        foreach (var c in cards)
        {
            if (n >= PerRow) break;
            v[i + 0] = 1f;
            v[i + 1] = (float)c.Type / 7f;
            v[i + 2] = c.TotalAttack / 12f;
            v[i + 3] = c.TotalDefense / 12f;
            v[i + 4] = c.KreditCost / (float)Rules.MaxKredits;
            v[i + 5] = c.Has(Kw.Guard) ? 1f : 0f;
            v[i + 6] = c.Has(Kw.Blitz) ? 1f : 0f;
            v[i + 7] = c.Has(Kw.Smokescreen) ? 1f : 0f;
            v[i + 8] = c.AttackedThisTurn ? 1f : 0f;
            v[i + 9] = c.Pinned || c.Suppressed ? 1f : 0f;
            v[i + 10] = c.Range / 5f;
            v[i + 11] = c.HeavyArmor / 5f;
            i += CardFeatures;
            n++;
        }
        return i + (PerRow - n) * CardFeatures;
    }

    /// <summary>合法动作 → 动作空间下标的映射。同一个下标必须恒对应同一语义。</summary>
    public static (float[] mask, List<GameAction> actions) Mask(GameEngine g)
    {
        var mask = new float[ActionSize];
        var acts = new List<GameAction>(ActionSize);
        var legal = g.LegalActions();
        foreach (var a in legal)
        {
            var idx = Index(g, a);
            if (idx < 0 || idx >= ActionSize) continue;
            if (mask[idx] > 0) continue;
            mask[idx] = 1f;
            acts.Add(a);
        }
        return (mask, acts);
    }

    /// <summary>
    /// 动作 → 下标。
    ///   0..4     ：手牌 0..4 的出牌
    ///   5..134   ：己方 10 个位置（前线 5 + 支援 5），每个 13 个动作
    ///              （11 个攻击目标：0..9 是敌方单位槽，10 是 HQ；11 前移；12 后退）
    ///   135      ：EndTurn
    /// </summary>
    public static int Index(GameEngine g, GameAction a)
    {
        if (a.Type == ActionType.EndTurn) return ActionSize - 1;
        if (a.Type == ActionType.PlayCard) return a.HandIndex is >= 0 and < PerRow ? a.HandIndex : -1;

        var slot = SlotOf(g, a.SourceId);
        if (slot < 0) return -1;
        var b = PerRow + slot * 13;
        switch (a.Type)
        {
            case ActionType.MoveToFrontline: return b + 11;
            case ActionType.MoveToSupport: return b + 12;
            case ActionType.Attack:
                var t = TargetSlot(g, a.TargetId);
                return t < 0 ? -1 : b + t;
        }
        return -1;
    }

    /// <summary>己方单位的槽位：前线 0..4，支援线 5..9。</summary>
    private static int SlotOf(GameEngine g, int instanceId)
    {
        var me = g.S.Current;
        var n = 0;
        foreach (var c in g.S.Frontline.Where(x => x.Owner == me))
        {
            if (c.InstanceId == instanceId) return n;
            n++;
            if (n >= PerRow) break;
        }
        n = PerRow;
        foreach (var c in g.S.Player(me).Board)
        {
            if (c.InstanceId == instanceId) return n;
            n++;
            if (n >= PerRow * 2) break;
        }
        return -1;
    }

    /// <summary>攻击目标的槽位：敌方前线 0..4，敌方支援 5..9，HQ = 10。</summary>
    private static int TargetSlot(GameEngine g, int targetId)
    {
        if (targetId == 0) return 10;
        var foe = GameState.Foe(g.S.Current);
        var n = 0;
        foreach (var c in g.S.Frontline.Where(x => x.Owner == foe))
        {
            if (c.InstanceId == targetId) return n;
            n++;
            if (n >= PerRow) break;
        }
        n = PerRow;
        foreach (var c in g.S.Player(foe).Board)
        {
            if (c.InstanceId == targetId) return n;
            n++;
            if (n >= PerRow * 2) break;
        }
        return -1;
    }
}