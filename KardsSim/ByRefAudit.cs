using System.Text;
using System.Text.RegularExpressions;

namespace KardsSim;

/// <summary>
/// 审计：**被当成纯 out、函数体里却又读它的形参** —— 这一读，读到的是空值。
///
/// <para>
/// 背景：直译产物把一个 out 形参发射成
/// <c>var __out_x = args[i].As&lt;Action&lt;Val&gt;&gt;(); L["x"] = Val.Nothing;</c>
/// —— 也就是「调用方不传值，函数退出时回调写回」。这在**函数体只写不读**时是对的。
/// </para>
///
/// <para>
/// 但 UE 的 Kismet 里所有形参都是把求值结果送进被调帧的：被调函数<b>能读到</b>
/// 调用方传进来的值，out 只是「退出时还要写回」而已。于是凡是「把 out 形参当输入读」
/// 的函数，在直译产物里都变成<b>空操作</b>：读到的永远是 <c>Val.Nothing</c>，
/// 不报错、不进 <c>Unhandled</c>、smoke / selfplay 也全绿。
/// </para>
///
/// <para>
/// 实测受害面（2026-02 一次全扫 45 处），其中影响对局的：
/// <c>GetRandomCard(cards)</c>（所有「随机一个敌方单位」类效果）、
/// <c>ApplyMakeCardRetreat(cards)</c>（送回手牌）、
/// <c>ApplySetCardsSeenByCipher(card)</c>（Intel）、
/// <c>AttackCard(defenderCardID)</c>、<c>ApplyDestroyMultipleCards</c>、
/// <c>ExecuteOnDestructionEffectTriggered</c>、
/// <c>TriggerMultipleDeploymentEffects(inputCards)</c>、
/// <c>GetRandomKreditCombo</c>、<c>MoveCards(EnemyUnits)</c>、
/// <c>SortCardsByLocationNumber</c> / <c>GetNewLocationNumbers</c>（撤退重排位置）。
/// </para>
///
/// <para>
/// 判据（都在同一个函数体内）：
/// </para>
/// <list type="number">
/// <item>有一行 <c>var __out_名 = args[..]</c>（说明它被判定成 out 形参）；</item>
/// <item>紧跟的那行把本地槽**零初始化**（<c>L["名"] = Val.Nothing;</c>）——
///   这是旧发射器形态，也就是「读不到调用方的值」。修好之后这里应当是
///   <c>L["名"] = args[i].In …</c>；</item>
/// <item>body 里有 <c>GetLocal(L, "名")</c>（说明它被当输入读）—— 那就是空操作。</item>
/// </list>
///
/// <para>
/// 也就是说这个模式在修好之后应当清零；剩下没清零的就是还没重新生成的产物
/// （例如 `_deps/` 里那些没有 uasset、暂时只能手工补的资产）。
/// </para>
/// </summary>
public static class ByRefAudit
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

        var rxDecl = new Regex(@"^\s*public static Val (\w+)\(", RegexOptions.Compiled);
        var rxOut = new Regex(@"var __out_(\w+) = args", RegexOptions.Compiled);
        var zero = new List<(string File, string Fn, string Param, int Line)>();
        var reads = new List<(string File, string Fn, string Param, int Line)>();

        foreach (var f in Directory.EnumerateFiles(genDir, "*.g.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(f);
            var cur = "(top)";
            // 形参名 → 声明行；另记「零初始化」的形参
            var outs = new Dictionary<string, int>(StringComparer.Ordinal);
            var isZeroed = new HashSet<string>(StringComparer.Ordinal);
            var readAt = new Dictionary<string, int>(StringComparer.Ordinal);

            void Flush()
            {
                foreach (var kv in outs)
                {
                    var name = kv.Key;
                    if (isZeroed.Contains(name)) zero.Add((Path.GetFileName(f), cur, name, kv.Value + 1));
                    if (readAt.TryGetValue(name, out var r) && r > kv.Value + 1)
                        reads.Add((Path.GetFileName(f), cur, name, r + 1));
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var d = rxDecl.Match(line);
                if (d.Success) { Flush(); cur = d.Groups[1].Value; outs.Clear(); isZeroed.Clear(); readAt.Clear(); continue; }

                var o = rxOut.Match(line);
                if (o.Success)
                {
                    var n = o.Groups[1].Value;
                    outs[n] = i;
                    if (i + 1 < lines.Length && lines[i + 1].Contains($"L[\"{n}\"] = Val.Nothing;"))
                        isZeroed.Add(n);
                    continue;
                }

                foreach (var name in outs.Keys)
                {
                    if (readAt.ContainsKey(name)) continue;
                    if (line.Contains($"GetLocal(L, \"{name}\")")) readAt[name] = i;
                }
            }
            Flush();
        }

        // 真·缺口 = 既零初始化、又被当输入读
        var zeroSet = new HashSet<string>(zero.Select(z => $"{z.File}|{z.Fn}|{z.Param}"));
        var hits = reads.Where(r => zeroSet.Contains($"{r.File}|{r.Fn}|{r.Param}")).ToList();

        Console.WriteLine();
        Console.WriteLine("=== 被当成纯 out（本地槽零初始化）、函数体里却先读它的形参 ===");
        Console.WriteLine("（UE 里被调帧能读到调用方传的值；这种形态读到的永远是 Val.Nothing → 空操作）");
        Console.WriteLine();

        foreach (var g in hits.GroupBy(h => h.Fn).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key,-40} {string.Join(", ", g.Select(x => x.Param))}   [{g.First().File}]");

        Console.WriteLine();
        Console.WriteLine($"缺口 {hits.Count} 处 / {hits.Select(h => h.Fn).Distinct().Count()} 个函数 / " +
                          $"{hits.Select(h => h.File).Distinct().Count()} 个文件");
        Console.WriteLine($"（其中零初始化的 out 形参共 {zero.Count} 处，被当输入读的共 {reads.Count} 处；" +
                          $"两者都占才算缺口）");
        Console.WriteLine("修法：转译器把 out 形参按 in-out 发射（调用点传值 + 写回），然后重新生成。");

        var report = Path.Combine(AppContext.BaseDirectory, "byref-report.txt");
        var sb = new StringBuilder();
        foreach (var h in hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line))
            sb.AppendLine($"{h.File}:{h.Line}\t{h.Fn}\t{h.Param}");
        foreach (var z in zero.OrderBy(z => z.File, StringComparer.Ordinal))
            sb.AppendLine($"# 仍是零初始化形态（未重新生成）\t{z.File}\t{z.Fn}\t{z.Param}");
        File.WriteAllText(report, sb.ToString());
        Console.WriteLine($"清单 -> {report}");

        return 0;
    }
}
