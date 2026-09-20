using Dalamud.Configuration;
using Dalamud.Plugin;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public int Version { get; set; } = 5;

    public bool Enabled { get; set; } = false;

    public bool AutoMount { get; set; } = true;

    public bool AutoDismount { get; set; } = true;

    public bool UseFlight { get; set; } = true;

    public bool EnableAeAssistIntegration { get; set; } = true;

    public bool EnableBossModRebornIntegration { get; set; } = true;

    public bool AutoTeleport { get; set; } = true;

    public bool AcceptPartyTeleportRequests { get; set; } = false;

    public bool AutoLeaveTreasureDungeon { get; set; } = false;

    public bool TeleportWhenStuck { get; set; } = true;

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

        configuration.Version = 5;
        configuration.Save();
        return configuration;
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
