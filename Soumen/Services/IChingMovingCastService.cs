using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Suppresses local position packets during the final part of a cast.</summary>
internal sealed class IChingMovingCastService : IDisposable
{
    private const string SendPacketCall = "E8 ?? ?? ?? ?? 84 ?? 74 ?? 48 ?? ?? C7 87 ?? ?? ?? ?? ?? ?? ?? ??";
    private const string NormalPositionOpcode = "41 B8 ?? ?? ?? ?? F6 C2";
    private const string CombatPositionOpcode =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B D8";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool SendPacketDelegate(nint client, nint packet, uint third, uint fourth, bool priority);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<SendPacketDelegate>? packetHook;
    private uint normalOpcode;
    private uint combatOpcode;
    private bool failed;
    private long suppressed;

    public IChingMovingCastService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= Update;
        packetHook?.Dispose();
    }

    private void Update(IFramework framework)
    {
        _ = framework;
        if (!configuration.IChingMovingCast)
        {
            if (packetHook?.IsEnabled == true) packetHook.Disable();
            return;
        }
        if (failed) return;
        if (IChingOriginalHookGuard.Blocks("NetRe", true, diagnostics))
        {
            if (packetHook?.IsEnabled == true) packetHook.Disable();
            return;
        }

        try
        {
            if (packetHook == null)
            {
                using var process = Process.GetCurrentProcess();
                if (process.MainModule?.ModuleMemorySize != 0x380A000)
                    throw new InvalidOperationException("客户端版本与 Hook 快照不同");

                var normal = Plugin.SigScanner.ScanText(NormalPositionOpcode);
                var combat = Plugin.SigScanner.ScanText(CombatPositionOpcode);
                if (!SafeMemory.Read<uint>(normal + 2, out normalOpcode)
                    || !SafeMemory.Read<uint>(combat + 81, out combatOpcode)
                    || normalOpcode == 0 || combatOpcode == 0
                    || normalOpcode > ushort.MaxValue || combatOpcode > ushort.MaxValue)
                    throw new InvalidOperationException("未能确认当前客户端的移动包编号");

                packetHook = Plugin.GameInteropProvider.HookFromSignature<SendPacketDelegate>(SendPacketCall, InterceptPacket);
                diagnostics.Write("I-Ching Hook", $"移动读条已读取本地移动包编号：{normalOpcode}/{combatOpcode}。");
            }
            if (!packetHook.IsEnabled) { packetHook.Enable(); diagnostics.Write("I-Ching Hook", "移动读条发送包入口已接管。"); }
        }
        catch (Exception exception)
        {
            failed = true;
            packetHook?.Dispose();
            packetHook = null;
            diagnostics.Write("I-Ching Hook", $"移动读条不可用：{exception.GetType().Name} {exception.Message}。");
        }

        var count = Interlocked.Exchange(ref suppressed, 0);
        if (count > 0)
            diagnostics.WriteThrottled("iching-moving-cast", "移动读条", $"窗口内已拦截 {count} 个位置更新包。", TimeSpan.FromSeconds(10));
    }

    private unsafe bool InterceptPacket(nint client, nint packet, uint third, uint fourth, bool priority)
    {
        if (configuration.IChingMovingCast && packet != 0)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player?.IsCasting == true)
            {
                var remaining = player.TotalCastTime - player.CurrentCastTime;
                if (remaining >= 0f && remaining <= configuration.IChingMovingCastWindow)
                {
                    var opcode = *(ushort*)packet;
                    if (opcode == normalOpcode || opcode == combatOpcode)
                    {
                        Interlocked.Increment(ref suppressed);
                        return true;
                    }
                }
            }
        }
        return packetHook!.Original(client, packet, third, fourth, priority);
    }
}
