using System.IO;
using StardewMCP.Agent;
using StardewMCP.Agent.Brain;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

/// <summary>
/// SMAPI entry point. Hosts the agents' MCP endpoints inside the game and drives each split-screen
/// seat's runtime from that screen's game loop (SMAPI raises UpdateTicked once per screen).
/// </summary>
public class ModEntry : Mod
{
    private ModConfig _config = new();
    private AgentLog _log = null!;
    private AgentHttp _http = null!;
    private bool _autoLoadDone;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        string logDir = string.IsNullOrWhiteSpace(_config.LogDir)
            ? Path.Combine(helper.DirectoryPath, "logs")
            : _config.LogDir;

        _log = new AgentLog(logDir, Monitor);
        AgentTools.Config = _config;
        Players.Init(helper, Monitor, _config, _log);
        DayStats.Init(_log);
        WorldClock.Init(_config);
        PassOut.Init(_log);
        Perception.Init();
        SplitScreen.Init(helper, Monitor, _config);
        SplitScreenPatches.Apply(ModManifest, _config);
        SocialTools.Apply(ModManifest);
        Ownership.Apply(ModManifest);
        AgentTools.Apply(ModManifest);
        string assetDir = Path.Combine(helper.DirectoryPath, "assets");
        _http = new AgentHttp(_log, _config, Monitor, assetDir);
        Brains.Init(helper, Monitor, _config, _log, assetDir);
        Hud.Init(helper, _config);

        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            _http.Start(_config.HttpPort);
            ConfigMenu.Register(helper, ModManifest, _config, Monitor);
        };
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            Brains.StopAll();
            SplitScreen.OnReturnedToTitle();
            PassOut.OnReturnedToTitle();
            WorldProgress.Reset();
        };
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Specialized.UnvalidatedUpdateTicked += (_, _) =>
        {
            // SMAPI deliberately skips normal UpdateTicked while a save is in progress.
            // Release only our pause and refresh diagnostics; do not run gameplay coroutines or AI here.
            if (!Context.IsWorldReady || (!Game1.newDay && !Game1.fadeToBlack && Game1.farmEvent == null && !Game1.game1.isLocalMultiplayerNewDayActive && Game1.activeClickableMenu is not SaveGameMenu and not ShippingMenu)) return;
            var seat = Players.Current;
            seat.OnTick();
            seat.State = ScreenState.Capture();
            if (seat.Screen == 0)
            {
                WorldClock.ReleaseForTransition();
                _http.RefreshState();
            }
        };
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            Briefing.OnDayStarted();
            JojaRoute.OnDayStarted();
            // a 0 stack (left by an older eat) blocks its slot forever: this screen's farmer drops it
            for (int i = 0; i < Game1.player.Items.Count; i++)
                if (Game1.player.Items[i] is { Stack: <= 0 }) Game1.player.Items[i] = null;
        };
        // The day's log is complete here: write day_XXX_summary.json (failure reasons per tool).
        helper.Events.GameLoop.DayEnding += (_, _) => { if (Context.ScreenId == 0) DaySummary.Write(_log, AgentLog.FarmDay, Monitor); };

        helper.ConsoleCommands.Add(
            "mcp_freeze",
            "Freeze the world while an AI is thinking.\n\nUsage: mcp_freeze [on|off] (no argument toggles)",
            OnFreezeCommand);

        helper.ConsoleCommands.Add(
            "mcp_brain",
            "The mod's built-in AI players.\n\nUsage: mcp_brain [status|start|stop]",
            (_, args) => Brains.Command(args));

        Monitor.Log($"Stardew MCP loaded{(_config.SplitScreen ? $" (split-screen: {Players.ScreenCount} screens, {Players.Describe()})" : "")}. Logs: {logDir}", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // Also raised on each farmhand screen when it joins (before its character exists);
        // SplitScreen logs those joins once they finish.
        SplitScreen.OnSaveLoaded();
        WorldProgress.Reset();
        // The agents keep playing while the terminal or OBS window has focus.
        Game1.options.pauseWhenOutOfFocus = false;
        if (Context.ScreenId != 0)
            return;
        // Seat roles and split-screen may have changed in the config menu; farmhands join on later host ticks.
        Players.Resolve(_config, Monitor);
        SaveFolder.OnSaveLoaded();
        EconomyTools.CheckWallets(Monitor, _config);
        Players.Host.Runtime.Log.Add("system", text: $"Save loaded: {Game1.player.Name} / {Game1.player.farmName} Farm");
        Brains.StartAll();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        var seat = Players.Current;
        seat.OnTick();

        if (seat.Screen == 0)
        {
            NewGamePrefill.Tick(Helper, _config);
            TryAutoLoad(e.Ticks);
            DebugApi.Tick();
            SplitScreen.HostTick();
        }
        else
            SplitScreen.GuestTick(seat.Screen);
        SplitScreen.ScreenTick();

        seat.Runtime.Tick();
        Briefing.Tick(seat, e.IsMultipleOf(300));
        if (e.IsMultipleOf(15))
            seat.State = ScreenState.Capture();
        // Local split-screen stops the clock once every screen asks to (a menu, or a farmer that cannot move). The parked
        // camera must never ask: with the three AIs waiting in bed the clock stopped at 18:20 and 2:00 AM never came (2026-10-20)
        if (seat.Role == SeatRole.Camera && Context.IsWorldReady)
        {
            Game1.player.requestingTimePause.Value = false;
            if (e.IsMultipleOf(60))
                ParkCameraInBed(Monitor);
        }

        if (seat.Screen == 0)
        {
            WorldClock.Tick();
            if (e.IsMultipleOf(60))
                Bedtime.Tick(_config);
            if (e.IsMultipleOf(15))
                _http.RefreshState();
        }
    }

    /// <summary>
    /// Keep the camera farmer on its own bed. A screen only fades into the night when its farmer is in bed (Game1.NewDay), and the
    /// game counts a farmer as in bed by the tile it stands on. Once the camera stood at its cabin door, the AIs' screens went
    /// black but the night waited for the camera forever (days 6-7, 2026-10-20). GetPlayerBedSpot falls back to the entry tile
    /// when the home has no bed of the upgrade's type, which is that same door, so the bed itself is looked up.
    /// </summary>
    private static void ParkCameraInBed(IMonitor monitor)
    {
        var p = Game1.player;
        if (p.isInBed.Value || Game1.newDay || Game1.fadeToBlack || Game1.isWarping || Game1.eventUp || Game1.activeClickableMenu != null)
            return;
        if (Utility.getHomeOfFarmer(p) is not { } home)
            return;
        if ((home.GetPlayerBed() ?? home.GetBed()) is not { } bedFurniture)
        {
            monitor.LogOnce($"The camera farmer {p.Name} has no bed in {home.NameOrUniqueName}, so the night can't start: put a bed in that cabin.", LogLevel.Error);
            return;
        }
        var bed = bedFurniture.GetBedSpot();
        if (p.currentLocation == home)
            p.Position = new Microsoft.Xna.Framework.Vector2(bed.X * 64, bed.Y * 64);
        else
            Game1.warpFarmer(home.NameOrUniqueName, bed.X, bed.Y, false);
    }

    /// <summary>For unattended runs: load the configured save once the title screen is up.</summary>
    private void TryAutoLoad(uint ticks)
    {
        if (_autoLoadDone || string.IsNullOrEmpty(_config.AutoLoadSave))
            return;
        if (ticks <= 120 || Game1.activeClickableMenu is not TitleMenu)
            return;

        _autoLoadDone = true;
        // A wrong name would otherwise end in a failed load and a stuck title screen (runner/game_watchdog.py then waits in vain).
        if (!Directory.Exists(Path.Combine(Constants.SavesPath, _config.AutoLoadSave)))
        {
            Monitor.Log($"AutoLoadSave \"{_config.AutoLoadSave}\" is not a folder in {Constants.SavesPath}; staying on the title screen.", LogLevel.Error);
            return;
        }
        SaveFolder.Pending = _config.AutoLoadSave;
        Monitor.Log($"Auto-loading save {_config.AutoLoadSave}", LogLevel.Info);
        SaveGame.Load(_config.AutoLoadSave);
        Game1.exitActiveMenu();
    }

    private void OnFreezeCommand(string command, string[] args)
    {
        _config.FreezeTimeWhileThinking = args.Length == 0
            ? !_config.FreezeTimeWhileThinking
            : args[0] != "off";
        Monitor.Log($"Freeze while thinking: {(_config.FreezeTimeWhileThinking ? "on" : "off")}", LogLevel.Info);
    }
}
