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
    private const int ChopMax = 12, AreaChopMax = 40;

    /// <summary>The farm area given as x,y to x2,y2 (any corner order), or null.</summary>
    private static Rectangle? ChopArea(JsonElement a)
    {
        if (!Has(a, "x") || !Has(a, "y") || !Has(a, "x2") || !Has(a, "y2")) return null;
        int x = I(a, "x", 0), y = I(a, "y", 0), x2 = I(a, "x2", 0), y2 = I(a, "y2", 0);
        return new Rectangle(Math.Min(x, x2), Math.Min(y, y2), Math.Abs(x2 - x) + 1, Math.Abs(y2 - y) + 1);
    }

    /// <summary>Every tree in <paramref name="area"/>, any size, stumps too (not tapped ones).</summary>
    private static List<Point> TreesIn(GameLocation loc, Rectangle area) =>
        loc.terrainFeatures.Pairs.Where(kv => kv.Value is Tree { tapped.Value: false } && area.Contains((int)kv.Key.X, (int)kv.Key.Y))
            .Select(kv => new Point((int)kv.Key.X, (int)kv.Key.Y)).ToList();

    private static ToolDef ChopTreesTool() => new()
    {
        Name = "chop_trees",
        Description = "Get Wood: walks to the nearest grown trees on the Farm and fells them with the Axe (about 12 Wood each, stumps included), then plants the Pine Cones, " +
                      "Acorns and Maple Seeds in your pack on open ground so new trees grow (not in winter). Stops on low energy, a menu or late night. " +
                      $"With an area (x,y to x2,y2) it fells every tree in it instead, young ones and stumps too (up to {AreaChopMax}), to free that ground, and plants no seeds there.",
        Schema = Obj(new
        {
            count = Int($"How many trees to fell, 1-{ChopMax} (default 6)"),
            x = Int("Area corner tile x (with y, x2, y2: fell the trees in that area)"), y = Int("Area corner tile y"),
            x2 = Int("Opposite corner tile x"), y2 = Int("Opposite corner tile y"),
            replant = new { type = "boolean", description = "Plant the tree seeds you carry afterwards (default: yes for an AI, no for a person in Manual mode)" },
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
        // A full pack sends drops to the chests (storage mods): count both, or day 11's 12 felled trees read "Wood 0 → 0".
        int WoodNow() => CountItem("(O)388") + ChestCount("(O)388");
        int wood0 = WoodNow(), pack0 = CountItem("(O)388");
        var sb = new StringBuilder();

        var area = ChopArea(a);
        var trees = area is { } r
            ? RouteOrder(TreesIn(farm, r), P.TilePoint).Take(AreaChopMax).ToList()
            : RouteOrder(GrownTrees(farm), P.TilePoint).Take(count).ToList();
        if (trees.Count == 0) sb.Append(area is { } e ? $"No trees stand in ({e.X},{e.Y}) to ({e.Right - 1},{e.Bottom - 1})." : "No grown trees stand on the Farm.");
        else
        {
            // One tree at a time, keeping FarmBatchMinEnergy plus one more tree's swings in reserve: day 1 felled trees from
            // 270 energy down to 1 by 08:20 and the day was over.
            var lines = new List<string>();
            int felled = 0, lastCost = 30;
            string? stop = null;
            foreach (var tree in trees)
            {
                if (P.Stamina < Math.Max(0, Config.FarmBatchMinEnergy) + lastCost) { stop = $"Stopped to keep energy for the rest of the day ({(int)P.Stamina} left): {DaySafety.LowEnergyAdvice(Game1.timeOfDay, BedRegen)}."; break; }
                if (Game1.activeClickableMenu != null || Game1.timeOfDay >= 2400) break;
                float before = P.Stamina;
                var chop = new CallContext();
                yield return UseTool(ArgsOf(new { tool = "Axe", tiles = new[] { new[] { tree.X, tree.Y } }, max_hits = 40, reason = "fell trees for Wood" }), chop);
                lines.Add(chop.Result.Split('\n')[0]);
                if (chop.Ok) felled++;
                lastCost = Math.Max(lastCost, (int)Math.Ceiling(before - P.Stamina));
            }
            int pack = CountItem("(O)388"), all = WoodNow();
            sb.Append($"Wood {wood0} → {all} (pack {pack0} → {pack}{(all - wood0 > pack - pack0 ? ", the rest went to your chests because the pack is full" : "")}). ")
              .Append($"Felled {felled} of {trees.Count} tree(s). ").Append(string.Join("\n", lines));
            if (stop != null) sb.Append('\n').Append(stop);
        }

        // Clearing an area for a field: no tree seeds go back into the ground there. A person (Manual mode) plants trees only
        // when asked (replant=true): day 65 a queued chop_trees sowed the cleared field with Acorns and Maple Seeds.
        bool replant = Has(a, "replant") ? a.TryGetProperty("replant", out var rp) && rp.ValueKind == JsonValueKind.True : Players.Current.Role != SeatRole.Human;
        if (area == null && replant && Game1.activeClickableMenu == null && Loc == farm)
        {
            var planted = new CallContext();
            yield return PlantTreeSeeds(farm, null, planted);
            if (planted.Result.Length > 0) sb.Append('\n').Append(planted.Result);
        }
        int left = GrownTrees(farm).Count;
        sb.Append($"\n{left} grown tree(s) left on the Farm.");
        if (area == null && WoodNow() == wood0 && trees.Count > 0) c.Fail(sb.ToString()); // saplings in an area give no Wood
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
