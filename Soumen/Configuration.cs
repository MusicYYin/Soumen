using Dalamud.Configuration;

namespace Soumen;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = false;

    public bool AutoMount { get; set; } = true;

    public bool AutoDismount { get; set; } = true;

    public bool UseFlight { get; set; } = true;

    public bool EnableAeAssistIntegration { get; set; } = true;

    public bool EnableBossModRebornIntegration { get; set; } = true;

    public bool AutoTeleport { get; set; } = true;

    public bool TeleportWhenStuck { get; set; } = true;

    public float TeleportPenaltyDistance { get; set; } = 260f;

    public float MinimumTeleportSaving { get; set; } = 140f;

    public float StuckSeconds { get; set; } = 8f;

    public float ArrivalTolerance { get; set; } = 8f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
