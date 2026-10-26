using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewModdingAPI;

namespace StardewMCP.Agent;

/// <summary>
/// Something a seat noticed. <see cref="Short"/> names it in the line that says why an action was stopped;
/// <see cref="Detail"/>, when set, is also listed in the "Events:" block of the seat's next tool result.
/// </summary>
public sealed record SeenEvent(string Kind, string Short, string? Detail);

/// <summary>What a seat is doing right now, as other players can see it: the running tool, the reason it gave, and when it started.</summary>
public sealed record SeatAction(string Tool, string What, string Reason, int Start);

/// <summary>
/// What the AI farmers perceive of each other, and what stops them mid-action.
/// <list type="bullet">
/// <item><c>look</c> and <c>status</c> list the other players in view with what each is actually doing (running tool, its reason,
/// game minutes so far) next to what it claimed (the last <c>say</c> of it this player heard), so a lie can be caught.</item>
/// <item>Events are queued per seat: an @mention, another player coming into view or entering your house,
/// a theft from you (<see cref="Ownership.Theft"/>), a gift, rain starting, crows on the farm. They are added to the seat's next
/// tool result, and a running walk, wait or farm action is stopped to hand them over at once (<see cref="AgentRuntime.Interrupt"/>).</item>
/// </list>
/// Only AI seats take part; the camera seat (P4) neither sees nor is seen. All screens share one thread, so this state needs no locks.
/// </summary>
public static class Perception
{
    /// <summary>Tiles around a player in which another player counts as "in view" (the default radius of <c>look</c>).</summary>
    public const int ViewRadius = 12;

    private const int SampleEvery = 6, MaxQueued = 20, MaxEventLines = 12;
    // Minimum game minutes between two notices of the same kind about the same thing, so edges of view or flickering weather don't nag.
    private const int ViewGapMinutes = 20, WeatherGapMinutes = 60, CrowGapMinutes = 30;

    /// <summary>
    /// When false events still reach the next tool result but never stop a running action. The smoke runner switches it off
    /// for the cases that walk around next to other seats; it is back on whenever another save is loaded.
    /// </summary>
    public static bool Interrupts { get; set; } = true;

    // Tools that may be stopped, and only where nothing is half done (SafeToStop): a wait at any time, a rest once in bed,
    // go_to_bed only while it waits in bed for the others (the game lets a sleeper cancel then), the others while the farmer walks.
    private static readonly HashSet<string> Stoppable = new()
    {
        "walk_to", "travel_to", "wait", "rest", "go_to_bed", "use_tool", "plant", "harvest", "interact", "talk_to", "ship"
    };

    private static readonly ConcurrentQueue<SeenEvent>[] Queues = Enumerable.Range(0, Players.Seats).Select(_ => new ConcurrentQueue<SeenEvent>()).ToArray();
    private static readonly SeatAction?[] Acts = new SeatAction?[Players.Seats];
    private static readonly PendingCall?[] Tracked = new PendingCall?[Players.Seats];
    // [listener, speaker]: the last line the listener heard the speaker say (say only reaches players who see the speaker).
    private static readonly (int day, string time, string text)?[,] LastHeard = new (int, string, string)?[Players.Seats, Players.Seats];
    private static readonly int[] Ticks = new int[Players.Seats];

    private sealed class Pair
    {
        public bool Known, InView, InHome;
        public int LastNotice = int.MinValue / 2;
    }

    private sealed class Mood
    {
        public GameLocation? Loc;
        public string? Weather;
        public int WeatherDay = -1, CrowDay = -1;
        public int Crows = -1, LastWeather = int.MinValue / 2, LastCrows = int.MinValue / 2;
    }

    private static readonly Dictionary<(int me, int other), Pair> Pairs = new();
    private static readonly Mood[] Moods = Enumerable.Range(0, Players.Seats).Select(_ => new Mood()).ToArray();
    private static string? _saveKey;

    public static void Init() => Ownership.Theft += OnTheft;

    // ------------------------------------------------------------------ small helpers

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Game minutes on one rising scale (days apart by 1440), for gaps and "how long".</summary>
    private static int Stamp() => AgentLog.FarmDay * 1440 + Utility.ConvertTimeToMinutes(Game1.timeOfDay);

    private static string Hhmm() => WorldText.FormatTime(Game1.timeOfDay);

