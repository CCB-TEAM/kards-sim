using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;

namespace KardsTranspiler;

/// <summary>
/// Kismet → C# 批量转译器。
///
///   dotnet run -- --in &lt;蓝图目录&gt; --usmap m.usmap --out ../generated [--only &lt;名字&gt;]
///
/// 产出：每个蓝图一个 .g.cs，内含该资产所有含字节码的函数，直译为 label+goto 的 C#。
/// 同时写报告（哪些节点没转出来），转译器绝不静默丢语义。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var input = Arg(args, "--in") ?? @"H:\go-cache\KardsSim\_input\Cards";
        var usmapPath = Arg(args, "--usmap") ?? @"H:\go-cache\KardsSim\_input\m.usmap";
        var outDir = Arg(args, "--out") ?? @"H:\go-cache\KardsSim\generated\Cards";
        var only = Arg(args, "--only");
        var limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : int.MaxValue;

        // UHT 头文件：导入函数的 out/in 只能从这里判定（蓝图侧没有标志位）
        var uhtRoot = Arg(args, "--uht") ?? @"H:\kards\Source";
        var uht = UhtParams.Load(uhtRoot);
        Console.WriteLine($"UHT 签名索引: {uht.Count} 个函数  ({uhtRoot})");


        var usmap = new Usmap(usmapPath);

        // 蓝图签名：cardFunction.* 是 BP_CardFunctions 里的函数，必须从它自己的
        // FunctionExport 取 out/in（头文件只覆盖了一部分）。
        var sigAssets = new List<string>();
        var bpSigDir = Arg(args, "--bpassets") ?? input;
        foreach (var pat in new[] { "BP_CardFunctions.uasset", "BP_Logic.uasset" })
        {
            var hit = Directory.EnumerateFiles(bpSigDir, pat, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) sigAssets.Add(hit);
        }
        var bpSig = BlueprintSignatures.Load(usmap, sigAssets, m => Console.WriteLine($"  [签名] {m}"));
        Console.WriteLine($"蓝图签名索引: {bpSig.Count} 个函数  ({string.Join(", ", sigAssets.Select(Path.GetFileName))})");

        // 预扫描：卡牌之间会互相调用对方资产里的辅助函数（EX_LocalVirtualFunction 只给名字、
        // 不带 FPackageIndex）。所以必须把所有资产扫一遍，收集全部导出函数签名。
        // 只加载 BP_CardFunctions 会漏掉 250+ 个卡牌内部辅助函数。
        var allAssets = Directory.EnumerateFiles(input, "*.uasset", SearchOption.AllDirectories)
                                .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // 依赖蓝图：GameStateRef 的成员（GetDeckBySide / GenerateNextCardID …）和
        // UtilityFunctions / BattleUtilityFunctions 等函数库都不在卡牌目录里，
        // 它们同样是签名来源，必须一起扫。
        var sigDeps = Arg(args, "--sigdeps");
        if (sigDeps is not null)
            allAssets.AddRange(Directory.EnumerateFiles(sigDeps, "*.uasset", SearchOption.AllDirectories)
                                        .OrderBy(x => x, StringComparer.Ordinal));

        var scanOk = 0;
        foreach (var f in allAssets)
        {
            try { bpSig.Add(new UAsset(f, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None), Path.GetFileNameWithoutExtension(f)); scanOk++; }
            catch { /* 预扫描失败不影响主流程，主流程会再报一次 */ }
        }
        Console.WriteLine($"跨资产签名索引: {bpSig.Count} 个函数（预扫描 {scanOk}/{allAssets.Count} 个资产）");

        // 诊断：--dump-params Foo,Bar 按三级来源逐条打印解析结果
        var dump = Arg(args, "--dump-params");
        if (dump is not null)
        {
            foreach (var raw in dump.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var n = raw.Trim();
                var src = bpSig.ByFunc.ContainsKey(n) ? "蓝图"
                        : uht.ByFunc.ContainsKey(n) ? "UHT"
                        : EngineLibraryParams.Names.Contains(n) ? "引擎表" : null;
                if (src is null) { Console.WriteLine($"{n,-44} <未解析>"); continue; }
                var m = src == "蓝图" ? bpSig.ByFunc[n]
                      : src == "UHT" ? uht.ByFunc[n]
                      : EngineLibraryParams.Mods(n, 0) ?? Array.Empty<string>();
                Console.WriteLine($"{n,-44} [{src}] ({m.Length}) [{string.Join(", ", m.Select(x => x.Length == 0 ? "in" : x))}]");
            }
            return 0;
        }

        // 诊断：--dump-bp 列出 BP 资产里的全部函数导出名（不过滤有无参数）
        if (Has(args, "--dump-bp"))
        {
            foreach (var a in sigAssets)
            {
                var ua = new UAsset(a, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None);
                var fns = ua.Exports.OfType<FunctionExport>().ToList();
                Console.WriteLine($"=== {Path.GetFileName(a)}: {fns.Count} 个函数导出 ===");
                foreach (var fn in fns.OrderBy(x => x.ObjectName.ToString(), StringComparer.Ordinal))
                {
                    var ps = fn.LoadedProperties?.Where(p => p.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm)
                             && !p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm)).ToList();
                    var desc = ps is null || ps.Count == 0 ? "(无参)"
                             : "[" + string.Join(",", ps.Select(p => (p.PropertyFlags.HasFlag(EPropertyFlags.CPF_OutParm)
                                 && !p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ConstParm) ? "out" : "in"))) + "]";
                    Console.WriteLine($"  {fn.ObjectName,-48} {desc}");
                }
            }
            return 0;
        }

        var files = Directory.EnumerateFiles(input, "*.uasset", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal).AsEnumerable();
        if (only != null) files = files.Where(f => Path.GetFileNameWithoutExtension(f).Contains(only, StringComparison.OrdinalIgnoreCase));
        files = files.Take(limit);

        // 依赖蓝图不只是「签名来源」，它们同样要被转译出来。
        //
        // 只拿它们当签名表会造成一类很难查的错：宿主按名字分派时找不到
        // BP_GameState_Battle.GetXxx，就退回 BP_CardFunctions 里的同名转发壳，
        // 而那个壳又调回 GameStateRef.GetXxx —— 名字一样、接收者没落地，
        // 于是无限递归到栈溢出。BP_GameState_Battle 与 Library\* 必须在索引里。
        //
        // --only / --limit 只作用于主输入目录，避免单卡调试时把依赖也一起裁掉。
        if (sigDeps is not null)
        {
            // 必须先物化成 HashSet 再比对：files 是惰性链，对它反复 Contains
            // 会把整条链重新求值一遍（O(n²)，而且链式求值会爆栈）。
            var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var deps = Directory.EnumerateFiles(sigDeps, "*.uasset", SearchOption.AllDirectories)
                                .Where(x => !seen.Contains(x))
                                .OrderBy(x => x, StringComparer.Ordinal)
                                .ToList();
            files = files.Concat(deps).ToList();
        }
        else
        {
            files = files.ToList();
        }

        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        var totalFn = 0; var okFn = 0; var emptyFn = 0;
        var allUnsupported = new List<string>();
        var unknownArity = new HashSet<string>(StringComparer.Ordinal);
        var arityMismatch = new HashSet<string>(StringComparer.Ordinal);
        /** 资产名 → 该资产里有字节码的函数名（用于生成全局索引） */
        var assetFns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var assetCount = 0; var failAssets = 0;

        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            try
            {
                var asset = new UAsset(f, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None);
                var funcNames = new List<string>();
                var code = TranspileAsset(asset, stem, uht, bpSig, ref totalFn, ref okFn, ref emptyFn, allUnsupported, unknownArity, arityMismatch, funcNames);
                if (code is null) continue;
                // 输出子目录沿用资产在输入树里的相对路径，保持阵营/系列的分层结构。
                // 依赖资产（--sigdeps）不在 input 树下，相对路径会算出 "..\sig_src\..."
                // 这种带 .. 的结果，拼接后直接写到 outDir 外面去。
                // 所以先判断是否真的落在 input 里，不在就统一收到 _deps\ 下。
                var rel = Path.GetRelativePath(input, f);
                var outside = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel);
                var sub = outside ? "_deps" : (Path.GetDirectoryName(rel) ?? "");
                var dir = Path.Combine(outDir, sub);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, stem + ".g.cs"), code);
                assetCount++;
                assetFns[stem] = funcNames;
            }
            catch (Exception ex)
            {
                failAssets++;
                report.AppendLine($"FAIL {stem}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"资产 {assetCount} 个已转译，失败 {failAssets}");
        Console.WriteLine($"函数 总计 {totalFn}，有字节码 {okFn}，空体 {emptyFn}");
        Console.WriteLine($"未支持节点 {allUnsupported.Count} 处");
        Console.WriteLine($"UHT 里查不到 out/in 的函数 {unknownArity.Count} 个");
        Console.WriteLine($"签名已知但实参个数对不上 {arityMismatch.Count} 个");
        if (unknownArity.Count > 0)
            foreach (var u in unknownArity.OrderBy(x => x).Take(25)) Console.WriteLine($"    ? {u}");

        // 全局索引：把「资产名 → 函数名 → 委托」汇总到一张表里。
        // 引擎靠它按 (卡牌资产名, 触发器名) 直接拿到要调的函数，不需要反射。
        var idx = new StringBuilder();
        idx.AppendLine("// <auto-generated>");
        idx.AppendLine("// 全部资产的函数总索引。引擎用它按 (资产名, 触发器名) 直接取函数。");
        idx.AppendLine("// </auto-generated>");
        idx.AppendLine("#nullable disable");
        idx.AppendLine("using System;");
        idx.AppendLine("using System.Collections.Generic;");
        idx.AppendLine("using KardsSim.Kismet;");
        idx.AppendLine();
        idx.AppendLine("namespace KardsSim.Generated;");
        idx.AppendLine();
        idx.AppendLine("/// <summary>资产名 → (函数名 → 委托)。</summary>");
        idx.AppendLine("public static class FnIndex");
        idx.AppendLine("{");
        idx.AppendLine("    public static readonly Dictionary<string, Dictionary<string, Func<IHost, Val, Val[], Val>>> Assets");
        idx.AppendLine("        = new(StringComparer.Ordinal)");
        idx.AppendLine("    {");
        foreach (var kv in assetFns.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            idx.AppendLine($"        [\"{kv.Key}\"] = {San(kv.Key)}.Registry.Fns,");
        }
        idx.AppendLine("    };");
        idx.AppendLine();
        idx.AppendLine("    /// <summary>取某资产里的某函数；找不到返回 null（绝不静默当成空操作）。</summary>");
        idx.AppendLine("    public static Func<IHost, Val, Val[], Val> Find(string asset, string fn)");
        idx.AppendLine("        => Assets.TryGetValue(asset, out var m) && m.TryGetValue(fn, out var f) ? f : null;");
        idx.AppendLine("}");
        File.WriteAllText(Path.Combine(outDir, "_index.g.cs"), idx.ToString());
        Console.WriteLine($"全局索引 -> {Path.Combine(outDir, "_index.g.cs")}（{assetFns.Count} 个资产）");

        // 未支持节点按种类聚合——这是「还剩多少要人工补」的直接度量
        var grouped = allUnsupported
            .GroupBy(s => s.Contains(": ") ? s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..] : s)
            .Select(g => (Kind: Normalize(g.Key), Count: g.Count()))
            .GroupBy(x => x.Kind)
            .Select(g => (g.Key, Count: g.Sum(x => x.Count)))
            .OrderByDescending(x => x.Count).Take(30).ToList();

        Console.WriteLine("\n未支持节点 Top30:");
        foreach (var (k, c) in grouped) Console.WriteLine($"  {c,7}  {k}");

        var reportPath = Path.Combine(outDir, "_transpile-report.txt");
        File.WriteAllText(reportPath, report.ToString() + "\n\n=== 未支持明细 ===\n" + string.Join("\n", allUnsupported) + "\n\n=== 未知签名 ===\n" + string.Join("\n", unknownArity.OrderBy(x=>x)) + "\n\n=== 实参个数不符 ===\n" + string.Join("\n", arityMismatch.OrderBy(x=>x)));
        Console.WriteLine($"\n报告 -> {reportPath}");
        return 0;
    }

    /// <summary>把 "… : EX_Foo 表达式" 归一成稳定类别，便于聚合统计。</summary>
    private static string Normalize(string s)
    {
        var i = s.IndexOf(" 不支持", StringComparison.Ordinal);
        if (i > 0) s = s[..i];
        var j = s.IndexOf(" 无语句语义", StringComparison.Ordinal);
        if (j > 0) s = s[..j];
        return s;
    }

    private static string TranspileAsset(UAsset asset, string stem, UhtParams uht, BlueprintSignatures bpSig,
        ref int totalFn, ref int okFn, ref int emptyFn, List<string> unsupported, HashSet<string> unknownArity, HashSet<string> arityMismatch,
        List<string> funcNames)
    {
        var funcs = asset.Exports.OfType<FunctionExport>().ToList();
        var withCode = funcs.Where(f => f.ScriptBytecode is { Length: > 0 }).ToList();
        totalFn += funcs.Count;
        okFn += withCode.Count;
        emptyFn += funcs.Count - withCode.Count;
        if (withCode.Count == 0) return null;

        var uber = withCode.FirstOrDefault(f => f.ObjectName.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal));
        var uberIdx = uber is null ? -1 : asset.Exports.IndexOf(uber) + 1;
        var calls = new List<(long N, string Caller)>();
        if (uber is not null)
            foreach (var fn in withCode)
            {
                if (ReferenceEquals(fn, uber)) continue;
                var n = FindUberCall(fn, uberIdx);
                if (n is not null) calls.Add((n.Value, fn.ObjectName.ToString()));
            }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// 由 KardsTranspiler 从 Kismet 字节码直译：{stem}");
        sb.AppendLine("// 控制流保留为 label + goto（不经 CFG 重建），执行流栈为运行期 Stack<int>。");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using KardsSim.Kismet;");
        sb.AppendLine();
        sb.AppendLine($"namespace KardsSim.Generated;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{stem} 的直译产物。</summary>");
        sb.AppendLine($"public static class {San(stem)}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 读局部槽。Kismet 的局部变量是零初始化的，但字典对未写过的名字会抛异常；");
        sb.AppendLine("    /// 循环体首次进入时读 Array_Get 的 out 槽就是这种情况（数组为空时该调用根本不执行）。");
        sb.AppendLine("    /// 未写过一律当 none，与虚拟机的零初值一致。");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static Val GetLocal(Dictionary<string, Val> L, string n)");
        sb.AppendLine("        => L.TryGetValue(n, out var v) ? v : Val.Nothing;");
        sb.AppendLine();

        foreach (var fn in withCode)
        {
            var name = fn.ObjectName.ToString();
            var em = new Emitter(asset, name, uht, bpSig);
            string body;
            try
            {
                body = ReferenceEquals(fn, uber)
                    ? em.Emit(fn, calls.OrderBy(c => c.N).ToList())
                    : em.Emit(fn, Array.Empty<(long, string)>());
            }
            catch (Exception ex)
            {
                unsupported.Add($"{stem}.{name}: 发射异常 {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            foreach (var u in em.Unsupported) unsupported.Add($"{stem}.{u}");
            foreach (var u in em.UnknownArity) unknownArity.Add(u);
            foreach (var u in em.ArityMismatch) arityMismatch.Add(u);
            funcNames.Add(name);
            sb.AppendLine($"// ---- {name} ----");
            sb.AppendLine(body);
            sb.AppendLine();
        }

        sb.AppendLine("public static class Registry");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly Dictionary<string, Func<IHost, Val, Val[], Val>> Fns = new(StringComparer.Ordinal)");
        sb.AppendLine("    {");
        foreach (var fn in withCode)
        {
            var name = fn.ObjectName.ToString();
            sb.AppendLine($"        [\"{name}\"] = {San(name)},");
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");

        sb.AppendLine("}");   // 资产类结束

        return sb.ToString();
    }

    private static string San(string s) => Emitter.San(s);

    /// <summary>从事件存根里找出它调用的 ExecuteUbergraph 入口偏移。</summary>
    private static long? FindUberCall(FunctionExport fn, int uberExportIdx)
    {
        long? found = null;
        void Walk(KismetExpression e)
        {
            if (found is not null || e is null) return;
            switch (e)
            {
                case EX_FinalFunction f when f.StackNode.Index == uberExportIdx: found = FirstConst(f.Parameters); return;
                case EX_CallMath c when c.StackNode.Index == uberExportIdx: found = FirstConst(c.Parameters); return;
                case EX_LocalVirtualFunction l when l.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal):
                    found = FirstConst(l.Parameters); return;
                case EX_VirtualFunction v when v.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal):
                    found = FirstConst(v.Parameters); return;
            }
            foreach (var f in e.GetType().GetFields())
            {
                if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
                else if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                    foreach (var x in arr) { if (found is not null) return; Walk(x); }
            }
        }
        foreach (var e in fn.ScriptBytecode!) { Walk(e); if (found is not null) break; }
        return found;

        static long? FirstConst(KismetExpression[] ps)
        {
            if (ps is null || ps.Length == 0) return null;
            var raw = ps[0].GetType().GetField("RawValue")?.GetValue(ps[0]);
            return raw switch { int i => i, byte b => b, long g => g, _ => null };
        }
    }

    /// <summary>取参数值；开关不存在、或后面跟的是另一个开关时返回 null（交由调用方用默认值）。</summary>
    private static string Arg(string[] a, string k)
    {
        var i = Array.IndexOf(a, k);
        if (i < 0 || i + 1 >= a.Length) return null;
        var v = a[i + 1];
        return v.StartsWith("--", StringComparison.Ordinal) ? null : v;
    }

    /// <summary>裸开关是否存在（--dump-bp 这种不带值的）。</summary>
    private static bool Has(string[] a, string k) => Array.IndexOf(a, k) >= 0;
}










