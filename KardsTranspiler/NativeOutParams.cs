namespace KardsTranspiler;

/// <summary>
/// 原生函数 out 形参补充表 —— <b>由调用点证据自动推导</b>，请勿手改。
///
/// <para><b>为什么需要这张表</b>：转译器判 out 有三条来源（本资产 LoadedProperties、
/// 蓝图资产签名、UHT 头文件）。三者都查不到时只能按 in 处理，而 out 实参在调用点
/// 是一个裸局部变量，被调方写进去的值传不回来 —— 效果「跑了但什么都没发生」。</para>
///
/// <para><b>推导依据</b>：UE 给 out 实参传的裸局部变量固定命名为
/// <c>CallFunc_&lt;调用的函数名&gt;_&lt;形参名&gt;</c>，而 in 实参的局部变量名来自
/// 产生它的上一个节点。所以一次调用里「末尾连续若干个、名字带本次函数名前缀的
/// 裸局部变量」就是这次调用的 out 参数。</para>
///
/// <para><b>只在调用点没有 Val.Out 时统计</b>，所以已判对的签名不会被覆盖。</para>
///
/// <para><b>防误判</b>：一个 (函数, 形参个数) 的所有调用点必须给出一致的 out 个数，
/// 否则整体丢弃。这一条挡的是「上一个调用的 out 又被当成下一个调用的 in」这类同名巧合。</para>
///
/// <para><b>形参个数口径</b>：不含接收者。调用点在 <c>Val[]</c> 的第 0 位放了接收者，
/// 而 <see cref="Emitter"/> 是按 AST 形参数传进来的，两者差 1。</para>
/// </summary>
public static class NativeOutParams
{
    // key = "函数名|形参个数"，value = 每个形参的修饰（"" = in，"out" = out）
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        ["Array_Random|3"] = new[] { "", "out", "out" },
        ["BranchOnPlatformType|1"] = new[] { "out" },
        ["BreakDateTime|8"] = new[] { "", "out", "out", "out", "out", "out", "out", "out" },
        ["BreakTimespan2|6"] = new[] { "", "out", "out", "out", "out", "out" },
        ["BreakVector|4"] = new[] { "", "out", "out", "out" },
        ["BreakVector2D|3"] = new[] { "", "out", "out" },
        ["CreateQuantityItem|3"] = new[] { "", "", "out" },
        ["DiscardAllMarkedCards|1"] = new[] { "out" },
        ["EnumCompareCardLocation|3"] = new[] { "", "", "out" },
        ["EnumCompareFaction|3"] = new[] { "", "", "out" },
        ["EnumCompareSide|3"] = new[] { "", "", "out" },
        ["EnumCompareType|3"] = new[] { "", "", "out" },
        ["FindCardCoordinatesInLocation|6"] = new[] { "", "", "", "out", "out", "out" },
        ["GetAllActorsOfClass|3"] = new[] { "", "", "out" },
        ["GetAllActorsWithTag|3"] = new[] { "", "", "out" },
        ["GetBattleHUD|1"] = new[] { "out" },
        ["GetBattlePass|1"] = new[] { "out" },
        ["GetCampaignObjectiveWidget|1"] = new[] { "out" },
        ["GetCampaignStrategy|1"] = new[] { "out" },
        ["GetCard|1"] = new[] { "out" },
        ["GetCardInfo|4"] = new[] { "", "out", "out", "out" },
        ["GetCardPackInfo|8"] = new[] { "", "out", "out", "out", "out", "out", "out", "out" },
        ["GetCardsByVisualLocation|2"] = new[] { "", "out" },
        ["GetClientSideInfo|1"] = new[] { "out" },
        ["GetCultureStringForLanguage|2"] = new[] { "", "out" },
        ["GetDataTableRowNames|2"] = new[] { "", "out" },
        ["GetDebugWidgetElements|2"] = new[] { "out", "out" },
        ["GetDeckCode|7"] = new[] { "", "", "", "", "out", "out", "out" },
        ["GetDeckInfoFromString|8"] = new[] { "", "", "out", "out", "out", "out", "out", "out" },
        ["GetDefaultFocusWidget|1"] = new[] { "out" },
        ["GetDataTableRowFromName|3"] = new[] { "", "", "out" },
        ["GetDetails|2"] = new[] { "out", "out" },
        ["GetDetailsImage|2"] = new[] { "out", "out" },
        ["GetEncryptionKey|1"] = new[] { "out" },
        ["GetGraphicsSettings|1"] = new[] { "out" },
        ["GetInfo|3"] = new[] { "", "out", "out" },
        ["GetInputTouchState|4"] = new[] { "", "out", "out", "out" },
        ["GetItems|1"] = new[] { "out" },
        ["GetJSONArray|3"] = new[] { "", "out", "out" },
        ["GetKardMap|1"] = new[] { "out" },
        ["getKreditBySide|2"] = new[] { "", "out" },
        ["getKreditSlotBySide|2"] = new[] { "", "out" },
        ["GetMatchCardsAsJsonString|1"] = new[] { "out" },
        ["getMaxPossibleKredits|1"] = new[] { "out" },
        ["GetNextAction|2"] = new[] { "out", "out" },
        ["GetOtherSideInfo|1"] = new[] { "out" },
        ["GetPackAmountsInOffer|2"] = new[] { "out", "out" },
        ["GetPrimaryAssetIdList|2"] = new[] { "", "out" },
        ["GetProjectVersion|1"] = new[] { "out" },
        ["GetRandomCardInfo|9"] = new[] { "", "out", "out", "out", "out", "out", "out", "out", "out" },
        ["GetSettingsSidebarWidget|1"] = new[] { "out" },
        ["getSkirmishBP|1"] = new[] { "out" },
        ["GetStaticCampaignName|2"] = new[] { "", "out" },
        ["GetStaticCardSet|2"] = new[] { "", "out" },
        ["GetStaticExileFaction|2"] = new[] { "", "out" },
        ["GetStaticFaction|2"] = new[] { "", "out" },
        ["GetStaticImage|2"] = new[] { "", "out" },
        ["GetStaticKredits|2"] = new[] { "", "out" },
        ["GetStaticRarity|2"] = new[] { "", "out" },
        ["GetStaticText|2"] = new[] { "", "out" },
        ["GetStaticTitle|2"] = new[] { "", "out" },
        ["GetStaticType|2"] = new[] { "", "out" },
        ["getStringFromClipboard|1"] = new[] { "out" },
        ["GetText|2"] = new[] { "out", "out" },
        ["GetThumbnailImage|2"] = new[] { "out", "out" },
        ["GetVisualBoardCardCardFromID|3"] = new[] { "", "out", "out" },
        ["GetVisualCardFromID|3"] = new[] { "", "out", "out" },
        ["GetVisualChooseOneBeingPlayedFromID|3"] = new[] { "", "out", "out" },
        ["GetVisualHandCardFromID|3"] = new[] { "", "out", "out" },
        ["HadBattlePassOnDate|2"] = new[] { "", "out" },
        ["IsCharacterInNameValid|2"] = new[] { "", "out" },
        ["IsReconnectMatch|3"] = new[] { "out", "out", "out" },
        ["IsRecruitMissionsActive|1"] = new[] { "out" },
        ["IsWebsocketNotifications|1"] = new[] { "out" },
        ["LoadAudioSettings|4"] = new[] { "out", "out", "out", "out" },
        ["LoadOtherSettings|2"] = new[] { "out", "out" },
        ["LocalToViewport|5"] = new[] { "", "", "", "out", "out" },
        ["Map_Keys|2"] = new[] { "", "out" },
        ["Map_Values|2"] = new[] { "", "out" },
        ["MinOfIntArray|3"] = new[] { "", "out", "out" },
        ["NotifyCheckCardBlacklisted|2"] = new[] { "", "out" },
        ["NotifyCheckCardReserved|2"] = new[] { "", "out" },
        ["NotifyShowTutorialMessage|4"] = new[] { "", "", "", "out" },
        ["OnActorStartDrag|1"] = new[] { "out" },
        ["OnBackInput|2"] = new[] { "out", "out" },
        ["PopupCreate|2"] = new[] { "", "out" },
        ["PopupOpen|4"] = new[] { "", "", "", "out" },
        ["SaveGameObject_GetAllSaveSlotFileNames|1"] = new[] { "out" },
        ["Set_BP_Logic_Receiver|1"] = new[] { "out" },
        ["Set_ToArray|2"] = new[] { "", "out" },
        ["SetApplicationScale|1"] = new[] { "out" },
        ["SetCampaignReceiver|1"] = new[] { "out" },
        ["SetDailyMissionsReceiver|1"] = new[] { "out" },
        ["SetOnlineMatchReceiver|1"] = new[] { "out" },
        ["SetPlayerMovesReceiver|1"] = new[] { "out" },
        ["SetScrollOffset|1"] = new[] { "out" },
    };

    /// <summary>查修饰；未收录或形参个数对不上返回 null。</summary>
    public static string[] Mods(string func, int argCount)
    {
        if (func is null) return null;
        if (!Table.TryGetValue($"{func}|{argCount}", out var m)) return null;
        return m;
    }

    public static int Count => Table.Count;
}

