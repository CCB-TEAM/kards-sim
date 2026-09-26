using KardsSim.Core;

namespace KardsSim.Engine;

/// <summary>
/// 对局引擎。刻意做成无状态之外只有 GameState 一个可变入口，
/// 这样 HTTP 层、CLI、AI 自对弈都走同一套规则。
/// </summary>
public sealed partial class GameEngine
{
    public GameState S { get; private set; }
    public readonly List<string> Unhandled = new();
    public int TriggerFireCount { get; private set; }
    public int Steps { get; private set; }

    /// <summary>
    /// 「客户端视角」的阵营。蓝图的 <c>GetClientSide</c> 返回本机玩家是哪一方，
    /// 效果里用它区分「自己 / 对手」。无头模拟恒为左方，只是让链路能跑通。
    /// </summary>
    public Side Perspective => Side.Left;

    /// <summary>
    /// 按阵营把一张牌生成到手牌（蓝图 <c>SpawnCardOnBattlefield</c> 的入口）。
    /// 与 <see cref="SpawnByName(Card, string)"/> 的区别是不需要「施法者」，
    /// 客户端那个接口直接指定阵营。
    /// </summary>
    public Card SpawnByName(Side side, string name)
    {
        var def = CardDb.Resolve(name);
        if (def is null)
        {
            Unhandled.Add($"spawn unresolved: '{name}'");
            return null;
        }
        var p = S.Player(side);
        var c = S.NewCard(def, side);
        if (p.Hand.Count >= Rules.MaxCardsOnHand)
        {
            c.Loc = Loc.Discard;
            p.Discard.Add(c);
            S.Log.Line($"    hand full, {c.Id} burned");
            return c;
        }
        c.Loc = Loc.Hand;
        p.Hand.Add(c);
        S.Log.Line($"    {side} gains {c.Id}");
        FireSpawnedInHandTriggers(c);
        return c;
    }

    /// <summary>给宿主用的前线放置入口（<c>TryPlaceOnFrontline</c> 是 private）。</summary>
    public bool TryPlaceOnFrontlinePublic(Card c) => TryPlaceOnFrontline(c);

    public GameEngine(int seed, string leftDeck = null, string rightDeck = null, bool log = false)
    {
        S = new GameState(seed, leftDeck, rightDeck, log);
        Start();
    }

    private GameEngine(GameState s) { S = s; }

    public static GameEngine FromState(GameState s) => new(s) { Steps = 0 };

    /// <summary>开局：双方各抽 4 张，Left 先手，第 1 回合只有 1 个 kredit 槽由回合流程给出。</summary>
    private void Start()
    {
        S.Log.Line($"=== 对局开始 seed={S.Seed} ===");
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var p = S.Player(side);
            for (var i = 0; i < Rules.StartingHand; i++) DrawTo(p, 1);
        }
        S.Current = Side.Left;
        S.Turn = 1;
        BeginTurn();
        FireTrigger(Trigger.OnStartOfGame, null);
    }

    // ---------- 查询 ----------

    public bool IsDone => S.Done;

    /// <summary>某方场上的单位数（前线 + 支援线）。</summary>
    public int BoardCount(Side s) => S.UnitsOnBoard(s).Count();

    /// <summary>某方支援线上的单位数，用于判断行是否满。</summary>
    public int SupportCount(Side s) => S.Player(s).Board.Count;

    /// <summary>前线上的单位数。</summary>
    public int FrontlineCount(Side s) => S.Frontline.Count(c => c.Owner == s);

    public int HandCount(Side s) => S.Player(s).Hand.Count;
    public int DeckCount(Side s) => S.Player(s).Deck.Count;

    /// <summary>前线是否可被 s 方进入。规则：前线归属为 None 或 s 方时可用。</summary>
    public bool CanEnterFrontline(Side s)
        => S.FrontlineOwner == Side.None || S.FrontlineOwner == s;

    /// <summary>HQ 是否直接暴露：对前线没有己方单位保护时，攻击可以直击 HQ。</summary>
    public bool HqExposed(Side defender)
    {
        // 前线归攻击方时，被攻击方的 HQ 暴露
        if (S.FrontlineOwner != Side.None && S.FrontlineOwner != defender) return true;
        return S.Frontline.Count(c => c.Owner == defender) == 0;
    }

    public override string ToString()
        => $"Turn {S.Turn} ({S.Current}) L.HQ={S.Left.Hq} R.HQ={S.Right.Hq}";
}