using System.Globalization;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace KardsTranspiler;

/// <summary>
/// Kismet AST → C# 直译器。
///
/// 设计要点（与「反编译成可读文本」的根本区别）：
///
/// 1. **不重建控制流。** Kismet 字节码已经带显式跳转（EX_Jump / EX_JumpIfNot /
///    EX_ComputedJump），因此直译成 <c>label + goto</c> 是 1:1 的，语义天然等价。
///    反编译器所有 bug 都出在「把 goto 图重建成 if/while/for」这一步；直译不碰它。
///
/// 2. **执行流栈保留为运行期真栈。** PushExecutionFlow / PopExecutionFlow 在 UE 里
///    本来就是 <c>UObject::FlowStack</c>，是个运行期栈。反编译器试图在编译期静态求解
///    它（因为要还原成 while/break），这是它在合流点失败的根源。直译用一个真的
///    <c>Stack&lt;int&gt;</c> + 常量目标 switch，零分析、零失败可能。
///
/// 3. **数据装箱。** 逻辑（跳转、比较）是原生 C# 控制流；只有数据是 <c>Val</c>。
///    宿主 API 是唯一懂类型的地方。
///
/// 分发入口（ExecuteUbergraph 的 EntryPoint）来自各事件存根的实参，是编译期常量集合，
/// 因此 ComputedJump 可以直接展开成 <c>switch</c>。
/// </summary>
public sealed class Emitter
{
    private readonly UAsset _asset;
    private readonly string _fnName;
    private readonly UhtParams _uht;
    private readonly BlueprintSignatures _bp;
    private readonly List<(long Off, int Size, KismetExpression E)> _stmts = new();
    private readonly Dictionary<long, int> _idxOf = new();
    private readonly StringBuilder _sb = new();
    private int _ind;
    private readonly List<string> _labels = new();

    /// <summary>无法直译的节点，登记后由报告汇总（不影响其它语句发射）。</summary>
    public readonly List<string> Unsupported = new();
    /// <summary>in/out 无法判定的调用点（UHT 与补充表里都查不到该函数）。</summary>
    public readonly HashSet<string> UnknownArity = new();
    /// <summary>签名已知但实参个数对不上的调用点（重载/默认参数，危害较小）。</summary>
    public readonly HashSet<string> ArityMismatch = new();
    /// <summary>本函数内所有 PushExecutionFlow 的常量目标（= Pop 的可能去向）。</summary>
    public readonly SortedSet<long> FlowTargets = new();

    public Emitter(UAsset asset, string fnName, UhtParams uht = null, BlueprintSignatures bp = null,
                   IReadOnlySet<string> persistentFrameSlots = null)
    {
        _asset = asset;
        _fnName = fnName;
        _uht = uht;
        _bp = bp;
        _persistentFrameSlots = persistentFrameSlots;
    }

    /// <summary>
    /// 本资产里所有「持久帧」槽位名（<c>EX_LetValueOnPersistentFrame</c> 的写入目标）。
    ///
    /// <para>
    /// <b>为什么需要单独一张表</b>：Kismet 的持久帧是一块跨函数共享的存储，
    /// 事件桩用 <c>LetValueOnPersistentFrame</c> 把形参写进去
    /// （发射成 <c>H.SetVar("K2Node_Event_killer", ...)</c>），
    /// 然后 ubergraph 从里面读出来用。
    /// </para>
    ///
    /// <para>
    /// 但同一个名字在 ubergraph 里是通过 <c>EX_LocalVariable</c> 读的，
    /// 如果按普通局部变量发射成 <c>GetLocal(L, "K2Node_Event_killer")</c>，
    /// 就读的是<b>另一个存储</b>（函数的局部字典），永远是空值 ——
    /// 实测 2548 处读取全部读不到，导致 172 个带参事件的效果静默失效。
    /// </para>
    ///
    /// <para>
    /// 所以名字必须由<b>写入侧</b>收集后传给读取侧，不能靠名字猜
    /// （虽然实测 129 个槽位确实都叫 <c>K2Node_Event_*</c>，但那是 UE 的命名习惯，
    /// 不是语言保证）。
    /// </para>
    /// </summary>
    private readonly IReadOnlySet<string> _persistentFrameSlots;

    private bool IsPersistentFrame(string name)
        => _persistentFrameSlots is not null && _persistentFrameSlots.Contains(name);

    private void Line(string s) => _sb.Append(new string(' ', _ind * 4)).Append(s).Append('\n');
    private void Open(string s = null) { if (s != null) Line(s); _sb.Append(new string(' ', _ind * 4)).Append("{\n"); _ind++; }
    private void Close() { _ind--; Line("}"); }

    /// <summary>本函数的 ubergraph 分发入口（仅 ExecuteUbergraph_* 非空）。</summary>
    private IReadOnlyList<(long N, string Caller)> _dispatch = Array.Empty<(long, string)>();

    public string Emit(FunctionExport fn, IReadOnlyList<(long N, string Caller)> dispatchEntries)
    {
        Build(fn);
        _dispatch = dispatchEntries ?? Array.Empty<(long, string)>();

        Line($"public static Val {San(_fnName)}(IHost H, Val self, Val[] args)");
        Open();
        _ind++;
        Line("var L = new Dictionary<string, Val>(StringComparer.Ordinal);");
        Line("var __ef = new Stack<int>();");
        Line("var __ret = Val.Nothing;");
        EmitParamBindings(fn);
        EmitContainerInit(fn);
        _ind--;

        // 逐语句发射：每条语句一个标签，跳转直接 goto。
        for (var i = 0; i < _stmts.Count; i++)
        {
            var (off, _, e) = _stmts[i];
            Line($"L_{off:X4}:");
            _ind++;
            EmitStatement(e, i);
            _ind--;
        }

        // 兜底出口：越界跳转（损坏字节码）落到这里而不是静默跑飞。
        Line("__halt:");
        _ind++;
        EmitOutWriteback();
        Line("return __ret;");
        _ind--;

        if (dispatchEntries is { Count: > 0 })
            EmitDispatch(dispatchEntries);

        Close();
        return _sb.ToString();
    }

    private void Build(FunctionExport fn)
    {
        long pos = 0;
        foreach (var e in fn.ScriptBytecode ?? Array.Empty<KismetExpression>())
        {
            var size = 1;
            try { size = (int)e.GetSize(_asset); } catch { }
            _idxOf[pos] = _stmts.Count;
            _stmts.Add((pos, size, e));
            pos += size;
            if (e is EX_PushExecutionFlow p) FlowTargets.Add(p.PushingAddress);
        }
    }

    /// <summary>本函数的 out 形参名（退出时回写给调用方的实参槽）。</summary>
    private readonly List<string> _outParams = new();

