// 一次性审计：把引擎原语表与 UHT 签名对照，找出「该写 out 槽却直接返回」的错。
//
// 背景：这是一个反复出现的缺陷类。UHT 里大量谓词是
//   void IsUnit(bool& isIt);
// 纯 out 形参、没有返回值。如果宿主实现写成
//   ["IsUnit"] = (h, a) => Val.Of(...)
// 调用方读的却是 out 槽（CallFunc_IsUnit_isIt），于是恒为 false。
// 不报错、不进 Unhandled，只是规则静默走偏 —— 只能靠比对签名发现。
//
// 用法：dotnet run -- --mode paramaudit <UHT头文件目录>
using System.Text.RegularExpressions;

namespace KardsSim;

public static class ParamAudit
{
    private sealed record Sig(string Name, bool HasReturn, int OutCount, int InCount);

    public static int Run(string[] args)
    {
        // 头文件目录：取 --uht <dir>，默认 H:\kards\Source
        var headerDir = @"H:\kards\Source";
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--uht") headerDir = args[i + 1];

        if (!Directory.Exists(headerDir))
        {
            Console.WriteLine($"找不到头文件目录: {headerDir}");
            return 2;
        }

        // 1. 从 UHT 头文件里抽签名
        var uht = new Dictionary<string, Sig>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(headerDir, "*.h", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(f),
                         @"^\s*(?:virtual\s+)?(void|bool|int32|float|FString)\s+(\w+)\s*\(([^;{]*)\)\s*;",
                         RegexOptions.Multiline))
            {
                var ret = m.Groups[1].Value;
                var name = m.Groups[2].Value;
                var ps = m.Groups[3].Value;
                var outs = Regex.Matches(ps, @"&").Count;
                var ins = string.IsNullOrWhiteSpace(ps)
                    ? 0
                    : ps.Split(',').Length - outs;
                uht[name] = new Sig(name, ret != "void", outs, ins);
            }
        }
        Console.WriteLine($"UHT 签名: {uht.Count} 个");

        // 2. 读原语表：抓出每个 [\"名字\"] = (h, a) => <实现> 的首行
        var src = File.ReadAllLines(Path.Combine("Bridge", "EngineHost.cs"));
        var suspects = new List<string>();
        var okCount = 0;
        var noSig = 0;

        for (var i = 0; i < src.Length; i++)
        {
            var m = Regex.Match(src[i], @"\[""(\w+)""\]\s*=\s*\(h, a\)\s*=>");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;

            if (!uht.TryGetValue(name, out var sig)) { noSig++; continue; }

            // 该写 out 的：UHT 有 out 形参、且返回 void
            if (sig.HasReturn || sig.OutCount == 0) { okCount++; continue; }

            // 看实现体有没有写 out 槽。窗口要够大 —— 多行 lambda 函数体很常见，
            // 窗口太小会把「其实写了」的报成可疑（ChangeDefense 就这样被误报过）。
            var body = string.Join("\n", src.Skip(i).Take(40));
            var writesOut = body.Contains("Out(a") || body.Contains("TrySetOut")
                            || Regex.IsMatch(body, @"Out\(a\[");
            if (!writesOut) suspects.Add($"{name}   UHT=({sig.InCount} in, {sig.OutCount} out, void)");
        }

        Console.WriteLine($"比对: 一致 {okCount} / 无 UHT 签名 {noSig} / 可疑 {suspects.Count}");
        Console.WriteLine();
        if (suspects.Count == 0)
        {
            Console.WriteLine("没有发现「该写 out 却直接返回」的原语。");
            return 0;
        }

        Console.WriteLine("可疑（UHT 是 void 带 out 形参，实现却没有写 out 槽）:");
        foreach (var s in suspects.OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine($"  {s}");
        Console.WriteLine();
        Console.WriteLine("注意：例外情况确实存在 ——");
        Console.WriteLine("  · ValueSet/ValueGet 这类按值返回、UHT 里恰好也有 & 的");
        Console.WriteLine("  · 实现里用 async/委托间接写 out 的");
        Console.WriteLine("所以这是「需要人看一眼」的清单，不是「全是 bug」的清单。");
        return suspects.Count == 0 ? 0 : 1;
    }
}
