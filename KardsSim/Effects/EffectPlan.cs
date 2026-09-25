namespace KardsSim.Effects;

/// <summary>效果原子操作。</summary>
public enum OpKind
{
    Damage, DamageHq, Destroy, Draw, Heal, BuffAttack, BuffDefense,
    Spawn, AddKredits, Pin, Suppress, MoveToFrontline, MoveToSupport, Discard,
}

public sealed class Op
{
    public OpKind Kind;
    public int Amount;
    /// <summary>目标选择方式（相对施法者）。</summary>
    public TargetSpec Target = TargetSpec.Chosen;
    public string CardName;
    public string Raw;

    public override string ToString() => $"{Kind}({Amount})@{Target}";
}

/// <summary>效果目标的选择方式。</summary>
public enum TargetSpec
{
    /// <summary>由出牌方指定（NeedsDeployTarget）。</summary>
    Chosen,
    Self,
    EnemyUnit,
    EnemyHq,
    OwnHq,
    AllEnemyUnits,
    AllFriendlyUnits,
    AllUnits,
    RandomEnemyUnit,
    /// <summary>由卡面文案里点名的牌（spawn 等）。</summary>
    Named,
}

/// <summary>一张牌的效果计划：触发点 → 操作序列。</summary>
public sealed class EffectPlan
{
    public readonly Dictionary<Core.Trigger, List<Op>> ByTrigger = new();
    /// <summary>常驻效果（光环），在查询时生效。</summary>
    public readonly List<Op> Passives = new();
    /// <summary>认得出来是文案但没解析成操作的子句，用于评估覆盖率。</summary>
    public readonly List<string> Unhandled = new();

    public List<Op> For(Core.Trigger t)
        => ByTrigger.TryGetValue(t, out var l) ? l : null;

    public bool Any => ByTrigger.Count > 0 || Passives.Count > 0;
}