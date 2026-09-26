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
/// 判据不是「名字里有 out」，而是三件事同时成立（都在同一个函数体内）：
/// </para>
/// <list type="number">
/// <item>有一行 <c>var __out_名 = args[..]</c>（说明它被判定成 out 形参）；</item>
/// <item>body 里有 <c>GetLocal(L, "名")</c>（说明被读）；</item>
/// <item>第一次读出现在第一次赋值之前（排除发射器那行零初始化
/// <c>L["名"] = Val.Nothing;</c>）—— 先赋值再读的属于正常写法。</item>
/// </list>
///
/// <para>
/// 修法在转译器侧（本审计只负责把清单摊开）：out 形参按 in-out 处理 ——
/// 调用点同时把当前值传进去（<c>Val.Out(值, 写回)</c>），被调帧据此初始化本地槽。
/// 那时这个清单应该清空，本模式会自己变绿。
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
        var hits = new List<(string File, string Fn, string Param, int Line)>();

        foreach (var f in Directory.EnumerateFiles(genDir, "*.g.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(f);
            var cur = "(top)";
            // 形参名 → (声明行, 零初始化行)
            var outs = new Dictionary<string, (int Decl, int Zero)>(StringComparer.Ordinal);
            var firstRead = new Dictionary<string, int>(StringComparer.Ordinal);

            void Flush()
            {
                foreach (var kv in outs)
                {
                    var name = kv.Key;
                    if (!firstRead.TryGetValue(name, out var readAt)) continue;
                    var decl = kv.Value.Decl;
                    var zero = kv.Value.Zero;
                    if (readAt <= decl || readAt == zero) continue;   // 没读，或读的就是那行零初始化
                    hits.Add((Path.GetFileName(f), cur, name, readAt + 1));
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var d = rxDecl.Match(line);
                if (d.Success) { Flush(); cur = d.Groups[1].Value; outs.Clear(); firstRead.Clear(); continue; }

                var o = rxOut.Match(line);
                if (o.Success)
                {
                    var n = o.Groups[1].Value;
                    outs[n] = (i, i + 1 < lines.Length && lines[i + 1].Contains($"L[\"{n}\"] = Val.Nothing;") ? i + 1 : -1);
                    continue;
                }

                foreach (var name in outs.Keys)
                {
                    if (firstRead.ContainsKey(name)) continue;
                    if (line.Contains($"GetLocal(L, \"{name}\")")) firstRead[name] = i;
                }
            }
            Flush();
        }

        Console.WriteLine();
        Console.WriteLine("=== 被当成纯 out、函数体里却先读它的形参 ===");
        Console.WriteLine("（UE 里被调帧能读到调用方传的值；直译产物把这行读成了 Val.Nothing）");
        Console.WriteLine();

        foreach (var g in hits.GroupBy(h => h.Fn).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key,-40} {string.Join(", ", g.Select(x => x.Param))}   [{g.First().File}]");

        Console.WriteLine();
        Console.WriteLine($"合计 {hits.Count} 处，涉及 {hits.Select(h => h.Fn).Distinct().Count()} 个函数、" +
                          $"{hits.Select(h => h.File).Distinct().Count()} 个文件");
        Console.WriteLine("修法：转译器把 out 形参按 in-out 发射（调用点传值 + 写回），然后重新生成。");

        var report = Path.Combine(AppContext.BaseDirectory, "byref-report.txt");
        var sb = new StringBuilder();
        foreach (var h in hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line))
            sb.AppendLine($"{h.File}:{h.Line}\t{h.Fn}\t{h.Param}");
        File.WriteAllText(report, sb.ToString());
        Console.WriteLine($"清单 -> {report}");

        // 不是「有就失败」：这是已知缺口的清单，先把数字摊开；
        // 缺口清零时这里返回 0，说明可以把这个模式并进 tests。
        return 0;
    }
}
