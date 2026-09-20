using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

public sealed class TreasureDungeonAutomation : IDisposable
{
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
            || !IsTreasureDungeon())
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
        if (HasPendingLootDistribution())
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

    private static unsafe bool IsTreasureDungeon()
    {
        var gameMain = GameMain.Instance();
        var conditionId = gameMain == null ? 0 : gameMain->CurrentContentFinderConditionId;
        if (conditionId == 0)
        {
            return false;
        }

        var condition = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRow(conditionId);
        return condition.ContentType.RowId == 9;
    }

    private static unsafe bool HasPendingLootDistribution()
    {
        var loot = Loot.Instance();
        if (loot == null)
        {
            return false;
        }

        foreach (var item in loot->Items)
        {
            if (item.ChestObjectId is 0 or 0xE0000000
                || item.ItemId == 0)
            {
                continue;
            }

            if (item.RollResult != RollResult.Awarded)
            {
                return true;
            }
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
