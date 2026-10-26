using System;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>What a split-screen seat is used for.</summary>
public enum SeatRole
{
    /// <summary>A farmer driven by an AI through <c>/mcp/p{N}</c>.</summary>
    Ai,
    /// <summary>The video's camera / summary screen: no AI, never blocks the world.</summary>
    Camera,
    /// <summary>A person at the keyboard (P1 only): never frozen by the clock, never driven by a tool.</summary>
    Human,
    /// <summary>The seat is not used: no screen is joined for it.</summary>
    Off
}

/// <summary>One split-screen seat (screen index = SMAPI <c>Context.ScreenId</c>) and its agent runtime.</summary>
public class PlayerSlot
{
    public int Screen { get; }
    public string Id => $"p{Screen + 1}";
    /// <summary>Set again by <see cref="Players.Resolve"/> on each save load (the config menu can change it).</summary>
    public SeatRole Role { get; internal set; }
    public AgentRuntime Runtime { get; }

    /// <summary>Game ticks seen on this screen; the HTTP thread uses it to tell whether the screen exists.</summary>
    public long LastTick;
    private readonly Stopwatch _sinceTick = new();

    /// <summary>True once an MCP client has talked to this seat; only connected AIs can hold the world frozen.</summary>
    public volatile bool Connected;

    /// <summary>Set by <see cref="WorldClock"/> when this AI thought longer than the configured timeout.</summary>
    public volatile bool TimedOut;

    /// <summary>The MCP client gives up on a call after this many seconds (<c>?reply_within=N</c>; 0 = it waits); <see cref="Perception"/> answers in time.</summary>
    public volatile int ReplyWithinSeconds;

    /// <summary>Latest per-screen state for /api/state, written on this screen's tick.</summary>
    public volatile object? State;

    public PlayerSlot(int screen, SeatRole role, AgentRuntime runtime)
    {
        Screen = screen;
        Role = role;
        Runtime = runtime;
    }

    /// <summary>This screen ticked within the last two seconds (it exists and is in the game loop).</summary>
    public bool Active => _sinceTick.IsRunning && _sinceTick.ElapsedMilliseconds < 2000;

    public void OnTick()
    {
        LastTick++;
        _sinceTick.Restart();
    }

    private volatile int _releasedDay;

    /// <summary>
    /// True while the runner has released this seat's freeze (POST /api/release_freeze): the seat no longer holds the
    /// world still, so game time runs on until the farmer passes out (#22). The release belongs to one farm day and
    /// ends by itself when the day changes; MCP traffic from the seat also ends it.
    /// </summary>
    public bool Released => _releasedDay > 0 && _releasedDay == AgentLog.FarmDay;

    /// <summary>Release (or take back the release of) this seat's freeze for the current farm day. Needs a loaded world.</summary>
    public void SetReleased(bool on) => _releasedDay = on ? AgentLog.FarmDay : 0;

    /// <summary>Called when a request for this seat's AI arrives.</summary>
    public void MarkContact()
    {
        Connected = true;
        TimedOut = false;
        _releasedDay = 0;
    }
}

/// <summary>What one screen looks like right now, captured on that screen's own tick.</summary>
public class ScreenState
{
    public bool Ready { get; init; }
    /// <summary>The date may already be tomorrow while an overnight event/fade still owns the farmer.</summary>
    public bool Transitioning { get; init; }
    public string? NightEvent { get; init; }
    public string? Name { get; init; }
    public string? Location { get; init; }
    public int[]? Tile { get; init; }
    public int Money { get; init; }
    public int Energy { get; init; }
    public bool InBed { get; init; }
    public int MaxEnergy { get; init; }
    /// <summary>Farm day (1 = the first day), so threads off the game loop can tell when a day changed.</summary>
    public int Day { get; init; }
    public string Time { get; init; } = "";
    public string? Menu { get; init; }
    public string? Dialogue { get; init; }

    /// <summary>The sleep ready check as this screen sees it ("ready/required"), while it is open: why a night does not begin.</summary>
    public string? Sleep { get; init; }
    /// <summary>This screen asks the game to stop the clock (local split-screen stops it once every screen asks).</summary>
    public bool TimePause { get; init; }
    /// <summary>The night as this screen sees it (newDay flag, fade, new-day sync), once one of them is set: where a stalled night waits.</summary>
    public string? Night { get; init; }

    /// <summary>How many gift tastes this player has found out (they are discovered by gifting, not known from the start).</summary>
    public int KnownTastes { get; init; }

    /// <summary>The map this screen shows (unique: every cabin is "Cabin") and the world pixels in view: who sees whom, for say.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Map { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public Microsoft.Xna.Framework.Rectangle View { get; init; }

    /// <summary>This screen shows <paramref name="who"/> (same map, inside the view).</summary>
    public bool Sees(Farmer who) =>
        Map != null && who.currentLocation?.NameOrUniqueName == Map && View.Contains(who.StandingPixel);

    private static string? NightState()
    {
        var sync = Game1.newDaySync;
        bool inSync = sync?.hasInstance() == true;
        if (!Game1.newDay && !Game1.fadeToBlack && !inSync)
            return null;
        return $"newDay:{Game1.newDay} fade:{Game1.fadeToBlack} sync:{(inSync ? (sync!.hasStarted() ? "started" : "created") : "none")}";
    }

