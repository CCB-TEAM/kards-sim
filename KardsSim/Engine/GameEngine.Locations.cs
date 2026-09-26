using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim.Engine;

/// <summary>
/// 位置与掩护状态维护。
///
/// <para>
/// 客户端有一整套「按行内位置相邻」的判定（Guard 只掩护紧邻的牌、
/// 若干卡按左右邻取值），它依赖卡上的 <c>locationNumber</c>。
/// 引擎自己的 <see cref="Loc"/> 没有位置概念，所以这里负责把
/// <c>locationNumber</c> 维护成客户端期望的形状，并驱动
/// <c>BP_CardFunctions.UpdateGuarded</c> 重算掩护状态。
/// </para>
///
/// <para>
/// <b>为什么不自己在引擎里算 Guard</b>：客户端的 Guard 语义比「有 Guard 就保护全排」
/// 复杂得多 —— <c>UpdateGuarded</c> 只处理支援线（location 5/6），
/// <c>GetAdjacentCards</c> 按 <c>locationNumber±1</c> 取紧邻，
/// 还要排掉未明牌的 Covert。自己写一份必然和真实规则漂移，
/// 所以这里只负责喂数据，判定交给客户端自己的实现。
/// </para>
/// </summary>
public sealed partial class GameEngine
{
    /// <summary>
    /// 重排某一方支援线的 locationNumber（0..n-1，与 <c>Board</c> 列表顺序一致），
    /// 然后让客户端实现重算掩护状态。
    /// </summary>
    public void RefreshLocationNumbers(Side side)
    {
        var p = S.Player(side);
        for (var i = 0; i < p.Board.Count; i++) p.Board[i].LocationNumber = i;
        // 前线是共享格，客户端按进入序给号
        var n = 0;
        foreach (var c in S.Frontline) c.LocationNumber = n++;
    }

    /// <summary>重排双方，再让客户端实现重算掩护。</summary>
    public void RefreshAllLocations()
    {
        RefreshLocationNumbers(Side.Left);
        RefreshLocationNumbers(Side.Right);
        UpdateGuarded();
    }

    /// <summary>
    /// 调客户端自己的 <c>BP_CardFunctions.UpdateGuarded</c>（逐个 location）。
    ///
    /// <para>
    /// 只跑 5 / 6（双方支援线）—— 客户端实现里也是只处理这两个值，
    /// 前线不加掩护（前线本来就挡在前面）。
    /// </para>
    /// </summary>
    public void UpdateGuarded()
    {
        if (Host is null) return;
        var fn = Generated.FnIndex.Find("BP_CardFunctions", "UpdateGuarded");
        if (fn is null) return;   // 没转译就退化成「无掩护」，不是错误

        var cf = Host.CardFunctions;
        foreach (var loc in stackalloc[] { 5, 6 })
        {
            try
            {
                // self = BP_CardFunctions 单例；真实实参只有 location
                fn(Host, Val.Ref(cf), new Val[] { Val.Of(loc) });
            }
            catch (Exception ex)
            {
                Unhandled.Add($"UpdateGuarded({loc}) 抛异常: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // 客户端写的是镜像里的 isBeingGuarded，同步回引擎实体
        SyncGuardedFromMirror(Side.Left);
        SyncGuardedFromMirror(Side.Right);
    }

    private void SyncGuardedFromMirror(Side side)
    {
        foreach (var c in S.Player(side).Board)
        {
            var k = Host.Obj(c);
            c.IsBeingGuarded = k.Get("isBeingGuarded").AsBool();
        }
    }

    /// <summary>
    /// 只重排双方的 <c>locationNumber</c>，<b>不</b>跑客户端的掩护重算。
    ///
    /// <para>
    /// 用在「卡自己的裸自事件之前」：事件体里会按 <c>locationNumber</c> 找左右邻
    /// （光环 / 位置型卡靠 <c>GetCardsToTheLeft</c> / <c>GetAdjacentCards</c>），
    /// 而刚换行的牌编号还停在上一条行里 —— 不先重排，事件读到的是错的行位置。
    /// 掩护重算（<c>UpdateGuarded</c>，要调客户端蓝图、比较贵）仍由
    /// <see cref="AfterBoardChange"/> 在动作收尾时统一做一次。
    /// </para>
    /// </summary>
    internal void RenumberRows()
    {
        RefreshLocationNumbers(Side.Left);
        RefreshLocationNumbers(Side.Right);
    }

    /// <summary>
    /// 位置类动作之后统一调一次。<b>每一个会改变场上占位的地方都必须调</b>，
    /// 漏一处就会出现「掩护状态和实际站位不一致」—— 这类错误不会崩，
    /// 只是规则偶尔判错，最难查。
    /// </summary>
    internal void AfterBoardChange() => RefreshAllLocations();
}
