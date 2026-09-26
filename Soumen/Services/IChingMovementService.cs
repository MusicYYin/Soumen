using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Movement features based on verified native entrypoints from the running 0.1.6.6 plugin.</summary>
internal sealed class IChingMovementService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float SpeedDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AccelerationDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FallDamageDelegate(nuint actor, uint flags);

    private static readonly HashSet<uint> MovementLockStatuses =
    [
        14, 67, 181, 240, 436, 484, 502, 623, 674, 709, 1073, 1107,
        1114, 1141, 1147, 1259, 1344, 1394, 1595, 1790, 1796, 1935,
        2099, 2158, 2391, 2551, 2662, 2731, 3167, 3284, 3472, 3473,
        3548, 3943, 3948, 4334, 4341,
    ];

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<SpeedDelegate>? speedHook;
    private Hook<AccelerationDelegate>? accelerationHook;
    private Hook<FallDamageDelegate>? fallDamageHook;
    private bool speedFailed;
    private bool accelerationFailed;
    private bool fallDamageFailed;

    public IChingMovementService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        fallDamageHook?.Dispose();
        accelerationHook?.Dispose();
        speedHook?.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        // The source plugin may remain loaded during the one-time snapshot; do not stack these hooks.
        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetType("SamplePlugin.Hook.MySpeedHook", false) != null)) return;

        if (configuration.IChingSpeedEnabled && speedHook == null && !speedFailed)
        {
            try
            {
                var address = IChingHookAddresses.Resolve("_speedUpdateHook", diagnostics);
                if (address == 0) speedFailed = true;
                else speedHook = Plugin.GameInteropProvider.HookFromAddress<SpeedDelegate>(address, GetSpeed);
            }
            catch (Exception e) { speedFailed = true; ReportFailure("移速", e); }
        }

        if (configuration.IChingMaxAcceleration && accelerationHook == null && !accelerationFailed)
        {
            try
            {
                var address = IChingHookAddresses.Resolve("_speed2", diagnostics);
                if (address == 0) accelerationFailed = true;
                else accelerationHook = Plugin.GameInteropProvider.HookFromAddress<AccelerationDelegate>(address, ApplyAcceleration);
            }
            catch (Exception e) { accelerationFailed = true; ReportFailure("最大加速度", e); }
        }

        if (configuration.IChingNoFallDamage && fallDamageHook == null && !fallDamageFailed)
        {
            try
            {
                var address = IChingHookAddresses.Resolve("_NoFallDamageHook", diagnostics);
                if (address == 0) fallDamageFailed = true;
                else fallDamageHook = Plugin.GameInteropProvider.HookFromAddress<FallDamageDelegate>(address, IgnoreFallDamage);
            }
            catch (Exception e) { fallDamageFailed = true; ReportFailure("掉落无伤", e); }
        }

        try
        {
            SetEnabled(speedHook, configuration.IChingSpeedEnabled);
            SetEnabled(accelerationHook, configuration.IChingMaxAcceleration);
            SetEnabled(fallDamageHook, configuration.IChingNoFallDamage);
        }
        catch (Exception exception) { ReportFailure("移动 Hook 开关", exception); }
    }

    private static void SetEnabled<T>(Hook<T>? hook, bool desired) where T : Delegate
    {
        if (hook == null || hook.IsEnabled == desired) return;
        if (desired) hook.Enable(); else hook.Disable();
    }

    private float GetSpeed(nint context)
    {
        var original = speedHook!.Original(context);
        if (!configuration.IChingSpeedEnabled || !float.IsFinite(original)) return original;
        if (Plugin.ClientState.IsPvP || Plugin.Condition[ConditionFlag.InDeepDungeon])
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null || local.StatusList.Any(status => MovementLockStatuses.Contains(status.StatusId)))
                return original;
        }
        return original + configuration.IChingSpeedBonus;
    }

    private void ApplyAcceleration(nint context)
    {
        if (configuration.IChingMaxAcceleration && context != 0)
        {
            try { SafeMemory.Write(context + 0x44, 100f); }
            catch (Exception exception) { ReportFailure("最大加速度写入", exception); }
        }
        accelerationHook!.Original(context);
    }

    private nint IgnoreFallDamage(nuint actor, uint flags)
        => configuration.IChingNoFallDamage ? 0 : fallDamageHook!.Original(actor, flags);

    private void ReportFailure(string feature, Exception exception)
    {
        diagnostics.Write("I-Ching Hook", $"{feature}：{exception.GetType().Name}。");
        Plugin.Log.Error(exception, $"I-Ching {feature} hook error");
    }
}
