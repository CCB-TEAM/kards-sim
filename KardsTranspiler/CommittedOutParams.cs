namespace KardsTranspiler;

/// <summary>
/// out 形参补充表（**从已入库的转译产物反推**）—— 由脚本生成，请勿手改。
///
/// <para>
/// <b>为什么需要</b>：重新生成需要 <c>--sigdeps</c> 指向 <c>BP_GameState_Battle</c> /
/// <c>Library/*</c> 等卡牌目录之外的蓝图。仓库里没有这些 uasset（只在 <c>_input/Cards</c>
/// 与 <c>_input/Logic</c> 里），于是 <c>GameStateRef.&lt;谓词&gt;(out x)</c> 这一族调用
/// 判不出 out，实参被当成 in —— 被调方写不回来，整条效果静默失效
/// （<c>GetAllCardInBattle</c>、<c>IsThereGameplayRestriction</c> 都是）。
/// </para>
///
/// <para>
/// <b>推导依据</b>：已入库的那份产物是在**资产齐全**时生成的，调用点该怎么传 out
/// 就是权威证据。扫全仓 <c>H.Call</c> 里带 <c>Val.Out(...)</c> 的实参，按
/// <c>(函数名, 形参个数)</c> 归并；同一个键的多次调用必须给出一致结果（本次 95 个键零冲突）。
/// 形参个数不含接收者（调用点 <c>Val[]</c> 的第 0 位是接收者）。
/// </para>
///
/// <para>
/// <b>只在资产签名查不到时才用</b>（见 <see cref="Emitter.ParamMods"/> 的 2d 级）：
/// 拿到完整资产之后，这一级不再命中，重新生成的结果与资产一致。
/// </para>
/// </summary>
public static class CommittedOutParams
{
    // key = "函数名|形参个数"，value = 每个形参的修饰（"" = in，"out" = out）
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        ["anyFriendlyA6M2Active|1"] = new[] { "out" },
        ["anyOrderPlayedThisTurn|1"] = new[] { "out" },
        ["ApplyDamageToMultipleCards|1"] = new[] { "out" },
        ["ApplyMakeCardRetreat|1"] = new[] { "out" },
        ["ApplySetCardsSeenByCipher|1"] = new[] { "out" },
        ["ApplyTheBuff|2"] = new[] { "", "out" },
        ["BranchOnPlatformType|1"] = new[] { "out" },
        ["CampaignAddFury|2"] = new[] { "", "out" },
        ["CampaignSetText|1"] = new[] { "out" },
        ["CheckFriendlyAirUnitsOnBoard|1"] = new[] { "out" },
        ["CheckFriendlyUnitsOnBoard|1"] = new[] { "out" },
        ["CompareHandCards|1"] = new[] { "out" },
        ["CompleteAchievements|1"] = new[] { "out" },
        ["CountFriendlyGuardUnits|1"] = new[] { "out" },
        ["CreateLocationNumberGapForCard|1"] = new[] { "out" },
        ["CreateStartingHandCards|2"] = new[] { "", "out" },
        ["didPlayBritishInfantryLastTurn|1"] = new[] { "out" },
        ["DisableOtherFriendly7th|2"] = new[] { "", "out" },
        ["doIControl3opCostUnit|1"] = new[] { "out" },
        ["ExecuteEndOfTurnQueue|1"] = new[] { "out" },
        ["findPairOfUnits|1"] = new[] { "out" },
        ["get_adjacent_japan_units_count|1"] = new[] { "out" },
        ["get_adjacent_unit_count|1"] = new[] { "out" },
        ["GetActiveGotchasOrdered|1"] = new[] { "out" },
        ["GetAllCardInBattle|1"] = new[] { "out" },
        ["GetAllCards|1"] = new[] { "out" },
        ["GetAllUnitsOnBoard|2"] = new[] { "", "out" },
        ["getAndDecryptAttack|1"] = new[] { "out" },
        ["getAndDecryptKredit|1"] = new[] { "out" },
        ["getCardsBuffedByThisCard|1"] = new[] { "out" },
        ["GetClientSide|1"] = new[] { "out" },
        ["GetCombatKeywords|1"] = new[] { "out" },
        ["GetCustomName2Attributes|1"] = new[] { "out" },
        ["GetDecks|2"] = new[] { "", "out" },
        ["GetDefenseBuffFromAdjacentUnits|1"] = new[] { "out" },
        ["GetEnemyPlayerID|1"] = new[] { "out" },
        ["getHasShock|1"] = new[] { "out" },
        ["GetHighestBomberAttack|1"] = new[] { "out" },
        ["GetKardMap|1"] = new[] { "out" },
        ["GetLocationCardBySide|1"] = new[] { "out" },
        ["GetNewLocationNumbers|1"] = new[] { "out" },
        ["GetOpponentSide|1"] = new[] { "out" },
        ["GetOppositeSide|1"] = new[] { "out" },
        ["GetOppositeUnitsOnBoard|1"] = new[] { "out" },
        ["GetPlayFromHandDamage|2"] = new[] { "", "out" },
        ["getPossibleCardsFromStaticCards|1"] = new[] { "out" },
        ["GetProjectVersion|1"] = new[] { "out" },
        ["GetRandomCard|1"] = new[] { "out" },
        ["GetRandomKreditCombo|1"] = new[] { "out" },
        ["GetSeenCardsFromOppositeSide|1"] = new[] { "out" },
        ["getStringFromClipboard|1"] = new[] { "out" },
        ["GetTargetArrowTargetCard|1"] = new[] { "out" },
        ["getTotalAttack|1"] = new[] { "out" },
        ["getTotalDefense|1"] = new[] { "out" },
        ["getTotalHeavyArmor|1"] = new[] { "out" },
        ["getTotalKreditCost|1"] = new[] { "out" },
        ["getTotalOperationCost|1"] = new[] { "out" },
        ["GetTurnNumber|1"] = new[] { "out" },
        ["getTwoCardsFromPossibleCards|1"] = new[] { "out" },
        ["getUnpinnedEnemyUnits|1"] = new[] { "out" },
        ["getUnseenCardsOppositeSide|1"] = new[] { "out" },
        ["GetUSAUnitsAndKredits|1"] = new[] { "out" },
        ["GiveCredits|2"] = new[] { "", "out" },
        ["GiveMobilizeBonus|2"] = new[] { "", "out" },
        ["hasGuardAdjacentUnit|1"] = new[] { "out" },
        ["HasIntel|1"] = new[] { "out" },
        ["IsActionProcess|1"] = new[] { "out" },
        ["IsDamaged|1"] = new[] { "out" },
        ["isFirstPlayerToAct|2"] = new[] { "", "out" },
        ["IsFrontlineLimited|1"] = new[] { "out" },
        ["IsLocalClientTurn|1"] = new[] { "out" },
        ["IsLocatedInDeck|1"] = new[] { "out" },
        ["IsLocatedInHand|1"] = new[] { "out" },
        ["IsLocatedOnBoard|1"] = new[] { "out" },
        ["IsTestModeActive|1"] = new[] { "out" },
        ["IsUnrevealedCovertCard|1"] = new[] { "out" },
        ["IsVeteran|2"] = new[] { "", "out" },
        ["LoadAudioSettings|1"] = new[] { "out" },
        ["LoadOtherSettings|1"] = new[] { "out" },
        ["MoveCards|1"] = new[] { "out" },
        ["MyHandLocation|2"] = new[] { "", "out" },
        ["PickRandomCard|1"] = new[] { "out" },
        ["provideKeysAndFrameCount|1"] = new[] { "out" },
        ["RefreshSelectedCard|1"] = new[] { "out" },
        ["RemoveTheBuff|2"] = new[] { "", "out" },
        ["SaveGameObject_GetAllSaveSlotFileNames|1"] = new[] { "out" },
        ["SetApplicationScale|1"] = new[] { "out" },
        ["ShouldGotchaTrigger|2"] = new[] { "", "out" },
        ["ShouldSkipAnimationsAndSetupBoard|1"] = new[] { "out" },
        ["SortCardsByLocationNumber|1"] = new[] { "out" },
        ["spawnCardFunctions|1"] = new[] { "out" },
        ["SpawnShermanInHand|1"] = new[] { "out" },
        ["SpawnUnitsWithKreditCombo|1"] = new[] { "out" },
        ["TriggerMultipleDeploymentEffects|1"] = new[] { "out" },
        ["WhichChooseOne|1"] = new[] { "out" },
    };

    /// <summary>查修饰；未收录或形参个数对不上返回 null。</summary>
    public static string[] Mods(string func, int argCount)
    {
        if (func is null) return null;
        return Table.TryGetValue($"{func}|{argCount}", out var m) ? m : null;
    }

    public static int Count => Table.Count;
}
