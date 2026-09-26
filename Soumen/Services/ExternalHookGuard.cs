using System.Reflection;

namespace Soumen.Services;

/// <summary>Waits while another loaded plugin still has an enabled Hook at the matching feature entrypoint.</summary>
internal static class ExternalHookGuard
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, (DateTime Checked, bool Enabled)> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    public static bool Blocks(string typeName, bool requested, DiagnosticLogger diagnostics)
    {
        if (!requested || !IsEnabled(typeName)) return false;
        diagnostics.WriteThrottled($"soumen-tools-external-{typeName}", "工具 Hook",
            $"其他插件的 {typeName} Hook 仍在运行；Soumen 暂停该入口以避免重复安装。",
            TimeSpan.FromSeconds(30));
        return true;
    }

    public static bool HasConflict(string typeName) => IsEnabled(typeName);

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
            if (assembly == typeof(Plugin).Assembly || assembly.IsDynamic) continue;
            Type[] types;
            try
            {
                if (!assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Dalamud")) continue;
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception) { types = exception.Types.OfType<Type>().ToArray(); }
            catch { continue; }

            var ownerType = types.FirstOrDefault(type => type.Name == typeName
                && type.Namespace?.Contains("Hook", StringComparison.OrdinalIgnoreCase) == true);
            var managerType = types.FirstOrDefault(type => type.Name == "HookManager");
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
