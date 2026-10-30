using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// chest(action=tidy): one storage chest for everything. The pack's resources (and anything that chest already holds) go
/// in, then the things kept in the other storage chests are carried over, a packful per trip. Chests that feed a machine
/// through Automate are left alone on both sides, so a Furnace never loses its ore and coal. Sale produce stays where it
/// is (it is for the shipping bin). Built on the chest tool's own store and take, so every move is logged.
/// </summary>
public static partial class AgentTools
{
    private const int TidyTripsPerChest = 3;

    /// <summary>Every storage chest on the farm maps (machine chests and fridges left out), as map name, tile and chest.</summary>
    private static List<(string map, Point tile, Chest chest)> StorageChests() => FarmChests(machine: false);

    /// <summary>
    /// What tidy leaves in the main chest of a resource a machine also burns or processes, for crafting: the Furnace gets the
    /// Coal above 10, a Charcoal Kiln the Wood above 50. Anything not listed goes to the machine whole (ores, fruit, milk...).
    /// </summary>
    private static int CraftingKeep(string qid) => qid switch
    {
        "(O)382" => 10, "(O)388" => 50, "(O)390" => 50, "(O)771" => 20, "(O)330" => 10, "(O)709" => 10, "(O)92" => 10,
        _ => 0
    };

    /// <param name="machine">True: the chests that touch an Automate machine; false: the storage chests.</param>
    private static List<(string map, Point tile, Chest chest)> FarmChests(bool machine)
    {
        var list = new List<(string, Point, Chest)>();
        var maps = new[] { "Farm", "FarmHouse", P.homeLocation.Value, "Greenhouse" }
            .Concat(Game1.getFarm().buildings.Select(b => b.GetIndoorsName()))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct();
        foreach (var name in maps)
        {
            if (Game1.getLocationFromName(name) is not { } loc) continue;
            foreach (var (pos, obj) in loc.objects.Pairs)
            {
                var tile = new Point((int)pos.X, (int)pos.Y);
                if (obj is Chest { playerChest.Value: true, fridge.Value: false } ch && IsMachineChest(loc, tile) == machine)
                    list.Add((name, tile, ch));
            }
        }
        return list;
    }

    /// <summary>What tidy carries: anything but sale produce (that is shipped) and your main tools.</summary>
    private static bool TidyMoves(Item it) =>
        (!IsSaleProduce(it) || SaleReserve.IsBasicResource(it.QualifiedItemId)) && MainToolKind(it) == null;

