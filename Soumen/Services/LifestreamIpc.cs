using Dalamud.Plugin.Ipc;

namespace Soumen.Services;

/// <summary>Optional Lifestream IPC for instance and world travel.</summary>
public sealed class LifestreamIpc
{
    private readonly DiagnosticLogger diagnostics;
    private readonly ICallGateSubscriber<int> instanceCount;
    private readonly ICallGateSubscriber<int> currentInstance;
    private readonly ICallGateSubscriber<bool> canChangeInstance;
    private readonly ICallGateSubscriber<int, object> changeInstance;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> sameDc;
    private readonly ICallGateSubscriber<string, bool> crossDc;
    private readonly ICallGateSubscriber<string, bool, string, bool, int?, bool?, bool?, object> changeWorld;

    public LifestreamIpc(DiagnosticLogger diagnostics)
    {
        this.diagnostics = diagnostics;
        var pi = Plugin.PluginInterface;
        instanceCount = pi.GetIpcSubscriber<int>("Lifestream.GetNumberOfInstances");
        currentInstance = pi.GetIpcSubscriber<int>("Lifestream.GetCurrentInstance");
        canChangeInstance = pi.GetIpcSubscriber<bool>("Lifestream.CanChangeInstance");
        changeInstance = pi.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance");
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        sameDc = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        crossDc = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        changeWorld = pi.GetIpcSubscriber<string, bool, string, bool, int?, bool?, bool?, object>("Lifestream.TPAndChangeWorld");
    }

    public bool IsInstalled => Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && plugin.InternalName.Equals("Lifestream", StringComparison.OrdinalIgnoreCase));

    private T Safe<T>(string operation, Func<T> call, T fallback)
    {
        if (!IsInstalled) return fallback;
        try { return call(); }
        catch (Exception ex)
        {
            diagnostics.WriteThrottled($"lifestream-{operation}", "Lifestream",
                $"{operation} 失败：{ex.Message}", TimeSpan.FromSeconds(10));
            return fallback;
        }
    }

    public int GetNumberOfInstances() => Safe("线数", () => instanceCount.InvokeFunc(), 0);
    public int GetCurrentInstance() => Safe("当前线", () => currentInstance.InvokeFunc(), 0);
    public bool CanChangeInstance() => Safe("换线状态", () => canChangeInstance.InvokeFunc(), false);
    public bool IsBusy() => Safe("忙碌状态", () => isBusy.InvokeFunc(), false);

    public bool ChangeInstance(int number)
    {
        if (!IsInstalled || !CanChangeInstance() || IsBusy()) return false;
        return Safe("换线", () => { changeInstance.InvokeAction(number); return true; }, false);
    }

    public bool ChangeWorld(string world)
    {
        if (!IsInstalled || IsBusy()) return false;
        var isCrossDc = !Safe("同数据中心", () => sameDc.InvokeFunc(world), false);
        if (isCrossDc && !Safe("跨数据中心", () => crossDc.InvokeFunc(world), false)) return false;
        // 9 is Ul'dah's main aetheryte, the requested world-visit gateway.
        return Safe("跨服", () =>
        {
            changeWorld.InvokeAction(world, isCrossDc, string.Empty, true, 9, false, false);
            return true;
        }, false);
    }
}
