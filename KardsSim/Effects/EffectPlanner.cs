using System.Text.RegularExpressions;
using KardsSim.Core;

namespace KardsSim.Effects;

/// <summary>
/// 文本 → 效果计划。
///
/// 这是**回退路径**：权威实现是客户端蓝图字节码，最终要由转译出的
/// 卡牌效果代码取代这里。文本解析只在字节码路径尚未覆盖某张卡时兜底。
/// </summary>
public static class EffectPlanner
{
    private static readonly Dictionary<string, EffectPlan> Cache = new(StringComparer.Ordinal);
    public static int Parsed, VanillaOnly, PassiveOnly, Partial;

    /// <summary>已知的文案前缀，用来切分「Deployment: ... / 触发点: ...」这类复合文案。</summary>
    private static readonly string[] Prefixes =
    {
        "Deployment", "Order", "Battlecry", "Deathblow", "Fury", "Veteran", "Mobilize",
        "On Play", "On Attack", "On Death", "On Destroy", "On Deploy", "On Turn Start",
        "On Turn End", "On Start", "On End", "On Draw", "On Discard", "On Kill",
        "When Played", "When Deployed", "When Destroyed", "Whenever",
    };

    public static EffectPlan For(CardDef d)
    {
        if (d == null) return new EffectPlan();
        if (Cache.TryGetValue(d.Id, out var p)) return p;
        var plan = Build(d);
        Cache[d.Id] = plan;
        return plan;
    }

    private static EffectPlan Build(CardDef d)
    {
        var plan = new EffectPlan();
        var text = (d.Text ?? "").Trim();

        // location 的 Text 是历史说明文，不是规则
        if (d.IsLocation || IsFlavorText(text))
        {
            PassiveOnly++;
            return plan;
        }

        // 触发点 → 文案段落。卡牌自己 CDO 里的 usedTriggers 才是权威的通道列表。
        var segments = SplitSegments(text);
        var channels = d.Triggers.Count > 0 ? d.Triggers : new List<Trigger> { Trigger.NotAvailable };

        var any = false;
        var i = 0;
        foreach (var seg in segments)
        {
            var channel = PickChannel(seg.Prefix, channels, i);
            var ops = ParseClauses(seg.Body, plan);
            if (ops.Count > 0)
            {
                if (!plan.ByTrigger.TryGetValue(channel, out var l)) plan.ByTrigger[channel] = l = new List<Op>();
                l.AddRange(ops);
                any = true;
            }
            i++;
        }

        if (!any)
        {
            // 没有前缀的纯关键字卡：有触发注册但文案本身不含可执行子句
            var ops = ParseClauses(text, plan);
            if (ops.Count > 0)
            {
                var ch = channels[0];
                if (!plan.ByTrigger.TryGetValue(ch, out var l)) plan.ByTrigger[ch] = l = new List<Op>();
                l.AddRange(ops);
                any = true;
            }
        }

        if (!any && plan.Unhandled.Count == 0) VanillaOnly++;
        else if (any && plan.Unhandled.Count == 0) Parsed++;
        else Partial++;
        return plan;
    }

    private sealed record Segment(string Prefix, string Body);

