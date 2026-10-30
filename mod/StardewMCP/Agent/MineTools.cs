using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Mine survival (#6): leave_mine, the damage interrupt the runtime applies to every long action, and the mine
/// parts of the footer (health, late-hour warning). The rules for when to retreat live in <see cref="IsHealthLow"/>
/// (combat), so fight and leave_mine agree on what "hurt" means.
/// </summary>
public static partial class AgentTools
{
    /// <summary>Tile index of the up ladder on a mine floor's "Buildings" layer (WorldText names it in look).</summary>
    internal const int MineLadderUpTile = 115;

    /// <summary>From this clock time (11 PM) the mines are too late: the footer warns and leave_mine stops climbing.</summary>
    internal const int MineLateTime = 2300;

    /// <summary>Mine level 121 is Skull Cavern floor 1; its up ladder exits at the Skull Cave entrance, not at the Mine.</summary>
    internal const int SkullCavernFirstFloor = 121;

    /// <summary>A monster this close (in tiles) when health drops interrupts a running action.</summary>
    internal const float DamageInterruptTiles = 3f;

    /// <summary>Game minutes leave_mine may spend climbing ladders before it gives up and warps out.</summary>
    private const int LeaveMineClimbMinutes = 60;

    /// <summary>Where the Mine entrance hall is entered from the elevator side (the same spot the elevator drops you at).</summary>
    private static readonly Point MineEntranceTile = new(17, 4);

    private static ToolDef LeaveMineTool() => new()
    {
        Name = "leave_mine",
        Description = "Get out of the Mines from any mine floor. You walk to the up ladder and choose Leave, which takes you out to the Mine entrance. " +
                      "In the Skull Cavern (mine level 121 and above) the same ladder takes you to the Skull Cave entrance in Calico Desert; the fallback warp goes there too. " +
                      "If the ladder cannot be found or reached, your health is low (30% of maximum or less), it is 11 PM or later, or the climb takes over an hour of game time, you are warped out instead. " +
                      "The result says which floor you reached, the time and your health. Use it before health runs out, when you have no food left or when it gets late.",
        Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
        Run = LeaveMine
    };

    /// <summary>Buildings-layer tiles of a mine floor that lead down: the ladder (index 173) and the shaft (174, Skull Cavern-style drop).</summary>
    private const int MineLadderDownTile = 173, MineShaftDownTile = 174;

    /// <summary>The mine tool starts no new floor after 22:00, so leave_mine still has time before the 02:00 pass-out.</summary>
    private const int MineStopTime = 2200;

    private const int MineMinEnergy = 10;
    private const int MineRocksPerFloor = 80;

    private static ToolDef MineTool() => new()
    {
        Name = "mine",
        Description = "Go down the Mines (or the Skull Cavern) floor by floor. Before going down you first store everything in your own chest at home except tools, weapons, " +
                      "bombs, staircases and food, so the pack has room. Then you walk to the Mine and take the elevator to the deepest floor you reached (a multiple of 5). " +
                      "On each floor you pick up quartz, crystals and forage lying on the floor, break rocks with the Pickaxe until a ladder shows up, take it, " +
                      "fight monsters that come close (fight, eating below 40% health), and open treasure chests. Ores, gems, geodes and coal land in your pack; " +
                      "once it is full you keep going down and new drops stay on the floor. floors: how many floors to go down this call (default 5). " +
                      "Stops early at 22:00, when energy or health runs low or on floor 120 (the bottom). Then carry on with mine, or leave_mine.",
        Schema = Obj(new { floors = Int("How many floors to go down, 1-20 (default 5)"), reason = Str(ReasonDesc) }, "reason"),
        Run = Mine
    };

    private static IEnumerator Mine(JsonElement a, CallContext c)
    {
        int want = Math.Clamp(I(a, "floors", 5), 1, 20);
        var fetched = new List<string>();
        if (Loc is not MineShaft && !HasMainTool(PickaxeKind)) yield return EnsureMainTool(PickaxeKind, fetched);
        int pick = FindItem("Pickaxe", it => it is StardewValley.Tools.Pickaxe);
        if (pick < 0) { c.Fail(Reasons.NoItem, "You have no Pickaxe; rocks and ladders need one. " + string.Join(" ", fetched)); yield break; }
        if (Game1.timeOfDay >= MineStopTime) { c.Fail($"It is {WorldText.FormatTime(Game1.timeOfDay)}: too late to start down the mines (stop at {WorldText.FormatTime(MineStopTime)})."); yield break; }
        if (IsHealthLow(P)) { c.Fail($"Health {P.health}/{P.maxHealth} is too low for the mines. Eat first."); yield break; }
        if (P.Stamina <= MineMinEnergy) { c.Fail(Reasons.LowEnergy, $"Energy {(int)P.Stamina} is too low for the mines. Eat or rest first."); yield break; }

        var before = Snapshot();
        int deepestBefore = P.deepestMineLevel;
        uint killsBefore = P.stats.MonstersKilled;
        var lines = new List<string>(fetched);

        if (Loc is not MineShaft)
        {
            // Days 59-119: the pack filled with crops and seeds and stopped the mine on the first floor 15 times. Everything but
            // the mine kit goes into your own chest first (not from the Mine entrance when the pack still has room: the walk home costs hours).
            if (MineStashCount() > 0 && (Loc.Name != "Mine" || FreeSlots() < MineStashFreeSlots))
            {
                var stash = new CallContext();
                yield return StashForMine(stash);
                lines.Add(stash.Result);
                before = Snapshot(); // the stored stacks are not losses of the trip
            }
            if (Loc.Name != "Mine")
            {
                var trip = new CallContext();
                yield return TravelTo("Mine", trip);
                if (!trip.Ok || Loc.Name != "Mine") { c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get to the Mine: {trip.Result}"); yield break; }
            }
            int start = P.deepestMineLevel >= 5 ? Math.Min(120, P.deepestMineLevel / 5 * 5) : 1;
            // Walk to the elevator like a player; it is the game's own way down (the elevator menu calls Game1.enterMine).
            var lift = FindActionTiles(Loc, s => s.Contains("MineElevator", StringComparison.OrdinalIgnoreCase));
            if (lift.Count > 0) yield return WalkAdjacent(lift[0], new CallContext());
            Game1.enterMine(start, null);
            for (int i = 0; i < 300 && Loc is not MineShaft; i++) yield return null;
            yield return WaitIdle();
            if (Loc is not MineShaft) { c.Fail("The way down did not open (the mines may still be closed: they open on Spring 5)."); yield break; }
            lines.Add($"took the {(start > 1 ? "elevator" : "ladder")} to floor {start}");
        }

        int floors = 0, rocks = 0, chests = 0, picked = 0;
        string stop = "";
        bool fullNoted = false;
        var skip = new HashSet<Point>();
        int floorRocks = 0, fights = 0, lastLevel = (Loc as MineShaft)!.mineLevel;
        while (floors < want)
        {
            if (Loc is not MineShaft shaft) { stop = "you left the mines"; break; }
            if (shaft.mineLevel != lastLevel) { lastLevel = shaft.mineLevel; skip.Clear(); floorRocks = 0; fights = 0; }
            if (Game1.activeClickableMenu != null) yield return FinishMessages(new CallContext());
            if (Game1.activeClickableMenu != null) { stop = "a menu is open"; break; }
            if (Game1.timeOfDay >= MineStopTime) { stop = $"it is {WorldText.FormatTime(Game1.timeOfDay)}"; break; }
            if (P.Stamina <= MineMinEnergy) { stop = $"energy is low ({(int)P.Stamina})"; break; }
            if (IsHealthLow(P)) { stop = $"health is low ({P.health}/{P.maxHealth})"; break; }
            // A full pack no longer ends the trip: ladders still need rocks broken, the drops just stay on the floor.
            if (FreeSlots() == 0 && !fullNoted)
            {
                fullNoted = true;
                lines.Add($"your pack filled up on floor {shaft.mineLevel}: going on down, new drops stay on the floor");
            }

            // Monsters next to you first: fight eats when health drops.
            if (NearestMonster(shaft, P.TilePoint, 4f) != null && fights < 6)
            {
                fights++;
                yield return Fight(ArgsOf(new { seconds = 10, eat_below = 40, reason = "monsters close by in the mine" }), new CallContext());
                continue;
            }

            // Treasure chests (floors 10, 20, ...): open them like a player.
            var chest = shaft.Objects.Pairs.FirstOrDefault(kv => kv.Value is StardewValley.Objects.Chest ch && !ch.playerChest.Value && !skip.Contains(kv.Key.ToPoint()));
            if (chest.Value != null)
            {
                var at = chest.Key.ToPoint();
                skip.Add(at);
                var walk = new CallContext();
                yield return WalkAdjacent(at, walk);
                if (walk.Ok) { PressAction(at); yield return Ticks(60); yield return FinishMessages(new CallContext()); chests++; }
                continue;
            }

            // Quartz, crystals, cave carrots and mushrooms lying on the floor: pick them up like a player (action button).
            var loose = FreeSlots() > 0 ? NearestLooseItem(shaft, skip) : null;
            if (loose is { } item)
            {
                skip.Add(item);
                var walk = new CallContext();
                yield return WalkAdjacent(item, walk);
                if (walk.Ok)
                {
                    PressAction(item);
                    yield return Ticks(20);
                    if (!shaft.Objects.ContainsKey(item.ToVector2())) picked++;
                }
                continue;
            }

            // A way down: the ladder, or a shaft (jump in).
            var down = FindDownTile(shaft, skip);
            if (down is { } d)
            {
                var walk = new CallContext();
                yield return WalkAdjacent(d.tile, walk);
                if (!walk.Ok) { skip.Add(d.tile); continue; }
                int level = shaft.mineLevel;
                PressAction(d.tile);
                if (d.shaft) yield return AnswerQuestion("Jump", new CallContext());
                for (int i = 0; i < 300 && Loc == shaft; i++) yield return null;
                yield return WaitIdle();
                if (Loc is MineShaft next && next.mineLevel != level)
                {
                    floors++;
                    if (d.shaft) lines.Add($"jumped down a shaft to floor {next.mineLevel}");
                }
                else skip.Add(d.tile);
                continue;
            }
            if (shaft.mineLevel == 120) { stop = "floor 120 is the bottom of the Mines (The Bottom); the Skull Cavern (Skull Key) goes deeper"; break; }

            // Break the nearest rock: each one may hide the ladder.
            var stone = NearestStone(shaft, skip);
            if (stone == null || floorRocks >= MineRocksPerFloor)
            {
                if (NearestMonster(shaft, P.TilePoint, 40f) != null && fights < 6)
                {
                    fights++; // infested floors open the ladder once every monster is gone
                    yield return Fight(ArgsOf(new { seconds = 20, eat_below = 40, reason = "clear the floor so the ladder appears" }), new CallContext());
                    continue;
                }
                stop = $"no ladder and nothing left to break on floor {shaft.mineLevel}; leave_mine and come back tomorrow";
                break;
            }
            var reach = new CallContext();
            yield return WalkAdjacent(stone.Value, reach);
            if (!reach.Ok) { skip.Add(stone.Value); continue; }
            P.CurrentToolIndex = pick;
            for (int hit = 0; hit < 8 && shaft.Objects.TryGetValue(stone.Value.ToVector2(), out var o) && o.IsBreakableStone() && P.Stamina > 2; hit++)
                yield return Swing(stone.Value);
            if (shaft.Objects.ContainsKey(stone.Value.ToVector2())) skip.Add(stone.Value);
            rocks++;
            floorRocks++;
        }
        yield return Ticks(45); // dropped ore flies into the pack

        var here = Loc is MineShaft s2 ? WorldText.MineFloorName(s2.mineLevel) : Loc.Name;
        var sb = new StringBuilder($"Went down {floors} floor(s); you are on {here}. Deepest ever: floor {P.deepestMineLevel}{(P.deepestMineLevel > deepestBefore ? " (new record)" : "")}.");
        if (lines.Count > 0) sb.Append(" ").Append(string.Join("; ", lines)).Append('.');
        sb.Append($" Broke {rocks} rock(s){(chests > 0 ? $", opened {chests} chest(s)" : "")}{(picked > 0 ? $", picked up {picked} item(s) from the floor" : "")}, monsters killed {P.stats.MonstersKilled - killsBefore}.");
        var diff = Diff(before);
        if (diff.Length > 0) sb.Append('\n').Append(diff);
        sb.Append($"\nHP {P.health}/{P.maxHealth}, energy {(int)P.Stamina}, {WorldText.FormatTime(Game1.timeOfDay)}.");
        if (stop.Length > 0) sb.Append($" Stopped: {stop}.");
        sb.Append(Game1.timeOfDay >= MineStopTime || IsHealthLow(P) ? " leave_mine now." : " mine again to go deeper, or leave_mine.");
        if (floors == 0 && stop.Length > 0) c.Fail(sb.ToString()); else c.Result = sb.ToString();
    }

    /// <summary>The nearest ladder or shaft down on this floor that is not in <paramref name="skip"/>.</summary>
    private static (Point tile, bool shaft)? FindDownTile(MineShaft loc, HashSet<Point> skip)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var me = P.TilePoint;
        (Point, bool)? best = null;
        int bestDist = int.MaxValue;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int index = loc.getTileIndexAt(new xTile.Dimensions.Location(x, y), "Buildings", "mine");
            if (index != MineLadderDownTile && index != MineShaftDownTile) continue;
            var p = new Point(x, y);
            if (skip.Contains(p)) continue;
            int dist = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (dist < bestDist) { bestDist = dist; best = (p, index == MineShaftDownTile); }
        }
        return best;
    }

    /// <summary>The nearest item lying on the floor to pick up (quartz, crystals, cave forage: the game's spawned objects) not in <paramref name="skip"/>.</summary>
    private static Point? NearestLooseItem(MineShaft loc, HashSet<Point> skip)
    {
        var me = P.TilePoint;
        return loc.Objects.Pairs
            .Where(kv => kv.Value.IsSpawnedObject && !kv.Value.IsBreakableStone() && !skip.Contains(kv.Key.ToPoint()))
            .Select(kv => kv.Key.ToPoint())
            .OrderBy(p => Math.Abs(p.X - me.X) + Math.Abs(p.Y - me.Y))
            .Select(p => (Point?)p)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------ the stash before going down

    /// <summary>At the Mine entrance the stash trip home is only made when fewer slots than this are free.</summary>
    private const int MineStashFreeSlots = 10;

    /// <summary>Bombs and the Staircase: they make the mines faster, so they stay in the pack.</summary>
    private static readonly HashSet<string> MineKitIds = new() { "(O)286", "(O)287", "(O)288", "(O)71" };

    /// <summary>
    /// What stays in the pack for the mines: tools and weapons, bombs and staircases, cooked dishes and the two best other
    /// foods (fight eats them when health drops). Everything else goes into the chest.
    /// </summary>
    private static HashSet<Item> MineKeeps()
    {
        var items = P.Items.Where(i => i != null).ToList();
        var keep = new HashSet<Item>(items.Where(i => i is Tool || MineKitIds.Contains(i.QualifiedItemId) || i.Category == -7));
        foreach (var food in items.OfType<StardewValley.Object>()
                     .Where(o => !keep.Contains(o) && o.Edibility > 0 && !o.bigCraftable.Value && o.Category != -74)
                     .OrderByDescending(o => o.Edibility).Take(2))
            keep.Add(food);
        return keep;
    }

    private static int MineStashCount()
    {
        var keep = MineKeeps();
        return P.Items.Count(i => i != null && !keep.Contains(i));
    }

    /// <summary>
    /// Walks to your own chest (your house, then the Farm) and stores everything but <see cref="MineKeeps"/>. The result
    /// line says what happened; with no chest of yours it says so and the mine trip goes on.
    /// </summary>
    private static IEnumerator StashForMine(CallContext c)
    {
        string? where = null;
        Point? tile = null;
        foreach (var name in new[] { Loc.NameOrUniqueName, P.homeLocation.Value, "FarmHouse", "Farm" }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            var loc = name == Loc.NameOrUniqueName ? Loc : Game1.getLocationFromName(name);
            if (loc == null || loc is MineShaft || NearestOwnChest(loc) is not { } t) continue;
            where = name;
            tile = t;
            break;
        }
        if (tile == null) { c.Fail("no chest of yours to leave things in before the mines (craft and place a Chest at home)"); yield break; }

        if (Loc.NameOrUniqueName != where)
        {
            var trip = new CallContext();
            yield return TravelTo(where!, trip);
            if (!trip.Ok) { c.Fail($"couldn't get to your chest in {where} before the mines: {trip.Result}"); yield break; }
        }
        var open = new CallContext();
        yield return Interact(ArgsOf(new { x = tile.Value.X, y = tile.Value.Y, reason = "store everything but the mine kit" }), open);
        for (int i = 0; i < 120 && Game1.activeClickableMenu == null; i++) yield return null;
        if (Game1.activeClickableMenu is not ItemGrabMenu { context: StardewValley.Objects.Chest })
        {
            if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
            c.Fail($"your chest at {where} ({tile.Value.X},{tile.Value.Y}) did not open: {open.Result}");
            yield break;
        }
        var keep = MineKeeps();
        var work = new CallContext();
        yield return ChestWork("store", new List<string>(), true, null, null, $" at {where} ({tile.Value.X},{tile.Value.Y})", work, i => keep.Contains(i));
        if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
        if (work.Ok) c.Result = "before the mines: " + work.Result.TrimEnd('.');
        else c.Fail("before the mines: " + work.Result.TrimEnd('.'));
    }

    /// <summary>The nearest breakable rock (ore nodes and gem rocks included) not in <paramref name="skip"/>.</summary>
    private static Point? NearestStone(MineShaft loc, HashSet<Point> skip)
    {
        var me = P.TilePoint;
        Point? best = null;
        int bestDist = int.MaxValue;
        foreach (var kv in loc.Objects.Pairs)
        {
            if (!kv.Value.IsBreakableStone()) continue;
            var p = kv.Key.ToPoint();
            if (skip.Contains(p)) continue;
            int dist = Math.Abs(p.X - me.X) + Math.Abs(p.Y - me.Y);
            if (dist < bestDist) { bestDist = dist; best = p; }
        }
        return best;
    }

    // ------------------------------------------------------------------ footer

    /// <summary>" · HP x/y" in the footer while in the mines (status shows health everywhere else).</summary>
    internal static string MineHealthFooter() => Loc is MineShaft ? $" · HP {P.health}/{P.maxHealth}" : "";

    /// <summary>A footer line when it is 11 PM or later and the farmer is still in the mines; passing out there costs money and items.</summary>
    internal static string MineLateWarning() =>
        Loc is MineShaft && Game1.timeOfDay >= MineLateTime ? "\n⚠ late in the mines: leave_mine now" : "";

    // ------------------------------------------------------------------ damage interrupt (called by AgentRuntime)

    /// <summary>Tools that deal with damage themselves: fight and mine handle health, leave_mine is the retreat, eat is the cure.</summary>
    internal static bool IgnoresDamageInterrupt(string tool) => tool is "fight" or "mine" or "leave_mine" or "eat";

    /// <summary>
    /// Runs once per tick while a (non-observing) action runs on this screen. When health dropped since the last call
    /// and a monster is within <see cref="DamageInterruptTiles"/>, stops the farmer and returns the text that ends the
    /// call early; otherwise null. <paramref name="lastHealth"/> is the caller's memory of the previous tick.
    /// </summary>
    internal static string? DamageInterrupt(string tool, ref int lastHealth)
    {
        int before = lastHealth;
        lastHealth = P.health;
        if (P.health >= before || IgnoresDamageInterrupt(tool) || Loc == null) return null;

        var mon = NearestMonster(Loc, P.TilePoint, DamageInterruptTiles);
        if (mon == null) return null;

        // The walk or swing that was running must not carry on when the AI's next call starts.
        P.controller = null;
        if (!P.UsingTool) P.Halt();
        var advice = Loc is MineShaft ? "fight it, eat, or leave_mine" : "fight it or get away";
        return $"Interrupted: {mon.Name} is attacking you (HP {P.health}/{P.maxHealth}). Your action was stopped part-way; {advice}.";
    }

    // ------------------------------------------------------------------ leave_mine

    private static IEnumerator LeaveMine(JsonElement a, CallContext c)
    {
        if (Loc is not MineShaft startShaft)
        {
            c.Fail($"You are not on a mine floor (you are in {Loc?.Name}). leave_mine only works inside the Mines; use travel_to to go elsewhere.");
            yield break;
        }

        int startFloor = startShaft.mineLevel;
        int startMinutes = Utility.ConvertTimeToMinutes(Game1.timeOfDay);
        int day = Game1.Date.TotalDays;
        int climbed = 0;
        string warpedBecause = "";
        string stopped = "";

        yield return WaitIdle();
        while (Loc is MineShaft && Game1.Date.TotalDays == day)
        {
            if (Game1.activeClickableMenu != null) { stopped = "a menu or dialogue opened"; break; }

            string rush = "";
            if (IsHealthLow(P)) rush = $"health {P.health}/{P.maxHealth} is low";
            else if (Game1.timeOfDay >= MineLateTime) rush = "it is late";
            else if (Utility.ConvertTimeToMinutes(Game1.timeOfDay) - startMinutes >= LeaveMineClimbMinutes) rush = "the climb takes too long";

            if (rush.Length == 0)
            {
                var step = new CallContext();
                yield return ClimbUpLadder(step);
                if (step.Ok) { climbed++; continue; }
                rush = step.Result;
            }

            warpedBecause = rush;
            yield return WarpToMineEntrance();
            break;
        }

        if (Game1.Date.TotalDays != day) yield break; // passed out: the runtime reports the new day

        if (stopped.Length > 0)
        {
            c.Fail($"Stopped on floor {(Loc as MineShaft)?.mineLevel}: {stopped}. Deal with it, then call leave_mine again.");
            yield break;
        }
        if (Loc is MineShaft stuck)
        {
            c.Fail($"Could not leave the mines (still on {WorldText.MineFloorName(stuck.mineLevel)}; {warpedBecause}). Try leave_mine again, or walk to the up ladder yourself.");
            yield break;
        }

        int spent = Utility.ConvertTimeToMinutes(Game1.timeOfDay) - startMinutes;
        bool skull = startFloor >= SkullCavernFirstFloor;
        var parts = new List<string>();
        if (climbed > 0) parts.Add($"climbed {climbed} ladder{(climbed == 1 ? "" : "s")}");
        if (warpedBecause.Length > 0) parts.Add($"warped to the {(skull ? "Skull Cave entrance" : "Mine entrance")} because {warpedBecause}");
        var where = Loc?.Name == "Mine" ? "the Mine entrance (floor 0)"
                  : Loc?.Name == "SkullCave" ? "the Skull Cave entrance in Calico Desert"
                  : Loc?.Name;
        var start = skull ? $"the Skull Cavern from {WorldText.MineFloorName(startFloor)}" : $"the mines from floor {startFloor}";
        c.Result = $"Left {start}; you are now at {where} ({string.Join(", then ", parts)}). " +
                   $"{spent} game minutes passed, it is {WorldText.FormatTime(Game1.timeOfDay)}. Health {P.health}/{P.maxHealth}.";
    }

    /// <summary>
    /// Walk to the up ladder of the current mine floor and use it. Fails with the reason when there is no ladder,
    /// it cannot be reached or using it does not change the floor.
    /// </summary>
    private static IEnumerator ClimbUpLadder(CallContext c)
    {
        var floor = Loc;
        var ladder = FindUpLadder(floor);
        if (ladder == null) { c.Fail("no up ladder was found on this floor"); yield break; }

        var walk = new CallContext();
        yield return WalkAdjacent(ladder.Value, walk);
        if (!walk.Ok) { c.Fail($"the up ladder at ({ladder.Value.X},{ladder.Value.Y}) could not be reached"); yield break; }
        if (Loc != floor) yield break; // something moved the farmer to another map on the way

        PressAction(ladder.Value);
        // The up ladder asks "Leave the mine?" (ExitMine); choosing Leave is the game's own way out.
        var answer = new CallContext();
        yield return AnswerQuestion("Leave", answer);
        for (int i = 0; i < 300 && Loc == floor; i++) yield return null;
        yield return WaitIdle();
        if (Loc == floor) c.Fail("the ladder did not take you up");
    }

    /// <summary>The up ladder tile (index 115) of a mine floor nearest to the farmer, or null.</summary>
    private static Point? FindUpLadder(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var me = P.TilePoint;
        Point? best = null;
        int bestDist = int.MaxValue;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (loc.getTileIndexAt(new xTile.Dimensions.Location(x, y), "Buildings", "mine") != MineLadderUpTile) continue;
            int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (d < bestDist) { bestDist = d; best = new Point(x, y); }
        }
        return best;
    }

    /// <summary>The fallback: warp out with the game's own warp, like the elevator does. Skull Cavern floors exit at the Skull Cave entrance.</summary>
    private static IEnumerator WarpToMineEntrance()
    {
        P.controller = null;
        if (!P.UsingTool) P.Halt();
        yield return WaitIdle(120);

        if (Loc is MineShaft shaft && shaft.mineLevel >= SkullCavernFirstFloor)
        {
            // The game's ExitMine_Leave uses the same spot for Skull Cavern floors above 120.
            Game1.warpFarmer("SkullCave", 3, 4, false);
        }
        else
        {
            Game1.warpFarmer("Mine", MineEntranceTile.X, MineEntranceTile.Y, false);
        }

        for (int i = 0; i < 300 && Loc is MineShaft; i++) yield return null;
        yield return WaitIdle();
    }
}
