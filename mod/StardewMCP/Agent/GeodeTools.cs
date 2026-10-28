using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using StardewValley;
using StardewValley.Locations;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// crack_geode: Clint at the Blacksmith breaks geodes open for 25g each. The tool pays and draws the treasure with the
/// game's own Utility.getTreasureFromGeode (what the geode menu uses), so the odds are the game's. It does not click through
/// the GeodeMenu; the menu's animation and sound are skipped. What each kind is and where it comes from is in <see cref="GeodeGuide"/>.
/// </summary>
public static partial class AgentTools
{
    private const int GeodeFee = 25;

    /// <summary>Qualified ids of what Clint opens when the game can't be asked: Geode, Frozen, Magma, Omni, Artifact Trove, Golden Coconut, Mystery Boxes.</summary>
    private static readonly string[] GeodeIds = { "(O)535", "(O)536", "(O)537", "(O)749", "(O)275", "(O)791", "(O)MysteryBox", "(O)GoldenMysteryBox" };

    /// <summary>Anything the geode menu takes (Utility.IsGeode also covers modded geodes); the fixed list if that can't be called.</summary>
    internal static bool IsGeode(Item it)
    {
        try { return Utility.IsGeode(it, false); }
        catch (Exception) { return GeodeIds.Contains(it.QualifiedItemId); }
    }

