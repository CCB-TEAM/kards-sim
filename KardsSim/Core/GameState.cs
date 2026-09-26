namespace KardsSim.Core;

/// <summary>
/// 规则常量。全部来自 BP_Logic 的 CDO，已与客户端一致：
/// MaxCardsOnHand=9, MaxCardsPerRow=5, MaxKreditsConst=12, NumCardsInDeck=30, NumStartingCards=4。
/// </summary>
public static class Rules
{
    public const int MaxCardsOnHand = 9;
    public const int MaxCardsPerRow = 5;
    /// <summary>每回合自然增长的上限（BP_Logic 的 MaxKreditsConst）。</summary>
    public const int MaxKredits = 12;
    /// <summary>
    /// 指挥点槽的**绝对上限**：卡牌可以把它顶到 24（自然增长只到 12）。
    /// 三处独立证据一致：wiki「通过卡牌最多可以有 24 个指挥点槽」、
    /// <c>kreditCombinationsUSUnits</c> 的行名 3..24、
    /// <c>card_event_mass_deployment</c> 里的 <c>Clamp(槽, 3, 24)</c>。
    /// </summary>
    public const int MaxKreditSlots = 24;
    /// <summary>
    /// BP_Logic CDO 里的 <c>NumCardsInDeck</c>。**不是**构筑卡组的牌数：
    /// 实测卡组码与私服发牌都是「每侧 40 = HQ 1 + 手牌 4/5 + 牌库 35/34」，
    /// 也就是 <b>39 张牌 + 1 张 HQ</b>。这里只留作「没给卡组时随机凑牌」的默认值。
    /// </summary>
    public const int DeckSize = 30;
    /// <summary>真实构筑卡组的牌数：39 张牌，外加 1 张 HQ（HQ 不进卡组，见 <see cref="DeckCode"/>）。</summary>
    public const int StandardDeckSize = 39;
    public const int StartingHand = 4;
    public const int HqDefense = 20;
    /// <summary>
    /// 打到<b>敌方支援线</b>所需的最低射程。
    ///
    /// <para>
    /// 客户端的 <c>not_enough_range</c> 判据是：防守方与攻击方都不在前线（<c>location != 7</c>）
    /// 且 <c>range &lt; 2</c>。也就是「支援线打支援线」才要射程 ≥ 2 ——
    /// 支援线上的射程 1 单位可以打敌方<b>前线</b>（挨着的那条线），只是够不到支援线；
    /// 而只要有一方在前线，射程就不再是限制。
    /// 引擎不自己用这个常量做前置否决，逐对判定走客户端规则库，
    /// 这里只作为文档与测试的参照（见 <c>--mode rules</c> / <c>--mode tests</c>）。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 反制指令的激活序号计数器。激活序号 &gt; 0 表示已激活，
    /// 客户端拿它当 <c>activeGotchas</c> 映射的排序键（先激活的序号小）。
    /// </summary>
    public int NextGotchaOrder;
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
            Cipher = def.Cipher,
        };
        return c;
    }

    /// <summary>
    /// 按卡组模板建牌库并洗牌。<paramref name="deckSpec"/> 可以是：
    /// 卡组码（<c>%%…</c>）、decks.json 里的预设名（中文名 / 短名 / <c>#序号</c>）、
    /// 逗号或分号分隔的卡牌 id 列表；为 null / "random" 时随机取牌。
    /// </summary>
    private void BuildDeck(PlayerState p, string deckSpec)
    {
        var ids = DeckLists.Resolve(deckSpec, p.Side, Rng);
        foreach (var id in ids.Take(Rules.StandardDeckSize))
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

    /// <summary>
    /// 双方**所有位置**的牌（场上 / 前线 / 手牌 / 牌库 / 弃牌堆）。
    ///
    /// <para>
    /// 用于「按来源清账」这类必须覆盖全场的维护（例如回合结束撤销临时加成：
    /// 一张被临时加攻的牌可能已经被退回手牌或进了弃牌堆，只清场上会留下脏值，
    /// 它再被打出来或者被复活时就带着不该有的加成）。
    /// </para>
    /// </summary>
    public IEnumerable<Card> AllCards()
    {
        foreach (var c in AllOnBoard()) yield return c;
        foreach (var p in new[] { Left, Right })
        {
            foreach (var c in p.Hand) yield return c;
            foreach (var c in p.Deck) yield return c;
            foreach (var c in p.Discard) yield return c;
        }
    }

    /// <summary>
    /// 按 instanceId 找卡，**覆盖所有位置**（场上 / 前线 / 手牌 / 牌库 / 弃牌堆）。
    ///
    /// <para>
    /// 必须搜弃牌堆：牌打出后引擎会立刻把它移进弃牌堆（order 尤其如此），
    /// 而它的效果体正是这个时候跑的。客户端 <c>GetCardFromID</c> 查的是全局卡表，
    /// 弃牌堆里的卡照样能查到。只搜场上和手牌的话，
    /// <c>GetCardFromID(自己)</c> 返回空 → 依赖它的整条效果链静默断掉
    /// （Develop 的候选列表就是这么变成空的）。
    /// </para>
    /// </summary>
    public Card FindCard(int instanceId)
    {
        foreach (var c in AllOnBoard()) if (c.InstanceId == instanceId) return c;
        foreach (var p in new[] { Left, Right })
        {
            foreach (var c in p.Hand) if (c.InstanceId == instanceId) return c;
            foreach (var c in p.Deck) if (c.InstanceId == instanceId) return c;
            foreach (var c in p.Discard) if (c.InstanceId == instanceId) return c;
        }
        return null;
    }
}

