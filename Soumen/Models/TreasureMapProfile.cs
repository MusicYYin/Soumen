namespace Soumen.Models;

public sealed record TreasureMapProfile(
    int Grade,
    uint ItemId,
    uint DecodedEventItemId,
    bool HasTreasureDungeon)
{
    public string GradeLabel => $"G{Grade}";
}

public static class TreasureMapCatalog
{
    public static IReadOnlyList<TreasureMapProfile> Profiles { get; } =
    [
        new(8, 12243, 2001764, true),
        new(9, 17835, 2002209, false),
        new(10, 17836, 2002210, true),
        new(11, 26744, 2002663, false),
        new(12, 26745, 2002664, true),
        new(13, 36611, 2003245, false),
        new(14, 36612, 2003246, true),
        new(15, 39591, 2003457, true),
        new(16, 43556, 2003562, false),
        new(17, 43557, 2003563, true),
        new(18, 46185, 2003785, true),
    ];

    public static TreasureMapProfile Default => Profiles[^1];

    public static TreasureMapProfile Get(uint itemId)
        => Profiles.FirstOrDefault(profile => profile.ItemId == itemId) ?? Default;

    public static bool Contains(uint itemId)
        => Profiles.Any(profile => profile.ItemId == itemId);
}
