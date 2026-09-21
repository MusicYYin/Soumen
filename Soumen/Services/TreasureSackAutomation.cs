using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

public sealed class TreasureSackAutomation : IDisposable
{
    private const uint GoldSackDataId = 0x1EBE47;
    private const uint SilverSackDataId = 0x1EBE48;
    private const float CollectionRange = 0.15f;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NavigationRetryInterval = TimeSpan.FromMilliseconds(800);

    private readonly Configuration configuration;
    private readonly MapFlagAutomation mapAutomation;
    private readonly DiagnosticLogger diagnostics;
    private readonly VNavmeshIpc vnavmesh;

    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime lastNavigationAttemptUtc = DateTime.MinValue;
    private nint activeSackAddress;
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
        StopOwnedNavigation();
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
            StopOwnedNavigation();
            activeSackAddress = 0;
            return;
        }

        var sacks = Plugin.ObjectTable
            .Where(obj => obj.Address != 0 && obj.BaseId is GoldSackDataId or SilverSackDataId)
            .ToList();
        if (sacks.Count == 0)
        {
            StopOwnedNavigation();
            activeSackAddress = 0;
            return;
        }

        var sack = sacks
            .OrderBy(obj => HorizontalDistanceSquared(player.Position, obj.Position)
                * (obj.BaseId == GoldSackDataId ? 1f : 3f))
            .First();

        if (activeSackAddress != sack.Address)
        {
            StopOwnedNavigation();
            activeSackAddress = sack.Address;
            diagnostics.Write(
                "袋子",
                $"发现{(sack.BaseId == GoldSackDataId ? "金" : "银")}袋，前往 ({sack.Position.X:F1},{sack.Position.Y:F1},{sack.Position.Z:F1})。");
        }

        if (!vnavmesh.IsInstalled || !vnavmesh.IsReady())
        {
            return;
        }

        if (ownsNavigation && vnavmesh.IsBusy())
        {
            return;
        }

        if (now - lastNavigationAttemptUtc < NavigationRetryInterval)
        {
            return;
        }

        lastNavigationAttemptUtc = now;
        ownsNavigation = vnavmesh.MoveCloseTo(sack.Position, fly: false, CollectionRange);
        if (!ownsNavigation)
        {
            diagnostics.WriteThrottled(
                "sack-navigation",
                "袋子",
                "vnavmesh 暂时无法开始袋子路线。",
                TimeSpan.FromSeconds(5));
        }
    }

    private void StopOwnedNavigation()
    {
        if (!ownsNavigation)
        {
            return;
        }

        vnavmesh.Stop();
        ownsNavigation = false;
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return (dx * dx) + (dz * dz);
    }
}
