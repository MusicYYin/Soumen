namespace Soumen.Models;

public sealed class DiscardPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "默认";

    public HashSet<uint> ItemIds { get; set; } = [];

    public HashSet<string> IncludedPresetIds { get; set; } = [];
}
