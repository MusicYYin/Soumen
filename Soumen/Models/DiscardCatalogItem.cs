namespace Soumen.Models;

public sealed record DiscardCatalogItem(
    uint ItemId,
    string Name,
    string EnglishName,
    ushort IconId,
    bool IsG18Loot,
    bool IsNonPriorityMateria);
