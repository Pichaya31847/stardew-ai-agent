using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// The five tools a farmer works with every day stay in the pack. Day 25 of the third local run: with a full 12-slot pack AI1
/// put its Axe, Hoe, Watering Can, Pickaxe and Scythe into a chest; from day 32 its Wheat stood ripe (only a Scythe cuts it)
/// and go_to_bed kept sending it to harvest. Now chest refuses to store them, and the farm tools fetch a missing one back
/// from the chests themselves, putting another stack into that chest when the pack has no free slot.
/// </summary>
public static partial class AgentTools
{
    internal const string ScytheKind = "Scythe", AxeKind = "Axe", HoeKind = "Hoe", CanKind = "Watering Can", PickaxeKind = "Pickaxe", RodKind = "Fishing Rod";

    /// <summary>Which of the five main tools this item is, or null.</summary>
    internal static string? MainToolKind(Item? it) => it switch
    {
        Axe => AxeKind,
        Hoe => HoeKind,
        WateringCan => CanKind,
        Pickaxe => PickaxeKind,
        MeleeWeapon w when w.isScythe() => ScytheKind,
        FishingRod => RodKind, // days 38-49: the rods sat in a chest and every fish call failed
        _ => null
    };

    private static bool HasMainTool(string kind) => P.Items.Any(i => MainToolKind(i) == kind);

    /// <summary>
    /// When <paramref name="kind"/> is not in the pack, takes it back out of the chest that holds it (walking there). The line
    /// for the result goes to <paramref name="notes"/>; nothing is added when the tool is in the pack already.
    /// </summary>
    internal static IEnumerator EnsureMainTool(string kind, List<string> notes)
    {
        if (HasMainTool(kind)) yield break;
        string? where = null;
        Point tile = default;
        foreach (var name in new[] { Loc.NameOrUniqueName, P.homeLocation.Value, "FarmHouse", "Farm" }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            var loc = name == Loc.NameOrUniqueName ? Loc : Game1.getLocationFromName(name);
            if (loc == null) continue;
            var hit = loc.objects.Pairs.FirstOrDefault(kv => kv.Value is Chest { playerChest.Value: true } ch && ch.Items.Any(i => MainToolKind(i) == kind));
            if (hit.Value == null) continue;
            where = name;
            tile = new Point((int)hit.Key.X, (int)hit.Key.Y);
            break;
        }
        if (where == null) { notes.Add($"You have no {kind} in your pack or in the chests."); yield break; }

        if (Loc.NameOrUniqueName != where)
        {
            var trip = new CallContext();
            yield return TravelTo(where, trip);
            if (!trip.Ok) { notes.Add($"Your {kind} is in the chest at {where} ({tile.X},{tile.Y}), but the way there failed: {trip.Result}"); yield break; }
        }
        var open = new CallContext();
        yield return Interact(ArgsOf(new { x = tile.X, y = tile.Y, reason = $"take the {kind} back" }), open);
        for (int i = 0; i < 120 && Game1.activeClickableMenu == null; i++) yield return null;
        if (Game1.activeClickableMenu is not ItemGrabMenu { context: Chest } menu)
        {
            if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
            notes.Add($"Your {kind} is in the chest at {where} ({tile.X},{tile.Y}), but it did not open: {open.Result}");
            yield break;
        }

        var swapped = "";
        if (!P.Items.Any(i => i == null) && P.Items.Count >= P.MaxItems && !IsMachineChest(Loc, tile))
        {
            // Make room with the stack that matters least on the job: not a tool, not seeds, not food.
            var spare = P.Items.Where(i => i != null && i is not Tool && i.Category != StardewValley.Object.SeedsCategory && i.Category != -7 && menu.inventory.highlightMethod(i))
                .OrderBy(i => i.salePrice() * i.Stack).FirstOrDefault();
            if (spare != null)
            {
                var put = new CallContext();
                yield return Put(menu, ArgsOf(new { item = spare.Name, quality = QualityName(spare.Quality), count = spare.Stack }), put);
                if (put.Ok) swapped = $" (put {spare.Stack} {spare.Name} in to make room)";
            }
        }
        var tool = menu.ItemsToGrabMenu.actualInventory.First(i => MainToolKind(i) == kind);
        var take = new CallContext();
        if (Game1.activeClickableMenu is ItemGrabMenu now)
            yield return Take(now, ArgsOf(new { item = tool.Name }), take);
        if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
        notes.Add(HasMainTool(kind)
            ? $"Took your {tool.Name} back from the chest at {where} ({tile.X},{tile.Y}){swapped}; main tools stay in the pack."
            : $"Your {tool.Name} is in the chest at {where} ({tile.X},{tile.Y}) and could not be taken: {take.Result}");
    }

    /// <summary>Every missing tool of <paramref name="kinds"/> fetched back from the chests, then back to <paramref name="loc"/> if the walk left it.</summary>
    internal static IEnumerator FetchMainTools(GameLocation loc, List<string> notes, params string[] kinds)
    {
        foreach (var kind in kinds)
            if (!HasMainTool(kind)) yield return EnsureMainTool(kind, notes);
        if (Loc != loc)
        {
            var back = new CallContext();
            yield return TravelTo(loc.NameOrUniqueName, back);
            if (!back.Ok) notes.Add($"Couldn't get back to {loc.Name}: {back.Result}");
        }
    }

    /// <summary>"Scythe", "Axe"... when the name the model gave is one of the main tools, else null.</summary>
    internal static string? MainToolNamed(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith("pickaxe") ? PickaxeKind : n.EndsWith("axe") ? AxeKind : n.EndsWith("hoe") ? HoeKind
            : n.Contains("watering") || n.EndsWith("can") ? CanKind : n.EndsWith("scythe") ? ScytheKind
            : n.Contains("rod") || n.Contains("pole") ? RodKind : null;
    }

    /// <summary>For store: your main tools are not put into a chest. Returns the jobs without them and says so in <paramref name="problems"/>.</summary>
    private static List<(Item item, int count)> KeepMainTools(List<(Item item, int count)> jobs, List<string> problems)
    {
        var tools = jobs.Where(j => MainToolKind(j.item) != null).Select(j => j.item.Name).ToList();
        if (tools.Count == 0) return jobs;
        problems.Add($"Not stored: {string.Join(", ", tools)}. Your Axe, Hoe, Watering Can, Pickaxe, Scythe and fishing rod stay in the pack: harvesting Wheat, " +
                     "clearing, mining, watering and fishing need them. To free slots, ship or store other things, or upgrade_backpack (2,000g for 24 slots).");
        return jobs.Where(j => MainToolKind(j.item) == null).ToList();
    }
}
