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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Soumen");
    private readonly Configuration configuration;
    private readonly MapFlagAutomation automation;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        configuration = Configuration.Load(PluginInterface);
        automation = new MapFlagAutomation(configuration);
        mainWindow = new MainWindow(configuration, automation);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Soumen；可用参数：on、off、pause、resume、stop",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        automation.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        _ = command;
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                automation.SetEnabled(true);
                break;
            case "off":
                automation.SetEnabled(false);
                break;
            case "stop":
                automation.Stop();
                break;
            case "pause":
                automation.SetPaused(true);
                break;
            case "resume":
                automation.SetPaused(false);
                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }

    private void OpenMainUi() => mainWindow.IsOpen = true;
}
