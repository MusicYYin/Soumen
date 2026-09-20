using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Soumen.Services;

public sealed class TreasureDungeonAutomation : IDisposable
{
    private static readonly HashSet<uint> TreasureDungeonTerritories =
    [
        558,  // The Aquapolis
        712,  // The Lost Canals of Uznair
        725,  // The Hidden Canals of Uznair
        794,  // The Shifting Altars of Uznair
        879,  // The Dungeons of Lyhe Ghiah
        924,  // The Shifting Oubliettes of Lyhe Ghiah
        1000, // The Excitatron 6000
        1123, // The Shifting Gymnasion Agonon
        1209, // Cenote Ja Ja Gural
        1279, // Vault Oneiron
    ];

    private static readonly TimeSpan MinimumExitDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EmptyLootDelay = TimeSpan.FromSeconds(3);

    private readonly Configuration configuration;
    private uint pendingTerritory;
    private DateTime dutyCompletedUtc;
    private DateTime? noLootSinceUtc;

    public TreasureDungeonAutomation(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        _ = args;
        var territory = Plugin.ClientState.TerritoryType;
        if (!configuration.Enabled
            || !configuration.AutoLeaveTreasureDungeon
            || !TreasureDungeonTerritories.Contains(territory))
        {
            return;
        }

        pendingTerritory = territory;
        dutyCompletedUtc = DateTime.UtcNow;
        noLootSinceUtc = null;
        Plugin.Log.Information("Treasure dungeon completed; waiting for loot before leaving.");
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        _ = framework;
        if (pendingTerritory == 0)
        {
            return;
        }

        if (!configuration.Enabled
            || !configuration.AutoLeaveTreasureDungeon
            || Plugin.ClientState.TerritoryType != pendingTerritory
            || !IsInDuty())
        {
            Reset();
            return;
        }

        if (Plugin.Condition.Any(
                ConditionFlag.InCombat,
                ConditionFlag.BetweenAreas,
                ConditionFlag.BetweenAreas51,
                ConditionFlag.OccupiedInCutSceneEvent,
                ConditionFlag.WatchingCutscene,
                ConditionFlag.WatchingCutscene78))
        {
            noLootSinceUtc = null;
            return;
        }

        var now = DateTime.UtcNow;
        if (HasRollableLoot())
        {
            noLootSinceUtc = null;
            return;
        }

        noLootSinceUtc ??= now;
        if (now - dutyCompletedUtc < MinimumExitDelay
            || now - noLootSinceUtc.Value < EmptyLootDelay)
        {
            return;
        }

        TryLeaveDuty();
    }

    private static unsafe bool IsInDuty()
    {
        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId != 0;
    }

    private static unsafe bool HasRollableLoot()
    {
        var loot = Loot.Instance();
        if (loot == null)
        {
            return false;
        }

        foreach (var item in loot->Items)
        {
            if (item.ChestObjectId is 0 or 0xE0000000
                || item.ItemId == 0
                || item.RollResult != RollResult.UnAwarded
                || item.RollState is RollState.Rolled or RollState.Unavailable or RollState.Unknown
                || item.LootMode is LootMode.LootMasterGreedOnly or LootMode.Unavailable)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private unsafe void TryLeaveDuty()
    {
        try
        {
            if (!EventFramework.CanLeaveCurrentContent())
            {
                return;
            }

            EventFramework.LeaveCurrentContent(true);
            Plugin.Log.Information("Leaving completed treasure dungeon.");
            Reset();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to leave the completed treasure dungeon.");
        }
    }

    private void Reset()
    {
        pendingTerritory = 0;
        dutyCompletedUtc = DateTime.MinValue;
        noLootSinceUtc = null;
    }
}
