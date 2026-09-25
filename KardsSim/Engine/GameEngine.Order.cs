using KardsSim.Core;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    /// <summary>
    /// 打出一张 order（指令）牌：先从手里移出，跑效果，再进弃牌堆。
    /// </summary>
    private void PlayOrder(Card c)
    {
        var p = S.Player(c.Owner);
        c.Loc = Loc.Discard;
        p.Discard.Add(c);
        S.Log.Line($"    (order) {c.Id}");

        // order 的效果挂在 NotAvailable / OnDeploymentEffectTriggered 通道上
        RunCardEffect(c, Trigger.NotAvailable);
        RunCardEffect(c, Trigger.OnDeploymentEffectTriggered);
    }
}