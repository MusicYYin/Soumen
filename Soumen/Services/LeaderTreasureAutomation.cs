using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Soumen.Models;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Soumen.Services;

public sealed class LeaderTreasureAutomation : IDisposable
{
    private const uint HypnoslotNameRowId = 2014790;
    private const ushort LimsaLominsaLowerDecksTerritoryId = 129;
    private static readonly uint[] MarketBoardDataIds = [2000402, 2000442];
    private const float ObjectApproachRange = 3.2f;
    // vnavmesh stops about 3.3y from Limsa's board; the game accepts interaction there.
    private const float MarketBoardInteractionRange = 4f;
    private const float OutdoorObjectSearchRange = 55f;

    private static readonly InventoryType[] MainInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractionRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObjectNavigationRetryInterval = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan HigherLowerRetryInterval = TimeSpan.FromMilliseconds(450);
    private static readonly (bool UpdateState, int Argument)[] HigherLowerCloseAttempts =
    [
        (true, 1),
        (false, -2),
        (true, 1),
        (true, -2),
        (true, 1),
        (true, -1),
        (false, -1),
    ];

    private readonly Configuration configuration;
    private readonly MapFlagAutomation mapAutomation;
    private readonly DiagnosticLogger diagnostics;
    private readonly VNavmeshIpc vnavmesh;
    private readonly TeleportService teleportService;
    private readonly MarketMapPurchaseService marketPurchase;
    private readonly Dictionary<uint, HashSet<uint>> progressionObjectBaseIds = [];
    private readonly Dictionary<uint, HashSet<string>> progressionObjectNames = [];

    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime stateEnteredUtc = DateTime.UtcNow;
    private DateTime lastActionUtc = DateTime.MinValue;
    private DateTime lastInteractionUtc = DateTime.MinValue;
    private DateTime lastObjectNavigationUtc = DateTime.MinValue;
    private DateTime noTreasureEnemySinceUtc = DateTime.MinValue;
    private DateTime pendingYesUntilUtc = DateTime.MinValue;
    private DateTime portalSearchStartedUtc = DateTime.MinValue;
    private DateTime restockArrivedUtc = DateTime.MinValue;
    private DateTime restockTeleportSubmittedUtc = DateTime.MinValue;
    private DateTime marketTravelStartedUtc = DateTime.MinValue;
    private DateTime nextRestockRetryUtc = DateTime.MinValue;
    private DateTime nextHigherLowerActionUtc = DateTime.MinValue;
    private readonly HashSet<uint> preDigChestIds = [];
    private readonly Dictionary<uint, DateTime> dungeonInteractionTimes = [];
    private MapFlagTarget? currentOwnTarget;
    private uint trackedChestEntityId;
    private Vector3 trackedChestPosition;
    private nint navigationObjectAddress;
    private bool ownsNavigation;
    private bool decipherMenuSelectionIssued;
    private int decipherInventoryCountBefore;
    private int higherLowerAttemptIndex;
    private int restockFailureCount;
    private uint restockSpentGil;
    private bool preferInventoryMap;
    private TreasureMapProfile? decodedMapProfile;
    private bool decodedInventoryLoaded;
    private RestockStage restockStage;
    private bool disposed;
    private DeveloperTestKind developerTest;
    private bool developerTestHold;

    public LeaderTreasureAutomation(
        Configuration configuration,
        MapFlagAutomation mapAutomation,
        DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.mapAutomation = mapAutomation;
        this.diagnostics = diagnostics;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface, diagnostics);
        teleportService = new TeleportService(diagnostics);
        marketPurchase = new MarketMapPurchaseService(diagnostics);
        LoadDungeonProgressionObjects();
        mapAutomation.DestinationReached += OnDestinationReached;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public LeaderAutomationState State { get; private set; } = LeaderAutomationState.Inactive;

    public string StatusText { get; private set; } = "跟车模式";

    public string SelectedMapName => GetSelectedMapName();

    public TreasureMapProfile SelectedMap => TreasureMapCatalog.Get(configuration.LeaderTreasureMapItemId);

    public string SelectedMapLabel => $"{SelectedMap.GradeLabel} · {SelectedMapName}";

    public int DecodedMapCount { get; private set; }

    public int InventoryMapCount { get; private set; }

    public string DeveloperTestResult { get; private set; } = "未运行功能测试";

    public bool IsDeveloperTestRunning => developerTest != DeveloperTestKind.None;

    public void TestMarketPurchase()
    {
        if (developerTest != DeveloperTestKind.None)
        {
            return;
        }

        if (!SelectedMap.CanMarketRestock || InventoryMapCount > 0 || TreasureContext.IsTreasureDungeon())
        {
            DeveloperTestResult = "请选择可购买的图，并确认背包没有同款地图且人不在宝物库。";
            return;
        }

        mapAutomation.Stop("功能测试：自动买图");
        developerTestHold = false;
        developerTest = DeveloperTestKind.Market;
        DeveloperTestResult = "正在前往海都、打开市场板并购买一张图…";
        restockStage = RestockStage.BuyFirst;
        restockFailureCount = 0;
        marketPurchase.Reset();
        PrepareRestockTravel("功能测试：前往海都市场板");
        diagnostics.Write("功能测试", $"开始自动买图：{SelectedMapName}，最高单价 {configuration.LeaderMapMaximumUnitPrice} Gil。");
    }

    public void TestDecipher()
    {
        if (developerTest != DeveloperTestKind.None)
        {
            return;
        }

        if (InventoryMapCount == 0 || DecodedMapCount > 0 || TreasureContext.IsTreasureDungeon())
        {
            DeveloperTestResult = "需要背包里有选定的未解读藏宝图，且任务道具里没有已解读地图。";
            return;
        }

        mapAutomation.Stop("功能测试：解读地图");
        developerTestHold = false;
        developerTest = DeveloperTestKind.Decipher;
        DeveloperTestResult = "正在使用解读技能，并等待地图选择和确认…";
        restockStage = RestockStage.None;
        lastActionUtc = DateTime.MinValue;
        SetState(LeaderAutomationState.LookingForMap, "功能测试：准备解读藏宝图");
        diagnostics.Write("功能测试", $"开始解读：{SelectedMapName}，背包数量 {InventoryMapCount}。");
    }

    public void CancelDeveloperTest()
    {
        if (developerTest != DeveloperTestKind.None)
        {
            FinishDeveloperTest("功能测试已取消");
        }
    }

    private void FinishDeveloperTest(string result)
    {
        StopOwnedNavigation();
        if (developerTest == DeveloperTestKind.Market)
        {
            CancelRestock();
        }

        developerTest = DeveloperTestKind.None;
        developerTestHold = true;
        DeveloperTestResult = result;
        SetState(LeaderAutomationState.Inactive, result);
        diagnostics.Write("功能测试", result);
    }

    public bool CanRetryCurrentStep
        => configuration.Enabled
            && !configuration.HuntEnabled
            && configuration.OperatingMode == OperatingMode.Leader
            && State != LeaderAutomationState.Inactive;

    public bool CanSkipCurrentStep
        => CanRetryCurrentStep
            && State != LeaderAutomationState.LookingForMap
            && !(State == LeaderAutomationState.Combat && Plugin.Condition[ConditionFlag.InCombat]);

    public void SelectMap(uint itemId)
    {
        if (!TreasureMapCatalog.Contains(itemId) || configuration.LeaderTreasureMapItemId == itemId)
        {
            return;
        }

        configuration.LeaderTreasureMapItemId = itemId;
        configuration.Save();
        Restart();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mapAutomation.DestinationReached -= OnDestinationReached;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        marketPurchase.Dispose();
        StopOwnedNavigation();
    }

    public void Restart()
    {
        developerTestHold = false;
        StopOwnedNavigation();
        ResetCycle();
        SetState(LeaderAutomationState.LookingForMap, "正在检查藏宝图");
    }