    private static List<ToolDef> GeodeToolDefs() => new()
    {
        new()
        {
            Name = "crack_geode",
            Description = $"Have Clint crack geodes open ({GeodeFee}g each): Geode, Frozen Geode, Magma Geode, Omni Geode, Artifact Trove, Golden Coconut, Mystery Box. " +
                          "You walk to the Blacksmith yourself when Clint's shop is open. item: which geode; leave it empty for a guide to the geodes you carry " +
                          "(where each kind comes from, what comes out) without moving. quantity: how many (default: all of that kind). " +
                          "The result lists what came out and which finds the Museum still needs: donate them with museum before you sell them.",
            Schema = Obj(new
            {
                item = Str("Geode name, e.g. Geode, Frozen Geode, Omni Geode; empty for the guide"),
                quantity = Int("How many to crack (default: all of that kind)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => S(a, "item").Trim().Length == 0,
            Run = CrackGeode
        }
    };

    /// <summary>The guide: the geodes carried with where they come from and what they give, then the other kinds in one line each.</summary>
    private static string GeodeGuideText(List<Item> carried)
    {
        var sb = new StringBuilder();
        if (carried.Count == 0)
            sb.Append("You carry no geodes.");
        else
        {
            sb.Append($"Geodes you carry: {Stacks(carried)}. Clint cracks each for {GeodeFee}g at the Blacksmith: crack_geode(item=...) walks you there.");
            foreach (var g in carried.GroupBy(i => i.QualifiedItemId))
            {
                var kind = GeodeGuide.For(g.Key);
                sb.Append($"\n- {g.First().DisplayName}: {(kind != null ? kind.Gives : "what this geode holds is up to the game data")}");
            }
        }
        var have = new HashSet<string>(carried.Select(i => i.QualifiedItemId));
        sb.Append("\nWhere geodes come from:");
        foreach (var k in GeodeGuide.Kinds.Where(k => !have.Contains(k.Id)))
            sb.Append($"\n- {k.Name}: {k.Where}");
        sb.Append("\nMinerals, gems and artifacts go to the Museum first (museum, one of each kind); Gunther gives rewards as the collection grows.");
        return sb.ToString();
    }

    private static IEnumerator CrackGeode(JsonElement a, CallContext c)
    {
        var want = S(a, "item").Trim();
        var carried = P.Items.Where(i => i != null && IsGeode(i)).ToList();
        if (want.Length == 0)
        {
            c.Result = GeodeGuideText(carried);
            yield break;
        }
        if (carried.Count == 0)
        {
            c.Fail(Reasons.NoItem, "You carry no geodes. " + GeodeGuideText(carried));
            yield break;
        }

        var kinds = carried.GroupBy(i => i.QualifiedItemId).Select(g => g.First()).ToList();
        int pick = GeodeGuide.Match(kinds.Select(i => i.DisplayName).ToList(), want);
        if (pick < 0 && kinds.Count == 1 && want.IndexOf("geode", StringComparison.OrdinalIgnoreCase) >= 0)
            pick = 0;
        if (pick < 0)
        {
            c.Fail(Reasons.NoItem, $"No geode called \"{want}\" in your pack. You carry: {Stacks(carried)}.");
            yield break;
        }
        var qid = kinds[pick].QualifiedItemId;
        var label = kinds[pick].DisplayName;

        if (Loc.Name != "Blacksmith")
        {
            var trip = new CallContext();
            yield return TravelTo("Blacksmith", trip);
            if (!trip.Ok || Loc.Name != "Blacksmith")
            {
                c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get to Clint at the Blacksmith: {trip.Result}");
                yield break;
            }
        }
        if (!Loc.characters.Any(n => n.Name == "Clint"))
        {
            c.Fail("Clint is not here right now (his shop is open 9:00-16:00 and closed on Fridays and festival days). Come back during opening hours; calendar shows today's hours.");
            yield break;
        }

        int total = carried.Where(i => i.QualifiedItemId == qid).Sum(i => i.Stack);
        int qty = Math.Clamp(I(a, "quantity", total), 1, 999);
        var found = new List<(string label, int count)>();
        var museumWants = new List<string>();
        var museum = Game1.getLocationFromName("ArchaeologyHouse") as LibraryMuseum;
        int cracked = 0, paid = 0;
        string why = "";
        bool countWorks = true;
        for (int n = 0; n < qty; n++)
        {
            int slot = FindItem(label, it => it.QualifiedItemId == qid);
            if (slot < 0) { why = $"no more {label}"; break; }
            if (P.Money < GeodeFee) { why = $"not enough money (Clint charges {GeodeFee}g each)"; break; }
            var stack = P.Items[slot];
            // The treasure comes from the game's table; the roll depends on the geodes-cracked counter.
            Item? treasure = Utility.getTreasureFromGeode(stack.getOne());
            if (treasure == null) { why = "the geode gave nothing"; break; }
            // Check the room before the geode and the money are spent.
            if (!P.couldInventoryAcceptThisItem(treasure)) { why = "your inventory is full"; break; }

            P.Money -= GeodeFee;
            paid += GeodeFee;
            stack.Stack--;
            if (stack.Stack <= 0) P.removeItemFromInventory(stack);
            P.addItemToInventoryBool(treasure);
            cracked++;
            // The counter seeds the next roll: without it every geode in this call would give the same thing.
            if (!CountGeodeCracked()) countWorks = false;

            found.Add((treasure.DisplayName, Math.Max(1, treasure.Stack)));
            if (MuseumKind(treasure) is { } kind && MuseumStillWants(museum, treasure))
            {
                var entry = $"{treasure.DisplayName} ({kind})";
                if (!museumWants.Contains(entry)) museumWants.Add(entry);
            }
        }
        yield return Ticks(2);

        if (cracked == 0)
        {
            c.Fail($"Could not crack {label}: {why}.");
            yield break;
        }
        var sb = new StringBuilder($"Clint cracked {cracked}x {label} for {paid}g. Found: {Tally(found)}.");
        if (cracked < qty) sb.Append($" Stopped after {cracked} of {qty}: {why}.");
        if (museumWants.Count > 0) sb.Append($" NEW for the Museum (not on display yet): {string.Join(", ", museumWants)}: museum(action=donate, item=all) takes them there; don't sell them first.");
        if (!countWorks && cracked > 1) sb.Append(" Note: the geodes-cracked counter could not be advanced, so the finds may repeat.");
        sb.Append($" {P.Money}g left.");
        c.Result = sb.ToString();
    }

    /// <summary>"artifact", "mineral" or "gem" for finds the Museum collects, else null.</summary>
    private static string? MuseumKind(Item it)
    {
        if (it.Category == -2) return "gem";
        if (it.Category == -12) return "mineral";
        return Peek.Text(ItemRegistry.GetData(it.QualifiedItemId), "ObjectType") == "Arch" ? "artifact" : null;
    }

    /// <summary>The Museum would take this item now; true when it can't be asked, so the hint errs on the side of donating.</summary>
    private static bool MuseumStillWants(LibraryMuseum? museum, Item it)
    {
        if (museum == null || it is not SObject) return museum == null;
        try { return MuseumAccepts(museum, it); }
        catch (LateBoundException) { return true; }
    }

    /// <summary>Stats.GeodesCracked + 1, as the geode menu does after each geode. Reflection keeps a renamed counter from breaking the build.</summary>
    private static bool CountGeodeCracked()
    {
        try
        {
            var prop = AccessTools.Property(typeof(Stats), "GeodesCracked");
            if (prop == null || prop.GetGetMethod(true) == null || prop.GetSetMethod(true) == null) return false;
            var current = Convert.ToUInt64(prop.GetValue(Game1.stats));
            prop.SetValue(Game1.stats, Convert.ChangeType(current + 1, prop.PropertyType));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
