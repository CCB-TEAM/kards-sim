using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim.Engine;

/// <summary>
/// 触发点 → 引擎里应该在什么时候发。集中在一处，免得散落各处漏掉。
///
/// <para>
/// 这张表的依据是游戏自己的 <c>Execute*Events</c>：每个触发点在客户端都有一个
/// 对应函数，函数内部向 <c>FetchAllCardsWithEventTrigger</c> 传常量 triggerID。
/// 用 <c>--mode triggers</c> 可以把「触发点 → 游戏分派函数」扫出来对照。
/// </para>
///
/// <para>
/// <b>为什么不能只按 cards.json 的 CDO 标志位发</b>：那只能告诉你「这张卡用没用这个
/// 触发点」，不能告诉你引擎该在哪个时刻发。两边都得有，缺一边就是白板。
/// </para>
/// </summary>
public sealed partial class GameEngine
{
    /// <summary>
    /// 统一的触发入口。所有会改变场上状态的地方都该走这里，
    /// 它负责：幂等保护（不重复发给同一张牌）、顺序稳定、终止检查。
    /// </summary>
    internal void Fire(Trigger t, Card subject = null) => FireTrigger(t, subject);

    // ===================== 回合 =====================

    /// <summary>配合 BeginTurn：回合开始的完整触发序列。</summary>
    internal void FireTurnStartTriggers()
    {
        Fire(Trigger.OnBeforeStartOfTurn);
        Fire(Trigger.OnStartofTurn);
    }

    internal void FireTurnEndTriggers()
    {
        Fire(Trigger.OnEndOfTurn);
        Fire(Trigger.CampaignOnEndOfGame);
    }

    // ===================== 出牌 =====================

    /// <summary>出牌相关的全部触发点。顺序照客户端 CardPlayedFromHand 的编排。</summary>
    internal void FirePlayTriggers(Card c)
    {
        Fire(Trigger.OnBeforeOtherCardPlayedFromHand, c);
        Fire(Trigger.OnOtherCardPlayedFromHand, c);
        // 隐蔽牌出牌是单独一个触发点
        if (c.Has(Kw.Covert)) Fire(Trigger.OnOtherCovertCardPlayedFromHand, c);
    }

