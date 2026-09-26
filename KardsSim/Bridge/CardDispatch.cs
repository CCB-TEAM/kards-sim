using KardsSim.Core;
using KardsSim.Generated;
using KardsSim.Kismet;

namespace KardsSim.Bridge;

/// <summary>
/// 触发分派：把引擎的 <see cref="Trigger"/> 事件交给卡牌自己的直译产物。
///
/// <para>
/// 这是取代 <c>Effects/EffectPlanner.cs</c>（文本解析版）的部分。
/// 以前是「读卡面文本猜效果」，现在是「调蓝图自己编译出来的字节码」——
/// 逻辑与客户端逐条一致，没有猜测成分。
/// </para>
///
/// <para>
/// 入口约定：卡牌资产的 ubergraph 分发函数叫
/// <c>ExecuteUbergraph_&lt;资产名&gt;</c>，实参是 <c>EntryPoint</c> 偏移。
/// 偏移在编译期就定死了，但同一张卡的不同事件对应哪个偏移，
/// 只能靠事件存根（<c>OnEnterPlay</c> 之类）里的调用点得知。
/// 所以这里不用猜偏移，而是直接调<b>同名事件函数</b>，由它自己转进 ubergraph。
/// </para>
/// </summary>
public static class CardDispatch
{
    /// <summary>
    /// 跑一张牌在某个触发点上的效果。返回 false 表示这张卡没注册这个触发点。
    /// </summary>
    public static bool Fire(EngineHost host, Card card, Trigger t, Card subject = null)
    {
        if (card?.Id is null) return false;
        if (t == Trigger.NotAvailable) return false;

        var fn = FnIndex.Find(card.Id, t.ToString());
        if (fn is null) return false;

        var self = Val.Ref(host.Obj(card));

        // 事件函数的形参约定（按 ERegisteredCardFunction 的实测调用点）：
        // 大多数是 (self, 相关卡) 两个；少数只有 self。多传的参数在直译产物里
        // 由 args.Length 守卫，不会越界。
        var args = subject is null
            ? new[] { self }
            : new[] { self, Val.Ref(host.Obj(subject)) };

        fn(host, self, args);
        host.SyncBack(host.Obj(card));
        if (host.TraceFire) host.OnTriggerFired?.Invoke(t, card.Id);
        return true;
    }

    /// <summary>
    /// 触发点带额外数值参数的版本（目前只有 <c>OnIntelTriggered(card, value)</c>）。
    ///
    /// <para>
    /// 为什么不能复用 <see cref="Fire"/>：那里的第二个实参是「相关卡」，
    /// 而这里的第二个是整数。混用会让卡的逻辑把整数当成卡对象读。
    /// </para>
    /// </summary>
    public static bool FireWith(EngineHost host, Card card, Trigger t, params Val[] extra)
    {
        if (card?.Id is null || t == Trigger.NotAvailable) return false;

        var fn = FnIndex.Find(card.Id, t.ToString());
        if (fn is null) return false;

        var self = Val.Ref(host.Obj(card));
        var args = new Val[1 + extra.Length];
        args[0] = self;
        Array.Copy(extra, 0, args, 1, extra.Length);

        fn(host, self, args);
        host.SyncBack(host.Obj(card));
        if (host.TraceFire) host.OnTriggerFired?.Invoke(t, card.Id);
        return true;
    }

    /// <summary>这张卡是否注册了某个触发点（用于引擎侧裁剪，避免无谓调用）。</summary>
    public static bool Has(Card card, Trigger t)
        => card?.Id is not null && FnIndex.Find(card.Id, t.ToString()) is not null;

    /// <summary>一张卡在直译产物里注册的全部触发点。</summary>
    public static IEnumerable<Trigger> TriggersOf(Card card)
    {
        if (card?.Id is null || !FnIndex.Assets.TryGetValue(card.Id, out var m)) yield break;
        foreach (var name in m.Keys)
        {
            if (Enum.TryParse<Trigger>(name, out var t) && t != Trigger.NotAvailable)
                yield return t;
        }
    }
}
