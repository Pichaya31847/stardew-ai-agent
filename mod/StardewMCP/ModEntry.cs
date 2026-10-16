using System.IO;
using StardewMCP.Agent;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

/// <summary>
/// SMAPI entry point. Hosts the agent's MCP endpoint inside the game and drives the agent
/// runtime from the game loop.
/// </summary>
public class ModEntry : Mod
{
    private ModConfig _config = new();
    private AgentRuntime _agent = null!;
    private AgentHttp _http = null!;
    private bool _autoLoadDone;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        string logDir = string.IsNullOrWhiteSpace(_config.LogDir)
            ? Path.Combine(helper.DirectoryPath, "logs")
            : _config.LogDir;

        _agent = new AgentRuntime(helper, Monitor, _config, new AgentLog(logDir, Monitor));
        AgentTools.Rt = _agent;
        _http = new AgentHttp(_agent, Monitor, Path.Combine(helper.DirectoryPath, "assets"));

        helper.Events.GameLoop.GameLaunched += (_, _) => _http.Start(_config.HttpPort);
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

        helper.ConsoleCommands.Add(
            "mcp_freeze",
            "Freeze the in-game clock while the AI is thinking.\n\nUsage: mcp_freeze [on|off] (no argument toggles)",
            OnFreezeCommand);

        Monitor.Log($"Stardew MCP loaded. Logs: {logDir}", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // The agent keeps playing while the terminal or OBS window has focus.
        Game1.options.pauseWhenOutOfFocus = false;
        _agent.Log.Add("system", text: $"Save loaded: {Game1.player.Name} / {Game1.player.farmName} Farm");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        TryAutoLoad(e.Ticks);
        DebugApi.Tick();
        _agent.Tick();
        if (e.IsMultipleOf(15))
            _http.RefreshState();
    }

    /// <summary>For unattended runs: load the configured save once the title screen is up.</summary>
    private void TryAutoLoad(uint ticks)
    {
        if (_autoLoadDone || string.IsNullOrEmpty(_config.AutoLoadSave))
            return;
        if (ticks <= 120 || Game1.activeClickableMenu is not TitleMenu)
            return;

        _autoLoadDone = true;
        Monitor.Log($"Auto-loading save {_config.AutoLoadSave}", LogLevel.Info);
        SaveGame.Load(_config.AutoLoadSave);
        Game1.exitActiveMenu();
    }

    private void OnFreezeCommand(string command, string[] args)
    {
        _config.FreezeTimeWhileThinking = args.Length == 0
            ? !_config.FreezeTimeWhileThinking
            : args[0] != "off";
        Monitor.Log($"Freeze time while thinking: {(_config.FreezeTimeWhileThinking ? "on" : "off")}", LogLevel.Info);
    }
}
