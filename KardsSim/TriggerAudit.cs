using System.Text;
using System.Text.RegularExpressions;
using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Generated;

namespace KardsSim;

/// <summary>
/// 触发点覆盖审计。
///
/// <para>
/// 三件事：
/// <list type="number">
/// <item>卡牌注册了哪些 trigger（从直译产物的函数名反推）</item>
/// <item>引擎手写的 FireTrigger 实际发了哪些（静态扫源码）</item>
/// <item><b>游戏自己的分派器</b>：每个 <c>Execute*Events</c> 内部向
///       <c>FetchAllCardsWithEventTrigger</c> 传的常量 triggerID —— 这是权威答案</item>
/// </list>
/// </para>
///
/// <para>
/// 第 3 项是关键。游戏把「何时发哪个 trigger、谁响应、什么顺序」全写在
/// <c>Execute*Events</c> 里了，那些函数已经被转译成 C#。手写 FireTrigger
/// 是在重复实现它，而且只实现了一小部分 —— 差集里的卡在模拟中就是白板。
/// </para>
/// </summary>
public static class TriggerAudit
{
    /// <summary>抓 FetchAllCardsWithEventTrigger(self, Val.Of(NNN), out ...) 里的 triggerID。</summary>
    private static readonly Regex TriggerIdRe =
        new(@"FetchAllCardsWithEventTrigger"",\s*new Val\[\]\s*\{\s*[^,]+,\s*Val\.Of\((\d+)\)", RegexOptions.Compiled);

    private static readonly Regex FnHeaderRe =
        new(@"^public static Val (\w+)\(IHost H, Val self, Val\[\] args\)", RegexOptions.Compiled);

    /// <summary>卡牌注册的触发点统计（给自对弈做完整性检查用）。</summary>
    public static (Dictionary<Trigger, List<string>> ByTrigger, int CardCount) RegisteredStats()
        => RegisteredByCards();

    /// <summary>卡牌注册了、但引擎从不触发的那些 —— 就是「白板卡」的成因。</summary>
    public static List<Trigger> UnfiredButRegistered()
    {
        var (byTrigger, _) = RegisteredByCards();
        var fired = ScanEngineFired();
        return byTrigger.Keys.Where(t => !fired.Contains(t))
                             .OrderByDescending(t => byTrigger[t].Count).ToList();
    }

    public static int Run(string[] args)
    {
        var (byTrigger, cardCount) = RegisteredByCards();
        var fired = ScanEngineFired();
        var gameMap = ScanGameExecutors();

        Console.WriteLine("=== 触发点覆盖 ===");
        Console.WriteLine($"卡牌资产: {cardCount} 张");
        Console.WriteLine($"卡牌注册的触发点: {byTrigger.Count}");
        Console.WriteLine($"引擎手写 FireTrigger 发的: {fired.Count}");
        Console.WriteLine($"游戏自己的 Execute*Events: {gameMap.Count}");
        Console.WriteLine();

        var dead = byTrigger.Keys.Where(t => !fired.Contains(t))
                                 .OrderByDescending(t => byTrigger[t].Count).ToList();
        Console.WriteLine($"--- 卡牌注册了、但引擎从不 fire（{dead.Count} 个，{dead.Sum(t => byTrigger[t].Count)} 处注册）---");
        foreach (var t in dead.Take(20))
        {
            var g = gameMap.TryGetValue(t, out var ex) ? ex : "<游戏里也没有>";
            Console.WriteLine($"  {t,-42} {byTrigger[t].Count,4} 张   游戏侧: {g}");
        }
        if (dead.Count > 20) Console.WriteLine($"  ... 还有 {dead.Count - 20} 个");

        Console.WriteLine();
        Console.WriteLine("--- 游戏侧有分派器、且卡牌有注册（可直接接管）---");
        var takeOver = byTrigger.Keys.Where(t => gameMap.ContainsKey(t))
                                     .OrderByDescending(t => byTrigger[t].Count).ToList();
        Console.WriteLine($"共 {takeOver.Count} 个，涉及 {takeOver.Sum(t => byTrigger[t].Count)} 处注册");
        foreach (var t in takeOver.Take(15))
            Console.WriteLine($"  {t,-42} {byTrigger[t].Count,4} 张   -> {gameMap[t]}");

        // 导出映射表，供后续把 FireTrigger 换成游戏自己的分派器
        var sb = new StringBuilder();
        sb.AppendLine("// 由 KardsSim --mode triggers 生成：触发点 -> 游戏自己的分派函数");
        sb.AppendLine("// 这张表是从转译产物里扫出来的，不是手写的。");
        sb.AppendLine("// 键是 Trigger，值是 BP_CardFunctions / BP_GameState_Battle 里的函数名。");
        sb.AppendLine("// 括号里是该函数向 FetchAllCardsWithEventTrigger 传的常量 triggerID。");
        sb.AppendLine();
        foreach (var t in Enum.GetValues<Trigger>().Where(t => t != Trigger.NotAvailable))
        {
            var reg = byTrigger.TryGetValue(t, out var l) ? l.Count : 0;
            var has = gameMap.TryGetValue(t, out var ex);
            sb.AppendLine($"// {t,-42} 注册 {reg,4} 张   {(has ? ex : "<无分派器>")}");
        }
        var outPath = Path.Combine(AppContext.BaseDirectory, "trigger-map.txt");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine();
        Console.WriteLine($"映射表 -> {outPath}");

        return 0;
    }

