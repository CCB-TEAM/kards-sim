using KardsSim.Kismet;

namespace KardsSim.Kismet;

/// <summary>
/// 宿主接口：直译出来的 C# 通过它访问游戏世界。
///
/// 直译代码不做语义理解，所有「这是什么」的判断都收敛到这里。这样 Kismet 侧的
/// 转译保持机械（因此可自动化、可验证），而规则语义在一个地方集中实现。
/// </summary>
public interface IHost
{
    // ---- 变量槽：局部变量 / 实例属性统一按名字存取 ----
    Val GetVar(string name);
    void SetVar(string name, Val v);

    /// <summary>实例属性按 (对象, 属性名) 存取；obj 为 null 表示 self。</summary>
    Val GetMember(Val obj, string property);
    void SetMember(Val obj, string property, Val v);

    /// <summary>调用函数。args 已按 Kismet 的实参顺序排好（含 out 实参）。</summary>
    Val Call(string func, Val[] args);

    /// <summary>Out 参数回写：Kismet 把 out 实参当作一个可写位置传入。</summary>
    void SetOut(Val slot, Val v);

    // ---- 容器 ----
    Val ArrayLength(Val arr);
    Val ArrayGet(Val arr, int index);
    void ArraySet(Val arr, int index, Val v);
    void ArrayAdd(Val arr, Val v);
    Val MakeArray(IEnumerable<Val> items);

    // ---- 时序 ----
    /// <summary>等待外部输入（玩家/AI 决策）。直译代码遇到玩家输入点时会挂起。</summary>
    void AwaitInput(string kind, Val payload);

    /// <summary>日志（诊断用）。</summary>
    void Log(string msg);
}

