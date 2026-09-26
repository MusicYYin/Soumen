using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin;

namespace Soumen.Services;

/// <summary>Reads native Hook metadata from a selected, currently loaded plugin assembly.</summary>
public static class NativeHookInspector
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static IReadOnlyList<string> GetCandidates()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly != typeof(Plugin).Assembly && !assembly.IsDynamic)
            .Where(assembly => !IsFrameworkAssembly(assembly.GetName().Name))
            .Where(assembly => assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Dalamud")
                || Types(assembly).Any(type => type != typeof(IDalamudPlugin)
                    && typeof(IDalamudPlugin).IsAssignableFrom(type)))
            .Select(assembly => assembly.GetName().Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string Capture(DiagnosticLogger diagnostics, string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item =>
            string.Equals(item.GetName().Name, assemblyName, StringComparison.Ordinal));
        if (assembly == null) return "未找到所选插件。请确认它已在当前游戏进程中加载。";

        using var process = Process.GetCurrentProcess();
        var game = process.MainModule;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var count = 0;
        diagnostics.Write("Hook 采集", $"开始；程序集={assembly.GetName().Name}；版本={assembly.GetName().Version}；客户端模块大小=0x{game?.ModuleMemorySize ?? 0:X}。");

        foreach (var owner in Types(assembly))
        {
            foreach (var field in owner.GetFields(All | BindingFlags.DeclaredOnly))
            {
                if (!field.IsStatic || !IsRoot(field, assembly)) continue;
                try
                {
                    var instance = field.GetValue(null);
                    if (instance != null)
                        count += Inspect(instance, $"{owner.FullName}.{field.Name}", 0, seen, diagnostics, game, assembly);
                }
                catch (Exception exception)
                {
                    diagnostics.Write("Hook 采集", $"{owner.FullName}.{field.Name}：读取失败 {exception.GetType().Name}。");
                }
            }
        }

        diagnostics.Write("Hook 采集", $"结束；找到 {count} 条 Hook。");
        return $"已从 {assemblyName} 采集 {count} 条 Hook，记录位于诊断日志。";
    }

    private static int Inspect(object instance, string path, int depth, HashSet<object> seen,
        DiagnosticLogger diagnostics, ProcessModule? game, Assembly assembly)
    {
        if (!seen.Add(instance) || seen.Count > 500) return 0;
        var hook = FindHookType(instance.GetType());
        if (hook != null)
        {
            try
            {
                var address = (nint)(instance.GetType().GetProperty("Address", All)?.GetValue(instance) ?? nint.Zero);
                var enabled = instance.GetType().GetProperty("IsEnabled", All)?.GetValue(instance);
                var invoke = hook.GetGenericArguments()[0].GetMethod("Invoke", All);
                var parameters = invoke == null ? "?" : string.Join(", ", invoke.GetParameters().Select(p => p.ParameterType.Name));
                var relative = game != null && address >= game.BaseAddress
                    && address - game.BaseAddress < game.ModuleMemorySize
                    ? $"exe+0x{(long)(address - game.BaseAddress):X}" : "非主模块";
                var bytes = relative == "非主模块" ? "未读取" : ReadOriginalBytes(address, game!);
                diagnostics.Write("Hook 采集", $"{path}：{relative}；启用={enabled}；签名=({parameters})->{invoke?.ReturnType.Name ?? "?"}；原始字节={bytes}。");
                return 1;
            }
            catch (Exception exception)
            {
                diagnostics.Write("Hook 采集", $"{path}：读取 Hook 元数据失败 {exception.GetType().Name}。");
                return 0;
            }
        }

        if (depth >= 3) return 0;
        var count = 0;
        for (var type = instance.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(All | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || !MayContainHook(field, assembly)) continue;
                try
                {
                    var child = field.GetValue(instance);
                    if (child != null) count += Inspect(child, $"{path}.{field.Name}", depth + 1, seen, diagnostics, game, assembly);
                }
                catch (Exception exception)
                {
                    diagnostics.Write("Hook 采集", $"{path}.{field.Name}：读取失败 {exception.GetType().Name}。");
                }
            }
        }
        return count;
    }

    private static bool LooksLikeHook(FieldInfo field)
        => FindHookType(field.FieldType) != null
            || field.Name.Contains("Hook", StringComparison.OrdinalIgnoreCase)
            || field.FieldType.Name.Contains("Hook", StringComparison.OrdinalIgnoreCase);

    private static bool IsRoot(FieldInfo field, Assembly assembly)
        => LooksLikeHook(field)
            || typeof(IDalamudPlugin).IsAssignableFrom(field.FieldType)
            || (field.FieldType.Assembly == assembly
                && (field.Name.Contains("Instance", StringComparison.OrdinalIgnoreCase)
                    || field.Name == "Current" || field.Name == "Plugin"));

    private static bool MayContainHook(FieldInfo field, Assembly assembly)
    {
        if (field.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("auth", StringComparison.OrdinalIgnoreCase)) return false;
        return LooksLikeHook(field)
            || field.FieldType.Assembly == assembly && field.FieldType.IsClass
                && !field.FieldType.IsArray && !typeof(Delegate).IsAssignableFrom(field.FieldType);
    }

    private static bool IsFrameworkAssembly(string? name)
        => name == null || name == "System" || name.StartsWith("System.", StringComparison.Ordinal)
            || name == "Microsoft" || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name == "Dalamud" || name.StartsWith("Dalamud.", StringComparison.Ordinal)
            || name == "Lumina" || name.StartsWith("Lumina.", StringComparison.Ordinal)
            || name == "FFXIVClientStructs" || name.StartsWith("FFXIVClientStructs.", StringComparison.Ordinal);

    private static Type? FindHookType(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Dalamud.Hooking.Hook`1")
                return current;
        return null;
    }

    private static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>().ToArray(); }
        catch { return []; }
    }

    private static string ReadOriginalBytes(nint address, ProcessModule game)
    {
        const int length = 48;
        if (address - game.BaseAddress > game.ModuleMemorySize - length) return "越界";
        if (!Plugin.SigScanner.IsCopy) return "原始镜像不可用";
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(Plugin.SigScanner.SearchBase + (address - game.BaseAddress), bytes, 0, length);
            return Convert.ToHexString(bytes);
        }
        catch (Exception exception) { return $"读取失败:{exception.GetType().Name}"; }
    }
}
