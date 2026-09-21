using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

internal static class TreasureContext
{
    public static unsafe bool IsInDuty()
    {
        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId != 0;
    }

    public static unsafe bool IsTreasureDungeon()
    {
        var gameMain = GameMain.Instance();
        var conditionId = gameMain == null ? 0 : gameMain->CurrentContentFinderConditionId;
        if (conditionId == 0)
        {
            return false;
        }

        var condition = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRow((uint)conditionId);
        return condition.ContentType.RowId == 9;
    }
}
