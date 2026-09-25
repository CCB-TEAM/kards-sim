using System.Globalization;

namespace KardsSim.Kismet;

/// <summary>装箱值：Kismet 是无类型字节码，直译出来的 C# 用统一的 Val 承载所有数据。</summary>
public enum VKind : byte { Nothing, Int, Bool, Float, Str, Name, Obj, Arr }

/// <summary>
/// Kismet 值。
///
/// 直译器不做事后类型推断，因此每个值自带标签。逻辑（跳转、比较、时序）仍然是
/// 原生 C# 控制流；只有数据是装箱的。宿主 API（Host.Call）是唯一需要懂类型的地方。
/// </summary>
public readonly struct Val
{
    public readonly VKind K;
    public readonly long I;
    public readonly double F;
    public readonly object O;

    private Val(VKind k, long i, double f, object o) { K = k; I = i; F = f; O = o; }

    public static readonly Val Nothing = new(VKind.Nothing, 0, 0, null);
    public static readonly Val True = new(VKind.Bool, 1, 0, null);
    public static readonly Val False = new(VKind.Bool, 0, 0, null);
    public static readonly Val Zero = new(VKind.Int, 0, 0, null);
    public static readonly Val One = new(VKind.Int, 1, 0, null);

    public static Val Of(bool b) => b ? True : False;
    public static Val Of(int i) => new(VKind.Int, i, i, null);
    public static Val Of(long i) => new(VKind.Int, i, i, null);
    public static Val Of(float f) => new(VKind.Float, (long)f, f, null);
    public static Val Of(double f) => new(VKind.Float, (long)f, f, null);
    public static Val Of(string s) => s is null ? Nothing : new(VKind.Str, 0, 0, s);
    public static Val Name(string s) => s is null ? Nothing : new(VKind.Name, 0, 0, s);
    public static Val Ref(object o) => o is null ? Nothing : new(VKind.Obj, 0, 0, o);
    public static Val Array(System.Collections.IEnumerable e) => e is null ? Nothing : new(VKind.Arr, 0, 0, e);

    /// <summary>
    /// out 实参占位。Kismet 的 out 参数是「可写位置」，直译时用回调表达：
    /// 宿主调用 <c>SetOut</c> 时回调把值写回局部/成员。
    /// </summary>
    public static Val Out(Action<Val> setter) => new(VKind.Obj, 0, 0, setter);

    /// <summary>若是 out 占位则回写；返回是否消费了该占位。</summary>
    public static bool TrySetOut(Val slot, Val v)
    {
        if (slot.O is Action<Val> a) { a(v); return true; }
        return false;
    }

    public static implicit operator Val(bool b) => Of(b);
    public static implicit operator Val(int i) => Of(i);
    public static implicit operator Val(long i) => Of(i);
    public static implicit operator Val(float f) => Of(f);
    public static implicit operator Val(double f) => Of(f);
    public static implicit operator Val(string s) => Of(s);

    public bool IsNothing => K == VKind.Nothing;

    public long AsInt() => K switch
    {
        VKind.Int => I,
        VKind.Bool => I,
        VKind.Float => (long)F,
        VKind.Nothing => 0,
        _ => 0,
    };

    public double AsFloat() => K switch
    {
        VKind.Int => I,
        VKind.Bool => I,
        VKind.Float => F,
        _ => 0,
    };

    public bool AsBool() => K switch
    {
        VKind.Bool => I != 0,
        VKind.Int => I != 0,
        VKind.Float => F != 0,
        VKind.Nothing => false,
        VKind.Obj => O is not null,
        VKind.Str => !string.IsNullOrEmpty((string)O),
        _ => false,
    };

    public string AsStr() => O as string ?? (K switch
    {
        VKind.Int => I.ToString(CultureInfo.InvariantCulture),
        VKind.Bool => I != 0 ? "true" : "false",
        VKind.Float => F.ToString(CultureInfo.InvariantCulture),
        _ => "",
    });

    public object AsObj() => O;

    public T As<T>() => O is T t ? t : default;

    // ---- 运算：全部经 AsFloat/AsInt 归一，与 Kismet 的数值提升一致 ----

    public static Val operator +(Val a, Val b) =>
        a.K == VKind.Str || b.K == VKind.Str ? Of(a.AsStr() + b.AsStr()) : Of(a.AsFloat() + b.AsFloat());

    public static Val operator -(Val a, Val b) => Of(a.AsFloat() - b.AsFloat());
    public static Val operator *(Val a, Val b) => Of(a.AsFloat() * b.AsFloat());
    public static Val operator /(Val a, Val b) => b.AsFloat() == 0 ? Zero : Of(a.AsFloat() / b.AsFloat());
    public static Val operator %(Val a, Val b) => b.AsInt() == 0 ? Zero : Of(a.AsInt() % b.AsInt());

    public static bool operator ==(Val a, Val b) => Cmp(a, b) == 0;
    public static bool operator !=(Val a, Val b) => Cmp(a, b) != 0;
    public static bool operator <(Val a, Val b) => Cmp(a, b) < 0;
    public static bool operator >(Val a, Val b) => Cmp(a, b) > 0;
    public static bool operator <=(Val a, Val b) => Cmp(a, b) <= 0;
    public static bool operator >=(Val a, Val b) => Cmp(a, b) >= 0;

    /// <summary>相等判定：字符串/对象按引用或值比，数值按数值比。Kismet 的 EqualEqual_* 语义。</summary>
    public static int Cmp(Val a, Val b)
    {
        if (a.K == VKind.Str || b.K == VKind.Str) return string.CompareOrdinal(a.AsStr(), b.AsStr());
        if (a.K == VKind.Obj || b.K == VKind.Obj) return ReferenceEquals(a.O, b.O) ? 0 : 1;
        if (a.K == VKind.Nothing || b.K == VKind.Nothing) return a.K == b.K ? 0 : 1;
        if (a.K == VKind.Float || b.K == VKind.Float)
        {
            var d = a.AsFloat() - b.AsFloat();
            return d < 0 ? -1 : d > 0 ? 1 : 0;
        }
        var l = a.AsInt() - b.AsInt();
        return l < 0 ? -1 : l > 0 ? 1 : 0;
    }

    public override bool Equals(object obj) => obj is Val v && Cmp(this, v) == 0;
    public override int GetHashCode() => K switch
    {
        VKind.Str or VKind.Name => O?.GetHashCode() ?? 0,
        VKind.Obj => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(O),
        VKind.Float => F.GetHashCode(),
        _ => I.GetHashCode(),
    };

    public override string ToString() => K switch
    {
        VKind.Nothing => "none",
        VKind.Int => I.ToString(CultureInfo.InvariantCulture),
        VKind.Bool => I != 0 ? "true" : "false",
        VKind.Float => F.ToString("0.###", CultureInfo.InvariantCulture),
        VKind.Str or VKind.Name => (string)O ?? "",
        VKind.Obj => O?.ToString() ?? "null",
        VKind.Arr => "[" + string.Join(", ", ArrItems()) + "]",
        _ => "?",
    };

    private IEnumerable<string> ArrItems()
    {
        if (O is System.Collections.IEnumerable e)
            foreach (var x in e) yield return x?.ToString() ?? "null";
    }

    /// <summary>EX_SwitchValue 用：按索引挑选分支。</summary>
    public static Val Switch(Val index, (Val Case, Val Result)[] cases, Val fallback)
    {
        foreach (var (c, r) in cases)
            if (Cmp(index, c) == 0) return r;
        return fallback;
    }
}