    /// <summary>
    /// 入口参数绑定：Kismet 用 EX_LocalVariable 访问形参，所以形参也是字典项。
    ///
    /// out 形参在调用方是 <c>Val.Out(回调)</c>。这里必须把回调单独存起来（<c>__out_名</c>），
    /// 并用 <c>Val.Nothing</c> 初始化形参槽 —— 否则函数体第一次写形参就把回调覆盖掉了，
    /// 结果永远传不回调用方（典型症状：out 出来的卡牌一直是 none）。
    /// </summary>
    private void EmitParamBindings(FunctionExport fn)
    {
        var props = fn.LoadedProperties;
        if (props is null) return;
        var i = 0;
        foreach (var p in props)
        {
            var flags = p.PropertyFlags;
            if (!flags.HasFlag(EPropertyFlags.CPF_Parm)) continue;
            if (flags.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
            var n = Esc(p.Name.ToString());
            var isOut = flags.HasFlag(EPropertyFlags.CPF_OutParm) && !flags.HasFlag(EPropertyFlags.CPF_ConstParm);
            if (isOut)
            {
                Line($"var __out_{San(n)} = args.Length > {i} ? args[{i}].As<Action<Val>>() : null;");
                Line($"L[\"{n}\"] = Val.Nothing;");
                _outParams.Add(n);
            }
            else
            {
                Line($"L[\"{n}\"] = args.Length > {i} ? args[{i}] : Val.Nothing;");
            }
            i++;
        }
    }

    /// <summary>
    /// 会**就地修改第一个参数**的容器库函数。
    ///
    /// <para>
    /// 为什么要单独盯它们：UE 里 TArray / TSet / TMap 的局部变量是零初始化的（空容器），
    /// 蓝图可以对着一个从没赋过值的局部直接 <c>Array_Add</c>。而直译产物把未写过的局部
    /// 读成 <c>Val.Nothing</c>，宿主对 Nothing 做 Array_Add 是**空操作** —— 追加全丢，
    /// 而且不报错。典型受害者是 <c>selectCardToDraw</c> 里的 <c>spawnCards</c>：
    /// 它收集 Develop 的候选卡名，丢了就导致「候选列表为空、选择永远做不了」。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ContainerMutators = new(StringComparer.Ordinal)
    {
        "Array_Add", "Array_AddUnique", "Array_Append", "Array_Insert", "Array_Remove",
        "Array_RemoveItem", "Array_Clear", "Array_Set", "Array_Resize", "Array_Reverse",
        "Array_ShuffleFromStream",
        "Set_Add", "Set_Clear", "Set_RemoveItems",
        "Map_Add", "Map_Remove", "Map_Clear",
    };

    /// <summary>
    /// 把「被当作容器就地修改目标」的局部变量初始化成空容器。
    ///
    /// <para>
    /// 只排除 in 形参（它们的值来自调用方）；out 形参和普通局部都要给空容器 ——
    /// 与 UE 的零初始化一致。已经赋过值的局部多这一行也无害（随后会被覆盖）。
    /// </para>
    /// </summary>
    private void EmitContainerInit(FunctionExport fn)
    {
        var inParams = new HashSet<string>(StringComparer.Ordinal);
        if (fn.LoadedProperties is not null)
            foreach (var p in fn.LoadedProperties)
            {
                if (!p.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm)) continue;
                if (p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
                var isOut = p.PropertyFlags.HasFlag(EPropertyFlags.CPF_OutParm)
                            && !p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ConstParm);
                if (!isOut) inParams.Add(p.Name.ToString());
            }

        foreach (var n in CollectContainerLocals(fn).OrderBy(x => x, StringComparer.Ordinal))
            if (!inParams.Contains(n))
                Line($"L[\"{Esc(n)}\"] = H.MakeArray(new Val[] {{ }});");
    }

    /// <summary>扫出被容器库函数当作修改目标的局部变量名。</summary>
    private HashSet<string> CollectContainerLocals(FunctionExport fn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in fn.ScriptBytecode ?? Array.Empty<KismetExpression>()) Walk(e);
        return names;