    /// <summary>The AI seats other than <paramref name="me"/> that are in the game. The camera seat never counts.</summary>
    private static IEnumerable<PlayerSlot> Others(PlayerSlot me) =>
        Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != me.Id && s.Active && SocialTools.NameOf(s) != null);

    /// <summary>Other AI seats on the screen's current map within <paramref name="radius"/> tiles, with their (synced copy of the) farmer.</summary>
    private static IEnumerable<(PlayerSlot seat, Farmer farmer, int dist)> Visible(PlayerSlot me, int radius)
    {
        var here = Game1.currentLocation;
        if (here == null) yield break;
        var mine = Game1.player.TilePoint;
        var online = Game1.getOnlineFarmers();
        foreach (var s in Others(me).ToList())
        {
            var f = online.FirstOrDefault(x => x.Name == SocialTools.NameOf(s));
            if (f?.currentLocation == null || f.currentLocation.NameOrUniqueName != here.NameOrUniqueName) continue;
            var t = f.TilePoint;
            int d = Math.Max(Math.Abs(t.X - mine.X), Math.Abs(t.Y - mine.Y));
            if (d <= radius) yield return (s, f, d);
        }
    }

    // ------------------------------------------------------------------ what a seat is doing, and what it claimed

    /// <summary>"walk_to (12,5)", "wait 120 min (standing still)": the running tool with its main argument.</summary>
    private static string DescribeCall(PendingCall call)
    {
        var a = call.Args;
        int Tiles() => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("tiles", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
        var name = call.Tool.Name;
        return name switch
        {
            "walk_to" => $"walk_to ({AgentTools.I(a, "x", -1)},{AgentTools.I(a, "y", -1)})",
            "travel_to" => $"travel_to {AgentTools.S(a, "location")}",
            "wait" => $"wait {Math.Clamp(AgentTools.I(a, "minutes", 30), 10, 240)} min (standing still)",
            "use_tool" => $"use_tool {AgentTools.S(a, "tool")} on {Tiles()} tile(s)",
            "plant" => $"plant {AgentTools.S(a, "item")} on {Tiles()} tile(s)",
            "harvest" => $"harvest {Tiles()} tile(s)",
            "talk_to" => $"talk_to {AgentTools.S(a, "name")}",
            "ship" => $"ship {AgentTools.S(a, "item")}",
            "interact" => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("x", out _) ? $"interact ({AgentTools.I(a, "x", -1)},{AgentTools.I(a, "y", -1)})" : "interact",
            _ => name
        };
    }

    /// <summary>Follow the seat's action lane (called every tick on the seat's own screen): remember what runs and since when.</summary>
    private static void TrackAction(PlayerSlot seat, AgentRuntime rt)
    {
        var call = rt.CurrentCall;
        if (call is { Instant: true }) call = null;
        if (ReferenceEquals(call, Tracked[seat.Screen])) return;
        Tracked[seat.Screen] = call;
        Acts[seat.Screen] = call == null ? null : new SeatAction(call.Tool.Name, DescribeCall(call), Clean(AgentTools.Reason(call.Args)), Stamp());
    }

    /// <summary>What <paramref name="s"/> is actually doing, in words an observer can compare with its claims.</summary>
    private static string DoingText(PlayerSlot s)
    {
        if (!s.Connected) return "idle (no AI is attached to this player)";
        if (Acts[s.Screen] is not { } act) return "between actions (thinking; no action running)";
        int minutes = Stamp() - act.Start;
        var span = minutes < 10 ? "just started" : minutes > 1200 ? "since an earlier day" : $"for {minutes} game min";
        var reason = act.Reason.Length > 0 ? $", reason given: \"{Clip(act.Reason, 80)}\"" : "";
        return $"{act.What} ({span}{reason})";
    }

    /// <summary>What <paramref name="me"/> last heard <paramref name="s"/> say: the claim a lie would contradict.</summary>
    private static string ClaimsText(PlayerSlot me, PlayerSlot s) =>
        LastHeard[me.Screen, s.Screen] is { } said
            ? $"said {(said.day == AgentLog.FarmDay ? "at " + said.time : "on day " + said.day)} \"{Clip(said.text, 100)}\""
            : "none yet (you have not heard them say anything)";

    private static string Describe(PlayerSlot me, PlayerSlot s, Farmer f, int dist) =>
        $"{SocialTools.Label(s)} at ({f.TilePoint.X},{f.TilePoint.Y}), {dist} tile(s) away. Actually doing: {DoingText(s)}. Claims: {ClaimsText(me, s)}.";

    /// <summary>
    /// Block for <c>look</c> and <c>status</c>: every other AI player within <paramref name="radius"/> tiles on this map, what it is
    /// actually doing and what it claimed. Empty when nobody is in view. Game thread, on the seat's own screen.
    /// </summary>
    public static string InViewText(int radius)
    {
        var me = Players.Current;
        if (me.Role != SeatRole.Ai) return "";
        var sb = new StringBuilder();
        foreach (var (seat, farmer, dist) in Visible(me, radius).OrderBy(v => v.dist))
            sb.Append("\n  ").Append(Describe(me, seat, farmer, dist));
        return sb.Length == 0 ? "" : "Other players in view (what each is actually doing, next to what it claimed):" + sb;
    }

    // ------------------------------------------------------------------ events

    /// <summary>Queue an event for a seat. It rides on the seat's next tool result and can stop its running action.</summary>
    public static void Notify(PlayerSlot seat, string kind, string shortText, string? detail = null)
    {
        if (seat.Role != SeatRole.Ai) return;
        var q = Queues[seat.Screen];
        q.Enqueue(new SeenEvent(kind, shortText, detail));
        while (q.Count > MaxQueued) q.TryDequeue(out _);
    }

    /// <summary>The seat's events for its tool result ("" when none), then the chat it heard. Replaces <see cref="SocialTools.TakeMail"/> in the runtime.</summary>
    public static string TakeMail(string player)
    {
        var mail = SocialTools.TakeMail(player);
        if (Players.ById(player) is not { } seat) return mail;
        var lines = new List<string>();
        var q = Queues[seat.Screen];
        while (q.TryDequeue(out var e))
            if (e.Detail != null) lines.Add(e.Detail);
        if (lines.Count == 0) return mail;

        var sb = new StringBuilder("Events:");
        if (lines.Count > MaxEventLines) sb.Append($"\n  ({lines.Count - MaxEventLines} older not shown)");
        foreach (var l in lines.TakeLast(MaxEventLines)) sb.Append("\n  - ").Append(l);
        return mail.Length > 0 ? sb.ToString() + "\n" + mail : sb.ToString();
    }

    /// <summary>Drop every seat's pending events and the memory of who was in view (debug op, and when another save loads).</summary>
    public static void Clear()
    {
        foreach (var q in Queues)
            while (q.TryDequeue(out _)) { }
        Pairs.Clear();
        System.Array.Clear(LastHeard);
        foreach (var m in Moods)
        {
            m.Loc = null;
            m.Weather = null;
            m.Crows = -1;
            m.WeatherDay = m.CrowDay = -1;
        }
    }

    // ------------------------------------------------------------------ sources fed by the social tools

    /// <summary>A seat spoke in chat: remember it as its latest claim for those who heard it, and interrupt whoever it addressed with @name or @pN.</summary>
    public static void OnSay(PlayerSlot speaker, string text, IEnumerable<PlayerSlot> heard)
    {
        foreach (var s in heard)
        {
            LastHeard[s.Screen, speaker.Screen] = (AgentLog.FarmDay, Hhmm(), text);
            if (Mentions(text, s))
                Notify(s, "mention", $"{SocialTools.Label(speaker)} mentioned you in chat");
        }
    }

    /// <summary>"@Gemini", "@gemini," or "@p2" in a chat line (not part of a longer word).</summary>
    private static bool Mentions(string text, PlayerSlot s)
    {
        var names = new List<string> { s.Id };
        if (SocialTools.NameOf(s) is { Length: > 0 } name) names.Add(name);
        var pattern = @"(?<![\p{L}\p{N}_])@(?:" + string.Join("|", names.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void OnTheft(Ownership.TheftEvent ev)
    {
        if (Players.ById(ev.Owner) is not { } victim) return;
        var who = Players.ById(ev.Taker) is { } thief ? SocialTools.Label(thief) : ev.Taker;
        Notify(victim, "theft", $"{who} took {ev.What} from your {ev.Thing}",
            $"[{ev.Time}] {who} took {ev.What} from your {ev.Thing} at ({ev.X},{ev.Y}) on {ev.Location}.");
    }

    // ------------------------------------------------------------------ per tick

    /// <summary>
    /// Called every tick on the seat's own screen (from <see cref="AgentRuntime.Tick"/>): follow what the seat is doing, look around
    /// a few times a second, and stop its running action when something it must know about has happened.
    /// </summary>
    public static void Tick(AgentRuntime rt)
    {
        if (Players.ById(rt.Log.Player) is not { Role: SeatRole.Ai } seat || !Context.IsWorldReady) return;
        SyncSave();
        TrackAction(seat, rt);
        if (++Ticks[seat.Screen] % SampleEvery == 0)
        {
            SamplePlayers(seat);
            SampleWeather(seat);
            SampleCrows(seat);
        }
        ReplyInTime(seat, rt);
        TryInterrupt(seat, rt);
    }

    /// <summary>A different save (the smoke runner's copy, then the real one) starts from a clean slate.</summary>
    internal static void SyncSave()
    {
        var save = SaveFolder.Name;
        if (save == _saveKey) return;
        _saveKey = save;
        Interrupts = true;
        Clear();
    }

    private static void SamplePlayers(PlayerSlot me)
    {
        var here = Game1.currentLocation;
        if (here == null) return;
        var online = Game1.getOnlineFarmers();
        var home = Game1.player.homeLocation.Value;
        var mine = Game1.player.TilePoint;
        foreach (var s in Others(me).ToList())
        {
            var f = online.FirstOrDefault(x => x.Name == SocialTools.NameOf(s));
            if (f?.currentLocation == null) continue;
            var there = f.currentLocation.NameOrUniqueName;
            var t = f.TilePoint;
            bool inHome = !string.IsNullOrEmpty(home) && there == home;
            bool inView = there == here.NameOrUniqueName && Math.Max(Math.Abs(t.X - mine.X), Math.Abs(t.Y - mine.Y)) <= ViewRadius;

            var key = (me.Screen, s.Screen);
            if (!Pairs.TryGetValue(key, out var st)) Pairs[key] = st = new Pair();
            // The first look only sets the baseline, so nobody "arrives" just because the game started with them nearby.
            bool enteredHome = st.Known && inHome && !st.InHome;
            bool cameInView = st.Known && inView && !st.InView;
            st.Known = true;
            st.InHome = inHome;
            st.InView = inView;
            if (!(enteredHome || cameInView) || Stamp() - st.LastNotice < ViewGapMinutes) continue;
            st.LastNotice = Stamp();

            var label = SocialTools.Label(s);
            string shortText, detail;
            if (enteredHome)
            {
                var whereYouAre = there == here.NameOrUniqueName ? "right where you are" : $"while you are at {here.Name} ({mine.X},{mine.Y})";
                shortText = $"{label} entered your house";
                detail = $"[{Hhmm()}] {label} entered your house ({f.currentLocation.Name}, at ({t.X},{t.Y})), {whereYouAre}. " +
                         $"Actually doing: {DoingText(s)}. Claims: {ClaimsText(me, s)}.";
            }
            else
            {
                var dist = Math.Max(Math.Abs(t.X - mine.X), Math.Abs(t.Y - mine.Y));
                shortText = $"{label} came into view";
                detail = $"[{Hhmm()}] {label} came into view at ({t.X},{t.Y}), {dist} tile(s) away. " +
                         $"Actually doing: {DoingText(s)}. Claims: {ClaimsText(me, s)}.";
            }
            // Both on their way to bed: not news, and waking each other right then left the night unable to begin (days 6-7, 2026-10-20).
            bool bothToBed = me.Runtime.CurrentTool == "go_to_bed" && Acts[s.Screen]?.Tool == "go_to_bed";
            if (!bothToBed)
                Notify(me, enteredHome ? "house" : "arrived", shortText, detail);
            me.Runtime.Log.Add("notice", text: detail, args: new { kind = enteredHome ? "house" : "arrived", who = s.Id, quiet = bothToBed ? true : (bool?)null });
        }
    }

    /// <summary>A day change, fade or warp: the world is being rebuilt, so what it looks like now is not news.</summary>
    private static bool InTransition() => Game1.newDay || Game1.fadeToBlack || Game1.isWarping;

    /// <summary>Rain, a storm or snow starting. Weather is read as <c>status</c> does; a change across a map change or a new day is ignored.</summary>
    private static void SampleWeather(PlayerSlot me)
    {
        var mood = Moods[me.Screen];
        if (InTransition())
        {
            mood.Loc = null; // start again from what the weather is once the fade or warp is over
            return;
        }
        var loc = Game1.currentLocation;
        var now = WorldText.Weather();
        var was = mood.Weather;
        bool sameMap = ReferenceEquals(mood.Loc, loc) && mood.WeatherDay == Game1.Date.TotalDays;
        mood.Loc = loc;
        mood.WeatherDay = Game1.Date.TotalDays;
        mood.Weather = now;
        if (was == null || !sameMap || was == now || now is not ("rain" or "storm" or "snow")) return;
        if (Stamp() - mood.LastWeather < WeatherGapMinutes) return;
        mood.LastWeather = Stamp();

        var note = now == "snow" ? "" : " Rain waters your crops for you.";
        var detail = $"[{Hhmm()}] The weather changed: {was} -> {now}.{note}";
        Notify(me, "weather", $"it started to {(now == "snow" ? "snow" : now == "storm" ? "storm" : "rain")}", detail);
        me.Runtime.Log.Add("notice", text: detail, args: new { kind = "weather", weather = now });
    }

    /// <summary>
    /// Crows landing on the farm, told to the seats that have crops there (a scarecrow nearby keeps them off). Crows are counted
    /// among the farm's critters by type name; a count that rises during the day is a landing, one found at a new day is not.
    /// </summary>
    private static void SampleCrows(PlayerSlot me)
    {
        var mood = Moods[me.Screen];
        var farm = Game1.getFarm();
        if (farm == null) return;
        if (InTransition())
        {
            mood.Crows = -1;
            return;
        }
        int crows = farm.critters?.Count(c => c.GetType().Name == "Crow") ?? 0;
        int was = mood.Crows;
        bool sameDay = mood.CrowDay == Game1.Date.TotalDays;
        mood.Crows = crows;
        mood.CrowDay = Game1.Date.TotalDays;
        if (was < 0 || !sameDay || crows <= was || Stamp() - mood.LastCrows < CrowGapMinutes) return;
        if (!OwnsCrops(me, farm)) return;
        mood.LastCrows = Stamp();

        var detail = $"[{Hhmm()}] {crows} crow(s) landed on the Farm. They eat crops, unless a scarecrow stands near them; check your plot.";
        Notify(me, "crows", "crows landed on the farm", detail);
        me.Runtime.Log.Add("notice", text: detail, args: new { kind = "crows", crows });
    }

    private static bool OwnsCrops(PlayerSlot me, GameLocation farm)
    {
        foreach (var pair in farm.terrainFeatures.Pairs)
            if (pair.Value is HoeDirt dirt && dirt.crop != null && Ownership.OwnerOf(dirt) == me.Id)
                return true;
        return false;
    }

    // ------------------------------------------------------------------ interrupting

    /// <summary>In bed waiting for the others: the sleep ready check is open and still cancelable (not once the night began).</summary>
    private static bool Asleep(PendingCall call, AgentRuntime rt) =>
        call.Tool.Name == "go_to_bed" && !rt.WakingUp &&
        Game1.activeClickableMenu is StardewValley.Menus.ReadyCheckDialog { checkName: "sleep" } check && check.isCancelable();

    /// <summary>
    /// Whether the running action may be stopped right now with nothing half done: never mid-swing, in a menu (but a sleeper's
    /// ready check), during a warp, fade or cutscene. A wait, a rest once in bed and a sleeper stand still and stop anywhere;
    /// everything else only while its farmer walks between two steps of the job, on a tile of its own. go_to_bed's walk home is
    /// stopped only for a client deadline (<paramref name="bedWalk"/>), never by an event.
    /// </summary>
    private static bool SafeToStop(PendingCall call, bool asleep, bool bedWalk = false)
    {
        var name = call.Tool.Name;
        if (!Stoppable.Contains(name) || (name == "go_to_bed" && !asleep && !bedWalk)) return false;
        var p = Game1.player;
        if ((Game1.activeClickableMenu != null && !asleep) || Game1.isWarping || Game1.fadeToBlack || Game1.newDay || Game1.eventUp || p.UsingTool)
            return false;
        if (asleep || name == "wait" || (name == "rest" && p.isInBed.Value)) return true;
        // farmers don't collide: stopping on another's tile stacks them, so the walk goes on to the next free tile first
        return p.controller != null &&
               !Game1.getOnlineFarmers().Any(f => f != p && f.currentLocation == p.currentLocation && f.TilePoint == p.TilePoint);
    }

    /// <summary>Halt the farmer's walk and end its running action with <paramref name="text"/> as the result.</summary>
    private static bool Stop(AgentRuntime rt, string text)
    {
        var p = Game1.player;
        if (p.controller != null)
        {
            p.controller = null;
            p.Halt();
        }
        return rt.Interrupt(text);
    }

    private static string Where() => $"{Game1.currentLocation?.Name} ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y})";

    /// <summary>
    /// A client that gives up on a call after a few minutes (agy: 3, 2026-10-20) is answered before that. A sleeper is told
    /// its day is over while go_to_bed keeps running, so it stays in bed; any other action stops at its next safe point;
    /// a call that never started (behind another action, or held by a pause) is answered and dropped.
    /// </summary>
    private static void ReplyInTime(PlayerSlot seat, AgentRuntime rt)
    {
        int limit = seat.ReplyWithinSeconds;
        if (limit <= 0) return;
        var call = rt.CurrentCall;
        if (call is { Instant: false, Tool.Name: "go_to_bed" } && call.Done.Task.IsCompleted && !rt.WakingUp)
        {
            // told its day is over, the client went on anyway: those calls would otherwise run on tomorrow's farmer
            if (rt.AnswerQueued("You are asleep in bed and your day is over: the night begins when everyone is in bed. Stop here.") > 0)
                rt.Log.Add("system", text: "Refused a call sent after go_to_bed was answered: asleep");
        }
        else if (rt.AnswerQueued(RunControl.Holding ? "The game is paused, so this call never started. Call it again."
                                     : $"This call never started: your {rt.CurrentTool ?? "earlier action"} was still running. Call it again.",
                                 limit) > 0)
            rt.Log.Add("system", text: $"Answered calls that waited {limit}s without starting");

        if (call == null || call.Instant || call.Done.Task.IsCompleted || (DateTime.UtcNow - call.Queued).TotalSeconds < limit) return;
        if (Asleep(call, rt))
        {
            call.Done.TrySetResult((true, "You are asleep in bed, waiting for the others to go to bed too; the night begins " +
                                          "when everyone is in bed. Your day is over: stop here."));
            rt.Log.Add("system", text: "Answered go_to_bed early: asleep, waiting for the others");
            return;
        }
        if (!SafeToStop(call, asleep: false, bedWalk: true)) return; // runs to its end; the 6-minute action timeout still applies
        var where = Where();
        if (Stop(rt, $"Paused: your {call.Tool.Name} ran long and was stopped at {where} at {WorldText.FormatTime(Game1.timeOfDay)}; " +
                     "nothing happened to you. Call it again to go on, or decide something else."))
            rt.Log.Add("interrupt", call.Tool.Name, "reply_within", args: new { seconds = limit });
    }

    /// <summary>
    /// Stop the seat's running walk, wait or farm action when it has unseen events (see <see cref="SafeToStop"/>). A call
    /// already answered (<see cref="ReplyInTime"/>) runs to its end: nobody is left to read why it stopped.
    /// </summary>
    private static void TryInterrupt(PlayerSlot seat, AgentRuntime rt)
    {
        var q = Queues[seat.Screen];
        if (!Interrupts || q.IsEmpty) return;
        var call = rt.CurrentCall;
        if (call == null || call.Instant || call.Done.Task.IsCompleted) return;
        // Speech/mentions and ambient events stay queued until morning. Never cancel a
        // committed sleep check to deliver social mail: another screen may already be fading.
        if (!DaySafety.CanInterruptForSocialEvent(call.Tool.Name)) return;
        // in bed waiting for the others: the sleep ready check is open and still cancelable (not once the night began)
        bool asleep = Asleep(call, rt);
        if (!SafeToStop(call, asleep)) return;

        var why = string.Join("; ", q.Select(e => e.Short).Distinct().Take(4));
        var tool = call.Tool.Name;
        var text = asleep
            ? $"Woken up: you got out of bed at {WorldText.FormatTime(Game1.timeOfDay)} because {why}. The day goes on; " +
              "call go_to_bed again when you want to sleep."
            : $"Interrupted: your {tool} was stopped at {Where()} before it finished, because {why}. " +
              "Its normal result is lost; use look or inventory to see how far it got, then decide what to do.";
        if (Stop(rt, text))
            rt.Log.Add("interrupt", tool, why, args: new { events = q.Select(e => e.Kind).Distinct().ToArray() });
    }
}
