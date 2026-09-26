using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace Soumen.Services;

/// <summary>Combat hooks whose original detour behavior has been confirmed in the 0.1.6.6 assembly.</summary>
internal sealed class IChingCombatService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NoBackswingDelegate(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetActionRangeDelegate(uint actionId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetActorRadiusDelegate(nuint actor, byte kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong NoActionMoveDelegate(ulong actor, byte moveId, ulong target, float facing, nint timeline);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<NoBackswingDelegate>? noBackswing;
    private Hook<GetActionRangeDelegate>? actionRange;
    private Hook<GetActorRadiusDelegate>? actorRadius;
    private Hook<NoActionMoveDelegate>? noActionMove;
    private bool backswingFailed;
    private bool rangeFailed;
    private bool radiusFailed;
    private bool noActionMoveFailed;

    public IChingCombatService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        actorRadius?.Dispose();
        noActionMove?.Dispose();
        actionRange?.Dispose();
        noBackswing?.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        if (!configuration.NoBackswingMovement || IChingOriginalHookGuard.Blocks("NoBackswingHook", configuration.NoBackswingMovement, diagnostics))
        {
            if (noBackswing?.IsEnabled == true) noBackswing.Disable();
        }
        else if (!backswingFailed)
        {
            try
            {
                if (noBackswing == null)
                {
                    var address = IChingHookAddresses.Resolve("_NoBackswingHook", diagnostics);
                    if (address == 0) backswingFailed = true;
                    else noBackswing = Plugin.GameInteropProvider.HookFromAddress<NoBackswingDelegate>(address, OnNoBackswing);
                }
                if (noBackswing?.IsEnabled == false) { noBackswing.Enable(); diagnostics.Write("I-Ching Hook", "后摇可移动已接管。"); }
            }
            catch (Exception exception)
            {
                backswingFailed = true;
                ReportFailure("后摇可移动", exception);
            }
        }

        if (!configuration.IChingActionRangeEnabled || IChingOriginalHookGuard.Blocks("ActionRangeHook", configuration.IChingActionRangeEnabled, diagnostics))
        {
            if (actionRange?.IsEnabled == true) actionRange.Disable();
        }
        else if (!rangeFailed)
        {
            try
            {
                if (actionRange == null)
                {
                    var address = IChingHookAddresses.Resolve("_ActionRangeHook", diagnostics);
                    if (address == 0) rangeFailed = true;
                    else actionRange = Plugin.GameInteropProvider.HookFromAddress<GetActionRangeDelegate>(address, GetActionRange);
                }
                if (actionRange?.IsEnabled == false) { actionRange.Enable(); diagnostics.Write("I-Ching Hook", "技能距离已接管。"); }
            }
            catch (Exception exception)
            {
                rangeFailed = true;
                ReportFailure("技能距离", exception);
            }
        }

        if (!configuration.IChingTargetRadiusEnabled || IChingOriginalHookGuard.Blocks("ActorRadiusHook", configuration.IChingTargetRadiusEnabled, diagnostics))
        {
            if (actorRadius?.IsEnabled == true) actorRadius.Disable();
        }
        else if (!radiusFailed)
        {
            try
            {
                if (actorRadius == null)
                {
                    var address = IChingHookAddresses.Resolve("_ActorRadiusHook", diagnostics);
                    if (address == 0) radiusFailed = true;
                    else actorRadius = Plugin.GameInteropProvider.HookFromAddress<GetActorRadiusDelegate>(address, GetRadius);
                }
                if (actorRadius?.IsEnabled == false) { actorRadius.Enable(); diagnostics.Write("I-Ching Hook", "目标圈大小已接管。"); }
            }
            catch (Exception exception)
            {
                radiusFailed = true;
                ReportFailure("目标圈大小", exception);
            }
        }

        if (!configuration.IChingNoActionMove || IChingOriginalHookGuard.Blocks("NoActionMoveHook", configuration.IChingNoActionMove, diagnostics))
        {
            if (noActionMove?.IsEnabled == true) noActionMove.Disable();
        }
        else if (!noActionMoveFailed)
        {
            try
            {
                if (noActionMove == null)
                {
                    var address = IChingHookAddresses.Resolve("_NoActionMoveHook", diagnostics);
                    if (address == 0) noActionMoveFailed = true;
                    else noActionMove = Plugin.GameInteropProvider.HookFromAddress<NoActionMoveDelegate>(address, PreventActionMovement);
                }
                if (noActionMove?.IsEnabled == false) { noActionMove.Enable(); diagnostics.Write("I-Ching Hook", "突进无位移已接管。"); }
            }
            catch (Exception exception)
            {
                noActionMoveFailed = true;
                ReportFailure("突进无位移", exception);
            }
        }
    }

    private float GetActionRange(uint actionId)
    {
        var original = actionRange!.Original(actionId);
        if (!configuration.IChingActionRangeEnabled || actionId == 0 || original <= 0f)
            return original;
        var sheet = Plugin.DataManager.GetExcelSheet<ActionRow>();
        if (sheet == null || !sheet.TryGetRow(actionId, out var action) || action.TargetArea)
            return original;
        return original + configuration.IChingActionRangeBonus;
    }

    private float GetRadius(nuint actor, byte kind)
    {
        var original = actorRadius!.Original(actor, kind);
        return configuration.IChingTargetRadiusEnabled
            ? MathF.Max(original, configuration.IChingTargetRadius)
            : original;
    }

    private ulong PreventActionMovement(ulong actor, byte moveId, ulong target, float facing, nint timeline)
    {
        // The second argument selects an ActionTimelineMove row. Zero means no action movement;
        // the original game routine applies the displacement for nonzero movement types.
        if (configuration.IChingNoActionMove && moveId != 0)
            return 0;
        return noActionMove!.Original(actor, moveId, target, facing, timeline);
    }

    private void ReportFailure(string feature, Exception exception)
    {
        diagnostics.Write("I-Ching Hook", $"{feature}安装失败：{exception.GetType().Name}。");
        Plugin.Log.Error(exception, $"{feature} hook failed");
    }

    // The 0.1.6.6 NoBackswingDetour takes one pointer, makes no external calls,
    // and returns that same pointer. Its native original must not be called while enabled.
    private static nint OnNoBackswing(nint value) => value;
}
