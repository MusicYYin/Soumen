using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Offsets outgoing movement heights while leaving the locally rendered player in place.</summary>
internal sealed unsafe class ToolVerticalService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MovementDelegate(nuint context, nint data, uint length);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<MovementDelegate>? normal;
    private Hook<MovementDelegate>? combat;

    private bool failed;

    public ToolVerticalService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= Update;
        combat?.Dispose();
        normal?.Dispose();
    }

    private void Update(IFramework framework)
    {
        _ = framework;
        var enabled = configuration.ToolVerticalMovement
            && !ExternalHookGuard.Blocks("YMove", true, diagnostics);
        if (!enabled || failed)
        {
            if (normal?.IsEnabled == true) normal.Disable();
            if (combat?.IsEnabled == true) combat.Disable();
            return;
        }

        if (normal == null || combat == null)
        {
            try
            {
                var normalAddress = ToolHookAddresses.Resolve("_SendNormalMoveHook", diagnostics);
                var combatAddress = ToolHookAddresses.Resolve("_SendCombatMoveHook", diagnostics);
                if (normalAddress == 0 || combatAddress == 0) { failed = true; return; }
                normal = Plugin.GameInteropProvider.HookFromAddress<MovementDelegate>(normalAddress, SendNormal);
                combat = Plugin.GameInteropProvider.HookFromAddress<MovementDelegate>(combatAddress, SendCombat);
            }
            catch (Exception exception)
            {
                failed = true;
                diagnostics.Write("工具 Hook", $"飞天遁地安装失败：{exception.GetType().Name}。");
                Plugin.Log.Error(exception, "Vertical movement hooks failed");
                combat?.Dispose();
                normal?.Dispose();
                combat = null;
                normal = null;
                return;
            }
        }

        if (normal?.IsEnabled == false) { normal.Enable(); diagnostics.Write("工具 Hook", "飞天遁地普通移动已接管。"); }
        if (combat?.IsEnabled == false) { combat.Enable(); diagnostics.Write("工具 Hook", "飞天遁地战斗移动已接管。"); }
    }

    private nint SendNormal(nuint context, nint data, uint length)
    {
        if (data != 0 && configuration.ToolVerticalMovement)
            ((float*)data)[3] += configuration.ToolVerticalOffset;
        return normal!.Original(context, data, length);
    }

    private nint SendCombat(nuint context, nint data, uint length)
    {
        if (data != 0 && configuration.ToolVerticalMovement)
        {
            ((float*)data)[4] += configuration.ToolVerticalOffset;
            ((float*)data)[7] += configuration.ToolVerticalOffset;
        }
        return combat!.Original(context, data, length);
    }
}
