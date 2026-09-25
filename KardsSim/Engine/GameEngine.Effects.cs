using KardsSim.Core;
using KardsSim.Effects;
using KardsSim.Bridge;
using KardsSim.Kismet;

namespace KardsSim.Engine;

public sealed partial class GameEngine
{
    /// <summary>
    /// 直译产物的宿主。为 null 时退回旧的文本解析效果表。
    ///
    /// <para>
    /// 现在默认走直译产物：每张卡的效果就是它自己那份编译出来的字节码，
    /// 没有文本猜测。旧的 <see cref="EffectPlanner"/> 只作为对照保留。
    /// </para>
    /// </summary>
    public EngineHost Host { get; set; }

    /// <summary>
    /// 触发所有注册了该触发点的牌。
    ///
    /// 触发源 = 双方场上（与手牌）所有 CDO 里用了这个 trigger 的牌。
    /// 每张牌的同一次触发只跑一次；顺序按「当前方 → 对手」稳定排列，保证可复现。
    /// </summary>
    public void FireTrigger(Trigger t, Card subject)
    {
        if (S.Done) return;
        var order = new List<Card>();
        foreach (var side in new[] { S.Current, GameState.Foe(S.Current) })
        {
            foreach (var c in S.Player(side).Board) order.Add(c);
            foreach (var c in S.Frontline.Where(x => x.Owner == side)) order.Add(c);
        }
        foreach (var c in order)
        {
            if (c.Destroyed) continue;

            // 触发点归属有两种来源：
            //  - 直译产物：卡牌自己的资产里有同名事件函数，这是权威。
            //  - cards.json 的 CDO 标志位：用来在没接直译产物时兜底。
            if (Host is not null)
            {
                if (!CardDispatch.Has(c, t)) continue;
                CardDispatch.Fire(Host, c, t, subject);
                TriggerFireCount++;
                if (S.Done) return;
                continue;
            }

            if (c.Def?.Triggers == null || !c.Def.Triggers.Contains(t)) continue;
            RunCardEffectLegacy(c, t);
            TriggerFireCount++;
            if (S.Done) return;
        }
    }

    /// <summary>跑一张牌在某个触发点上的效果（优先直译产物）。</summary>
    public void RunCardEffect(Card self, Trigger t)
    {
        if (Host is not null)
        {
            CardDispatch.Fire(Host, self, t);
            return;
        }
        RunCardEffectLegacy(self, t);
    }

    /// <summary>旧的文本解析效果表路径，仅在 <see cref="Host"/> 为 null 时使用。</summary>
    public void RunCardEffectLegacy(Card self, Trigger t)
    {
        var plan = EffectPlanner.For(self.Def);
        var ops = plan.For(t);
        if (ops == null || ops.Count == 0) return;
        foreach (var op in ops)
        {
            Execute(self, op);
            if (S.Done) return;
        }
    }

    /// <summary>把效果操作落到具体目标上。</summary>
    public void Execute(Card self, Op op)
    {
        var me = self.Owner;
        var foe = GameState.Foe(me);

        switch (op.Kind)
        {
            case OpKind.Damage:
                foreach (var t in ResolveTargets(self, op))
                {
                    if (t == null) DamageHq(foe, op.Amount);
                    else DamageCard(t, op.Amount);
                    if (S.Done) return;
                }
                break;

            case OpKind.DamageHq:
                DamageHq(foe, op.Amount);
                break;

            case OpKind.Destroy:
                foreach (var t in ResolveTargets(self, op)) if (t != null) DestroyCard(t);
                break;

            case OpKind.Draw:
                DrawTo(S.Player(me), op.Amount);
                break;

            case OpKind.Heal:
                foreach (var t in ResolveTargets(self, op))
                {
                    if (t == null) { var p = S.Player(me); p.Hq = Math.Min(Rules.HqDefense, p.Hq + op.Amount); }
                    else t.Defense = Math.Min(t.MaxDefense, t.Defense + op.Amount);
                }
                break;

            case OpKind.BuffAttack:
                foreach (var t in ResolveTargets(self, op)) if (t != null) t.BuffAttack += op.Amount;
                break;

            case OpKind.BuffDefense:
                foreach (var t in ResolveTargets(self, op)) if (t != null) { t.BuffDefense += op.Amount; t.MaxDefense += op.Amount; }
                break;

            case OpKind.AddKredits:
                S.Player(me).Kredits += op.Amount;
                break;

            case OpKind.Pin:
                foreach (var t in ResolveTargets(self, op)) if (t != null) t.Pinned = true;
                break;

            case OpKind.Suppress:
                foreach (var t in ResolveTargets(self, op)) if (t != null) t.Suppressed = true;
                break;

            case OpKind.Discard:
                foreach (var t in ResolveTargets(self, op)) if (t != null) DiscardCard(t);
                break;

            case OpKind.Spawn:
                SpawnByName(self, op.CardName);
                break;
        }
    }

    /// <summary>把一张牌从手里丢进弃牌堆。</summary>
    public void DiscardCard(Card c)
    {
        var p = S.Player(c.Owner);
        if (!p.Hand.Remove(c)) return;
        c.Loc = Loc.Discard;
        p.Discard.Add(c);
        S.Log.Line($"    {c.Id} discarded");
        FireTrigger(Trigger.OnOtherCardDiscarded, c);
    }

    /// <summary>按名字生成一张牌入手牌。名字解析不到时记入 Unhandled，不静默丢掉。</summary>
    public Card SpawnByName(Card self, string name)
    {
        var def = CardDb.Resolve(name);
        if (def == null)
        {
            Unhandled.Add($"spawn unresolved: '{name}'");
            return null;
        }
        var p = S.Player(self.Owner);
        var c = S.NewCard(def, self.Owner);
        if (p.Hand.Count >= Rules.MaxCardsOnHand)
        {
            c.Loc = Loc.Discard;
            p.Discard.Add(c);
            S.Log.Line($"    hand full, {c.Id} burned");
            return c;
        }
        c.Loc = Loc.Hand;
        p.Hand.Add(c);
        S.Log.Line($"    {self.Owner} gains {c.Id}");
        FireTrigger(Trigger.OnOtherCardSpawnedInHand, c);
        return c;
    }
}