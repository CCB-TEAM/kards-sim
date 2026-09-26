using System.Globalization;

namespace KardsSim.Kismet;

/// <summary>
/// 宿主实现：直译出来的 C# 通过 <see cref="IHost"/> 访问游戏世界。
///
/// 设计要点：
/// <list type="number">
/// <item>未实现的分派统一走 <see cref="Unhandled"/>：登记调用名、返回 Nothing、
///       绝不抛异常。这样「哪些宿主 API 还没写」是一个可查询的清单，而不是运行期崩溃。</item>
/// <item>纯函数（容器 / 数学 / 转换 / 字符串）与 UI no-op 在这里就地实现，
///       它们没有规则语义，不需要进游戏引擎。</item>
/// <item>规则相关的调用交给 <see cref="RuleDispatch"/>（由 GameEngine 侧注入）。</item>
/// </list>
/// </summary>
public class Host : IHost
{
    /// <summary>
    /// 规则回调：返回 <c>null</c> 表示「未处理」，交给下一层。
    /// 返回 <c>Val</c>（含 <c>Val.Nothing</c>）表示「已处理，这就是返回值」。
    ///
    /// 必须是 <c>Val?</c> 而不是 <c>bool</c>：蓝图函数有两类返回值传递方式 ——
    /// 走 out 形参（Array_Get）和走函数返回值（IsValid / Set_Length）。后者在直译产物里是
    /// <c>L["x"] = H.Call(...)</c> 的赋值形式，若这里只回一个 bool，返回值就丢了，
    /// 调用点拿到 Nothing → 条件判断走反 → 整段效果被静默跳过。
    ///
    /// 名字不叫 Rules：那会和 <c>KardsSim.Core.Rules</c>（规则常量）撞名，
    /// 在同时 using 两个命名空间的文件里会变成不明确引用。
    /// </summary>
    public Func<string, Val[], Val?> RuleDispatch;

    /// <summary>状态根对象（GameState / self 等），按名字索引。</summary>
    public readonly Dictionary<string, Val> Globals = new(StringComparer.Ordinal);

    /// <summary>
    /// 引擎库内建（Array/Map/Set/String/Math…）的函数名，给完备性审计用。
    ///
    /// 这份名单是<b>探测出来的</b>，不是手写维护的：拿 Generated 里所有实际调用目标
    /// 逐个去问 <see cref="Builtin"/> 认不认。手写名单一定会和 switch 里的 case 漂移，
    /// 那样审计就会把「其实有实现」的函数误报成缺口。
    /// </summary>
    public static IReadOnlyCollection<string> BuiltinNames { get; } = ProbeBuiltins();

