using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
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
        if (!configuration.FrontlineRadarEnabled)
            return;

        if (!Plugin.ClientState.IsPvP)
        {
            diagnostics.WriteThrottled("iching-frontline-inactive", "战场透视",
                $"当前地图={Plugin.ClientState.TerritoryType}，未处于 PvP 地图，未绘制。", TimeSpan.FromSeconds(30));
            return;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.Address == 0)
        {
            diagnostics.WriteThrottled("iching-frontline-no-player", "战场透视",
                "角色对象尚未加载，未绘制。", TimeSpan.FromSeconds(30));
            return;
        }

        var localBattalion = ((NativeBattleChara*)local.Address)->Battalion;
        var maxDistanceSquared = configuration.FrontlineRadarRange * configuration.FrontlineRadarRange;
        var scale = ImGuiHelpers.GlobalScale;
        var color = ImGui.ColorConvertFloat4ToU32(EnemyColor);
        var drawList = ImGui.GetBackgroundDrawList();
        var lineStart = new Vector2(ImGui.GetIO().DisplaySize.X * 0.5f, ImGui.GetIO().DisplaySize.Y * 0.85f);
        var inspected = 0;
        var enemies = 0;
        var battalions = new Dictionary<byte, int>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.Address == 0 || player.GameObjectId == local.GameObjectId
                || player.CurrentHp == 0 || Vector3.DistanceSquared(player.Position, local.Position) > maxDistanceSquared)
                continue;

            inspected++;
            var native = (NativeBattleChara*)player.Address;
            battalions.TryGetValue(native->Battalion, out var count);
            battalions[native->Battalion] = count + 1;
            // Battalion is a team identifier, and zero is a valid value for the local team in Frontline.
            // IsHostile alone is not reliable in PvP (the previous check selected zero of 50+ players).
            var enemy = native->IsHostile || native->Battalion != localBattalion;
            if (!enemy)
                continue;

            enemies++;
            if (!Plugin.GameGui.WorldToScreen(player.Position + new Vector3(0f, 1.8f, 0f), out var screen))
                continue;

            drawList.AddCircleFilled(screen, 5f * scale, color);
            var label = screen + new Vector2(9f, -8f) * scale;
            if (configuration.FrontlineRadarJobIcons)
            {
                var jobId = player.ClassJob.RowId;
                if (jobId != 0)
                    label = DrawIcon(drawList, label, 62100u + jobId, scale);
            }
            if (configuration.FrontlineRadarBattleHighIcons)
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Status>();
                foreach (var effect in player.StatusList)
                {
                    if (sheet == null || !sheet.TryGetRow(effect.StatusId, out var status)) continue;
                    var name = status.Name.ToString();
                    if (!name.Contains("Battle High", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("战意", StringComparison.Ordinal)
                        && !name.Contains("戰意", StringComparison.Ordinal)) continue;
                    label = DrawIcon(drawList, label, status.Icon, scale);
                    break;
                }
            }
            drawList.AddText(label, color, player.Name.TextValue);
            if (configuration.FrontlineRadarLines)
                drawList.AddLine(lineStart, screen, color, 1f * scale);
        }

        diagnostics.WriteThrottled("iching-frontline-radar", "战场透视",
            $"地图={Plugin.ClientState.TerritoryType}，自身阵营={localBattalion}，附近玩家={inspected}，判定敌方={enemies}，阵营分布={string.Join(",", battalions.Select(pair => $"{pair.Key}:{pair.Value}"))}。",
            TimeSpan.FromSeconds(10));
    }

    private static Vector2 DrawIcon(ImDrawListPtr drawList, Vector2 position, uint iconId, float scale)
    {
        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        if (texture.Handle != 0)
        {
            var size = new Vector2(18f * scale);
            drawList.AddImage(texture.Handle, position, position + size);
            position.X += size.X + 3f * scale;
        }
        return position;
    }
}
