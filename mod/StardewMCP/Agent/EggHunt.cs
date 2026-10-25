using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// The Egg Festival's egg hunt (spring 13). In the game (Event.cs, 1.6) the "eggHunt" player-control sequence turns
/// every Paths-layer tile of a "fest" tilesheet into a solid <see cref="Prop"/> in <c>Event.festivalProps</c> and starts
/// a 52 s <c>festivalTimer</c>. The action button on an egg's tile (<c>GameLocation.checkAction</c> → <c>Event.checkAction</c>)
/// adds one to <c>festivalScore</c> and removes the prop for everyone. The timer counts only while the world runs, so
/// the host pause that freezes the world while an AI thinks stops it too (split-screen).
/// </summary>
public static partial class AgentTools
{
    /// <summary>The egg hunt event, while the hunt itself runs (after Lewis's countdown, before the timer ends).</summary>
    private static Event? EggHuntEvent() =>
        Game1.eventUp && Game1.CurrentEvent is { isFestival: true, playerControlSequenceID: "eggHunt" } ev ? ev : null;

    internal static bool EggHuntRunning => EggHuntEvent() != null;

    /// <summary>An egg still lies on this tile: the game's egg tile rule (Paths layer, "fest" tilesheet) with its prop not picked yet.</summary>
    internal static bool EggAt(GameLocation loc, int x, int y)
    {
        if (EggHuntEvent() is not { } ev) return false;
        if (loc.Map.GetLayer("Paths") is not { } layer || x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight) return false;
        if (layer.Tiles[x, y] is not { } tile || !tile.TileSheet.Id.StartsWith("fest")) return false;
        var rect = new Rectangle(x * 64, y * 64, 64, 64);
        return ev.festivalProps.Any(p => p.isColliding(rect));
    }

    /// <summary>Every egg still on the map.</summary>
    private static List<Point> EggTiles()
    {
        var eggs = new List<Point>();
        var loc = Loc;
        if (EggHuntEvent() == null || loc.Map.GetLayer("Paths") is not { } layer) return eggs;
        for (int x = 0; x < layer.LayerWidth; x++)
            for (int y = 0; y < layer.LayerHeight; y++)
                if (EggAt(loc, x, y)) eggs.Add(new Point(x, y));
        return eggs;
    }

    /// <summary>Eggs a farmer needs to be able to win (Event.eggHuntWinner): fewer players need more eggs.</summary>
    private static int EggsToWin() => (Game1.numberOfPlayers() - (Game1.HasDedicatedHost ? 1 : 0)) switch { 1 => 9, 2 => 6, 3 => 5, _ => 4 };

    private static string EggScores() =>
        string.Join(", ", PlayingFarmers().Select(f => $"{(f == P ? "you" : f.Name)} {f.festivalScore}"));

    /// <summary>The egg hunt line of status while the hunt runs, else null.</summary>
    internal static string? EggHuntLine()
    {
        if (EggHuntEvent() is not { } ev) return null;
        return $"Egg hunt running: {Math.Max(0, ev.festivalTimer) / 1000}s left, {EggTiles().Count} eggs left on the map. Eggs: {EggScores()}. " +
               $"The most eggs wins (at least {EggsToWin()}). festival(eggs) picks them up; look shows them as E.";
    }

    /// <summary>
    /// festival(eggs): pick up eggs nearest first until the hunt ends, none are left or <c>max</c> are picked.
    /// Before the hunt it advances Lewis's countdown lines (no question in them) and waits for the start.
    /// </summary>
    private static IEnumerator FestivalEggs(int max, CallContext c)
    {
        for (int i = 0; i < 1800 && EggHuntEvent() == null && Game1.eventUp && Game1.CurrentEvent is { isFestival: true }; i++)
        {
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: true }) break; // the AI answers questions with respond
            if (Game1.activeClickableMenu is DialogueBox) yield return Respond(FestivalJson("{}"), new CallContext());
            else yield return null;
        }
        if (EggHuntEvent() == null)
        {
            c.Fail(Reasons.NotReady, CalendarData.Today()?.Name == "Egg Festival"
                ? $"The egg hunt is not running. It starts after festival(start) and Lewis's countdown, and lasts about 50 seconds. {WorldText.MenuState().Trim()}".Trim()
                : "There is no egg hunt here. It is the Egg Festival's main event (spring 13 in Town).");
            yield break;
        }

        int start = P.festivalScore;
        var missed = new List<Point>();
        string stop = "no eggs left";
        while (true)
        {
            if (EggHuntEvent() == null) { stop = "the hunt is over"; break; }
            if (P.festivalScore - start >= max) { stop = $"picked the {max} asked for"; break; }
            var me = P.TilePoint;
            var next = EggTiles().Where(t => !missed.Contains(t))
                .OrderBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y)).Cast<Point?>().FirstOrDefault();
            if (next is not { } egg) { if (missed.Count > 0) stop = "only eggs you could not reach are left"; break; }

            var walk = new CallContext();
            yield return WalkAdjacent(egg, walk);
            if (EggHuntEvent() == null) { stop = "the hunt is over"; break; }
            // Someone else may have picked it on the way; a failed walk means no free tile next to it.
            if (walk.Ok && EggAt(Loc, egg.X, egg.Y)) PressAction(egg);
            if (EggAt(Loc, egg.X, egg.Y)) missed.Add(egg);
            yield return null;
        }

        int picked = P.festivalScore - start;
        var left = EggHuntEvent() is { } ev ? $" {Math.Max(0, ev.festivalTimer) / 1000}s left, {EggTiles().Count} eggs left." : "";
        var unreached = missed.Count > 0 ? $" Not reached: {string.Join(", ", missed.Take(6).Select(t => $"({t.X},{t.Y})"))}." : "";
        c.Result = $"Picked up {picked} egg{(picked == 1 ? "" : "s")} ({stop}).{left}{unreached} Eggs: {EggScores()}. The most eggs wins (at least {EggsToWin()}).";
    }
}
