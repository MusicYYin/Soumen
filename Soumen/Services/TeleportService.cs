using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

public sealed record AetheryteCandidate(uint Id, string Name, Vector3 Position);

public sealed class TeleportService
{
    public unsafe IReadOnlyList<AetheryteCandidate> GetCandidates(uint territoryId)
    {
        var result = new List<AetheryteCandidate>();
        var telepo = Telepo.Instance();
        if (telepo == null || Plugin.ObjectTable.LocalPlayer == null || telepo->UpdateAetheryteList() == null)
        {
            return result;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
        var seen = new HashSet<uint>();
        foreach (var entry in telepo->TeleportList)
        {
            // The teleport list is the authoritative list of destinations unlocked by
            // the current character. Sub-indexed entries are housing destinations.
            if (entry.TerritoryId != territoryId
                || entry.SubIndex != 0
                || !seen.Add(entry.AetheryteId)
                || !sheet.TryGetRow(entry.AetheryteId, out var aetheryte)
                || !aetheryte.IsAetheryte)
            {
                continue;
            }

            var position = ResolvePosition(aetheryte);
            if (position == null)
            {
                continue;
            }

            var name = aetheryte.PlaceName.ValueNullable?.Name.ToString();
            result.Add(new AetheryteCandidate(
                entry.AetheryteId,
                string.IsNullOrWhiteSpace(name) ? $"以太水晶 #{entry.AetheryteId}" : name,
                position.Value));
        }

        return result;
    }

    public static unsafe bool CanTeleportNow()
    {
        var actionManager = ActionManager.Instance();
        return Plugin.ObjectTable.LocalPlayer != null
            && actionManager != null
            && actionManager->GetActionStatus(ActionType.Action, 5) == 0;
    }

    public unsafe bool Teleport(uint aetheryteId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null || !IsAttuned(aetheryteId))
        {
            return false;
        }

        // Lifestream and GatherBuddy both treat reaching this call as a submitted
        // request and confirm success from casting/loading state afterwards. The
        // native return value is not a reliable completion signal.
        var nativeResult = telepo->Teleport(aetheryteId, 0);
        Plugin.Log.Debug(
            "Submitted teleport request for aetheryte {AetheryteId}; native result: {NativeResult}.",
            aetheryteId,
            nativeResult);
        return true;
    }

    private static Vector3? ResolvePosition(Aetheryte aetheryte)
    {
        var level = aetheryte.Level[0].ValueNullable;
        if (level != null)
        {
            return new Vector3(level.Value.X, level.Value.Y, level.Value.Z);
        }

        var territory = aetheryte.Territory.ValueNullable;
        var map = territory?.Map.ValueNullable;
        if (map == null)
        {
            return null;
        }

        foreach (var row in Plugin.DataManager.GetSubrowExcelSheet<MapMarker>())
        {
            foreach (var marker in row)
            {
                if (!((marker.DataType == 3 && marker.DataKey.RowId == aetheryte.RowId)
                      || (marker.DataType == 4 && marker.DataKey.RowId == aetheryte.AethernetName.RowId)))
                {
                    continue;
                }

                var scale = map.Value.SizeFactor * 0.01f;
                return new Vector3(
                    PixelToWorld(marker.X, scale, map.Value.OffsetX),
                    0f,
                    PixelToWorld(marker.Y, scale, map.Value.OffsetY));
            }
        }

        return null;
    }

    private static float PixelToWorld(float coordinate, float scale, short offset)
    {
        const float factor = 2048f / (50f * 41f);
        return ((coordinate * factor) - 1024f) / scale - (offset * 0.001f);
    }

    private static unsafe bool IsAttuned(uint aetheryteId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null || telepo->UpdateAetheryteList() == null)
        {
            return false;
        }

        foreach (var entry in telepo->TeleportList)
        {
            if (entry.AetheryteId == aetheryteId && entry.SubIndex == 0)
            {
                return true;
            }
        }

        return false;
    }
}
