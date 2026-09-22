namespace Soumen.Models;

public sealed record TreasureMapProfile(
    int Grade,
    uint ItemId,
    uint DecodedEventItemId,
    bool HasTreasureDungeon,
    bool IsSpecial = false)
{
    public string GradeLabel => IsSpecial ? "特殊" : $"G{Grade}";

    public bool CanMarketRestock => !IsSpecial;
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
        // Event reward maps. Unlike regular timeworn maps, these are untradable and stack to 999.
        new(8, 24794, 2002503, true, true),
        new(12, 33328, 2003075, true, true),
        new(15, 39593, 2003455, true, true),
        new(15, 39918, 2003463, true, true),
        new(17, 44349, 2003704, true, true),
    ];

    public static TreasureMapProfile Default => Profiles.First(profile => profile.ItemId == 46185);

    public static TreasureMapProfile Get(uint itemId)
        => Profiles.FirstOrDefault(profile => profile.ItemId == itemId) ?? Default;

    public static bool Contains(uint itemId)
        => Profiles.Any(profile => profile.ItemId == itemId);
}
