using KardsSim.Core;
using KardsSim.Generated;
using KardsSim.Kismet;

namespace KardsSim.Bridge;

/// <summary>
/// 一次触发要传给卡牌逻辑的载荷，按<b>形参名</b>存。
///
/// <para>
/// 为什么要具名而不是位置：事件形参个数从 0 到 6 不等
/// （<c>OnOtherCardDestroyed</c> 有 6 个），而引擎调用点各不相同。
/// 用具名载荷 + 生成好的形参表（<see cref="FnIndex.EventParams"/>），
/// 就能保证「值落在正确的槽位」，而不是靠调用方记住顺序。
/// </para>
///
/// <para>
/// 之前是按位置固定传两个，第 3 个参数起恒为 <c>Nothing</c>。
/// 而直译产物里大量 <c>if (!destroyedInCombat) return;</c> 这类前置守卫，
/// 于是效果<b>静默消失</b> —— 不报错、不进 Unhandled，
/// 自对弈的「未实现调用 0%」也照样是绿的。
/// </para>
/// </summary>
public sealed class TriggerPayload
{
    private readonly Dictionary<string, Val> _named = new(StringComparer.Ordinal);

    /// <summary>相关的卡（多数事件的第一张卡参数）。</summary>
    public Card Subject;

    /// <summary>
    /// 以「相关卡」建载荷。
    ///
    /// <para><b>必须设 <see cref="Subject"/> 字段本身</b>：<c>BuildArgs</c> 是靠
    /// <c>payload.Subject</c> 给第一个卡参数兜底的。曾经这里只调了
    /// <c>SetCard("subject", …)</c>，而 <c>SetCard</c> 在 <c>host == null</c> 时直接
    /// 返回不写值，于是 Subject 恒为 null、<c>args[0]</c> 恒为 Nothing。
    /// 后果是 <c>if (!IsUnit(cardDestroyed)) return;</c> 这类第一参守卫全部短路，
    /// 触发点照常计次、日志看着一切正常，效果却什么都不做。</para>
    /// </summary>
    public static TriggerPayload Of(Card subject) => new() { Subject = subject };

    public TriggerPayload Set(string name, Val v)
    {
        if (!string.IsNullOrEmpty(name)) _named[name] = v;
        return this;
    }

    /// <summary>按卡设置（卡参数一律走镜像对象引用，直译产物按对象读成员）。</summary>
    public TriggerPayload SetCard(string name, Card c, EngineHost host = null)
    {
        if (c is null || host is null) return this;
        return Set(name, Val.Ref(host.Obj(c)));
    }

    public TriggerPayload SetBool(string name, bool v) => Set(name, Val.Of(v));
    public TriggerPayload SetInt(string name, int v) => Set(name, Val.Of(v));

    /// <summary>供 <see cref="CardDispatch"/> 按形参表取值。</summary>
    internal Val Get(string name) => _named.TryGetValue(name, out var v) ? v : Val.Nothing;
}

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
        => Fire(host, card, t, TriggerPayload.Of(subject));

    /// <summary>
    /// 带具名载荷的版本。按生成好的形参表把值填到正确槽位 ——
    /// 这是「事件参数能真正到达效果体」的唯一保证。
    /// </summary>
    public static bool Fire(EngineHost host, Card card, Trigger t, TriggerPayload payload)
    {
        if (card?.Id is null || t == Trigger.NotAvailable) return false;

        var fn = FnIndex.Find(card.Id, t.ToString());
        if (fn is null) return false;

        var self = Val.Ref(host.Obj(card));
        payload ??= new TriggerPayload();

        var args = BuildArgs(host, t, payload, self);

        fn(host, self, args);
        host.SyncBack(host.Obj(card));
        if (host.TraceFire) host.OnTriggerFired?.Invoke(t, card.Id);
        return true;
    }

    /// <summary>
    /// 按形参表逐位填值。
    ///
    /// <para>
    /// 事件名就是触发点名（<c>Trigger.OnOtherCardDestroyed</c> → <c>"OnOtherCardDestroyed"</c>），
    /// 形参表在转译时从 <c>LoadedProperties</c> 抽出来，与
    /// <c>Emitter.EmitParamBindings</c> 用的是同一份来源，所以位置一定对得上。
    /// </para>
    ///
    /// <para>
    /// 表里查不到该事件时退化成旧的「self + 相关卡」两个参数 ——
    /// 至少不比以前差，但会在 <c>Unhandled</c> 里留痕，避免又变成静默缺口。
    /// </para>
    /// </summary>
    private static Val[] BuildArgs(EngineHost host, Trigger t, TriggerPayload payload, Val self)
    {
        var names = FnIndex.ParamsOf(t.ToString());
        if (names.Length == 0)
        {
            return payload.Subject is null
                ? new[] { self }
                : new[] { self, Val.Ref(host.Obj(payload.Subject)) };
        }

        var args = new Val[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var n = names[i];
            var v = payload.Get(n);
            // 载荷没显式给值时的兜底：第一张卡参数用 Subject 顶上。
            // 大多数事件的第一参就是「相关的卡」，这样单卡调用点不用逐个写名字。
            if (v.IsNothing && i == 0 && payload.Subject is not null)
                v = Val.Ref(host.Obj(payload.Subject));
            args[i] = v;
        }
        return args;
    }

    /// <summary>
    /// 触发点带额外数值参数的版本（目前只有 <c>OnIntelTriggered(card, value)</c>）。
    ///
    /// <para>
    /// 为什么不能复用 <see cref="Fire(EngineHost, Card, Trigger, Card)"/>：那里的第二个实参是「相关卡」，
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
