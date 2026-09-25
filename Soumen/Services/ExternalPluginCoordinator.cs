using System.Reflection;
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
    private float? dungeonFollowOriginalDistance;
    private int? dungeonFollowOriginalSlot;
    private bool bossModSettingsReadFailed;
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
            dungeonFollowOriginalDistance = null;
            dungeonFollowOriginalSlot = null;
            bossModSettingsReadFailed = false;
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
            // BMR exposes commands to change these settings, but no command to
            // read them. Preserve the live settings before changing either one.
            if (!TryCaptureBossModFollowSettings()) return false;
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
        var originalDistance = dungeonFollowOriginalDistance;
        var originalSlot = dungeonFollowOriginalSlot;
        dungeonFollowOriginalDistance = null;
        dungeonFollowOriginalSlot = null;
        if (!BossModRebornInstalled) return;
        Execute("/bmrai followoutofcombat off");
        if (originalDistance is { } distance)
            Execute($"/bmrai maxdistancetarget {distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        if (originalSlot is >= 0 and < 8)
            Execute($"/bmrai follow slot{originalSlot.Value + 1}");
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

    private bool TryCaptureBossModFollowSettings()
    {
        if (dungeonFollowOriginalDistance != null && dungeonFollowOriginalSlot != null) return true;
        if (bossModSettingsReadFailed) return false;

        try
        {
            var managerType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("BossMod", StringComparison.OrdinalIgnoreCase) == true)
                .Select(assembly => assembly.GetType("BossMod.AI.AIManager", throwOnError: false))
                .FirstOrDefault(type => type != null);
            var config = managerType?.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var configType = config?.GetType();
            var distance = configType?.GetField("MaxDistanceToTarget", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            var slot = configType?.GetField("FollowSlot", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            if (distance is not float originalDistance || slot is not int originalSlot || originalSlot is < 0 or > 7)
                throw new InvalidOperationException("BMR AI follow configuration is unavailable.");

            dungeonFollowOriginalDistance = originalDistance;
            dungeonFollowOriginalSlot = originalSlot;
            return true;
        }
        catch (Exception ex)
        {
            bossModSettingsReadFailed = true;
            Plugin.Log.Warning(ex, "Could not preserve BMR follow settings; using vnavmesh for dungeon follow.");
            return false;
        }
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