    public void RetryCurrentStep()
    {
        if (!CanRetryCurrentStep)
        {
            return;
        }

        StopOwnedNavigation();
        lastActionUtc = DateTime.MinValue;
        lastInteractionUtc = DateTime.MinValue;
        lastObjectNavigationUtc = DateTime.MinValue;
        pendingYesUntilUtc = DateTime.MinValue;
        portalSearchStartedUtc = DateTime.MinValue;
        decipherMenuSelectionIssued = false;
        dungeonInteractionTimes.Clear();

        switch (State)
        {
            case LeaderAutomationState.RestockingTravel:
                PrepareRestockTravel("正在重新前往海都市场板");
                break;
            case LeaderAutomationState.RestockingMarket:
                marketPurchase.Reset();
                if (marketPurchase.IsBoardOpen)
                {
                    StartMarketPurchase();
                }
                else
                {
                    PrepareRestockTravel("市场板已关闭，正在重新靠近");
                }
                break;
            case LeaderAutomationState.DecipheringMap:
            case LeaderAutomationState.ConfirmingDecipher:
                SetState(LeaderAutomationState.LookingForMap, "重新检查并解读藏宝图");
                break;
            case LeaderAutomationState.OpeningDecodedMap:
            case LeaderAutomationState.WaitingForFlag:
                SetState(LeaderAutomationState.OpeningDecodedMap, "重新使用已解读藏宝图并读取坐标");
                break;
            case LeaderAutomationState.Navigating:
                if (currentOwnTarget != null)
                {
                    mapAutomation.Stop("正在重新规划自己的藏宝图路线");
                    mapAutomation.NavigateTo(currentOwnTarget.Serial);
                }
                SetState(LeaderAutomationState.Navigating, "正在重新检查藏宝图路线");
                break;
            case LeaderAutomationState.Dungeon:
                SetState(LeaderAutomationState.Dungeon, "重新检测当前宝物库阶段");
                break;
            case LeaderAutomationState.Waiting:
            case LeaderAutomationState.Error:
                if (restockStage != RestockStage.None)
                {
                    marketPurchase.Reset();
                    PrepareRestockTravel("正在恢复自动补图流程");
                }
                else if (TreasureContext.IsTreasureDungeon())
                {
                    SetState(LeaderAutomationState.Dungeon, "重新检测当前宝物库阶段");
                }
                else
                {
                    SetState(LeaderAutomationState.LookingForMap, "重新检查当前流程");
                }
                break;
            default:
                SetState(State, $"重新检测：{StatusText}");
                break;
        }

        diagnostics.Write("车头", $"手动重新检测当前步骤：{State}。" );
    }

    public void SkipCurrentStep()
    {
        if (!CanSkipCurrentStep)
        {
            return;
        }

        var skippedState = State;
        StopOwnedNavigation();
        lastActionUtc = DateTime.MinValue;
        lastInteractionUtc = DateTime.MinValue;
        pendingYesUntilUtc = DateTime.MinValue;

        switch (State)
        {
            case LeaderAutomationState.RestockingTravel:
            case LeaderAutomationState.RestockingMarket:
                CancelRestock();
                SetState(LeaderAutomationState.Waiting, "已跳过本次自动补图，可重新检测后继续");
                break;
            case LeaderAutomationState.DecipheringMap:
            case LeaderAutomationState.ConfirmingDecipher:
                SetState(DecodedMapCount > 0
                        ? LeaderAutomationState.OpeningDecodedMap
                        : LeaderAutomationState.LookingForMap,
                    DecodedMapCount > 0 ? "已跳过解读，尝试使用任务道具中的藏宝图" : "已跳过当前解读步骤");
                break;
            case LeaderAutomationState.OpeningDecodedMap:
            case LeaderAutomationState.WaitingForFlag:
            case LeaderAutomationState.Navigating:
                SetState(LeaderAutomationState.WaitingForParty, "已跳过当前定位步骤，等待队伍确认");
                break;
            case LeaderAutomationState.WaitingForParty:
                SetState(LeaderAutomationState.Digging, "已跳过队伍到齐检查，准备挖掘");
                break;
            case LeaderAutomationState.Digging:
                SetState(LeaderAutomationState.ApproachingChest, "已跳过挖掘等待，开始搜索宝箱");
                break;
            case LeaderAutomationState.ApproachingChest:
            case LeaderAutomationState.WaitingForCombat:
            case LeaderAutomationState.ReopeningChest:
                SetState(LeaderAutomationState.WaitingForLootOrPortal, "已跳过当前宝箱阶段，检查战利品与传送魔纹");
                break;
            case LeaderAutomationState.Combat:
                SetState(LeaderAutomationState.ReopeningChest, "已跳过战斗等待，重新检查宝箱");
                break;
            case LeaderAutomationState.WaitingForLootOrPortal:
            case LeaderAutomationState.EnteringPortal:
                preferInventoryMap = true;
                ResetOutdoorEncounter();
                SetState(LeaderAutomationState.LookingForMap, "已跳过本张地图剩余步骤，检查下一张藏宝图");
                break;
            case LeaderAutomationState.Dungeon:
                DeferCurrentDungeonObject();
                SetState(LeaderAutomationState.Dungeon, "已跳过当前宝物库交互，重新搜索可处理目标");
                break;
            case LeaderAutomationState.Waiting:
            case LeaderAutomationState.Error:
                ResetCycle();
                SetState(LeaderAutomationState.LookingForMap, "已跳过异常步骤，重新检查藏宝图");
                break;
        }

        diagnostics.Write("车头", $"手动跳过步骤：{skippedState} -> {State}。" );
    }

    private void OnDestinationReached(MapFlagTarget target)
    {
        if (configuration.OperatingMode != OperatingMode.Leader || !target.IsOwnTreasure)
        {
            return;
        }

        currentOwnTarget = target;
        StopOwnedNavigation();
        SetState(LeaderAutomationState.WaitingForParty, "已到达藏宝图坐标，等待全队到达当前地图");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
        {
            return;
        }

        nextUpdateUtc = now + UpdateInterval;
        RefreshInventorySummary();

        if (developerTest != DeveloperTestKind.None)
        {
            ProcessDeveloperTest(now);
            return;
        }

        if (!configuration.Enabled || configuration.HuntEnabled || configuration.OperatingMode != OperatingMode.Leader)
        {
            developerTestHold = false;
        }
        else if (developerTestHold)
        {
            return;
        }

        if (!configuration.Enabled || configuration.HuntEnabled || configuration.OperatingMode != OperatingMode.Leader)
        {
            if (State != LeaderAutomationState.Inactive)
            {
                StopOwnedNavigation();
                ResetCycle();
                SetState(LeaderAutomationState.Inactive,
                    configuration.Enabled ? "跟车模式" : "Soumen 已关闭");
            }

            return;
        }

        if (mapAutomation.IsPaused)
        {
            StopOwnedNavigation();
            StatusText = "已暂停，车头流程保持当前阶段";
            return;
        }

        if (IsLoadingOrWatchingCutscene())
        {
            StopOwnedNavigation();
            StatusText = "读图或过场中，等待恢复";
            return;
        }

        if (restockStage != RestockStage.None
            && (!configuration.AutoRestockLeaderMaps || !SelectedMap.CanMarketRestock))
        {
            CancelRestock();
            SetState(LeaderAutomationState.Waiting,
                SelectedMap.CanMarketRestock ? "自动补图已关闭" : "所选藏宝图无法通过市场板补充");
            return;
        }

        if (TreasureContext.IsTreasureDungeon())
        {
            if (State != LeaderAutomationState.Dungeon)
            {
                ResetOutdoorEncounter();
                SetState(LeaderAutomationState.Dungeon, "已进入宝物库");
            }

            if (TreasureDungeonCatalog.TryGet(Plugin.ClientState.TerritoryType, out _))
            {
                ProcessDungeon(now);
            }
            else
            {
                StopOwnedNavigation();
                StatusText = "当前宝物库尚未加入车头自动流程";
            }
            return;
        }

        if (State == LeaderAutomationState.Dungeon)
        {
            preferInventoryMap = true;
            ResetCycle();
            SetState(LeaderAutomationState.LookingForMap, "已离开宝物库，检查下一张藏宝图");
        }

        switch (State)
        {
            case LeaderAutomationState.Inactive:
                SetState(LeaderAutomationState.LookingForMap, "正在检查藏宝图");
                break;
            case LeaderAutomationState.LookingForMap:
                ProcessMapAcquisition(now);
                break;
            case LeaderAutomationState.RestockingTravel:
                ProcessRestockingTravel(now);
                break;
            case LeaderAutomationState.RestockingMarket:
                ProcessRestockingMarket(now);
                break;
            case LeaderAutomationState.DecipheringMap:
                ProcessDecipherMenu(now);
                break;
            case LeaderAutomationState.ConfirmingDecipher:
                ProcessDecipherConfirmation(now);
                break;
            case LeaderAutomationState.OpeningDecodedMap:
                ProcessOpenDecodedMap(now);
                break;
            case LeaderAutomationState.WaitingForFlag:
                ProcessWaitingForFlag(now);
                break;
            case LeaderAutomationState.Navigating:
                ProcessNavigationWait();
                break;
            case LeaderAutomationState.WaitingForParty:
                ProcessPartyWait(now);
                break;
            case LeaderAutomationState.Digging:
                ProcessDigging(now);
                break;
            case LeaderAutomationState.ApproachingChest:
                ProcessApproachingChest(now);
                break;
            case LeaderAutomationState.WaitingForCombat:
                ProcessWaitingForCombat(now);
                break;
            case LeaderAutomationState.Combat:
                ProcessCombat(now);
                break;
            case LeaderAutomationState.ReopeningChest:
                ProcessReopeningChest(now);
                break;
            case LeaderAutomationState.WaitingForLootOrPortal:
                ProcessPortalOrNextMap(now);
                break;
            case LeaderAutomationState.EnteringPortal:
                ProcessEnteringPortal(now);
                break;
            case LeaderAutomationState.Waiting:
            case LeaderAutomationState.Error:
                break;
        }
    }

