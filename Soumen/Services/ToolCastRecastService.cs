using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Adjusts action cast and recast calculations on the captured client build.</summary>
internal sealed unsafe class ToolCastRecastService : IDisposable
{
    private const string CastTimeCall = "E8 ?? ?? ?? ?? 45 ?? ?? 33 ?? 48 ?? ?? 66 ?? ?? ??";
    private const string CastProgressEntry = "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 0F 29 74 24 ?? 0F B6 49";
    private const string CastProgressValue = "F3 44 0F 2C C0 BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? F3 44 0F 10 1D";
    private const string RecastEntry =
        "48 89 5C 24 ?? 48 89 74 24 ?? 55 57 41 ?? 41 ?? 41 ?? 48 ?? ?? 48 ?? ?? ?? 4C ?? ?? ?? ?? ?? ??";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCastTimeDelegate(uint type, uint actionId, bool adjusted, byte* context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CastProgressDelegate(nint data, uint actionId, float progress, float total);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long GetRecastTimeDelegate(int type, int actionId, char variant);

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private Hook<GetCastTimeDelegate>? castTime;
    private Hook<CastProgressDelegate>? castProgress;
    private Hook<GetRecastTimeDelegate>? recastTime;

    public bool CastActive => castTime?.IsEnabled == true && castProgress?.IsEnabled == true;
    public bool RecastActive => recastTime?.IsEnabled == true;
    private float* castProgressValue;
    private bool castFailed;
    private bool recastFailed;

    public ToolCastRecastService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.Framework.Update += Update;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= Update;
        recastTime?.Dispose();
        castProgress?.Dispose();
        castTime?.Dispose();
    }

    private void Update(IFramework framework)
    {
        _ = framework;
        var castRequested = configuration.ToolCastReduction
            && !ExternalHookGuard.Blocks("CastHook", true, diagnostics);
        var recastRequested = configuration.ToolRecastReduction
            && !ExternalHookGuard.Blocks("RecastHook", true, diagnostics);

        if (castRequested && castTime == null && !castFailed)
        {
            try
            {
                VerifyClient();
                castTime = Plugin.GameInteropProvider.HookFromSignature<GetCastTimeDelegate>(CastTimeCall, ShortenCast);
                castProgress = Plugin.GameInteropProvider.HookFromSignature<CastProgressDelegate>(CastProgressEntry, UpdateCastProgress);
                var address = Plugin.SigScanner.GetStaticAddressFromSig(CastProgressValue, 18);
                if (address == 0) throw new InvalidOperationException("咏唱进度地址不存在");
                castProgressValue = (float*)address;
            }
            catch (Exception e)
            {
                castFailed = true;
                castProgress?.Dispose();
                castTime?.Dispose();
                castProgress = null;
                castTime = null;
                Report("咏唱缩减", e);
            }
        }

        if (recastRequested && recastTime == null && !recastFailed)
        {
            try
            {
                VerifyClient();
                recastTime = Plugin.GameInteropProvider.HookFromSignature<GetRecastTimeDelegate>(RecastEntry, ShortenRecast);
            }
            catch (Exception e) { recastFailed = true; Report("复唱缩减", e); }
        }

        try
        {
            Switch(castTime, castRequested && !castFailed, "咏唱缩减计时");
            Switch(castProgress, castRequested && !castFailed, "咏唱缩减进度");
            Switch(recastTime, recastRequested && !recastFailed, "复唱缩减");
        }
        catch (Exception e) { Report("咏唱／复唱 Hook", e); }
    }

    private static void VerifyClient()
    {
        using var process = Process.GetCurrentProcess();
        if (process.MainModule?.ModuleMemorySize != 0x380A000)
            throw new InvalidOperationException("客户端模块与 Hook 快照不同");
    }

    private void Switch<T>(Hook<T>? hook, bool enabled, string feature) where T : Delegate
    {
        if (hook == null || hook.IsEnabled == enabled) return;
        if (enabled) { hook.Enable(); diagnostics.Write("工具 Hook", $"{feature}已接管。"); }
        else hook.Disable();
    }

    private int ShortenCast(uint type, uint actionId, bool adjusted, byte* context)
    {
        var original = castTime!.Original(type, actionId, adjusted, context);
        return configuration.ToolCastReduction && original > 0
            ? Math.Max(0, original - (int)(configuration.ToolCastSeconds * 1000f))
            : original;
    }

    private uint UpdateCastProgress(nint data, uint actionId, float progress, float total)
    {
        if (configuration.ToolCastReduction && data != 0 && castProgressValue != null
            && *(uint*)(data + 4) == actionId)
        {
            progress = MathF.Max(0f, progress - configuration.ToolCastSeconds);
            *castProgressValue = progress;
        }
        return castProgress!.Original(data, actionId, progress, total);
    }

    private long ShortenRecast(int type, int actionId, char variant)
    {
        var original = recastTime!.Original(type, actionId, variant);
        return configuration.ToolRecastReduction && type == 1 && original > 0
            ? Math.Max(0L, original - (long)(configuration.ToolRecastSeconds * 1000f))
            : original;
    }

    private void Report(string feature, Exception exception)
    {
        diagnostics.Write("工具 Hook", $"{feature}不可用：{exception.GetType().Name}。");
        Plugin.Log.Error(exception, $"Soumen {feature} hook failed");
    }
}
