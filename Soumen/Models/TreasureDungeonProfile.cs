namespace Soumen.Models;

public enum TreasureDungeonStyle
{
    Door,
    Roulette,
}

public sealed record TreasureDungeonProfile(
    uint TerritoryId,
    TreasureDungeonStyle Style,
    string ProgressionObjectName);

public static class TreasureDungeonCatalog
{
    private static readonly IReadOnlyDictionary<uint, TreasureDungeonProfile> ByTerritory =
        new Dictionary<uint, TreasureDungeonProfile>
        {
            [558] = new(558, TreasureDungeonStyle.Door, "Vault Door"),
            [712] = new(712, TreasureDungeonStyle.Door, "Sluice Gate"),
            [725] = new(725, TreasureDungeonStyle.Door, "Sluice Gate"),
            [794] = new(794, TreasureDungeonStyle.Roulette, "Arcane Sphere"),
            [879] = new(879, TreasureDungeonStyle.Door, "Elaborate Gate"),
            [924] = new(924, TreasureDungeonStyle.Roulette, "Arcane Sphere"),
            [1000] = new(1000, TreasureDungeonStyle.Door, "Stage Door"),
            [1123] = new(1123, TreasureDungeonStyle.Roulette, "Arcane Sphere"),
            [1209] = new(1209, TreasureDungeonStyle.Door, "Vault Door"),
            [1279] = new(1279, TreasureDungeonStyle.Roulette, "Hypnoslot Machine"),
        };

    public static IReadOnlyCollection<TreasureDungeonProfile> Profiles { get; } = ByTerritory.Values.ToArray();

    public static bool TryGet(uint territoryId, out TreasureDungeonProfile profile)
        => ByTerritory.TryGetValue(territoryId, out profile!);
}
