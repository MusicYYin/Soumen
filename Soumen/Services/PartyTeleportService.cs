using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

public sealed unsafe class PartyTeleportService : IDisposable
{
    private const uint TeleportOfferAddonRow = 1800;

    private readonly Configuration configuration;
    private readonly System.Action onTeleportAccepted;
    private readonly System.Func<bool> canAcceptTeleport;
    private readonly DiagnosticLogger diagnostics;
    private readonly string[] promptFragments;
    private nint lastPromptAddress;
    private DateTime lastResponseUtc = DateTime.MinValue;

    public PartyTeleportService(
        Configuration configuration,
        System.Action onTeleportAccepted,
        System.Func<bool> canAcceptTeleport,
        DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.onTeleportAccepted = onTeleportAccepted;
        this.canAcceptTeleport = canAcceptTeleport;
        this.diagnostics = diagnostics;
        promptFragments = LoadPromptFragments();
        // PostSetup runs before the game finishes wiring the dialog. Deferred
        // offers can appear later, so respond only when it is actually drawn.
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "SelectYesno", OnSelectYesnoPostDraw);
    }

    public void Dispose()
        => Plugin.AddonLifecycle.UnregisterListener(OnSelectYesnoPostDraw);

    private void OnSelectYesnoPostDraw(AddonEvent type, AddonArgs args)
    {
        _ = type;
        if (!configuration.Enabled
            || (!configuration.AcceptPartyTeleportRequests && !configuration.AutoTeleport)
            || promptFragments.Length == 0)
        {
            return;
        }

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (addon == null || (lastPromptAddress == (nint)addon
            && DateTime.UtcNow - lastResponseUtc < TimeSpan.FromSeconds(1.5))) return;
        var prompt = addon == null || addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(prompt)
            || !promptFragments.All(fragment => prompt.Contains(fragment, StringComparison.Ordinal)))
        {
            return;
        }

        lastPromptAddress = (nint)addon;
        lastResponseUtc = DateTime.UtcNow;

        if (canAcceptTeleport())
        {
            Plugin.Log.Information("Accepting party teleport request: {Prompt}", prompt);
            diagnostics.Write("队友传送", "已向游戏确认接受队友传送邀请。" );
            onTeleportAccepted();
            addon->AtkUnitBase.FireCallbackInt(0);
        }
        else
        {
            Plugin.Log.Information("Rejecting party teleport request while self teleport is enabled: {Prompt}", prompt);
            diagnostics.Write("队友传送", "当前使用自行传送，已向游戏确认拒绝邀请。" );
            addon->AtkUnitBase.FireCallbackInt(1);
        }
    }

    private static string[] LoadPromptFragments()
    {
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(TeleportOfferAddonRow);
            return
            [
                .. SeString.Parse(row.Text)
                    .Payloads.OfType<TextPayload>()
                    .Select(payload => payload.Text?.Trim() ?? string.Empty)
                    .Where(text => text.Length > 0),
            ];
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load the party teleport prompt.");
            return [];
        }
    }
}
