using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

public sealed record AetheryteCandidate(uint Id, Vector3 Position);

public sealed class TeleportService
{
    public unsafe IReadOnlyList<AetheryteCandidate> GetCandidates(uint territoryId)
    {
        var result = new List<AetheryteCandidate>();
        foreach (var aetheryte in Plugin.DataManager.GetExcelSheet<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte
                || aetheryte.Territory.RowId != territoryId
                || !IsAttuned(aetheryte.RowId))
            {
                continue;
            }

            var level = aetheryte.Level[0].ValueNullable;
            if (level == null)
            {
                continue;
            }

            result.Add(new AetheryteCandidate(
                aetheryte.RowId,
                new Vector3(level.Value.X, level.Value.Y, level.Value.Z)));
        }

        return result;
    }

    public unsafe bool Teleport(uint aetheryteId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null || !IsAttuned(aetheryteId))
        {
            return false;
        }

        telepo->Teleport(aetheryteId, 0);
        return true;
    }

    private static unsafe bool IsAttuned(uint aetheryteId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            return false;
        }

        telepo->UpdateAetheryteList();
        for (var entry = telepo->TeleportList.First; entry != telepo->TeleportList.Last; ++entry)
        {
            if (entry->AetheryteId == aetheryteId)
            {
                return true;
            }
        }

        return false;
    }
}
