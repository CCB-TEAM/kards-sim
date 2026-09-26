using System.Text.RegularExpressions;
using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Kismet;

namespace KardsSim;

/// <summary>
/// 宿主 API 完备性审计。
///
/// <para>
/// 自对弈和 smoke 都是「采样」：没被采到的调用等于没测。这个模式改成静态全扫 ——
/// 把 <c>Generated/</c> 里所有 <c>H.Call("X", ...)</c> 的目标名全抓出来，
/// 然后判断每个名字是否<b>有地方接</b>：
/// </para>
/// <list type="number">
///   <item><see cref="EngineHost"/> 的引擎原语表（真正改 GameState 的叶子）</item>
///   <item><see cref="Host.Builtin"/> 的引擎库内建（Array/Map/Set/String/Math…）</item>
///   <item>转译产物里的某个资产函数（蓝图自己实现的功能）</item>
/// </list>
///
/// <para>
/// 三类都不沾的名字，就是「一旦被调到就静默变成空操作」的缺口。这是比跑多少局
/// 都可靠的完备性指标：<b>它不依赖采样运气</b>。
/// </para>
/// </summary>
public static class ApiAudit
{
    public static int Run(string[] args)
    {
        var genDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Generated");
        if (!Directory.Exists(genDir)) genDir = "Generated";
        if (!Directory.Exists(genDir))
        {
            Console.Error.WriteLine($"找不到 Generated 目录: {genDir}");
            return 1;
        }

        // ---- 1. 扫所有调用点 ----
        var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rxFn = new Regex(@"H\.Call\(""([^""]+)""", RegexOptions.Compiled);
        var rxDecl = new Regex(@"^\s*public static Val (\w+)\(", RegexOptions.Compiled);

        var files = Directory.EnumerateFiles(genDir, "*.g.cs", SearchOption.AllDirectories).ToList();
        foreach (var f in files)
        {
            var cur = "(top)";
            foreach (var line in File.ReadLines(f))
            {
                var d = rxDecl.Match(line);
                if (d.Success) { cur = d.Groups[1].Value; continue; }
                foreach (Match m in rxFn.Matches(line))
                {
                    var name = m.Groups[1].Value;
                    if (!callers.TryGetValue(name, out var s))
                        callers[name] = s = new HashSet<string>(StringComparer.Ordinal);
                    if (s.Count < 4) s.Add(cur);
                }
            }
        }

        // ---- 2. 三类接收方 ----
        var prims = EngineHost.PrimitiveNames;          // 引擎原语
        var builtin = Host.BuiltinNames;                // 引擎库内建
        // 转译产物：任何资产里存在同名函数就算有实现
        var transpiled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in Generated.FnIndex.Assets)
            foreach (var fn in kv.Value.Keys)
                transpiled.Add(fn);

        var missing = new List<(string Name, HashSet<string> Callers)>();
        var handled = 0;
        foreach (var kv in callers)
        {
            var n = kv.Key;
            if (prims.Contains(n) || builtin.Contains(n) || transpiled.Contains(n)) { handled++; continue; }
            // 前导下划线/编译器内部名不算缺口
            if (n.StartsWith('_')) { handled++; continue; }
            missing.Add((n, kv.Value));
        }

        Console.WriteLine("=== 宿主 API 完备性（静态全扫）===");
        Console.WriteLine($"扫描文件      : {files.Count}");
        Console.WriteLine($"不同调用目标  : {callers.Count}");
        Console.WriteLine($"有实现        : {handled}  ({(callers.Count > 0 ? Math.Round(100.0 * handled / callers.Count, 2) : 0)}%)");
        Console.WriteLine($"无实现        : {missing.Count}");
        Console.WriteLine();

        // 366 个缺口里绝大多数是 UI 控件 / 日期 / 向量运算 —— 无头模拟永远不会调到。
        // 真正要紧的是「卡牌逻辑链上够得着」的那些：从卡牌 ubergraph 或
        // BP_CardFunctions / BP_GameState_Battle 直接发起的调用。
        static bool IsGameLogic(string caller) =>
            caller.StartsWith("ExecuteUbergraph_card_", StringComparison.Ordinal) ||
            caller.StartsWith("card_", StringComparison.Ordinal) ||
            caller is "BP_CardFunctions" or "BP_GameState_Battle" or "(top)";

        var critical = missing.Where(m => m.Callers.Any(IsGameLogic)).ToList();
        var cosmetic = missing.Count - critical.Count;

        Console.WriteLine($"--- 卡牌逻辑可达（{critical.Count} 个）—— 这些会真的影响对局 ---");
        foreach (var (name, cs) in critical.OrderBy(x => x.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {name,-42} {string.Join(", ", cs.OrderBy(x => x, StringComparer.Ordinal).Take(3))}");
        Console.WriteLine();
        Console.WriteLine($"--- 仅 UI / 平台 / 界面可达（{cosmetic} 个，无头模拟不会走到）---");

        return 0;
    }
}
