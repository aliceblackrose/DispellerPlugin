using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dispeller.Models;
using Dispeller.Services;
using Dispeller.Windows;

namespace Dispeller;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/dispeller";

    public Configuration Configuration { get; }
    public DresserScanner DresserScanner { get; }
    public ItemMetadataService ItemMetadataService { get; }
    public SnapshotService SnapshotService { get; }
    public DuplicateAnalyzer DuplicateAnalyzer { get; }
    public DresserDuplicateRemover DuplicateRemover { get; }

    public readonly WindowSystem WindowSystem = new("Dispeller");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ItemMetadataService = new ItemMetadataService(DataManager, PluginInterface, Log);
        SnapshotService = new SnapshotService(PluginInterface, PlayerState, Log);
        DuplicateAnalyzer = new DuplicateAnalyzer(ItemMetadataService);
        DresserScanner = new DresserScanner(Framework, Log);
        DuplicateRemover = new DresserDuplicateRemover(
            Framework,
            Log,
            ChatGui,
            DresserScanner,
            DuplicateAnalyzer,
            SnapshotService,
            Configuration);
        DresserScanner.Updated += OnDresserUpdated;

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Dispeller. Use /dispeller clean to restore recommended duplicate glamours to inventory, or /dispeller stop to cancel cleanup.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("Dispeller 2.1 loaded.");
    }

    private void OnDresserUpdated(IReadOnlyList<DresserItem> items)
    {
        SnapshotService.Capture(items);
        mainWindow.NotifyDresserUpdated();
    }

    private void OnLogin() =>
        OnCharacterChanged();

    private void OnLogout(int _, int __) =>
        OnCharacterChanged();

    private void OnCharacterChanged()
    {
        if (DuplicateRemover.IsRunning)
            DuplicateRemover.Cancel();

        DresserScanner.ClearCache();
        mainWindow.NotifyCharacterChanged();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        DresserScanner.Updated -= OnDresserUpdated;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        WindowSystem.RemoveAllWindows();

        mainWindow.Dispose();
        DuplicateRemover.Dispose();
        DresserScanner.Dispose();
        ItemMetadataService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var action = args.Trim();

        if (action.Equals("clean", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            DuplicateRemover.Start();
            return;
        }

        if (action.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            DuplicateRemover.Cancel();
            return;
        }

        mainWindow.Toggle();
    }

    public void ToggleMainUi() =>
        mainWindow.Toggle();
}
