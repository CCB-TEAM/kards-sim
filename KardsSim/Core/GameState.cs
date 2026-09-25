namespace KardsSim.Core;

/// <summary>
/// 规则常量。全部来自 BP_Logic 的 CDO，已与客户端一致：
/// MaxCardsOnHand=9, MaxCardsPerRow=5, MaxKreditsConst=12, NumCardsInDeck=30, NumStartingCards=4。
/// </summary>
public static class Rules
{
    public const int MaxCardsOnHand = 9;
    public const int MaxCardsPerRow = 5;
    public const int MaxKredits = 12;
    public const int DeckSize = 30;
    public const int StartingHand = 4;
    public const int HqDefense = 20;
    /// <summary>支援线上的单位要能打到前线，range 至少要有这么多。</summary>
    public const int SupportLineAttackRange = 2;
    /// <summary>前线归属为 None 时，任何单位都能进。</summary>
    public const Side FrontlineNone = Side.None;
}

public sealed class PlayerState
{
    public Side Side;
    public int Hq = Rules.HqDefense;
    public int Kredits;
    public int KreditSlots;
    /// <summary>本回合已花掉的克redit（用于 OnOtherCardOperationKreditSpent 之类）。</summary>
    public int KreditsSpentThisTurn;
    public int Fatigue;

    public readonly List<Card> Deck = new();
    public readonly List<Card> Hand = new();
    public readonly List<Card> Board = new();      // 支援线
    public readonly List<Card> Discard = new();

    public PlayerState Opponent;

    public bool Alive => Hq > 0;
}

/// <summary>一次对局的完整可变状态。</summary>
public sealed class GameState
{
    public Rng Rng;
    public int Seed;
    public int Turn;
    public Side Current;
    public Side Winner = Side.None;
    public bool Done;

    public PlayerState Left = new() { Side = Side.Left };
    public PlayerState Right = new() { Side = Side.Right };

    /// <summary>前线归属：由第一张进入前线的单位决定，清空后回到 None。</summary>
    public Side FrontlineOwner = Side.None;
    /// <summary>前线上的牌（双方共享这一格）。</summary>
    public readonly List<Card> Frontline = new();

    public readonly GameLog Log = new();

    private int _nextId = 1;

    public GameState(int seed, string leftDeck, string rightDeck, bool log = false)
    {
        Seed = seed;
        Rng = new Rng(seed);
        Log.Enabled = log;
        Left.Opponent = Right;
        Right.Opponent = Left;
        BuildDeck(Left, leftDeck);
        BuildDeck(Right, rightDeck);
    }

    public Card NewCard(CardDef def, Side owner)
    {
        var c = new Card
        {
            InstanceId = _nextId++,
            Def = def,
            Owner = owner,
            Attack = def.Attack,
            Defense = def.Defense,
            MaxDefense = def.Defense,
            Range = def.Range,
            KreditCost = def.Kredits,
            Keywords = def.Keywords,
            HeavyArmor = def.HeavyArmor,
        };
        return c;
    }

    /// <summary>用卡组模板建 30 张牌并洗牌。leftDeck 为 null 时随机取卡。</summary>
    private void BuildDeck(PlayerState p, string deckSpec)
    {
        var ids = DeckLists.Resolve(deckSpec, p.Side, Rng);
        foreach (var id in ids.Take(Rules.DeckSize))
        {
            var def = CardDb.Get(id) ?? CardDb.All[Rng.Next(CardDb.All.Count)];
            var c = NewCard(def, p.Side);
            c.Loc = Loc.Deck;
            p.Deck.Add(c);
        }
        Rng.Shuffle(p.Deck);
    }

    public PlayerState Player(Side s) => s == Side.Left ? Left : Right;
    public static Side Foe(Side s) => s == Side.Left ? Side.Right : Side.Left;
    public PlayerState Opponent(Side s) => Player(Foe(s));
    public PlayerState CurrentPlayer => Player(Current);

    /// <summary>某方的所有场上单位（支援线 + 前线）。</summary>
    public IEnumerable<Card> UnitsOnBoard(Side s)
    {
        foreach (var c in Player(s).Board) yield return c;
        foreach (var c in Frontline) if (c.Owner == s) yield return c;
    }

    public IEnumerable<Card> AllOnBoard()
    {
        foreach (var c in Left.Board) yield return c;
        foreach (var c in Frontline) yield return c;
        foreach (var c in Right.Board) yield return c;
    }

    public Card FindCard(int instanceId)
    {
        foreach (var c in AllOnBoard()) if (c.InstanceId == instanceId) return c;
        foreach (var c in Left.Hand) if (c.InstanceId == instanceId) return c;
        foreach (var c in Right.Hand) if (c.InstanceId == instanceId) return c;
        return null;
    }
}

/// <summary>卡组模板。'random' 之外的名字按 id 查找。</summary>
public static class DeckLists
{
    public static List<string> Resolve(string spec, Side side, Rng rng)
    {
        if (!string.IsNullOrWhiteSpace(spec) && !spec.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            var ids = spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (ids.Count > 0) return ids;
        }
        // 没有指定卡组时随机：优先与所属阵营匹配的牌，凑够 30 张
        var pool = CardDb.All.Where(c => c.Type != CardType.Gotcha).ToList();
        var pick = new List<string>();
        for (var i = 0; i < Rules.DeckSize; i++) pick.Add(pool[rng.Next(pool.Count)].Id);
        return pick;
    }
}