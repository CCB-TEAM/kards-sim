namespace KardsSim.Core;

/// <summary>
/// DataTable 注册表。
///
/// <para>
/// 无头模拟不加载 UDataTable 资产，但卡牌逻辑里有几处会去查表 —— 其中
/// <c>card_event_mass_deployment</c> 的 <c>GetRandomKreditCombo</c> 是「随机取一个组合，
/// 取不到就重试」，表查不到时那个循环<b>永不退出</b>，会把整个自对弈挂死。
/// 所以这些表必须有真实内容，不能返回空行。
/// </para>
///
/// <para>
/// 表内容由导出资产核对过：<c>kreditCombinationsUSUnits</c> 的 22 行
/// （行名 "3".."24"）逐一比对，行数与内容都一致（见 <see cref="KreditCombinations"/>）。
/// 只被 UI / 平台函数引用的表（DT_CardImages / RankLevelDataTable /
/// AlternativeCardsDataTable 等）不在这里，返回「查不到」是正确行为。
/// </para>
/// </summary>
public static class DataTables
{
    /// <summary>行内容：字段名 → 值（值可以是 int / string / List&lt;int[]&gt; 之类）。</summary>
    public delegate bool RowLookup(string rowName, out Dictionary<string, object> row);

    private static readonly Dictionary<string, RowLookup> Tables = new(StringComparer.Ordinal)
    {
        ["kreditCombinationsUSUnits"] = KreditCombinations,
    };

    public static bool TryGetRow(string table, string rowName, out Dictionary<string, object> row)
    {
        row = null;
        return table is not null && rowName is not null
               && Tables.TryGetValue(table, out var f) && f(rowName, out row);
    }

    /// <summary>
    /// <c>kreditCombinationsUSUnits</c>：把 N 点克redit拆成 3 个正整数（单位费用）的所有组合。
    ///
    /// <para>
    /// 行名 = N（"3".."24"），字段 <c>combos</c> = <c>TArray&lt;FVector&gt;</c>，
    /// 每个元素是一个三元组，X+Y+Z = N，且 X ≥ Y ≥ Z ≥ 1；
    /// 顺序按 X 降序、再按 Y 降序 —— 与导出资产逐行一致。
    /// </para>
    ///
    /// <para>
    /// 这份数据是**算出来的**而不是抄的：导出资产 22 行的行数与
    /// 「整数三分拆数 p₃(N)=round(N²/12)」完全吻合（1,1,2,3,4,5,7,8,10,12,14,16,
    /// 19,21,24,27,30,33,37,40,44,48），<c>--mode tests</c> 里有断言守着。
    /// </para>
    /// </summary>
    private static bool KreditCombinations(string rowName, out Dictionary<string, object> row)
    {
        row = null;
        if (!int.TryParse(rowName, out var n)) return false;

        var combos = new List<int[]>();
        for (var x = n - 2; x >= 1; x--)
            for (var y = Math.Min(x, n - x - 1); y >= 1; y--)
            {
                var z = n - x - y;
                if (z < 1 || z > y) continue;
                combos.Add(new[] { x, y, z });
            }

        // 转译产物读的是带属性 GUID 后缀的字段名（来自行结构 vector3List），
        // 所以两个键都写：原名照抄，另给一个短名方便别处引用。
        row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["combos"] = combos,
            ["combos_4_561CC77D4BB75053570144B4E1CA91A4"] = combos,
        };
        return true;
    }

    /// <summary>注册表里的表名（给审计用）。</summary>
    public static IEnumerable<string> Names => Tables.Keys;
}
