using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Soumen.Models;

namespace Soumen.Services;

/// <summary>Tracks S/SS reports printed by Sonar and visits them in priority order.</summary>
public sealed class SonarHuntAutomation : IDisposable
{
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(25);
    private readonly Configuration configuration;
    private readonly MapFlagAutomation navigator;
    private readonly DiagnosticLogger diagnostics;
    private readonly LifestreamIpc lifestream;
    private readonly TeleportService teleporter;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Dictionary<string, Report> reports = [];
    private string? currentKey;
    private DateTime nextUpdateUtc;
    private DateTime lastWorldRequestUtc;
    private DateTime lastCityTeleportUtc;
    private DateTime lastMobMoveUtc;
    private DateTime lastMobSeenUtc;
    private bool seenPulledMob;
    private bool ownsMobNavigation;

    public SonarHuntAutomation(Configuration configuration, MapFlagAutomation navigator, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.navigator = navigator;
        this.diagnostics = diagnostics;
        lifestream = new LifestreamIpc(diagnostics);
        teleporter = new TeleportService(diagnostics);
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface, diagnostics);
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public bool IsInstalled => Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && plugin.InternalName.Equals("SonarPlugin", StringComparison.OrdinalIgnoreCase));
    public string StatusText { get; private set; } = "等待 Sonar S 怪报告";
    public int ReportCount => reports.Count;
    public IEnumerable<string> PendingReports => reports.Values
        .OrderBy(r => r.RankPriority).ThenByDescending(LocalCrowd)
        .ThenBy(r => r.ReportedUtc)
        .Select(r => $"{(r.RankPriority == 0 ? "SS" : "S")} · {r.Name} · {r.WorldName}"
            + (r.Instance > 0 ? $" {r.Instance}线" : string.Empty)
            + (LocalCrowd(r) > 0 ? $" · 附近 {LocalCrowd(r)} 人" : string.Empty));

    public void Reset()
    {
        StopMobNavigation();
        reports.Clear();
        currentKey = null;
        StatusText = "等待 Sonar S 怪报告";
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (configuration.ActiveTask != AutomationTask.HuntSonar || !IsInstalled
            || !string.Equals(message.Sender.TextValue, "Sonar", StringComparison.OrdinalIgnoreCase)) return;

        var text = message.Message.TextValue;
        var rank = Regex.Match(text, @"\bRank\s+(SS|S)\s*:\s*(.*?)\s*(?:|<|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!rank.Success) return;
        var link = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (link == null) return;
        var worldMatch = Regex.Match(text, @"<([^>]+)>");
        if (!worldMatch.Success) return;
        var worldName = worldMatch.Groups[1].Value.Replace("", string.Empty).Trim();
        var world = Plugin.DataManager.GetExcelSheet<World>()
            .FirstOrDefault(row => string.Equals(row.Name.ToString(), worldName, StringComparison.OrdinalIgnoreCase));
        if (world.RowId == 0)
        {
            diagnostics.Write("Sonar", $"无法识别服务器：{worldName}，已忽略该报告。");
            return;
        }

        var instance = HuntAutomation.ParseInstance(text);
        var key = $"{world.RowId}:{link.TerritoryType.RowId}:{instance}:{link.RawX / 15000}:{link.RawY / 15000}";
        if (text.Contains("was just killed", StringComparison.OrdinalIgnoreCase))
        {
            var deadKeys = reports.Values.Where(r => r.WorldId == world.RowId
                && r.Link.TerritoryType.RowId == link.TerritoryType.RowId
                && (instance == 0 || r.Instance == instance)
                && Vector3.Distance(r.LinkToWorld(0f), new Vector3(link.RawX / 1000f, 0f, link.RawY / 1000f)) < 75f)
                .Select(r => r.Key).ToList();
            foreach (var deadKey in deadKeys) reports.Remove(deadKey);
            if (deadKeys.Contains(currentKey))
            {
                StopMobNavigation();
                navigator.Stop("Sonar 报告怪物死亡，选择下一目标");
                currentKey = null;
                StatusText = "目标已死亡，正在选择下一只 S 怪";
            }
            return;
        }

        reports[key] = new Report(key, world.RowId, worldName, rank.Groups[2].Value.Trim(),
            rank.Groups[1].Value.Equals("SS", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
            instance, link, DateTime.UtcNow);
        diagnostics.Write("Sonar", $"收到 {worldName} Rank {rank.Groups[1].Value} {link.PlaceName} ({link.XCoord:F1}, {link.YCoord:F1}) instance={instance}。");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc) return;
        nextUpdateUtc = now + TimeSpan.FromMilliseconds(500);
        if (configuration.ActiveTask != AutomationTask.HuntSonar) { StopMobNavigation(); return; }
        if (navigator.IsPaused)
        {
            StopMobNavigation();
            StatusText = "Sonar 狩猎已暂停";
            return;
        }

        foreach (var stale in reports.Values.Where(r => now - r.ReportedUtc > ReportLifetime)
                     .Select(r => r.Key).ToList()) reports.Remove(stale);
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51)) return;
        var selected = reports.Values.OrderBy(r => r.RankPriority)
            .ThenBy(r => r.WorldId == player.CurrentWorld.RowId ? 0 : 1)
            .ThenByDescending(LocalCrowd).ThenBy(r => r.ReportedUtc).FirstOrDefault();
        if (selected == null)
        {
            if (currentKey != null) { StopMobNavigation(); navigator.Stop("Sonar 暂无存活 S 怪"); currentKey = null; }
            ReturnToUldah(now);
            return;
        }

        if (selected.Key != currentKey)
        {
            StopMobNavigation();
            navigator.Stop("Sonar 切换目标");
            currentKey = selected.Key;
            seenPulledMob = false;
            lastMobSeenUtc = DateTime.MinValue;
        }

        if (selected.WorldId != player.CurrentWorld.RowId)
        {
            if (!configuration.HuntAutoWorldVisit) { StatusText = $"等待手动前往 {selected.WorldName}"; return; }
            if (!lifestream.IsInstalled) { StatusText = "需要 Lifestream 才能自动跨服"; return; }
            if (Plugin.Condition[ConditionFlag.InCombat] || lifestream.IsBusy())
            {
                StatusText = "等待战斗结束或 Lifestream 空闲";
                return;
            }
            if (now - lastWorldRequestUtc > TimeSpan.FromSeconds(90))
            {
                lastWorldRequestUtc = now;
                if (lifestream.ChangeWorld(selected.WorldName))
                    diagnostics.Write("Sonar", $"请求从沙都网关跨服前往 {selected.WorldName}。");
            }
            StatusText = $"正在经沙都前往 {selected.WorldName} · {selected.Name}";
            return;
        }

        var sameZone = Plugin.ClientState.TerritoryType == selected.Link.TerritoryType.RowId;
        var sameInstance = selected.Instance == 0 || lifestream.GetNumberOfInstances() == 1
            || selected.Instance == lifestream.GetCurrentInstance();
        if (!sameZone || !sameInstance || Vector3.Distance(player.Position, selected.LinkToWorld(player.Position.Y)) > 30f)
        {
            if (navigator.ActiveTarget?.IsSonar != true)
                navigator.NavigateHunt(selected.Link, $"Sonar · {selected.Name}", selected.Instance, sonar: true);
            StatusText = $"前往 {selected.WorldName} · {selected.Name}";
            return;
        }

        var monster = Plugin.ObjectTable.OfType<IBattleChara>()
            .Where(obj => obj.MaxHp > 0
                && (selected.Name.Length == 0 || string.Equals(obj.Name.TextValue, selected.Name,
                    StringComparison.OrdinalIgnoreCase))
                && Vector3.Distance(obj.Position, selected.LinkToWorld(obj.Position.Y)) < 65f)
            .OrderBy(obj => Vector3.Distance(obj.Position, selected.LinkToWorld(obj.Position.Y)))
            .FirstOrDefault();

        if (navigator.ActiveTarget?.IsSonar == true)
            navigator.Stop("已到怪物附近，转入开怪距离判断");

        if (monster == null)
        {
            StopMobNavigation();
            if (seenPulledMob && now - lastMobSeenUtc > TimeSpan.FromSeconds(8)
                && Vector3.Distance(player.Position, selected.LinkToWorld(player.Position.Y)) < 80f)
            {
                reports.Remove(selected.Key);
                currentKey = null;
                navigator.Stop("怪物消失，选择下一目标");
            }
            else StatusText = $"已到 {selected.Name} 附近，等待怪物进入视野或 Sonar 死亡报告";
            return;
        }

        lastMobSeenUtc = now;
        if (monster.CurrentHp == 0)
        {
            reports.Remove(selected.Key);
            currentKey = null;
            StopMobNavigation();
            navigator.Stop("怪物已死亡，选择下一目标");
            return;
        }
        var distance = Vector3.Distance(player.Position, monster.Position);
        if (monster.CurrentHp == monster.MaxHp)
        {
            if (distance < 18f && vnavmesh.IsReady() && now - lastMobMoveUtc > TimeSpan.FromSeconds(3))
            {
                var direction = player.Position - monster.Position;
                direction.Y = 0f;
                if (direction.LengthSquared() < 1f) direction = Vector3.UnitX;
                var safePoint = monster.Position + Vector3.Normalize(direction) * 22f;
                safePoint = vnavmesh.NearestPoint(safePoint, 8f, 6f) ?? safePoint;
                lastMobMoveUtc = now;
                ownsMobNavigation = vnavmesh.MoveCloseTo(safePoint, false, 2f);
                navigator.SetHuntCombatNavigation(ownsMobNavigation);
            }
            else if (distance >= 20f) StopMobNavigation();
            StatusText = $"{selected.Name} 尚未开怪，保持约 20y · 当前 {distance:F0}y";
            return;
        }

        seenPulledMob = true;
        if (distance <= 5f)
        {
            StopMobNavigation();
            StatusText = $"{selected.Name} 已开怪 · 距离 {distance:F0}y";
            return;
        }
        if (vnavmesh.IsReady() && now - lastMobMoveUtc > TimeSpan.FromSeconds(3)
            && (!ownsMobNavigation || !vnavmesh.IsBusy()))
        {
            lastMobMoveUtc = now;
            ownsMobNavigation = vnavmesh.MoveCloseTo(monster.Position, false, 3f);
            navigator.SetHuntCombatNavigation(ownsMobNavigation);
        }
        StatusText = $"{selected.Name} 已开怪，正在靠近 · {distance:F0}y";
    }

    private int LocalCrowd(Report report)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.CurrentWorld.RowId != report.WorldId
            || Plugin.ClientState.TerritoryType != report.Link.TerritoryType.RowId
            || report.Instance > 0 && lifestream.GetNumberOfInstances() > 1
                && lifestream.GetCurrentInstance() != report.Instance) return 0;
        return Plugin.ObjectTable.OfType<IPlayerCharacter>()
            .Count(obj => Vector3.Distance(obj.Position, report.LinkToWorld(obj.Position.Y)) <= 60f);
    }

    private void ReturnToUldah(DateTime now)
    {
        StopMobNavigation();
        if (Plugin.ClientState.TerritoryType == 130)
        {
            StatusText = "当前无 Sonar S/SS 报告 · 在沙都等待";
            return;
        }
        if (Plugin.Condition[ConditionFlag.InCombat] || now - lastCityTeleportUtc < TimeSpan.FromSeconds(45))
        {
            StatusText = "等待可传送后返回沙都";
            return;
        }
        lastCityTeleportUtc = now;
        StatusText = teleporter.Teleport(9) ? "返回沙都主水晶等待新报告"
            : "沙都主水晶未解锁或暂时无法传送";
    }

    private void StopMobNavigation()
    {
        if (ownsMobNavigation) vnavmesh.Stop();
        ownsMobNavigation = false;
        navigator.SetHuntCombatNavigation(false);
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        StopMobNavigation();
    }

    private sealed record Report(string Key, uint WorldId, string WorldName, string Name,
        int RankPriority, int Instance, MapLinkPayload Link, DateTime ReportedUtc)
    {
        public Vector3 LinkToWorld(float height) => new(Link.RawX / 1000f, height, Link.RawY / 1000f);
    }
}
