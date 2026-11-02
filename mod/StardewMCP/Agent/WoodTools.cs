using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewMCP.Agent;

/// <summary>
/// chop_trees: Wood comes from the farm's trees, never from a shop (decided 2026-11-01). Days 102-133 brought in 161 Wood
/// for a Coop that needs 300 while 79 Pine Cones and 55 Maple Seeds sat in a chest: use_tool on guessed tiles was too fiddly.
/// It fells the nearest grown trees with the Axe (the same swings as use_tool) and plants tree seeds from the pack on open
/// ground, so the farm never runs out of trees. plant sends tree seeds here too.
/// </summary>
public static partial class AgentTools
{
    private const int ChopMax = 12;

    private static ToolDef ChopTreesTool() => new()
    {
        Name = "chop_trees",
        Description = "Get Wood: walks to the nearest grown trees on the Farm and fells them with the Axe (about 12 Wood each, stumps included), then plants the Pine Cones, " +
                      "Acorns and Maple Seeds in your pack on open ground so new trees grow (not in winter). Stops on low energy, a menu or late night.",
        Schema = Obj(new
        {
            count = Int($"How many trees to fell, 1-{ChopMax} (default 6)"),
            reason = Str(ReasonDesc)
        }, "reason"),
        Run = ChopTrees
    };

    internal static bool IsTreeSeed(Item? it) => it?.QualifiedItemId is "(O)309" or "(O)310" or "(O)311" or "(O)292";

    /// <summary>Grown trees (not stumps, not tapped) on a map.</summary>
    private static List<Point> GrownTrees(GameLocation loc) =>
        loc.terrainFeatures.Pairs.Where(kv => kv.Value is Tree { stump.Value: false, tapped.Value: false } t && t.growthStage.Value >= 5)
            .Select(kv => new Point((int)kv.Key.X, (int)kv.Key.Y)).ToList();

    private static IEnumerator ChopTrees(JsonElement a, CallContext c)
    {
        int count = Math.Clamp(I(a, "count", 6), 1, ChopMax);
        if (Loc is not Farm)
        {
            var trip = new CallContext();
            yield return TravelTo("Farm", trip);
            if (!trip.Ok || Loc is not Farm) { c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get to the Farm: {trip.Result}"); yield break; }
        }
        var farm = (Farm)Loc;
        int wood0 = CountItem("(O)388");
        var sb = new StringBuilder();

        var trees = RouteOrder(GrownTrees(farm), P.TilePoint).Take(count).ToList();
        if (trees.Count == 0) sb.Append("No grown trees stand on the Farm.");
        else
        {
            var chop = new CallContext();
            yield return UseTool(ArgsOf(new { tool = "Axe", tiles = trees.Select(t => new[] { t.X, t.Y }).ToArray(), max_hits = 40, reason = "fell trees for Wood" }), chop);
            sb.Append($"Wood {wood0} → {CountItem("(O)388")} in your pack. ").Append(chop.Result);
        }

        if (Game1.activeClickableMenu == null && Loc == farm)
        {
            var planted = new CallContext();
            yield return PlantTreeSeeds(farm, null, planted);
            if (planted.Result.Length > 0) sb.Append('\n').Append(planted.Result);
        }
        int left = GrownTrees(farm).Count;
        sb.Append($"\n{left} grown tree(s) left on the Farm.");
        if (CountItem("(O)388") == wood0 && trees.Count > 0) c.Fail(sb.ToString());
        else c.Result = sb.ToString();
    }

    /// <summary>
    /// Plants the tree seeds in the pack (only <paramref name="name"/> when given) on open farm ground, away from the shipping
    /// bin where buildings go, through place. Says nothing when there are no tree seeds.
    /// </summary>
    private static IEnumerator PlantTreeSeeds(Farm farm, string? name, CallContext c, List<Point>? tiles = null)
    {
        var seeds = P.Items.Where(i => IsTreeSeed(i) && (name == null || i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || i.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(i => i!.Name).Select(g => (name: g.Key, n: g.Sum(i => i!.Stack))).ToList();
        if (seeds.Count == 0) yield break;
        if (Game1.IsWinter) { c.Result = "Tree seeds kept: trees don't grow in winter, plant them in spring."; yield break; }

        var spots = tiles ?? FreeSpots(farm, 1, 1, 60).Skip(30).Take(seeds.Sum(s => s.n)).ToList();
        if (spots.Count == 0) { c.Result = "No open ground left for tree seeds."; yield break; }
        var lines = new List<string>();
        int at = 0;
        foreach (var (seed, n) in seeds)
        {
            var mine = spots.Skip(at).Take(n).ToList();
            if (mine.Count == 0) break;
            at += mine.Count;
            var put = new CallContext();
            yield return Place(ArgsOf(new { item = seed, tiles = mine.Select(t => new[] { t.X, t.Y }).ToArray(), reason = "trees for Wood" }), put);
            lines.Add(put.Result.Split('\n')[0]);
        }
        c.Result = "Tree seeds: " + string.Join(" ", lines) + " Chop them once they are grown (about a month).";
    }
}
