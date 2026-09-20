using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Soumen.Models;

namespace Soumen.Services;

public sealed class MapFlagAutomation : IDisposable
{
    private const float NearbyMapCoordinateTolerance = 0.5f;
    private const float ProgressDistance = 2f;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PartyRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NavigationRetryInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly VNavmeshIpc vnavmesh;
    private readonly ExternalPluginCoordinator externalPlugins;
    private readonly TeleportService teleporter = new();
    private readonly Dictionary<string, MapFlagTarget> destinations = [];

    private MapFlagTarget? activeTarget;
    private Vector3? destination;
    private RoutePlan? routePlan;
    private Vector3 progressAnchor;
    private float progressDistance = float.MaxValue;
    private Vector3? teleportArrivalPosition;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime nextPartyRefreshUtc = DateTime.MinValue;
    private DateTime lastMountAttemptUtc = DateTime.MinValue;
    private DateTime lastDismountAttemptUtc = DateTime.MinValue;
    private DateTime lastNavigationAttemptUtc = DateTime.MinValue;
    private DateTime lastProgressUtc = DateTime.UtcNow;
    private DateTime teleportIssuedUtc = DateTime.MinValue;
    private long? teleportedTargetSerial;
    private long serial;
    private int recoveryAttempts;
    private bool manualSelection;
    private bool paused;
    private bool teleportSawLoading;
    private bool disposed;

    public MapFlagAutomation(Configuration configuration)
    {
        this.configuration = configuration;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface);
        externalPlugins = new ExternalPluginCoordinator(configuration);

        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;