    private static List<string> ProbeBuiltins()
    {
        var names = new List<string>();
        var genDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Generated");
        if (!Directory.Exists(genDir)) genDir = "Generated";
        if (!Directory.Exists(genDir)) return names;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rx = new System.Text.RegularExpressions.Regex("H\\.Call\\(\"([^\"]+)\"");
        foreach (var f in Directory.EnumerateFiles(genDir, "*.g.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f)))
                seen.Add(m.Groups[1].Value);

        // 8 个 Nothing 占位：内建实现都按需取用，越界前会自己判断
        var probe = new Host();
        var dummy = Enumerable.Repeat(Val.Nothing, 8).ToArray();
        foreach (var n in seen)
            if (probe.Builtin(n, dummy).Found) names.Add(n);
        return names;
    }
    public readonly Dictionary<string, int> Unhandled = new(StringComparer.Ordinal);

    /// <summary>所有 Call 的总次数（用于算覆盖率）。</summary>
    public long CallCount;

    /// <summary>
    /// 触发点实测钩子：引擎真要跑一张卡的某个触发点时回调一次。
    ///
    /// 存在的理由是静态覆盖会说谎 —— 「引擎发了 OnX」不等于「有卡接住了」。
    /// 只有实测命中数才能区分「完整」和「空跑」（发出去但场上没有响应者）。
    /// </summary>
    public Action<Core.Trigger, string> OnTriggerFired;

    /// <summary>打开触发点实测（有额外开销，只在审计时开）。</summary>
    public bool TraceFire;

    /// <summary>诊断日志；默认丢弃。</summary>
    public Action<string> Logger;

    /// <summary>是否把未实现调用也记日志（调试时开）。</summary>
    public bool Verbose;

    /// <summary>玩家输入请求；模拟器里由 AI 决策填充。</summary>
    public Func<string, Val, Val> InputHandler;

    // 集合库实例：全局唯一，因为蓝图里对 Set/Array 的修改是就地生效的
    public readonly KObj SetLib = new("BlueprintSetLibrary");
    public readonly KObj ArrayLib = new("KismetArrayLibrary");
    public readonly KObj MapLib = new("BlueprintMapLibrary");
    public readonly KObj StringLib = new("KismetStringLibrary");
    public readonly KObj MathLib = new("KismetMathLibrary");
    public readonly KObj ConvLib = new("KismetMathLibrary");
    public readonly KObj TextLib = new("KismetTextLibrary");
    public readonly KObj SysLib = new("KismetSystemLibrary");
    public readonly KObj JsonLib = new("BlueprintJsonLibrary");

    // ===================== 变量 =====================

    private readonly Dictionary<string, Val> _vars = new(StringComparer.Ordinal);
    public Val GetVar(string name) => _vars.TryGetValue(name, out var v) ? v : Val.Nothing;
    public void SetVar(string name, Val v) => _vars[name] = v;

    // ===================== 成员 =====================

    public Val GetMember(Val obj, string property)
    {
        var o = Resolve(obj);
        if (o is null) return Val.Nothing;
        return o.Get(property);
    }

    public void SetMember(Val obj, string property, Val v)
    {
        var o = Resolve(obj);
        if (o is null) return;
        o.Set(property, v);
    }

    /// <summary>把 Val 解析成 KObj；字符串引用（如 "BlueprintSetLibrary"）映射到对应的库实例。</summary>
    public KObj Resolve(Val v)
    {
        if (v.K == VKind.Obj)
        {
            if (v.O is KObj k) return k;
            if (v.O is string s) return LibByName(s);
            return null;
        }
        if (v.K == VKind.Str || v.K == VKind.Name) return LibByName(v.AsStr());
        return null;
    }

    private KObj LibByName(string s) => s switch
    {
        "BlueprintSetLibrary" => SetLib,
        "KismetArrayLibrary" => ArrayLib,
        "BlueprintMapLibrary" => MapLib,
        "KismetStringLibrary" => StringLib,
        "KismetMathLibrary" => MathLib,
        "KismetTextLibrary" => TextLib,
        "KismetSystemLibrary" => SysLib,
        "BlueprintJsonLibrary" => JsonLib,
        _ => null,
    };

    // ===================== 容器 =====================

    public Val ArrayLength(Val arr) => Val.Of(AsArr(arr)?.Count ?? 0);
    public Val ArrayGet(Val arr, int index) => AsArr(arr)?[index] ?? Val.Nothing;

    public void ArraySet(Val arr, int index, Val v)
    {
        var a = AsArr(arr);
        if (a is not null && index >= 0 && index < a.Count) a[index] = v;
    }

    public void ArrayAdd(Val arr, Val v) => AsArr(arr)?.Items.Add(v);
    public Val MakeArray(IEnumerable<Val> items) => Val.Ref(new KArr(items));

    public static KArr AsArr(Val v) => v.O as KArr;

    // ===================== 时序 =====================

    public void AwaitInput(string kind, Val payload)
    {
        if (InputHandler is not null) InputHandler(kind, payload);
    }

    public void Log(string msg)
    {
        if (Verbose) (Logger ?? Console.WriteLine)($"[kismet] {msg}");
    }

    // ===================== 调用分派 =====================

    public Val Call(string func, Val[] args)
    {
        CallCount++;

        // 1. 规则层优先（GameEngine 侧的实现）。
        //    返回 null 才算未处理 —— 不能把「处理了但返回 none」当成未处理，
        //    否则纯副作用调用（Set_Clear / ChangeAttack）会被再走一遍内建分派。
        if (RuleDispatch is not null && RuleDispatch(func, args) is { } rv) return rv;

        // 2. 纯函数 / UI no-op / 引擎库
        var v = Builtin(func, args);
        if (v.Found) return v.Value;

        // 3. 未实现：登记但不崩
        Unhandled[func] = Unhandled.TryGetValue(func, out var n) ? n + 1 : 1;
        if (Verbose) Log($"未实现宿主调用: {func}({args.Length} 实参)");
        return Val.Nothing;
    }

    private readonly record struct R(Val Value, bool Found);

    private static readonly R Miss = new(Val.Nothing, false);
    private static R Hit(Val v) => new(v, true);

    // ===================== 纯函数实现 =====================

    /// <summary>
    /// 与规则无关的宿主函数：容器、数学、类型转换、字符串、UI no-op。
    /// 这些函数在蓝图里被大量调用（Array_* / Conv_* / EnumCompare* / Notify*），
    /// 但语义是通用的，与 KARDS 规则无关，因此在这里就地实现。
    /// </summary>
    private R Builtin(string f, Val[] a)
    {
        // 约定：a[0] 是接收者（静态调用为 Nothing 或库引用）
        switch (f)
        {
            // ---------- 数组 ----------
            // Array_Get 的结果走 out 形参（第三个），漏掉它整条循环都拿不到元素。
            case "Array_Get":
                {
                    var arr = AsArr(a[1]);
                    var idx = (int)a[2].AsInt();
                    Val.TrySetOut(a[3], arr is not null && idx >= 0 && idx < arr.Count ? arr[idx] : Val.Nothing);
                    return Hit(Val.Nothing);
                }
            case "Array_Set":
                {
                    var arr = AsArr(a[1]);
                    var idx = (int)a[2].AsInt();
                    if (arr is not null && idx >= 0 && idx < arr.Count) arr[idx] = a[3];
                    return Hit(Val.Nothing);
                }
            case "Array_Length": return Hit(Val.Of(AsArr(a[1])?.Count ?? 0));
            case "Array_IsEmpty": return Hit(Val.Of((AsArr(a[1])?.Count ?? 0) == 0));
            case "Array_IsNotEmpty": return Hit(Val.Of((AsArr(a[1])?.Count ?? 0) != 0));
            case "Array_IsValidIndex":
                { var n = AsArr(a[1])?.Count ?? 0; var i = (int)a[2].AsInt(); return Hit(Val.Of(i >= 0 && i < n)); }
            case "Array_LastIndex": return Hit(Val.Of((AsArr(a[1])?.Count ?? 0) - 1));
            case "Array_Add": AsArr(a[1])?.Items.Add(a[2]); return Hit(Val.Nothing);
            case "Array_AddUnique":
                {
                    var arr = AsArr(a[1]);
                    if (arr is not null && !arr.Items.Any(x => Val.Cmp(x, a[2]) == 0)) arr.Items.Add(a[2]);
                    return Hit(Val.Nothing);
                }
            case "Array_Append":
                {
                    var dst = AsArr(a[1]); var src = AsArr(a[2]);
                    if (dst is not null && src is not null) dst.Items.AddRange(src.Items);
                    return Hit(Val.Nothing);
                }
            case "Array_Insert": AsArr(a[1])?.Items.Insert(Math.Clamp((int)a[2].AsInt(), 0, AsArr(a[1])!.Count), a[3]); return Hit(Val.Nothing);
            case "Array_Remove": AsArr(a[1])?.Items.RemoveAt((int)a[2].AsInt()); return Hit(Val.Nothing);
            case "Array_RemoveItem": AsArr(a[1])?.Items.RemoveAll(x => Val.Cmp(x, a[2]) == 0); return Hit(Val.Nothing);
            case "Array_Clear": AsArr(a[1])?.Items.Clear(); return Hit(Val.Nothing);
            case "Array_Resize":
                {
                    var arr = AsArr(a[1]);
                    if (arr is not null) { var n = (int)a[2].AsInt(); while (arr.Count > n) arr.Items.RemoveAt(arr.Count - 1); while (arr.Count < n) arr.Items.Add(Val.Nothing); }
                    return Hit(Val.Nothing);
                }
            case "Array_Contains": return Hit(Val.Of(AsArr(a[1])?.Items.Any(x => Val.Cmp(x, a[2]) == 0) ?? false));
            case "Array_Reverse": AsArr(a[1])?.Items.Reverse(); return Hit(Val.Nothing);
            case "Array_ShuffleFromStream": Shuffle(AsArr(a[1])); return Hit(Val.Nothing);
            case "Array_Identical":
                {
                    var x = AsArr(a[1]); var y = AsArr(a[2]);
                    if (x is null || y is null || x.Count != y.Count) return Hit(Val.False);
                    for (var i = 0; i < x.Count; i++) if (Val.Cmp(x[i], y[i]) != 0) return Hit(Val.False);
                    return Hit(Val.True);
                }
            case "MinOfIntArray":
                {
                    var arr = AsArr(a[1]);
                    if (arr is null || arr.Count == 0) return Hit(Val.Of(0));
                    return Hit(Val.Of(arr.Items.Min(x => x.AsInt())));
                }

            // ---------- Set ----------
            case "Set_Add": SetOf(a[1])?.Add(a[2].AsInt()); return Hit(Val.Nothing);
            case "Set_Clear": SetOf(a[1])?.Clear(); return Hit(Val.Nothing);
            case "Set_Length": return Hit(Val.Of(SetOf(a[1])?.Count ?? 0));
            case "Set_Contains": return Hit(Val.Of(SetOf(a[1])?.Contains(a[2].AsInt()) ?? false));
            case "Set_RemoveItems":
                {
                    var s = SetOf(a[1]);
                    if (s is not null) foreach (var x in AsArr(a[2])?.Items ?? new List<Val>()) s.Remove(x.AsInt());
                    return Hit(Val.Nothing);
                }
            case "Set_ToArray":
                {
                    var s = SetOf(a[1]);
                    var outv = new KArr((s ?? new HashSet<long>()).Select(x => Val.Of((int)x)));
                    Val.TrySetOut(a[2], Val.Ref(outv));
                    return Hit(Val.Nothing);
                }

            // ---------- Map ----------
            case "Map_Add": MapOf(a[1])?[a[2].AsStr()] = a[3]; return Hit(Val.Nothing);
            case "Map_Remove": MapOf(a[1])?.Remove(a[2].AsStr()); return Hit(Val.Nothing);
            case "Map_Length": return Hit(Val.Of(MapOf(a[1])?.Count ?? 0));
            case "Map_IsNotEmpty": return Hit(Val.Of((MapOf(a[1])?.Count ?? 0) != 0));
            case "Map_Contains": return Hit(Val.Of(MapOf(a[1])?.ContainsKey(a[2].AsStr()) ?? false));
            case "Map_Find":
                {
                    var m = MapOf(a[1]);
                    Val got = Val.Nothing;
                    var found = m is not null && m.TryGetValue(a[2].AsStr(), out got);
                    Val.TrySetOut(a[3], found ? got : Val.Nothing);
                    return Hit(Val.Of(found));
                }
            case "Map_Keys":
                {
                    var ks = new KArr((MapOf(a[1])?.Keys ?? Enumerable.Empty<string>()).Select(x => Val.Of(x)));
                    Val.TrySetOut(a[2], Val.Ref(ks));
                    return Hit(Val.Nothing);
                }

            // ---------- 数学 ----------
            case "Abs_Int": return Hit(Val.Of(Math.Abs(a[1].AsInt())));
            case "FMod":
                {
                    var d = a[2].AsFloat();
                    Val.TrySetOut(a[3], Val.Of(d == 0 ? 0 : a[1].AsFloat() % d));
                    return Hit(Val.Nothing);
                }
            case "FTrunc": return Hit(Val.Of(Math.Truncate(a[1].AsFloat())));
            case "Clamp": return Hit(Val.Of(Math.Clamp(a[1].AsFloat(), a[2].AsFloat(), a[3].AsFloat())));
            case "Min": return Hit(Val.Of(Math.Min(a[1].AsInt(), a[2].AsInt())));
            case "Max": return Hit(Val.Of(Math.Max(a[1].AsInt(), a[2].AsInt())));
            case "Not_PreBool": return Hit(Val.Of(!a[1].AsBool()));
            case "BooleanNOR": return Hit(Val.Of(!(a[1].AsBool() || a[2].AsBool())));
            case "SelectInt": return Hit(a[1].AsBool() ? a[2] : a[3]);
            case "SelectString": return Hit(a[1].AsBool() ? a[2] : a[3]);
            case "GetValidValue": return Hit(a[1]);

            // ---------- 类型转换 ----------
            case "Conv_IntToInt64": return Hit(a[1]);
            case "Conv_Int64ToInt": return Hit(a[1]);
            case "Conv_ByteToInt": return Hit(Val.Of((int)a[1].AsInt()));
            case "Conv_IntToByte": return Hit(Val.Of((int)(byte)a[1].AsInt()));
            case "Conv_IntToBool": return Hit(Val.Of(a[1].AsInt() != 0));
            case "Conv_IntToDouble": return Hit(Val.Of((double)a[1].AsInt()));
            case "Conv_IntToFloat": return Hit(Val.Of((float)a[1].AsInt()));
            case "Conv_IntToString": return Hit(Val.Of(a[1].AsInt().ToString(CultureInfo.InvariantCulture)));
            case "Conv_IntToText": return Hit(Val.Of(a[1].AsInt().ToString(CultureInfo.InvariantCulture)));
            case "Conv_NameToString": return Hit(Val.Of(a[1].AsStr()));
            case "Conv_StringToName": return Hit(Val.Name(a[1].AsStr()));
            case "Conv_TextToString": return Hit(Val.Of(a[1].AsStr()));
            case "Conv_StringToText": return Hit(Val.Of(a[1].AsStr()));
            case "Conv_StringToInt":
                return Hit(Val.Of(int.TryParse(a[1].AsStr(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ip) ? ip : 0));
            case "Conv_FloatToString": return Hit(Val.Of(a[1].AsFloat().ToString(CultureInfo.InvariantCulture)));
            case "Conv_BoolToInt": return Hit(Val.Of(a[1].AsBool() ? 1 : 0));
            case "Conv_ObjectToString": return Hit(Val.Of(a[1].ToString()));

            // ---------- 字符串 ----------
            // 注意这三个：它们的结果是「返回值」，不是 out 形参。
            // 直译产物里写作 `L["x"] = H.Call(...)`，写成 TrySetOut(a[3], ...) 会越界
            // （实参数组只有 {库, A, B} 三个元素），并且返回值会丢。
            case "Concat_StrStr": return Hit(Val.Of(a[1].AsStr() + a[2].AsStr()));
            case "Split":
                {
                    var parts = a[1].AsStr().Split(a[2].AsStr());
                    return Hit(Val.Ref(new KArr(parts.Select(x => Val.Of(x)))));
                }
            // AppendText(text, out text, out ...)：第一个 out 是「追加后的文本」，
            // 后面若干个 out 是把同一结果再回写一遍（蓝图的多重赋值）。
            case "AppendText":
                {
                    var merged = Val.Of(a[1].AsStr() + a[2].AsStr());
                    for (var i = 3; i < a.Length; i++) Val.TrySetOut(a[i], merged);
                    return Hit(merged);
                }
            case "TextIsEmpty": return Hit(Val.Of(string.IsNullOrEmpty(a[1].AsStr())));
            case "Len": return Hit(Val.Of(a[1].AsStr().Length));
            case "ToLower": return Hit(Val.Of(a[1].AsStr().ToLowerInvariant()));
            case "ToUpper": return Hit(Val.Of(a[1].AsStr().ToUpperInvariant()));
            case "EndsWith": return Hit(Val.Of(a[1].AsStr().EndsWith(a[2].AsStr(), StringComparison.Ordinal)));
            case "StartsWith": return Hit(Val.Of(a[1].AsStr().StartsWith(a[2].AsStr(), StringComparison.Ordinal)));
            case "Contains": return Hit(Val.Of(a[1].AsStr().Contains(a[2].AsStr(), StringComparison.Ordinal)));
            case "ParseIntoArray":
                Val.TrySetOut(a[3], Val.Ref(new KArr(a[1].AsStr().Split(a[2].AsStr()).Where(x => x.Length > 0).Select(x => Val.Of(x)))));
                return Hit(Val.Nothing);
            case "Format": return Hit(a[1]);

            // ---------- 枚举比较（游戏自定义，纯比较无语义） ----------
            case "EnumCompareFaction": return Hit(Val.Of(a[1].AsInt() == a[2].AsInt()));
            case "EnumCompareSide": return Hit(Val.Of(a[1].AsInt() == a[2].AsInt()));
            case "EnumCompareCardLocation": return Hit(Val.Of(a[1].AsInt() == a[2].AsInt()));

            // ---------- 文本比较（StriStri = 大小写不敏感） ----------
            case "EqualEqual_StriStri": return Hit(Val.Of(string.Equals(a[1].AsStr(), a[2].AsStr(), StringComparison.OrdinalIgnoreCase)));
            case "NotEqual_StriStri": return Hit(Val.Of(!string.Equals(a[1].AsStr(), a[2].AsStr(), StringComparison.OrdinalIgnoreCase)));
            case "EqualEqual_TextText": return Hit(Val.Of(Val.Cmp(a[1], a[2]) == 0));
            case "EqualEqual_StrStr": return Hit(Val.Of(string.Equals(a[1].AsStr(), a[2].AsStr(), StringComparison.Ordinal)));

            // ---------- 结构体 ----------
            case "BreakVector": Val.TrySetOut(a[2], Val.Of(a[1].AsInt())); return Hit(Val.Nothing);

            // ---------- 系统 / 日志：无头模拟里全部 no-op ----------
            case "PrintString": case "LogError": case "LogWarning": case "Log":
            case "SetEncryptionKey": case "DirectClientLogger":
                if (f == "LogError") Log($"蓝图 LogError: {(a.Length > 1 ? a[1] : Val.Nothing)}");
                return Hit(Val.Nothing);

            case "GetGameInstanceSubsystem": case "GetEngineSubsystem": case "GetWorldSubsystem":
                return Hit(Val.Ref(new KObj("Subsystem")));
            case "GetTimeSeconds": return Hit(Val.Of(0.0));
            case "GetObjectClass": return Hit(Val.Ref(new KObj("Class")));
            case "IsValidClass": return Hit(Val.True);
        }

        // Notify* / Show* / 纯 UI：无头模拟不需要，统一 no-op 而不是记为未实现。
        if (f.StartsWith("Notify", StringComparison.Ordinal) ||
            f.StartsWith("Show", StringComparison.Ordinal) ||
            f.StartsWith("UpdateNotification", StringComparison.Ordinal) ||
            f.StartsWith("BigNotify", StringComparison.Ordinal) ||
            f.StartsWith("CampaignSetText", StringComparison.Ordinal))
            return Hit(Val.Nothing);

        return Miss;
    }

    private static HashSet<long> SetOf(Val v)
    {
        if (v.O is HashSet<long> s) return s;
        var k = v.O as KObj;
        var inner = k?.Get("__set").O as HashSet<long>;
        if (inner is not null) return inner;
        var made = new HashSet<long>();
        k?.Set("__set", Val.Ref(made));
        return made;
    }

    private static Dictionary<string, Val> MapOf(Val v)
    {
        if (v.O is Dictionary<string, Val> m) return m;
        var k = v.O as KObj;
        var inner = k?.Get("__map").O as Dictionary<string, Val>;
        if (inner is not null) return inner;
        var made = new Dictionary<string, Val>(StringComparer.Ordinal);
        k?.Set("__map", Val.Ref(made));
        return made;
    }

    private static readonly Random Rng = new(12345);
    private static void Shuffle(KArr a)
    {
        if (a is null) return;
        for (var i = a.Count - 1; i > 0; i--)
        {
            var j = Rng.Next(i + 1);
            (a.Items[i], a.Items[j]) = (a.Items[j], a.Items[i]);
        }
    }

    // 兼容 IHost 的 SetOut
    public void SetOut(Val slot, Val v) => Val.TrySetOut(slot, v);
}