/// <summary>
/// 卡组模板解析。四种写法：
/// <list type="number">
/// <item><c>%%…</c>：KARDS 卡组码（见 <see cref="DeckCode"/>）—— 训练/对战用的就是这种。</item>
/// <item>decks.json 里的预设名（<c>日波炸槽</c> / <c>jp-pl-boom</c> / <c>#7</c>）。</item>
/// <item>逗号或分号分隔的卡牌 id 列表（调试单个卡组用）。</item>
/// <item>null / <c>random</c>：从卡池里随机凑 <see cref="Rules.DeckSize"/> 张。</item>
/// </list>
/// </summary>
public static class DeckLists
{
    /// <summary>已经警告过的卡组串，避免自对弈里每局刷一遍。</summary>
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    public static List<string> Resolve(string spec, Side side, Rng rng)
    {
        if (DeckCode.IsDeckCode(spec))
        {
            var parsed = DeckCode.Parse(spec);
            Report(parsed, spec);
            return parsed.Cards;
        }

        if (!string.IsNullOrWhiteSpace(spec) && !spec.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            var preset = DeckPresets.Find(spec);
            if (preset != null)
            {
                var parsed = DeckCode.Parse(preset.Code);
                Report(parsed, $"{preset.Key}({preset.Name})");
                return parsed.Cards;
            }

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

    /// <summary>
    /// 卡组码解析出的牌数对不上时打一行警告（每种卡组只打一次）。
    ///
    /// <para>
    /// 缺牌不是引擎的问题，而是本模拟器的卡牌导出不全（<c>_input/Cards</c> 比线上客户端少），
    /// 新版本的 <c>_bal</c> 平衡卡尤其容易缺。诚实地报出来，比悄悄拿 35 张牌开打强。
    /// </para>
    /// </summary>
    private static void Report(ParsedDeck p, string label)
    {
        if (p.Complete && p.Legal) return;
        lock (Warned)
        {
            if (!Warned.Add(label)) return;
        }
        var parts = new List<string>();
        if (p.Total != Rules.StandardDeckSize) parts.Add($"只有 {p.Total}/{Rules.StandardDeckSize} 张");
        if (p.MissingCards.Count > 0) parts.Add($"卡库缺 {p.MissingCards.Count} 张（{string.Join(", ", p.MissingCards.Take(6))}）");
        if (p.UnknownCodes.Count > 0) parts.Add($"码表不认 {string.Join(",", p.UnknownCodes)}");
        if (p.Fallbacks.Count > 0) parts.Add($"_bal 回退 {p.Fallbacks.Count} 处");
        Console.Error.WriteLine($"[卡组] {label}: {string.Join("；", parts)}");
    }
}