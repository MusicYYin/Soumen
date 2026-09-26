using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soumen.Services;

/// <summary>Entrypoints observed in a compatible game client; validate the executable before installing a native Hook.</summary>
internal static class ToolHookAddresses
{
    private const int CapturedModuleSize = 0x380A000;
    private static readonly Dictionary<string, (int Offset, string Bytes)> Captured = new(StringComparer.Ordinal)
    {
        ["_speedUpdateHook"] = (0x8D5060, "40574883EC20488BF9488B4908488B01"), // SpeedDelegate
        ["_speed2"] = (0x1827FC0, "40534883EC5080793C00488BD90F841E"), // SpeedDelegate2
        ["_MovePermissionHook"] = (0xE12940, "48895C240848896C2410488974241848"), // MovePermissionDelegate
        ["_AntiKnockHook"] = (0x909EB0, "488BC4574881EC900000000F2970E80F"), // AntiKnockDelegate
        ["_NoFallDamageHook"] = (0x62A250, "48895C2408574883EC208BFA488BD933"), // NoFallDamageDelegate
        ["_SendNormalMoveHook"] = (0x62A970, "48895C24084889742420574881EC800F0000488B05CF8C2A"), // SendNormalMoveDelegate
        ["_SendCombatMoveHook"] = (0x62AA20, "48895C24084889742420574881EC800F0000488B051F8C2A"), // SendCombatMoveDelegate
        ["_FallCheckHook"] = (0x863220, "488BC20FB615DB282302F6C2017413F6"), // FallCheckDelegate
        ["noBewitchActionHook"] = (0x909CA0, "40534883EC500F57C0F30F114C243045"), // NoBewitchActionDelegate
        ["_StatusCheckHook"] = (0x8A2810, "4C8BDC55498DAB88FEFFFF4881EC7002"), // StatusCheckDelegate
        ["_ProcessPacketStatusEffectHookGL"] = (0xE5C939, "488BC444884820555741544155488D68"), // ProcessPacketStatusEffectHookDelegateGL
        ["_ActionRangeHook"] = (0x8DAFC0, "48895C2408574883EC30488B3DBF4E1C"), // ActionRangeDelegate
        ["_ActorRadiusHook"] = (0x905070, "40534883EC20488BD980FA017537488B"), // ActorRadiusDelegate
        ["_NoBackswingHook"] = (0x8D5150, "4883EC28488B49084533C033D2E89E16"), // NoBackswingDelegate
        ["_NoActionMoveHook"] = (0x181E460, "48895C24084889742410574883EC4048"), // NoActionMoveDelegate
        ["_getResourceSyncHook"] = (0x315190, "48895C241048896C2418488974242057"), // GetResourceSyncDelegate
        ["_getResourceAsyncHook"] = (0x314EE0, "48895C241048896C2418488974242057"), // GetResourceAsyncDelegate
    };

    public static nint Resolve(string name, DiagnosticLogger diagnostics)
        => Check(name, diagnostics);

    /// <summary>Read-only check of the executable snapshot, without installing or enabling a Hook.</summary>
    public static bool IsAvailable(string name) => Check(name, null) != 0;

    private static nint Check(string name, DiagnosticLogger? diagnostics)
    {
        if (!Captured.TryGetValue(name, out var entry))
            throw new ArgumentOutOfRangeException(nameof(name));

        using var process = Process.GetCurrentProcess();
        var module = process.MainModule;
        if (module == null || module.ModuleMemorySize != CapturedModuleSize)
        {
            diagnostics?.Write("工具 Hook", $"{name}: 客户端模块大小不符，未安装 Hook。");
            return 0;
        }

        try
        {
            var expected = Convert.FromHexString(entry.Bytes);
            var actual = new byte[expected.Length];
            var baseAddress = Plugin.SigScanner.IsCopy ? Plugin.SigScanner.SearchBase : module.BaseAddress;
            Marshal.Copy(baseAddress + entry.Offset, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                diagnostics?.Write("工具 Hook", $"{name}: 函数字节不符，未安装 Hook。");
                return 0;
            }
            return module.BaseAddress + entry.Offset;
        }
        catch (Exception exception)
        {
            diagnostics?.Write("工具 Hook", $"{name}: 地址检查失败 {exception.GetType().Name}。");
            return 0;
        }
    }
}
