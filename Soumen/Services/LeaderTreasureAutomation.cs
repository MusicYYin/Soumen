using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Soumen.Models;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Soumen.Services;

public sealed class LeaderTreasureAutomation : IDisposable
{
    private const uint GargantuaskinMapItemId = 46185;
    private const uint GargantuaskinDecodedEventItemId = 2003785;
    private const uint VaultOneironTerritoryId = 1279;
    private const uint HypnoslotNameRowId = 2014790;
    private const float ObjectApproachRange = 3.2f;
    private const float OutdoorObjectSearchRange = 55f;

    private static readonly InventoryType[] MainInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] SaddlebagInventories =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
    ];

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractionRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObjectNavigationRetryInterval = TimeSpan.FromMilliseconds(800);

    private readonly Configuration configuration;
    private readonly MapFlagAutomation mapAutomation;
    private readonly DiagnosticLogger diagnostics;
    private readonly VNavmeshIpc vnavmesh;
    private readonly string hypnoslotName;

    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime stateEnteredUtc = DateTime.UtcNow;
    private DateTime lastActionUtc = DateTime.MinValue;
    private DateTime lastInteractionUtc = DateTime.MinValue;
    private DateTime lastObjectNavigationUtc = DateTime.MinValue;
    private DateTime noTreasureEnemySinceUtc = DateTime.MinValue;
    private DateTime pendingYesUntilUtc = DateTime.MinValue;
    private DateTime saddlebagOpenedUtc = DateTime.MinValue;
    private DateTime portalSearchStartedUtc = DateTime.MinValue;
    private readonly HashSet<uint> preDigChestIds = [];
    private readonly Dictionary<uint, DateTime> dungeonInteractionTimes = [];
    private MapFlagTarget? currentOwnTarget;
    private uint trackedChestEntityId;
    private Vector3 trackedChestPosition;
    private nint navigationObjectAddress;
    private bool ownsNavigation;
    private bool decipherMenuSelectionIssued;
    private bool sawTreasureCombat;
    private bool disposed;

    public LeaderTreasureAutomation(
        Configuration configuration,
        MapFlagAutomation mapAutomation,
        DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.mapAutomation = mapAutomation;
        this.diagnostics = diagnostics;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface, diagnostics);
        hypnoslotName = LoadHypnoslotName();
        mapAutomation.DestinationReached += OnDestinationReached;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public LeaderAutomationState State { get; private set; } = LeaderAutomationState.Inactive;

    public string StatusText { get; private set; } = "跟车模式";

    public string SelectedMapName => GetSelectedMapName();

    public int DecodedMapCount { get; private set; }

    public int InventoryMapCount { get; private set; }

    public int SaddlebagMapCount { get; private set; }

    public bool SaddlebagLoaded { get; private set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mapAutomation.DestinationReached -= OnDestinationReached;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        StopOwnedNavigation();
    }

    public void Restart()
    {
        StopOwnedNavigation();
        ResetCycle();
        SetState(LeaderAutomationState.LookingForMap, "正在检查藏宝图");
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

        if (!configuration.Enabled || configuration.OperatingMode != OperatingMode.Leader)
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

        if (TreasureContext.IsTreasureDungeon())
        {
            if (State != LeaderAutomationState.Dungeon)
            {
                ResetOutdoorEncounter();
                SetState(LeaderAutomationState.Dungeon, "已进入宝物库");
            }

            ProcessDungeon(now);
            return;
        }

        if (State == LeaderAutomationState.Dungeon)
        {
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
            case LeaderAutomationState.MovingMapFromSaddlebag:
                ProcessSaddlebag(now);
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

    private void ProcessMapAcquisition(DateTime now)
    {
        if (DecodedMapCount > 0)
        {
            ClearMapFlag();
            SetState(LeaderAutomationState.OpeningDecodedMap, "正在使用已解读的 G18 藏宝图");
            lastActionUtc = DateTime.MinValue;
            return;
        }

        if (InventoryMapCount > 0)
        {
            if (now - lastActionUtc < ActionRetryInterval)
            {
                return;
            }

            lastActionUtc = now;
            decipherMenuSelectionIssued = false;
            Plugin.CommandManager.ProcessCommand("/gaction decipher");
            SetState(LeaderAutomationState.DecipheringMap, $"正在解读 {SelectedMapName}");
            diagnostics.Write("车头", $"已打开解读菜单，目标物品 #{configuration.LeaderTreasureMapItemId}。" );
            return;
        }

        SetState(LeaderAutomationState.MovingMapFromSaddlebag, "背包中没有 G18，正在检查陆行鸟鞍囊");
        saddlebagOpenedUtc = DateTime.MinValue;
        lastActionUtc = DateTime.MinValue;
    }

    private void ProcessSaddlebag(DateTime now)
    {
        if (DecodedMapCount > 0 || InventoryMapCount > 0)
        {
            SetState(LeaderAutomationState.LookingForMap, "藏宝图已准备好");
            return;
        }

        if (!SaddlebagLoaded)
        {
            if (now - lastActionUtc >= TimeSpan.FromSeconds(4))
            {
                lastActionUtc = now;
                saddlebagOpenedUtc = now;
                Plugin.CommandManager.ProcessCommand("/saddlebag");
                StatusText = "正在打开陆行鸟鞍囊";
            }

            if (saddlebagOpenedUtc != DateTime.MinValue
                && now - saddlebagOpenedUtc > TimeSpan.FromSeconds(10))
            {
                SetState(LeaderAutomationState.Waiting, "无法读取陆行鸟鞍囊，请手动打开后点击“重新检查”");
            }

            return;
        }

        if (SaddlebagMapCount == 0)
        {
            SetState(LeaderAutomationState.Waiting, "没有找到可用的 G18 藏宝图");
            return;
        }

        if (TryMoveMapFromSaddlebag())
        {
            lastActionUtc = now;
            StatusText = "已从陆行鸟鞍囊取出一张 G18";
            return;
        }

        SetState(LeaderAutomationState.Waiting, "无法从鞍囊取图，请确认背包有空格");
    }

    private unsafe void ProcessDecipherMenu(DateTime now)
    {
        if (DecodedMapCount > 0)
        {
            SetState(LeaderAutomationState.LookingForMap, "藏宝图已解读");
            return;
        }

        if (now - stateEnteredUtc > TimeSpan.FromSeconds(8))
        {
            SetState(LeaderAutomationState.Error, "未能在解读菜单中找到所选藏宝图");
            return;
        }

        if (decipherMenuSelectionIssued)
        {
            return;
        }

        var addon = Plugin.GameGui.GetAddonByName<AddonSelectIconString>("SelectIconString", 1);
        if (addon == null || !addon->IsVisible)
        {
            return;
        }

        var targetName = GetSelectedMapName();
        var menu = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < menu.EntryCount; index++)
        {
            var pointer = menu.EntryNames[index].Value;
            if (pointer == null)
            {
                continue;
            }

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)pointer).TextValue;
            if (!text.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = stackalloc AtkValue[2];
            values[0].Type = AtkValueType.Bool;
            values[0].Byte = 1;
            values[1].Type = AtkValueType.Int;
            values[1].Int = index;
            addon->AtkUnitBase.FireCallback(2, values);
            decipherMenuSelectionIssued = true;
            pendingYesUntilUtc = now + TimeSpan.FromSeconds(6);
            SetState(LeaderAutomationState.ConfirmingDecipher, "正在确认解读藏宝图");
            diagnostics.Write("车头", $"在解读菜单第 {index} 项匹配到“{text}”。" );
            return;
        }
    }

    private void ProcessDecipherConfirmation(DateTime now)
    {
        if (DecodedMapCount > 0 || InventoryMapCount == 0)
        {
            SetState(LeaderAutomationState.LookingForMap, "藏宝图已解读");
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
        if (now - lastActionUtc < ActionRetryInterval)
        {
            return;
        }

        lastActionUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager == null
            || actionManager->GetActionStatus(ActionType.KeyItem, GargantuaskinDecodedEventItemId) != 0)
        {
            StatusText = "等待已解读藏宝图可使用";
            return;
        }

        actionManager->UseAction(ActionType.KeyItem, GargantuaskinDecodedEventItemId);
        SetState(LeaderAutomationState.WaitingForFlag, "已打开藏宝图，等待坐标插件创建旗标");
        diagnostics.Write("车头", $"已使用已解读藏宝图 #{GargantuaskinDecodedEventItemId}，等待新旗标。" );
    }

    private void ProcessWaitingForFlag(DateTime now)
    {
        var target = mapAutomation.RegisterOwnFlagFromGame();
        if (target != null)
        {
            currentOwnTarget = target;
            Plugin.CommandManager.ProcessCommand("/p <flag>");
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

        sawTreasureCombat = false;
        SetState(LeaderAutomationState.WaitingForCombat, "已打开宝箱，等待敌人出现");
    }

    private void ProcessWaitingForCombat(DateTime now)
    {
        if (Plugin.Condition[ConditionFlag.InCombat] || HasTreasureEnemies())
        {
            sawTreasureCombat = true;
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
        if (Plugin.Condition[ConditionFlag.InCombat] || HasTreasureEnemies())
        {
            noTreasureEnemySinceUtc = DateTime.MinValue;
            StatusText = "寻宝战斗中，由 AE Assist 与 BossMod Reborn 处理";
            return;
        }

        noTreasureEnemySinceUtc = noTreasureEnemySinceUtc == DateTime.MinValue ? now : noTreasureEnemySinceUtc;
        if (now - noTreasureEnemySinceUtc < TimeSpan.FromSeconds(2))
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
        if (now - portalSearchStartedUtc < TimeSpan.FromSeconds(12))
        {
            StatusText = "等待传送魔纹出现";
            return;
        }

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
        TryAcceptPendingYes(now);

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

        var chest = FindDungeonChest(now);
        if (chest != null)
        {
            if (ApproachAndInteract(chest, now, "宝物库宝箱"))
            {
                dungeonInteractionTimes[GetEntityId(chest)] = now;
                StatusText = "已打开宝物库宝箱，等待下一阶段";
            }

            return;
        }

        var hypnoslot = FindHypnoslot(now);
        if (hypnoslot != null)
        {
            if (ApproachAndInteract(hypnoslot, now, "潜网巡梦"))
            {
                dungeonInteractionTimes[GetEntityId(hypnoslot)] = now;
                pendingYesUntilUtc = now + TimeSpan.FromSeconds(8);
                StatusText = "已调查潜网巡梦，等待下一轮";
            }

            return;
        }

        StopOwnedNavigation();
        StatusText = "等待潜网巡梦、宝箱或下一场战斗";
    }

    private unsafe bool ApproachAndInteract(IGameObject gameObject, DateTime now, string label)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || gameObject.Address == 0 || !gameObject.IsTargetable)
        {
            return false;
        }

        var distance = HorizontalDistance(player.Position, gameObject.Position);
        if (distance > ObjectApproachRange)
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
                ownsNavigation = vnavmesh.MoveCloseTo(gameObject.Position, fly: false, ObjectApproachRange - 0.4f);
            }

            StatusText = $"正在前往{label} · {distance:F0}y";
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

        targetSystem->InteractWithObject((NativeGameObject*)gameObject.Address, false);
        diagnostics.Write("车头", $"交互 {label}：baseId={gameObject.BaseId}，entity={GetEntityId(gameObject)}。" );
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

        var targetPosition = currentOwnTarget.ToWorld(Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f);
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
            origin = currentOwnTarget.ToWorld(Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f);
        }

        return Plugin.ObjectTable
            .Where(obj => IsTreasureHuntObject(obj, ObjectKind.EventObj)
                && obj.IsTargetable
                && (origin == Vector3.Zero || HorizontalDistance(obj.Position, origin) <= OutdoorObjectSearchRange))
            .OrderBy(obj => origin == Vector3.Zero ? 0f : HorizontalDistance(obj.Position, origin))
            .FirstOrDefault();
    }

    private unsafe IGameObject? FindDungeonChest(DateTime now)
        => Plugin.ObjectTable
            .Where(obj => obj.Address != 0
                && obj.IsTargetable
                && ((NativeGameObject*)obj.Address)->ObjectKind == ObjectKind.Treasure
                && CanRetryDungeonObject(GetEntityId(obj), now))
            .OrderBy(obj => HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position))
            .FirstOrDefault();

    private unsafe IGameObject? FindHypnoslot(DateTime now)
        => Plugin.ObjectTable
            .Where(obj => obj.Address != 0
                && obj.IsTargetable
                && ((NativeGameObject*)obj.Address)->ObjectKind == ObjectKind.EventObj
                && string.Equals(obj.Name.TextValue, hypnoslotName, StringComparison.OrdinalIgnoreCase)
                && CanRetryDungeonObject(GetEntityId(obj), now))
            .OrderBy(obj => HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position ?? obj.Position, obj.Position))
            .FirstOrDefault();

    private bool CanRetryDungeonObject(uint entityId, DateTime now)
        => !dungeonInteractionTimes.TryGetValue(entityId, out var last)
            || now - last >= TimeSpan.FromSeconds(8);

    private unsafe bool HasTreasureEnemies()
        => Plugin.ObjectTable.Any(obj =>
        {
            if (obj.Address == 0 || !obj.IsTargetable)
            {
                return false;
            }

            var native = (NativeGameObject*)obj.Address;
            return native->ObjectKind == ObjectKind.BattleNpc
                && native->SubKind == (byte)BattleNpcSubKind.Combatant
                && native->EventId.ContentId == EventHandlerContent.TreasureHuntDirector
                && native->NamePlateIconId is 60094 or 60096;
        });

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
            DecodedMapCount = 0;
            InventoryMapCount = 0;
            SaddlebagMapCount = 0;
            SaddlebagLoaded = false;
            return;
        }

        DecodedMapCount = CountItem(manager, InventoryType.KeyItems, GargantuaskinDecodedEventItemId);
        InventoryMapCount = MainInventories.Sum(type => CountItem(manager, type, configuration.LeaderTreasureMapItemId));
        SaddlebagLoaded = SaddlebagInventories.All(type =>
        {
            var container = manager->GetInventoryContainer(type);
            return container != null && container->IsLoaded;
        });
        SaddlebagMapCount = SaddlebagInventories.Sum(type => CountItem(manager, type, configuration.LeaderTreasureMapItemId));
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

    private unsafe bool TryMoveMapFromSaddlebag()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        foreach (var sourceType in SaddlebagInventories)
        {
            var source = manager->GetInventoryContainer(sourceType);
            if (source == null || !source->IsLoaded)
            {
                continue;
            }

            for (var sourceIndex = 0; sourceIndex < source->Size; sourceIndex++)
            {
                var sourceItem = source->GetInventorySlot(sourceIndex);
                if (sourceItem == null || sourceItem->ItemId != configuration.LeaderTreasureMapItemId)
                {
                    continue;
                }

                if (!TryFindEmptyMainSlot(manager, out var destinationType, out var destinationIndex))
                {
                    return false;
                }

                manager->MoveItemSlot(
                    sourceType,
                    (ushort)sourceIndex,
                    destinationType,
                    (ushort)destinationIndex,
                    true);
                diagnostics.Write(
                    "车头",
                    $"从 {sourceType}[{sourceIndex}] 移动一张图到 {destinationType}[{destinationIndex}]。" );
                return true;
            }
        }

        return false;
    }

    private static unsafe bool TryFindEmptyMainSlot(
        InventoryManager* manager,
        out InventoryType inventoryType,
        out int slotIndex)
    {
        foreach (var type in MainInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item != null && item->ItemId == 0)
                {
                    inventoryType = type;
                    slotIndex = index;
                    return true;
                }
            }
        }

        inventoryType = default;
        slotIndex = -1;
        return false;
    }

    private string GetSelectedMapName()
    {
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(configuration.LeaderTreasureMapItemId);
            var name = item.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? "G18 藏宝图" : name;
        }
        catch
        {
            return "G18 藏宝图";
        }
    }

    private static string LoadHypnoslotName()
    {
        try
        {
            var value = Plugin.DataManager.GetExcelSheet<EObjName>().GetRow(HypnoslotNameRowId).Singular.ToString();
            return string.IsNullOrWhiteSpace(value) ? "潜网巡梦" : value;
        }
        catch
        {
            return "潜网巡梦";
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
        sawTreasureCombat = false;
        noTreasureEnemySinceUtc = DateTime.MinValue;
        portalSearchStartedUtc = DateTime.MinValue;
        pendingYesUntilUtc = DateTime.MinValue;
    }

    private void ResetCycle()
    {
        ResetOutdoorEncounter();
        decipherMenuSelectionIssued = false;
        saddlebagOpenedUtc = DateTime.MinValue;
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
}