    /// <summary>Must run on the game thread during that screen's update.</summary>
    public static ScreenState Capture()
    {
        var menu = Game1.activeClickableMenu;
        if (!Context.IsWorldReady)
            return new ScreenState { Ready = false, Menu = menu?.GetType().Name };
        var p = Game1.player;
        return new ScreenState
        {
            Ready = true,
            Transitioning = Game1.newDay || Game1.fadeToBlack || Game1.isWarping || Game1.farmEvent != null || Game1.game1.isLocalMultiplayerNewDayActive || p.passedOut,
            NightEvent = Game1.farmEvent?.GetType().Name,
            Name = p.Name,
            Location = Game1.currentLocation?.Name,
            Tile = new[] { p.TilePoint.X, p.TilePoint.Y },
            Money = p.Money,
            Energy = (int)p.Stamina,
            InBed = p.isInBed.Value,
            MaxEnergy = p.MaxStamina,
            Day = AgentLog.FarmDay,
            Time = WorldText.FormatTime(Game1.timeOfDay),
            Menu = menu?.GetType().Name,
            Dialogue = menu is StardewValley.Menus.DialogueBox box ? box.characterDialogue?.speaker?.displayName ?? "" : null,
            KnownTastes = NpcKnowledge.Count(p),
            Sleep = menu is StardewValley.Menus.ReadyCheckDialog { checkName: "sleep" } || Context.IsMainPlayer && Game1.netReady.GetNumberRequired("sleep") > 0
                ? $"{Game1.netReady.GetNumberReady("sleep")}/{Game1.netReady.GetNumberRequired("sleep")}{(Game1.netReady.IsReady("sleep") ? " done" : "")}"
                : null,
            TimePause = p.requestingTimePause.Value,
            Night = NightState(),
            Map = Game1.currentLocation?.NameOrUniqueName,
            View = new Microsoft.Xna.Framework.Rectangle(Game1.viewport.X, Game1.viewport.Y, Game1.viewport.Width, Game1.viewport.Height)
        };
    }
}

/// <summary>The four split-screen seats: P1 host, P2/P3 farmhands (AIs), P4 camera.</summary>
public static class Players
{
    public const int Seats = 4;

    public static PlayerSlot[] Slots { get; private set; } = Array.Empty<PlayerSlot>();

    /// <summary>Screens the game should run: P1 plus every seat up to the last one that is not "off". 1 without split-screen.</summary>
    public static int ScreenCount { get; private set; } = 1;

    public static void Init(IModHelper helper, IMonitor monitor, ModConfig config, AgentLog log)
    {
        Slots = Enumerable.Range(0, Seats)
            .Select(i => new PlayerSlot(i, SeatRole.Ai, new AgentRuntime(helper, monitor, config, log.For($"p{i + 1}"))))
            .ToArray();
        Resolve(config, monitor);
    }

    /// <summary>
    /// Seat roles and <see cref="ScreenCount"/> from the config. Runs at startup and again when the host loads a save,
    /// so seat and split-screen changes made in the config menu apply to the next save; the seats keep their runtimes.
    /// </summary>
    public static void Resolve(ModConfig config, IMonitor monitor)
    {
        var roles = new SeatRole[Seats];
        int last = 0;
        for (int i = 0; i < Seats; i++)
        {
            var c = config.Seat(i);
            roles[i] = c.IsOff ? SeatRole.Off : c.IsCamera ? SeatRole.Camera : c.IsHuman ? SeatRole.Human : SeatRole.Ai;
            if (i == 0 && roles[i] is SeatRole.Off or SeatRole.Camera)
                roles[i] = SeatRole.Ai; // P1 is the save's farmer and the host screen; it is always there
            if (i > 0 && roles[i] == SeatRole.Human)
            {
                monitor.Log($"Seats[{i}].Role \"human\" only works for P1 (the keyboard reaches the host screen); P{i + 1} is a camera seat instead.", LogLevel.Warn);
                roles[i] = SeatRole.Camera;
            }
            if (roles[i] != SeatRole.Off) last = i;
        }
        for (int i = 1; i < last; i++)
            if (roles[i] == SeatRole.Off)
            {
                monitor.Log($"Seats[{i}] is \"off\" but a later seat is on; screens are joined in order, so P{i + 1} is a camera seat.", LogLevel.Warn);
                roles[i] = SeatRole.Camera;
            }
        // An operator console is a composed UI region, not another online farmer.
        // Only trailing camera seats can be virtual: real local instances still join in order.
        while (config.OperatorDashboard && last > 0 && roles[last] == SeatRole.Camera) last--;
        ScreenCount = config.SplitScreen ? last + 1 : 1;
        for (int i = 0; i < Seats; i++)
            Slots[i].Role = roles[i];
    }

    /// <summary>The seat of the screen being updated. Only valid on the game thread.</summary>
    public static PlayerSlot Current => Slots[Math.Clamp(Context.ScreenId, 0, Seats - 1)];

    public static PlayerSlot Host => Slots[0];

    /// <summary>Seat by id ("p1".."p4"), or null.</summary>
    public static PlayerSlot? ById(string id) => Slots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>"P1 ai, P2 ai, P3 ai, P4 camera" for the log.</summary>
    public static string Describe() =>
        string.Join(", ", Slots.Take(ScreenCount).Select(s => $"{s.Id} {s.Role.ToString().ToLowerInvariant()}"));

    /// <summary>True while the game runs more than one local screen.</summary>
    public static bool SplitScreen => GameRunner.instance?.gameInstances.Count > 1;
}
