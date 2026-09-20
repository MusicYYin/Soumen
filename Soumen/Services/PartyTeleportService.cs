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
    private readonly string[] promptFragments;

    public PartyTeleportService(Configuration configuration)
    {
        this.configuration = configuration;
        promptFragments = LoadPromptFragments();
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup);
    }

    public void Dispose()
        => Plugin.AddonLifecycle.UnregisterListener(OnSelectYesnoPostSetup);

    private void OnSelectYesnoPostSetup(AddonEvent type, AddonArgs args)
    {
        _ = type;
        if (!configuration.Enabled
            || !configuration.AcceptPartyTeleportRequests
            || promptFragments.Length == 0)
        {
            return;
        }

        var addon = args.GetAddon<AddonSelectYesno>();
        var prompt = addon == null || addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(prompt)
            || !promptFragments.All(fragment => prompt.Contains(fragment, StringComparison.Ordinal)))
        {
            return;
        }

        Plugin.Log.Information("Accepting party teleport request: {Prompt}", prompt);
        AddonSelectYesno.Yes();
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
