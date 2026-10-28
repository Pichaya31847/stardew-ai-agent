using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// machine: the crafting stations and artisan machines placed in the world (Furnace, Keg, Preserves Jar, Cheese Press,
/// Mayonnaise Machine, Loom, Oil Maker, Charcoal Kiln, Recycling Machine, Seed Maker, Crystalarium, Bee House, Tapper...).
/// Loading is the game's own Object.performObjectDropInAction (it checks the machine rules, takes the input and any fuel such
/// as Coal from the pack); collecting is Object.checkForAction, as a click on a ready machine.
/// </summary>
public static partial class AgentTools
{
    /// <summary>What the common machines take and make, by English name; shown next to a machine in the list.</summary>
    private static readonly Dictionary<string, string> MachineUses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Furnace"] = "5 ore + 1 Coal → a bar (Copper, Iron, Gold, Iridium); Quartz → Refined Quartz",
        ["Keg"] = "fruit → wine, vegetable → juice, Wheat → beer, Hops → pale ale, Coffee Bean x5 → coffee, Honey → mead",
        ["Preserves Jar"] = "fruit → jelly, vegetable → pickles, roe → aged roe",
        ["Cheese Press"] = "milk → cheese, goat milk → goat cheese",
        ["Mayonnaise Machine"] = "egg → mayonnaise (duck, void, dinosaur eggs too)",
        ["Loom"] = "Wool → Cloth",
        ["Oil Maker"] = "Truffle → Truffle Oil; Corn, Sunflower or its seeds → Oil",
        ["Charcoal Kiln"] = "10 Wood → Coal",
        ["Recycling Machine"] = "trash (Joja Cola, driftwood, broken glasses...) → resources",
        ["Seed Maker"] = "a crop → its seeds (sometimes Mixed Seeds or Ancient Seeds)",
        ["Crystalarium"] = "a gem or mineral → copies of it",
        ["Bee House"] = "nothing: makes Honey by itself (better near flowers)",
        ["Tapper"] = "place on a tree: syrup or resin by itself",
        ["Worm Bin"] = "nothing: makes bait by itself",
        ["Fish Smoker"] = "a fish + 1 Coal → smoked fish",
        ["Dehydrator"] = "5 fruit or mushrooms → dried fruit or mushrooms; Grapes → raisins",
        ["Bone Mill"] = "bones → fertilizer",
        ["Mushroom Log"] = "nothing: grows mushrooms by itself (near trees)",
        ["Lightning Rod"] = "nothing: catches lightning in storms → Battery Pack"
    };

    private static ToolDef MachineTool() => new()
    {
        Name = "machine",
        Description = "Use the machines placed where you are (Furnace, Keg, Preserves Jar, Cheese Press, Mayonnaise Machine, Loom, Oil Maker, Charcoal Kiln, Recycling Machine, Seed Maker, Crystalarium, Bee House, Tapper...). " +
                      "action=list (default): every machine here, what it makes or holds and when it is ready, plus ready machines on the rest of the farm. " +
                      "action=load with item (and quantity = how many machines to fill, default all idle ones that take it): walks to each idle machine that takes the item and puts it in; fuel like Coal comes from your pack. " +
                      "action=collect: walks to every ready machine here and takes the products. Craft a machine with craft, put it down with place. Artisan goods sell for much more than the crops.",
        Schema = Obj(new
        {
            action = new { type = "string", @enum = new[] { "list", "load", "collect" }, description = "list (default), load or collect" },
            item = Str("load: the item to put in, e.g. Copper Ore, Blueberry, Milk"),
            quantity = Int("load: how many machines to fill at most (default: every idle one that takes it)"),
            reason = Str(ReasonDesc)
        }),
        InstantFor = a => S(a, "action").Trim().ToLowerInvariant() is "" or "list",
        Run = MachineRun
    };

    private static IEnumerator MachineRun(JsonElement a, CallContext c)
    {
        switch (S(a, "action").Trim().ToLowerInvariant())
        {
            case "":
            case "list":
                c.Result = MachineList();
                yield break;
            case "load":
                yield return MachineLoad(a, c);
                yield break;
            case "collect":
                yield return MachineCollect(c);
                yield break;
            default:
                c.Fail(Reasons.InvalidArgs, "action is list, load or collect.");
                yield break;
        }
    }

    /// <summary>Machines on a map: big craftables with machine rules (chests and other furniture left out).</summary>
    private static List<(Point tile, SObject obj)> MachinesIn(GameLocation loc)
    {
        var list = new List<(Point, SObject)>();
        foreach (var kv in loc.Objects.Pairs)
        {
            var o = kv.Value;
            if (o is Chest || o.GetMachineData() == null) continue;
            list.Add((kv.Key.ToPoint(), o));
        }
        return list;
    }

    private static bool MachineReady(SObject o) => o.readyForHarvest.Value && o.heldObject.Value != null;

    private static string MachineState(SObject o) =>
        MachineReady(o) ? $"READY: {o.heldObject.Value!.DisplayName}"
        : o.heldObject.Value != null ? $"making {o.heldObject.Value.DisplayName}, {Math.Max(0, o.MinutesUntilReady)} min left"
        : "idle";

    private static string MachineList()
    {
        var here = MachinesIn(Loc);
        var sb = new StringBuilder();
        if (here.Count == 0) sb.Append($"No machines on {Loc.Name}.");
        else
        {
            sb.Append($"Machines on {Loc.Name}:");
            foreach (var g in here.GroupBy(m => m.obj.Name))
            {
                var use = MachineUses.TryGetValue(g.Key, out var u) ? $" ({u})" : "";
                sb.Append($"\n- {g.First().obj.DisplayName} x{g.Count()}{use}: ");
                sb.Append(string.Join("; ", g.Select(m => $"({m.tile.X},{m.tile.Y}) {MachineState(m.obj)}")));
            }
        }
        // Ready machines elsewhere on the farm, so the next stop is clear.
        var elsewhere = new List<string>();
        foreach (var loc in FarmMachineLocations())
        {
            if (loc == Loc) continue;
            int ready = MachinesIn(loc).Count(m => MachineReady(m.obj));
            if (ready > 0) elsewhere.Add($"{loc.NameOrUniqueName} {ready}");
        }
        if (elsewhere.Count > 0) sb.Append($"\nReady elsewhere: {string.Join(", ", elsewhere)} (travel_to there, then machine(action=collect)).");
        var packed = P.Items.Where(i => i is SObject o && o.bigCraftable.Value && MachineUses.ContainsKey(o.Name)).Select(i => i.DisplayName).Distinct().ToList();
        if (packed.Count > 0) sb.Append($"\nIn your pack: {string.Join(", ", packed)}: put them down with place (in a shed, a barn or on the farm).");
        return sb.ToString();
    }

    /// <summary>The farm and the insides of its buildings (sheds, coops, barns, the greenhouse, cabins).</summary>
    private static IEnumerable<GameLocation> FarmMachineLocations()
    {
        var farm = Game1.getFarm();
        if (farm == null) yield break;
        yield return farm;
        foreach (var b in farm.buildings)
            if (b.GetIndoors() is { } inside) yield return inside;
        if (Game1.getLocationFromName("Greenhouse") is { } gh) yield return gh;
    }

    private static IEnumerator MachineCollect(CallContext c)
    {
        var loc = Loc;
        var ready = MachinesIn(loc).Where(m => MachineReady(m.obj)).Select(m => m.tile).ToList();
        if (ready.Count == 0) { c.Fail(Reasons.NotReady, "No machine here is ready. " + MachineList()); yield break; }
        var before = Snapshot();
        int took = 0;
        string stop = "";
        foreach (var tile in RouteOrder(ready, P.TilePoint))
        {
            if (FreeSlots() == 0) { stop = "your pack is full"; break; }
            if (!loc.Objects.TryGetValue(tile.ToVector2(), out var o) || !MachineReady(o)) continue;
            var walk = new CallContext();
            yield return WalkAdjacent(tile, walk);
            if (!walk.Ok) continue;
            Face(tile);
            if (o.checkForAction(P, false)) took++;
            yield return Ticks(10);
        }
        yield return Ticks(20);
        var diff = Diff(before);
        var text = $"Collected from {took} machine(s). {(diff.Length > 0 ? diff : "")}{(stop.Length > 0 ? $" Stopped: {stop}." : "")}".Trim();
        if (took == 0) c.Fail(Reasons.NotReady, text); else c.Result = text;
    }

    private static IEnumerator MachineLoad(JsonElement a, CallContext c)
    {
        var want = S(a, "item").Trim();
        if (want.Length == 0) { c.Fail(Reasons.InvalidArgs, "load needs item: what to put in, e.g. Copper Ore."); yield break; }
        int slot = FindItem(want, it => it is SObject);
        if (slot < 0) { c.Fail(Reasons.NoItem, $"No \"{want}\" in your pack."); yield break; }
        var item = P.Items[slot];
        var qid = item.QualifiedItemId;
        var loc = Loc;
        // Ask each idle machine whether it would take the item (probe: nothing changes).
        var takers = MachinesIn(loc).Where(m => m.obj.heldObject.Value == null && Probe(m.obj, item)).Select(m => m.tile).ToList();
        if (takers.Count == 0)
        {
            var idle = MachinesIn(loc).Where(m => m.obj.heldObject.Value == null).Select(m => m.obj.DisplayName).Distinct().ToList();
            c.Fail(Reasons.NotHere, idle.Count == 0
                ? $"No idle machine on {loc.Name}. {MachineList()}"
                : $"None of the idle machines here ({string.Join(", ", idle)}) takes {item.DisplayName} now (wrong item, too few of it, or fuel such as Coal missing).");
            yield break;
        }
        int limit = Math.Clamp(I(a, "quantity", takers.Count), 1, takers.Count);
        var before = Snapshot();
        var loaded = new List<string>();
        foreach (var tile in RouteOrder(takers, P.TilePoint).Take(limit))
        {
            int s = FindItem(item.DisplayName, it => it.QualifiedItemId == qid);
            if (s < 0) break;
            if (!loc.Objects.TryGetValue(tile.ToVector2(), out var o) || o.heldObject.Value != null) continue;
            var walk = new CallContext();
            yield return WalkAdjacent(tile, walk);
            if (!walk.Ok) continue;
            Face(tile);
            P.CurrentToolIndex = s;
            var input = P.Items[s];
            if (o.performObjectDropInAction(input, false, P, false))
                loaded.Add($"{o.DisplayName} ({tile.X},{tile.Y}): {(o.heldObject.Value != null ? $"{o.heldObject.Value.DisplayName} in {o.MinutesUntilReady} min" : "loaded")}");
            yield return Ticks(10);
        }
        if (loaded.Count == 0) { c.Fail(Reasons.NotHere, $"No machine took {item.DisplayName}."); yield break; }
        var diff = Diff(before);
        c.Result = $"Loaded {loaded.Count} machine(s): {string.Join("; ", loaded)}.{(diff.Length > 0 ? "\n" + diff : "")}\nCome back with machine(action=collect) when they are ready.";
    }

    private static bool Probe(SObject machine, Item item)
    {
        try { return machine.performObjectDropInAction(item, true, P, false); }
        catch (Exception) { return false; }
    }
}
