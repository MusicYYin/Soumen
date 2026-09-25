using Soumen.Models;

namespace Soumen.Services;

public sealed class ExternalPluginCoordinator
{
    private readonly Configuration configuration;
    private bool? aeTargetingEnabled;
    private bool bossModArmed;
    private bool lazyLootArmed;
    private LazyLootRollMode? appliedLazyLootRollMode;

    public ExternalPluginCoordinator(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool AeAssistInstalled => IsPluginLoaded("AEAssistV3");

    public bool BossModRebornInstalled => IsPluginLoaded("BossModReborn");

    public bool LazyLootInstalled => IsPluginLoaded("LazyLoot");

    public bool GlobetrotterInstalled => IsPluginLoaded("Globetrotter");

    public bool DailyRoutinesInstalled => IsPluginLoaded("DailyRoutines");

    public void StartRuntime()
    {
        if (!configuration.HuntEnabled && configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            Execute("/bmrai on");
            bossModArmed = true;
        }

        if (!configuration.HuntEnabled) ApplyLazyLootRollMode();

        SetNavigating(false);
    }

    public void StopRuntime()
    {
        SetNavigating(false);
        if (bossModArmed && BossModRebornInstalled)
        {
            Execute("/bmrai off");
        }

        if (lazyLootArmed && LazyLootInstalled)
        {
            Execute("/fulf off");
        }

        bossModArmed = false;
        lazyLootArmed = false;
        appliedLazyLootRollMode = null;
        aeTargetingEnabled = null;
    }

    public void RefreshRuntime()
    {
        if (!BossModRebornInstalled)
        {
            bossModArmed = false;
        }

        if (!configuration.HuntEnabled && configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            Execute("/bmrai on");
            bossModArmed = true;
        }
        else if ((configuration.HuntEnabled || !configuration.EnableBossModRebornIntegration) && bossModArmed && BossModRebornInstalled)
        {
            Execute("/bmrai off");
            bossModArmed = false;
        }

        if (!AeAssistInstalled)
        {
            aeTargetingEnabled = null;
        }

        if (!LazyLootInstalled)
        {
            lazyLootArmed = false;
            appliedLazyLootRollMode = null;
        }
        else if (!lazyLootArmed && !configuration.HuntEnabled)
        {
            ApplyLazyLootRollMode();
        }
    }

    public void ApplyLazyLootRollMode()
    {
        if (!LazyLootInstalled)
        {
            lazyLootArmed = false;
            appliedLazyLootRollMode = null;
            return;
        }

        if (lazyLootArmed && appliedLazyLootRollMode == configuration.LazyLootRollMode)
        {
            return;
        }

        var command = configuration.LazyLootRollMode switch
        {
            LazyLootRollMode.Need => "need",
            LazyLootRollMode.Greed => "greed",
            LazyLootRollMode.Pass => "pass",
            _ => "need",
        };

        Execute($"/fulf {command}");
        Execute("/fulf on");
        lazyLootArmed = true;
        appliedLazyLootRollMode = configuration.LazyLootRollMode;
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
