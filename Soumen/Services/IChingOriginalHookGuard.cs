using System.Reflection;

namespace Soumen.Services;

/// <summary>Only blocks an entrypoint while the original plugin actually has an enabled hook there.</summary>
internal static class IChingOriginalHookGuard
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, (DateTime Checked, bool Enabled)> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    public static bool Blocks(string typeName, bool requested, DiagnosticLogger diagnostics)
    {
        if (!requested || !IsEnabled(typeName)) return false;
        diagnostics.WriteThrottled($"iching-original-{typeName}", "I-Ching Hook",
            $"原版 I-Ching 的 {typeName} Hook 仍在运行；当前入口等待原版停用后再接管。",
            TimeSpan.FromSeconds(30));
        return true;
    }

    private static bool IsEnabled(string typeName)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(typeName, out var cached) && DateTime.UtcNow - cached.Checked < TimeSpan.FromSeconds(1))
                return cached.Enabled;
            var enabled = ProbeIsEnabled(typeName);
            Cache[typeName] = (DateTime.UtcNow, enabled);
            return enabled;
        }
    }

    private static bool ProbeIsEnabled(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var ownerType = assembly.GetType($"SamplePlugin.Hook.{typeName}", false);
            var managerType = assembly.GetType("SamplePlugin.HookManager", false);
            if (ownerType == null || managerType == null) continue;

            foreach (var owner in managerType.GetFields(All))
            {
                if (!owner.IsStatic) continue;
                object? instance;
                try { instance = owner.GetValue(null); }
                catch { continue; }
                if (instance == null || !ownerType.IsInstanceOfType(instance)) continue;

                for (var current = ownerType; current != null && current != typeof(object); current = current.BaseType)
                {
                    foreach (var field in current.GetFields(All | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic) continue;
                        try
                        {
                            var hook = field.GetValue(instance);
                            if (hook?.GetType().GetProperty("IsEnabled", All)?.GetValue(hook) is true)
                                return true;
                        }
                        catch { /* Other plugin versions may expose different field layouts. */ }
                    }
                }
            }
        }
        return false;
    }
}
