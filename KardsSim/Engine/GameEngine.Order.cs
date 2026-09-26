using KardsSim.Core;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    /// <summary>
    /// 打出一张 order（指令）牌：从手里移进弃牌堆。
    ///
    /// <para>
    /// 效果不在这里跑 —— 统一由 <c>DoPlayCard</c> 的出牌管线调卡牌的
    /// <c>OnPlayedFromHand</c>。以前这里和 DoPlayCard 各跑一遍
    /// <c>Trigger.NotAvailable</c>，而那条通道是空操作，等于跑了两遍「什么都不做」。
    /// </para>
    /// </summary>
    private void PlayOrder(Card c)
    {
        var p = S.Player(c.Owner);
        c.Loc = Loc.Discard;
        p.Discard.Add(c);
        S.Log.Line($"    (order) {c.Id}");
    }
}