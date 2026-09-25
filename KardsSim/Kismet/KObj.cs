using System.Collections;

namespace KardsSim.Kismet;

/// <summary>
/// Kismet 对象：卡牌 / GameState / 子系统 / 函数库实例。
///
/// 字段按名字存值，不做静态类型化 —— 因为蓝图侧本来就是弱类型的
/// （同一属性在不同卡上可能是 int、enum 或 object 引用）。类型语义集中在
/// <see cref="Host"/> 的调用实现里，而不是散在字段声明上。
/// </summary>
public sealed class KObj
{
    public readonly Dictionary<string, Val> Fields = new(StringComparer.Ordinal);
    public string ClassName;
    public int Id;

    public KObj(string className = "", int id = 0) { ClassName = className; Id = id; }

    public Val Get(string n) => Fields.TryGetValue(n, out var v) ? v : Val.Nothing;
    public void Set(string n, Val v) => Fields[n] = v;
    public bool Has(string n) => Fields.ContainsKey(n);

    public override string ToString() =>
        string.IsNullOrEmpty(ClassName) ? $"KObj#{Id}" : $"{ClassName}#{Id}";

    /// <summary>集合类型：蓝图 Set&lt;byte&gt; 直译成 HashSet 语义（去重 + 无序遍历）。</summary>
    public static HashSet<long> AsSet(Val v) => v.O as HashSet<long>;
}

/// <summary>
/// Kismet 的结构体常量（EX_VectorConst / EX_RotationConst / EX_Vector3fConst）。
/// 蓝图里 V 结构以值语义整体传递，直译产物用 <c>Val.Ref(new Vec3(x,y,z))</c> 承载。
/// </summary>
public sealed class Vec3
{
    public double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public override string ToString() => $"({X}, {Y}, {Z})";
    public static Vec3 Zero => new(0, 0, 0);
}

/// <summary>
/// 蓝图数组：Kismet 的 TArray 是值语义 + 引用传递的混合体。
/// 直译产物把数组当对象传来传去，因此这里用可变列表承载，原地修改对调用方可见。
/// </summary>
public sealed class KArr : IEnumerable<Val>
{
    public readonly List<Val> Items = new();
    public KArr() { }
    public KArr(IEnumerable<Val> xs) { Items.AddRange(xs); }
    public int Count => Items.Count;
    public Val this[int i] { get => i >= 0 && i < Items.Count ? Items[i] : Val.Nothing; set { if (i >= 0 && i < Items.Count) Items[i] = value; } }
    public IEnumerator<Val> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();
    public override string ToString() => "[" + string.Join(", ", Items) + "]";
}
