namespace KardsTranspiler;

/// <summary>
/// UE 引擎蓝图库函数的参数修饰补充表。
///
/// **为什么需要**：这些函数来自 Engine 模块（KismetArrayLibrary / KismetSetLibrary /
/// KismetSystemLibrary），不在游戏自己的 <c>H:\kards\Source</c> 里，本机也没有引擎的
/// UHTHeaderDump，所以 <see cref="UhtParams"/> 查不到它们。而其中少数函数带真正的
/// out 参数（最典型的是 <c>Array_Get</c> 的第三个参数），漏判会让取值结果永远传不回来。
///
/// **收录原则**：只收录「包内确实调用了、且签名在 UE5 里稳定不变」的少数几个。
/// 依据是 UE5 引擎头文件里这些函数的声明（CustomThunk 家族的固定签名）：
///
/// <code>
/// // KismetArrayLibrary.h
/// UFUNCTION(BlueprintPure, CustomThunk, meta=(ArrayParm="TargetArray", ArrayTypeDependentParams="Item"))
/// static void Array_Get(const TArray&lt;int32&gt;&amp; TargetArray, int32 Index, int32&amp; Item);          // Item 是 out
///
/// UFUNCTION(BlueprintPure, CustomThunk, meta=(ArrayParm="TargetArray"))
/// static int32 Array_Length(const TArray&lt;int32&gt;&amp; TargetArray);                                 // 无 out
///
/// // KismetSetLibrary.h
/// static void Set_Add(TSet&lt;int32&gt;&amp; TargetSet, const int32&amp; NewItem);                             // 容器按引用改
/// static void Set_Clear(TSet&lt;int32&gt;&amp; TargetSet);
/// static int32 Set_Length(const TSet&lt;int32&gt;&amp; TargetSet);                                          // 无 out
///
/// // KismetSystemLibrary.h
/// static bool IsValid(const UObject* Object);                                                       // 无 out
/// </code>
///
/// 容器的「按引用修改」（Array_Add / Set_Add / Set_Clear）不需要在这里标 out：
/// 只要宿主把容器实现为引用类型，原地修改自然对调用方可见。
/// </summary>
public static class EngineLibraryParams
{
    /// <summary>函数名 → 每个形参的修饰。仅收录确实需要 out 标记的。</summary>
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        // Array_Get(Array, Index, out Item)
        ["Array_Get"] = new[] { "", "", "out" },
        // Array_Length(Array) -> int
        ["Array_Length"] = new[] { "" },
        // Set_Add(Set, Item) / Set_Clear(Set) / Set_Length(Set)
        ["Set_Add"] = new[] { "", "" },
        ["Set_Clear"] = new[] { "" },
        ["Set_Length"] = new[] { "" },
        // IsValid(Object) -> bool
        ["IsValid"] = new[] { "" },
    };

    /// <summary>查修饰；未收录返回 null。</summary>
    public static string[] Mods(string func, int argCount)
    {
        if (!Table.TryGetValue(func, out var m)) return null;
        return m.Length == argCount ? m : null;
    }

    public static IEnumerable<string> Names => Table.Keys;
}
