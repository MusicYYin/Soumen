using System.Numerics;
using Lumina.Excel.Sheets;
using Soumen.Models;

namespace Soumen.Services;

public sealed class TreasureSpotResolver
{
    private const float MaximumSnapDistance = 24f;

    private readonly DiagnosticLogger diagnostics;
    private readonly Dictionary<uint, List<TreasureSpotPoint>> pointsByTerritory = [];

    public TreasureSpotResolver(DiagnosticLogger diagnostics)
    {
        this.diagnostics = diagnostics;
        Load();
    }

    public bool TryResolve(MapFlagTarget target, out Vector3 position, out float distance)
    {
        position = default;
        distance = float.PositiveInfinity;
        if (!pointsByTerritory.TryGetValue(target.TerritoryId, out var territoryPoints))
        {
            return false;
        }

        var raw = target.ToWorld(0f);
        var mapPoints = territoryPoints.Where(point => point.MapId == target.MapId).ToList();
        var candidates = mapPoints.Count > 0 ? mapPoints : territoryPoints;
        TreasureSpotPoint? best = null;
        foreach (var point in candidates)
        {
            var current = HorizontalDistance(raw, point.Position);
            if (current >= distance)
            {
                continue;
            }

            best = point;
            distance = current;
        }

        if (best == null || distance > MaximumSnapDistance)
        {
            return false;
        }

        position = best.Position;
        return true;
    }

    private void Load()
    {
        try
        {
            var ranks = Plugin.DataManager.GetExcelSheet<TreasureHuntRank>();
            var spots = Plugin.DataManager.GetSubrowExcelSheet<TreasureSpot>();
            foreach (var rank in ranks)
            {
                for (ushort subRowId = 0; subRowId < 200; subRowId++)
                {
                    var spot = spots.GetSubrowOrDefault(rank.RowId, subRowId);
                    if (spot == null)
                    {
                        break;
                    }

                    var location = spot.Value.Location.ValueNullable;
                    var map = location?.Map.ValueNullable;
                    var territory = map?.TerritoryType.ValueNullable;
                    if (location == null || map == null || territory == null || territory.Value.RowId == 0)
                    {
                        continue;
                    }

                    var position = new Vector3(location.Value.X, location.Value.Y, location.Value.Z);
                    if (!float.IsFinite(position.X)
                        || !float.IsFinite(position.Y)
                        || !float.IsFinite(position.Z))
                    {
                        continue;
                    }

                    if (!pointsByTerritory.TryGetValue(territory.Value.RowId, out var points))
                    {
                        points = [];
                        pointsByTerritory[territory.Value.RowId] = points;
                    }

                    if (points.All(existing => existing.MapId != map.Value.RowId
                        || HorizontalDistance(existing.Position, position) > 0.1f))
                    {
                        points.Add(new TreasureSpotPoint(map.Value.RowId, position));
                    }
                }
            }

            diagnostics.Write(
                "藏宝点",
                $"已载入 {pointsByTerritory.Values.Sum(points => points.Count)} 个真实挖掘点，覆盖 {pointsByTerritory.Count} 个区域。" );
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Failed to load TreasureSpot locations.");
            diagnostics.Write("藏宝点", $"读取真实挖掘点失败，将使用普通旗标导航：{exception.Message}" );
        }
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private sealed record TreasureSpotPoint(uint MapId, Vector3 Position);
}
