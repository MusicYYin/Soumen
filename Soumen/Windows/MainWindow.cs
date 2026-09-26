using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Soumen.Models;
using Soumen.Services;

namespace Soumen.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Success = new(0.34f, 0.84f, 0.56f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.72f, 0.30f, 1f);
    private static readonly Vector4 Danger = new(0.95f, 0.36f, 0.36f, 1f);

    private readonly Configuration configuration;
    private readonly MapFlagAutomation automation;
    private readonly LeaderTreasureAutomation leaderAutomation;
    private readonly AutoDiscardService autoDiscardService;
    private readonly StatisticsService statisticsService;
    private readonly DiagnosticLogger diagnostics;
    private readonly HuntAutomation huntAutomation;
    private readonly ISharedImmediateTexture treasureIcon;
    private readonly ISharedImmediateTexture huntIcon;
    private readonly ISharedImmediateTexture toolsIcon;
    private readonly ISharedImmediateTexture aboutIcon;
    private MainSection selectedSection = MainSection.Treasure;
    private Vector2 expandedSize = new(800f, 500f);
    private bool collapsed;
    private string discardSearch = string.Empty;
    private int discardSource;
    private string presetNameDraft = string.Empty;
    private string? presetDraftId;
    private bool presetRenameOpen;
    private string includePresetId = string.Empty;
    private bool confirmStatisticsReset;
    private Vector3 lastSpeedPosition;
    private DateTime lastSpeedSampleUtc = DateTime.MinValue;
    private uint lastSpeedTerritory;
    private float currentSpeed;

    private ThemePalette Theme => GetTheme(configuration.UiTheme);
    private Vector4 Accent => Theme.Accent;
    private Vector4 AccentSoft => Theme.AccentSoft;
    private Vector4 Panel => Theme.Panel;
    private Vector4 Muted => Theme.Muted;

    public MainWindow(
        Configuration configuration,
        MapFlagAutomation automation,
        LeaderTreasureAutomation leaderAutomation,
        AutoDiscardService autoDiscardService,
        StatisticsService statisticsService,
        DiagnosticLogger diagnostics,
        HuntAutomation huntAutomation)
        : base("Soumen##SoumenMain")
    {
        this.configuration = configuration;
        this.automation = automation;
        this.leaderAutomation = leaderAutomation;
        this.autoDiscardService = autoDiscardService;
        this.statisticsService = statisticsService;
        this.diagnostics = diagnostics;
        this.huntAutomation = huntAutomation;
        treasureIcon = Plugin.TextureProvider.GetFromManifestResource(
            typeof(MainWindow).Assembly, "Soumen.Assets.treasure-chest.jpg");
        huntIcon = Plugin.TextureProvider.GetFromManifestResource(
            typeof(MainWindow).Assembly, "Soumen.Assets.hunt.jpg");
        toolsIcon = Plugin.TextureProvider.GetFromManifestResource(
            typeof(MainWindow).Assembly, "Soumen.Assets.tools.jpg");
        aboutIcon = Plugin.TextureProvider.GetFromManifestResource(
            typeof(MainWindow).Assembly, "Soumen.Assets.about-question.jpg");

        Flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800f, 500f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Text);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Theme.TableRowBg);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, Theme.TableRowBgAlt);
    }

    public override void PostDraw()
        => ImGui.PopStyleColor(4);

    public override void Draw()
    {
        PushThemeColors();
        DrawHeader();
        if (collapsed)
        {
            ImGui.PopStyleColor(11);
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        if (ImGui.BeginChild("##SoumenSidebar", new Vector2(56f * scale, 0f), false))
        {
            ImGui.Spacing();
            DrawSidebarItem(MainSection.Treasure, treasureIcon, "寻宝", scale);
            ImGui.Spacing();
            DrawSidebarItem(MainSection.Hunt, huntIcon, "狩猎", scale);
            ImGui.Spacing();
            DrawSidebarItem(MainSection.Tools, toolsIcon, "工具", scale);
            ImGui.Spacing();
            DrawSidebarItem(MainSection.About, aboutIcon, "关于", scale);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.BeginChild("##SoumenContent", Vector2.Zero, false))
        {
            if (selectedSection == MainSection.About)
            {
                DrawAbout();
            }
            else if (selectedSection == MainSection.Hunt)
            {
                DrawHunt();
            }
            else if (selectedSection == MainSection.Tools)
            {
                DrawTools();
            }
            else if (ImGui.BeginTabBar("##SoumenTabs"))
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

                if (ImGui.BeginTabItem("自动丢弃"))
                {
                    DrawAutoDiscard();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("统计"))
                {
                    DrawStatistics();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();

        ImGui.PopStyleColor(11);
    }

    private void DrawSidebarItem(MainSection section, ISharedImmediateTexture artwork, string name, float scale)
    {
        var size = new Vector2(38f * scale);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - size.X) / 2f);
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##SoumenNav{section}", size);
        if (ImGui.IsItemClicked())
        {
            selectedSection = section;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(name);
        }

        var rounding = 11f * scale;
        var texture = artwork.GetWrapOrEmpty();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddImageRounded(texture.Handle, start, start + size,
            new Vector2(0.025f), new Vector2(0.975f), 0xFFFFFFFFu, rounding, ImDrawFlags.RoundCornersAll);
        if (selectedSection == section)
        {
            drawList.AddRect(start, start + size,
                ImGui.ColorConvertFloat4ToU32(Accent), rounding, ImDrawFlags.RoundCornersAll, 2f * scale);
        }
    }

    private void DrawHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var position = ImGui.GetCursorScreenPos();
        var dragWidth = Math.Max(100f * scale, ImGui.GetContentRegionAvail().X - 72f * scale);
        ImGui.InvisibleButton("##SoumenDragTitle", new Vector2(dragWidth, 28f * scale));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        ImGui.GetWindowDrawList().AddText(position + new Vector2(3f * scale, 5f * scale),
            ImGui.ColorConvertFloat4ToU32(Accent), "Soumen");
        var enabled = configuration.Enabled;
        var label = !enabled ? "已关闭" : automation.IsPaused ? "已暂停" : "运行中";
        var color = !enabled ? Muted : automation.IsPaused ? Warning : Success;
        var stateText = "●  " + label;
        var width = ImGui.CalcTextSize(stateText).X;
        ImGui.GetWindowDrawList().AddText(position + new Vector2(dragWidth - width - 8f * scale, 5f * scale),
            ImGui.ColorConvertFloat4ToU32(color), stateText);
        ImGui.SameLine();
        if (ImGui.Button(collapsed ? "+##SoumenExpand" : "−##SoumenCollapse", new Vector2(30f * scale, 27f * scale)))
        {
            ToggleCollapsed();
        }
        ImGui.SameLine();
        if (ImGui.Button("×##SoumenClose", new Vector2(30f * scale, 27f * scale)))
        {
            IsOpen = false;
        }
        ImGui.Separator();
    }

    private void ToggleCollapsed()
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (!collapsed)
        {
            expandedSize = ImGui.GetWindowSize();
            collapsed = true;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(360f * scale, 54f * scale),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
            };
            Flags |= ImGuiWindowFlags.NoResize;
            ImGui.SetWindowSize(new Vector2(400f * scale, 54f * scale));
            return;
        }

        collapsed = false;
        Flags &= ~ImGuiWindowFlags.NoResize;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800f, 500f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ImGui.SetWindowSize(expandedSize);
    }

    private void DrawOverview()
    {
        ImGui.Spacing();
        DrawModeSelector();
        ImGui.Spacing();
        DrawControlBar();
        ImGui.Spacing();
        DrawStatusCard();
        ImGui.Spacing();
        DrawDestinationList();
        ImGui.Spacing();
        DrawDependencies();
    }

    private void DrawModeSelector()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing) / 2f;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f * scale);

        DrawModeButton(
            "跟车模式",
            "读取队伍坐标并导航",
            OperatingMode.Follow,
            width,
            scale);
        ImGui.SameLine();
        DrawModeButton(
            "车头模式",
            "使用自己的图并推进流程",
            OperatingMode.Leader,
            width,
            scale);

        ImGui.PopStyleVar();
    }

    private void DrawModeButton(
        string title,
        string subtitle,
        OperatingMode mode,
        float width,
        float scale)
    {
        var selected = !configuration.HuntEnabled && configuration.OperatingMode == mode;
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Theme.Button : Panel);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
            selected ? Theme.ButtonHovered : Theme.PanelHovered);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.Text : Muted);
        if (ImGui.Button($"{title}\n{subtitle}##{mode}", new Vector2(width, 54f * scale)))
        {
            automation.SetOperatingMode(mode);
        }

        ImGui.PopStyleColor(3);
    }

    private void DrawControlBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * scale);

        var enabled = configuration.ActiveTask is AutomationTask.TreasureFollow or AutomationTask.TreasureLeader;
        ImGui.PushStyleColor(ImGuiCol.Button, enabled ? new Vector4(0.34f, 0.14f, 0.17f, 1f) : Theme.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, enabled ? new Vector4(0.48f, 0.19f, 0.22f, 1f) : Theme.ButtonHovered);
        if (ImGui.Button(enabled ? "关闭寻宝" : "开启寻宝", new Vector2(150f, 38f) * scale))
        {
            automation.ActivateTask(enabled ? AutomationTask.None
                : configuration.OperatingMode == OperatingMode.Leader
                    ? AutomationTask.TreasureLeader : AutomationTask.TreasureFollow);
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

        if (configuration.OperatingMode == OperatingMode.Leader)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!leaderAutomation.CanRetryCurrentStep);
            if (ImGui.Button("重试步骤", new Vector2(100f, 38f) * scale))
            {
                leaderAutomation.RetryCurrentStep();
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(!leaderAutomation.CanSkipCurrentStep);
            if (ImGui.Button("跳过步骤", new Vector2(100f, 38f) * scale))
            {
                leaderAutomation.SkipCurrentStep();
            }
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();

        ImGui.PopStyleVar();
    }

    private void DrawStatusCard()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, automation.ActiveTarget == null ? Panel : AccentSoft);
        ImGui.BeginChild("##SoumenStatusCard", new Vector2(0f, 132f * scale), false);

        ImGui.SetCursorPos(new Vector2(16f, 13f) * scale);
        if (configuration.OperatingMode == OperatingMode.Leader)
        {
            ImGui.TextColored(GetLeaderStateColor(leaderAutomation.State),
                $"●  {GetLeaderStateName(leaderAutomation.State)}");
        }
        else
        {
            ImGui.TextColored(GetStateColor(automation.State), $"●  {GetStateName(automation.State)}");
        }
        ImGui.SetCursorPosX(16f * scale);
        ImGui.TextWrapped(configuration.OperatingMode == OperatingMode.Leader
            ? leaderAutomation.StatusText
            : automation.StatusText);

        if (configuration.OperatingMode == OperatingMode.Leader)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(16f * scale);
            ImGui.TextColored(
                Muted,
                $"{leaderAutomation.SelectedMapName}    已解读 {leaderAutomation.DecodedMapCount} · 背包 {leaderAutomation.InventoryMapCount}");
        }

        var target = automation.ActiveTarget;
        if (target != null)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(16f * scale);
            var mode = target.IsOwnTreasure
                ? "自己的藏宝图"
                : automation.IsManualSelection ? "手动选择 · 已锁定" : "最新坐标 · 自动选择";
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
            ImGui.TextColored(Muted, configuration.OperatingMode == OperatingMode.Leader
                ? "等待自己的藏宝图旗标或队伍坐标"
                : "等待小队或跨服小队成员发送坐标链接");
            return;
        }

        ImGui.TextColored(Muted, configuration.OperatingMode == OperatingMode.Leader
            ? "默认前往自己的藏宝图；手动选择队友坐标会切换到跟车模式。"
            : "默认使用最新坐标；手动选择后锁定该目标。");
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
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 170f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var target in targets)
        {
            var active = automation.ActiveTarget?.Serial == target.Serial;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(active ? Accent : target.IsOwnTreasure ? Success : Muted,
                active ? "● 当前" : target.IsOwnTreasure ? "自己的图" : "候选");

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
            ImGui.TextColored(distance == null ? Muted : Theme.Text, distance == null ? "异地图" : $"{distance:F0}y");

            ImGui.TableNextColumn();
            ImGui.PushID(target.Serial.GetHashCode());
            ImGui.BeginDisabled(active && automation.IsManualSelection);
            if (ImGui.Button(active && automation.IsManualSelection ? "已锁定" : "导航至此"))
            {
                automation.NavigateTo(target.Serial);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("删除"))
            {
                automation.RemoveDestination(target.Serial);
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawDependencies()
    {
        DrawSectionTitle("依赖状态");
        if (BeginDependencyTable("##SoumenDependencies"))
        {
            DrawDependencyRow("vnavmesh", automation.VnavmeshInstalled,
                automation.VnavmeshInstalled ? automation.VnavmeshReady ? "已就绪" : "生成网格中" : "未加载");
            DrawLazyLootDependencyRow();
            DrawDependencyRow("Globetrotter / DR", automation.MapLocatorInstalled,
                automation.MapLocatorInstalled ? "已加载" : "未加载");
            DrawDependencyRow("AE Assist", automation.AeAssistInstalled,
                automation.AeAssistInstalled ? "已加载" : "未加载（可选）");
            DrawDependencyRow("BossMod Reborn", automation.BossModRebornInstalled,
                automation.BossModRebornInstalled ? "已加载" : "未加载（可选）");
            ImGui.EndTable();
        }
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("车头模式", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextColored(Muted, "支持 G8–G18、特殊图、绿图与深层绿图；Globetrotter 或 Daily Routines 标记坐标，LazyLoot 处理掷点。");
            ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo("藏宝图##LeaderTreasureMap", leaderAutomation.SelectedMapLabel))
            {
                foreach (var profile in TreasureMapCatalog.Profiles
                             .OrderBy(profile => profile.CatalogOrder)
                             .ThenByDescending(profile => profile.Grade))
                {
                    var selected = profile.ItemId == leaderAutomation.SelectedMap.ItemId;
                    var name = GetItemName(profile.ItemId, $"{profile.GradeLabel} 藏宝图");
                    var kind = profile.DirectPortal
                        ? "直达隐秘水道"
                        : profile.HasTreasureDungeon ? "宝物库" : "野外";
                    if (ImGui.Selectable($"{profile.GradeLabel} · {name}  ·  {kind}##leader-map-{profile.ItemId}", selected))
                    {
                        leaderAutomation.SelectMap(profile.ItemId);
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(!leaderAutomation.SelectedMap.CanMarketRestock);
            DrawCheckbox(
                $"无图时前往海都市场板自动补满两张 {leaderAutomation.SelectedMap.GradeLabel}",
                nameof(configuration.AutoRestockLeaderMaps),
                configuration.AutoRestockLeaderMaps,
                value => configuration.AutoRestockLeaderMaps = value);
            ImGui.EndDisabled();

            ImGui.BeginDisabled(!leaderAutomation.SelectedMap.CanMarketRestock);
            var maximumUnitPrice = (int)configuration.LeaderMapMaximumUnitPrice;
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("单张最高价格（Gil）", ref maximumUnitPrice, 1_000, 10_000))
            {
                configuration.LeaderMapMaximumUnitPrice = (uint)Math.Clamp(maximumUnitPrice, 1_000, 9_999_999);
                configuration.Save();
            }

            ImGui.EndDisabled();

            ImGui.TextColored(Muted, leaderAutomation.SelectedMap.CanMarketRestock
                ? "第一张自动解读，第二张留在背包；只购买单张上架。"
                : "该藏宝图不可交易；会按堆叠数量逐张解读和使用。" );
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("移动与路线", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCheckbox("自动上坐骑", nameof(configuration.AutoMount), configuration.AutoMount, value => configuration.AutoMount = value);
            DrawCheckbox("优先飞行导航", nameof(configuration.UseFlight), configuration.UseFlight, value => configuration.UseFlight = value);
            DrawCheckbox("到达后自动落地并下坐骑", nameof(configuration.AutoDismount), configuration.AutoDismount, value => configuration.AutoDismount = value);
            DrawCheckbox("重新寻路后仍卡住时传送", nameof(configuration.TeleportWhenStuck), configuration.TeleportWhenStuck, value => configuration.TeleportWhenStuck = value);

            var stuckSeconds = configuration.StuckSeconds;
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("卡住判定时间（秒）", ref stuckSeconds, 1f, 20f, "%.1f"))
            {
                configuration.StuckSeconds = stuckSeconds;
                configuration.Save();
            }

            var tolerance = configuration.ArrivalTolerance;
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("到达判定（世界距离）", ref tolerance, 0f, 30f, "%.1f"))
            {
                configuration.ArrivalTolerance = tolerance;
                configuration.Save();
            }

            var correctionRange = configuration.TreasureSpotCorrectionRange;
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("藏宝点纠偏范围（y）", ref correctionRange, 0f, 150f, "%.0f"))
            {
                configuration.TreasureSpotCorrectionRange = MathF.Round(correctionRange);
                configuration.Save();
            }
            ImGui.TextColored(Muted, "旗标距真实挖掘点不超过此范围时才纠偏；0 表示关闭纠偏。");
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("传送方式（二选一）", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.RadioButton("自行传送至更近的以太水晶", configuration.AutoTeleport))
            {
                configuration.AutoTeleport = true;
                configuration.AcceptPartyTeleportRequests = false;
                configuration.Save();
            }
            ImGui.TextColored(Muted, "自行传送时会拒绝队友发起的传送邀请。" );

            if (ImGui.RadioButton("接受队友传送邀请", configuration.AcceptPartyTeleportRequests))
            {
                configuration.AutoTeleport = false;
                configuration.AcceptPartyTeleportRequests = true;
                configuration.Save();
            }
            ImGui.TextColored(Muted, "正常导航中接受同地图普通小队的传送；其余情况自行传送。车头始终自行传送。");
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("外部插件", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCheckbox("管理 AE Assist 自动选目标", nameof(configuration.EnableAeAssistIntegration), configuration.EnableAeAssistIntegration,
                value => configuration.EnableAeAssistIntegration = value);
            DrawCheckbox("运行期间启用 BossMod Reborn AI", nameof(configuration.EnableBossModRebornIntegration), configuration.EnableBossModRebornIntegration,
                value => configuration.EnableBossModRebornIntegration = value);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("宝物库", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var followDistance = configuration.DungeonFollowDistance;
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("跟随队长距离（y）", ref followDistance, 1.5f, 12f, "%.1f"))
            {
                configuration.DungeonFollowDistance = followDistance;
                configuration.Save();
            }
            DrawCheckbox("宝物库内非战斗时自动跟随队长", nameof(configuration.DungeonAutoFollow),
                configuration.DungeonAutoFollow, value => configuration.DungeonAutoFollow = value);
            ImGui.TextColored(Muted, "需要已加载并启用 BossMod Reborn AI；未加载时不会跟随。");
            DrawCheckbox("宝物库结束且无待掷点物品时自动离开", nameof(configuration.AutoLeaveTreasureDungeon), configuration.AutoLeaveTreasureDungeon,
                value => configuration.AutoLeaveTreasureDungeon = value);
            DrawCheckbox("自动收集金袋和银袋", nameof(configuration.AutoCollectTreasureSacks), configuration.AutoCollectTreasureSacks,
                value => configuration.AutoCollectTreasureSacks = value);
        }

    }

    private void DrawTools()
    {
        ImGui.Spacing();
        DrawSectionTitle("工具");
        if (ImGui.CollapsingHeader("战场透视", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCheckbox("启用战场透视", nameof(configuration.FrontlineRadarEnabled),
                configuration.FrontlineRadarEnabled, value => configuration.FrontlineRadarEnabled = value);
            var range = configuration.FrontlineRadarRange;
            ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("探测距离", ref range, 20f, 200f, "%.0f y"))
            {
                configuration.FrontlineRadarRange = range;
                configuration.Save();
            }
            DrawCheckbox("显示敌方连线", nameof(configuration.FrontlineRadarLines),
                configuration.FrontlineRadarLines, value => configuration.FrontlineRadarLines = value);
            ImGui.TextColored(Muted, "在 PvP 区域显示已加载的敌方玩家。首次进入战场请核对敌我识别。");
        }
    }

    private void DrawAbout()
    {
        ImGui.Spacing();
        DrawSectionTitle($"Soumen {typeof(MainWindow).Assembly.GetName().Version?.ToString(4) ?? "开发版"}");
        ImGui.TextWrapped("自动化工具：寻宝、狩猎与工具。");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "维护者：MusicYYin");
        ImGui.TextColored(Muted, "命令：/soumen（打开面板）");
        ImGui.Spacing();
        var player = Plugin.ObjectTable.LocalPlayer;
        var territory = Plugin.ClientState.TerritoryType;
        UpdateSpeed(player?.Position, territory);
        ImGui.TextUnformatted($"当前地图编号：{territory}");
        ImGui.TextUnformatted(player == null ? "自身移动速度：未进入游戏" : $"自身移动速度：{currentSpeed:F2} y/s");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("界面", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(260f * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo("主题颜色##SoumenUiTheme", GetThemeName(configuration.UiTheme)))
            {
                foreach (var theme in Enum.GetValues<UiTheme>())
                {
                    var selected = configuration.UiTheme == theme;
                    ImGui.PushStyleColor(ImGuiCol.Text, GetTheme(theme).Accent);
                    if (ImGui.Selectable(GetThemeName(theme), selected))
                    {
                        configuration.UiTheme = theme;
                        configuration.Save();
                    }

                    ImGui.PopStyleColor();
                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("开发者模式"))
        {
            DrawCheckbox("诊断模式", nameof(configuration.DiagnosticMode), configuration.DiagnosticMode,
                value =>
                {
                    configuration.DiagnosticMode = value;
                    if (value)
                        diagnostics.Write("诊断", "诊断模式已开启，后续内容将追加写入此文件。");
                });
            ImGui.TextColored(Muted, $"日志目录：{diagnostics.DirectoryPath}");
            ImGui.TextColored(Muted, "文件名：diagnostic.log（关闭诊断模式时不会写入）");
            ImGui.Separator();
            ImGui.TextColored(Muted, "解读地图测试使用寻宝设置中选定的地图。");
            ImGui.BeginDisabled(leaderAutomation.IsDeveloperTestRunning);
            if (ImGui.Button("测试解读地图"))
                leaderAutomation.TestDecipher();
            ImGui.EndDisabled();
            if (leaderAutomation.IsDeveloperTestRunning)
            {
                ImGui.SameLine();
                if (ImGui.Button("取消测试"))
                    leaderAutomation.CancelDeveloperTest();
            }
            ImGui.TextWrapped(leaderAutomation.DeveloperTestResult);
        }
    }

    private void UpdateSpeed(Vector3? position, uint territory)
    {
        if (position == null)
        {
            lastSpeedSampleUtc = DateTime.MinValue;
            currentSpeed = 0f;
            return;
        }

        var now = DateTime.UtcNow;
        if (lastSpeedSampleUtc == DateTime.MinValue || lastSpeedTerritory != territory)
        {
            lastSpeedPosition = position.Value;
            lastSpeedSampleUtc = now;
            lastSpeedTerritory = territory;
            currentSpeed = 0f;
            return;
        }

        var elapsed = (float)(now - lastSpeedSampleUtc).TotalSeconds;
        if (elapsed < 0.35f) return;
        var measured = Vector3.Distance(position.Value, lastSpeedPosition) / elapsed;
        currentSpeed = measured > 35f ? 0f : measured;
        lastSpeedPosition = position.Value;
        lastSpeedSampleUtc = now;
    }

    private void DrawHunt()
    {
        if (ImGui.BeginTabBar("##SoumenHuntTabs"))
        {
            if (ImGui.BeginTabItem("运行"))
            {
                DrawHuntRun();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("设置"))
            {
                DrawHuntSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawHuntRun()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var active = configuration.ActiveTask == AutomationTask.HuntTrain;
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * scale);
        ImGui.PushStyleColor(ImGuiCol.Button, active ? new Vector4(0.34f, 0.14f, 0.17f, 1f) : Theme.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? new Vector4(0.48f, 0.19f, 0.22f, 1f) : Theme.ButtonHovered);
        if (ImGui.Button(active ? "关闭狩猎" : "开启狩猎", new Vector2(150f, 38f) * scale))
        {
            if (active) automation.ActivateTask(AutomationTask.None);
            else automation.ActivateTask(AutomationTask.HuntTrain);
        }
        ImGui.PopStyleColor(2);
        ImGui.SameLine();
        ImGui.BeginDisabled(!active);
        ImGui.PushStyleColor(ImGuiCol.Button, automation.IsPaused ? new Vector4(0.12f, 0.42f, 0.30f, 1f) : new Vector4(0.38f, 0.30f, 0.10f, 1f));
        if (ImGui.Button(automation.IsPaused ? "继续" : "暂停", new Vector2(104f, 38f) * scale))
            automation.SetPaused(!automation.IsPaused);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("停止导航", new Vector2(112f, 38f) * scale))
        {
            automation.Stop("狩猎导航已停止");
        }
        ImGui.EndDisabled();
        ImGui.PopStyleVar();

        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, active ? AccentSoft : Panel);
        ImGui.BeginChild("##HuntStatusCard", new Vector2(0f, 132f * scale), false);
        ImGui.SetCursorPos(new Vector2(16f, 13f) * scale);
        ImGui.TextColored(!active ? Muted : automation.IsPaused ? Warning : Success,
            !active ? "●  未开启" : automation.IsPaused ? "●  已暂停" : "●  运行中");
        ImGui.SetCursorPosX(16f * scale);
        ImGui.TextWrapped(!active ? "等待开启狩猎"
            : automation.ActiveTarget?.IsHunt == true ? automation.StatusText : "等待选中车头发布地图坐标");
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.Spacing();

        DrawHuntLeaders();
        ImGui.Spacing();
        DrawSectionTitle("依赖状态");
        if (BeginDependencyTable("##HuntDependencies"))
        {
            DrawDependencyRow("vnavmesh", automation.VnavmeshInstalled,
                automation.VnavmeshInstalled ? automation.VnavmeshReady ? "已就绪" : "生成网格中" : "未加载");
            DrawDependencyRow("AE Assist", automation.AeAssistInstalled,
                automation.AeAssistInstalled ? "已加载" : "未加载（可选）");
            DrawDependencyRow("Lifestream", automation.LifestreamInstalled,
                automation.LifestreamInstalled ? "已加载" : "未加载（换线和跨服需要）");
            ImGui.EndTable();
        }
    }

    private void DrawHuntLeaders()
    {
        var scale = ImGuiHelpers.GlobalScale;
        DrawSectionTitle("车头");
        if (ImGui.Button("添加当前目标为车头") && !huntAutomation.AddCurrentTarget())
            ImGui.OpenPopup("##HuntTargetMissing");
        ImGui.SameLine();
        ImGui.BeginDisabled(huntAutomation.SelectedLeader == null);
        if (ImGui.Button("删除车头")) huntAutomation.ClearSession();
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("##HuntTargetMissing"))
        {
            ImGui.TextUnformatted("请先在游戏中选中车头玩家。");
            ImGui.EndPopup();
        }
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, huntAutomation.SelectedLeader == null ? Panel : AccentSoft);
        ImGui.BeginChild("##SelectedHuntLeader", new Vector2(0f, 75f * scale), false);
        ImGui.SetCursorPos(new Vector2(16f, 13f) * scale);
        ImGui.TextColored(huntAutomation.SelectedLeader == null ? Muted : Accent,
            huntAutomation.SelectedLeader == null ? "●  未选车头" : "●  当前车头");
        ImGui.SetCursorPosX(16f * scale);
        ImGui.TextUnformatted(huntAutomation.SelectedLeader ?? "先在游戏中选中玩家，再点击上方按钮");
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawHuntSettings()
    {
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("车头跟车", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCheckbox("自动比较直达与以太水晶路线并传送", nameof(configuration.HuntAutoTeleport), configuration.HuntAutoTeleport,
                value => configuration.HuntAutoTeleport = value);
            DrawCheckbox("自动打开车头地图链接", nameof(configuration.HuntAutoOpenMap), configuration.HuntAutoOpenMap,
                value => configuration.HuntAutoOpenMap = value);
            DrawCheckbox("高亮车头消息", nameof(configuration.HuntHighlightLeader), configuration.HuntHighlightLeader,
                value => configuration.HuntHighlightLeader = value);
            DrawCheckbox("暂时隐藏其他人的喊话", nameof(configuration.HuntMuteOtherShouts), configuration.HuntMuteOtherShouts,
                value => configuration.HuntMuteOtherShouts = value);
            DrawCheckbox("收到新坐标时在聊天栏提醒", nameof(configuration.HuntChatNotification), configuration.HuntChatNotification,
                value => configuration.HuntChatNotification = value);
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("换线", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCheckbox("多线地图自动核对并换线", nameof(configuration.HuntAutoInstance), configuration.HuntAutoInstance,
                value => configuration.HuntAutoInstance = value);
            ImGui.TextColored(Muted, "从车头发出的地图链接读取线路数字；有多条线且当前线路不符时使用 Lifestream 换线。");
        }
    }

    private void DrawAutoDiscard()
    {
        ImGui.Spacing();
        DrawSectionTitle("自动丢弃");

        var enabled = configuration.AutoDiscardEnabled;
        if (ImGui.Checkbox("启用自动丢弃", ref enabled))
        {
            configuration.AutoDiscardEnabled = enabled;
            configuration.Save();
        }

        ImGui.TextColored(
            Muted,
            "只处理本轮挖宝后出现在原空格中的新增整堆；合并进旧堆或数量不一致时会跳过。" );
        ImGui.Spacing();

        var sessionColor = autoDiscardService.SessionActive ? Success : Muted;
        ImGui.TextColored(sessionColor, autoDiscardService.SessionActive ? "● 正在记录" : "● 尚未开始本轮记录");
        ImGui.SameLine();
        ImGui.TextColored(Muted, autoDiscardService.StatusText);
        ImGui.Spacing();

        if (!ImGui.BeginTable("##SoumenDiscardColumns", 2, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("可选物品", ImGuiTableColumnFlags.WidthStretch, 1.25f);
        ImGui.TableSetupColumn("丢弃预设", ImGuiTableColumnFlags.WidthStretch, 1.05f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawDiscardSourcePanel();
        ImGui.TableNextColumn();
        DrawDiscardPresetPanel();
        ImGui.EndTable();
    }

    private void DrawDiscardSourcePanel()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        ImGui.BeginChild("##SoumenDiscardSource", new Vector2(0f, 420f * scale), true);

        ImGui.TextColored(Accent, "可选物品");
        ImGui.SameLine();
        ImGui.TextColored(Muted, "右键加入");

        var sourceNames = new[] { "最近获得", "搜索全部" };
        discardSource = Math.Clamp(discardSource, 0, sourceNames.Length - 1);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoumenDiscardSourcePicker", sourceNames[discardSource]))
        {
            for (var index = 0; index < sourceNames.Length; index++)
            {
                if (ImGui.Selectable(sourceNames[index], discardSource == index))
                {
                    discardSource = index;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##SoumenDiscardSearch", "搜索名称或物品 ID", ref discardSearch, 96);
        ImGui.Separator();

        var items = GetDiscardSourceItems();
        if (items.Count == 0)
        {
            var emptyText = discardSource switch
            {
                0 => "挖宝期间最近获得的 20 种物品会显示在这里。",
                1 when discardSearch.Trim().Length < 2 => "输入至少两个字开始搜索。",
                _ => "没有匹配的物品。",
            };
            ImGui.TextColored(Muted, emptyText);
        }
        else
        {
            foreach (var item in items)
            {
                DrawDiscardItemRow(item, source: true);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawDiscardPresetPanel()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        ImGui.BeginChild("##SoumenDiscardPreset", new Vector2(0f, 420f * scale), true);

        ImGui.TextColored(Accent, "丢弃预设");
        var activePreset = configuration.ActiveDiscardPreset;
        EnsurePresetDraft(activePreset);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoumenDiscardPresetPicker", activePreset.Name))
        {
            foreach (var preset in configuration.AutoDiscardPresets)
            {
                var selected = preset.Id == activePreset.Id;
                if (ImGui.Selectable($"{preset.Name}##preset-{preset.Id}", selected))
                {
                    configuration.ActiveAutoDiscardPresetId = preset.Id;
                    presetDraftId = null;
                    presetRenameOpen = false;
                    includePresetId = string.Empty;
                    configuration.Save();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.SmallButton("改名"))
        {
            presetNameDraft = activePreset.Name;
            presetRenameOpen = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("新建"))
        {
            var preset = configuration.CreateDiscardPreset();
            presetDraftId = preset.Id;
            presetNameDraft = preset.Name;
            presetRenameOpen = true;
            includePresetId = string.Empty;
            configuration.Save();
            activePreset = preset;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(configuration.AutoDiscardPresets.Count <= 1);
        if (ImGui.SmallButton("删除") && configuration.DeleteDiscardPreset(activePreset.Id))
        {
            presetDraftId = null;
            presetRenameOpen = false;
            includePresetId = string.Empty;
            configuration.Save();
            activePreset = configuration.ActiveDiscardPreset;
        }
        ImGui.EndDisabled();

        if (presetRenameOpen)
        {
            ImGui.Spacing();
            ImGui.SetNextItemWidth(Math.Max(120f * scale, ImGui.GetContentRegionAvail().X - 112f * scale));
            ImGui.InputTextWithHint("##SoumenPresetName", "输入预设名称", ref presetNameDraft, 48);
            ImGui.SameLine();
            if (ImGui.SmallButton("确定"))
            {
                var name = presetNameDraft.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    activePreset.Name = name;
                    presetNameDraft = name;
                    presetRenameOpen = false;
                    configuration.Save();
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("取消"))
            {
                presetNameDraft = activePreset.Name;
                presetRenameOpen = false;
            }
        }

        var availableIncludes = configuration.AutoDiscardPresets
            .Where(preset => preset.Id != activePreset.Id
                && !activePreset.IncludedPresetIds.Contains(preset.Id)
                && configuration.CanIncludeDiscardPreset(activePreset.Id, preset.Id))
            .ToList();
        if (availableIncludes.All(preset => preset.Id != includePresetId))
        {
            includePresetId = availableIncludes.FirstOrDefault()?.Id ?? string.Empty;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(availableIncludes.Count == 0);
        var includeName = availableIncludes.FirstOrDefault(preset => preset.Id == includePresetId)?.Name
            ?? "没有可加入的预设";
        ImGui.SetNextItemWidth(Math.Max(120f * scale, ImGui.GetContentRegionAvail().X - 104f * scale));
        if (ImGui.BeginCombo("##SoumenIncludePreset", includeName))
        {
            foreach (var preset in availableIncludes)
            {
                if (ImGui.Selectable($"{preset.Name}##include-{preset.Id}", preset.Id == includePresetId))
                {
                    includePresetId = preset.Id;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("加入预设") && !string.IsNullOrWhiteSpace(includePresetId))
        {
            activePreset.IncludedPresetIds.Add(includePresetId);
            includePresetId = string.Empty;
            configuration.Save();
        }
        ImGui.EndDisabled();

        foreach (var includedId in activePreset.IncludedPresetIds.ToList())
        {
            var included = configuration.AutoDiscardPresets.FirstOrDefault(preset => preset.Id == includedId);
            if (included == null)
            {
                continue;
            }

            ImGui.PushID($"included-{included.Id}");
            ImGui.TextColored(Muted, $"组合：{included.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("移除"))
            {
                activePreset.IncludedPresetIds.Remove(included.Id);
                configuration.Save();
            }
            ImGui.PopID();
        }

        ImGui.Separator();

        var items = autoDiscardService.SelectedItems;
        if (items.Count == 0)
        {
            ImGui.TextColored(Muted, "当前预设为空。左侧右键加入物品，或组合另一个预设。");
        }
        else
        {
            ImGui.TextColored(Muted, $"生效物品 {items.Count} 种 · 右键移除当前预设中的物品");
            foreach (var item in items)
            {
                DrawDiscardItemRow(item, source: false);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private IReadOnlyList<DiscardCatalogItem> GetDiscardSourceItems()
    {
        IReadOnlyList<DiscardCatalogItem> items = discardSource switch
        {
            0 => autoDiscardService.ObservedItems,
            _ => autoDiscardService.Search(discardSearch),
        };

        var query = discardSearch.Trim();
        if (discardSource == 1 || string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        return items
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ItemId.ToString().Contains(query, StringComparison.Ordinal))
            .ToList();
    }

    private void DrawDiscardItemRow(DiscardCatalogItem item, bool source)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushID($"{(source ? "source" : "selected")}-{item.ItemId}");

        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrEmpty();
        ImGui.Image(texture.Handle, new Vector2(24f, 24f) * scale);
        ImGui.SameLine();

        var activePreset = configuration.ActiveDiscardPreset;
        var selected = configuration.ResolveActiveDiscardItemIds().Contains(item.ItemId);
        var direct = activePreset.ItemIds.Contains(item.ItemId);
        var pending = autoDiscardService.PendingQuantity(item.ItemId);
        var inherited = !source && !direct ? "  ·  来自组合预设" : string.Empty;
        var label = pending > 0 ? $"{item.Name}  ·  本轮 +{pending}{inherited}" : item.Name + inherited;
        ImGui.Selectable($"{label}##row", selected && source, ImGuiSelectableFlags.None, new Vector2(0f, 24f * scale));

        if (source && ImGui.BeginPopupContextItem("##add"))
        {
            ImGui.BeginDisabled(selected);
            if (ImGui.MenuItem(selected ? "当前预设已包含" : "加入当前预设"))
            {
                activePreset.ItemIds.Add(item.ItemId);
                configuration.Save();
            }

            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        else if (!source && ImGui.BeginPopupContextItem("##remove"))
        {
            ImGui.BeginDisabled(!direct);
            if (ImGui.MenuItem(direct ? "从当前预设移除" : "由组合预设提供"))
            {
                activePreset.ItemIds.Remove(item.ItemId);
                configuration.Save();
            }
            ImGui.EndDisabled();

            ImGui.EndPopup();
        }

        if (ImGui.IsItemHovered())
        {
            var action = source ? "右键加入当前预设" : direct ? "右键从当前预设移除" : "来自组合预设";
            ImGui.SetTooltip($"{item.Name}\n物品 ID：{item.ItemId}\n{action}");
        }

        ImGui.PopID();
    }

    private void EnsurePresetDraft(DiscardPreset preset)
    {
        if (presetDraftId == preset.Id)
        {
            return;
        }

        presetDraftId = preset.Id;
        presetNameDraft = preset.Name;
    }

    private void DrawStatistics()
    {
        ImGui.Spacing();
        DrawSectionTitle("统计");
        ImGui.TextColored(Muted, "统计本次插件加载期间、Soumen 开启时的数据；重新加载插件后自动清零。");
        ImGui.Spacing();

        if (ImGui.BeginTable("##SoumenStatistics", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawStatisticCard("获得金币", statisticsService.GilEarned.ToString("N0"), "Gil", Accent);
            ImGui.TableNextColumn();
            DrawStatisticCard("进入宝物库", statisticsService.TreasureDungeonEntries.ToString("N0"), "次", Success);
            ImGui.TableNextColumn();
            DrawStatisticCard("下底", statisticsService.TreasureDungeonCompletions.ToString("N0"), "次", Warning);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (!confirmStatisticsReset)
        {
            if (ImGui.Button("重置统计"))
            {
                confirmStatisticsReset = true;
            }
        }
        else
        {
            ImGui.TextColored(Warning, "确定清空全部统计数据？");
            if (ImGui.Button("确认重置"))
            {
                statisticsService.Reset();
                confirmStatisticsReset = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                confirmStatisticsReset = false;
            }
        }
    }

    private void DrawStatisticCard(string title, string value, string unit, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f * scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        ImGui.BeginChild($"##stat-{title}", new Vector2(0f, 112f * scale), true);
        ImGui.SetCursorPos(new Vector2(14f, 13f) * scale);
        ImGui.TextColored(Muted, title);
        ImGui.SetCursorPosX(14f * scale);
        var valueTop = ImGui.GetCursorPosY();
        ImGui.SetWindowFontScale(1.55f);
        var valueHeight = ImGui.GetTextLineHeight();
        ImGui.TextColored(color, value);
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine(0f, 6f * scale);
        ImGui.SetCursorPosY(valueTop + valueHeight - ImGui.GetTextLineHeight());
        ImGui.TextColored(Muted, unit);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private void DrawDependencyRow(string name, bool installed, string status)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        ImGui.TextColored(installed ? Success : Muted, $"● {status}");
    }

    private static bool BeginDependencyTable(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp)) return false;
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 180f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    private void DrawLazyLootDependencyRow()
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted("LazyLoot");
        ImGui.TableNextColumn();
        ImGui.TextColored(automation.LazyLootInstalled ? Success : Muted,
            automation.LazyLootInstalled ? "● 已加载" : "● 未加载");
        ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);

        DrawLazyLootModeButton(LazyLootRollMode.Need, "需");
        ImGui.SameLine(0f, 3f * ImGuiHelpers.GlobalScale);
        DrawLazyLootModeButton(LazyLootRollMode.Greed, "贪");
        ImGui.SameLine(0f, 3f * ImGuiHelpers.GlobalScale);
        DrawLazyLootModeButton(LazyLootRollMode.Pass, "弃");
    }

    private void DrawLazyLootModeButton(LazyLootRollMode mode, string label)
    {
        var selected = configuration.LazyLootRollMode == mode;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 2f) * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Theme.Button : Panel);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.Text : Muted);
        if (ImGui.SmallButton($"{label}##LazyLoot{mode}"))
        {
            automation.SetLazyLootRollMode(mode);
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(automation.LazyLootInstalled
                ? $"LazyLoot 自动{label}"
                : $"保存自动{label}设置；LazyLoot 加载后生效");
        }
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

    private static string GetItemName(uint itemId, string fallback)
    {
        try
        {
            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch
        {
            return fallback;
        }
    }

    private Vector4 GetStateColor(AutomationState state)
        => state switch
        {
            AutomationState.Idle => Success,
            AutomationState.Disabled => Muted,
            AutomationState.Paused => Warning,
            AutomationState.Error => Danger,
            AutomationState.WaitingForPlayer or AutomationState.WaitingForVnavmesh or AutomationState.PlanningRoute => Warning,
            _ => Accent,
        };

    private Vector4 GetLeaderStateColor(LeaderAutomationState state)
        => state switch
        {
            LeaderAutomationState.Inactive => Muted,
            LeaderAutomationState.Waiting or LeaderAutomationState.WaitingForParty => Warning,
            LeaderAutomationState.Error => Danger,
            LeaderAutomationState.Combat => Warning,
            _ => Accent,
        };

    private static string GetLeaderStateName(LeaderAutomationState state)
        => state switch
        {
            LeaderAutomationState.Inactive => "车头未运行",
            LeaderAutomationState.LookingForMap => "检查藏宝图",
            LeaderAutomationState.RestockingTravel => "前往市场板",
            LeaderAutomationState.RestockingMarket => "购买藏宝图",
            LeaderAutomationState.DecipheringMap or LeaderAutomationState.ConfirmingDecipher => "解读藏宝图",
            LeaderAutomationState.OpeningDecodedMap or LeaderAutomationState.WaitingForFlag => "读取坐标",
            LeaderAutomationState.Navigating => "前往藏宝图",
            LeaderAutomationState.WaitingForParty => "等待队友",
            LeaderAutomationState.Digging => "挖掘中",
            LeaderAutomationState.ApproachingChest or LeaderAutomationState.ReopeningChest => "开启宝箱",
            LeaderAutomationState.WaitingForCombat => "等待敌人",
            LeaderAutomationState.Combat => "战斗中",
            LeaderAutomationState.WaitingForLootOrPortal => "等待结算",
            LeaderAutomationState.EnteringPortal => "进入宝物库",
            LeaderAutomationState.Dungeon => "宝物库中",
            LeaderAutomationState.Waiting => "等待处理",
            LeaderAutomationState.Error => "需要处理",
            _ => state.ToString(),
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

    private static string GetThemeName(UiTheme theme)
        => theme switch
        {
            UiTheme.Ocean => "默认蓝",
            UiTheme.Dark => "深色",
            UiTheme.Light => "浅色",
            _ => "默认蓝",
        };

    private static ThemePalette GetTheme(UiTheme theme)
        => theme switch
        {
            UiTheme.Dark => new(
                new Vector4(0.62f, 0.70f, 0.82f, 1f),
                new Vector4(0.10f, 0.12f, 0.16f, 0.98f),
                new Vector4(0.04f, 0.045f, 0.055f, 0.98f),
                new Vector4(0.09f, 0.10f, 0.12f, 1f),
                new Vector4(0.15f, 0.17f, 0.22f, 1f),
                new Vector4(0.23f, 0.27f, 0.34f, 1f),
                new Vector4(0.55f, 0.59f, 0.66f, 1f),
                new Vector4(0.025f, 0.03f, 0.04f, 0.98f),
                new Vector4(0.91f, 0.93f, 0.96f, 1f),
                new Vector4(0.05f, 0.06f, 0.08f, 0.42f),
                new Vector4(0.08f, 0.09f, 0.12f, 0.46f)),
            UiTheme.Light => new(
                new Vector4(0.58f, 0.36f, 0.13f, 1f),
                new Vector4(0.86f, 0.78f, 0.61f, 1f),
                new Vector4(0.91f, 0.87f, 0.78f, 1f),
                new Vector4(0.84f, 0.78f, 0.66f, 1f),
                new Vector4(0.73f, 0.62f, 0.43f, 1f),
                new Vector4(0.81f, 0.69f, 0.47f, 1f),
                new Vector4(0.38f, 0.34f, 0.28f, 1f),
                new Vector4(0.96f, 0.94f, 0.89f, 1f),
                new Vector4(0.15f, 0.12f, 0.09f, 1f),
                new Vector4(0.87f, 0.82f, 0.72f, 0.45f),
                new Vector4(0.81f, 0.74f, 0.62f, 0.48f)),
            _ => new(
                new Vector4(0.31f, 0.67f, 0.94f, 1f),
                new Vector4(0.10f, 0.20f, 0.29f, 0.96f),
                new Vector4(0.075f, 0.085f, 0.105f, 0.96f),
                new Vector4(0.12f, 0.14f, 0.18f, 1f),
                new Vector4(0.11f, 0.40f, 0.66f, 1f),
                new Vector4(0.16f, 0.50f, 0.79f, 1f),
                new Vector4(0.62f, 0.66f, 0.72f, 1f),
                new Vector4(0.055f, 0.065f, 0.085f, 0.98f),
                new Vector4(0.93f, 0.95f, 0.98f, 1f),
                new Vector4(0.08f, 0.10f, 0.13f, 0.42f),
                new Vector4(0.11f, 0.14f, 0.18f, 0.46f)),
        };

    private void PushThemeColors()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Panel);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Theme.PanelHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Panel);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.42f));
    }

    private readonly record struct ThemePalette(
        Vector4 Accent,
        Vector4 AccentSoft,
        Vector4 Panel,
        Vector4 PanelHovered,
        Vector4 Button,
        Vector4 ButtonHovered,
        Vector4 Muted,
        Vector4 WindowBg,
        Vector4 Text,
        Vector4 TableRowBg,
        Vector4 TableRowBgAlt);

    private enum MainSection
    {
        Treasure,
        Hunt,
        Tools,
        About,
    }
}
