using System.Numerics;
using Lumina.Excel.Sheets;
using Soumen.Models;

namespace Soumen.Services;

public sealed class TreasureSpotResolver
{
    // A map flag can be over one displayed coordinate away from the actual dig spot.
    private const float MinimumRunnerUpGap = 12f;

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private readonly Dictionary<uint, List<TreasureSpotPoint>> pointsByTerritory = [];

    public TreasureSpotResolver(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Load();
    }

    public bool TryResolve(MapFlagTarget target, out Vector3 position, out float distance)
    {
        var result = Inspect(target);
        position = result.Position;
        distance = result.Distance;
        return result.Matched;
    }

    public SpotResolution Inspect(MapFlagTarget target)
    {
        var maximumSnapDistance = configuration.TreasureSpotCorrectionRange;
        if (maximumSnapDistance <= 0f)
        {
            return new(false, default, float.PositiveInfinity, float.PositiveInfinity, 0, "藏宝点纠偏已关闭");
        }

        if (!pointsByTerritory.TryGetValue(target.TerritoryId, out var territoryPoints))
        {
            return new(false, default, float.PositiveInfinity, float.PositiveInfinity, 0, "当前区域没有藏宝点数据");
        }

        var raw = target.ToWorld(0f);
        // A territory may have multiple map floors. Never borrow a spot from another map.
        var candidates = (target.MapId == 0
                ? territoryPoints
                : territoryPoints.Where(point => point.MapId == target.MapId))
            .Select(point => (point.Position, Distance: HorizontalDistance(raw, point.Position)))
            .OrderBy(point => point.Distance)
            .Take(2)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new(false, default, float.PositiveInfinity, float.PositiveInfinity, 0,
                $"未找到匹配地图 #{target.MapId} 的藏宝点");
        }

        var closest = candidates[0];
        var second = candidates.Length > 1 ? candidates[1].Distance : float.PositiveInfinity;
        if (closest.Distance > maximumSnapDistance)
        {
            return new(false, closest.Position, closest.Distance, second, candidates.Length,
                $"最近藏宝点距离 {closest.Distance:F1}y，超过 {maximumSnapDistance:F0}y 搜索范围");
        }

        if (second - closest.Distance < MinimumRunnerUpGap)
        {
            return new(false, closest.Position, closest.Distance, second, candidates.Length,
                $"两处藏宝点相距相近（{closest.Distance:F1}/{second:F1}y），无法确定目标");
        }

        return new(true, closest.Position, closest.Distance, second, candidates.Length, "已定位藏宝点");
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

    public readonly record struct SpotResolution(
        bool Matched, Vector3 Position, float Distance, float RunnerUpDistance, int CandidateCount, string Reason);
}
