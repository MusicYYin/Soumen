using Dalamud.Configuration;

namespace Soumen;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = false;

    public bool LivingMemoryOnly { get; set; } = true;

    public bool AutoMount { get; set; } = true;

    public bool AutoDismount { get; set; } = true;

    public bool UseFlight { get; set; } = true;

    public bool ClearTargetWhileNavigating { get; set; } = true;

    public bool ShowChatLogs { get; set; } = true;

    public int DebounceMilliseconds { get; set; } = 500;

    public float ArrivalTolerance { get; set; } = 8f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
