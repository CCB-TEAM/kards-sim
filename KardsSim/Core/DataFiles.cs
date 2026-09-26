namespace KardsSim.Core;

/// <summary>
/// 运行时数据文件（cards.json / deckcodes.json / decks.json）的定位。
///
/// <para>
/// 三个文件都放在仓库根，和 exe 不在同一层：从 bin/Release/net10.0 往上找几层
/// 才能碰到。开发时（dotnet run / bin 目录下直接跑）与把 exe 拷出去单跑，
/// 两种情况的相对深度不一样，所以统一「先看 exe 旁边，再逐级往上」。
/// </para>
/// </summary>
public static class DataFiles
{
    /// <summary>找数据文件；找不到返回 null。</summary>
    public static string Find(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;
        var walk = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && walk != null; i++)
        {
            var p = Path.Combine(walk, name);
            if (File.Exists(p)) return p;
            walk = Path.GetDirectoryName(walk);
        }
        return null;
    }

    /// <summary>找数据文件；找不到抛异常（调用方需要一个明确的失败原因）。</summary>
    public static string Require(string name, string hint)
    {
        var p = Find(name);
        if (p == null)
            throw new FileNotFoundException($"找不到 {name}。{hint}（在 exe 所在目录及其上 6 层内查找）");
        return p;
    }
}
