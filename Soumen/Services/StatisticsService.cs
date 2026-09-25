using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Soumen.Services;

public sealed class StatisticsService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Configuration configuration;
    private DateTime nextPollUtc = DateTime.MinValue;
    private long lastGil;
    private long gilEarned;
    private int treasureDungeonEntries;
    private int treasureDungeonCompletions;
    private bool hasGilBaseline;
    private bool dungeonStateInitialized;
    private bool wasInTreasureDungeon;

    public StatisticsService(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
    }

    public long GilEarned => gilEarned;

    public int TreasureDungeonEntries => treasureDungeonEntries;

    public int TreasureDungeonCompletions => treasureDungeonCompletions;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
    }

    public void Reset()
    {
        gilEarned = 0;
        treasureDungeonEntries = 0;
        treasureDungeonCompletions = 0;
        hasGilBaseline = TryReadGil(out lastGil);
        wasInTreasureDungeon = TreasureContext.IsTreasureDungeon();
        dungeonStateInitialized = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextPollUtc)
        {
            return;
        }

        nextPollUtc = now + PollInterval;
        if (!configuration.Enabled || configuration.HuntEnabled)
        {
            hasGilBaseline = false;
            dungeonStateInitialized = false;
            return;
        }

        TrackTreasureDungeonEntry();
        TrackGil();
    }

    private void TrackTreasureDungeonEntry()
    {
        var inTreasureDungeon = TreasureContext.IsTreasureDungeon();
        if (!dungeonStateInitialized)
        {
            wasInTreasureDungeon = inTreasureDungeon;
            dungeonStateInitialized = true;
            return;
        }

        if (inTreasureDungeon && !wasInTreasureDungeon)
        {
            if (treasureDungeonEntries < int.MaxValue)
            {
                treasureDungeonEntries++;
            }
        }

        wasInTreasureDungeon = inTreasureDungeon;
    }

    private void TrackGil()
    {
        if (!TryReadGil(out var currentGil))
        {
            hasGilBaseline = false;
            return;
        }

        if (!hasGilBaseline)
        {
            lastGil = currentGil;
            hasGilBaseline = true;
            return;
        }

        if (currentGil > lastGil)
        {
            gilEarned += currentGil - lastGil;
        }

        lastGil = currentGil;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        _ = args;
        if (!configuration.Enabled || !TreasureContext.IsTreasureDungeon())
        {
            return;
        }

        if (treasureDungeonCompletions < int.MaxValue)
        {
            treasureDungeonCompletions++;
        }
    }

    private static unsafe bool TryReadGil(out long gil)
    {
        gil = 0;
        if (Plugin.ObjectTable.LocalPlayer == null)
        {
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var value = manager->GetInventoryItemCount(1);
        if (value < 0)
        {
            return false;
        }

        gil = value;
        return true;
    }
}
