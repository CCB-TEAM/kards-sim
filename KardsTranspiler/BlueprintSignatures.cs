using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace KardsTranspiler;

/// <summary>
/// 从蓝图资产本身读取函数签名。
///
/// **为什么必须这么做**：卡牌效果里调用的 <c>cardFunction.*</c> 是 <c>BP_CardFunctions</c>
/// 蓝图里的函数（292 个），而 <c>CardFunctionsStub.h</c> 只声明了 190 个——
/// 剩下的在 cpp 侧根本没有声明，UHT 头文件里查不到。
/// 但它们全都在 <c>BP_CardFunctions.uasset</c> 里，<c>FunctionExport.LoadedProperties</c>
/// 带着原生 <c>CPF_OutParm</c> 标志位，比任何头文件都权威。
///
/// 同理，<c>BaseCardObject</c> 的成员也是蓝图可调用事件，签名一并从这里取。
/// </summary>
public sealed class BlueprintSignatures
{
    /// <summary>函数名 → 每个形参的修饰（"" = in，"out" = out）。</summary>
    public readonly Dictionary<string, string[]> ByFunc = new(StringComparer.Ordinal);
    /// <summary>函数名 → 形参名。</summary>
    public readonly Dictionary<string, string[]> Names = new(StringComparer.Ordinal);
    /// <summary>函数名 → 所在资产（诊断用）。</summary>
    public readonly Dictionary<string, string> Origin = new(StringComparer.Ordinal);

    public int Count => ByFunc.Count;

    /// <summary>加载一批蓝图资产，收集其中所有函数的参数修饰。</summary>
    public static BlueprintSignatures Load(Usmap usmap, IEnumerable<string> assets, Action<string> warn = null)
    {
        var s = new BlueprintSignatures();
        foreach (var f in assets)
        {
            UAsset asset;
            try { asset = new UAsset(f, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None); }
            catch (Exception ex) { warn?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); continue; }
            s.Add(asset, Path.GetFileNameWithoutExtension(f));
        }
        return s;
    }

    public void Add(UAsset asset, string origin)
    {
        foreach (var fe in asset.Exports.OfType<FunctionExport>())
        {
            var props = fe.LoadedProperties;
            var name = fe.ObjectName.ToString();

            // 无参函数也要登记（值为空数组），否则 --dump-bp 之类的诊断看不到它们，
            // 调用点也会被误判成「函数不存在」而不是「参数个数不符」。
            if (props is null || props.Length == 0)
            {
                if (!ByFunc.ContainsKey(name))
                {
                    ByFunc[name] = Array.Empty<string>();
                    Names[name] = Array.Empty<string>();
                    Origin[name] = origin;
                }
                continue;
            }

            var mods = new List<string>();
            var names = new List<string>();
            foreach (var p in props)
            {
                if (!p.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm)) continue;
                if (p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
                // 原生标志位：CPF_OutParm 且非 CPF_ConstParm 才是真 out
                mods.Add(p.PropertyFlags.HasFlag(EPropertyFlags.CPF_OutParm)
                         && !p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ConstParm) ? "out" : "");
                names.Add(p.Name.ToString());
            }
            // 先加载的优先：BP_CardFunctions 先于别处加载时，同名以它为准
            if (!ByFunc.ContainsKey(name))
            {
                ByFunc[name] = mods.ToArray();
                Names[name] = names.ToArray();
                Origin[name] = origin;
            }
        }
    }

    /// <summary>查修饰；未收录或个数不符返回 null。</summary>
    public string[] Mods(string func, int argCount)
    {
        if (!ByFunc.TryGetValue(func, out var m)) return null;
        return m.Length == argCount ? m : null;
    }

    /// <summary>函数名是否已登记（不论参数个数）。</summary>
    public bool Has(string func) => func is not null && ByFunc.ContainsKey(func);
}