    internal void FireEnterPlayTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardEnterPlay, c);
        if (c.Has(Kw.Deployment)) Fire(Trigger.OnBeforeOtherCardDeploymentTrigger, c);
        Fire(Trigger.OnDeploymentEffectTriggered, c);
        if (c.Has(Kw.Develop)) Fire(Trigger.OnOtherCardDeveloped, c);
    }

    // ===================== 攻击 =====================

    internal void FireAttackStartTriggers(Card atk) => Fire(Trigger.OnBeforeOtherCardAttacks, atk);

    internal void FireAttackEndTriggers(Card atk, Card def)
    {
        Fire(Trigger.OnOtherCardAttacks, atk);
        Fire(Trigger.OnAfterOtherCardAttacks, atk);
        if (def is not null && !def.Destroyed) Fire(Trigger.OnOtherCardSurvivedCombat, def);
        if (def is not null && !def.Destroyed) Fire(Trigger.OnOtherCardSurvivedCombat, atk);
    }

    // ===================== 伤害 =====================

    /// <summary>伤害计算前的修正窗口（客户端在结算前让卡牌改伤害）。</summary>
    internal void FireDamageModifyTriggers(Card target)
    {
        Fire(Trigger.OnOtherCardDealDamageAddDamage, target);
        Fire(Trigger.OnOtherCardDealDamageAddDamageAfterCalc, target);
    }

    internal void FireDamageTakenTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardReceiveDamage, c);
        Fire(Trigger.OnOtherCardDealDamage, c);
    }

    // ===================== 摧毁 =====================

    internal void FireBeforeDestroyTriggers(Card c) => Fire(Trigger.OnBeforeOtherCardDestroyed, c);

    internal void FireDestroyTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardDestroyed, c);
        Fire(Trigger.OnDestructionEffectTriggered, c);
        // 离场：客户端 before/after 两个触发点各有一个分派器
        Fire(Trigger.OnOtherCardLeaveBoardOrOwner, c);
        Fire(Trigger.OnAfterOtherCardLeaveBoardOrOwner, c);
    }

    // ===================== 移动 =====================

    internal void FireMoveToFrontlineTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardMoveToFrontline, c);
        Fire(Trigger.OnFrontlineOwnershipChange, c);
    }

    internal void FireMoveFromFrontlineTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardMoveFromFrontline, c);
        Fire(Trigger.OnFrontlineOwnershipChange, c);
    }

    internal void FireLocationMovedTriggers(Card c) => Fire(Trigger.OnOtherCardLocationMoved, c);

    // ===================== 数值变化 =====================

    /// <summary>攻防变动后通知。客户端有专门的 before/after 分派器。</summary>
    internal void FireAttackChangedTriggers(Card c)
    {
        Fire(Trigger.OnAfterOtherCardChangeAttack, c);
    }

    internal void FireDefenseChangedTriggers(Card c)
    {
        Fire(Trigger.OnAfterOtherCardGainDefense, c);
        Fire(Trigger.OnAfterOtherCardDefenseIsSet, c);
    }

    /// <summary>加防前的修正窗口（客户端 ChangeDefense 里的 id=16 / id=17）。</summary>
    internal void FireBeforeGainDefenseTriggers(Card c)
    {
        Fire(Trigger.OnBeforeOtherCardGainDefense, c);
        Fire(Trigger.OnBeforeOtherCardGainDefenseAfterAdd, c);
    }

    internal void FireKreditCostChangedTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardKreditCostChanged, c);
        Fire(Trigger.OnAfterOtherCardOperactionCostChanged, c);
    }

    /// <summary>移动端选择目标时的可被选判定（客户端 CanOtherCardBeTargetted）。</summary>
    internal void FireCanBeTargettedTriggers(Card c) => Fire(Trigger.CanOtherCardBeTargetted, c);

    /// <summary>攻击中途换目标（客户端 AttackCard 里的 id=30）。</summary>
    internal void FireAttackSwitchTargetTriggers(Card c) => Fire(Trigger.OnOtherCardAttackSwitchTarget, c);

    /// <summary>操作费用 buff 被重置（客户端 ChangeOperationCost 里的 id=9）。</summary>
    internal void FireOperactionCostBuffsResetTriggers(Card c) => Fire(Trigger.OnAfterOtherCardOperactionCostBuffsReset, c);

    // ===================== 状态位 =====================

    internal void FireSuppressTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardSuppressed, c);
        Fire(Trigger.OnAfterOtherCardSuppressed, c);
    }

    // 写成显式分支而不是 Fire(pinned ? A : B)：审计是静态扫 Fire(Trigger.X) 字面量，
    // 三元表达式它看不见，会把这两个触发点误报成「从不触发」。
    internal void FirePinTriggers(Card c, bool pinned)
    {
        if (pinned) Fire(Trigger.OnOtherUnitPinned, c);
        else Fire(Trigger.OnOtherUnitUnPinned, c);
    }

    internal void FireVeteranTriggers(Card c) => Fire(Trigger.OnOtherCardBecomingVeteran, c);

    internal void FireRevealTriggers(Card c)
    {
        Fire(Trigger.OnOtherCardRevealed, c);
        if (c.Has(Kw.Covert)) Fire(Trigger.OnOtherCovertCardSpawned, c);
    }

    internal void FireRepairTriggers(Card c)
    {
        Fire(Trigger.OnBeforeFullyRepaired, c);
        Fire(Trigger.OnOtherCardFullyRepaired, c);
    }

    internal void FireSmokescreenLossTriggers(Card c) => Fire(Trigger.OnOtherCardLoseSmokescreen, c);

    internal void FireResetTriggers(Card c) => Fire(Trigger.OnOtherCardReset, c);

    internal void FireConvertTriggers(Card c) => Fire(Trigger.OnOtherCardConverted, c);

    internal void FireRetreatTriggers(Card c) => Fire(Trigger.OnOtherCardRetreat, c);

    internal void FireSalvageTriggers(Card c) => Fire(Trigger.OnOtherCardSalvaged, c);

    internal void FireForecastTriggers(Card c) => Fire(Trigger.OnOtherCardForecasted, c);

    internal void FireDeckChangedTriggers()
    {
        Fire(Trigger.OnAfterDeckChanged);
        Fire(Trigger.OnDeckShuffled);
    }

    /// <summary>
    /// <c>OnIntelTriggered(intelCard, intelValue)</c> —— 响应者的形参是两元，
    /// 第二个是这次翻了多少张牌。只发一个实参的话，卡的逻辑读 intelValue 会拿到零。
    /// </summary>
    internal void FireIntelTriggers(Card intelCard, int amount = 0)
    {
        if (Host is null) { Fire(Trigger.OnIntelTriggered, intelCard); return; }
        foreach (var t in TriggerOrder())
        {
            if (t.Destroyed || !CardDispatch.Has(t, Trigger.OnIntelTriggered)) continue;
            CardDispatch.FireWith(Host, t, Trigger.OnIntelTriggered,
                intelCard is null ? Val.Nothing : Val.Ref(Host.Obj(intelCard)),
                Val.Of(amount));
            TriggerFireCount++;
            if (S.Done) return;
        }
    }

    internal void FireCounterMeasureTriggers(Card c) => Fire(Trigger.OnCounterMeasureTriggered, c);

    internal void FireSpawnedInHandTriggers(Card c) => Fire(Trigger.OnOtherCardSpawnedInHand, c);

    internal void FireDrawnTriggers(Card c) => Fire(Trigger.OnOtherCardDrawnFromDeck, c);

    internal void FireDiscardedTriggers(Card c) => Fire(Trigger.OnOtherCardDiscarded, c);

    internal void FireMovedInHandTriggers(Card c) => Fire(Trigger.OnOtherCardMovedToLocationInHand, c);

    internal void FireAlterCardTriggers(Card c) => Fire(Trigger.OnOtherCardCreatedAlterCard, c);

    internal void FireAbilitiesChangedTriggers(Card c) => Fire(Trigger.OnOtherCardAblitiesChanged, c);

    internal void FireFatigueTriggers() => Fire(Trigger.OnFatigueDamage);

    internal void FireKreditSpentTriggers(int amount) => Fire(Trigger.OnOtherCardOperationKreditSpent);

    /// <summary>
    /// 一次完整攻击里所有能换目标的时机（客户端在选定目标后会问一次）。
    /// 引擎的攻击是一步到位的，所以这里在开打前发一次，语义上等于「确认目标」。
    /// </summary>
    internal void FireTargetConfirmTriggers(Card atk, Card def)
    {
        FireAttackSwitchTargetTriggers(atk);
        if (def is not null) FireCanBeTargettedTriggers(def);
    }
}


