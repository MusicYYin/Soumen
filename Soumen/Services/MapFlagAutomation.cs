using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Soumen.Models;

namespace Soumen.Services;

public sealed class MapFlagAutomation : IDisposable
{
    public const uint LivingMemoryTerritoryId = 1192;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VnavTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private readonly VNavmeshIpc vnavmesh;
    private readonly List<string> recentEvents = [];

    private MapFlagTarget? activeTarget;
    private MapFlagTarget? lastSeenTarget;
    private Vector3? destination;
    private DateTime stateStartedUtc = DateTime.UtcNow;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime lastMountAttemptUtc = DateTime.MinValue;
    private DateTime lastDismountAttemptUtc = DateTime.MinValue;
    private DateTime lastNavigationAttemptUtc = DateTime.MinValue;
    private DateTime navigationStartedUtc = DateTime.MinValue;
    private long serial;
    private bool disposed;

    public MapFlagAutomation(Configuration configuration)
    {
        this.configuration = configuration;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface);

        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;

        SetState(configuration.Enabled ? AutomationState.Idle : AutomationState.Disabled,
            configuration.Enabled ? "等待队伍坐标" : "自动化已关闭");
    }

    public AutomationState State { get; private set; }

    public string StatusText { get; private set; } = "初始化";

    public MapFlagTarget? LatestTarget => activeTarget ?? lastSeenTarget;

    public bool VnavmeshInstalled => vnavmesh.IsInstalled;

    public bool VnavmeshReady => vnavmesh.IsReady();

    public IReadOnlyList<string> RecentEvents => recentEvents;

    public void SetEnabled(bool enabled)
    {
        configuration.Enabled = enabled;
        configuration.Save();

        if (!enabled)
        {
            Stop("自动化已关闭", clearTarget: true);
            SetState(AutomationState.Disabled, "自动化已关闭");
            return;
        }

        SetState(AutomationState.Idle, "等待队伍坐标");
        AddEvent("自动化已开启");
    }

    public void Stop(string reason = "已手动停止", bool clearTarget = true)
    {
        vnavmesh.Stop();
        activeTarget = null;
        destination = null;

        if (clearTarget)
        {
            ClearGameTarget();
        }

        AddEvent(reason);
        SetState(configuration.Enabled ? AutomationState.Idle : AutomationState.Disabled,
            configuration.Enabled ? reason : "自动化已关闭");
    }

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
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!configuration.Enabled
            || message.LogKind is not (XivChatType.Party or XivChatType.CrossParty))
        {
            return;
        }

        var payload = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (payload == null)
        {
            return;
        }

        var territoryId = payload.TerritoryType.RowId;
        if (configuration.LivingMemoryOnly && territoryId != LivingMemoryTerritoryId)
        {
            AddEvent($"忽略非活着的记忆坐标：Territory {territoryId}");
            return;
        }

        var now = DateTime.UtcNow;
        if (lastSeenTarget != null
            && lastSeenTarget.TerritoryId == territoryId
            && lastSeenTarget.RawX == payload.RawX
            && lastSeenTarget.RawY == payload.RawY
            && now - lastSeenTarget.ReceivedAtUtc < TimeSpan.FromSeconds(20))
        {
            return;
        }

        var target = new MapFlagTarget(
            ++serial,
            message.Sender.TextValue,
            territoryId,
            payload.Map.RowId,
            payload.RawX,
            payload.RawY,
            payload.XCoord,
            payload.YCoord,
            payload.PlaceName,
            now);

        lastSeenTarget = target;
        activeTarget = target;
        destination = null;
        vnavmesh.Stop();
        ClearGameTarget();

        AddEvent($"收到 #{target.Serial} {target.Sender}：{target.PlaceName} {target.MapX:F1}, {target.MapY:F1}");
        SetState(AutomationState.Debouncing, "等待最新坐标（防抖）");
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
            if (State != AutomationState.Disabled)
            {
                Stop("自动化已关闭", clearTarget: false);
                SetState(AutomationState.Disabled, "自动化已关闭");
            }

            return;
        }

        switch (State)
        {
            case AutomationState.Disabled:
                SetState(AutomationState.Idle, "等待队伍坐标");
                break;
            case AutomationState.Idle:
            case AutomationState.Error:
                break;
            case AutomationState.Debouncing:
                ProcessDebounce(now);
                break;
            case AutomationState.WaitingForPlayer:
                ProcessPlayerReady(now);
                break;
            case AutomationState.Mounting:
                ProcessMounting(now);
                break;
            case AutomationState.WaitingForVnavmesh:
                ProcessVnavReady(now);
                break;
            case AutomationState.Navigating:
                ProcessNavigation(now);
                break;
            case AutomationState.Dismounting:
                ProcessDismounting(now);
                break;
        }
    }

    private void ProcessDebounce(DateTime now)
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待队伍坐标");
            return;
        }

        var debounce = TimeSpan.FromMilliseconds(Math.Clamp(configuration.DebounceMilliseconds, 100, 3000));
        if (now - activeTarget.ReceivedAtUtc < debounce)
        {
            return;
        }

        SetState(AutomationState.WaitingForPlayer, "等待人物与地图状态稳定");
    }

    private void ProcessPlayerReady(DateTime now)
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待队伍坐标");
            return;
        }

        if (now - stateStartedUtc > ReadyTimeout)
        {
            Fail("等待人物状态稳定超时");
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer == null || IsLoadingOrOccupied())
        {
            StatusText = "传送、读图或过场中，保留最新坐标";
            return;
        }

        if (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            AddEvent($"忽略异地图坐标：当前 {Plugin.ClientState.TerritoryType}，目标 {activeTarget.TerritoryId}");
            activeTarget = null;
            SetState(AutomationState.Idle, "异地图坐标已忽略");
            return;
        }

        if (!vnavmesh.IsInstalled)
        {
            Fail("vnavmesh 未安装或未加载");
            return;
        }

        vnavmesh.Stop();
        ClearGameTarget();
        SetMapFlag(activeTarget);

        if (configuration.AutoMount && !IsMounted())
        {
            SetState(AutomationState.Mounting, "正在上坐骑");
            return;
        }

        SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
    }

    private unsafe void ProcessMounting(DateTime now)
    {
        if (IsMounted())
        {
            AddEvent("已上坐骑");
            SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
            return;
        }

        if (now - stateStartedUtc > MountTimeout)
        {
            Fail("上坐骑超时");
            return;
        }

        if (IsLoadingOrOccupied() || Plugin.Condition[ConditionFlag.InCombat])
        {
            StatusText = "当前无法上坐骑，继续等待";
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

    private void ProcessVnavReady(DateTime now)
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待队伍坐标");
            return;
        }

        if (now - stateStartedUtc > VnavTimeout)
        {
            Fail("等待 vnavmesh 就绪超时");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            return;
        }

        if (!StartNavigation())
        {
            Fail("vnavmesh 未能开始导航");
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
            Fail("导航中地图发生变化");
            return;
        }

        if (now - navigationStartedUtc > NavigationTimeout)
        {
            Fail("导航超时");
            return;
        }

        if (configuration.ClearTargetWhileNavigating)
        {
            ClearGameTarget();
        }

        if (vnavmesh.IsBusy())
        {
            StatusText = $"飞往 {activeTarget.PlaceName} {activeTarget.MapX:F1}, {activeTarget.MapY:F1}";
            return;
        }

        var distance = HorizontalDistance(Plugin.ObjectTable.LocalPlayer?.Position, destination.Value);
        if (distance <= Math.Clamp(configuration.ArrivalTolerance, 3f, 30f))
        {
            AddEvent($"已到达坐标附近，距离 {distance:F1}");
            vnavmesh.Stop();

            if (configuration.AutoDismount && IsMounted())
            {
                SetState(AutomationState.Dismounting, "正在下坐骑");
            }
            else
            {
                Complete();
            }

            return;
        }

        if (now - lastNavigationAttemptUtc < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        AddEvent($"导航中断，距离目标 {distance:F1}，尝试续飞");
        if (!StartNavigation())
        {
            Fail("导航中断且无法续飞");
        }
    }

    private unsafe void ProcessDismounting(DateTime now)
    {
        if (!IsMounted())
        {
            AddEvent("已下坐骑");
            Complete();
            return;
        }

        if (now - stateStartedUtc > DismountTimeout)
        {
            Fail("下坐骑超时");
            return;
        }

        if (Plugin.Condition.Any(
                ConditionFlag.InFlight,
                ConditionFlag.Jumping,
                ConditionFlag.Jumping61,
                ConditionFlag.MountOrOrnamentTransition))
        {
            return;
        }

        if (now - lastDismountAttemptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lastDismountAttemptUtc = now;
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
    }

    private bool StartNavigation()
    {
        if (activeTarget == null || Plugin.ObjectTable.LocalPlayer == null)
        {
            return false;
        }

        SetMapFlag(activeTarget);
        var player = Plugin.ObjectTable.LocalPlayer;
        var fallback = activeTarget.ToWorld(player.Position.Y);
        destination = vnavmesh.ResolveFlagPoint() ?? vnavmesh.NearestPoint(fallback) ?? fallback;

        var fly = configuration.UseFlight && IsMounted();
        var wasNavigating = State == AutomationState.Navigating;
        var started = vnavmesh.MoveCloseTo(
            destination.Value,
            fly,
            Math.Clamp(configuration.ArrivalTolerance / 2f, 2f, 8f));

        lastNavigationAttemptUtc = DateTime.UtcNow;
        if (started)
        {
            var status = $"{(wasNavigating ? "继续" : "开始")}{(fly ? "飞行" : "步行")}导航：{activeTarget.MapX:F1}, {activeTarget.MapY:F1}";
            if (wasNavigating)
            {
                StatusText = status;
            }
            else
            {
                navigationStartedUtc = DateTime.UtcNow;
                SetState(AutomationState.Navigating, status);
            }
        }

        return started;
    }

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

    private static float HorizontalDistance(Vector3? player, Vector3 target)
    {
        if (player == null)
        {
            return float.MaxValue;
        }

        var dx = player.Value.X - target.X;
        var dz = player.Value.Z - target.Z;
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

    private void Complete()
    {
        var completed = activeTarget;
        activeTarget = null;
        destination = null;
        SetState(AutomationState.Idle, "流程完成，等待下一个队伍坐标");
        if (completed != null)
        {
            PrintChat($"已到达 #{completed.Serial} {completed.PlaceName} {completed.MapX:F1}, {completed.MapY:F1}");
        }
    }

    private void Fail(string reason)
    {
        vnavmesh.Stop();
        AddEvent(reason);
        PrintChat(reason, error: true);
        activeTarget = null;
        destination = null;
        SetState(AutomationState.Error, reason);
    }

    private void SetState(AutomationState state, string status)
    {
        State = state;
        StatusText = status;
        stateStartedUtc = DateTime.UtcNow;
    }

    private void AddEvent(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        recentEvents.Insert(0, line);
        if (recentEvents.Count > 30)
        {
            recentEvents.RemoveAt(recentEvents.Count - 1);
        }

        Plugin.Log.Information("{Message}", message);
    }

    private void PrintChat(string message, bool error = false)
    {
        if (!configuration.ShowChatLogs)
        {
            return;
        }

        if (error)
        {
            Plugin.ChatGui.PrintError(message, "Soumen");
        }
        else
        {
            Plugin.ChatGui.Print(message, "Soumen");
        }
    }
}