    /// <summary>卡牌资产名 -> 它注册的 trigger（从直译产物的函数名反推）。</summary>
    private static (Dictionary<Trigger, List<string>>, int) RegisteredByCards()
    {
        var byTrigger = new Dictionary<Trigger, List<string>>();
        var n = 0;
        foreach (var kv in FnIndex.Assets)
        {
            if (!kv.Key.StartsWith("card_", StringComparison.Ordinal)) continue;
            n++;
            foreach (var fn in kv.Value.Keys)
            {
                if (!Enum.TryParse<Trigger>(fn, out var t) || t == Trigger.NotAvailable) continue;
                if (!byTrigger.TryGetValue(t, out var list)) byTrigger[t] = list = new List<string>();
                list.Add(kv.Key);
            }
        }
        return (byTrigger, n);
    }

    /// <summary>扫游戏自己那 ~60 个 Execute*Events，取它们对应的常量 triggerID。</summary>
    private static Dictionary<Trigger, string> ScanGameExecutors()
    {
        var map = new Dictionary<Trigger, string>();
        foreach (var path in GeneratedFiles())
        {
            var cur = "";
            foreach (var line in File.ReadLines(path))
            {
                var h = FnHeaderRe.Match(line);
                if (h.Success) { cur = h.Groups[1].Value; continue; }

                var m = TriggerIdRe.Match(line);
                if (!m.Success || cur.Length == 0) continue;
                var id = int.Parse(m.Groups[1].Value);
                if (!Enum.IsDefined(typeof(Trigger), id)) continue;
                var t = (Trigger)id;
                // 同一个 trigger 可能有多个分派器（比如 before/after 两段），保留第一个
                if (!map.ContainsKey(t)) map[t] = $"{cur}(id={id})";
            }
        }
        return map;
    }

    private static IEnumerable<string> GeneratedFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Generated");
        if (!Directory.Exists(root)) root = "Generated";
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.g.cs", SearchOption.AllDirectories))
            yield return f;
    }

    /// <summary>
    /// 扫引擎源码里所有发触发点的地方，拿到实际会触发的集合。
    ///
    /// 要同时认两种写法：<c>FireTrigger(Trigger.X, ...)</c>（直接发）和
    /// <c>Fire(Trigger.X, ...)</c>（GameEngine.Triggers.cs 里的语义化包装）。
    /// 只认前者的话，把调用改成包装函数之后审计就会误报「从不 fire」。
    /// </summary>
    private static HashSet<Trigger> ScanEngineFired()
    {
        var set = new HashSet<Trigger>();
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Engine");
        if (!Directory.Exists(dir)) dir = "Engine";
        if (!Directory.Exists(dir)) return set;

        foreach (var f in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"\bFire(?:Trigger)?\(Trigger\.(\w+)"))
                if (Enum.TryParse<Trigger>(m.Groups[1].Value, out var t)) set.Add(t);
        }
        return set;
    }
}