    private void ProcessDeveloperTest(DateTime now)
    {
        if (IsLoadingOrWatchingCutscene())
        {
            return;
        }

        if (developerTest == DeveloperTestKind.Market)
        {
            if (State == LeaderAutomationState.RestockingTravel)
            {
                ProcessRestockingTravel(now);
            }
            else if (State == LeaderAutomationState.RestockingMarket)
            {
                ProcessRestockingMarket(now);
            }
        }
        else if (developerTest == DeveloperTestKind.Decipher)
        {
            switch (State)
            {
                case LeaderAutomationState.LookingForMap:
                    BeginDecipher(now);
                    break;
                case LeaderAutomationState.DecipheringMap:
                    ProcessDecipherMenu(now);
                    break;
                case LeaderAutomationState.ConfirmingDecipher:
                    ProcessDecipherConfirmation(now);
                    break;
            }
        }

        DeveloperTestResult = StatusText;
        if (State == LeaderAutomationState.Error)
        {
            FinishDeveloperTest($"测试失败：{StatusText}");
        }
    }

    private void ProcessMapAcquisition(DateTime now)
    {
        if (!decodedInventoryLoaded)
        {
            StatusText = "等待任务道具载入后检查已解读藏宝图";
            return;
        }

        if (DecodedMapCount > 0 && (!preferInventoryMap || InventoryMapCount == 0))
        {
            if (preferInventoryMap && now - stateEnteredUtc < TimeSpan.FromSeconds(2))
            {
                StatusText = "等待上张藏宝图任务道具状态刷新";
                return;
            }

            ClearMapFlag();
            SetState(LeaderAutomationState.OpeningDecodedMap, $"正在使用已解读的 {decodedMapProfile?.GradeLabel ?? SelectedMap.GradeLabel} 藏宝图");
            lastActionUtc = DateTime.MinValue;
            return;
        }

        if (InventoryMapCount > 0)
        {
            BeginDecipher(now);
            return;
        }

        if (DecodedMapCount > 0)
        {
            StatusText = "本张地图已完成，等待已解读藏宝图状态刷新";
            return;
        }

        if (configuration.AutoRestockLeaderMaps && SelectedMap.CanMarketRestock)
        {
            BeginRestock();
            return;
        }

        SetState(LeaderAutomationState.Waiting, $"任务道具和背包中都没有可用的 {SelectedMapName}");
    }

    private unsafe void BeginDecipher(DateTime now)
    {
        if (now - lastActionUtc < ActionRetryInterval)
        {
            return;
        }

        lastActionUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            StatusText = "等待游戏动作管理器就绪后解读";
            return;
        }

        var status = actionManager->GetActionStatus(ActionType.GeneralAction, 19);
        if (status != 0)
        {
            StatusText = "目前无法使用解读技能";
            diagnostics.WriteThrottled("decipher-action-unavailable", "车头",
                $"解读技能不可用：GeneralAction#19，status={status}，背包数量={InventoryMapCount}。",
                TimeSpan.FromSeconds(5));
            return;
        }

        if (!actionManager->UseAction(ActionType.GeneralAction, 19))
        {
            StatusText = "解读技能未成功发动，正在重试";
            diagnostics.WriteThrottled("decipher-action-failed", "车头",
                "解读技能 GeneralAction#19 返回 false。", TimeSpan.FromSeconds(5));
            return;
        }

