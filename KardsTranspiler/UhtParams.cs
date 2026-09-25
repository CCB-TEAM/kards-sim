using System.Text.RegularExpressions;

namespace KardsTranspiler;

/// <summary>
/// 从游戏自己的 UHT 头文件里还原函数的参数修饰（out / in）。
///
/// **为什么必须靠头文件**：蓝图字节码里，out 实参和 in 实参在语法上完全一样
/// （都是一个表达式）。被调函数若是本资产的导出，还能查它的 <c>LoadedProperties</c>；
/// 但卡牌效果调用的是 <c>cardFunction.*</c> / <c>BaseCardObject</c> 这些**导入**，
/// 蓝图侧拿不到任何标志位。只有 UHT 头文件写着 <c>Type&amp;</c>。
///
/// 判据（与 UHT 一致）：
/// <list type="bullet">
/// <item><c>const T&amp;</c> → 输入（按引用传只是性能考虑）</item>
/// <item><c>T&amp;</c> / <c>UPARAM(ref)</c> / <c>UPARAM(out)</c> → 输出</item>
/// <item><c>T</c>（值） → 输入</item>
/// </list>
/// 不做 out 回写会让所有 <c>Array_Get</c> / <c>GetCardFromID</c> 这类取结果的调用永远返回空，
/// 这正是「效果看起来跑了但什么都没发生」的根因。
/// </summary>
public sealed class UhtParams
{
    /// <summary>函数名 → 每个形参的修饰（"" = in，"out" = out）。</summary>
    public readonly Dictionary<string, string[]> ByFunc = new(StringComparer.Ordinal);
    /// <summary>函数名 → 形参名（用于日志与诊断）。</summary>
    public readonly Dictionary<string, string[]> Names = new(StringComparer.Ordinal);

    public int Count => ByFunc.Count;

    // 注意两点：
    //  1. 末尾不要要求 ';' —— 调用方已按 ';' 切分，分号在那一步就被去掉了。
    //  2. 参数表必须允许一层嵌套括号：UPARAM(Ref) FText& x 这种参数里带括号，
    //     用 [^()]* 会把几乎所有 CardFunctionsStub 的声明全部拒掉。
    private static readonly Regex DeclRe = new(
        @"^\s*(?:static\s+|virtual\s+|explicit\s+|inline\s+)*([A-Za-z_][\w:<>,\s\*&]*?)\s+([A-Za-z_]\w*)\s*\(((?:[^()]|\([^()]*\))*)\)\s*(?:const)?\s*$",
        RegexOptions.Compiled);

    public static UhtParams Load(params string[] roots)
    {
        var p = new UhtParams();
        foreach (var root in roots)
        {
            if (File.Exists(root)) { p.ParseFile(root); continue; }
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.h", SearchOption.AllDirectories))
                p.ParseFile(f);
        }
        return p;
    }

    private void ParseFile(string file)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return; }

        // 先去掉注释，再把整个文件按「声明分隔符」切开。
        // 不能按行累积：类体开括号 { 会跟函数声明挤在同一段里，破坏 ^ 锚定的匹配。
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", " ");

        foreach (var chunk in text.Split(new[] { ';', '{', '}' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // 折叠空白：UFUNCTION(...) 与声明之间通常隔着换行，不折叠则 ^...$ 匹配不到
            var d = Collapse(StripLeadingMacros(Collapse(chunk)));
            if (d.Length == 0) continue;
            var m = DeclRe.Match(d);
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            if (name is "if" or "for" or "while" or "switch" or "return" or "else" or "class" or "struct") continue;
            var (mods, names) = SplitParams(m.Groups[3].Value);
            if (!ByFunc.ContainsKey(name)) { ByFunc[name] = mods; Names[name] = names; }
        }
    }

    /// <summary>把任意空白折叠成单个空格（保留字符串语义无关，头文件里没有字符串字面量参数）。</summary>
    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>剥掉声明前的宏前缀（UCLASS(...) / UFUNCTION(...) / GENERATED_BODY() 等）。</summary>
    private static string StripLeadingMacros(string s)
    {
        while (true)
        {
            var t = s.TrimStart();
            var m = Regex.Match(t, @"^([A-Z_][A-Z0-9_]*)\s*\(");
            if (!m.Success) return t;
            // 跳到该宏的配对右括号
            var depth = 0; var i = m.Length - 1;
            for (; i < t.Length; i++)
            {
                if (t[i] == '(') depth++;
                else if (t[i] == ')' && --depth == 0) break;
            }
            if (i >= t.Length) return t;
            var rest = t[(i + 1)..].TrimStart();
            // 宏后面紧跟的仍是声明才继续剥；否则说明这是返回类型的一部分，停手
            if (rest.Length == 0 || rest.StartsWith("(")) return t;
            // 剩下的部分若本身不含 '('，说明整个 chunk 只是个宏（如 GENERATED_BODY），
            // 不是函数声明；此时保留原样交给 DeclRe 判失败即可。
            s = rest;
        }
    }

    /// <summary>按顶层逗号切分参数表，逐个判定修饰。</summary>
    private static (string[] Mods, string[] Names) SplitParams(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (Array.Empty<string>(), Array.Empty<string>());
        var parts = SplitTopLevel(text);
        var mods = new string[parts.Count];
        var names = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var p = StripUparam(parts[i]);
            names[i] = LastIdent(p);
            mods[i] = IsOut(p) ? "out" : "";
        }
        return (mods, names);
    }

    /// <summary>去掉 UPARAM(...) 前缀，同时记录它是否声明了 Ref/Out。</summary>
    private static string StripUparam(string p)
    {
        p = p.Trim();
        var depth = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '(') depth++;
            else if (p[i] == ')')
            {
                depth--;
                if (depth == 0 && p.StartsWith("UPARAM", StringComparison.OrdinalIgnoreCase))
                    return p[(i + 1)..].TrimStart();
                if (depth < 0) break;
            }
        }
        return p;
    }

    private static bool IsOut(string p)
    {
        var upper = p.ToUpperInvariant();
        if (upper.Contains("UPARAM") && (upper.Contains("REF") || upper.Contains("OUT"))) return true;
        if (p.TrimStart().StartsWith("const ", StringComparison.Ordinal)) return false;
        if (p.TrimStart().StartsWith("const&") || p.TrimStart().StartsWith("const &")) return false;
        // 非 const 的 & 或 *& → 可写
        return p.Contains('&');
    }

    private static string LastIdent(string p)
    {
        var m = Regex.Match(p, @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*$");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> SplitTopLevel(string s)
    {
        var res = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) { res.Add(s[start..i]); start = i + 1; }
        }
        res.Add(s[start..]);
        return res.Where(x => x.Trim().Length > 0).ToList();
    }

    /// <summary>查修饰；未知时返回 null（调用方应保守按 in 处理并记诊断）。</summary>
    public string[] Mods(string func, int argCount)
    {
        if (!ByFunc.TryGetValue(func, out var m)) return null;
        return m.Length == argCount ? m : null;
    }
}
