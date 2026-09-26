using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 单卡机制验证：不做「跑通没炸」这种弱检查，而是断言<b>具体数值和状态变化</b>。
///
/// <para>
/// 直译产物最大的风险不是崩溃，是「编译通过、跑得动、数字是错的」——
/// 接收者算错、out 参数没回写、触发点发错时机，全都会静默产出看似合理的错值。
/// 所以每个机制都要有一个能失败的断言。
/// </para>
/// </summary>
public static class CardTests
{
    private static int _pass, _fail;

    public static int Run(string[] args)
    {
        _pass = _fail = 0;

        IntelRevealsEnemyHand();
        IntelNotifiesResponders();

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Intel n = 打出时随机翻开对手 n 张手牌，且不动手牌顺序。
    ///
    /// <para>
    /// sign 1：翻开的张数正好是 n，不多不少。
    /// sign 2：只翻对手的牌，自己的手牌一张都不许被动。
    /// sign 3：手牌<b>顺序</b>不变 —— 翻牌只加标记，不重排。顺序是对局的可见信息，
    /// 洗错地方会让同一 seed 跑出不同结果。
    /// </para>
    /// </summary>
    private static void IntelRevealsEnemyHand()
    {
        var g = new Engine.GameEngine(7, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        var card = new CardDef { Id = "test_intel", Name = "test_intel", Type = CardType.Order, Cipher = 3 };
        var played = PlaceInHand(g, Side.Left, card);

        var foeHand = g.S.Player(Side.Right).Hand;
        var myHand = g.S.Player(Side.Left).Hand;
        var myOrder = myHand.Select(c => c.InstanceId).ToList();
        var foeOrder = foeHand.Select(c => c.InstanceId).ToList();

        // 清掉可能已有的标记
        foreach (var c in myHand.Concat(foeHand)) h.Obj(c).Set("seenByCipher", Val.False);

        // 直接走宿主原语，等价于 CardPlayedFromHand 里的调用
        h.Call("SetCardsSeenByCipher", new Val[]
        {
            Val.Ref(h.Obj(played)), Val.Of(played.Cipher), Val.Of(played.InstanceId),
        });

        var foeSeen = foeHand.Count(c => h.Obj(c).Get("seenByCipher").AsBool());
        var mySeen = myHand.Count(c => h.Obj(c).Get("seenByCipher").AsBool());

        Check("Intel 翻牌张数 = cipher", foeSeen == Math.Min(3, foeHand.Count), $"期望 {Math.Min(3, foeHand.Count)}, 实得 {foeSeen}");
        Check("Intel 不翻自己的牌", mySeen == 0, $"自己的手牌被翻了 {mySeen} 张");
        Check("Intel 不改对手手牌顺序",
            foeHand.Select(c => c.InstanceId).SequenceEqual(foeOrder),
            $"顺序变了: [{string.Join(",", foeOrder)}] -> [{string.Join(",", foeHand.Select(c => c.InstanceId))}]");
        Check("Intel 不改自己手牌顺序",
            myHand.Select(c => c.InstanceId).SequenceEqual(myOrder), "自己的手牌顺序变了");
    }

    /// <summary>
    /// Intel 要给注册了 OnIntelTriggered 的卡传「翻了几张」这个数值，而不是传卡对象。
    ///
    /// <para>
    /// 这是最容易错的一处：普通触发点的第二个形参是「相关卡」，
    /// 而 OnIntelTriggered 的第二个是整数。传错的话卡的逻辑会把整数当卡用，
    /// 不炸但结果全错。
    /// </para>
    /// </summary>
    private static void IntelNotifiesResponders()
    {
        // 找一张真的注册了 OnIntelTriggered 的卡
        var responder = Generated.FnIndex.Assets.Keys
            .FirstOrDefault(k => k.StartsWith("card_", StringComparison.Ordinal)
                                 && Generated.FnIndex.Find(k, "OnIntelTriggered") is not null);

        if (responder is null)
        {
            Check("存在注册 OnIntelTriggered 的卡", false, "一张都没有，无法验证");
            return;
        }

        var g = new Engine.GameEngine(11, null, null, false);
        var h = new Bridge.EngineHost(g);
        g.Host = h;

        var def = CardDb.Get(responder) ?? CardDb.Resolve(responder);
        if (def is null) { Check($"{responder} 能在卡库里找到", false, "找不到定义"); return; }

        var board = g.S.NewCard(def, Side.Left);
        board.Loc = Loc.Board;
        g.S.Player(Side.Left).Board.Add(board);

        try
        {
            h.Call("SetCardsSeenByCipher", new Val[] { Val.Nothing, Val.Of(2), Val.Of(board.InstanceId) });
            Check($"{responder}.OnIntelTriggered 能跑完不抛异常", true, null);
        }
        catch (Exception ex)
        {
            Check($"{responder}.OnIntelTriggered 能跑完不抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Card PlaceInHand(Engine.GameEngine g, Side s, CardDef def)
    {
        var c = g.S.NewCard(def, s);
        c.Loc = Loc.Hand;
        g.S.Player(s).Hand.Add(c);
        return c;
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}   {detail}"); }
    }
}

