using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Soumen.Services;

/// <summary>Filters local movement restrictions at their native action and status entrypoints.</summary>
internal sealed unsafe class IChingStatusService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ForcedActionDelegate(GameObject* actor, float x, float y, float z, int fifth, nint sixth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusUpdateDelegate(StatusManager* manager);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusPacketDelegate(uint entityId, StatusEffectList* packet, bool replay, bool firstHalf);

    private static readonly HashSet<uint> MotionRestrictions =
    [
        142, 149, 604, 905, 911, 1257, 1293, 1294, 1295, 1296,
        1422, 1579, 1580, 1681, 1958, 1959, 1960, 1961, 2161, 2162,
        2163, 2164, 2381, 2382, 2383, 2384, 2538, 2539, 2540, 2541,
        2936, 3629, 3694, 3698, 3699, 3700, 3701, 3715, 3716, 3717,
        3718, 3719, 3737, 3909,
    ];

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<ForcedActionDelegate>? forcedAction;
    private Hook<StatusUpdateDelegate>? statusUpdate;
    private Hook<StatusPacketDelegate>? statusPacket;
    private bool forcedFailed;
    private bool updateFailed;
    private bool packetFailed;

    public IChingStatusService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= Update;
        statusPacket?.Dispose();
        statusUpdate?.Dispose();
        forcedAction?.Dispose();
    }

    private void Update(IFramework framework)
    {
        _ = framework;
        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                assembly.GetType("SamplePlugin.Hook.StatusCheck", false) != null)) return;

        Sync(ref forcedAction, ref forcedFailed, configuration.IChingIgnoreCharm,
            "noBewitchActionHook", new ForcedActionDelegate(InterceptForcedAction));
        Sync(ref statusUpdate, ref updateFailed, configuration.IChingStatusBlock,
            "_StatusCheckHook", new StatusUpdateDelegate(FilterStatusManager));
        Sync(ref statusPacket, ref packetFailed, configuration.IChingStatusBlock,
            "_ProcessPacketStatusEffectHookGL", new StatusPacketDelegate(FilterStatusPacket));
    }

    private void Sync<T>(ref Hook<T>? hook, ref bool failed, bool enabled, string name, T detour) where T : Delegate
    {
        if (failed) return;
        try
        {
            if (enabled && hook == null)
            {
                var address = IChingHookAddresses.Resolve(name, diagnostics);
                if (address == 0) { failed = true; return; }
                hook = Plugin.GameInteropProvider.HookFromAddress(address, detour);
            }
            if (hook != null && hook.IsEnabled != enabled)
            {
                if (enabled) hook.Enable(); else hook.Disable();
            }
        }
        catch (Exception exception)
        {
            failed = true;
            diagnostics.Write("I-Ching Hook", $"{name}安装失败：{exception.GetType().Name}。");
            Plugin.Log.Error(exception, $"I-Ching {name} hook failed");
        }
    }

    private nint InterceptForcedAction(GameObject* actor, float x, float y, float z, int fifth, nint sixth)
    {
        if (configuration.IChingIgnoreCharm && Plugin.ObjectTable.LocalPlayer != null) return 0;
        return forcedAction!.Original(actor, x, y, z, fifth, sixth);
    }

    private void FilterStatusManager(StatusManager* manager)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (configuration.IChingStatusBlock && manager != null && manager->Owner != null
            && player != null && manager->Owner->EntityId == player.EntityId)
        {
            foreach (ref var entry in manager->Status)
                if (MotionRestrictions.Contains(entry.StatusId)) entry = default;
        }
        statusUpdate!.Original(manager);
    }

    private void FilterStatusPacket(uint entityId, StatusEffectList* packet, bool replay, bool firstHalf)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (configuration.IChingStatusBlock && packet != null && player != null && entityId == player.EntityId)
        {
            foreach (ref var entry in packet->Entries)
                if (MotionRestrictions.Contains(entry.StatusID)) entry = default;
        }
        statusPacket!.Original(entityId, packet, replay, firstHalf);
    }
}
