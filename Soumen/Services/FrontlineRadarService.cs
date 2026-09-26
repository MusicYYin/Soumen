using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace Soumen.Services;

/// <summary>Shows loaded hostile players through world geometry in PvP areas.</summary>
public sealed class FrontlineRadarService : IDisposable
{
    private static readonly Vector4 EnemyColor = new(1f, 0.31f, 0.35f, 1f);
    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;

    public FrontlineRadarService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        Plugin.PluginInterface.UiBuilder.Draw += Draw;
    }

    public void Dispose() => Plugin.PluginInterface.UiBuilder.Draw -= Draw;

    private unsafe void Draw()
    {
        if (!configuration.FrontlineRadarEnabled || !Plugin.ClientState.IsPvP)
            return;

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.Address == 0)
            return;

        var localBattalion = ((NativeBattleChara*)local.Address)->Battalion;
        var maxDistanceSquared = configuration.FrontlineRadarRange * configuration.FrontlineRadarRange;
        var scale = ImGuiHelpers.GlobalScale;
        var color = ImGui.ColorConvertFloat4ToU32(EnemyColor);
        var drawList = ImGui.GetBackgroundDrawList();
        var lineStart = new Vector2(ImGui.GetIO().DisplaySize.X * 0.5f, ImGui.GetIO().DisplaySize.Y * 0.85f);
        var inspected = 0;
        var enemies = 0;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.Address == 0 || player.GameObjectId == local.GameObjectId
                || player.CurrentHp == 0 || Vector3.DistanceSquared(player.Position, local.Position) > maxDistanceSquared)
                continue;

            inspected++;
            var native = (NativeBattleChara*)player.Address;
            var enemy = native->IsHostile || (localBattalion != 0 && native->Battalion != 0 && native->Battalion != localBattalion);
            if (!enemy)
                continue;

            enemies++;
            if (!Plugin.GameGui.WorldToScreen(player.Position + new Vector3(0f, 1.8f, 0f), out var screen))
                continue;

            drawList.AddCircleFilled(screen, 5f * scale, color);
            drawList.AddText(screen + new Vector2(9f, -8f) * scale, color, player.Name.TextValue);
            if (configuration.FrontlineRadarLines)
                drawList.AddLine(lineStart, screen, color, 1f * scale);
        }

        diagnostics.WriteThrottled("iching-frontline-radar", "战场透视",
            $"地图={Plugin.ClientState.TerritoryType}，自身阵营={localBattalion}，附近玩家={inspected}，判定敌方={enemies}。",
            TimeSpan.FromSeconds(10));
    }
}