    private static IEnumerator ChestTidy(CallContext c)
    {
        var chests = StorageChests();
        if (chests.Count == 0) { c.Fail(Reasons.NotFound, "There is no storage chest on the farm (chests beside an Automate machine don't count). Craft a Chest (50 Wood) and place it."); yield break; }
        // The main chest: the fullest one on the Farm, else the fullest anywhere.
        var main = chests.OrderBy(x => x.map == "Farm" ? 0 : 1).ThenByDescending(x => x.chest.Items.Count(i => i != null)).First();
        var notes = new List<string>();

        IEnumerator StoreIntoMain(IEnumerable<string> names, List<string> into)
        {
            var list = names.Distinct().ToList();
            if (list.Count == 0) yield break;
            var sub = new CallContext();
            yield return ChestTool(ArgsOf(new { x = main.tile.X, y = main.tile.Y, action = "store", items = list, reason = "tidy: into the main chest" }), sub);
            into.Add(sub.Result.Split('\n')[0]);
        }

        // 1. The pack: resources and whatever the main chest already keeps.
        var kept = main.chest.Items.Where(i => i != null).Select(i => i.QualifiedItemId).ToHashSet();
        var fromPack = P.Items.Where(i => i != null && TidyMoves(i) && (SaleReserve.IsBasicResource(i.QualifiedItemId) || kept.Contains(i.QualifiedItemId))).Select(i => i.Name);
        yield return StoreIntoMain(fromPack, notes);

        // 2. The other storage chests, a packful per trip.
        foreach (var other in chests.Where(x => !(x.map == main.map && x.tile == main.tile)))
        {
            for (int trip = 0; trip < TidyTripsPerChest; trip++)
            {
                var names = other.chest.Items.Where(i => i != null && TidyMoves(i)).Select(i => i.Name).Distinct().ToList();
                if (names.Count == 0 || !P.Items.Any(i => i == null) && P.Items.Count >= P.MaxItems) break;
                if (main.chest.Items.Count(i => i != null) >= main.chest.GetActualCapacity()) { notes.Add($"The main chest at {main.map} ({main.tile.X},{main.tile.Y}) is full: craft another Chest and place it beside it."); goto done; }
                var take = new CallContext();
                yield return ChestTool(ArgsOf(new { x = other.tile.X, y = other.tile.Y, action = "take", items = names, reason = "tidy: carry to the main chest" }), take);
                if (!take.Ok) { notes.Add($"{other.map} ({other.tile.X},{other.tile.Y}): {take.Result.Split('\n')[0]}"); break; }
                yield return StoreIntoMain(names, notes);
            }
        }
        done:
        // 3. With Automate: each machine chest gets what its machines take (from the pack, then from the main chest, keeping a
        //    little for crafting), and its finished products come back to the main chest.
        foreach (var mc in FarmChests(machine: true))
        {
            if (Game1.getLocationFromName(mc.map) is not { } mloc) continue;
            var machines = MachinesAround(mloc, mc.tile);
            bool Feeds(Item it) => machines.Any(m => MachineTakes(m.machine, it));
            var label = $"{string.Join(", ", machines.Select(m => m.machine.Name).Distinct())} chest at {mc.map} ({mc.tile.X},{mc.tile.Y})";

            var packIn = P.Items.Where(i => i != null && Feeds(i) && CraftingKeep(i.QualifiedItemId) == 0 && MainToolKind(i) == null).Select(i => i.Name).Distinct().ToList();
            var fromMain = main.chest.Items.Where(i => i != null && Feeds(i))
                .GroupBy(i => (i.QualifiedItemId, i.Name))
                .Select(g => (name: g.Key.Name, n: g.Sum(i => i.Stack) - CraftingKeep(g.Key.QualifiedItemId)))
                .Where(x => x.n > 0).ToList();
            foreach (var (name, n) in fromMain)
            {
                if (!P.Items.Any(i => i == null) && P.Items.Count >= P.MaxItems) break;
                var take = new CallContext();
                yield return ChestTool(ArgsOf(new { x = main.tile.X, y = main.tile.Y, action = "take", item = name, quantity = n, reason = "tidy: inputs for the machines" }), take);
                if (take.Ok) packIn.Add(name);
            }
            if (packIn.Count > 0)
            {
                var put = new CallContext();
                yield return ChestTool(ArgsOf(new { x = mc.tile.X, y = mc.tile.Y, action = "store", items = packIn.Distinct().ToList(), reason = "tidy: inputs for the machines" }), put);
                notes.Add($"{label}: {put.Result.Split('\n')[0]}");
            }

            var products = mc.chest.Items.Where(i => i != null && !Feeds(i)).Select(i => i.Name).Distinct().ToList();
            if (products.Count > 0 && (P.Items.Any(i => i == null) || P.Items.Count < P.MaxItems))
            {
                var take = new CallContext();
                yield return ChestTool(ArgsOf(new { x = mc.tile.X, y = mc.tile.Y, action = "take", items = products, reason = "tidy: products to the main chest" }), take);
                if (take.Ok)
                {
                    var keep = products.Where(n => P.Items.Any(i => i != null && i.Name == n && TidyMoves(i))).ToList();
                    yield return StoreIntoMain(keep, notes);
                    if (products.Count > keep.Count) notes.Add($"Products to ship from the {label}: {string.Join(", ", products.Except(keep))} (now in your pack).");
                }
            }
        }

        var left =chests.Where(x => !(x.map == main.map && x.tile == main.tile) && x.chest.Items.Any(i => i != null && TidyMoves(i)))
            .Select(x => $"{x.map} ({x.tile.X},{x.tile.Y})").ToList();
        var result = $"Tidied into the main chest at {main.map} ({main.tile.X},{main.tile.Y}): {main.chest.Items.Count(i => i != null)}/{main.chest.GetActualCapacity()} slots used." +
                     (notes.Count > 0 ? "\n" + string.Join("\n", notes.Where(n => n.Length > 0)) : "") +
                     (left.Count > 0 ? $"\nStill to carry over (call tidy again): {string.Join(", ", left)}." : "") +
                     (AutomateLoaded ? "\nChests beside a machine (Automate) got their machines' inputs and gave up their products; nothing else goes into them." : "") +
                     "\nSale produce stays in place for the shipping bin.";
        c.Result = result;
    }
}
