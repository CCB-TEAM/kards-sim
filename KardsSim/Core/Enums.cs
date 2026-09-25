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

public enum CardType { Order = 0, Infantry = 1, Tank = 2, Fighter = 3, Bomber = 4, Artillery = 5, Location = 6, Gotcha = 7 }

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
}

/// <summary>玩家可用的动作种类。</summary>
public enum ActionType
{
    EndTurn, PlayCard, Attack, MoveToFrontline, MoveToSupport, UseAbility, Mulligan
}

/// <summary>
/// 引擎内部产生的、会被触发器观察的事件。
/// 注意：动作（玩家意图）叫 <see cref="Engine.GameAction"/>，这里是被观察到的事实，两者不同。
/// </summary>
public enum GameEvent
{
    None, PlayCard, Attack, Move, Destroy, Damage, Draw, Spawn, Heal, Buff,
}