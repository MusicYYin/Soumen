using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Soumen.Services;

/// <summary>One-time migration diagnostic. No dependency on or calls into I-Ching gameplay methods.</summary>
public static class IChingHookProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static string Capture(DiagnosticLogger diagnostics)
    {
        var manager = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("SamplePlugin.HookManager", throwOnError: false))
            .FirstOrDefault(type => type != null);
        if (manager == null)
        {
            diagnostics.Write("I-Ching", "未找到正在运行的 I-Ching HookManager。");
            return "未找到运行中的 I-Ching 本体。请先加载 I-Ching，再采集一次。";
        }

        using var process = Process.GetCurrentProcess();
        var module = process.MainModule;
        var baseAddress = module?.BaseAddress ?? nint.Zero;
        var moduleSize = module?.ModuleMemorySize ?? 0;
        var count = 0;
        diagnostics.Write("I-Ching", $"Hook 快照开始；本体={manager.Assembly.GetName().Name}，版本={manager.Assembly.GetName().Version}，客户端模块大小={moduleSize:X}。");

        foreach (var owner in manager.GetFields(All).Where(field => field.IsStatic))
        {
            var name = owner.Name;
            if (!name.Contains("my", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Hook", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var instance = owner.GetValue(null);
                if (instance == null)
                {
                    diagnostics.Write("I-Ching", $"{name}: 未实例化");
                    continue;
                }

                var found = false;
                for (var type = instance.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(All | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic) continue;
                        object? value;
                        try { value = field.GetValue(instance); }
                        catch (Exception e) { diagnostics.Write("I-Ching", $"{name}.{field.Name}: 读取失败 {e.GetType().Name}"); continue; }
                        if (value == null) continue;

                        var hookBase = FindGenericHook(value.GetType());
                        if (hookBase == null) continue;
                        found = true;
                        count++;
                        var address = (nint)(value.GetType().GetProperty("Address", All)?.GetValue(value) ?? nint.Zero);
                        var enabled = value.GetType().GetProperty("IsEnabled", All)?.GetValue(value);
                        var delegateType = hookBase.GetGenericArguments()[0];
                        var invoke = delegateType.GetMethod("Invoke", All);
                        var parameters = invoke == null ? "?" : string.Join(", ", invoke.GetParameters().Select(p => p.ParameterType.Name));
                        var relative = baseAddress != 0 && address >= baseAddress && address - baseAddress < moduleSize
                            ? $"exe+0x{(long)(address - baseAddress):X}" : "非客户端模块";
                        var bytes = relative == "非客户端模块" ? "省略" : ReadValidatedBytes(address, baseAddress, moduleSize);
                        diagnostics.Write("I-Ching", $"{name}.{field.Name}: {relative}; 启用={enabled}; {delegateType.Name}({parameters})->{invoke?.ReturnType.Name ?? "?"}; 原始字节={bytes}");
                    }
                }

                if (!found)
                    diagnostics.Write("I-Ching", $"{name}: 类型={instance.GetType().FullName}，未发现原生 Hook 字段");
            }
            catch (Exception e)
            {
                diagnostics.Write("I-Ching", $"{name}: 采集失败 {e.GetType().Name}: {e.Message}");
            }
        }

        var fishing = manager.Assembly.GetType("SamplePlugin.Hook.AutoCancelFSHAnimationHook", false);
        if (fishing != null)
        {
            foreach (var name in new[] { "FishingAnimations", "NothingPathBytes" })
            {
                try
                {
                    var value = fishing.GetField(name, All)?.GetValue(null);
                    if (value is byte[] bytes)
                        diagnostics.Write("I-Ching", $"{name}: {Encoding.UTF8.GetString(bytes).TrimEnd('\0')}");
                    else if (value is IEnumerable entries)
                    {
                        var paths = entries.Cast<object>().Select(item => item?.ToString() ?? "")
                            .Where(path => path.Length is > 0 and < 220).Take(100);
                        diagnostics.Write("I-Ching", $"{name}: {string.Join(" | ", paths)}");
                    }
                }
                catch (Exception e) { diagnostics.Write("I-Ching", $"{name}: 读取失败 {e.GetType().Name}"); }
            }
        }

        diagnostics.Write("I-Ching", $"Hook 快照结束；共 {count} 条原生 Hook。");
        return $"已采集 {count} 条 Hook；请将诊断日志发给我，我会集中还原全部目标功能。";
    }

    private static Type? FindGenericHook(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Dalamud.Hooking.Hook`1")
                return current;
        return null;
    }

    private static string ReadValidatedBytes(nint address, nint baseAddress, int moduleSize)
    {
        const int length = 48;
        if (address < baseAddress || (long)(address - baseAddress) > moduleSize - length)
            return "越界";

        try
        {
            var bytes = new byte[length];
            // Dalamud normally scans a copy of the original module. Live hook targets contain jump patches.
            var source = Plugin.SigScanner.IsCopy
                ? Plugin.SigScanner.SearchBase + (address - baseAddress)
                : address;
            Marshal.Copy(source, bytes, 0, length);
            return Convert.ToHexString(bytes);
        }
        catch (Exception e) { return $"读取失败:{e.GetType().Name}"; }
    }
}