        decipherInventoryCountBefore = InventoryMapCount;
        decipherMenuSelectionIssued = false;
        SetState(LeaderAutomationState.DecipheringMap, $"正在等待 {SelectedMapName} 的解读选择菜单");
        diagnostics.Write("车头", $"已发动解读技能 GeneralAction#19，目标物品 #{configuration.LeaderTreasureMapItemId}，背包数量={InventoryMapCount}。" );
    }

    private void BeginRestock()
    {
        if (!SelectedMap.CanMarketRestock)
        {
            SetState(LeaderAutomationState.Waiting, "所选藏宝图无法通过市场板补充");
            return;
        }

        StopOwnedNavigation();
        restockStage = RestockStage.BuyFirst;
        restockFailureCount = 0;
        restockSpentGil = 0;
        marketPurchase.Reset();
        diagnostics.Write("自动补图", $"未找到可继续使用的 {SelectedMap.GradeLabel}，开始前往海都补充两张藏宝图。" );
        PrepareRestockTravel($"正在前往海都市场板购买第一张 {SelectedMap.GradeLabel}");
    }

    private void ProcessRestockingTravel(DateTime now)
    {
        if (!configuration.AutoRestockLeaderMaps && developerTest != DeveloperTestKind.Market)
        {
            CancelRestock();
            SetState(LeaderAutomationState.Waiting, "自动补图已关闭");
            return;
        }

        if (Plugin.ClientState.TerritoryType != LimsaLominsaLowerDecksTerritoryId)
        {
            restockArrivedUtc = DateTime.MinValue;
            if (restockTeleportSubmittedUtc != DateTime.MinValue)
            {
                if (now - restockTeleportSubmittedUtc > TimeSpan.FromSeconds(45))
                {
                    var currentTerritory = Plugin.ClientState.TerritoryType;
                    CancelRestock();
                    SetState(
                        LeaderAutomationState.Error,
                        $"传送后未到达海都下层甲板（当前地图 {currentTerritory}），请开启诊断模式后重试");
                }
                else
                {
                    StatusText = "已提交海都传送，等待读图完成";
                }

                return;
            }

            if (now - lastActionUtc >= TimeSpan.FromSeconds(10)
                && TeleportService.CanTeleportNow())
            {
                lastActionUtc = now;
                var limsaAetheryte = teleportService
                    .GetCandidates(LimsaLominsaLowerDecksTerritoryId)
                    .FirstOrDefault();
                if (limsaAetheryte == null)
                {
                    CancelRestock();
                    SetState(LeaderAutomationState.Error, "已解锁传送列表中没有海都下层甲板以太水晶");
                    return;
                }

                diagnostics.Write(
                    "自动补图",
                    $"动态选择海都水晶：{limsaAetheryte.Name}#{limsaAetheryte.Id}；当前地图={Plugin.ClientState.TerritoryType}，目标地图={LimsaLominsaLowerDecksTerritoryId}。" );
                if (!teleportService.Teleport(limsaAetheryte.Id))
                {
                    CancelRestock();
                    SetState(LeaderAutomationState.Error, $"无法传送到{limsaAetheryte.Name}，请确认传送状态");
                    return;
                }

                restockTeleportSubmittedUtc = now;
            }

            StatusText = "正在传送到海都下层甲板";
            return;
        }

        restockTeleportSubmittedUtc = DateTime.MinValue;
        if (restockArrivedUtc == DateTime.MinValue)
        {
            restockArrivedUtc = now;
        }

        if (marketPurchase.IsBoardOpen)
        {
            StartMarketPurchase();
            return;
        }

        if (now - restockArrivedUtc < TimeSpan.FromSeconds(2))
        {
            StatusText = "已到达海都，等待人物状态稳定";
            return;
        }

        var marketBoard = FindMarketBoard();
        if (marketBoard != null)
        {
            marketTravelStartedUtc = marketTravelStartedUtc == DateTime.MinValue ? now : marketTravelStartedUtc;
            if (now - marketTravelStartedUtc > TimeSpan.FromSeconds(35))
            {
                CancelRestock();
                SetState(LeaderAutomationState.Error, "市场布告板交互后未打开市场界面，请查看诊断日志");
                return;
            }

            if (ApproachAndOpenMarketBoard(marketBoard, now))
            {
                StatusText = "已操作海都市场布告板，等待市场界面";
            }
            return;
        }

        marketTravelStartedUtc = marketTravelStartedUtc == DateTime.MinValue ? now : marketTravelStartedUtc;
        if (now - marketTravelStartedUtc > TimeSpan.FromSeconds(30))
        {
            CancelRestock();
            SetState(LeaderAutomationState.Error, "海都市场布告板未载入，请靠近布告板后重新检查");
            return;
        }

        StatusText = "已到达海都，等待市场布告板载入";
        diagnostics.WriteThrottled(
            "market-board-not-found",
            "自动补图",
            $"暂未在物体表中找到市场布告板（DataId 2000402/2000442）；position={FormatPosition(Plugin.ObjectTable.LocalPlayer?.Position)}。",
            TimeSpan.FromSeconds(5));
    }

    private void ProcessRestockingMarket(DateTime now)
    {
        marketPurchase.Update(now);
        StatusText = marketPurchase.StatusText;

        if (marketPurchase.State == MarketMapPurchaseState.Failed)
        {
            if (InventoryMapCount > 0 && marketPurchase.PurchasedTotalPrice > 0)
            {
                CompleteMarketPurchaseStage();
                return;
            }

            if (nextRestockRetryUtc == DateTime.MinValue)
            {
                restockFailureCount++;
                nextRestockRetryUtc = now + TimeSpan.FromSeconds(10);
            }

            var remaining = Math.Max(0, (int)Math.Ceiling((nextRestockRetryUtc - now).TotalSeconds));
            StatusText = $"{marketPurchase.StatusText}；{remaining} 秒后重试";
            if (now < nextRestockRetryUtc)
            {
                return;
            }

            nextRestockRetryUtc = DateTime.MinValue;
            if (restockFailureCount >= 5)
            {
                var failure = marketPurchase.StatusText;
                CancelRestock();
                SetState(LeaderAutomationState.Error, $"自动补图连续失败：{failure}");
                return;
            }

            marketPurchase.Reset();
            if (!marketPurchase.IsBoardOpen || restockFailureCount % 2 == 0)
            {
                marketPurchase.CloseBoard();
                PrepareRestockTravel("正在重新打开海都市场板");
                return;
            }

            StartMarketPurchase();
            return;
        }

        if (marketPurchase.State != MarketMapPurchaseState.Success || InventoryMapCount == 0)
        {
            return;
        }

        CompleteMarketPurchaseStage();
    }

    private void CompleteMarketPurchaseStage()
    {
        if (developerTest == DeveloperTestKind.Market)
        {
            FinishDeveloperTest($"自动买图测试完成：已购入一张 {SelectedMapName}，花费 {marketPurchase.PurchasedTotalPrice:N0} Gil。");
            return;
        }

        restockSpentGil = checked(restockSpentGil + marketPurchase.PurchasedTotalPrice);
        restockFailureCount = 0;

        switch (restockStage)
        {
            case RestockStage.BuyFirst:
                marketPurchase.CloseBoard();
                marketPurchase.Reset();
                restockStage = RestockStage.DecipherFirst;
                lastActionUtc = DateTime.MinValue;
                SetState(LeaderAutomationState.LookingForMap, $"第一张 {SelectedMap.GradeLabel} 已购买，准备解读");
                break;

            case RestockStage.BuySecond:
                CompleteRestock();
                break;
        }
    }

    private void ResumeRestockAfterDecipher()
    {
        restockStage = RestockStage.BuySecond;
        PrepareRestockTravel($"第一张 {SelectedMap.GradeLabel} 已解读，返回市场板购买第二张");
    }

    private void StartMarketPurchase()
    {
        marketPurchase.Begin(
            configuration.LeaderTreasureMapItemId,
            SelectedMapName,
            configuration.LeaderMapMaximumUnitPrice);
        nextRestockRetryUtc = DateTime.MinValue;
        SetState(
            LeaderAutomationState.RestockingMarket,
            restockStage switch
            {
                RestockStage.BuyFirst => $"正在购买第一张 {SelectedMap.GradeLabel}",
                RestockStage.BuySecond => $"正在购买第二张 {SelectedMap.GradeLabel}",
                _ => $"正在购买 {SelectedMap.GradeLabel}",
            });
    }

    private void PrepareRestockTravel(string status)
    {
        marketTravelStartedUtc = DateTime.MinValue;
        restockArrivedUtc = DateTime.MinValue;
        restockTeleportSubmittedUtc = DateTime.MinValue;
        nextRestockRetryUtc = DateTime.MinValue;
        lastActionUtc = DateTime.MinValue;
        SetState(LeaderAutomationState.RestockingTravel, status);
    }

    private void CompleteRestock()
    {
        var totalSpent = restockSpentGil;
        marketPurchase.CloseBoard();
        marketPurchase.Reset();
        restockStage = RestockStage.None;
        restockFailureCount = 0;
        restockSpentGil = 0;
        nextRestockRetryUtc = DateTime.MinValue;
        lastActionUtc = DateTime.UtcNow;
        preferInventoryMap = false;
        diagnostics.Write("自动补图", $"两张 {SelectedMap.GradeLabel} 已补充完成；合计含税 {totalSpent:N0} Gil。" );
        SetState(LeaderAutomationState.LookingForMap, $"两张 {SelectedMap.GradeLabel} 已补满，准备使用已解读藏宝图");
    }

    private void CancelRestock()
    {
        if (restockStage != RestockStage.None)
        {
            marketPurchase.CloseBoard();
        }

        marketPurchase.Reset();
        restockStage = RestockStage.None;
        restockFailureCount = 0;
        restockSpentGil = 0;
        restockArrivedUtc = DateTime.MinValue;
        restockTeleportSubmittedUtc = DateTime.MinValue;
        marketTravelStartedUtc = DateTime.MinValue;
        nextRestockRetryUtc = DateTime.MinValue;
    }

    private unsafe void ProcessDecipherMenu(DateTime now)
    {
        if (decipherMenuSelectionIssued)
        {
            return;
        }

        if (now - stateEnteredUtc > TimeSpan.FromSeconds(8))
        {
            SetState(LeaderAutomationState.Error, "未能在解读菜单中找到所选藏宝图");
            return;
        }

        var targetName = GetSelectedMapName();
        var normalizedTarget = NormalizeMapName(targetName);
        for (var addonIndex = 1; addonIndex <= 10; addonIndex++)
        {
            var iconAddon = Plugin.GameGui.GetAddonByName<AddonSelectIconString>("SelectIconString", addonIndex);
            if (iconAddon != null && iconAddon->IsVisible)
            {
                var menu = iconAddon->PopupMenu.PopupMenu;
                for (var index = 0; index < menu.EntryCount; index++)
                {
                    var pointer = menu.EntryNames[index].Value;
                    if (pointer == null)
                    {
                        continue;
                    }

                    var text = MemoryHelper.ReadSeStringNullTerminated((nint)pointer).TextValue;
                    if (!MatchesSelectedMap(text, targetName, normalizedTarget))
                    {
                        continue;
                    }

                    FireDecipherChoice(&iconAddon->AtkUnitBase, index);
                    CompleteDecipherSelection(now, index, text, "SelectIconString");
                    return;
                }

                diagnostics.WriteThrottled("decipher-icon-menu", "车头",
                    $"SelectIconString#{addonIndex} 已显示，条目数={menu.EntryCount}；未匹配“{targetName}”。",
                    TimeSpan.FromSeconds(2));
            }

            var stringAddon = Plugin.GameGui.GetAddonByName<AddonSelectString>("SelectString", addonIndex);
            if (stringAddon == null || !stringAddon->IsVisible)
            {
                continue;
            }

            var stringMenu = stringAddon->PopupMenu.PopupMenu;
            for (var index = 0; index < stringMenu.EntryCount; index++)
            {
                var pointer = stringMenu.EntryNames[index].Value;
                if (pointer == null)
                {
                    continue;
                }

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)pointer).TextValue;
                if (!MatchesSelectedMap(text, targetName, normalizedTarget))
                {
                    continue;
                }

                stringAddon->AtkUnitBase.FireCallbackInt(index);
                CompleteDecipherSelection(now, index, text, "SelectString");
                return;
            }

            diagnostics.WriteThrottled("decipher-string-menu", "车头",
                $"SelectString#{addonIndex} 已显示，条目数={stringMenu.EntryCount}；未匹配“{targetName}”。",
                TimeSpan.FromSeconds(2));
        }

        diagnostics.WriteThrottled("decipher-menu-missing", "车头",
            $"等待解读选择菜单 SelectIconString/SelectString，物品“{targetName}”。", TimeSpan.FromSeconds(3));
    }

    private static unsafe void FireDecipherChoice(AtkUnitBase* addon, int index)
    {
        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[0].Type = AtkValueType.Bool;
        values[0].Byte = 1;
        values[1] = default;
        values[1].Type = AtkValueType.Int;
        values[1].Int = index;
        addon->FireCallback(2, values);
    }

    private static bool MatchesSelectedMap(string text, string targetName, string normalizedTarget)
    {
        var normalizedEntry = NormalizeMapName(text);
        return text.Contains(targetName, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Length > 0 && normalizedEntry.Length > 0
                && (normalizedEntry.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    || normalizedTarget.Contains(normalizedEntry, StringComparison.OrdinalIgnoreCase));
    }

    private void CompleteDecipherSelection(DateTime now, int index, string text, string addonName)
    {
        decipherMenuSelectionIssued = true;
        pendingYesUntilUtc = now + TimeSpan.FromSeconds(6);
        SetState(LeaderAutomationState.ConfirmingDecipher, "正在确认解读藏宝图");
        diagnostics.Write("车头", $"在 {addonName} 解读菜单第 {index} 项匹配到“{text}”。");
    }

    private void ProcessDecipherConfirmation(DateTime now)
    {
        if (InventoryMapCount < decipherInventoryCountBefore)
        {
            if (developerTest == DeveloperTestKind.Decipher)
            {
                FinishDeveloperTest($"解读测试完成：背包数量 {decipherInventoryCountBefore} → {InventoryMapCount}，已解读地图 {DecodedMapCount} 张。");
                return;
            }

            preferInventoryMap = false;
            if (restockStage == RestockStage.DecipherFirst)
            {
                ResumeRestockAfterDecipher();
            }
            else
            {
                SetState(LeaderAutomationState.LookingForMap, "藏宝图已解读");
            }
            diagnostics.Write("车头", $"解读完成：背包数量 {decipherInventoryCountBefore} -> {InventoryMapCount}，任务道具数量={DecodedMapCount}。" );
            return;
        }

        TryAcceptPendingYes(now);
        if (now - stateEnteredUtc > TimeSpan.FromSeconds(10))
        {
            SetState(LeaderAutomationState.Error, "解读确认超时，请检查是否已有另一张已解读藏宝图");
        }
    }

    private unsafe void ProcessOpenDecodedMap(DateTime now)
    {
        if (DecodedMapCount == 0 || decodedMapProfile == null)
        {
            SetState(LeaderAutomationState.LookingForMap, "已解读藏宝图已用完，重新检查背包");
            return;
        }

        if (now - lastActionUtc < ActionRetryInterval)
        {
            return;
        }

        lastActionUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager == null
            || actionManager->GetActionStatus(ActionType.EventItem, decodedMapProfile.DecodedEventItemId) != 0)
        {
            StatusText = "等待已解读藏宝图可使用";
            return;
        }

        actionManager->UseAction(ActionType.EventItem, decodedMapProfile.DecodedEventItemId);
        SetState(LeaderAutomationState.WaitingForFlag, "已打开藏宝图，等待坐标插件创建旗标");
        diagnostics.Write("车头", $"已使用已解读藏宝图 #{decodedMapProfile.DecodedEventItemId}，等待新旗标。" );
    }

    private void ProcessWaitingForFlag(DateTime now)
    {
        var target = mapAutomation.RegisterOwnFlagFromGame();
        if (target != null)
        {
            currentOwnTarget = target;
            SendPartyFlag();
            SetState(LeaderAutomationState.Navigating,
                $"已发送自己的藏宝图坐标，前往 {target.PlaceName} {target.MapX:F1}, {target.MapY:F1}");
            return;
        }

        if (now - stateEnteredUtc > TimeSpan.FromSeconds(12))
        {
            SetState(
                LeaderAutomationState.Error,
                "没有检测到藏宝图旗标，请确认坐标标记插件已启用后重新检查");
        }
    }

    private void ProcessNavigationWait()
    {
        if (configuration.OperatingMode != OperatingMode.Leader)
        {
            return;
        }

        if (mapAutomation.ActiveTarget?.IsOwnTreasure == true)
        {
            StatusText = mapAutomation.StatusText;
            return;
        }

        if (mapAutomation.State == AutomationState.Error)
        {
            SetState(LeaderAutomationState.Error, $"导航未完成：{mapAutomation.StatusText}");
        }
    }

    private void ProcessPartyWait(DateTime now)
    {
        var missing = CountPartyMembersOutsideCurrentTerritory();
        if (missing > 0)
        {
            StatusText = $"等待 {missing} 名队员进入当前地图";
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StatusText = "附近战斗中，结束后开始挖掘";
            return;
        }

        CapturePreDigChests();
        lastActionUtc = DateTime.MinValue;
        SetState(LeaderAutomationState.Digging, "全队已到齐，准备挖掘");
        ProcessDigging(now);
    }

    private unsafe void ProcessDigging(DateTime now)
    {
        if (SelectedMap.DirectPortal)
        {
            var portal = FindOutdoorPortal();
            if (portal != null)
            {
                portalSearchStartedUtc = now;
                SetState(LeaderAutomationState.WaitingForLootOrPortal, "挖掘成功，正在前往传送魔纹");
                ProcessPortalOrNextMap(now);
                return;
            }
        }

        var chest = FindOutdoorChest(requireNew: true);
        if (chest != null)
        {
            TrackChest(chest);
            SetState(LeaderAutomationState.ApproachingChest, "挖掘成功，正在前往宝箱");
            return;
        }

        if (now - lastActionUtc < TimeSpan.FromSeconds(4))
        {
            return;
        }

        lastActionUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager == null || actionManager->GetActionStatus(ActionType.GeneralAction, 20) != 0)
        {
            StatusText = "等待挖掘可用";
            return;
        }

        actionManager->UseAction(ActionType.GeneralAction, 20);
        StatusText = "正在挖掘，等待自己的宝箱出现";
        diagnostics.Write("车头", "已使用挖掘，等待当前位置附近新增的寻宝宝箱。" );
    }

    private void ProcessApproachingChest(DateTime now)
    {
        var chest = FindTrackedChest() ?? FindOutdoorChest(requireNew: true);
        if (chest == null)
        {
            if (now - stateEnteredUtc > TimeSpan.FromSeconds(12))
            {
                SetState(LeaderAutomationState.Digging, "宝箱暂时不可见，重新确认挖掘结果");
            }

            return;
        }

        TrackChest(chest);
        if (!ApproachAndInteract(chest, now, "藏宝图宝箱"))
        {
            return;
        }

        pendingYesUntilUtc = now + TimeSpan.FromSeconds(8);
        SetState(LeaderAutomationState.WaitingForCombat, "已打开宝箱，等待敌人出现");
    }

    private void ProcessWaitingForCombat(DateTime now)
    {
        TryAcceptPendingYes(now);
        if (Plugin.Condition[ConditionFlag.InCombat] || HasTreasureEnemies())
        {
            StopOwnedNavigation();
            SetState(LeaderAutomationState.Combat, "寻宝战斗中，由 AE Assist 与 BossMod Reborn 处理");
            return;
        }

        if (TreasureDungeonAutomation.HasPendingLootDistribution())
        {
            portalSearchStartedUtc = now;
            SetState(LeaderAutomationState.WaitingForLootOrPortal, "没有检测到战斗，等待战利品与传送魔纹");
            return;
        }

        if (now - stateEnteredUtc > TimeSpan.FromSeconds(10))
        {
            SetState(LeaderAutomationState.ReopeningChest, "未检测到敌人，尝试再次打开宝箱");
        }
    }

    private void ProcessCombat(DateTime now)
    {
        if (HasTreasureEnemies())
        {
            noTreasureEnemySinceUtc = DateTime.MinValue;
            StatusText = "寻宝战斗中，由 AE Assist 与 BossMod Reborn 处理";
            return;
        }

        noTreasureEnemySinceUtc = noTreasureEnemySinceUtc == DateTime.MinValue ? now : noTreasureEnemySinceUtc;
        if (now - noTreasureEnemySinceUtc < TimeSpan.FromMilliseconds(700))
        {
            StatusText = "战斗结束，确认场上敌人已清空";
            return;
        }

        SetState(LeaderAutomationState.ReopeningChest, "战斗结束，重新打开宝箱");
    }

    private void ProcessReopeningChest(DateTime now)
    {
        var chest = FindTrackedChest() ?? FindOutdoorChest(requireNew: false);
        if (chest == null)
        {
            portalSearchStartedUtc = now;
            SetState(LeaderAutomationState.WaitingForLootOrPortal, "宝箱已消失，等待战利品与传送魔纹");
            return;
        }

        if (!ApproachAndInteract(chest, now, "藏宝图宝箱"))
        {
            return;
        }

        portalSearchStartedUtc = now;
        SetState(LeaderAutomationState.WaitingForLootOrPortal, "宝箱已开启，等待战利品与传送魔纹");
    }

    private void ProcessPortalOrNextMap(DateTime now)
    {
        var portal = FindOutdoorPortal();
        if (portal != null)
        {
            if (TreasureDungeonAutomation.HasPendingLootDistribution())
            {
                StatusText = "传送魔纹已出现，等待战利品分配完成";
                return;
            }

            if (ApproachAndInteract(portal, now, "传送魔纹"))
            {
                pendingYesUntilUtc = now + TimeSpan.FromSeconds(8);
                SetState(LeaderAutomationState.EnteringPortal, "正在进入传送魔纹");
            }

            return;
        }

        if (TreasureDungeonAutomation.HasPendingLootDistribution())
        {
            StatusText = "等待战利品分配完成";
            return;
        }

        portalSearchStartedUtc = portalSearchStartedUtc == DateTime.MinValue ? now : portalSearchStartedUtc;
        if (now - portalSearchStartedUtc < TimeSpan.FromSeconds(3))
        {
            StatusText = "等待传送魔纹出现";
            return;
        }

        preferInventoryMap = true;
        ResetCycle();
        SetState(LeaderAutomationState.LookingForMap, "本张地图已结束，检查下一张藏宝图");
    }

    private void ProcessEnteringPortal(DateTime now)
    {
        TryAcceptPendingYes(now);
        if (TreasureContext.IsTreasureDungeon())
        {
            SetState(LeaderAutomationState.Dungeon, "已进入宝物库");
            return;
        }

        if (now - stateEnteredUtc > TimeSpan.FromSeconds(25))
        {
            var portal = FindOutdoorPortal();
            if (portal == null)
            {
                preferInventoryMap = true;
                ResetCycle();
                SetState(LeaderAutomationState.LookingForMap, "传送魔纹已关闭，检查下一张藏宝图");
            }
            else
            {
                SetState(LeaderAutomationState.WaitingForLootOrPortal, "进入宝物库未完成，准备重试");
            }
        }
    }

    private void ProcessDungeon(DateTime now)
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (!TreasureDungeonCatalog.TryGet(territoryId, out var dungeon))
        {
            StopOwnedNavigation();
            StatusText = "当前宝物库尚未加入车头自动流程";
            return;
        }

        TryAcceptPendingYes(now);

        if (dungeon.HandleHigherLower && TryHandleHigherLower(now))
        {
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat] || HasTreasureEnemies())
        {
            StopOwnedNavigation();
            StatusText = "宝物库战斗中，由 AE Assist 与 BossMod Reborn 处理";
            return;
        }

        if (TreasureSackAutomation.HasCollectibleSacks())
        {
            StopOwnedNavigation();
            StatusText = "正在收集金袋与银袋";
            return;
        }

        if (TreasureDungeonAutomation.HasPendingLootDistribution())
        {
            StopOwnedNavigation();
            StatusText = "等待战利品分配完成";
            return;
        }

        var chest = FindDungeonChest(dungeon, now);
        var progression = FindDungeonProgressionObject(dungeon, now);
        if (chest != null && (progression == null || !dungeonInteractionTimes.ContainsKey(GetEntityId(chest))))
        {
            if (ApproachAndInteract(chest, now, "宝物库宝箱", dungeon.InteractionRange))
            {
                dungeonInteractionTimes[GetEntityId(chest)] = now;
                StatusText = "已打开宝物库宝箱，等待下一阶段";
            }

            return;
        }

        if (progression != null)
        {
            var label = progressionObjectNames.GetValueOrDefault(territoryId)?.FirstOrDefault()
                ?? (dungeon.Style == TreasureDungeonStyle.Door ? "宝物库门" : "转盘机关");
            if (ApproachAndInteract(progression, now, label, dungeon.InteractionRange))
            {
                dungeonInteractionTimes[GetEntityId(progression)] = now;
                pendingYesUntilUtc = dungeon.AutoConfirmProgression
                    ? now + TimeSpan.FromSeconds(8)
                    : DateTime.MinValue;
                StatusText = dungeon.Style == TreasureDungeonStyle.Door
                    ? "已选择宝物库门，等待结果"
                    : "已启动转盘机关，等待下一轮";
            }

            return;
        }

        StopOwnedNavigation();
        StatusText = dungeon.Style == TreasureDungeonStyle.Door
            ? "等待宝箱、可选门或下一场战斗"
            : "等待宝箱、转盘机关或下一场战斗";
    }

    private unsafe bool TryHandleHigherLower(DateTime now)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var higherLower = manager->GetAddonByName("TreasureHighLow");
        var challenge = manager->GetAddonByName("_NotificationChallenge");
        var higherLowerVisible = higherLower != null && higherLower->IsVisible;
        var challengeVisible = challenge != null && challenge->IsVisible;
        if (!higherLowerVisible && !challengeVisible)
        {
            higherLowerAttemptIndex = 0;
            nextHigherLowerActionUtc = DateTime.MinValue;
            return false;
        }

        StopOwnedNavigation();
        StatusText = "正在直接领取比大小宝箱";
        if (now < nextHigherLowerActionUtc)
        {
            return true;
        }

        nextHigherLowerActionUtc = now + HigherLowerRetryInterval;
        if (!higherLowerVisible)
        {
            FireAddonCallback("_Notification", false, true, 0, 1);
            return true;
        }

        var attempt = HigherLowerCloseAttempts[higherLowerAttemptIndex % HigherLowerCloseAttempts.Length];
        higherLowerAttemptIndex++;
        FireAddonCallback("TreasureHighLow", true, attempt.UpdateState, attempt.Argument);
        return true;
    }

    private static unsafe bool FireAddonCallback(
        string addonName,
        bool requireVisible,
        bool updateState,
        params int[] arguments)
    {
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            var addon = manager == null ? null : manager->GetAddonByName(addonName);
            if (addon == null || (requireVisible && !addon->IsVisible))
            {
                return false;
            }

            var values = stackalloc AtkValue[arguments.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                values[index].Type = AtkValueType.Int;
                values[index].Int = arguments[index];
            }

            addon->FireCallback((uint)arguments.Length, values, updateState);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Failed to handle treasure Higher/Lower addon {AddonName}.", addonName);
            return false;
        }
    }

    private unsafe bool ApproachAndInteract(
        IGameObject gameObject,
        DateTime now,
        string label,
        float approachRange = ObjectApproachRange)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || gameObject.Address == 0)
        {
            return false;
        }

        var distance = HorizontalDistance(player.Position, gameObject.Position);
        if (distance > approachRange)
        {
            if (!vnavmesh.IsInstalled || !vnavmesh.IsReady())
            {
                StatusText = $"等待 vnavmesh 就绪后前往{label}";
                return false;
            }

            if (navigationObjectAddress != gameObject.Address)
            {
                StopOwnedNavigation();
                navigationObjectAddress = gameObject.Address;
            }

            if ((!ownsNavigation || !vnavmesh.IsBusy())
                && now - lastObjectNavigationUtc >= ObjectNavigationRetryInterval)
            {
                lastObjectNavigationUtc = now;
                ownsNavigation = vnavmesh.MoveCloseTo(
                    gameObject.Position,
                    fly: false,
                    MathF.Max(0.5f, approachRange - 0.4f));
            }

            StatusText = $"正在前往{label} · {distance:F0}y";
            return false;
        }

        StopOwnedNavigation();
        if (!gameObject.IsTargetable)
        {
            StatusText = $"已靠近{label}，等待可以交互";
            return false;
        }

        if (now - lastInteractionUtc < InteractionRetryInterval)
        {
            return false;
        }

        lastInteractionUtc = now;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            return false;
        }

        targetSystem->InteractWithObject((NativeGameObject*)gameObject.Address, false);
        diagnostics.Write("车头", $"交互 {label}：baseId={gameObject.BaseId}，entity={GetEntityId(gameObject)}。" );
        return true;
    }

    private unsafe bool ApproachAndOpenMarketBoard(IGameObject marketBoard, DateTime now)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || marketBoard.Address == 0)
        {
            return false;
        }

        var distance = Vector3.Distance(player.Position, marketBoard.Position);
        diagnostics.WriteThrottled("market-board-position", "自动补图",
            $"找到市场布告板：baseId={marketBoard.BaseId}，entity={GetEntityId(marketBoard)}，"
                + $"position={FormatPosition(marketBoard.Position)}，玩家={FormatPosition(player.Position)}，"
                + $"三维距离={distance:F1}y，targetable={marketBoard.IsTargetable}。",
            TimeSpan.FromSeconds(5));
        if (distance > MarketBoardInteractionRange)
        {
            if (!vnavmesh.IsInstalled || !vnavmesh.IsReady())
            {
                StatusText = "等待 vnavmesh 就绪后前往海都市场布告板";
                return false;
            }

            if (navigationObjectAddress != marketBoard.Address)
            {
                StopOwnedNavigation();
                navigationObjectAddress = marketBoard.Address;
            }

            if ((!ownsNavigation || !vnavmesh.IsBusy())
                && now - lastObjectNavigationUtc >= ObjectNavigationRetryInterval)
            {
                lastObjectNavigationUtc = now;
                ownsNavigation = vnavmesh.MoveCloseTo(
                    marketBoard.Position,
                    fly: false,
                    2.3f);
            }

            StatusText = $"正在前往海都市场布告板 · {distance:F0}y";
            return false;
        }

        StopOwnedNavigation();
        if (now - lastInteractionUtc < InteractionRetryInterval)
        {
            return false;
        }

        lastInteractionUtc = now;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            return false;
        }

        if (!marketBoard.IsTargetable)
        {
            StatusText = "等待市场布告板可交互";
            return false;
        }

        Plugin.TargetManager.Target = marketBoard;
        var interactionResult = targetSystem->InteractWithObject((NativeGameObject*)marketBoard.Address, false);
        diagnostics.Write(
            "自动补图",
            $"已尝试交互市场布告板：baseId={marketBoard.BaseId}，entity={GetEntityId(marketBoard)}，distance={distance:F1}，result={interactionResult}。" );
        return true;
    }

    private unsafe void TryAcceptPendingYes(DateTime now)
    {
        if (now > pendingYesUntilUtc)
        {
            return;
        }

        for (var index = 1; index < 10; index++)
        {
            var addon = Plugin.GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", index);
            if (addon == null || !addon->IsVisible)
            {
                continue;
            }

            addon->FireCallbackInt(0);
            pendingYesUntilUtc = DateTime.MinValue;
            diagnostics.Write("车头", "已确认当前车头流程产生的是/否对话框。" );
            return;
        }
    }

    private unsafe IGameObject? FindOutdoorChest(bool requireNew)
    {
        if (currentOwnTarget == null)
        {
            return null;
        }

        var targetPosition = mapAutomation.GetResolvedWorldPosition(
            currentOwnTarget,
            Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f);
        return Plugin.ObjectTable
            .Where(obj => IsTreasureHuntObject(obj, ObjectKind.Treasure)
                && obj.IsTargetable
                && HorizontalDistance(obj.Position, targetPosition) <= OutdoorObjectSearchRange
                && (!requireNew || !preDigChestIds.Contains(GetEntityId(obj))))
            .OrderBy(obj => HorizontalDistance(obj.Position, targetPosition))
            .FirstOrDefault();
    }

    private IGameObject? FindTrackedChest()
    {
        if (trackedChestEntityId == 0)
        {
            return null;
        }

        return Plugin.ObjectTable.FirstOrDefault(obj =>
            obj.Address != 0
            && GetEntityId(obj) == trackedChestEntityId
            && obj.IsTargetable);
    }

    private unsafe IGameObject? FindOutdoorPortal()
    {
        var origin = trackedChestPosition;
        if (origin == Vector3.Zero && currentOwnTarget != null)
        {
            origin = mapAutomation.GetResolvedWorldPosition(
                currentOwnTarget,
                Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f);
        }

        return Plugin.ObjectTable
            .Where(obj => IsTreasureHuntObject(obj, ObjectKind.EventObj)
                && obj.IsTargetable
                && (origin == Vector3.Zero || HorizontalDistance(obj.Position, origin) <= OutdoorObjectSearchRange))
            .OrderBy(obj => origin == Vector3.Zero ? 0f : HorizontalDistance(obj.Position, origin))
            .FirstOrDefault();
    }

    private unsafe IGameObject? FindDungeonChest(TreasureDungeonProfile dungeon, DateTime now)
        => Plugin.ObjectTable
            .Where(obj => obj.Address != 0
                && obj.IsTargetable
                && ((NativeGameObject*)obj.Address)->ObjectKind == ObjectKind.Treasure
                && CanRetryDungeonObject(GetEntityId(obj), now, dungeon.InteractionRetrySeconds))
            .OrderBy(obj => HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position))
            .FirstOrDefault();

    private unsafe IGameObject? FindDungeonProgressionObject(TreasureDungeonProfile dungeon, DateTime now)
        => Plugin.ObjectTable
            .Where(obj => obj.Address != 0
                && obj.IsTargetable
                && ((NativeGameObject*)obj.Address)->ObjectKind == ObjectKind.EventObj
                && HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position)
                    <= dungeon.ProgressionSearchRange
                && IsDungeonProgressionObject(dungeon, obj)
                && CanRetryDungeonObject(GetEntityId(obj), now, dungeon.InteractionRetrySeconds))
            .OrderBy(obj => HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position))
            .FirstOrDefault();

    private bool IsDungeonProgressionObject(TreasureDungeonProfile dungeon, IGameObject gameObject)
    {
        if (progressionObjectBaseIds.TryGetValue(dungeon.TerritoryId, out var baseIds)
            && baseIds.Contains(gameObject.BaseId))
        {
            return true;
        }

        return progressionObjectNames.TryGetValue(dungeon.TerritoryId, out var names)
            && names.Contains(gameObject.Name.TextValue);
    }

    private bool CanRetryDungeonObject(uint entityId, DateTime now, double retrySeconds)
        => !dungeonInteractionTimes.TryGetValue(entityId, out var last)
            || now - last >= TimeSpan.FromSeconds(retrySeconds);

    private unsafe bool HasTreasureEnemies()
        => Plugin.ObjectTable.Any(obj =>
        {
            if (obj.Address == 0 || !obj.IsTargetable)
            {
                return false;
            }

            var native = (NativeGameObject*)obj.Address;
            if (native->ObjectKind != ObjectKind.BattleNpc
                || native->SubKind != (byte)BattleNpcSubKind.Combatant
                || native->EventId.ContentId != EventHandlerContent.TreasureHuntDirector
                || native->NamePlateIconId is not (60094 or 60096))
            {
                return false;
            }

            if (!TreasureContext.IsTreasureDungeon()
                && trackedChestPosition != Vector3.Zero
                && HorizontalDistance(obj.Position, trackedChestPosition) > OutdoorObjectSearchRange)
            {
                return false;
            }

            var ownerId = native->OwnerId;
            if (ownerId == 0)
            {
                return TreasureContext.IsTreasureDungeon() || native->GetNamePlateColorType() != 11;
            }

            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            var localEntityId = localPlayer != null && localPlayer.Address != 0
                ? ((NativeGameObject*)localPlayer.Address)->EntityId
                : 0;
            var groupManager = GroupManager.Instance();
            var group = groupManager == null ? null : groupManager->GetGroup();
            return ownerId == localEntityId || (group != null && group->IsEntityIdInParty(ownerId));
        });

    private IGameObject? FindMarketBoard()
        => Plugin.ObjectTable
            .Where(obj => obj.Address != 0 && (MarketBoardDataIds.Contains(obj.BaseId)
                || obj.IsTargetable && (obj.Name.TextValue.Contains("市场布告板", StringComparison.OrdinalIgnoreCase)
                    || obj.Name.TextValue.Equals("Market Board", StringComparison.OrdinalIgnoreCase))))
            .OrderBy(obj => Vector3.Distance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position))
            .FirstOrDefault();

    private unsafe bool IsTreasureHuntObject(IGameObject gameObject, ObjectKind objectKind)
    {
        if (gameObject.Address == 0)
        {
            return false;
        }

        var native = (NativeGameObject*)gameObject.Address;
        return native->ObjectKind == objectKind
            && native->EventId.ContentId == EventHandlerContent.TreasureHuntDirector;
    }

    private void CapturePreDigChests()
    {
        preDigChestIds.Clear();
        foreach (var obj in Plugin.ObjectTable.Where(obj => IsTreasureHuntObject(obj, ObjectKind.Treasure)))
        {
            preDigChestIds.Add(GetEntityId(obj));
        }
    }

    private void TrackChest(IGameObject chest)
    {
        trackedChestEntityId = GetEntityId(chest);
        trackedChestPosition = chest.Position;
    }

    private unsafe int CountPartyMembersOutsideCurrentTerritory()
    {
        var manager = GroupManager.Instance();
        var group = manager == null ? null : manager->GetGroup();
        if (group == null || group->MemberCount == 0)
        {
            return 0;
        }

        var territory = Plugin.ClientState.TerritoryType;
        var missing = 0;
        for (var index = 0; index < group->MemberCount; index++)
        {
            var member = group->PartyMembers[index];
            if (member.ContentId != 0 && member.TerritoryType != territory)
            {
                missing++;
            }
        }

        return missing;
    }

    private unsafe void RefreshInventorySummary()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            decodedInventoryLoaded = false;
            return;
        }

        var keyItems = manager->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItems == null || !keyItems->IsLoaded)
        {
            decodedInventoryLoaded = false;
            return;
        }

        decodedInventoryLoaded = true;
        decodedMapProfile = null;
        DecodedMapCount = 0;
        foreach (var profile in TreasureMapCatalog.Profiles.OrderByDescending(profile => profile.ItemId == configuration.LeaderTreasureMapItemId))
        {
            var count = CountItem(manager, InventoryType.KeyItems, profile.DecodedEventItemId);
            if (count == 0)
            {
                continue;
            }

            decodedMapProfile ??= profile;
            DecodedMapCount += count;
        }

        InventoryMapCount = MainInventories.Sum(type => CountItem(manager, type, configuration.LeaderTreasureMapItemId));
    }

    private static unsafe int CountItem(InventoryManager* manager, InventoryType type, uint itemId)
    {
        var container = manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < container->Size; index++)
        {
            var item = container->GetInventorySlot(index);
            if (item != null && item->ItemId == itemId)
            {
                count += (int)item->Quantity;
            }
        }

        return count;
    }

    private string GetSelectedMapName()
    {
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(configuration.LeaderTreasureMapItemId);
            var name = item.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"{SelectedMap.GradeLabel} 藏宝图" : name;
        }
        catch
        {
            return $"{SelectedMap.GradeLabel} 藏宝图";
        }
    }

    private static string NormalizeMapName(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        foreach (var prefix in new[] { "陈旧的", "陳舊的", "timeworn", "usée", "uséeparletemps" })
        {
            normalized = normalized.Replace(prefix, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return normalized;
    }

    private unsafe void SendPartyFlag()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            diagnostics.Write("车头", "无法取得 UIModule，未发送小队坐标。" );
            return;
        }

        var message = Utf8String.FromString("/p <flag>");
        try
        {
            uiModule->ProcessChatBoxEntry(message);
            diagnostics.Write("车头", "已向小队发送当前旗标。" );
        }
        finally
        {
            message->Dtor(true);
        }
    }

    private static string FormatPosition(Vector3? position)
        => position is { } value
            ? $"({value.X:F1},{value.Y:F1},{value.Z:F1})"
            : "无";

    private void LoadDungeonProgressionObjects()
    {
        try
        {
            var englishSheet = Plugin.DataManager.GetExcelSheet<EObjName>(ClientLanguage.English);
            var localSheet = Plugin.DataManager.GetExcelSheet<EObjName>();
            var wantedNames = TreasureDungeonCatalog.Profiles
                .SelectMany(profile => profile.EnumerateProgressionObjectNames())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rowsByEnglishName = englishSheet
                .Where(row => wantedNames.Contains(row.Singular.ToString()))
                .GroupBy(row => row.Singular.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => row.RowId).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var dungeon in TreasureDungeonCatalog.Profiles)
            {
                var baseIds = new HashSet<uint>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (dungeon.TerritoryId == 1279)
                {
                    baseIds.Add(HypnoslotNameRowId);
                    names.Add("潜网巡梦");
                    if (localSheet.TryGetRow(HypnoslotNameRowId, out var knownObject))
                    {
                        var localizedName = knownObject.Singular.ToString();
                        if (!string.IsNullOrWhiteSpace(localizedName))
                        {
                            names.Add(localizedName);
                        }
                    }
                }

                foreach (var expectedName in dungeon.EnumerateProgressionObjectNames())
                {
                    names.Add(expectedName);
                    if (!rowsByEnglishName.TryGetValue(expectedName, out var rowIds))
                    {
                        continue;
                    }

                    foreach (var baseId in rowIds)
                    {
                        baseIds.Add(baseId);
                        var localName = localSheet.GetRow(baseId).Singular.ToString();
                        if (!string.IsNullOrWhiteSpace(localName))
                        {
                            names.Add(localName);
                        }
                    }
                }

                if (baseIds.Count > 0)
                {
                    progressionObjectBaseIds[dungeon.TerritoryId] = baseIds;
                }

                progressionObjectNames[dungeon.TerritoryId] = names;
            }

            diagnostics.Write(
                "宝物库",
                $"已载入 {progressionObjectBaseIds.Count}/{TreasureDungeonCatalog.Profiles.Count} 个宝物库机关。" );
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Failed to load treasure dungeon progression objects.");
            diagnostics.Write("宝物库", $"读取宝物库机关数据失败：{exception.Message}" );
        }
    }

    private static unsafe void ClearMapFlag()
    {
        var agent = AgentMap.Instance();
        if (agent != null)
        {
            agent->FlagMarkerCount = 0;
        }
    }

    private static uint GetEntityId(IGameObject gameObject)
    {
        unsafe
        {
            return gameObject.Address == 0 ? 0 : ((NativeGameObject*)gameObject.Address)->EntityId;
        }
    }

    private void DeferCurrentDungeonObject()
    {
        if (navigationObjectAddress == 0)
        {
            return;
        }

        var current = Plugin.ObjectTable.FirstOrDefault(obj => obj.Address == navigationObjectAddress);
        if (current != null)
        {
            dungeonInteractionTimes[GetEntityId(current)] = DateTime.UtcNow + TimeSpan.FromSeconds(22);
        }

        navigationObjectAddress = 0;
    }

    private void StopOwnedNavigation()
    {
        if (ownsNavigation)
        {
            vnavmesh.Stop();
        }

        ownsNavigation = false;
        navigationObjectAddress = 0;
    }

    private void ResetOutdoorEncounter()
    {
        currentOwnTarget = null;
        trackedChestEntityId = 0;
        trackedChestPosition = Vector3.Zero;
        preDigChestIds.Clear();
        noTreasureEnemySinceUtc = DateTime.MinValue;
        portalSearchStartedUtc = DateTime.MinValue;
        pendingYesUntilUtc = DateTime.MinValue;
    }

    private void ResetCycle()
    {
        CancelRestock();
        ResetOutdoorEncounter();
        decipherMenuSelectionIssued = false;
        decipherInventoryCountBefore = 0;
        higherLowerAttemptIndex = 0;
        nextHigherLowerActionUtc = DateTime.MinValue;
        dungeonInteractionTimes.Clear();
    }

    private void SetState(LeaderAutomationState state, string status)
    {
        var previous = State;
        State = state;
        StatusText = status;
        stateEnteredUtc = DateTime.UtcNow;
        if (previous != state)
        {
            diagnostics.Write("车头状态", $"{previous} -> {state}；{status}" );
        }
    }

    private static bool IsLoadingOrWatchingCutscene()
        => Plugin.Condition.Any(
            ConditionFlag.BetweenAreas,
            ConditionFlag.BetweenAreas51,
            ConditionFlag.OccupiedInCutSceneEvent,
            ConditionFlag.WatchingCutscene,
            ConditionFlag.WatchingCutscene78);

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private enum RestockStage
    {
        None,
        BuyFirst,
        DecipherFirst,
        BuySecond,
    }

    private enum DeveloperTestKind
    {
        None,
        Market,
        Decipher,
    }
}
