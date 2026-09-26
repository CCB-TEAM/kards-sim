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

    /// <summary>每张手牌的目标槽位数（见 <see cref="PlaySlot"/>）：
    /// 0 = 不需要目标；1..5 敌方前线；6..10 敌方支援；11..15 己方前线；16..20 己方支援。</summary>
    public const int PlayTargetSlots = 21;

    /// <summary>「三选一」的分支槽位数（卡池里 choose-one 卡都是 2 个分支）。</summary>
    public const int ChooseSlots = 2;

    /// <summary>每张手牌占用的出牌槽位数 = 目标槽 × 分支槽。</summary>
    public const int PlaySlotsPerCard = PlayTargetSlots * ChooseSlots;

    /// <summary>出牌区大小：5 张手牌 × 42 个槽。</summary>
    public const int PlayBlock = PerRow * PlaySlotsPerCard;

    /// <summary>单位区：己方 10 个位置 × 13 个动作。</summary>
    public const int UnitBlock = 10 * 13;

    /// <summary>EndTurn 的下标。</summary>
    public const int EndTurnIndex = PlayBlock + UnitBlock;

    /// <summary>「待决选择」的候选槽位数（客户端目前最多一次列 3 张，8 足够）。</summary>
    public const int ChoiceSlots = 8;

    /// <summary>抉择区起始下标。</summary>
    public const int ChoiceBlock = EndTurnIndex + 1;

    /// <summary>
    /// 动作空间：
    ///   出牌区（5 手牌 × 21 目标 × 2 三选一分支）+ 单位区（10 × 13）+ EndTurn + 抉择区（8）。
    ///
    /// <para>
    /// 出牌从「一张牌一个下标」扩成「一张牌 × 一个目标 × 一个三选一分支」，
    /// 这样「打谁」和「选哪支」都是模型能学的维度 —— 以前 TargetId 恒为 -1、
    /// WhichChooseOne 恒为 0，需要目标或需要选择的牌等于没有决策权。
    /// 抉择区对应二段式选择（Develop / 选手牌）：效果跑到一半要求选牌时，
    /// 动作列表里只剩这一区（见 <see cref="Engine.PendingChoice"/>）。
    /// </para>
    /// </summary>
    public const int ActionSize = ChoiceBlock + ChoiceSlots;

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
    ///   0..209    ：出牌区。handIndex * 42 + 目标槽 * 2 + 三选一分支
    ///               （目标槽见 <see cref="PlaySlot"/>，分支 0/1）
    ///   210..339  ：己方 10 个位置（前线 5 + 支援 5），每个 13 个动作
    ///               （11 个攻击目标：0..9 是敌方单位槽，10 是 HQ；11 前移；12 后退）
    ///   340       ：EndTurn
    ///   341..348  ：待决选择的候选（HandIndex 即候选序号）
    /// </summary>
    public static int Index(GameEngine g, GameAction a)
    {
        if (a.Type == ActionType.EndTurn) return EndTurnIndex;

        if (a.Type == ActionType.ChooseCard)
            return a.HandIndex is >= 0 and < ChoiceSlots ? ChoiceBlock + a.HandIndex : -1;

        if (a.Type == ActionType.PlayCard)
        {
            if (a.HandIndex is < 0 or >= PerRow) return -1;
            var ps = PlaySlot(g, a.TargetId);
            if (ps < 0) return -1;
            var ch = a.ChoiceIndex < 0 ? 0 : a.ChoiceIndex;
            if (ch >= ChooseSlots) return -1;
            return a.HandIndex * PlaySlotsPerCard + ps * ChooseSlots + ch;
        }

        var slot = SlotOf(g, a.SourceId);
        if (slot < 0) return -1;
        var b = PlayBlock + slot * 13;
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

    /// <summary>
    /// 出牌目标 → 槽位；-1 表示这个目标不在编码范围里。
    /// 出牌目标与攻击目标分开编码：出牌可以是己方单位（增益类），攻击只能是敌方。
    /// </summary>
    private static int PlaySlot(GameEngine g, int targetId)
    {
        if (targetId < 0) return 0;               // 不需要目标
        var me = g.S.Current;
        var foe = GameState.Foe(me);

        var n = 0;
        foreach (var c in g.S.Frontline.Where(x => x.Owner == foe))
        {
            if (c.InstanceId == targetId) return 1 + n;
            if (++n >= PerRow) break;
        }
        n = 0;
        foreach (var c in g.S.Player(foe).Board)
        {
            if (c.InstanceId == targetId) return 6 + n;
            if (++n >= PerRow) break;
        }
        n = 0;
        foreach (var c in g.S.Frontline.Where(x => x.Owner == me))
        {
            if (c.InstanceId == targetId) return 11 + n;
            if (++n >= PerRow) break;
        }
        n = 0;
        foreach (var c in g.S.Player(me).Board)
        {
            if (c.InstanceId == targetId) return 16 + n;
            if (++n >= PerRow) break;
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