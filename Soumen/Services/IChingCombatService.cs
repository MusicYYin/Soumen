using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Combat hooks whose original detour behavior has been confirmed in the 0.1.6.6 assembly.</summary>
internal sealed class IChingCombatService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NoBackswingDelegate(nint value);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<NoBackswingDelegate>? noBackswing;
    private bool failed;

    public IChingCombatService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        noBackswing?.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        if (!configuration.NoBackswingMovement || failed)
        {
            if (noBackswing?.IsEnabled == true) noBackswing.Disable();
            return;
        }

        // Installing two detours on the same function while I-Ching is active is undefined.
        if (noBackswing == null && AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                assembly.GetType("SamplePlugin.Hook.NoBackswingHook", false) != null))
            return;

        try
        {
            if (noBackswing == null)
            {
                var address = IChingHookAddresses.Resolve("_NoBackswingHook", diagnostics);
                if (address == 0) { failed = true; return; }
                noBackswing = Plugin.GameInteropProvider.HookFromAddress<NoBackswingDelegate>(address, OnNoBackswing);
            }

            if (!noBackswing.IsEnabled) noBackswing.Enable();
        }
        catch (Exception exception)
        {
            failed = true;
            diagnostics.Write("I-Ching Hook", $"后摇可移动安装失败：{exception.GetType().Name}。");
            Plugin.Log.Error(exception, "NoBackswing hook failed");
        }
    }

    // The 0.1.6.6 NoBackswingDetour takes one pointer, makes no external calls,
    // and returns that same pointer. Its native original must not be called while enabled.
    private static nint OnNoBackswing(nint value) => value;
}
