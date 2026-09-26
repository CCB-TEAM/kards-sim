using KardsSim.Ai;
using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Effects;
using KardsSim.Engine;
using KardsSim.Server;

namespace KardsSim;

/// <summary>
/// 入口。引擎本身是一个库；CLI 与 HTTP 只是它的两个宿主。
///
///   dotnet run                       → 起 HTTP 服务（默认 http://127.0.0.1:8642/）
///   dotnet run -- --mode selfplay    → 批量自对弈体检
///   dotnet run -- --mode dump        → 单局逐步打印
///   dotnet run -- --mode coverage    → 报告效果解析覆盖率
///   dotnet run -- --mode decks       → 卡组码审计（22 套推荐卡组哪几套能打完整）
///
/// 需要卡组的模式都认 <c>--left-deck</c> / <c>--right-deck</c>：
/// 卡组码（<c>%%…</c>）、decks.json 里的预设名、或逗号分隔的卡牌 id 列表。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var cardsPath = Arg(args, "--cards") ?? DataFiles.Find("cards.json");
        if (cardsPath == null || !File.Exists(cardsPath))
        {
            Console.Error.WriteLine("找不到 cards.json。用 --cards <path> 指定，或先运行 KardsDataExtract。");
            return 2;
        }
        CardDb.Load(cardsPath);
        Console.WriteLine($"卡牌库: {CardDb.All.Count} 张  ({cardsPath})");

        var mode = Arg(args, "--mode");
        if (mode == null || mode is "serve" or "http") return Serve(args);
        return mode switch
        {
            "selfplay" => SelfPlay(args),
            "dump" => Dump(args),
            "coverage" => Coverage(args),
            "fuzz" => Fuzz(args),
            "decks" => DeckAudit.Run(args),
            "triggers" => TriggerAudit.Run(args),
            "triggerhits" => TriggerRuntimeAudit.Run(args),
            "smoke" => SmokeAll.Run(args),
            "apicheck" => ApiAudit.Run(args),
            "rules" => RulesAudit.Run(args),
            "tests" => CardTests.Run(args),
        "repro1" => ReproIssue1.Run(args),
        "paramaudit" => ParamAudit.Run(args),
        "byref" => ByRefAudit.Run(args),
            // 未知 mode 必须报错而不是回落到 Serve：写错一个字母就会静默起一个 HTTP 服务，
            // 看起来「跑起来了」，实际什么都没测。
            _ => UnknownMode(mode),
        };
    }

    private static int UnknownMode(string mode)
    {
        Console.Error.WriteLine($"未知 --mode: {mode}");
        Console.Error.WriteLine("可用: selfplay | dump | coverage | fuzz | decks | triggers | triggerhits | smoke | apicheck | rules | tests | repro1 | paramaudit | byref | serve");
        return 2;
    }

    /// <summary>
    /// 卡组参数：<c>--left-deck</c> / <c>--right-deck</c> 接受卡组码（<c>%%…</c>）、
    /// decks.json 里的预设名（中文名 / 短名 / <c>#序号</c>）、逗号分隔的卡牌 id 列表。
    /// </summary>
    private static (string Left, string Right) Decks(string[] args) =>
        (Arg(args, "--left-deck"), Arg(args, "--right-deck"));

    private static int Serve(string[] args)
    {
        var url = Arg(args, "--url") ?? "http://127.0.0.1:8642/";
        new HttpServer(url).Run();
        return 0;
    }

    private static int SelfPlay(string[] args)
    {
        var games = Int(args, "--games") ?? 100;
        var maxSteps = Int(args, "--max-steps") ?? 2000;
        // 默认接直译产物。加 --legacy 才退回旧的文本解析效果表。
        var useHost = !args.Contains("--legacy");
        var (leftDeck, rightDeck) = Decks(args);
        // 给了卡组就每局交换左右：同一套卡组只打左方会带位置偏差。
        var swap = args.Contains("--swap-sides") && (leftDeck != null || rightDeck != null);
        Console.WriteLine($"自对弈 {games} 局  效果来源: {(useHost ? "Kismet 直译产物" : "旧文本解析表")}");
        Console.WriteLine($"  卡组: 左={leftDeck ?? "random"} 右={rightDeck ?? "random"}{(swap ? "（每局交换）" : "")}");

        var illegal = 0; var stuck = 0; var exceptions = 0;
        var steps = 0L; var triggers = 0L; var unhandled = 0L;
        var wins = new Dictionary<string, int> { ["LeftWins"] = 0, ["RightWins"] = 0, ["Draw"] = 0 };
        var turns = 0L;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 所有局的宿主未实现调用汇总：跨局看才是「还缺哪些 API」的真实清单，
        // 单局看到的只是这张卡碰到的子集。
        var missTotal = new Dictionary<string, int>(StringComparer.Ordinal);
        var hosts = new List<EngineHost>();

        for (var i = 0; i < games; i++)
        {
            try
            {
                var flip = swap && i % 2 == 1;
                var g = new GameEngine(1000 + i, flip ? rightDeck : leftDeck, flip ? leftDeck : rightDeck, false);
                if (useHost)
                {
                    var h = new EngineHost(g);
                    g.Host = h;
                    hosts.Add(h);
                }
                var n = 0;
                while (!g.IsDone && n < maxSteps)
                {
                    var legal = g.LegalActions();
                    if (legal.Count == 0) { stuck++; break; }
                    var pick = legal[g.S.Rng.Next(legal.Count)];
                    if (!g.Apply(pick)) illegal++;
                    n++;
                }
                steps += n; turns += g.S.Turn;
                triggers += g.TriggerFireCount;
                unhandled += g.Unhandled.Count;
                wins[g.S.Winner == Side.None ? "Draw" : (g.S.Winner == Side.Left ? "LeftWins" : "RightWins")]++;
            }
            catch (Exception ex)
            {
                exceptions++;
                if (exceptions <= 3) Console.Error.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
        sw.Stop();

        foreach (var h in hosts)
            foreach (var kv in h.Unhandled)
                missTotal[kv.Key] = missTotal.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;

        var calls = hosts.Sum(x => x.CallCount);
        var missCalls = missTotal.Values.Sum();

        Console.WriteLine($"  平均步数 : {(games > 0 ? Math.Round((double)steps / games, 1) : 0)}");
        Console.WriteLine($"  平均回合 : {(games > 0 ? Math.Round((double)turns / games, 1) : 0)}");
        Console.WriteLine($"  总耗时   : {sw.ElapsedMilliseconds} ms  ({(games > 0 ? Math.Round((double)sw.ElapsedMilliseconds / games, 2) : 0)} ms/局)");
        Console.WriteLine($"  非法动作 : {illegal}");
        Console.WriteLine($"  卡死     : {stuck}");
        Console.WriteLine($"  异常     : {exceptions}");
        Console.WriteLine($"  触发次数 : {triggers}");
        Console.WriteLine($"  未实现效果: {unhandled}");

        if (useHost)
        {
            Console.WriteLine();
            Console.WriteLine($"  宿主调用总数   : {calls}");
            Console.WriteLine($"  未实现调用占比 : {(calls > 0 ? Math.Round(100.0 * missCalls / calls, 2) : 0)}%  ({missCalls} 次)");
            Console.WriteLine($"  未实现函数种类 : {missTotal.Count}");
            foreach (var kv in missTotal.OrderByDescending(x => x.Value).Take(25))
                Console.WriteLine($"      {kv.Value,7}  {kv.Key}");
        }

        foreach (var kv in wins) Console.WriteLine($"  {kv.Key,-10}: {kv.Value}");

        // 没检查触发点的自对弈等于没跑：一张牌的逻辑全对，
        // 但如果引擎从不发它注册的那个触发点，它在模拟里就是白板 —— 而这是静默的。
        if (useHost && !args.Contains("--no-trigger-check"))
        {
            var (registered, cardCount) = TriggerAudit.RegisteredStats();
            var missed = TriggerAudit.UnfiredButRegistered();
            Console.WriteLine();
            Console.WriteLine($"  卡牌注册触发点 : {registered.Count} 个 / {cardCount} 张卡");
            Console.WriteLine($"  引擎从不触发   : {missed.Count} 个 ({(missed.Count == 0 ? "完整" : "缺口")})");
            foreach (var t in missed.Take(10))
                Console.WriteLine($"      {t}");
        }

        return illegal == 0 && stuck == 0 && exceptions == 0 ? 0 : 1;
    }

    private static int Fuzz(string[] args)
    {
        // 随机动作 + 随机 seed，专找崩溃与非法的边界情况
        var games = Int(args, "--games") ?? 200;
        var (leftDeck, rightDeck) = Decks(args);
        Console.WriteLine($"Fuzz {games} 局（随机 seed + 随机动作）");
        Console.WriteLine($"  卡组: 左={leftDeck ?? "random"} 右={rightDeck ?? "random"}");
        var bad = 0;
        for (var i = 0; i < games; i++)
        {
            try
            {
                var g = new GameEngine(i * 7919, leftDeck, rightDeck, false);
                var n = 0;
                while (!g.IsDone && n < 3000)
                {
                    var legal = g.LegalActions();
                    if (legal.Count == 0) break;
                    g.Apply(legal[g.S.Rng.Next(legal.Count)]);
                    n++;
                }
            }
            catch (Exception ex)
            {
                bad++;
                Console.Error.WriteLine($"  seed {i * 7919}: {ex.GetType().Name}: {ex.Message}");
                if (bad > 5) break;
            }
        }
        Console.WriteLine(bad == 0 ? "  全部通过" : $"  {bad} 局异常");
        return bad == 0 ? 0 : 1;
    }

    private static int Dump(string[] args)
    {
        var seed = Int(args, "--seed") ?? 42;
        var maxSteps = Int(args, "--max-steps") ?? 60;
        var (leftDeck, rightDeck) = Decks(args);
        var g = new GameEngine(seed, leftDeck, rightDeck, true);
        var n = 0;
        while (!g.IsDone && n < maxSteps)
        {
            var legal = g.LegalActions();
            if (legal.Count == 0) break;
            g.Apply(legal[g.S.Rng.Next(legal.Count)]);
            n++;
        }
        Console.WriteLine(g.S.Log.Text);
        Console.WriteLine($"steps={n} turn={g.S.Turn} done={g.IsDone} winner={g.S.Winner}");
        return 0;
    }

    private static int Coverage(string[] args)
    {
        var plans = CardDb.All.Select(d => (d, EffectPlanner.For(d))).ToList();
        var withOps = plans.Count(x => x.Item2.ByTrigger.Count > 0 || x.Item2.Passives.Count > 0);
        var onlyUnhandled = plans.Count(x => x.Item2.ByTrigger.Count == 0 && x.Item2.Unhandled.Count > 0);
        var clean = plans.Count(x => x.Item2.ByTrigger.Count > 0 && x.Item2.Unhandled.Count == 0);
        var partial = plans.Count(x => x.Item2.ByTrigger.Count > 0 && x.Item2.Unhandled.Count > 0);
        var vanilla = plans.Count(x => x.Item2.ByTrigger.Count == 0 && x.Item2.Unhandled.Count == 0);
        var clauses = plans.Sum(x => x.Item2.ByTrigger.Values.Sum(l => l.Count));

        Console.WriteLine($"卡牌总数       : {CardDb.All.Count}");
        Console.WriteLine($"  有可执行效果 : {withOps}");
        Console.WriteLine($"    全部解析   : {clean}");
        Console.WriteLine($"    部分解析   : {partial}");
        Console.WriteLine($"  仅未解析文案 : {onlyUnhandled}");
        Console.WriteLine($"  纯香草/关键字: {vanilla}");
        Console.WriteLine($"效果子句总数   : {clauses}");
        Console.WriteLine($"解析器计数     : Parsed={EffectPlanner.Parsed} VanillaOnly={EffectPlanner.VanillaOnly} PassiveOnly={EffectPlanner.PassiveOnly} Partial={EffectPlanner.Partial}");

        var top = plans.Where(x => x.Item2.Unhandled.Count > 0)
                       .SelectMany(x => x.Item2.Unhandled)
                       .GroupBy(s => s.Length > 70 ? s[..70] : s)
                       .OrderByDescending(g2 => g2.Count()).Take(20);
        Console.WriteLine("\n最常见的未解析子句:");
        foreach (var g2 in top) Console.WriteLine($"  {g2.Count(),5}  {g2.Key}");
        return 0;
    }

    private static string Arg(string[] a, string k)
    {
        var i = Array.IndexOf(a, k);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static int? Int(string[] a, string k)
        => int.TryParse(Arg(a, k), out var v) ? v : null;
}


