using System.Reflection;
using Soumen.Models;

namespace Soumen.Services;

public sealed class ExternalPluginCoordinator
{
    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private bool? aeTargetingEnabled;
    private bool bossModArmed;
    private bool lazyLootArmed;
    private bool dungeonFollowArmed;
    private float dungeonFollowDistance = float.NaN;
    private float? dungeonFollowOriginalDistance;
    private int? dungeonFollowOriginalSlot;
    private DateTime nextBossModSettingsReadUtc = DateTime.MinValue;
    private LazyLootRollMode? appliedLazyLootRollMode;

    public ExternalPluginCoordinator(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
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
            nextBossModSettingsReadUtc = DateTime.MinValue;
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
        if (!configuration.EnableBossModRebornIntegration || !BossModRebornInstalled || !bossModArmed
            || string.IsNullOrWhiteSpace(leaderName))
        {
            diagnostics.WriteThrottled("bmr-unavailable", "BMR跟随",
                "未启用 BMR AI、BMR 未加载或队长姓名不可用，不启动宝物库跟随。", TimeSpan.FromSeconds(10));
            return false;
        }
        if (!dungeonFollowArmed)
        {
            // BMR exposes commands to change these settings, but no command to
            // read them. Preserve the live settings before changing either one.
            if (!TryCaptureBossModFollowSettings()) return false;
            Execute($"/bmrai follow {leaderName}");
            Execute("/bmrai followtarget on");
            Execute("/bmrai followoutofcombat on");
            if (!TryReadBossModFollowState(out var followOutOfCombat, out var followTarget)
                || !followOutOfCombat || !followTarget)
            {
                diagnostics.Write("BMR跟随", "脱战跟随或目标跟随未成功开启，已撤销跟随请求。");
                Execute("/bmrai followoutofcombat off");
                if (dungeonFollowOriginalSlot is >= 0 and < 8)
                    Execute($"/bmrai follow slot{dungeonFollowOriginalSlot.Value + 1}");
                dungeonFollowOriginalDistance = null;
                dungeonFollowOriginalSlot = null;
                return false;
            }
            dungeonFollowArmed = true;
            diagnostics.Write("BMR跟随", $"已请求 BMR 跟随队长 {leaderName}，开启脱战跟随。");
        }
        if (float.IsNaN(dungeonFollowDistance) || Math.Abs(dungeonFollowDistance - distance) > 0.05f)
        {
            Execute($"/bmrai maxdistanceslot {distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            dungeonFollowDistance = distance;
            diagnostics.Write("BMR跟随", $"队友槽位跟随距离设为 {distance:F1}y。");
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
            Execute($"/bmrai maxdistanceslot {distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
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
        if (DateTime.UtcNow < nextBossModSettingsReadUtc) return false;

        try
        {
            var managerType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("BossMod", StringComparison.OrdinalIgnoreCase) == true)
                .Select(assembly => assembly.GetType("BossMod.AI.AIManager", throwOnError: false))
                .FirstOrDefault(type => type != null);
            var config = managerType?.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var configType = config?.GetType();
            var distance = configType?.GetField("MaxDistanceToSlot", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            var slot = configType?.GetField("FollowSlot", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            if (distance is not float originalDistance || slot is not int originalSlot || originalSlot is < 0 or > 7)
                throw new InvalidOperationException($"BMR AI 配置不可读：AIManager={managerType != null}，config={config != null}，distance={distance?.GetType().Name ?? "null"}，slot={slot?.GetType().Name ?? "null"}。");

            dungeonFollowOriginalDistance = originalDistance;
            dungeonFollowOriginalSlot = originalSlot;
            nextBossModSettingsReadUtc = DateTime.MinValue;
            diagnostics.Write("BMR跟随", $"已保存原队友槽位距离 {originalDistance:F1}y，原跟随槽位 {originalSlot + 1}。");
            return true;
        }
        catch (Exception ex)
        {
            nextBossModSettingsReadUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            diagnostics.WriteException("BMR跟随", "保存 BMR 原设置", ex);
            Plugin.Log.Warning(ex, "Could not preserve BMR follow settings; dungeon follow disabled.");
            return false;
        }
    }

    private static bool TryReadBossModFollowState(out bool followOutOfCombat, out bool followTarget)
    {
        followOutOfCombat = false;
        followTarget = false;
        try
        {
            var config = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("BossMod", StringComparison.OrdinalIgnoreCase) == true)
                .Select(assembly => assembly.GetType("BossMod.AI.AIManager", throwOnError: false))
                .FirstOrDefault(type => type != null)?
                .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (config == null) return false;
            var type = config.GetType();
            if (type.GetField("FollowOutOfCombat")?.GetValue(config) is not bool ooc
                || type.GetField("FollowTarget")?.GetValue(config) is not bool target)
                return false;
            followOutOfCombat = ooc;
            followTarget = target;
            return true;
        }
        catch
        {
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
