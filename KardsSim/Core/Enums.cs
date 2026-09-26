namespace KardsSim.Core;

/// <summary>对局双方。数值与 ESideEnum 对齐（None=0, Left=1, Right=2）。</summary>
public enum Side { None = 0, Left = 1, Right = 2 }

/// <summary>卡牌可处的位置。数值与 ECardLocationEnum 对齐。</summary>
public enum Loc
{
    None = 0,
    Deck = 1,
    Hand = 2,
    Board = 3,
    Discard = 4,
    Removed = 5,
    /// <summary>前线是双方共享的一个格子（Board_Frontline=7），归属由第一张牌决定。</summary>
    Frontline = 7,
}

/// <summary>
/// 卡牌类型。
///
/// <para>
/// <b>注意：这套数值与客户端的 <c>ETypeEnum</c> 不是一回事</b>
/// （客户端是 location=1, order=2, tank=3, fighter=4, bomber=5, infantry=6, artillery=7 …）。
/// 直译产物里读镜像 <c>type</c> 的地方按客户端编号比较，所以写镜像时必须过
/// <c>EngineHost.ClientType()</c> 换算，不能直接写这里的值。
/// </para>
/// </summary>
public enum CardType { Order = 0, Infantry = 1, Tank = 2, Fighter = 3, Bomber = 4, Artillery = 5, Location = 6, Gotcha = 7, AntiAir = 8 }

public enum Faction { Neutral, Germany, Soviet, Japan, Britain, USA, France, Italy, Poland, Finland, Anzac }

public enum Rarity { Common, Uncommon, Rare, Elite }

/// <summary>卡面关键字。来自 CDO 的 has* 布尔字段。</summary>
public enum Kw
{
    None = 0,
    Guard = 1 << 0,
    Blitz = 1 << 1,
    Smokescreen = 1 << 2,
    Ambush = 1 << 3,
    Mobilize = 1 << 4,
    HeavyArmor = 1 << 5,
    Deployment = 1 << 6,
    Veteran = 1 << 7,
    Pinned = 1 << 8,
    Suppressed = 1 << 9,
    Fury = 1 << 10,
    Covert = 1 << 11,
    Bond = 1 << 12,
    Develop = 1 << 13,
    Forecast = 1 << 14,
    Scrying = 1 << 15,
    Salvage = 1 << 16,
    /// <summary>Shock（休克）：命中时压制目标。CDO 字段 hasShock，31 张卡带它。</summary>
    Shock = 1 << 17,
    /// <summary>Immune（免疫）：不吃伤害。CDO 里没有静态标志位，只能被效果动态赋予。</summary>
    Immune = 1 << 18,
    /// <summary>Pincer（钳形）：与配对的单位一起触发额外效果。CDO 字段 hasPincer，15 张卡带它。</summary>
    Pincer = 1 << 19,
}

/// <summary>玩家可用的动作种类。</summary>
public enum ActionType
{
    EndTurn, PlayCard, Attack, MoveToFrontline, MoveToSupport, UseAbility, Mulligan,
    /// <summary>
    /// 结算一个「待决选择」（二段式抉择）：效果跑到一半要求玩家选一张牌时，
    /// 动作列表里只剩这个类型的选项。见 <see cref="Engine.GameEngine.Pending"/>。
    /// </summary>
    ChooseCard,
}

/// <summary>
/// 引擎内部产生的、会被触发器观察的事件。
/// 注意：动作（玩家意图）叫 <see cref="Engine.GameAction"/>，这里是被观察到的事实，两者不同。
/// </summary>
public enum GameEvent
{
    None, PlayCard, Attack, Move, Destroy, Damage, Draw, Spawn, Heal, Buff,
}