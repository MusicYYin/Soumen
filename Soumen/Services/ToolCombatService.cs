using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace Soumen.Services;

/// <summary>Combat hooks whose native entrypoints were checked against a running client snapshot.</summary>
internal sealed class ToolCombatService : IDisposable
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

    public bool BackswingActive => noBackswing?.IsEnabled == true;
    public bool ActionRangeActive => actionRange?.IsEnabled == true;
    public bool ActorRadiusActive => actorRadius?.IsEnabled == true;
    public bool NoActionMoveActive => noActionMove?.IsEnabled == true;
    private bool backswingFailed;
    private bool rangeFailed;
    private bool radiusFailed;
    private bool noActionMoveFailed;

    public ToolCombatService(Configuration configuration, DiagnosticLogger diagnostics)
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
        if (!configuration.NoBackswingMovement || ExternalHookGuard.Blocks("NoBackswingHook", configuration.NoBackswingMovement, diagnostics))
        {
            if (noBackswing?.IsEnabled == true) noBackswing.Disable();
        }
        else if (!backswingFailed)
        {
            try
            {
                if (noBackswing == null)
                {
                    var address = ToolHookAddresses.Resolve("_NoBackswingHook", diagnostics);
                    if (address == 0) backswingFailed = true;
                    else noBackswing = Plugin.GameInteropProvider.HookFromAddress<NoBackswingDelegate>(address, OnNoBackswing);
                }
                if (noBackswing?.IsEnabled == false) { noBackswing.Enable(); diagnostics.Write("工具 Hook", "后摇可移动已接管。"); }
            }
            catch (Exception exception)
            {
                backswingFailed = true;
                ReportFailure("后摇可移动", exception);
            }
        }

        if (!configuration.ToolActionRangeEnabled || ExternalHookGuard.Blocks("ActionRangeHook", configuration.ToolActionRangeEnabled, diagnostics))
        {
            if (actionRange?.IsEnabled == true) actionRange.Disable();
        }
        else if (!rangeFailed)
        {
            try
            {
                if (actionRange == null)
                {
                    var address = ToolHookAddresses.Resolve("_ActionRangeHook", diagnostics);
                    if (address == 0) rangeFailed = true;
                    else actionRange = Plugin.GameInteropProvider.HookFromAddress<GetActionRangeDelegate>(address, GetActionRange);
                }
                if (actionRange?.IsEnabled == false) { actionRange.Enable(); diagnostics.Write("工具 Hook", "技能距离已接管。"); }
            }
            catch (Exception exception)
            {
                rangeFailed = true;
                ReportFailure("技能距离", exception);
            }
        }

        if (!configuration.ToolTargetRadiusEnabled || ExternalHookGuard.Blocks("ActorRadiusHook", configuration.ToolTargetRadiusEnabled, diagnostics))
        {
            if (actorRadius?.IsEnabled == true) actorRadius.Disable();
        }
        else if (!radiusFailed)
        {
            try
            {
                if (actorRadius == null)
                {
                    var address = ToolHookAddresses.Resolve("_ActorRadiusHook", diagnostics);
                    if (address == 0) radiusFailed = true;
                    else actorRadius = Plugin.GameInteropProvider.HookFromAddress<GetActorRadiusDelegate>(address, GetRadius);
                }
                if (actorRadius?.IsEnabled == false) { actorRadius.Enable(); diagnostics.Write("工具 Hook", "目标圈大小已接管。"); }
            }
            catch (Exception exception)
            {
                radiusFailed = true;
                ReportFailure("目标圈大小", exception);
            }
        }

        if (!configuration.ToolNoActionMove || ExternalHookGuard.Blocks("NoActionMoveHook", configuration.ToolNoActionMove, diagnostics))
        {
            if (noActionMove?.IsEnabled == true) noActionMove.Disable();
        }
        else if (!noActionMoveFailed)
        {
            try
            {
                if (noActionMove == null)
                {
                    var address = ToolHookAddresses.Resolve("_NoActionMoveHook", diagnostics);
                    if (address == 0) noActionMoveFailed = true;
                    else noActionMove = Plugin.GameInteropProvider.HookFromAddress<NoActionMoveDelegate>(address, PreventActionMovement);
                }
                if (noActionMove?.IsEnabled == false) { noActionMove.Enable(); diagnostics.Write("工具 Hook", "突进无位移已接管。"); }
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
        if (!configuration.ToolActionRangeEnabled || actionId == 0 || original <= 0f)
            return original;
        var sheet = Plugin.DataManager.GetExcelSheet<ActionRow>();
        if (sheet == null || !sheet.TryGetRow(actionId, out var action) || action.TargetArea)
            return original;
        return original + configuration.ToolActionRangeBonus;
    }

    private float GetRadius(nuint actor, byte kind)
    {
        var original = actorRadius!.Original(actor, kind);
        return configuration.ToolTargetRadiusEnabled
            ? MathF.Max(original, configuration.ToolTargetRadius)
            : original;
    }

    private ulong PreventActionMovement(ulong actor, byte moveId, ulong target, float facing, nint timeline)
    {
        // The second argument selects an ActionTimelineMove row. Zero means no action movement;
        // the original game routine applies the displacement for nonzero movement types.
        if (configuration.ToolNoActionMove && moveId != 0)
            return 0;
        return noActionMove!.Original(actor, moveId, target, facing, timeline);
    }

    private void ReportFailure(string feature, Exception exception)
    {
        diagnostics.Write("工具 Hook", $"{feature}安装失败：{exception.GetType().Name}。");
        Plugin.Log.Error(exception, $"{feature} hook failed");
    }

    // The captured NoBackswing detour takes one pointer, makes no external calls,
    // and returns that same pointer. Its native original must not be called while enabled.
    private static nint OnNoBackswing(nint value) => value;
}
