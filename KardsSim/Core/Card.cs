namespace KardsSim.Core;

/// <summary>
/// 对局中的一张牌实例。数值（attack/defense）是可变的，因为会被 buff/伤害改动；
/// 而 <see cref="Def"/> 里的原始值始终不变，用于计算 TotalAttack/TotalDefense。
/// </summary>
public sealed class Card
{
    public int InstanceId;
    public CardDef Def;
    public Side Owner;
    public Loc Loc = Loc.Deck;

    public int Attack;
    public int Defense;
    public int MaxDefense;
    public int Range;
    public int KreditCost;
    public Kw Keywords;
    public int HeavyArmor;

    /// <summary>本回合是否已攻击过。</summary>
    public bool AttackedThisTurn;
    /// <summary>召唤失调：本回合刚进场，不能攻击（Blitz 除外）。</summary>
    public bool SummonedThisTurn;

    /// <summary>
    /// 进场时的回合号。客户端规则库用它判召唤失调：
    /// <c>currentTurn == enterPlayOnTurn &amp;&amp; !getHasBlitz(...)</c>。
    ///
    /// <para>
    /// 0 表示「不在场上过」（还在牌库/手牌）。不回填这个字段的话它读出 0，
    /// 而 <c>currentTurn</c> 从 1 起，比较结果恒为不等 → 召唤失调判定永远不成立，
    /// 刚出的牌当回合就能打。
    /// </para>
    /// </summary>
    public int EnterPlayTurn;
    /// <summary>本回合是否已移动过（每张牌每回合只能移动一次）。</summary>
    public bool MovedThisTurn;

    public bool Pinned;
    public bool Suppressed;
    public bool Veteran;
    public bool Covert;
    public bool Destroyed;
    /// <summary>单位所处行：true = 前线（共享格），false = 支援线。</summary>
    public bool OnFrontline;

    /// <summary>
    /// 行内位置序号。客户端用它算「相邻」（<c>GetAdjacentCards</c> 比较前后一号），
    /// Guard 的掩护范围就是按这个邻接算的 —— 只保护紧邻的牌，不是整行。
    /// 引擎自己的 <see cref="Loc"/> 没有位置概念，所以这个字段是给客户端规则用的。
    /// </summary>
    public int LocationNumber;

    /// <summary>
    /// 是否正被 Guard 单位掩护。由客户端 <c>BP_CardFunctions.UpdateGuarded</c> 维护
    /// （它按 location 遍历、按 LocationNumber 判邻接），引擎不自己算。
    /// 客户端规则库 CanAttack 直接读这个字段。
    /// </summary>
    public bool IsBeingGuarded;

    /// <summary>部署效果要选择的目标（由出牌方在 PlayCard 里指定）。</summary>
    public int ChosenTargetId = -1;

    public string Id => Def?.Id;
    public string Name => Def?.Name;
    public CardType Type => Def?.Type ?? CardType.Order;
    public bool IsUnit => Def?.IsUnit ?? false;
    public bool IsLocation => Def?.IsLocation ?? false;
    public bool IsOrder => Def?.IsOrder ?? false;

    public bool Has(Kw k) => (Keywords & k) != 0;

    /// <summary>有效攻击力：被压制/钉住时为 0。</summary>
    public int TotalAttack => (Pinned || Suppressed) ? 0 : Math.Max(0, Attack + BuffAttack);

    /// <summary>有效防御力。</summary>
    public int TotalDefense => Math.Max(0, Defense + BuffDefense);

    /// <summary>装备/光环带来的额外攻防。卡牌自身的 attack/defense 只是基础值。</summary>
    public int BuffAttack;
    public int BuffDefense;

    /// <summary>
    /// 攻击力加成<b>按来源记账</b>：来源卡的 InstanceId → 它加了多少攻击。
    ///
    /// <para>
    /// 客户端把这份账存在卡上的 <c>buffsFromCards</c> 映射里（键就是 instigatorID），
    /// <c>ChangeBuffsFromCards</c> 全靠它做增减，<c>isBuffedByCard(card, instigatorID)</c>
    /// 也是查这张表。引擎以前只记 <see cref="BuffAttack"/> 总额，于是：
    /// </para>
    /// <list type="bullet">
    ///   <item>「撤销某个来源的加成」只能粗暴地把总额清零 —— 两个光环叠加时先走的那张
    ///         会把另一张的加成一起抹掉；</item>
    ///   <item><c>isBuffedByCard</c> 只能用「总额非零」近似 —— 一张已经被别的来源
    ///         buff 过的卡，不会再被新光环加成（<c>!isBuffedByCard(...)</c> 守卫提前退出）。</item>
    /// </list>
    /// <para>
    /// 归类为「无来源」（instigatorID ≤ 0）的加成记在 <see cref="BuffAttackFromNoSource"/>。
    /// </para>
    /// </summary>
    public Dictionary<int, int> AttackBuffBySource = new();

    /// <summary>instigatorID ≤ 0（匿名加成）累计出来的攻击加成。</summary>
    public int BuffAttackFromNoSource;

    /// <summary>
    /// Intel（情报）值：打出本卡时随机翻开对手手牌的张数。
    /// 客户端存在 <c>cipher</c> 字段里（AddIntelToCard 会把它 clamp 到 0..9），
    /// 所以这里沿用同一个名字，语义才能和直译产物对齐。
    /// </summary>
    public int Cipher;

    /// <summary>
    /// 费用上的临时增减（蓝图里的 kreditBuff）。客户端把它加密存着防内存修改，
    /// 无头模拟直接读明文。与 <see cref="KreditCost"/> 分开存，因为
    /// 「基础费用」和「本回合的费用修正」在判定时用法不同。
    /// </summary>
    public int BuffCost;

    /// <summary>
    /// 反制指令（Gotcha）的激活序号：**> 0 表示已激活**，客户端拿它当
    /// <c>activeGotchas</c> 映射的排序键（先激活的序号小）。
    ///
    /// <para>
    /// 字段名照抄客户端（<c>gotchaActivated</c>，int 不是 bool）。以前宿主读的是一个
    /// 谁都不写的 <c>gotcha</c> 布尔字段，于是 50 张反制指令永远不触发。
    /// 客户端只在 UI 侧赋正值（取消激活时写 0），无头模拟里由引擎在打出时激活。
    /// </para>
    /// </summary>
    public int GotchaActivated;

    public bool CanAct => !Pinned && !Suppressed && !Destroyed;

    public Card Clone()
    {
        var c = (Card)MemberwiseClone();
        c.AttackBuffBySource = new Dictionary<int, int>(AttackBuffBySource);
        return c;
    }

    public override string ToString() => $"{Id}#{InstanceId}";
}