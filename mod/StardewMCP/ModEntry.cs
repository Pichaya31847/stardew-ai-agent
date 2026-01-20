using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewMCP;

/// <summary>Main entry point for the Stardew MCP Bridge mod.</summary>
public class ModEntry : Mod
{
    private WebSocketServer? _wsServer;
    private GameStateSerializer? _stateSerializer;
    private CommandExecutor? _commandExecutor;

    /// <summary>The mod entry point.</summary>
    public override void Entry(IModHelper helper)
    {
        Monitor.Log("Stardew MCP Bridge loading...", LogLevel.Info);

        // Initialize components
        _stateSerializer = new GameStateSerializer();
        _commandExecutor = new CommandExecutor(helper, Monitor);
        _stateSerializer.SetCommandExecutor(_commandExecutor); // Wire up for movement state
        _wsServer = new WebSocketServer(Monitor, _stateSerializer, _commandExecutor);

        // Register events
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        Monitor.Log("Stardew MCP Bridge loaded!", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Monitor.Log("Game launched, starting WebSocket server on port 8765...", LogLevel.Info);
        _wsServer?.Start(8765);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Monitor.Log($"Save loaded: {Game1.player.Name} on {Game1.player.farmName} Farm", LogLevel.Info);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Only process when game is running
        if (!Context.IsWorldReady)
            return;

        // Process any pending commands from WebSocket
        _commandExecutor?.ProcessPendingCommands();
    }

    private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        // Only broadcast state when game is running
        if (!Context.IsWorldReady)
            return;

        // Broadcast game state to connected clients
        _wsServer?.BroadcastState();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Monitor.Log("Returned to title screen", LogLevel.Info);
    }
}