        if (configuration.Enabled)
        {
            externalPlugins.StartRuntime();
            SetState(AutomationState.Idle, "等待小队坐标");
        }
        else
        {
            SetState(AutomationState.Disabled, "自动化已关闭");
        }
    }

    public AutomationState State { get; private set; }

    public string StatusText { get; private set; } = "初始化";

    public MapFlagTarget? ActiveTarget => activeTarget;

    public bool IsManualSelection => manualSelection;

    public bool IsPaused => paused;

    public IReadOnlyList<MapFlagTarget> Destinations
        => destinations.Values.OrderByDescending(target => target.ReceivedAtUtc).ToList();

    public bool VnavmeshInstalled => vnavmesh.IsInstalled;

    public bool VnavmeshReady => vnavmesh.IsReady();

    public bool AeAssistInstalled => externalPlugins.AeAssistInstalled;

    public bool BossModRebornInstalled => externalPlugins.BossModRebornInstalled;

    public void SetEnabled(bool enabled)
    {
        configuration.Enabled = enabled;
        configuration.Save();

        if (!enabled)
        {
            paused = false;
            ResetNavigation(clearDestinations: true);
            externalPlugins.StopRuntime();
            SetState(AutomationState.Disabled, "自动化已关闭");
            return;
        }

        externalPlugins.StartRuntime();
        SetState(AutomationState.Idle, "等待小队坐标");
    }

    public void SetPaused(bool value)
    {
        if (!configuration.Enabled || paused == value)
        {
            return;
        }

        paused = value;
        if (paused)
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);
            routePlan = null;
            SetState(AutomationState.Paused, activeTarget == null ? "已暂停" : "已暂停，当前目的地已保留");
            return;
        }

        if (activeTarget != null)
        {
            destination = null;
            SetState(AutomationState.WaitingForPlayer, "继续当前目的地");
        }
        else
        {
            SetState(AutomationState.Idle, "等待小队坐标");
        }
    }

    public void Stop(string reason = "导航已停止")
    {
        ResetNavigation(clearDestinations: false);
        SetState(configuration.Enabled ? AutomationState.Idle : AutomationState.Disabled,
            configuration.Enabled ? reason : "自动化已关闭");
    }

    public bool NavigateTo(long targetSerial)
    {
        var target = destinations.Values.FirstOrDefault(entry => entry.Serial == targetSerial);
        if (target == null || !configuration.Enabled)
        {
            return false;
        }

        SelectTarget(target, isManual: true);
        return true;
    }

    public float? DistanceTo(MapFlagTarget target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.ClientState.TerritoryType != target.TerritoryId)
        {
            return null;
        }

        return HorizontalDistance(player.Position, target.ToWorld(player.Position.Y));
    }

    public int NearbyDestinationCount(MapFlagTarget target)
        => destinations.Values.Count(other => IsNearby(target, other));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        vnavmesh.Stop();
        externalPlugins.StopRuntime();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!configuration.Enabled
            || message.LogKind is not (XivChatType.Party or XivChatType.CrossParty))
        {
            return;
        }

        var mapLink = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null)
        {
            return;
        }

        var sender = ResolveSender(message.Sender);
        var target = new MapFlagTarget(
            ++serial,
            message.Sender.TextValue,
            sender.Name,
            sender.WorldId,
            sender.ContentId,
            mapLink.TerritoryType.RowId,
            mapLink.Map.RowId,
            mapLink.RawX,
            mapLink.RawY,
            mapLink.XCoord,
            mapLink.YCoord,
            mapLink.PlaceName,
            DateTime.UtcNow);

        foreach (var duplicate in destinations.Values
                     .Where(existing => existing.SenderName == target.SenderName
                         && existing.SenderWorldId == target.SenderWorldId
                         && existing.SenderKey != target.SenderKey)
                     .Select(existing => existing.SenderKey)
                     .ToList())
        {
            destinations.Remove(duplicate);
        }

        destinations[target.SenderKey] = target;

        if (activeTarget?.SenderKey == target.SenderKey)
        {
            SelectTarget(target, manualSelection);
        }
        else if (!manualSelection)
        {
            SelectTarget(target, isManual: false);
        }

        Plugin.Log.Information(
            "Received party destination from {Sender}: {Place} {X:F1}, {Y:F1}.",
            target.Sender,
            target.PlaceName,
            target.MapX,
            target.MapY);
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
        {
            return;
        }

        nextUpdateUtc = now + UpdateInterval;

        if (!configuration.Enabled)
        {
            return;
        }

        externalPlugins.RefreshRuntime();
        externalPlugins.SetNavigating(State == AutomationState.Navigating);

        if (now >= nextPartyRefreshUtc && !IsLoadingOrOccupied())
        {
            nextPartyRefreshUtc = now + PartyRefreshInterval;
            RemoveDepartedMembers();
        }

        if (paused)
        {
            if (State != AutomationState.Paused)
            {
                SetState(AutomationState.Paused, "已暂停");
            }

            return;
        }

        switch (State)
        {
            case AutomationState.Disabled:
                SetState(AutomationState.Idle, "等待小队坐标");
                break;
            case AutomationState.Idle:
            case AutomationState.Error:
                break;
            case AutomationState.Paused:
                SetState(activeTarget == null ? AutomationState.Idle : AutomationState.WaitingForPlayer,
                    activeTarget == null ? "等待小队坐标" : "继续当前目的地");
                break;
            case AutomationState.WaitingForPlayer:
                ProcessPlayerReady();
                break;
            case AutomationState.PlanningRoute:
                ProcessRoutePlanning();
                break;
            case AutomationState.Teleporting:
                ProcessTeleporting(now);
                break;
            case AutomationState.Mounting:
                ProcessMounting(now);
                break;
            case AutomationState.WaitingForVnavmesh:
                ProcessVnavReady();
                break;
            case AutomationState.Navigating:
                ProcessNavigation(now);
                break;
            case AutomationState.Landing:
                ProcessLanding(now);
                break;
            case AutomationState.Dismounting:
                ProcessDismounting(now);
                break;
        }
    }

    private void SelectTarget(MapFlagTarget target, bool isManual)
    {
        activeTarget = target;
        manualSelection = isManual;
        destination = null;
        routePlan = null;
        teleportArrivalPosition = null;
        teleportedTargetSerial = null;
        recoveryAttempts = 0;
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        ClearGameTarget();

        if (paused)
        {
            SetState(AutomationState.Paused, $"已选择 {target.Sender} 的坐标，继续后导航");
        }
        else
        {
            SetState(AutomationState.WaitingForPlayer,
                isManual ? $"已手动选择 {target.Sender} 的坐标" : "正在准备最新坐标");
        }
    }

    private void ProcessPlayerReady()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer == null || IsLoadingOrOccupied())
        {
            StatusText = "读图或过场中，目的地已保留";
            return;
        }

        if (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            StatusText = $"目标位于其他地图（Territory {activeTarget.TerritoryId}），等待进入该地图";
            return;
        }

        if (!vnavmesh.IsInstalled)
        {
            Fail("vnavmesh 未安装或未加载");
            return;
        }

        SetMapFlag(activeTarget);
        destination = ResolveDestination(activeTarget);
        if (destination == null)
        {
            Fail("无法解析目的地");
            return;
        }

        if (configuration.AutoTeleport
            && !Plugin.Condition[ConditionFlag.InCombat]
            && teleportedTargetSerial != activeTarget.Serial
            && BeginRoutePlanning(recovery: false))
        {
            return;
        }

        ContinueToMountOrNavigate();
    }

    private void ContinueToMountOrNavigate()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (configuration.AutoMount && !IsMounted() && !Plugin.Condition[ConditionFlag.InCombat])
        {
            SetState(AutomationState.Mounting, "正在上坐骑");
        }
        else
        {
            SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
        }
    }

    private unsafe void ProcessMounting(DateTime now)
    {
        if (IsMounted())
        {
            SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
            return;
        }

        if (IsLoadingOrOccupied())
        {
            StatusText = "人物状态变化中，等待上坐骑";
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            SetState(AutomationState.WaitingForVnavmesh, "战斗中改为步行导航");
            return;
        }

        if (now - lastMountAttemptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lastMountAttemptUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager != null
            && actionManager->GetActionStatus(ActionType.GeneralAction, 9) == 0)
        {
            actionManager->UseAction(ActionType.GeneralAction, 9);
        }
    }

    private void ProcessVnavReady()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            StatusText = "等待 vnavmesh 生成导航网格";
            return;
        }

        if (!StartNavigation())
        {
            StatusText = "vnavmesh 暂时无法开始导航，正在等待重试";
        }
    }

    private void ProcessNavigation(DateTime now)
    {
        if (activeTarget == null || destination == null)
        {
            Fail("导航目标丢失");
            return;
        }

        if (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);
            destination = null;
            SetState(AutomationState.WaitingForPlayer, "地图发生变化，等待重新规划");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var distance = HorizontalDistance(player.Position, destination.Value);
        if (distance <= Math.Clamp(configuration.ArrivalTolerance, 3f, 30f))
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);

            if (configuration.AutoDismount && IsMounted())
            {
                SetState(Plugin.Condition[ConditionFlag.InFlight]
                        ? AutomationState.Landing
                        : AutomationState.Dismounting,
                    Plugin.Condition[ConditionFlag.InFlight] ? "正在落地" : "正在下坐骑");
            }
            else
            {
                CompleteArrival();
            }

            return;
        }

        if (CheckForStuck(now, player.Position, distance))
        {
            return;
        }

        StatusText = $"前往 {activeTarget.PlaceName}  X {activeTarget.MapX:F1}  Y {activeTarget.MapY:F1} · {distance:F0}y";
        if (vnavmesh.IsBusy() || now - lastNavigationAttemptUtc < NavigationRetryInterval)
        {
            return;
        }

        if (!StartNavigation())
        {
            StatusText = $"路线中断，正在重新规划 · 距离 {distance:F0}y";
        }
    }

    private bool CheckForStuck(DateTime now, Vector3 position, float distance)
    {
        if (IsLoadingOrOccupied()
            || Plugin.Condition.Any(
                ConditionFlag.Jumping,
                ConditionFlag.Jumping61,
                ConditionFlag.MountOrOrnamentTransition))
        {
            ResetProgress(position, distance, now);
            return false;
        }

        if (HorizontalDistance(position, progressAnchor) >= ProgressDistance
            || progressDistance - distance >= ProgressDistance)
        {
            ResetProgress(position, distance, now);
            return false;
        }

        if ((now - lastProgressUtc).TotalSeconds < Math.Clamp(configuration.StuckSeconds, 4f, 30f))
        {
            return false;
        }

        if (recoveryAttempts == 0)
        {
            recoveryAttempts++;
            vnavmesh.Stop();
            ResetProgress(position, distance, now);
            StatusText = "检测到移动停滞，正在重新规划路线";
            _ = StartNavigation();
            return true;
        }

        if (configuration.TeleportWhenStuck
            && teleportedTargetSerial != activeTarget?.Serial
            && BeginRoutePlanning(recovery: true))
        {
            return true;
        }

        Fail("重新规划后仍无法前进，已停止等待人工处理");
        return true;
    }

    private unsafe void ProcessLanding(DateTime now)
    {
        if (!IsMounted())
        {
            CompleteArrival();
            return;
        }

        if (!Plugin.Condition[ConditionFlag.InFlight])
        {
            SetState(AutomationState.Dismounting, "已落地，正在下坐骑");
            return;
        }

        if (now - lastDismountAttemptUtc < ActionRetryInterval)
        {
            return;
        }

        lastDismountAttemptUtc = now;
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
    }

    private unsafe void ProcessDismounting(DateTime now)
    {
        if (!IsMounted())
        {
            CompleteArrival();
            return;
        }

        if (Plugin.Condition.Any(
                ConditionFlag.Jumping,
                ConditionFlag.Jumping61,
                ConditionFlag.MountOrOrnamentTransition))
        {
            return;
        }

        if (Plugin.Condition[ConditionFlag.InFlight])
        {
            SetState(AutomationState.Landing, "正在落地");
            return;
        }

        if (now - lastDismountAttemptUtc < ActionRetryInterval)
        {
            return;
        }

        lastDismountAttemptUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager->GetActionStatus(ActionType.Mount, 0) == 0)
        {
            actionManager->UseAction(ActionType.Mount, 0);
        }
        else
        {
            actionManager->UseAction(ActionType.GeneralAction, 23);
        }
    }

    private bool StartNavigation()
    {
        if (activeTarget == null || Plugin.ObjectTable.LocalPlayer == null)
        {
            return false;
        }

        destination ??= ResolveDestination(activeTarget);
        if (destination == null)
        {
            return false;
        }

        SetMapFlag(activeTarget);
        var fly = configuration.UseFlight && IsMounted();
        var started = vnavmesh.MoveCloseTo(
            destination.Value,
            fly,
            Math.Clamp(configuration.ArrivalTolerance / 2f, 2f, 8f));

        lastNavigationAttemptUtc = DateTime.UtcNow;
        if (!started)
        {
            return false;
        }

        externalPlugins.SetNavigating(true);
        var player = Plugin.ObjectTable.LocalPlayer.Position;
        ResetProgress(player, HorizontalDistance(player, destination.Value), DateTime.UtcNow);
        SetState(AutomationState.Navigating,
            $"开始{(fly ? "飞行" : "步行")}导航：{activeTarget.MapX:F1}, {activeTarget.MapY:F1}");
        return true;
    }

    private bool BeginRoutePlanning(bool recovery)
    {
        if (activeTarget == null || destination == null || Plugin.ObjectTable.LocalPlayer == null || !vnavmesh.IsReady())
        {
            return false;
        }

        var candidates = teleporter.GetCandidates(activeTarget.TerritoryId);
        if (candidates.Count == 0)
        {
            return false;
        }

        var fly = configuration.UseFlight;
        var directTask = vnavmesh.Pathfind(Plugin.ObjectTable.LocalPlayer.Position, destination.Value, fly);
        var candidatePaths = candidates
            .Select(candidate => new CandidatePath(candidate, vnavmesh.Pathfind(candidate.Position, destination.Value, fly)))
            .Where(path => path.Task != null)
            .ToList();

        if (candidatePaths.Count == 0)
        {
            return false;
        }

        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        routePlan = new RoutePlan(activeTarget.Serial, recovery, destination.Value, directTask, candidatePaths);
        SetState(AutomationState.PlanningRoute,
            recovery ? "重新寻路仍停滞，正在计算以太水晶方案" : "正在比较直达与传送路线");
        return true;
    }

    private void ProcessRoutePlanning()
    {
        if (routePlan == null || activeTarget == null || routePlan.TargetSerial != activeTarget.Serial)
        {
            routePlan = null;
            SetState(AutomationState.WaitingForPlayer, "目的地已变化，重新规划");
            return;
        }

        var tasks = routePlan.CandidatePaths
            .Where(path => path.Task != null)
            .Select(path => path.Task!)
            .ToList();
        if ((routePlan.DirectTask != null && !routePlan.DirectTask.IsCompleted)
            || tasks.Any(task => !task.IsCompleted))
        {
            return;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? routePlan.Destination;
        var directLength = GetPathLength(routePlan.DirectTask, playerPosition);
        if (!float.IsFinite(directLength))
        {
            directLength = HorizontalDistance(playerPosition, routePlan.Destination);
        }

        CandidatePath? best = null;
        var bestLength = float.PositiveInfinity;
        foreach (var candidate in routePlan.CandidatePaths)
        {
            var length = GetPathLength(candidate.Task, candidate.Candidate.Position);
            if (length < bestLength)
            {
                best = candidate;
                bestLength = length;
            }
        }

        var shouldTeleport = best != null
            && float.IsFinite(bestLength)
            && (routePlan.Recovery
                || directLength - (bestLength + Math.Clamp(configuration.TeleportPenaltyDistance, 0f, 1000f))
                    >= Math.Clamp(configuration.MinimumTeleportSaving, 0f, 2000f));

        routePlan = null;
        if (shouldTeleport && best != null && teleporter.Teleport(best.Candidate.Id))
        {
            teleportedTargetSerial = activeTarget.Serial;
            teleportArrivalPosition = best.Candidate.Position;
            teleportIssuedUtc = DateTime.UtcNow;
            teleportSawLoading = false;
            SetState(AutomationState.Teleporting, $"正在传送至以太水晶 #{best.Candidate.Id}");
            return;
        }

        ContinueToMountOrNavigate();
    }

    private void ProcessTeleporting(DateTime now)
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (IsLoadingOrOccupied())
        {
            teleportSawLoading = true;
            StatusText = "传送读图中";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var arrivedNearCrystal = player != null
            && teleportArrivalPosition != null
            && HorizontalDistance(player.Position, teleportArrivalPosition.Value) < 100f;

        if (teleportSawLoading
            || (arrivedNearCrystal && now - teleportIssuedUtc > TimeSpan.FromSeconds(8)))
        {
            destination = null;
            teleportArrivalPosition = null;
            SetState(AutomationState.WaitingForPlayer, "传送完成，重新规划前往坐标");
            return;
        }

        if (now - teleportIssuedUtc > TimeSpan.FromSeconds(20))
        {
            teleportArrivalPosition = null;
            ContinueToMountOrNavigate();
        }
    }

    private void CompleteArrival()
    {
        var completed = activeTarget;
        if (completed != null)
        {
            var keys = destinations
                .Where(pair => IsNearby(completed, pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in keys)
            {
                destinations.Remove(key);
            }

            Plugin.Log.Information(
                "Arrived at destination {X:F1}, {Y:F1}; removed {Count} nearby destination(s).",
                completed.MapX,
                completed.MapY,
                keys.Count);
        }

        activeTarget = null;
        destination = null;
        routePlan = null;
        manualSelection = false;
        recoveryAttempts = 0;
        externalPlugins.SetNavigating(false);
        SetState(AutomationState.Idle, "已到达，等待挖宝、战斗或新的小队坐标");
    }

    private void Fail(string reason)
    {
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        Plugin.Log.Error("{Reason}", reason);
        activeTarget = null;
        destination = null;
        routePlan = null;
        manualSelection = false;
        SetState(AutomationState.Error, reason);
    }

    private void ResetNavigation(bool clearDestinations)
    {
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        activeTarget = null;
        destination = null;
        routePlan = null;
        teleportArrivalPosition = null;
        manualSelection = false;
        recoveryAttempts = 0;
        ClearGameTarget();
        if (clearDestinations)
        {
            destinations.Clear();
        }
    }

    private unsafe void RemoveDepartedMembers()
    {
        var groupManager = GroupManager.Instance();
        if (groupManager == null)
        {
            return;
        }

        var group = groupManager->GetGroup();
        if (group == null)
        {
            return;
        }

        var contentIds = new HashSet<ulong>();
        var nameWorldKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < group->MemberCount; ++i)
        {
            var member = group->PartyMembers[i];
            contentIds.Add(member.ContentId);
            nameWorldKeys.Add($"{member.NameString}@{member.HomeWorld}");
        }

        var departed = destinations
            .Where(pair => pair.Value.SenderContentId != 0
                ? !contentIds.Contains(pair.Value.SenderContentId)
                : !nameWorldKeys.Contains($"{pair.Value.SenderName}@{pair.Value.SenderWorldId}"))
            .Select(pair => pair.Key)
            .ToList();

        if (departed.Count == 0)
        {
            return;
        }

        var activeDeparted = activeTarget != null && departed.Contains(activeTarget.SenderKey);
        foreach (var key in departed)
        {
            destinations.Remove(key);
        }

        if (activeDeparted)
        {
            ResetNavigation(clearDestinations: false);
            SetState(paused ? AutomationState.Paused : AutomationState.Idle,
                paused ? "当前坐标发送者已离队，已取消该目的地" : "当前坐标发送者已离队，等待新坐标");
        }
    }

    private static unsafe SenderIdentity ResolveSender(SeString sender)
    {
        var source = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        var name = source?.PlayerName ?? sender.TextValue;
        var worldId = source?.World.RowId ?? 0;
        ulong contentId = 0;

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        if (group != null)
        {
            for (var i = 0; i < group->MemberCount; ++i)
            {
                var member = group->PartyMembers[i];
                if (member.HomeWorld == worldId && member.NameString == name)
                {
                    contentId = member.ContentId;
                    break;
                }
            }
        }

        return new SenderIdentity(name, worldId, contentId);
    }

    private Vector3? ResolveDestination(MapFlagTarget target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        SetMapFlag(target);
        var fallback = target.ToWorld(player.Position.Y);
        return vnavmesh.ResolveFlagPoint() ?? vnavmesh.NearestPoint(fallback) ?? fallback;
    }

    private static float GetPathLength(Task<List<Vector3>>? task, Vector3 origin)
    {
        if (task == null || !task.IsCompletedSuccessfully || task.Result.Count == 0)
        {
            return float.PositiveInfinity;
        }

        var length = 0f;
        var previous = origin;
        foreach (var waypoint in task.Result)
        {
            length += Vector3.Distance(previous, waypoint);
            previous = waypoint;
        }

        return length;
    }

    private static bool IsNearby(MapFlagTarget left, MapFlagTarget right)
        => left.TerritoryId == right.TerritoryId
            && left.MapId == right.MapId
            && Math.Abs(left.MapX - right.MapX) <= NearbyMapCoordinateTolerance
            && Math.Abs(left.MapY - right.MapY) <= NearbyMapCoordinateTolerance;

    private static bool IsMounted()
        => Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];

    private static bool IsLoadingOrOccupied()
        => Plugin.Condition.Any(
            ConditionFlag.BetweenAreas,
            ConditionFlag.BetweenAreas51,
            ConditionFlag.OccupiedInCutSceneEvent,
            ConditionFlag.WatchingCutscene,
            ConditionFlag.WatchingCutscene78,
            ConditionFlag.LoggingOut);

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static void ClearGameTarget()
    {
        Plugin.TargetManager.Target = null;
        Plugin.TargetManager.SoftTarget = null;
    }

    private static unsafe void SetMapFlag(MapFlagTarget target)
    {
        try
        {
            var agent = AgentMap.Instance();
            if (agent == null)
            {
                return;
            }

            agent->FlagMarkerCount = 0;
            agent->SetFlagMapMarker(
                target.TerritoryId,
                target.MapId,
                new Vector3(target.RawX / 1000f, 0f, target.RawY / 1000f));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to set the map flag for target {Serial}.", target.Serial);
        }
    }

    private void ResetProgress(Vector3 position, float distance, DateTime now)
    {
        progressAnchor = position;
        progressDistance = distance;
        lastProgressUtc = now;
    }

    private void SetState(AutomationState state, string status)
    {
        State = state;
        StatusText = status;
    }

    private sealed record SenderIdentity(string Name, uint WorldId, ulong ContentId);

    private sealed record CandidatePath(AetheryteCandidate Candidate, Task<List<Vector3>>? Task);

    private sealed record RoutePlan(
        long TargetSerial,
        bool Recovery,
        Vector3 Destination,
        Task<List<Vector3>>? DirectTask,
        IReadOnlyList<CandidatePath> CandidatePaths);
}
