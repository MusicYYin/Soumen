using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Soumen.Services;
using Soumen.Windows;

namespace Soumen;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/soumen";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Soumen");
    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private readonly MapFlagAutomation automation;
    private readonly PartyTeleportService partyTeleportService;
    private readonly TreasureDungeonAutomation treasureDungeonAutomation;
    private readonly TreasureSackAutomation treasureSackAutomation;
    private readonly LeaderTreasureAutomation leaderTreasureAutomation;
    private readonly AutoDiscardService autoDiscardService;
    private readonly StatisticsService statisticsService;
    private readonly HuntAutomation huntAutomation;
    private readonly FrontlineRadarService frontlineRadarService;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        configuration = Configuration.Load(PluginInterface);
        diagnostics = new DiagnosticLogger(configuration);
        diagnostics.Write("运行", "Soumen 已加载。");
        automation = new MapFlagAutomation(configuration, diagnostics);
        partyTeleportService = new PartyTeleportService(configuration, automation.PrepareForPartyTeleport,
            automation.CanAcceptPartyTeleport, diagnostics);
        treasureDungeonAutomation = new TreasureDungeonAutomation(configuration);
        treasureSackAutomation = new TreasureSackAutomation(configuration, automation, diagnostics);
        leaderTreasureAutomation = new LeaderTreasureAutomation(configuration, automation, treasureSackAutomation, diagnostics);
        autoDiscardService = new AutoDiscardService(configuration, automation, diagnostics);
        statisticsService = new StatisticsService(configuration);
        huntAutomation = new HuntAutomation(configuration, automation, diagnostics);
        frontlineRadarService = new FrontlineRadarService(configuration, diagnostics);
        mainWindow = new MainWindow(configuration, automation, leaderTreasureAutomation, autoDiscardService,
            statisticsService, diagnostics, huntAutomation);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Soumen 面板",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public void Dispose()
    {
        diagnostics.Write("运行", "Soumen 正在卸载。");
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        frontlineRadarService.Dispose();
        huntAutomation.Dispose();
        statisticsService.Dispose();
        autoDiscardService.Dispose();
        leaderTreasureAutomation.Dispose();
        treasureSackAutomation.Dispose();
        treasureDungeonAutomation.Dispose();
        partyTeleportService.Dispose();
        automation.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        _ = command;
        _ = args;
        mainWindow.IsOpen = true;
    }

    private void OpenMainUi() => mainWindow.IsOpen = true;

}
