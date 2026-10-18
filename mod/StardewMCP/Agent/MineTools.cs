using System;
using System.Collections;
using System.Collections.Generic;
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

    // ------------------------------------------------------------------ footer

    /// <summary>" · HP x/y" in the footer while in the mines (status shows health everywhere else).</summary>
    internal static string MineHealthFooter() => Loc is MineShaft ? $" · HP {P.health}/{P.maxHealth}" : "";

    /// <summary>A footer line when it is 11 PM or later and the farmer is still in the mines; passing out there costs money and items.</summary>
    internal static string MineLateWarning() =>
        Loc is MineShaft && Game1.timeOfDay >= MineLateTime ? "\n⚠ late in the mines: leave_mine now" : "";

    // ------------------------------------------------------------------ damage interrupt (called by AgentRuntime)

    /// <summary>Tools that deal with damage themselves: fight handles health, leave_mine is the retreat, eat is the cure.</summary>
    internal static bool IgnoresDamageInterrupt(string tool) => tool is "fight" or "leave_mine" or "eat";

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
