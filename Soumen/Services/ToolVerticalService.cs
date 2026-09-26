using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Soumen.Services;

/// <summary>Applies a requested vertical position offset and keeps movement packets consistent.</summary>
internal sealed unsafe class ToolVerticalService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MovementDelegate(nuint context, nint data, uint length);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<MovementDelegate>? normal;
    private Hook<MovementDelegate>? combat;

    public bool IsActive => normal?.IsEnabled == true && combat?.IsEnabled == true;
    private bool failed;
    private float appliedOffset;
    private nint lastPlayer;

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
        RestoreOffset();
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
            RestoreOffset();
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

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0) return;
        if (lastPlayer != player.Address) { lastPlayer = player.Address; appliedOffset = 0f; }
        var desired = configuration.ToolVerticalOffset;
        var change = desired - appliedOffset;
        if (MathF.Abs(change) < 0.01f) return;

        var position = player.Position;
        ((GameObject*)player.Address)->SetPosition(position.X, position.Y + change, position.Z);
        appliedOffset = desired;
    }

    private nint SendNormal(nuint context, nint data, uint length)
    {
        if (data != 0 && configuration.ToolVerticalMovement)
            ((float*)data)[3] += configuration.ToolVerticalOffset;
        return normal!.Original(context, data, length);
    }

    private void RestoreOffset()
    {
        if (appliedOffset == 0f) return;
        var current = Plugin.ObjectTable.LocalPlayer;
        if (current != null && current.Address == lastPlayer)
        {
            var position = current.Position;
            ((GameObject*)current.Address)->SetPosition(position.X, position.Y - appliedOffset, position.Z);
        }
        appliedOffset = 0f;
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
