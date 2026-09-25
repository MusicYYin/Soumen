using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Soumen.Models;

namespace Soumen.Services;

/// <summary>Hunt conductors and their last announced map link live only in memory.</summary>
public sealed class HuntAutomation : IDisposable
{
    private readonly Configuration configuration;
    private readonly MapFlagAutomation navigator;
    private readonly DiagnosticLogger diagnostics;
    private readonly List<string> leaders = [];
    private MapLinkPayload? lastLink;
    private string lastSender = string.Empty;
    private DateTime lastLinkUtc;

    public HuntAutomation(Configuration configuration, MapFlagAutomation navigator, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.navigator = navigator;
        this.diagnostics = diagnostics;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public IReadOnlyList<string> Leaders => leaders;
    public string LastLocation => lastLink == null ? "暂无车头坐标"
        : $"{lastLink.PlaceName}  X {lastLink.XCoord:F1}  Y {lastLink.YCoord:F1} · {lastSender}";
    public bool HasLocation => lastLink != null;

    public void SetEnabled(bool enabled)
    {
        if (configuration.HuntEnabled == enabled) return;
        navigator.Stop(enabled ? "切换到狩猎" : "已结束狩猎");
        configuration.HuntEnabled = enabled;
        if (enabled)
        {
            if (configuration.OperatingMode != OperatingMode.Follow)
                navigator.SetOperatingMode(OperatingMode.Follow);
            if (!configuration.Enabled) navigator.SetEnabled(true);
        }
        configuration.Save();
    }

    public bool AddCurrentTarget()
    {
        var target = Plugin.TargetManager.Target;
        var name = target?.Name.TextValue?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == Plugin.ObjectTable.LocalPlayer?.Name.TextValue)
            return false;
        if (!leaders.Contains(name, StringComparer.OrdinalIgnoreCase)) leaders.Add(name);
        diagnostics.Write("狩猎", $"添加车头：{name}");
        return true;
    }

    public void RemoveLeader(string name) => leaders.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    public void ClearSession()
    {
        leaders.Clear();
        lastLink = null;
        lastSender = string.Empty;
        if (navigator.ActiveTarget?.IsHunt == true) navigator.Stop("车头信息已清空");
    }

    public void NavigateLast()
    {
        if (lastLink != null) navigator.NavigateHunt(lastLink, lastSender);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!configuration.HuntEnabled || leaders.Count == 0) return;
        if (message.LogKind is not (XivChatType.Shout or XivChatType.Yell or XivChatType.Say
            or XivChatType.Party or XivChatType.CrossParty)) return;

        var sender = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault()?.PlayerName
            ?? message.Sender.TextValue;
        var fromLeader = leaders.Contains(sender, StringComparer.OrdinalIgnoreCase);
        var link = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (configuration.HuntMuteOtherShouts && !fromLeader && link == null
            && message.LogKind is (XivChatType.Shout or XivChatType.Yell or XivChatType.Say))
        {
            message.PreventOriginal();
            return;
        }
        if (!fromLeader) return;

        if (configuration.HuntHighlightLeader)
        {
            var colored = new SeStringBuilder();
            colored.AddUiForeground(578);
            foreach (var payload in message.Message.Payloads) colored.Add(payload);
            colored.AddUiForegroundOff();
            message.Message = colored.Build();
        }

        if (link == null) return;
        var now = DateTime.UtcNow;
        if (lastLink != null && lastLink.TerritoryType.RowId == link.TerritoryType.RowId
            && lastLink.Map.RowId == link.Map.RowId && lastLink.RawX == link.RawX
            && lastLink.RawY == link.RawY && now - lastLinkUtc < TimeSpan.FromMinutes(2)) return;

        lastLink = link;
        lastSender = sender;
        lastLinkUtc = now;
        diagnostics.Write("狩猎", $"{sender} 发布坐标：{link.PlaceName} ({link.XCoord:F1}, {link.YCoord:F1})。");
        if (configuration.HuntAutoOpenMap)
        {
            try { Plugin.GameGui.OpenMapWithMapLink(link); }
            catch (Exception exception) { diagnostics.WriteException("狩猎", "打开车头地图", exception); }
        }
        if (configuration.HuntChatNotification)
            Plugin.ChatGui.Print($"[Soumen 狩猎] {sender}：{link.PlaceName} ({link.XCoord:F1}, {link.YCoord:F1})");
        if (configuration.HuntAutoNavigate) NavigateLast();
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        ClearSession();
    }
}
