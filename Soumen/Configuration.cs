using Dalamud.Configuration;
using Dalamud.Plugin;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soumen.Models;

namespace Soumen;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private const string FileName = "config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    [JsonIgnore]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 13;

    public OperatingMode OperatingMode { get; set; } = OperatingMode.Follow;

    public uint LeaderTreasureMapItemId { get; set; } = 46185;

    public bool AutoRestockLeaderMaps { get; set; } = false;

    public uint LeaderMapMaximumUnitPrice { get; set; } = 100000;

    public uint LeaderMapMaximumRestockCost { get; set; } = 220000;

    public bool Enabled { get; set; } = false;

    public bool AutoMount { get; set; } = true;

    public bool AutoDismount { get; set; } = true;

    public bool UseFlight { get; set; } = true;

    public bool EnableAeAssistIntegration { get; set; } = true;

    public bool EnableBossModRebornIntegration { get; set; } = true;

    public LazyLootRollMode LazyLootRollMode { get; set; } = LazyLootRollMode.Need;

    public bool AutoTeleport { get; set; } = true;

    public bool AcceptPartyTeleportRequests { get; set; } = false;

    public bool AutoLeaveTreasureDungeon { get; set; } = false;

    public bool AutoCollectTreasureSacks { get; set; } = true;

    public bool AutoDiscardEnabled { get; set; } = false;

    // Kept for one-time migration from 0.4.0 and earlier.
    public HashSet<uint> AutoDiscardItemIds { get; set; } = [];

    public List<DiscardPreset> AutoDiscardPresets { get; set; } = [];

    public string ActiveAutoDiscardPresetId { get; set; } = string.Empty;

    public UiTheme UiTheme { get; set; } = UiTheme.Ocean;

    public bool TeleportWhenStuck { get; set; } = true;

    public bool RecognizeAllChatCoordinates { get; set; } = false;

    public bool DiagnosticMode { get; set; } = false;

    public float StuckSeconds { get; set; } = 8f;

    public float ArrivalTolerance { get; set; } = 8f;

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var directory = pluginInterface.GetPluginConfigDirectory();
        var path = Path.Combine(directory, FileName);
        Directory.CreateDirectory(directory);

        Configuration configuration;
        if (File.Exists(path))
        {
            try
            {
                configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), SerializerOptions)
                    ?? new Configuration();
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(exception, "Failed to load Soumen config; using defaults.");
                configuration = new Configuration();
            }
        }
        else
        {
            try
            {
                configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(exception, "Failed to migrate the old Soumen config; using defaults.");
                configuration = new Configuration();
            }
        }

        configuration.pluginInterface = pluginInterface;
        if (configuration.AcceptPartyTeleportRequests)
        {
            configuration.AutoTeleport = false;
        }
        else
        {
            configuration.AutoTeleport = true;
        }

        configuration.AutoDiscardItemIds ??= [];
        configuration.AutoDiscardPresets ??= [];
        configuration.NormalizeDiscardPresets();
        if (!Enum.IsDefined(typeof(UiTheme), configuration.UiTheme))
        {
            configuration.UiTheme = UiTheme.Ocean;
        }
        if (!Enum.IsDefined(typeof(LazyLootRollMode), configuration.LazyLootRollMode))
        {
            configuration.LazyLootRollMode = LazyLootRollMode.Need;
        }
        if (!TreasureMapCatalog.Contains(configuration.LeaderTreasureMapItemId))
        {
            configuration.LeaderTreasureMapItemId = TreasureMapCatalog.Default.ItemId;
        }
        configuration.LeaderMapMaximumUnitPrice = Math.Clamp(configuration.LeaderMapMaximumUnitPrice, 1_000u, 9_999_999u);
        configuration.LeaderMapMaximumRestockCost = Math.Clamp(configuration.LeaderMapMaximumRestockCost, 2_000u, 19_999_998u);
        configuration.StuckSeconds = Math.Clamp(configuration.StuckSeconds, 1f, 30f);
        configuration.ArrivalTolerance = Math.Clamp(configuration.ArrivalTolerance, 0f, 30f);
        configuration.Version = 13;
        configuration.Save();
        return configuration;
    }

    [JsonIgnore]
    public DiscardPreset ActiveDiscardPreset
    {
        get
        {
            NormalizeDiscardPresets();
            return AutoDiscardPresets.First(preset => preset.Id == ActiveAutoDiscardPresetId);
        }
    }

    public HashSet<uint> ResolveActiveDiscardItemIds()
        => ResolveDiscardItemIds(ActiveDiscardPreset.Id);

    public HashSet<uint> ResolveDiscardItemIds(string presetId)
    {
        var result = new HashSet<uint>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AddPreset(string id)
        {
            if (!visited.Add(id))
            {
                return;
            }

            var preset = AutoDiscardPresets.FirstOrDefault(candidate => candidate.Id == id);
            if (preset == null)
            {
                return;
            }

            result.UnionWith(preset.ItemIds);
            foreach (var includedId in preset.IncludedPresetIds)
            {
                AddPreset(includedId);
            }
        }

        AddPreset(presetId);
        return result;
    }

    public bool CanIncludeDiscardPreset(string parentId, string candidateId)
    {
        if (parentId == candidateId
            || AutoDiscardPresets.All(preset => preset.Id != parentId)
            || AutoDiscardPresets.All(preset => preset.Id != candidateId))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool ReachesParent(string id)
        {
            if (id == parentId)
            {
                return true;
            }

            if (!visited.Add(id))
            {
                return false;
            }

            var preset = AutoDiscardPresets.FirstOrDefault(candidate => candidate.Id == id);
            return preset != null && preset.IncludedPresetIds.Any(ReachesParent);
        }

        return !ReachesParent(candidateId);
    }

    public DiscardPreset CreateDiscardPreset(string? name = null)
    {
        var preset = new DiscardPreset
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"预设 {AutoDiscardPresets.Count + 1}" : name.Trim(),
        };
        AutoDiscardPresets.Add(preset);
        ActiveAutoDiscardPresetId = preset.Id;
        return preset;
    }

    public bool DeleteDiscardPreset(string presetId)
    {
        if (AutoDiscardPresets.Count <= 1)
        {
            return false;
        }

        var removed = AutoDiscardPresets.RemoveAll(preset => preset.Id == presetId) > 0;
        if (!removed)
        {
            return false;
        }

        foreach (var preset in AutoDiscardPresets)
        {
            preset.IncludedPresetIds.Remove(presetId);
        }

        if (ActiveAutoDiscardPresetId == presetId)
        {
            ActiveAutoDiscardPresetId = AutoDiscardPresets[0].Id;
        }

        return true;
    }

    private void NormalizeDiscardPresets()
    {
        AutoDiscardPresets ??= [];
        AutoDiscardItemIds ??= [];

        if (AutoDiscardPresets.Count == 0)
        {
            AutoDiscardPresets.Add(new DiscardPreset
            {
                Name = "默认",
                ItemIds = [.. AutoDiscardItemIds],
            });
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in AutoDiscardPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || !usedIds.Add(preset.Id))
            {
                preset.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(preset.Id);
            }

            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "未命名预设" : preset.Name.Trim();
            preset.ItemIds ??= [];
            preset.IncludedPresetIds ??= [];
        }

        foreach (var preset in AutoDiscardPresets)
        {
            preset.IncludedPresetIds.RemoveWhere(id => id == preset.Id || !usedIds.Contains(id));
        }

        if (!usedIds.Contains(ActiveAutoDiscardPresetId))
        {
            ActiveAutoDiscardPresetId = AutoDiscardPresets[0].Id;
        }

        AutoDiscardItemIds.Clear();
    }

    public void Save()
    {
        if (pluginInterface == null)
        {
            return;
        }

        try
        {
            var directory = pluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(directory, FileName);
            var temporaryPath = path + ".tmp";
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "Failed to save Soumen config.");
        }
    }
}
