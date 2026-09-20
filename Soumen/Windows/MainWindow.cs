using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Soumen.Models;
using Soumen.Services;

namespace Soumen.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Accent = new(0.31f, 0.67f, 0.94f, 1f);
    private static readonly Vector4 AccentSoft = new(0.10f, 0.20f, 0.29f, 0.96f);
    private static readonly Vector4 Panel = new(0.075f, 0.085f, 0.105f, 0.96f);
    private static readonly Vector4 Success = new(0.34f, 0.84f, 0.56f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.72f, 0.30f, 1f);
    private static readonly Vector4 Danger = new(0.95f, 0.36f, 0.36f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.66f, 0.72f, 1f);

    private readonly Configuration configuration;
    private readonly MapFlagAutomation automation;

    public MainWindow(Configuration configuration, MapFlagAutomation automation)
        : base("Soumen##SoumenMain")
    {
        this.configuration = configuration;
        this.automation = automation;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 500f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##SoumenTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("运行"))
        {
            DrawOverview();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("设置"))
        {
            DrawSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("关于"))
        {
            DrawAbout();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.SetWindowFontScale(1.28f);
        ImGui.TextUnformatted("Soumen");
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();

        var enabled = configuration.Enabled;
        var label = !enabled ? "已关闭" : automation.IsPaused ? "已暂停" : "运行中";
        var color = !enabled ? Muted : automation.IsPaused ? Warning : Success;
        var available = ImGui.GetContentRegionAvail().X;
        var width = ImGui.CalcTextSize(label).X + (24f * ImGuiHelpers.GlobalScale);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + available - width));
        ImGui.TextColored(color, "●  " + label);
        ImGui.Separator();
    }

    private void DrawOverview()
    {
        ImGui.Spacing();
        DrawControlBar();
        ImGui.Spacing();
        DrawStatusCard();
        ImGui.Spacing();
        DrawDestinationList();
        ImGui.Spacing();
        DrawDependencies();
    }

    private void DrawControlBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * scale);

        var enabled = configuration.Enabled;
        ImGui.PushStyleColor(ImGuiCol.Button, enabled ? new Vector4(0.34f, 0.14f, 0.17f, 1f) : new Vector4(0.11f, 0.40f, 0.66f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, enabled ? new Vector4(0.48f, 0.19f, 0.22f, 1f) : new Vector4(0.16f, 0.50f, 0.79f, 1f));
        if (ImGui.Button(enabled ? "关闭 Soumen" : "开启 Soumen", new Vector2(150f, 38f) * scale))
        {
            automation.SetEnabled(!enabled);
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();
        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Button, automation.IsPaused ? new Vector4(0.12f, 0.42f, 0.30f, 1f) : new Vector4(0.38f, 0.30f, 0.10f, 1f));
        if (ImGui.Button(automation.IsPaused ? "继续" : "暂停", new Vector2(104f, 38f) * scale))
        {
            automation.SetPaused(!automation.IsPaused);
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.Button("停止导航", new Vector2(112f, 38f) * scale))
        {
            automation.Stop();
        }
        ImGui.EndDisabled();

        ImGui.PopStyleVar();
    }

    private void DrawStatusCard()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, automation.ActiveTarget == null ? Panel : AccentSoft);
        ImGui.BeginChild("##SoumenStatusCard", new Vector2(0f, 112f * scale), false);

        ImGui.SetCursorPos(new Vector2(16f, 13f) * scale);
        ImGui.TextColored(GetStateColor(automation.State), $"●  {GetStateName(automation.State)}");
        ImGui.SetCursorPosX(16f * scale);
        ImGui.TextWrapped(automation.StatusText);

        var target = automation.ActiveTarget;
        if (target != null)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(16f * scale);
            var mode = automation.IsManualSelection ? "手动选择 · 已锁定" : "最新坐标 · 自动选择";
            ImGui.TextColored(Muted,
                $"{mode}    {target.Sender}    {target.PlaceName}  X {target.MapX:F1}  Y {target.MapY:F1}");
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawDestinationList()
    {
        DrawSectionTitle("目的地");
        var targets = automation.Destinations;
        if (targets.Count == 0)
        {
            ImGui.TextColored(Muted, "等待小队或跨服小队成员发送坐标链接");
            return;
        }

        ImGui.TextColored(Muted, "默认使用最新坐标；手动选择后锁定该目标。");
        ImGui.Spacing();

        var flags = ImGuiTableFlags.RowBg
            | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##SoumenDestinations", 6, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 82f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("发送者", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Content ID", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("坐标", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("距离", ImGuiTableColumnFlags.WidthFixed, 74f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var target in targets)
        {
            var active = automation.ActiveTarget?.Serial == target.Serial;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(active ? Accent : Muted, active ? "● 当前" : "候选");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(target.Sender);

            ImGui.TableNextColumn();
            if (target.SenderContentId == 0)
            {
                ImGui.TextColored(Muted, "未解析");
            }
            else
            {
                ImGui.TextUnformatted(target.SenderContentId.ToString());
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{target.PlaceName}  {target.MapX:F1}, {target.MapY:F1}");
            var nearby = automation.NearbyDestinationCount(target);
            if (nearby > 1)
            {
                ImGui.SameLine();
                ImGui.TextColored(Warning, $"同地点 ×{nearby}");
            }

            ImGui.TableNextColumn();
            var distance = automation.DistanceTo(target);
            ImGui.TextColored(distance == null ? Muted : Vector4.One, distance == null ? "异地图" : $"{distance:F0}y");

            ImGui.TableNextColumn();
            ImGui.PushID(target.Serial.GetHashCode());
            ImGui.BeginDisabled(active && automation.IsManualSelection);
            if (ImGui.Button(active && automation.IsManualSelection ? "已锁定" : "导航至此", new Vector2(-1f, 0f)))
            {
                automation.NavigateTo(target.Serial);
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawDependencies()
    {
        DrawSectionTitle("依赖状态");
        if (ImGui.BeginTable("##SoumenDependencies", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawDependencyRow("vnavmesh", automation.VnavmeshInstalled,
                automation.VnavmeshInstalled ? automation.VnavmeshReady ? "已就绪" : "生成网格中" : "未加载");
            DrawDependencyRow("AE Assist", automation.AeAssistInstalled,
                automation.AeAssistInstalled ? "已连接" : "未加载（可选）");
            DrawDependencyRow("BossMod Reborn", automation.BossModRebornInstalled,
                automation.BossModRebornInstalled ? "AI 已由 Soumen 管理" : "未加载（可选）");
            ImGui.EndTable();
        }
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        DrawSectionTitle("移动行为");
        DrawCheckbox("自动上坐骑", nameof(configuration.AutoMount), configuration.AutoMount, value => configuration.AutoMount = value);
        DrawCheckbox("优先飞行导航", nameof(configuration.UseFlight), configuration.UseFlight, value => configuration.UseFlight = value);
        DrawCheckbox("到达后自动落地并下坐骑", nameof(configuration.AutoDismount), configuration.AutoDismount, value => configuration.AutoDismount = value);

        ImGui.Spacing();
        DrawSectionTitle("传送方式（二选一）");
        if (ImGui.RadioButton("自行传送至更近的以太水晶", configuration.AutoTeleport))
        {
            configuration.AutoTeleport = true;
            configuration.AcceptPartyTeleportRequests = false;
            configuration.Save();
        }

        if (ImGui.RadioButton("接受队友传送邀请", configuration.AcceptPartyTeleportRequests))
        {
            configuration.AutoTeleport = false;
            configuration.AcceptPartyTeleportRequests = true;
            configuration.Save();
        }

        ImGui.Spacing();
        DrawSectionTitle("路线与恢复");
        DrawCheckbox("重新寻路后仍卡住时传送", nameof(configuration.TeleportWhenStuck), configuration.TeleportWhenStuck, value => configuration.TeleportWhenStuck = value);

        var stuckSeconds = configuration.StuckSeconds;
        ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("卡住判定时间（秒）", ref stuckSeconds, 4f, 20f, "%.1f"))
        {
            configuration.StuckSeconds = stuckSeconds;
            configuration.Save();
        }

        var tolerance = configuration.ArrivalTolerance;
        ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("到达判定（世界距离）", ref tolerance, 3f, 30f, "%.1f"))
        {
            configuration.ArrivalTolerance = tolerance;
            configuration.Save();
        }

        ImGui.Spacing();
        DrawSectionTitle("外部插件");
        DrawCheckbox("管理 AE Assist 自动选目标", nameof(configuration.EnableAeAssistIntegration), configuration.EnableAeAssistIntegration,
            value => configuration.EnableAeAssistIntegration = value);
        DrawCheckbox("运行期间启用 BossMod Reborn AI", nameof(configuration.EnableBossModRebornIntegration), configuration.EnableBossModRebornIntegration,
            value => configuration.EnableBossModRebornIntegration = value);

        ImGui.Spacing();
        DrawSectionTitle("宝物库");
        DrawCheckbox("宝物库结束且无待掷点物品时自动离开", nameof(configuration.AutoLeaveTreasureDungeon), configuration.AutoLeaveTreasureDungeon,
            value => configuration.AutoLeaveTreasureDungeon = value);
    }

    private static void DrawAbout()
    {
        ImGui.Spacing();
        DrawSectionTitle("Soumen 0.3.3");
        ImGui.TextWrapped("小队藏宝图坐标导航插件。");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "维护者：MusicYYin");
        ImGui.TextColored(Muted, "命令：/soumen · on · off · pause · resume · stop");
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private static void DrawDependencyRow(string name, bool installed, string status)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        ImGui.TextColored(installed ? Success : Muted, $"● {status}");
    }

    private void DrawCheckbox(string label, string id, bool current, Action<bool> update)
    {
        var value = current;
        if (ImGui.Checkbox($"{label}##{id}", ref value))
        {
            update(value);
            configuration.Save();
        }
    }

    private static Vector4 GetStateColor(AutomationState state)
        => state switch
        {
            AutomationState.Idle => Success,
            AutomationState.Disabled => Muted,
            AutomationState.Paused => Warning,
            AutomationState.Error => Danger,
            AutomationState.WaitingForPlayer or AutomationState.WaitingForVnavmesh or AutomationState.PlanningRoute => Warning,
            _ => Accent,
        };

    private static string GetStateName(AutomationState state)
        => state switch
        {
            AutomationState.Disabled => "已关闭",
            AutomationState.Idle => "待命",
            AutomationState.Paused => "已暂停",
            AutomationState.WaitingForPlayer => "准备中",
            AutomationState.PlanningRoute => "路线计算",
            AutomationState.Teleporting => "传送中",
            AutomationState.Mounting => "上坐骑",
            AutomationState.WaitingForVnavmesh => "等待导航",
            AutomationState.Navigating => "导航中",
            AutomationState.Landing => "落地中",
            AutomationState.Dismounting => "下坐骑",
            AutomationState.Error => "异常",
            _ => state.ToString(),
        };
}
