using KardsSim.Core;
using KardsSim.Effects;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    /// <summary>部署类效果是否需要玩家指定目标。</summary>
    public bool NeedsDeployTarget(Card c)
    {
        var plan = EffectPlanner.For(c.Def);
        foreach (var l in plan.ByTrigger.Values)
            foreach (var op in l)
                if (op.Target == TargetSpec.Chosen) return true;
        return false;
    }

    /// <summary>可选的部署目标。0 表示敌方 HQ。</summary>
    public List<int> DeployTargets(Card c)
    {
        var res = new List<int>();
        var foe = GameState.Foe(c.Owner);
        foreach (var t in S.Player(foe).Board) res.Add(t.InstanceId);
        foreach (var t in S.Frontline.Where(x => x.Owner == foe)) res.Add(t.InstanceId);
        return res;
    }

    /// <summary>
    /// 把一个效果操作解析成具体目标列表。
    /// 返回的列表里 null 表示「HQ 那一侧」，由调用方决定是敌是友。
    /// </summary>
    public List<Card> ResolveTargets(Card self, Op op)
    {
        var me = self.Owner;
        var foe = GameState.Foe(me);
        var res = new List<Card>();

        switch (op.Target)
        {
            case TargetSpec.Self:
                res.Add(self);
                break;

            case TargetSpec.Chosen:
                var pick = self.ChosenTargetId > 0 ? S.FindCard(self.ChosenTargetId) : null;
                if (pick == null) pick = PickAutoTarget(self);
                if (pick != null) res.Add(pick);
                else res.Add(null);                 // 没目标就默认指向敌方 HQ
                break;

            case TargetSpec.EnemyUnit:
                var e = PickAutoTarget(self);
                if (e != null) res.Add(e); else res.Add(null);
                break;

            case TargetSpec.EnemyHq:
                res.Add(null);
                break;

            case TargetSpec.OwnHq:
                res.Add(null);
                break;

            case TargetSpec.AllEnemyUnits:
                res.AddRange(S.UnitsOnBoard(foe));
                break;

            case TargetSpec.AllFriendlyUnits:
                res.AddRange(S.UnitsOnBoard(me));
                break;

            case TargetSpec.AllUnits:
                res.AddRange(S.AllOnBoard());
                break;

            case TargetSpec.RandomEnemyUnit:
                var cand = S.UnitsOnBoard(foe).ToList();
                if (cand.Count > 0) res.Add(cand[S.Rng.Next(cand.Count)]);
                else res.Add(null);
                break;

            default:
                res.Add(null);
                break;
        }
        return res;
    }

    /// <summary>
    /// 没指定目标时的自动选择：优先能被打死的敌方单位，否则打最弱的。
    /// 这是确定性的，保证同 seed 可复现。
    /// </summary>
    private Card PickAutoTarget(Card self)
    {
        var foe = GameState.Foe(self.Owner);
        var cand = S.UnitsOnBoard(foe).Where(u => !u.Destroyed).ToList();
        if (cand.Count == 0) return null;
        return cand.OrderBy(u => u.TotalDefense).ThenBy(u => u.InstanceId).First();
    }
}