        void Walk(KismetExpression e)
        {
            if (e is null) return;
            string fname = null;
            KismetExpression[] ps = null;
            // 派生类型必须排在基类之前（EX_CallMath / EX_LocalFinalFunction : EX_FinalFunction）
            switch (e)
            {
                case EX_CallMath c: fname = ResolveFn(c.StackNode); ps = c.Parameters; break;
                case EX_LocalFinalFunction f: fname = ResolveFn(f.StackNode); ps = f.Parameters; break;
                case EX_FinalFunction f: fname = ResolveFn(f.StackNode); ps = f.Parameters; break;
                case EX_LocalVirtualFunction v: fname = v.VirtualFunctionName.ToString(); ps = v.Parameters; break;
                case EX_VirtualFunction v: fname = v.VirtualFunctionName.ToString(); ps = v.Parameters; break;
            }
            if (fname is not null && ContainerMutators.Contains(fname) && ps is { Length: > 0 }
                && ps[0] is EX_LocalVariable lv)
            {
                var n = PropPath(lv.Variable);
                // 持久帧槽位是跨函数共享的另一块存储，不走 L 字典
                if (!string.IsNullOrEmpty(n) && n != "?" && !IsPersistentFrame(n)) names.Add(n);
            }

            foreach (var f in e.GetType().GetFields())
            {
                if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
                else if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                    foreach (var x in arr) Walk(x);
            }
        }
    }

    /// <summary>
    /// 退出前把 out 形参写回调用方的实参槽。
    /// 所有路径都汇聚到 <c>__halt</c>，因此这里是唯一的回写点。
    /// </summary>
    private void EmitOutWriteback()
    {
        foreach (var n in _outParams)
            Line($"__out_{San(n)}?.Invoke(L[\"{n}\"]);");
    }

    /// <summary>
    /// 分发器：ComputedJump 的入口是各事件存根传进来的常量，展开成 switch。
    /// 前言的 PushExecutionFlow 已经在前面的语句里正常发射，所以这里只做跳转选择。
    /// </summary>
    private void EmitDispatch(IReadOnlyList<(long N, string Caller)> entries)
    {
        Line("__dispatch:");
        _ind++;
        Line("switch ((int)L[\"EntryPoint\"].AsInt())");
        Open();
        foreach (var (n, caller) in entries.OrderBy(x => x.N))
        {
            Line($"case {n}: // {Esc(caller)}");
            _ind++;
            if (_idxOf.ContainsKey(n)) Line($"goto L_{n:X4};");
            else { Line($"H.Log(\"[dispatch] 入口 {n} 越界\");"); Line("goto __halt;"); }
            _ind--;
        }
        Line("default:");
        _ind++;
        Line("goto __halt;");
        _ind--;
        Close();
        _ind--;
    }

    // ===================== 语句 =====================

    private void EmitStatement(KismetExpression e, int idx)
    {
        switch (e)
        {
            case EX_Jump j:
                Line($"goto L_{j.CodeOffset:X4};");
                return;
            case EX_JumpIfNot j:
                Line($"if (!{Bool(j.BooleanExpression)}) goto L_{j.CodeOffset:X4};");
                return;
            case EX_ComputedJump cj:
                // 非 ubergraph 的 ComputedJump（如 switch-on-string 的跳转表）。
                // 目标集合来自本函数的 PushExecutionFlow 常量；无法静态枚举时显式报错，
                // 绝不静默降级成顺序执行（那正是反编译器的老毛病）。
                EmitComputedJump(cj);
                return;
            case EX_PushExecutionFlow p:
                Line($"__ef.Push({p.PushingAddress});");
                return;
            case EX_PopExecutionFlow:
                EmitPop(null);
                return;
            case EX_PopExecutionFlowIfNot pf:
                EmitPop(pf.BooleanExpression);
                return;
            case EX_Return r:
                if (r.ReturnExpression is not EX_Nothing)
                    Line($"__ret = {Val(r.ReturnExpression)};");
                Line("goto __halt;");
                return;
            case EX_EndOfScript:
                Line("goto __halt;");
                return;

            case EX_Let l: Assign(l.Variable, l.Expression); return;
            case EX_LetObj l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            case EX_LetBool l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            case EX_LetWeakObjPtr l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            case EX_LetValueOnPersistentFrame l:
                Line($"H.SetVar(\"{Esc(PropPath(l.DestinationProperty))}\", {Val(l.AssignmentExpression)});");
                return;

            case EX_Assert a:
                Line($"if (!{Bool(a.AssertExpression)}) H.Log(\"[assert] {Esc(_fnName)}:{a.LineNumber}\");");
                return;

            case EX_SwitchValue sw:
                EmitSwitchValue(sw);
                return;

            case EX_Breakpoint:
            case EX_Tracepoint:
            case EX_WireTracepoint:
            case EX_InstrumentationEvent:
            case EX_DeprecatedOp4A:
            case EX_EndFunctionParms:
            case EX_EndStructConst:
            case EX_EndArrayConst:
            case EX_EndSetConst:
            case EX_EndMapConst:
            case EX_EndArray:
            case EX_EndSet:
            case EX_EndMap:
            case EX_EndParmValue:
                return;   // 解析期的结构标记，运行时无动作

            // SetArray / SetSet / SetMap 的「属性」是赋值目标，不是读。
            // 必须走 Assign：局部变量读出来是 GetLocal(L,"x")，那是右值，写在等号左边会 CS0131。
            case EX_SetArray sa:
                Assign(sa.AssigningProperty, new SyntheticArray(sa.Elements));
                return;
            case EX_SetSet ss:
                Assign(ss.SetProperty, new SyntheticArray(ss.Elements));
                return;
            case EX_SetMap sm:
                Assign(sm.MapProperty, new SyntheticArray(sm.Elements));
                return;

            case EX_AddMulticastDelegate d:
                Line($"H.Call(\"__delegateAdd\", new Val[] {{ {Val(d.Delegate)}, {Val(d.DelegateToAdd)} }});");
                return;
            case EX_RemoveMulticastDelegate d:
                Line($"H.Call(\"__delegateRemove\", new Val[] {{ {Val(d.Delegate)}, {Val(d.DelegateToAdd)} }});");
                return;
            case EX_ClearMulticastDelegate d:
                Line($"H.Call(\"__delegateClear\", new Val[] {{ {Val(d.DelegateToClear)} }});");
                return;
            case EX_BindDelegate b:
                Line($"H.Call(\"__delegateBind\", new Val[] {{ {Val(b.Delegate)}, {Val(b.ObjectTerm)}, Val.Name(\"{Esc(b.FunctionName.ToString())}\") }});");
                return;
            case EX_CallMulticastDelegate c:
                Line("H.Call(\"__delegateCall\", new Val[] { " + Val(c.Delegate) + " });");
                return;

            // 有值但无副作用的表达式语句：求值即可（保持求值顺序）
            default:
                if (HasValue(e)) { Line($"_ = {Val(e)};"); return; }
                Unsupported.Add($"{_fnName}: {e.GetType().Name} 无语句语义");
                Line($"H.Log(\"[transpiler] 未处理的语句 {e.GetType().Name} @ {_stmts[idx].Off:X4}\");");
                return;
        }
    }

    /// <summary>
    /// ComputedJump：目标是运行期值。可达目标集合 = 本函数所有 Push 常量 ∪ 分发入口。
    ///
    /// 注意必须并上分发入口：ubergraph 的 ComputedJump 按 EntryPoint 跳转，而 EntryPoint
    /// 的各 case 入口是事件存根传进来的常量，从来不是任何 Push 的目标。只枚举 Push 常量会
    /// 让所有 case 落到 default 被丢弃 —— 这正是「卡牌效果整段消失」的同一类错误。
    /// </summary>
    private void EmitComputedJump(EX_ComputedJump cj)
    {
        var targets = FlowTargets.Where(t => _idxOf.ContainsKey(t)).ToHashSet();
        foreach (var (n, _) in _dispatch) if (_idxOf.ContainsKey(n)) targets.Add(n);

        if (targets.Count == 0)
        {
            Unsupported.Add($"{_fnName}: ComputedJump 无可枚举目标");
            Line("H.Log(\"[transpiler] ComputedJump 目标集为空\");");
            Line("goto __halt;");
            return;
        }
        Line($"switch ((int)({Val(cj.CodeOffsetExpression)}).AsInt())");
        Open();
        foreach (var t in targets.OrderBy(x => x)) Line($"case {t}: goto L_{t:X4};");
        Line("default: goto __halt;");
        Close();
    }

    /// <summary>
    /// PopExecutionFlow[IfNot]：弹出运行期栈顶并跳过去。若栈空则记诊断而不是静默继续
    /// —— 栈空说明字节码或转译有问题，静默继续会产生看似正常实则错误的结果。
    /// </summary>
    private void EmitPop(KismetExpression cond)
    {
        var targets = FlowTargets.Where(t => _idxOf.ContainsKey(t)).ToList();
        var body = new Action(() =>
        {
            Line("if (__ef.Count == 0) { H.Log(\"[transpiler] 执行流栈下溢\"); goto __halt; }");
            Line("switch (__ef.Pop())");
            Open();
            foreach (var t in targets) Line($"case {t}: goto L_{t:X4};");
            Line("default: goto __halt;");
            Close();
        });

        if (cond is null) { body(); return; }

        Line($"if (!{Bool(cond)})");
        Open();
        body();
        Close();
    }

    /// <summary>
    /// EX_SwitchValue：按索引选分支。每个 CaseTerm 结束都会跳到 EndGotoOffset（switch 之后），
    /// 因此展开成 if/else 链与原本的跳转表等价。
    /// </summary>
    private void EmitSwitchValue(EX_SwitchValue sw)
    {
        var cases = sw.Cases ?? Array.Empty<FKismetSwitchCase>();
        Line($"var __sw = {Val(sw.IndexTerm)};");
        var first = true;
        foreach (var c in cases)
        {
            var idxExpr = Val(c.CaseIndexValueTerm);
            Line($"{(first ? "if" : "else if")} (Val.Cmp(__sw, {idxExpr}) == 0)");
            Open();
            EmitInline(c.CaseTerm);
            Close();
            first = false;
        }
        Line("else");
        Open();
        EmitInline(sw.DefaultTerm);
        Close();
    }

    /// <summary>在表达式位置执行一个语句型节点（switch case 体 / default 体）。</summary>
    private void EmitInline(KismetExpression e)
    {
        if (e is null || e is EX_Nothing) return;
        switch (e)
        {
            case EX_Let l: Assign(l.Variable, l.Expression); return;
            case EX_LetObj l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            case EX_LetBool l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            case EX_LetWeakObjPtr l: Assign(l.VariableExpression, l.AssignmentExpression); return;
            default:
                Line($"_ = {Val(e)};");
                return;
        }
    }

    // ===================== 赋值 =====================

    /// <summary>
    /// 把 SetArray/SetSet/SetMap 的「元素列表」包装成一个合成表达式，
    /// 好让它能作为 <see cref="Assign"/> 的右值被统一处理。
    /// Kismet 里这三个节点的属性确实是赋值目标（等价于 EX_Let 的左侧）。
    /// </summary>
    private sealed class SyntheticArray : KismetExpression
    {
        public readonly KismetExpression[] Elements;
        public SyntheticArray(KismetExpression[] e) { Elements = e; }
    }

    private void Assign(KismetExpression target, KismetExpression value)
    {
        var v = Val(value);
        switch (target)
        {
            case EX_LocalVariable lv:
            {
                // 与读取侧对称：持久帧槽位写 H.SetVar，普通局部写 L[...]。
                // 两边必须一致，否则写进去的值读不到（这正是事件参数恒为空的原因）。
                var n = PropPath(lv.Variable);
                Line(IsPersistentFrame(n)
                    ? $"H.SetVar(\"{Esc(n)}\", {v});"
                    : $"L[\"{Esc(n)}\"] = {v};");
                return;
            }
            // out 形参也是普通命名槽：Kismet 对它的写与对局部变量的写没有区别。
            // 回传给实参由 Emit 结尾的统一回写完成（见 EmitCallerOutWriteback）。
            case EX_LocalOutVariable lv:
                Line($"L[\"{Esc(PropPath(lv.Variable))}\"] = {v};");
                return;
            case EX_InstanceVariable iv:
                Line($"H.SetMember(self, \"{Esc(PropPath(iv.Variable))}\", {v});");
                return;
            case EX_Context ctx:
                Line($"H.SetMember({Val(ctx.ObjectExpression)}, \"{Esc(MemberName(ctx.ContextExpression))}\", {v});");
                return;
            case EX_StructMemberContext smc:
                Line($"H.SetMember({Val(smc.StructExpression)}, \"{Esc(PropPath(smc.StructMemberExpression))}\", {v});");
                return;
            case EX_ArrayGetByRef arr:
                Line($"H.ArraySet({Val(arr.ArrayVariable)}, (int)({Val(arr.ArrayIndex)}).AsInt(), {v});");
                return;
            default:
                Unsupported.Add($"{_fnName}: 赋值目标 {target?.GetType().Name} 不支持");
                Line($"H.Log(\"[transpiler] 不支持的赋值目标 {target?.GetType().Name}\");");
                return;
        }
    }

    // ===================== 表达式 =====================

    /// <summary>把表达式渲染成 C# 表达式（返回 Val 或 bool）。</summary>
    public string Val(KismetExpression e)
    {
        switch (e)
        {
            case null: return "Val.Nothing";
            case SyntheticArray sa: return $"H.MakeArray(new Val[] {{ {Join(sa.Elements)} }})";
            case EX_Nothing or EX_NoObject or EX_NoInterface: return "Val.Nothing";
            case EX_True: return "Val.True";
            case EX_False: return "Val.False";
            case EX_IntZero: return "Val.Zero";
            case EX_IntOne: return "Val.One";
            case EX_IntConst c: return $"Val.Of({c.Value})";
            case EX_Int64Const c: return $"Val.Of({c.Value}L)";
            case EX_UInt64Const c: return $"Val.Of({c.Value}L)";
            case EX_ByteConst c: return $"Val.Of({c.Value})";
            case EX_IntConstByte c: return $"Val.Of({c.Value})";
            case EX_FloatConst c: return $"Val.Of({Lit(c.Value)}f)";
            case EX_DoubleConst c: return $"Val.Of({Lit(c.Value)})";
            case EX_StringConst c: return $"Val.Of({Str(c.Value)})";
            case EX_UnicodeStringConst c: return $"Val.Of({Str(c.Value)})";
            case EX_NameConst c: return $"Val.Name({Str(c.Value.ToString())})";
            case EX_TextConst c: return $"Val.Of({Str(TextOf(c))})";
            case EX_SoftObjectConst c: return $"Val.Ref({Str(SoftObjectName(c))})";
            case EX_ObjectConst c: return $"Val.Ref({Str(ObjectName(c.Value))})";
            case EX_Self: return "self";
            case EX_SkipOffsetConst c: return "Val.Nothing";

            // 局部变量必须走 GetLocal：Kismet 的局部槽是零初始化的，
            // 但直译出来的字典对「从未写过的名字」会抛 KeyNotFound。
            // 循环体第一次进来读 Array_Get 的 out 槽、或读一个未被赋值的临时量，
            // 都会撞上这个（表现是整张卡抛异常而不是算出结果）。
            //
            // 例外：持久帧槽位（事件桩用 LetValueOnPersistentFrame 写的那些）
            // 是跨函数共享的另一块存储，必须读 H.GetVar 才拿得到。
            // 读错存储不会报错，只会恒为空 —— 详见 _persistentFrameSlots 的注释。
            case EX_LocalVariable v:
            {
                var n = PropPath(v.Variable);
                return IsPersistentFrame(n)
                    ? $"H.GetVar(\"{Esc(n)}\")"
                    : $"GetLocal(L, \"{Esc(n)}\")";
            }
            case EX_LocalOutVariable v: return $"GetLocal(L, \"{Esc(PropPath(v.Variable))}\")";
            case EX_InstanceVariable v: return $"H.GetMember(self, \"{Esc(PropPath(v.Variable))}\")";
            case EX_DefaultVariable v: return $"H.GetMember(self, \"{Esc(PropPath(v.Variable))}\")";
            case EX_ClassSparseDataVariable v: return $"H.GetMember(self, \"{Esc(PropPath(v.Variable))}\")";

            // 派生类型必须排在基类之前（EX_ClassContext / EX_Context_FailSilent : EX_Context）
            case EX_ClassContext ctx: return ContextRead(ctx);
            case EX_Context_FailSilent ctx: return ContextRead(ctx);
            case EX_Context ctx: return ContextRead(ctx);

            case EX_StructMemberContext smc:
                return $"H.GetMember({Val(smc.StructExpression)}, \"{Esc(PropPath(smc.StructMemberExpression))}\")";

            case EX_ArrayGetByRef a:
                return $"H.ArrayGet({Val(a.ArrayVariable)}, (int)({Val(a.ArrayIndex)}).AsInt())";

            // 同上：派生类型必须排在基类之前
            // EX_CallMath / EX_LocalFinalFunction : EX_FinalFunction
            // EX_LocalVirtualFunction : EX_VirtualFunction
            //
            // Local* 前缀的含义是「在本对象上调用」——接收者必须是 self，不能交给
            // DefaultReceiver 去猜。事件存根转进 ubergraph 用的就是 EX_LocalFinalFunction，
            // 发成 Nothing 的话，宿主那侧按「谁的方法」分派就永远找不到目标函数，
            // 表现是整张卡的效果全部计为「未实现」。
            case EX_CallMath f: return Call(null, f.StackNode, f.Parameters);
            case EX_LocalFinalFunction f: return Call("self", f.StackNode, f.Parameters);
            case EX_FinalFunction f: return Call(null, f.StackNode, f.Parameters);
            case EX_LocalVirtualFunction f: return Call("self", null, f.Parameters, f.VirtualFunctionName.ToString());
            case EX_VirtualFunction f: return Call("self", null, f.Parameters, f.VirtualFunctionName.ToString());

            case EX_ArrayConst ac: return $"H.MakeArray(new Val[] {{ {Join(ac.Elements)} }})";
            case EX_SetConst sc: return $"H.MakeArray(new Val[] {{ {Join(sc.Elements)} }})";
            case EX_MapConst mc: return $"H.MakeArray(new Val[] {{ {Join(mc.Elements)} }})";
            case EX_StructConst sc: return $"H.MakeArray(new Val[] {{ {Join(sc.Value as KismetExpression[])} }})";

            case EX_PrimitiveCast pc: return Val(pc.Target);
            case EX_DynamicCast dc: return Val(dc.Target);
            case EX_MetaCast mc: return Val(mc.Target);
            case EX_ObjToInterfaceCast ic: return Val(ic.Target);
            case EX_CrossInterfaceCast ic: return Val(ic.Target);
            case EX_InterfaceToObjCast ic: return Val(ic.Target);
            case EX_InterfaceContext ic: return Val(ic.InterfaceValue);

            case EX_PropertyConst p: return $"H.GetVar(\"{Esc(PropPath(p.Property))}\")";
            case EX_BitFieldConst bf: return $"H.GetVar(\"{Esc(PropPath(bf.Property))}\")";

            case EX_VectorConst vc: return $"Val.Ref(new Vec3({Lit(vc.Value.X)}, {Lit(vc.Value.Y)}, {Lit(vc.Value.Z)}))";
            case EX_RotationConst rc: return $"Val.Ref(new Vec3({Lit(rc.Value.Pitch)}, {Lit(rc.Value.Yaw)}, {Lit(rc.Value.Roll)}))";
            case EX_Vector3fConst v3: return $"Val.Ref(new Vec3({Lit(v3.X)}, {Lit(v3.Y)}, {Lit(v3.Z)}))";
            // FTransform 目前只承载平移（蓝图侧拿它做位置摆放，无头模拟不需要旋转/缩放）。
            case EX_TransformConst tc:
                {
                    var p = tc.Value.Translation;
                    return $"Val.Ref(new Vec3({Lit(p.X)}, {Lit(p.Y)}, {Lit(p.Z)}))";
                }

            case EX_SwitchValue sw: return SwitchExpr(sw);
            case EX_Skip s: return Val(s.SkipExpression);

            // 表达式位置的控制流：C# 不允许在表达式里 goto，无法机械翻译。
            // 这些形态在 Kismet 里极少（多出现在短路的极端编排），显式登记为不支持，
            // 绝不生成编译不过的代码，也绝不静默当成无操作。
            case EX_Jump or EX_JumpIfNot or EX_PushExecutionFlow or EX_PopExecutionFlow
                 or EX_PopExecutionFlowIfNot or EX_ComputedJump:
                Unsupported.Add($"{_fnName}: 表达式位置的控制流 {e.GetType().Name}");
                return $"__ctl(\"{Esc(e.GetType().Name)}\")";

            default:
                Unsupported.Add($"{_fnName}: 表达式 {e.GetType().Name} 不支持");
                return $"__unsupported(\"{Esc(e.GetType().Name)}\")";
        }
    }

    private string SwitchExpr(EX_SwitchValue sw)
    {
        var parts = new List<string>();
        foreach (var c in sw.Cases ?? Array.Empty<FKismetSwitchCase>())
            parts.Add($"ValueTuple.Create({Val(c.CaseIndexValueTerm)}, {Val(c.CaseTerm)})");
        return $"Val.Switch({Val(sw.IndexTerm)}, new[] {{ {string.Join(", ", parts)} }}, {Val(sw.DefaultTerm)})";
    }

    /// <summary>
    /// 成员调用统一约定：args[0] 是接收者，其余为实参。
    /// out 实参包成 <c>Val.Out(回调)</c>，被调函数退出时回调写回本地槽。
    ///
    /// 接收者的判定（很重要）：
    /// <list type="bullet">
    /// <item>调用点显式给了 context（EX_Context）→ 用那个对象。</item>
    /// <item>没给 context 时看被调函数的<b>声明类</b>：import 链第二层就是它。</item>
    /// </list>
    /// 声明类决定一切：
    /// <list type="bullet">
    /// <item><c>BaseCardObject</c>（/Script/kards）→ 成员函数，接收者是 <c>self</c>。
    ///   发成 Val.Nothing 会让 <c>IsLocatedOnBoard</c> 这类判断永远为假，
    ///   整段效果被静默跳过（卡牌「什么也没发生」的根因）。</item>
    /// <item><c>Kismet*Library</c>（/Script/Engine）→ 静态库，接收者是库引用。</item>
    /// </list>
    /// </summary>
    private string Call(string receiverExpr, FPackageIndex idx, KismetExpression[] ps, string byName = null)
    {
        var name = byName ?? ResolveFn(idx);
        var op = TryOperator(name, ps);
        if (op != null) return op;

        var args = new List<string>();
        args.Add(receiverExpr ?? DefaultReceiver(idx));
        var mods = ParamMods(idx, ps?.Length ?? 0, byName);
        for (var i = 0; i < (ps?.Length ?? 0); i++)
        {
            var isOut = mods is not null && i < mods.Length && mods[i] == "out";
            args.Add(isOut ? OutArg(ps[i]) : Val(ps[i]));
        }
        return $"H.Call({Str(name)}, new Val[] {{ {string.Join(", ", args)} }})";
    }

    /// <summary>
    /// 无 context 调用时的接收者。按 import 链的声明类判定，见 <see cref="Call"/>。
    /// 判定不出来时退回 <c>Val.Nothing</c>（静态调用），这是安全的默认值：
    /// 宿主对静态函数不看接收者。
    /// </summary>
    private string DefaultReceiver(FPackageIndex idx)
    {
        var cls = DeclaringClass(idx);
        if (cls is null) return "Val.Nothing";
        if (IsLibraryClass(cls)) return $"Val.Ref(\"{cls}\")";
        // /Script/kards 下的非库类 → 成员函数，接收者是 self
        if (cls is "BaseCardObject" or "BP_CardFunctions_C" or "U_CardFunctionsNotifier_C") return "self";
        return "self";
    }

    /// <summary>import 链第二层 = 声明类（Function → Class → Package）。</summary>
    private string DeclaringClass(FPackageIndex idx)
    {
        if (idx is null || idx.Index >= 0 || -idx.Index - 1 >= _asset.Imports.Count) return null;
        var outer = _asset.Imports[-idx.Index - 1].OuterIndex;
        if (outer is null) return null;
        if (outer.Index < 0 && -outer.Index - 1 < _asset.Imports.Count)
            return _asset.Imports[-outer.Index - 1].ObjectName.ToString();
        if (outer.Index > 0 && outer.Index - 1 < _asset.Exports.Count)
            return _asset.Exports[outer.Index - 1].ObjectName.ToString();
        return null;
    }

    /// <summary>
    /// UE 静态蓝图库。这些类的函数没有 self，接收者是库对象本身。
    ///
    /// 用显式集合而不是前缀匹配：<c>BlueprintSetLibrary</c> / <c>BlueprintMapLibrary</c>
    /// 不以 Kismet 开头，而 <c>KismetMathLibrary</c> 等以 Kismet 开头；
    /// 只按前缀判会漏掉前者，那些调用就会错把 self 当接收者。
    /// </summary>
    private static readonly HashSet<string> LibraryClasses = new(StringComparer.Ordinal)
    {
        "KismetArrayLibrary", "KismetMathLibrary", "KismetStringLibrary",
        "KismetTextLibrary", "KismetSystemLibrary", "KismetInputLibrary",
        "KismetGameplayTagLibrary", "KismetGuidLibrary", "KismetNodeHelperLibrary",
        "BlueprintSetLibrary", "BlueprintMapLibrary", "BlueprintJsonLibrary",
        "BlueprintPathsLibrary", "GameplayStatics", "BlueprintPlatformLibrary",
        "WidgetLayoutLibrary", "WidgetBlueprintLibrary", "DataTableFunctionLibrary",
        "GameplayTagContainer", "SlateBlueprintLibrary", "RenderingLibrary",
    };

    private static bool IsLibraryClass(string cls) => cls is not null && LibraryClasses.Contains(cls);

    /// <summary>
    /// out 实参：包成回调，被调函数用 <c>TrySetOut</c> 触发写回。
    /// Kismet 的 out 实参必然是可写位置（局部变量 / 成员 / 数组元素），这里按位置生成写入。
    /// </summary>
    private string OutArg(KismetExpression e) => e switch
    {
        EX_LocalVariable v => $"Val.Out(__v => L[\"{Esc(PropPath(v.Variable))}\"] = __v)",
        EX_LocalOutVariable v => $"Val.Out(__v => L[\"{Esc(PropPath(v.Variable))}\"] = __v)",
        EX_InstanceVariable v => $"Val.Out(__v => H.SetMember(self, \"{Esc(PropPath(v.Variable))}\", __v))",
        EX_Context c => $"Val.Out(__v => H.SetMember({Val(c.ObjectExpression)}, \"{Esc(MemberName(c.ContextExpression))}\", __v))",
        EX_ArrayGetByRef a => $"Val.Out(__v => H.ArraySet({Val(a.ArrayVariable)}, (int)({Val(a.ArrayIndex)}).AsInt(), __v))",
        EX_StructMemberContext s => $"Val.Out(__v => H.SetMember({Val(s.StructExpression)}, \"{Esc(PropPath(s.StructMemberExpression))}\", __v))",
        _ => "Val.Nothing",
    };

    /// <summary>out 实参的回写目标。Kismet 要求 out 实参是可写位置。</summary>
    private string ContextRead(EX_Context ctx)
    {
        var obj = Val(ctx.ObjectExpression);
        switch (ctx.ContextExpression)
        {
            // 派生在前（EX_CallMath / EX_LocalFinalFunction : EX_FinalFunction；
            //           EX_LocalVirtualFunction : EX_VirtualFunction）
            case EX_CallMath f: return Call(obj, f.StackNode, f.Parameters);
            case EX_LocalFinalFunction f: return Call(obj, f.StackNode, f.Parameters);
            case EX_FinalFunction f: return Call(obj, f.StackNode, f.Parameters);
            case EX_LocalVirtualFunction f: return Call(obj, null, f.Parameters, f.VirtualFunctionName.ToString());
            case EX_VirtualFunction f: return Call(obj, null, f.Parameters, f.VirtualFunctionName.ToString());
            default: return $"H.GetMember({obj}, \"{Esc(MemberName(ctx.ContextExpression))}\")";
        }
    }

    // ===================== 运算符 =====================

    /// <summary>
    /// Kismet 的比较/逻辑函数返回的是<b>值</b>（可被赋值），而 C# 的 <c>==</c> 返回 bool。
    /// 因此这里发射成显式的 Val 构造，保持「可赋值」的性质。
    /// </summary>
    private static readonly Dictionary<string, string> ValueCmp = new(StringComparer.Ordinal)
    {
        ["EqualEqual_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_ByteByte"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_BoolBool"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_StrStr"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_NameName"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_ObjectObject"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_ClassClass"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["EqualEqual_InterfaceInterface"] = "Val.Of(Val.Cmp({0}, {1}) == 0)",
        ["NotEqual_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_ByteByte"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_BoolBool"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_StrStr"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_NameName"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_ObjectObject"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_ClassClass"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["NotEqual_InterfaceInterface"] = "Val.Of(Val.Cmp({0}, {1}) != 0)",
        ["Greater_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) > 0)",
        ["Greater_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) > 0)",
        ["Greater_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) > 0)",
        ["Less_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) < 0)",
        ["Less_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) < 0)",
        ["Less_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) < 0)",
        ["GreaterEqual_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) >= 0)",
        ["GreaterEqual_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) >= 0)",
        ["GreaterEqual_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) >= 0)",
        ["LessEqual_IntInt"] = "Val.Of(Val.Cmp({0}, {1}) <= 0)",
        ["LessEqual_FloatFloat"] = "Val.Of(Val.Cmp({0}, {1}) <= 0)",
        ["LessEqual_DoubleDouble"] = "Val.Of(Val.Cmp({0}, {1}) <= 0)",
        ["BooleanAND"] = "Val.Of(({0}).AsBool() && ({1}).AsBool())",
        ["BooleanOR"] = "Val.Of(({0}).AsBool() || ({1}).AsBool())",
        ["Not_PreBool"] = "Val.Of(!({0}).AsBool())",
    };

    /// <summary>纯算术运算符（Kismet 语义 = Val 上的运算，结果仍是 Val）。</summary>
    private static readonly Dictionary<string, (string Op, int Arity)> Ops = new(StringComparer.Ordinal)
    {
        ["Add_IntInt"] = ("+", 2), ["Add_FloatFloat"] = ("+", 2), ["Add_DoubleDouble"] = ("+", 2),
        ["Subtract_IntInt"] = ("-", 2), ["Subtract_FloatFloat"] = ("-", 2), ["Subtract_DoubleDouble"] = ("-", 2),
        ["Multiply_IntInt"] = ("*", 2), ["Multiply_FloatFloat"] = ("*", 2), ["Multiply_DoubleDouble"] = ("*", 2),
        ["Divide_IntInt"] = ("/", 2), ["Divide_FloatFloat"] = ("/", 2), ["Divide_DoubleDouble"] = ("/", 2),
        ["Percent_IntInt"] = ("%", 2),
        ["BitwiseAND_IntInt"] = ("&", 2), ["BitwiseOR_IntInt"] = ("|", 2), ["BitwiseXOR_IntInt"] = ("^", 2),
        ["ShiftLeft_IntInt"] = ("<<", 2), ["ShiftRight_IntInt"] = (">>", 2),
    };

    /// <summary>
    /// 返回 bool 的 C# 表达式，供 if / goto 条件使用。
    /// 命中比较/逻辑函数时直接发射 C# 布尔表达式（省掉一次装箱），否则退回 AsBool()。
    /// </summary>
    private string Bool(KismetExpression e)
    {
        var (idx, ps) = e switch
        {
            // 派生类型必须排在基类之前
            EX_CallMath c => (c.StackNode, c.Parameters),
            EX_LocalFinalFunction f => (f.StackNode, f.Parameters),
            EX_FinalFunction f => (f.StackNode, f.Parameters),
            _ => (null, null),
        };
        if (idx is not null && ps is not null)
        {
            var name = ResolveFn(idx);
            var s = BoolFromOp(name, ps);
            if (s is not null) return s;
        }
        return $"({Val(e)}).AsBool()";
    }

    /// <summary>把比较/逻辑函数直接发射成 C# 布尔表达式；不是这类函数时返回 null。</summary>
    private string BoolFromOp(string name, KismetExpression[] ps)
    {
        if (BoolCmp.TryGetValue(name, out var cmpOp) && ps.Length == 2)
            return $"Val.Cmp({Val(ps[0])}, {Val(ps[1])}) {cmpOp} 0";
        if (BoolArith.TryGetValue(name, out var ar) && ps.Length == 2)
            return $"({Val(ps[0])} {ar} {Val(ps[1])}).AsBool()";
        switch (name)
        {
            case "BooleanAND" when ps.Length == 2: return $"({Val(ps[0])}).AsBool() && ({Val(ps[1])}).AsBool()";
            case "BooleanOR" when ps.Length == 2: return $"({Val(ps[0])}).AsBool() || ({Val(ps[1])}).AsBool()";
            case "Not_PreBool" when ps.Length == 1: return $"!({Val(ps[0])}).AsBool()";
        }
        return null;
    }

    private static readonly Dictionary<string, string> BoolCmp = new(StringComparer.Ordinal)
    {
        ["EqualEqual_IntInt"] = "==", ["EqualEqual_ByteByte"] = "==",
        ["EqualEqual_BoolBool"] = "==", ["EqualEqual_StrStr"] = "==",
        ["EqualEqual_NameName"] = "==", ["EqualEqual_ObjectObject"] = "==",
        ["EqualEqual_ClassClass"] = "==", ["EqualEqual_InterfaceInterface"] = "==",
        ["EqualEqual_FloatFloat"] = "==", ["EqualEqual_DoubleDouble"] = "==",
        ["NotEqual_IntInt"] = "!=", ["NotEqual_ByteByte"] = "!=",
        ["NotEqual_BoolBool"] = "!=", ["NotEqual_StrStr"] = "!=",
        ["NotEqual_NameName"] = "!=", ["NotEqual_ObjectObject"] = "!=",
        ["NotEqual_ClassClass"] = "!=", ["NotEqual_InterfaceInterface"] = "!=",
        ["NotEqual_FloatFloat"] = "!=", ["NotEqual_DoubleDouble"] = "!=",
        ["Greater_IntInt"] = ">", ["Greater_FloatFloat"] = ">", ["Greater_DoubleDouble"] = ">",
        ["Less_IntInt"] = "<", ["Less_FloatFloat"] = "<", ["Less_DoubleDouble"] = "<",
        ["GreaterEqual_IntInt"] = ">=", ["GreaterEqual_FloatFloat"] = ">=", ["GreaterEqual_DoubleDouble"] = ">=",
        ["LessEqual_IntInt"] = "<=", ["LessEqual_FloatFloat"] = "<=", ["LessEqual_DoubleDouble"] = "<=",
    };

    private static readonly Dictionary<string, string> BoolArith = new(StringComparer.Ordinal);

    private string TryOperator(string name, KismetExpression[] ps)
    {
        if (ps is null) return null;

        // 语义为「返回 Val」的比较/逻辑
        if (ValueCmp.TryGetValue(name, out var fmt) && ps.Length == 2)
            return string.Format(fmt, Val(ps[0]), Val(ps[1]));

        // 纯算术
        if (Ops.TryGetValue(name, out var op) && op.Arity == ps.Length)
        {
            if (op.Arity == 1) return $"({op.Op}{Val(ps[0])})";
            return $"({Val(ps[0])} {op.Op} {Val(ps[1])})";
        }
        return null;
    }

    // ===================== 解析辅助 =====================

    private string ResolveFn(FPackageIndex idx)
    {
        var n = ObjectName(idx);
        return string.IsNullOrEmpty(n) ? $"fn{idx?.Index ?? 0}" : n;
    }

    private string ObjectName(FPackageIndex idx)
    {
        if (idx is null) return null;
        if (idx.Index > 0 && idx.Index - 1 < _asset.Exports.Count)
            return StripCdo(_asset.Exports[idx.Index - 1].ObjectName.ToString());
        if (idx.Index < 0 && -idx.Index - 1 < _asset.Imports.Count)
            return StripCdo(_asset.Imports[-idx.Index - 1].ObjectName.ToString());
        return null;
    }

    /// <summary>EX_SoftObjectConst 的值是 FSoftObjectPath，取资产名即可。</summary>
    private string SoftObjectName(EX_SoftObjectConst c)
    {
        try
        {
            var v = c.Value;
            if (v is null) return "";
            var s = v.ToString();
            var dot = s.LastIndexOf('.');
            return dot >= 0 ? s[(dot + 1)..] : s;
        }
        catch { return ""; }
    }

    private static string StripCdo(string n)
    {
        if (n is null) return null;
        var slash = n.LastIndexOf('/'); if (slash >= 0) n = n[(slash + 1)..];
        var dot = n.LastIndexOf('.'); if (dot >= 0) n = n[(dot + 1)..];
        if (n.StartsWith("Default__", StringComparison.Ordinal)) n = n["Default__".Length..];
        if (n.EndsWith("_C", StringComparison.Ordinal) && n.Length > 2) n = n[..^2];
        return n;
    }

    /// <summary>KismetPropertyPointer → 属性路径（New.Path 优先，回退 Old 导出索引）。</summary>
    private string PropPath(KismetPropertyPointer kp)
    {
        if (kp is null) return "?";
        if (kp.New?.Path is { Length: > 0 } path)
            return string.Join(".", path.Select(p => p.ToString()));
        if (kp.Old.Index > 0 && kp.Old.Index - 1 < _asset.Exports.Count)
            return _asset.Exports[kp.Old.Index - 1].ObjectName.ToString();
        return "?";
    }

    private string MemberName(KismetExpression e) => e switch
    {
        EX_LocalVariable v => PropPath(v.Variable),
        EX_InstanceVariable v => PropPath(v.Variable),
        EX_PropertyConst p => PropPath(p.Property),
        _ => "?",
    };

    /// <summary>
    /// 判定被调函数的参数修饰（out / in）。
    ///
    /// 三条来源，按可靠性排序：
    /// <list type="number">
    /// <item>本资产导出的函数：<c>LoadedProperties</c> 里有 CPF_OutParm，是原生标志位，最可靠。</item>
    /// <item>导入的函数（cardFunction.* / BaseCardObject / Kismet*Library）：蓝图侧没有任何标志位，
    ///       只能查 UHT 头文件里的 <c>T&amp;</c>。</item>
    /// <item>都查不到：返回 null，调用点按 in 处理并登记到 UnknownArity。</item>
    /// </list>
    /// 第 2 条是必需的：<c>Array_Get(Array, Index, out Item)</c> 这类取值调用若少了 out，
    /// 结果永远传不回来，效果「跑了但什么都没发生」。
    /// </summary>
    private string[] ParamMods(FPackageIndex idx, int n, string byName = null)
    {
        if (n == 0) return null;

        // 1. 本资产导出：原生标志位
        //
        // byName 这条路径很关键：EX_LocalVirtualFunction / EX_VirtualFunction 只带函数名、
        // 不带 FPackageIndex（idx 为 null）。若因为 idx==null 就跳过，卡牌之间互调的辅助函数
        // （ApplyTheBuff 这类）全部判不出 out，结果一样传不回来。
        FunctionExport fe = null;
        if (idx is not null && idx.Index > 0 && idx.Index - 1 < _asset.Exports.Count)
            fe = _asset.Exports[idx.Index - 1] as FunctionExport;
        else if (byName is not null)
            fe = _asset.Exports.OfType<FunctionExport>()
                       .FirstOrDefault(x => x.ObjectName.ToString() == byName);

        if (fe?.LoadedProperties is { Length: > 0 })
        {
            var mods = new List<string>();
            foreach (var p in fe.LoadedProperties)
            {
                if (!p.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm)) continue;
                if (p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
                mods.Add(p.PropertyFlags.HasFlag(EPropertyFlags.CPF_OutParm)
                         && !p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ConstParm) ? "out" : "");
            }
            if (mods.Count == n) return mods.ToArray();
        }

        // 2a. 蓝图资产自身：cardFunction.* 是 BP_CardFunctions 里的函数，
        //     CardFunctionsStub.h 只声明了一部分，这里带着原生 CPF_OutParm，最权威。
        var name = byName ?? ResolveFn(idx);
        if (_bp is not null)
        {
            var b = _bp.Mods(name, n);
            if (b is not null) return b;
        }

        // 2b. 导入：查 UHT 头文件
        if (_uht is not null)
        {
            var m = _uht.Mods(name, n);
            if (m is not null) return m;
        }

        // 2c. 调用点证据推导表：以上都查不到时，用「out 实参固定命名为
        //     CallFunc_<函数名>_<形参名>」这条规律，从转译产物里反推出来的签名。
        //     见 NativeOutParams 的文档；该表由脚本生成，不在调用点无 Val.Out 时统计，
        //     所以不会覆盖上面几条更权威的来源。
        if (name is not null)
        {
            var nop = NativeOutParams.Mods(name, n);
            if (nop is not null) return nop;
        }

        // 3. 引擎蓝图库：不在游戏源码里，用显式补充表
        if (name is not null)
        {
            var e = EngineLibraryParams.Mods(name, n);
            if (e is not null) return e;

            // 区分两种失败：函数压根没解析出来 vs 解析出来了但参数个数对不上。
            // 后者通常是重载或默认参数，危害小；前者才需要人补签名。
            var known = (_bp?.Has(name) ?? false) || (_uht?.ByFunc.ContainsKey(name) ?? false) || EngineLibraryParams.Names.Contains(name);
            if (known) ArityMismatch.Add($"{name}({n})");
            else UnknownArity.Add(name);
        }
        return null;
    }

    /// <summary>
    /// 取文本常量的字面量。
    ///
    /// <c>FScriptText</c> 不是「一个字符串」，而是按 <c>TextLiteralType</c> 选字段的联合体：
    /// LocalizedText 用 LocalizedSource / LocalizedKey+Namespace，
    /// InvariantText 用 InvariantLiteralString，StringTableEntry 用 StringTableKey，
    /// 其余（含 CultureInvariant）用 LiteralString。
    ///
    /// 之前直接 <c>Value.ToString()</c> 拿到的是类型名 <c>UAssetAPI...FScriptText</c>，
    /// 于是卡面文本全变成了那串类型名 —— 不报错，但显示和比较全是错的。
    /// </summary>
    private string TextOf(EX_TextConst tc)
    {
        var v = tc?.Value;
        if (v is null) return "";

        KismetExpression pick = v.TextLiteralType switch
        {
            EBlueprintTextLiteralType.LocalizedText => v.LocalizedSource ?? v.LiteralString,
            EBlueprintTextLiteralType.InvariantText => v.InvariantLiteralString ?? v.LiteralString,
            EBlueprintTextLiteralType.StringTableEntry => v.StringTableKey ?? v.LiteralString,
            _ => v.LiteralString ?? v.InvariantLiteralString,
        };
        return pick is null ? "" : TextLiteral(pick);
    }

    /// <summary>从字符串常量节点里挖出裸字符串（LocalizedSource 是内嵌的 EX_StringConst）。</summary>
    private static string TextLiteral(KismetExpression e) => e switch
    {
        EX_StringConst s => s.Value,
        EX_UnicodeStringConst s => s.Value,
        EX_IntConst i => i.Value.ToString(),
        _ => e.ToString() ?? "",
    };

    private string Join(KismetExpression[] ps)
    {
        if (ps is null || ps.Length == 0) return "";
        return string.Join(", ", ps.Where(p => p is not null).Select(Val));
    }

    private static bool HasValue(KismetExpression e) => e switch
    {
        EX_Nothing or EX_EndFunctionParms or EX_EndStructConst or EX_EndArrayConst
            or EX_EndSetConst or EX_EndMapConst or EX_EndArray or EX_EndSet or EX_EndMap
            or EX_EndParmValue or EX_Breakpoint or EX_Tracepoint or EX_WireTracepoint => false,
        _ => true,
    };

    private static string Lit(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    private static string Lit(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    private static string Str(string s)
    {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string San(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}

