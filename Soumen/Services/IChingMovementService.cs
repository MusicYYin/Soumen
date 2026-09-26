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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MovePermissionDelegate(nint conditions, uint actionId, int third, int fourth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AntiKnockbackDelegate(nint actor, float rotation, float distance, float duration, byte fifth, nint sixth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FallCheckDelegate(nint actor, nint flags, nint extra);

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
    private Hook<MovePermissionDelegate>? permissionHook;
    private Hook<AntiKnockbackDelegate>? knockbackHook;
    private Hook<FallCheckDelegate>? fallCheckHook;
    private bool speedFailed;
    private bool accelerationFailed;
    private bool fallDamageFailed;
    private bool permissionFailed;
    private bool knockbackFailed;
    private bool fallCheckFailed;

    public IChingMovementService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        fallCheckHook?.Dispose();
        knockbackHook?.Dispose();
        permissionHook?.Dispose();
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

        Manage(ref permissionHook, ref permissionFailed, configuration.IChingForceMovement,
            "_MovePermissionHook", new MovePermissionDelegate(AllowMovement));
        Manage(ref knockbackHook, ref knockbackFailed, configuration.IChingAntiKnockback,
            "_AntiKnockHook", new AntiKnockbackDelegate(IgnoreKnockback));
        Manage(ref fallCheckHook, ref fallCheckFailed, configuration.IChingNoDrop,
            "_FallCheckHook", new FallCheckDelegate(ClearFallFlags));
    }

    private void Manage<T>(ref Hook<T>? hook, ref bool failed, bool desired, string key, T detour) where T : Delegate
    {
        if (failed) return;
        try
        {
            if (desired && hook == null)
            {
                var address = IChingHookAddresses.Resolve(key, diagnostics);
                if (address == 0) { failed = true; return; }
                hook = Plugin.GameInteropProvider.HookFromAddress(address, detour);
            }
            SetEnabled(hook, desired);
        }
        catch (Exception exception) { failed = true; ReportFailure(key, exception); }
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

    private nint AllowMovement(nint conditions, uint actionId, int third, int fourth)
    {
        if (configuration.IChingForceMovement && actionId is 96 or 97 or 98 or 99 or 1001 or 1006 or 1007 or 1008)
            return 1;
        return permissionHook!.Original(conditions, actionId, third, fourth);
    }

    private nint IgnoreKnockback(nint actor, float rotation, float distance, float duration, byte fifth, nint sixth)
        => configuration.IChingAntiKnockback ? 0 : knockbackHook!.Original(actor, rotation, distance, duration, fifth, sixth);

    private nint ClearFallFlags(nint actor, nint flags, nint extra)
    {
        if (configuration.IChingNoDrop && (flags.ToInt64() & 0x700) != 0)
            flags = (nint)((flags.ToInt64() & ~0x700L) | 2L);
        return fallCheckHook!.Original(actor, flags, extra);
    }

    private void ReportFailure(string feature, Exception exception)
    {
        diagnostics.Write("I-Ching Hook", $"{feature}：{exception.GetType().Name}。");
        Plugin.Log.Error(exception, $"I-Ching {feature} hook error");
    }
}
