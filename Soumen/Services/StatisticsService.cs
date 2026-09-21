using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Soumen.Services;

public sealed class StatisticsService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private DateTime nextPollUtc = DateTime.MinValue;
    private DateTime nextSaveUtc = DateTime.MinValue;
    private long lastGil;
    private bool hasGilBaseline;
    private bool dungeonStateInitialized;
    private bool wasInTreasureDungeon;
    private bool dirty;

    public StatisticsService(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
    }

    public long GilEarned => configuration.StatisticsGilEarned;

    public int TreasureDungeonEntries => configuration.StatisticsTreasureDungeonEntries;

    public int TreasureDungeonCompletions => configuration.StatisticsTreasureDungeonCompletions;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        SaveIfDirty();
    }

    public void Reset()
    {
        configuration.StatisticsGilEarned = 0;
        configuration.StatisticsTreasureDungeonEntries = 0;
        configuration.StatisticsTreasureDungeonCompletions = 0;
        hasGilBaseline = TryReadGil(out lastGil);
        wasInTreasureDungeon = TreasureContext.IsTreasureDungeon();
        dungeonStateInitialized = true;
        dirty = false;
        configuration.Save();
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
        if (!configuration.Enabled)
        {
            hasGilBaseline = false;
            dungeonStateInitialized = false;
            SaveIfDue(now);
            return;
        }

        TrackTreasureDungeonEntry();
        TrackGil();
        SaveIfDue(now);
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
            if (configuration.StatisticsTreasureDungeonEntries < int.MaxValue)
            {
                configuration.StatisticsTreasureDungeonEntries++;
            }

            dirty = true;
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
            configuration.StatisticsGilEarned += currentGil - lastGil;
            dirty = true;
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

        if (configuration.StatisticsTreasureDungeonCompletions < int.MaxValue)
        {
            configuration.StatisticsTreasureDungeonCompletions++;
        }

        dirty = true;
        SaveIfDirty();
    }

    private void SaveIfDue(DateTime now)
    {
        if (!dirty || now < nextSaveUtc)
        {
            return;
        }

        SaveIfDirty();
        nextSaveUtc = now + SaveInterval;
    }

    private void SaveIfDirty()
    {
        if (!dirty)
        {
            return;
        }

        configuration.Save();
        dirty = false;
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