    /// <summary>
    /// 按「下一个已知前缀」切分文案。
    /// 注意：这里必须用「查找下一个前缀的位置」而不是前瞻匹配整段——早先的写法
    /// 会把整个 Deployment: 段吞掉，导致卡牌效果全部为空。
    /// </summary>
    private static List<Segment> SplitSegments(string text)
    {
        var hits = new List<(int pos, int len, string prefix)>();
        foreach (var p in Prefixes)
        {
            var start = 0;
            while (true)
            {
                var idx = text.IndexOf(p, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                // 必须是词边界，且后面跟 ":" 或 " -" 才算前缀
                var after = idx + p.Length;
                var isBoundary = (idx == 0 || !char.IsLetter(text[idx - 1]));
                var tail = text.Substring(after, Math.Min(3, text.Length - after));
                if (isBoundary && (tail.StartsWith(":") || tail.StartsWith(" -")))
                    hits.Add((idx, p.Length, p));
                start = after;
            }
        }
        if (hits.Count == 0) return new List<Segment> { new("", text) };

        hits.Sort((a, b) => a.pos.CompareTo(b.pos));
        // 去掉重叠
        var keep = new List<(int pos, int len, string prefix)>();
        var last = -1;
        foreach (var h in hits)
        {
            if (h.pos <= last) continue;
            keep.Add(h);
            last = h.pos;
        }

        var res = new List<Segment>();
        for (var i = 0; i < keep.Count; i++)
        {
            var start = keep[i].pos + keep[i].len;
            // 跳过紧接的 ':' 与空白
            while (start < text.Length && (text[start] == ':' || text[start] == '-' || text[start] == ' ')) start++;
            var end = i + 1 < keep.Count ? keep[i + 1].pos : text.Length;
            var body = text.Substring(start, Math.Max(0, end - start)).Trim().TrimEnd('.', ',', ';').Trim();
            if (body.Length > 0) res.Add(new Segment(keep[i].prefix, body));
        }
        return res.Count > 0 ? res : new List<Segment> { new("", text) };
    }

    /// <summary>把文案段落分配到具体的触发器通道。</summary>
    private static Trigger PickChannel(string prefix, List<Trigger> channels, int index)
    {
        if (channels.Count == 0) return Trigger.NotAvailable;
        var pl = (prefix ?? "").ToLowerInvariant();
        if (pl.Contains("deploy") || pl.Contains("battlecry"))
            return channels.FirstOrDefault(t => t == Trigger.OnDeploymentEffectTriggered, channels[0]);
        if (pl.Contains("turn end") || pl.Contains("end of turn"))
            return channels.FirstOrDefault(t => t == Trigger.OnEndOfTurn, channels[0]);
        if (pl.Contains("turn start") || pl.Contains("start of turn"))
            return channels.FirstOrDefault(t => t == Trigger.OnStartofTurn, channels[0]);
        if (pl.Contains("death") || pl.Contains("destroy"))
            return channels.FirstOrDefault(t => t is Trigger.OnOtherCardDestroyed or Trigger.OnBeforeOtherCardDestroyed, channels[0]);
        if (pl.Contains("attack"))
            return channels.FirstOrDefault(t => t is Trigger.OnOtherCardAttacks or Trigger.OnAfterOtherCardAttacks, channels[0]);
        if (pl.Contains("draw"))
            return channels.FirstOrDefault(t => t == Trigger.OnOtherCardDrawnFromDeck, channels[0]);
        return index < channels.Count ? channels[Math.Min(index, channels.Count - 1)] : channels[0];
    }

    /// <summary>把一段文案拆成子句并解析成操作。</summary>
    private static List<Op> ParseClauses(string body, EffectPlan plan)
    {
        var ops = new List<Op>();
        if (string.IsNullOrWhiteSpace(body)) return ops;

        // 按句号/逗号切，但保留 "or"/"and" 连接的目标列表
        var clauses = Regex.Split(body, @"(?<=[.;])\s+|\s+(?:and then|then)\s+")
                           .Select(x => x.Trim().TrimEnd('.', ';'))
                           .Where(x => x.Length > 0).ToList();

        foreach (var cl in clauses)
        {
            var op = ParseClause(cl);
            if (op != null) ops.Add(op);
            else plan.Unhandled.Add(cl);
        }
        return ops;
    }

    private static Op ParseClause(string c)
    {
        var s = c.Trim();
        var low = s.ToLowerInvariant();
        if (low.Length == 0) return null;

        // Deal N damage to <target>
        var m = Regex.Match(s, @"[Dd]eal\s+(\d+)\s+damage(?:\s+to\s+(?<t>.+))?", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value);
            var t = m.Groups["t"].Success ? Target(m.Groups["t"].Value) : TargetSpec.Chosen;
            return t == TargetSpec.EnemyHq
                ? new Op { Kind = OpKind.DamageHq, Amount = n, Target = t, Raw = s }
                : new Op { Kind = OpKind.Damage, Amount = n, Target = t, Raw = s };
        }

        // Destroy <target>
        if (Regex.IsMatch(s, @"\b[Dd]estroy\b"))
            return new Op { Kind = OpKind.Destroy, Amount = 1, Target = Target(s), Raw = s };

        // Draw N cards
        m = Regex.Match(s, @"[Dd]raw\s+(\d+|a|an|two|three)\s+card", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = Word2Num(m.Groups[1].Value);
            return new Op { Kind = OpKind.Draw, Amount = n, Target = TargetSpec.Self, Raw = s };
        }

        // Restore/heal N
        m = Regex.Match(s, @"[Rr]estor(?:e|es)\s+(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return new Op { Kind = OpKind.Heal, Amount = int.Parse(m.Groups[1].Value), Target = Target(s), Raw = s };

        // Give +N attack / defence
        m = Regex.Match(s, @"([+-]\d+)\s*(?:attack|defen[cs]e)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value);
            var isAtk = low.Contains("attack");
            return new Op { Kind = isAtk ? OpKind.BuffAttack : OpKind.BuffDefense, Amount = n, Target = Target(s), Raw = s };
        }

        // Gain N kredits
        m = Regex.Match(s, @"[Gg]ain\s+(\d+)\s+kredit", RegexOptions.IgnoreCase);
        if (m.Success)
            return new Op { Kind = OpKind.AddKredits, Amount = int.Parse(m.Groups[1].Value), Target = TargetSpec.Self, Raw = s };

        if (Regex.IsMatch(low, @"\bpin(ned|s)?\b")) return new Op { Kind = OpKind.Pin, Amount = 1, Target = Target(s), Raw = s };
        if (Regex.IsMatch(low, @"\bsuppress")) return new Op { Kind = OpKind.Suppress, Amount = 1, Target = Target(s), Raw = s };

        // Add <CARD NAME> to your hand
        m = Regex.Match(s, @"[Aa]dd\s+(?<n>[A-Z][A-Z0-9 ']+?)\s+to\s+your\s+hand");
        if (m.Success)
            return new Op { Kind = OpKind.Spawn, CardName = m.Groups["n"].Value.Trim(), Target = TargetSpec.Named, Raw = s };

        // 纯关键字声明（Guard / Blitz / Smokescreen ...）：不是操作，也不算未解析
        if (Regex.IsMatch(low, @"^(guard|blitz|smokescreen|ambush|mobilize|fury|covert|bond|veteran|heavy armor|salvage|scrying|forecast)\b"))
            return null;

        return null;
    }

    private static TargetSpec Target(string t)
    {
        var l = (t ?? "").ToLowerInvariant();
        if (l.Contains("enemy") && (l.Contains("hq") || l.Contains("headquarter"))) return TargetSpec.EnemyHq;
        if (l.Contains("your hq") || l.Contains("friendly hq")) return TargetSpec.OwnHq;
        if (l.Contains("all enemy") || l.Contains("each enemy")) return TargetSpec.AllEnemyUnits;
        if (l.Contains("all friendly") || l.Contains("each friendly")) return TargetSpec.AllFriendlyUnits;
        if (l.Contains("all units") || l.Contains("each unit")) return TargetSpec.AllUnits;
        if (l.Contains("random")) return TargetSpec.RandomEnemyUnit;
        if (l.Contains("enemy")) return TargetSpec.EnemyUnit;
        if (l.Contains("this unit") || l.Contains("itself")) return TargetSpec.Self;
        return TargetSpec.Chosen;
    }

    private static int Word2Num(string w) => w.ToLowerInvariant() switch
    {
        "a" or "an" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
        _ => int.TryParse(w, out var n) ? n : 1,
    };

    /// <summary>
    /// location 卡的 Text 是历史介绍文，不是规则文案。
    /// 判定：很长、且不含任何规则动词。
    /// </summary>
    private static bool IsFlavorText(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return true;
        if (t.Length > 400) return true;
        var low = t.ToLowerInvariant();
        return !Regex.IsMatch(low, @"\b(deal|destroy|draw|gain|restore|pin|suppress|damage|kredit|deploy)\b");
    }
}