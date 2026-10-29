using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Machine chests for the Automate mod (Pathoschild.Automate, optional): a chest touching a machine (any side or corner)
/// feeds it, since Automate pulls the machine's input (and fuel such as Coal) from it and puts the output back. Such a chest
/// only takes what the machines it touches use, read from the game's machine rules (Data/Machines: the input item or tags,
/// the count, the extra fuel); everything else stays out, so a stored pumpkin never ends up in a Keg by accident and the
/// chest is not filled with things the machine can't use. place says this when a machine or a chest goes down, machine
/// lists each machine's chest, and chest refuses the other items. Without Automate nothing here applies.
/// </summary>
public static partial class AgentTools
{
    internal const string AutomateId = "Pathoschild.Automate";

    internal static bool AutomateLoaded => Rt.Helper.ModRegistry.IsLoaded(AutomateId);

    private static readonly Point[] Around =
    {
        new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)
    };

    private static bool IsMachine(SObject? o) => o != null && o is not Chest && o.GetMachineData() != null;

    /// <summary>The machines touching this tile (8 neighbours).</summary>
    internal static List<(Point tile, SObject machine)> MachinesAround(GameLocation loc, Point tile) =>
        Around.Select(d => new Point(tile.X + d.X, tile.Y + d.Y))
            .Select(p => (p, o: loc.Objects.TryGetValue(p.ToVector2(), out var o) ? o : null))
            .Where(x => IsMachine(x.o)).Select(x => (x.p, x.o!)).ToList();

    /// <summary>The player chests touching this tile.</summary>
    internal static List<Point> ChestsAround(GameLocation loc, Point tile) =>
        Around.Select(d => new Point(tile.X + d.X, tile.Y + d.Y))
            .Where(p => loc.Objects.TryGetValue(p.ToVector2(), out var o) && o is Chest { playerChest.Value: true, fridge.Value: false })
            .ToList();

    /// <summary>True when the chest at this tile feeds a machine (Automate installed and a machine touches it).</summary>
    internal static bool IsMachineChest(GameLocation loc, Point tile) => AutomateLoaded && MachinesAround(loc, tile).Count > 0;

    /// <summary>The triggers of a machine that take an item put into it (not the ones that run by themselves).</summary>
    private static IEnumerable<(MachineOutputRule rule, MachineOutputTriggerRule trigger)> DropInTriggers(MachineData data) =>
        (data.OutputRules ?? new List<MachineOutputRule>())
        .SelectMany(r => (r.Triggers ?? new List<MachineOutputTriggerRule>()).Select(t => (r, t)))
        .Where(x => x.t.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine));

    private static bool TagsMatch(Item item, List<string>? tags) =>
        tags == null || tags.All(tag => tag.StartsWith("!") ? !item.HasContextTag(tag[1..]) : item.HasContextTag(tag));

    /// <summary>True when the machine takes this item as its input or as its fuel (Coal for the Furnace).</summary>
    internal static bool MachineTakes(SObject machine, Item item)
    {
        if (machine.GetMachineData() is not { } data) return false;
        if (data.AdditionalConsumedItems?.Any(f => ItemRegistry.QualifyItemId(f.ItemId) == item.QualifiedItemId) == true) return true;
        foreach (var (_, t) in DropInTriggers(data))
        {
            if (t.RequiredItemId != null && ItemRegistry.QualifyItemId(t.RequiredItemId) != item.QualifiedItemId) continue;
            if (t.RequiredItemId == null && (t.RequiredTags == null || t.RequiredTags.Count == 0)) continue;
            if (TagsMatch(item, t.RequiredTags)) return true;
        }
        return false;
    }

    /// <summary>"5 Copper Ore + 1 Coal → Copper Bar; fruit (category_fruits) → …": what the machine takes and makes.</summary>
    internal static string MachineRecipes(SObject machine, int max = 8)
    {
        if (machine.GetMachineData() is not { } data) return "";
        var fuel = (data.AdditionalConsumedItems ?? new List<MachineItemAdditionalConsumedItems>())
            .Select(f => $" + {f.RequiredCount} {ItemRegistry.GetData(f.ItemId)?.InternalName ?? f.ItemId}").ToList();
        var lines = new List<string>();
        foreach (var (rule, t) in DropInTriggers(data))
        {
            string input = t.RequiredItemId != null
                ? ItemRegistry.GetData(t.RequiredItemId)?.InternalName ?? t.RequiredItemId
                : t.RequiredTags is { Count: > 0 } ? "anything tagged " + string.Join(" + ", t.RequiredTags) : "";
            if (input.Length == 0) continue;
            var outId = rule.OutputItem?.FirstOrDefault()?.ItemId;
            var output = outId == null ? "its product" : ItemRegistry.GetData(outId)?.InternalName ?? outId;
            var line = $"{Math.Max(1, t.RequiredCount)} {input}{string.Join("", fuel)} → {output}";
            if (!lines.Contains(line)) lines.Add(line);
        }
        if (lines.Count == 0) return data.HasInput ? "" : "nothing: it works by itself";
        return string.Join("; ", lines.Take(max)) + (lines.Count > max ? $"; and {lines.Count - max} more" : "");
    }

    /// <summary>What a placed machine's chest is for, or "" without Automate.</summary>
    internal static string MachineChestNote(GameLocation loc, Point machineTile, SObject machine)
    {
        if (!AutomateLoaded) return "";
        var recipes = MachineRecipes(machine);
        var chests = ChestsAround(loc, machineTile);
        var chest = chests.Count > 0
            ? $"its chest is at ({chests[0].X},{chests[0].Y})"
            : $"place a Chest touching it (any side or corner of ({machineTile.X},{machineTile.Y}))";
        if (!machine.GetMachineData()!.HasInput && recipes.StartsWith("nothing"))
            return $"{machine.Name} ({machineTile.X},{machineTile.Y}) works by itself; with Automate {chest} and its products land in that chest.";
        return $"{machine.Name} ({machineTile.X},{machineTile.Y}) with Automate: {chest}; Automate takes the input from it and puts the product back. " +
               $"It makes: {recipes}. Put ONLY these inputs into that chest; nothing else.";
    }

    /// <summary>What a chest touching machines may hold, or "" when it touches none (or Automate is not installed).</summary>
    internal static string ChestFeedsNote(GameLocation loc, Point chestTile)
    {
        if (!AutomateLoaded) return "";
        var machines = MachinesAround(loc, chestTile);
        if (machines.Count == 0) return "";
        var parts = machines.GroupBy(m => m.machine.Name).Select(g => $"{g.Key} ({MachineRecipes(g.First().machine, 5)})");
        return $"This chest at ({chestTile.X},{chestTile.Y}) feeds {string.Join(", ", parts)} through Automate: it only takes their inputs and fuel; " +
               "keep your other things in a chest that touches no machine.";
    }

    /// <summary>For store: the items the chest at this tile may not take (it feeds machines and they don't use them).</summary>
    internal static Func<Item, bool>? MachineChestRefuses(GameLocation? loc, Point chestTile)
    {
        if (loc == null || !AutomateLoaded) return null;
        var machines = MachinesAround(loc, chestTile);
        if (machines.Count == 0) return null;
        return item => !machines.Any(m => MachineTakes(m.machine, item));
    }
}
