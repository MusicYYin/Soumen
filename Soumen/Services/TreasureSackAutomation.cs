using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Soumen.Services;

public sealed class TreasureSackAutomation : IDisposable
{
    internal const uint GoldSackDataId = 0x1EBE47;
    internal const uint SilverSackDataId = 0x1EBE48;
    private const float CollectionRange = 3f;
    private const int MaxSacksPerRoute = 40;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan RouteRetryInterval = TimeSpan.FromMilliseconds(200);

    private readonly Configuration configuration;
    private readonly MapFlagAutomation mapAutomation;
    private readonly DiagnosticLogger diagnostics;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Dictionary<nint, DateTime> interactionTimes = [];

    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime lastRouteAttemptUtc = DateTime.MinValue;
    private Task<SackRoute?>? routeBuildTask;
    private int routeGeneration;
    private float? previousPathTolerance;
    private bool ownsNavigation;

    public TreasureSackAutomation(
        Configuration configuration,
        MapFlagAutomation mapAutomation,
        DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.mapAutomation = mapAutomation;
        this.diagnostics = diagnostics;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface, diagnostics);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        ResetRoute(stopNavigation: true);
    }

    internal static bool HasCollectibleSacks()
        => Plugin.ObjectTable.Any(obj =>
            obj.Address != 0
            && obj.IsTargetable
            && IsTreasureSack(obj));

    private static bool IsTreasureSack(IGameObject obj)
    {
        if (obj.BaseId is GoldSackDataId or SilverSackDataId) return true;
        var name = obj.Name.TextValue;
        return name.Contains("金袋", StringComparison.Ordinal)
            || name.Contains("银袋", StringComparison.Ordinal)
            || name.Contains("Gold Sack", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Silver Sack", StringComparison.OrdinalIgnoreCase);
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

        var player = Plugin.ObjectTable.LocalPlayer;
        if (!configuration.Enabled
            || configuration.HuntEnabled
            || !configuration.AutoCollectTreasureSacks
            || mapAutomation.IsPaused
            || !TreasureContext.IsTreasureDungeon()
            || player == null
            || player.CurrentHp == 0
            || Plugin.Condition.Any(
                ConditionFlag.BetweenAreas,
                ConditionFlag.BetweenAreas51,
                ConditionFlag.OccupiedInCutSceneEvent,
                ConditionFlag.WatchingCutscene,
                ConditionFlag.WatchingCutscene78))
        {
            ResetRoute(stopNavigation: true);
            interactionTimes.Clear();
            return;
        }

        var allSacks = Plugin.ObjectTable
            .Where(obj => obj.Address != 0
                && obj.IsTargetable
                && IsTreasureSack(obj))
            .Select(obj => new SackSnapshot(obj.Address, obj.Position))
            .ToList();

        interactionTimes.Keys.Where(address => allSacks.All(sack => sack.Address != address)).ToList()
            .ForEach(address => interactionTimes.Remove(address));
        if (allSacks.Count == 0)
        {
            ResetRoute(stopNavigation: true);
            return;
        }

        // Reaching an object is not the same as collecting it. Interact while it is
        // still targetable and wait for it to disappear before continuing the route.
        var nearby = allSacks.FirstOrDefault(sack =>
            HorizontalDistanceSquared(player.Position, sack.Position) <= CollectionRange * CollectionRange
            && Math.Abs(player.Position.Y - sack.Position.Y) < 4f);
        if (nearby != null)
        {
            ResetRoute(stopNavigation: true);
            TryCollect(nearby, now);
            return;
        }

        if (!vnavmesh.IsInstalled || !vnavmesh.IsReady())
        {
            return;
        }

        if (ownsNavigation)
        {
            if (vnavmesh.IsBusy())
            {
                return;
            }

            FinishOwnedRoute();
        }

        if (routeBuildTask != null)
        {
            if (!routeBuildTask.IsCompleted)
            {
                return;
            }

            SackRoute? route = null;
            if (routeBuildTask.IsCompletedSuccessfully)
            {
                route = routeBuildTask.Result;
            }
            else if (routeBuildTask.Exception != null)
            {
                diagnostics.Write("袋子", $"连续路线生成失败：{routeBuildTask.Exception.GetBaseException().Message}");
            }

            routeBuildTask = null;
            if (route == null || route.Generation != routeGeneration || route.Waypoints.Count == 0)
            {
                return;
            }

            previousPathTolerance = vnavmesh.GetPathTolerance();
            vnavmesh.SetPathTolerance(CollectionRange);
            ownsNavigation = vnavmesh.MoveAlongPath(route.Waypoints, fly: false);
            if (!ownsNavigation)
            {
                RestorePathTolerance();
                return;
            }

            diagnostics.Write(
                "袋子",
                $"已启动连续袋子路线：本段 {route.SackCount} 个袋子，{route.Waypoints.Count} 个路径点。" );
            return;
        }

        if (now - lastRouteAttemptUtc < RouteRetryInterval)
        {
            return;
        }

        lastRouteAttemptUtc = now;
        var ordered = OrderNearest(player.Position, allSacks)
            .Take(MaxSacksPerRoute)
            .ToList();
        var generation = ++routeGeneration;
        routeBuildTask = BuildRouteAsync(generation, player.Position, ordered);
    }

    private unsafe void TryCollect(SackSnapshot sack, DateTime now)
    {
        if (interactionTimes.TryGetValue(sack.Address, out var last)
            && now - last < TimeSpan.FromSeconds(1.5)) return;
        var obj = Plugin.ObjectTable.FirstOrDefault(candidate => candidate.Address == sack.Address);
        var targetSystem = TargetSystem.Instance();
        if (obj == null || !obj.IsTargetable || targetSystem == null) return;
        interactionTimes[sack.Address] = now;
        var success = targetSystem->InteractWithObject((NativeGameObject*)sack.Address, false);
        diagnostics.WriteThrottled($"sack-{sack.Address}", "袋子",
            $"尝试拾取 {obj.Name.TextValue}，baseId={obj.BaseId}，交互结果={success}。", TimeSpan.FromSeconds(4));
    }

    private async Task<SackRoute?> BuildRouteAsync(
        int generation,
        Vector3 origin,
        IReadOnlyList<SackSnapshot> sacks)
    {
        var tasks = new List<Task<List<Vector3>>>(sacks.Count);
        var from = origin;
        foreach (var sack in sacks)
        {
            var task = vnavmesh.Pathfind(from, sack.Position, fly: false);
            if (task == null)
            {
                return null;
            }

            tasks.Add(task);
            from = sack.Position;
        }

        var segments = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (generation != routeGeneration)
        {
            return null;
        }

        var waypoints = new List<Vector3>();
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Count == 0)
            {
                return null;
            }

            if (waypoints.Count > 0
                && Vector3.DistanceSquared(waypoints[^1], segment[0]) < 0.01f)
            {
                waypoints.AddRange(segment.Skip(1));
            }
            else
            {
                waypoints.AddRange(segment);
            }

            var sackPosition = sacks[index].Position;
            if (waypoints.Count == 0
                || Vector3.DistanceSquared(waypoints[^1], sackPosition) > 0.01f)
            {
                waypoints.Add(sackPosition);
            }
        }

        return new SackRoute(generation, sacks.Count, waypoints);
    }

    private static IReadOnlyList<SackSnapshot> OrderNearest(
        Vector3 origin,
        IReadOnlyCollection<SackSnapshot> sacks)
    {
        var remaining = sacks.ToList();
        var ordered = new List<SackSnapshot>(remaining.Count);
        var current = origin;
        while (remaining.Count > 0)
        {
            var next = remaining
                .OrderBy(sack => HorizontalDistanceSquared(current, sack.Position))
                .First();
            ordered.Add(next);
            remaining.Remove(next);
            current = next.Position;
        }

        return ordered;
    }

    private void FinishOwnedRoute()
    {
        ownsNavigation = false;
        RestorePathTolerance();
    }

    private void ResetRoute(bool stopNavigation)
    {
        routeGeneration++;
        routeBuildTask = null;
        if (stopNavigation && ownsNavigation)
        {
            vnavmesh.Stop();
        }

        ownsNavigation = false;
        RestorePathTolerance();
    }

    private void RestorePathTolerance()
    {
        if (previousPathTolerance is { } tolerance)
        {
            vnavmesh.SetPathTolerance(tolerance);
        }

        previousPathTolerance = null;
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return (dx * dx) + (dz * dz);
    }

    private sealed record SackSnapshot(nint Address, Vector3 Position);

    private sealed record SackRoute(int Generation, int SackCount, IReadOnlyList<Vector3> Waypoints);
}
