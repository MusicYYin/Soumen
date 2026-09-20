using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Soumen.Models;
using Soumen.Services;

namespace Soumen.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Accent = new(0.24f, 0.62f, 0.96f, 1f);
    private static readonly Vector4 Success = new(0.34f, 0.84f, 0.56f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.72f, 0.30f, 1f);
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
            MinimumSize = new Vector2(600f, 430f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##SoumenTabs"))
        {
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

            if (ImGui.BeginTabItem("日志"))
            {
                DrawLog();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("关于"))
            {
                DrawAbout();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.SetWindowFontScale(1.24f);
        ImGui.TextUnformatted("Soumen");
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextColored(Muted, "G18 自动跟旗 · Preview");

        var enabled = configuration.Enabled;
        var label = enabled ? "运行中" : "已关闭";
        var color = enabled ? Success : Muted;
        var available = ImGui.GetContentRegionAvail().X;
        var width = ImGui.CalcTextSize(label).X + (26f * ImGuiHelpers.GlobalScale);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + available - width));
        ImGui.TextColored(color, "●  " + label);
        ImGui.Separator();
    }

    private void DrawOverview()
    {
        ImGui.Spacing();
        DrawSectionTitle("自动化");

        var enabled = configuration.Enabled;
        ImGui.PushStyleColor(ImGuiCol.Button, enabled ? new Vector4(0.35f, 0.16f, 0.18f, 1f) : new Vector4(0.12f, 0.38f, 0.62f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, enabled ? new Vector4(0.46f, 0.20f, 0.22f, 1f) : new Vector4(0.18f, 0.48f, 0.76f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, enabled ? new Vector4(0.28f, 0.12f, 0.14f, 1f) : new Vector4(0.10f, 0.31f, 0.52f, 1f));
        if (ImGui.Button(enabled ? "关闭自动跟旗" : "开启自动跟旗", new Vector2(155f, 36f) * ImGuiHelpers.GlobalScale))
        {
            automation.SetEnabled(!enabled);
        }
        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        if (ImGui.Button("紧急停止", new Vector2(110f, 36f) * ImGuiHelpers.GlobalScale))
        {
            automation.Stop();
        }

        ImGui.Spacing();
        ImGui.TextColored(GetStateColor(automation.State), $"● {GetStateName(automation.State)}");
        ImGui.SameLine();
        ImGui.TextWrapped(automation.StatusText);

        ImGui.Spacing();
        DrawSectionTitle("最新坐标");
        var target = automation.LatestTarget;
        if (target == null)
        {
            ImGui.TextColored(Muted, "尚未收到队伍坐标链接");
        }
        else
        {
            ImGui.TextUnformatted($"{target.PlaceName}  X {target.MapX:F1}  Y {target.MapY:F1}");
            ImGui.TextColored(Muted, $"发送者：{target.Sender}    序号：#{target.Serial}");
        }

        ImGui.Spacing();
        DrawSectionTitle("依赖状态");
        DrawDependency("vnavmesh", automation.VnavmeshInstalled, automation.VnavmeshReady);
        ImGui.TextColored(Muted, "坐标识别与地图旗标由 Soumen 原生完成，无需 ChatCoordinates。");

        ImGui.Spacing();
        ImGui.TextColored(Warning, "当前版本不会自动挖掘、战斗、开箱、传送或进入传送门。");
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        DrawSectionTitle("响应范围");

        var livingMemoryOnly = configuration.LivingMemoryOnly;
        if (ImGui.Checkbox("仅处理活着的记忆（Territory 1192）", ref livingMemoryOnly))
        {
            configuration.LivingMemoryOnly = livingMemoryOnly;
            configuration.Save();
        }
        ImGui.TextColored(Muted, "关闭后仍只处理与人物当前地图相同的小队坐标。");

        ImGui.Spacing();
        DrawSectionTitle("移动行为");
        DrawCheckbox("自动上坐骑", nameof(configuration.AutoMount), configuration.AutoMount, value => configuration.AutoMount = value);
        DrawCheckbox("优先飞行导航", nameof(configuration.UseFlight), configuration.UseFlight, value => configuration.UseFlight = value);
        DrawCheckbox("导航期间清除目标", nameof(configuration.ClearTargetWhileNavigating), configuration.ClearTargetWhileNavigating, value => configuration.ClearTargetWhileNavigating = value);
        DrawCheckbox("到达后自动下坐骑", nameof(configuration.AutoDismount), configuration.AutoDismount, value => configuration.AutoDismount = value);
        DrawCheckbox("在聊天栏显示运行日志", nameof(configuration.ShowChatLogs), configuration.ShowChatLogs, value => configuration.ShowChatLogs = value);

        ImGui.Spacing();
        DrawSectionTitle("容错参数");

        var debounce = configuration.DebounceMilliseconds;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("新旗标防抖（毫秒）", ref debounce, 100, 3000))
        {
            configuration.DebounceMilliseconds = debounce;
            configuration.Save();
        }

        var tolerance = configuration.ArrivalTolerance;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("到达判定（世界距离）", ref tolerance, 3f, 30f, "%.1f"))
        {
            configuration.ArrivalTolerance = tolerance;
            configuration.Save();
        }
    }

    private void DrawLog()
    {
        ImGui.Spacing();
        DrawSectionTitle("最近事件");
        if (automation.RecentEvents.Count == 0)
        {
            ImGui.TextColored(Muted, "暂无事件");
            return;
        }

        ImGui.BeginChild("##SoumenLog", Vector2.Zero, true);
        foreach (var line in automation.RecentEvents)
        {
            ImGui.TextWrapped(line);
        }
        ImGui.EndChild();
    }

    private static void DrawAbout()
    {
        ImGui.Spacing();
        DrawSectionTitle("Soumen 0.1");
        ImGui.TextWrapped("面向 FF14 藏宝图队伍流程的独立 Dalamud 插件。第一阶段专注于稳定、可观察、可随时停止的自动跟旗导航。");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "维护者：MusicYYin");
        ImGui.TextColored(Muted, "命令：/soumen · /soumen on · /soumen off · /soumen stop");
        ImGui.Spacing();
        ImGui.TextWrapped("Dalamud 和第三方插件不属于 Square Enix 官方功能。自动化可能违反游戏服务条款，请自行判断并承担风险。");
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private static void DrawDependency(string name, bool installed, bool ready)
    {
        var color = !installed ? new Vector4(0.92f, 0.38f, 0.38f, 1f) : ready ? Success : Warning;
        var status = !installed ? "未加载" : ready ? "已就绪" : "准备中";
        ImGui.TextUnformatted(name);
        ImGui.SameLine(170f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(color, "● " + status);
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
            AutomationState.Error => new Vector4(0.95f, 0.36f, 0.36f, 1f),
            AutomationState.Debouncing or AutomationState.WaitingForPlayer or AutomationState.WaitingForVnavmesh => Warning,
            _ => Accent,
        };

    private static string GetStateName(AutomationState state)
        => state switch
        {
            AutomationState.Disabled => "已关闭",
            AutomationState.Idle => "待命",
            AutomationState.Debouncing => "接收坐标",
            AutomationState.WaitingForPlayer => "等待人物",
            AutomationState.Mounting => "上坐骑",
            AutomationState.WaitingForVnavmesh => "等待导航",
            AutomationState.Navigating => "导航中",
            AutomationState.Dismounting => "下坐骑",
            AutomationState.Error => "异常",
            _ => state.ToString(),
        };
}
