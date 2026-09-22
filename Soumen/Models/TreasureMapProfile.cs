namespace Soumen.Models;

public enum TreasureMapKind
{
    Standard,
    Special,
    Green,
    DeepGreen,
}

public sealed record TreasureMapProfile(
    int Grade,
    uint ItemId,
    uint DecodedEventItemId,
    bool HasTreasureDungeon,
    TreasureMapKind Kind = TreasureMapKind.Standard)
{
    public string GradeLabel => Kind switch
    {
        TreasureMapKind.Special => "特殊",
        TreasureMapKind.Green => "绿图",
        TreasureMapKind.DeepGreen => "深层绿图",
        _ => $"G{Grade}",
    };

    public bool IsSpecial => Kind == TreasureMapKind.Special;

    public bool CanMarketRestock => Kind is TreasureMapKind.Standard or TreasureMapKind.Green;

    public bool DirectPortal => Kind == TreasureMapKind.DeepGreen;

    public bool IsStackable => Kind is not TreasureMapKind.Standard;

    public int CatalogOrder => Kind switch
    {
        TreasureMapKind.Standard => 0,
        TreasureMapKind.Special => 1,
        TreasureMapKind.Green => 2,
        TreasureMapKind.DeepGreen => 3,
        _ => 4,
    };
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
        new(8, 24794, 2002503, true, TreasureMapKind.Special),
        new(12, 33328, 2003075, true, TreasureMapKind.Special),
        new(15, 39593, 2003455, true, TreasureMapKind.Special),
        new(15, 39918, 2003463, true, TreasureMapKind.Special),
        new(17, 44349, 2003704, true, TreasureMapKind.Special),
        // Rare green maps. The thief's map digs up a guaranteed portal instead of a chest.
        new(0, 8156, 2001352, false, TreasureMapKind.Green),
        new(0, 19770, 2002386, true, TreasureMapKind.DeepGreen),
    ];

    public static TreasureMapProfile Default => Profiles.First(profile => profile.ItemId == 46185);

    public static TreasureMapProfile Get(uint itemId)
        => Profiles.FirstOrDefault(profile => profile.ItemId == itemId) ?? Default;

    public static bool Contains(uint itemId)
        => Profiles.Any(profile => profile.ItemId == itemId);
}
