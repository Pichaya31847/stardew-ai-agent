using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Brings up the four split-screen seats without gamepads and keeps every screen ready to record:
/// the host adds one local instance at a time (the same call the co-op join menu makes), and each
/// new screen picks its farmhand and finishes character creation by itself.
/// </summary>
public static class SplitScreen
{
    private static IModHelper _helper = null!;
    private static IMonitor _monitor = null!;
    private static ModConfig _config = null!;

    private static readonly Stopwatch _sinceLoad = new();
    private static readonly Stopwatch _joinWatch = new();
    private static int _joiningScreen = -1;
    private static bool _gaveUp;
    private static bool _cabinsTried;
    private static string? _lastProblem;
    private static readonly int[] _menuTicks = new int[Players.Seats];

    /// <summary>Seconds a farmhand screen may take to join before setup stops trying.</summary>
    private const int JoinTimeoutSeconds = 60;

    public static void Init(IModHelper helper, IMonitor monitor, ModConfig config)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
    }

    /// <summary>Farmhand name for a seat (P2..P4); P1 is the save's own farmer.</summary>
    public static string NameFor(int screen)
    {
        var own = _config.Seat(screen).Name;
        if (!string.IsNullOrWhiteSpace(own))
            return own.Trim();
        var names = _config.FarmhandNames;
        int i = screen - 1;
        return i >= 0 && i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i].Trim() : $"P{screen + 1}";
    }

    public static void OnSaveLoaded()
    {
        if (Context.ScreenId == 0)
        {
            _sinceLoad.Restart();
            _gaveUp = false;
            _cabinsTried = false;
            _lastProblem = null;
        }
    }

    public static void OnReturnedToTitle()
    {
        if (Context.ScreenId == 0)
        {
            _sinceLoad.Reset();
            _joiningScreen = -1;
        }
    }

    /// <summary>Host screen tick.</summary>
    public static void HostTick()
    {
        if (!_config.SplitScreen)
            return;

        // A joining instance gets fresh Options when it starts loading, and SMAPI raises no ticks for a
        // loading screen; with pauseWhenOutOfFocus it would then skip its updates forever while OBS or a
        // terminal has focus. Clear it from here for every instance.
        foreach (var game in GameRunner.instance.gameInstances)
            if (game.instanceOptions is { pauseWhenOutOfFocus: true } options)
                options.pauseWhenOutOfFocus = false;
        // MonoGame sleeps 20 ms a frame while unfocused, dropping four screens to ~40 FPS.
        GameRunner.instance.InactiveSleepTime = TimeSpan.Zero;

        if (Context.IsWorldReady && Context.IsMainPlayer && _sinceLoad.IsRunning && _sinceLoad.ElapsedMilliseconds > 2000)
            JoinNext();
    }

    private static void JoinNext()
    {
        int screens = GameRunner.instance.gameInstances.Count;
        if (_joiningScreen >= 0)
        {
            var joining = Players.Slots[_joiningScreen];
            // A new farmhand's world is loaded before its character creation finishes.
            if (joining.State is ScreenState { Ready: true, Menu: not (nameof(CharacterCustomization) or nameof(FarmhandMenu)) })
            {
                joining.Runtime.Log.Add("system", text: $"{joining.Id} joined as {NameFor(_joiningScreen)}");
                _joiningScreen = -1;
            }
            else if (_joinWatch.Elapsed.TotalSeconds > JoinTimeoutSeconds)
            {
                Problem($"{joining.Id} did not finish joining within {JoinTimeoutSeconds}s; split-screen setup stopped. " +
                        "If Windows Firewall asked about Stardew Valley, allow it and reload the save.");
                _joiningScreen = -1;
                _gaveUp = true;
            }
            return;
        }
        if (_gaveUp || screens >= Players.ScreenCount)
            return;

        int free = 0;
        Utility.ForEachLocation(l =>
        {
            if (l is Cabin c && (!c.HasOwner || !c.IsOwnerActivated)) free++;
            return true;
        });
        if (free == 0)
        {
            int missing = Players.ScreenCount - screens;
            if (_config.AutoBuildCabins && !_cabinsTried)
            {
                _cabinsTried = true;
                var built = Cabins.Build(missing);
                _monitor.Log($"Split-screen: built {built.Count} cabin(s) for the farmhands: {string.Join(", ", built)}", LogLevel.Info);
                Players.Host.Runtime.Log.Add("system", text: $"Built {built.Count} cabin(s) for the farmhands");
                if (built.Count > 0)
                    return; // the next tick sees the free cabins
            }
            Problem($"Split-screen needs {missing} more free cabin(s) on the farm (have {screens} of {Players.ScreenCount} players). " +
                    "Start a co-op farm with starting cabins, build them, or turn on AutoBuildCabins.");
            return;
        }

        var used = GameRunner.instance.gameInstances.Select(g => g.instancePlayerOneIndex).ToHashSet();
        var index = Enumerable.Range((int)PlayerIndex.Two, 3).Select(i => (PlayerIndex)i).First(i => !used.Contains(i));
        _joiningScreen = screens;
        _joinWatch.Restart();
        _monitor.Log($"Split-screen: adding {Players.Slots[_joiningScreen].Id} ({NameFor(_joiningScreen)})", LogLevel.Info);
        // The first call also starts the game's local co-op server (a Windows Firewall prompt may appear once).
        GameRunner.instance.AddGameInstance(index);
    }

    private static void Problem(string text)
    {
        if (text == _lastProblem)
            return;
        _lastProblem = text;
        _monitor.Log(text, LogLevel.Warn);
        Players.Host.Runtime.Log.Add("system", text: text);
    }

    /// <summary>Farmhand screen tick (screens 1..3): finish joining without a gamepad.</summary>
    public static void GuestTick(int screen)
    {
        if (!_config.SplitScreen)
            return;
        if (Game1.options is { pauseWhenOutOfFocus: true })
            Game1.options.pauseWhenOutOfFocus = false;

        switch (Game1.activeClickableMenu)
        {
            case FarmhandMenu menu:
                if (menu.approvingFarmhand)
                    return;
                var slots = menu.MenuSlots.OfType<FarmhandMenu.FarmhandSlot>().ToList();
                if (slots.Count == 0 || ++_menuTicks[screen] < 30) // let the list settle for half a second
                    return;
                _menuTicks[screen] = 0;
                var name = NameFor(screen);
                // The same farmhand every day: match by name, else take a new one.
                var pick = slots.FirstOrDefault(s => s.Farmer.isCustomized.Value && s.Farmer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                           ?? slots.FirstOrDefault(s => !s.Farmer.isCustomized.Value)
                           ?? slots[0];
                _monitor.Log($"Split-screen: screen {screen + 1} takes farmhand '{pick.Farmer.Name}'", LogLevel.Info);
                pick.Activate();
                return;

            case CharacterCustomization cc when cc.source == CharacterCustomization.Source.NewFarmhand:
                if (++_menuTicks[screen] < 30)
                    return;
                _menuTicks[screen] = 0;
                var newName = NameFor(screen);
                _helper.Reflection.GetField<TextBox>(cc, "nameBox").GetValue().Text = newName;
                _helper.Reflection.GetField<TextBox>(cc, "favThingBox").GetValue().Text = "Farming";
                Game1.player.Name = newName;
                Game1.player.displayName = newName;
                Game1.player.favoriteThing.Value = "Farming";
                if (string.IsNullOrEmpty(Game1.player.farmName.Value))
                    Game1.player.farmName.Value = Game1.MasterPlayer.farmName.Value;
                _monitor.Log($"Split-screen: screen {screen + 1} creates farmhand '{newName}'", LogLevel.Info);
                _helper.Reflection.GetMethod(cc, "optionButtonClick").Invoke("OK");
                return;

            default:
                _menuTicks[screen] = 0;
                return;
        }
    }

    /// <summary>Every screen's tick, world loaded: keep the frame clean and the framing constant.</summary>
    public static void ScreenTick()
    {
        if (!_config.SplitScreen || !Context.IsWorldReady)
            return;

        if (_config.CleanFrames)
        {
            // The first visit to the farm or house shows a gamepad "MOVE / CHECK" box on farmhand screens.
            Game1.player.mailReceived.Add("button_tut_1");
            Game1.player.mailReceived.Add("button_tut_2");
            for (int i = Game1.onScreenMenus.Count - 1; i >= 0; i--)
                if (Game1.onScreenMenus[i] is ButtonTutorialMenu)
                    Game1.onScreenMenus.RemoveAt(i);
            // The thought strip sits top left of each quadrant in OBS: the game's own "pin toolbar" option keeps the
            // toolbar at the bottom instead of flipping up into it.
            Game1.options.pinToolbarToggle = true;
        }

        if (_config.AutoZoom && Players.SplitScreen)
        {
            // Split-screen halves the zoom; 1.5 × width/960 keeps ~20 tiles across at any resolution
            // (2.0 at 1280-wide screens = effective zoom 1.0, the sharpest).
            float zoom = (float)Math.Round(1.5 * Game1.game1.localMultiplayerWindow.Width / 960.0, 2);
            if (Math.Abs(Game1.options.desiredBaseZoomLevel - zoom) > 0.01f)
                Game1.options.desiredBaseZoomLevel = zoom;
        }
    }
}
