using Soumen.Models;

namespace Soumen.Services;

public sealed class ExternalPluginCoordinator
{
    private readonly Configuration configuration;
    private bool? aeTargetingEnabled;
    private bool bossModArmed;
    private bool lazyLootArmed;
    private bool dungeonFollowArmed;
    private float dungeonFollowDistance = float.NaN;
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
            StartBossMod();
        }

        if (!configuration.HuntEnabled) ApplyLazyLootRollMode();

        SetNavigating(false);
    }

    public void StopRuntime()
    {
        StopDungeonFollow();
        SetNavigating(false);
        if (bossModArmed && BossModRebornInstalled)
        {
            StopBossMod();
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
            dungeonFollowArmed = false;
        }

        if (!configuration.HuntEnabled && configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            StartBossMod();
        }
        else if ((configuration.HuntEnabled || !configuration.EnableBossModRebornIntegration) && bossModArmed && BossModRebornInstalled)
        {
            StopDungeonFollow();
            StopBossMod();
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

    public bool SetDungeonFollow(string leaderName, float distance)
    {
        if (!BossModRebornInstalled || !bossModArmed || string.IsNullOrWhiteSpace(leaderName)) return false;
        if (!dungeonFollowArmed)
        {
            Execute($"/bmrai follow {leaderName}");
            Execute("/bmrai followoutofcombat on");
            dungeonFollowArmed = true;
        }
        if (float.IsNaN(dungeonFollowDistance) || Math.Abs(dungeonFollowDistance - distance) > 0.05f)
        {
            Execute($"/bmrai maxdistancetarget {distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            dungeonFollowDistance = distance;
        }
        return true;
    }

    public void StopDungeonFollow()
    {
        if (!dungeonFollowArmed) return;
        dungeonFollowArmed = false;
        dungeonFollowDistance = float.NaN;
        if (!BossModRebornInstalled) return;
        Execute("/bmrai followoutofcombat off");
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

    private void StartBossMod()
    {
        Execute("/bmrai followcombat on");
        Execute("/bmrai followmodule on");
        Execute("/bmrai followtarget on");
        Execute("/bmrai followoutofcombat off");
        Execute("/bmrai on");
        bossModArmed = true;
    }

    private void StopBossMod()
    {
        Execute("/bmrai followoutofcombat off");
        Execute("/bmrai followmodule off");
        Execute("/bmrai followcombat off");
        Execute("/bmrai followtarget off");
        Execute("/bmrai off");
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
