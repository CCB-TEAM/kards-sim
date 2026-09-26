using KardsSim.Core;

namespace KardsSim.Engine;

/// <summary>一个待决选择的候选项。</summary>
public sealed class ChoiceOption
{
    /// <summary>
    /// 候选卡的 cardID。静态卡是负数（见 EngineHost.ObjStatic）；
    /// 客户端 Develop 路径给的是**卡名**而不是 id，这时这里是 0，看 <see cref="Name"/>。
    /// </summary>
    public int CardId;

    /// <summary>候选卡名（客户端非牌库路径传的就是名字数组）。</summary>
    public string Name;

    /// <summary>给人和 AI 看的标签，用于抉择预览。</summary>
    public string Label;

    public override string ToString() => $"{Label}(id={CardId})";
}

/// <summary>
/// 一次「效果跑到一半要求玩家选一张牌」的待决状态。
///
/// <para>
/// 为什么需要它：卡牌逻辑是**同步**执行的，没有挂起/恢复机制
/// （直译产物里 <c>AwaitInput</c> 一次都没被调用）。而客户端的 Develop / 选手牌
/// 是 UI 往返：效果体调 <c>NotifySelectCardToDrawPending</c> 通知 UI 列出候选，
/// UI 让玩家点，再回调 <c>OnHandTargetSelected(卡, 选中的ID, instigatorID)</c>
/// 执行真正的效果 —— 而**调用方忽略** <c>selectCardToDraw</c> 的返回值，
/// 所以把选择挪到效果之后是安全的（不会重复结算）。
/// </para>
///
/// <para>
/// 于是做成两段式：效果体发起选择 → 引擎记下待决状态 → 动作列表里只剩候选项
/// （<see cref="ActionType.ChooseCard"/>）→ 选定后回调 <c>OnHandTargetSelected</c>。
/// </para>
/// </summary>
public sealed class PendingChoice
{
    /// <summary>发起选择的卡（回调要打在它身上）。</summary>
    public Card Source;
    /// <summary>选择类型（目前只有 selectCardToDraw）。</summary>
    public string Kind;
    /// <summary>候选项。</summary>
    public List<ChoiceOption> Options = new();
    /// <summary>客户端传来的 isEffect 标志（是卡牌效果发起，还是别的流程）。</summary>
    public bool IsEffect;

    public override string ToString()
        => $"{Kind}({Source?.Id}) 候选 {Options.Count} 个";
}

public sealed partial class GameEngine
{
    /// <summary>当前待决的选择；null 表示没有。有它时 <see cref="LegalActions"/> 只返回选项。</summary>
    public PendingChoice Pending { get; private set; }

    /// <summary>由宿主在卡牌逻辑请求「选一张牌」时调用（见 <see cref="PendingChoice"/>）。</summary>
    public void RaiseChoice(Card source, string kind, IEnumerable<ChoiceOption> options, bool isEffect = false)
    {
        var list = options.Where(o => o is not null).ToList();
        if (list.Count == 0) return;   // 没有候选就不该产生待决状态，否则会把对局卡住
        Pending = new PendingChoice { Source = source, Kind = kind, IsEffect = isEffect };
        Pending.Options.AddRange(list);
        S.Log.Line($"    [抉择] {kind} 候选 {list.Count} 个：{string.Join(", ", list.Take(6).Select(o => o.Label))}");
    }

    public void ClearChoice() => Pending = null;

    /// <summary>
    /// 结算一个选择：清掉待决状态，回调发起卡自己的 <c>OnHandTargetSelected</c>。
    /// 回调里可能再发起下一次选择（例如「Develop 两次」），所以这里不能假设一次就结束。
    /// </summary>
    private bool DoChooseCard(GameAction a)
    {
        var p = Pending;
        if (p is null) return false;
        if (a.HandIndex < 0 || a.HandIndex >= p.Options.Count) return false;
        var opt = p.Options[a.HandIndex];
        if (opt.CardId != a.TargetId) return false;

        Pending = null;
        // 选择本身已经结算，动作就是合法的 —— 回调缺失是卡牌侧的已知偏差
        //（客户端这类卡的 Develop 结果是 UI 直接做的），不能算成「非法动作」，
        // 否则自对弈会把它记成引擎缺陷。
        Host?.ResolveHandTargetSelected(p.Source, opt.CardId);
        return true;
    }
}
