namespace Soumen.Services;

public sealed class ExternalPluginCoordinator
{
    private readonly Configuration configuration;
    private bool? aeTargetingEnabled;
    private bool bossModArmed;

    public ExternalPluginCoordinator(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool AeAssistInstalled => IsPluginLoaded("AEAssistV3");

    public bool BossModRebornInstalled => IsPluginLoaded("BossModReborn");

    public void StartRuntime()
    {
        if (configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            Execute("/bmrai on");
            bossModArmed = true;
        }

        SetNavigating(false);
    }

    public void StopRuntime()
    {
        SetNavigating(false);
        if (bossModArmed && BossModRebornInstalled)
        {
            Execute("/bmrai off");
        }

        bossModArmed = false;
        aeTargetingEnabled = null;
    }

    public void RefreshRuntime()
    {
        if (!BossModRebornInstalled)
        {
            bossModArmed = false;
        }

        if (configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            Execute("/bmrai on");
            bossModArmed = true;
        }
        else if (!configuration.EnableBossModRebornIntegration && bossModArmed && BossModRebornInstalled)
        {
            Execute("/bmrai off");
            bossModArmed = false;
        }

        if (!AeAssistInstalled)
        {
            aeTargetingEnabled = null;
        }
    }

    public void SetNavigating(bool navigating)
    {
        if (!configuration.EnableAeAssistIntegration || !AeAssistInstalled)
        {
            if (!configuration.EnableAeAssistIntegration && aeTargetingEnabled == false && AeAssistInstalled)
            {
                Execute("/aeTargetSelector on");
            }

            aeTargetingEnabled = null;
            return;
        }

        var desired = !navigating;
        if (aeTargetingEnabled == desired)
        {
            return;
        }

        Execute(desired ? "/aeTargetSelector on" : "/aeTargetSelector off");
        aeTargetingEnabled = desired;
    }

    private static bool IsPluginLoaded(string internalName)
        => Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded && plugin.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    private static void Execute(string command)
    {
        try
        {
            Plugin.CommandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to execute external plugin command {Command}.", command);
        }
    }
}
