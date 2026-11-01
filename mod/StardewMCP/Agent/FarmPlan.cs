using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewMCP.Agent;

/// <summary>
/// The next farm buildings to save up for, in order: a Coop and chickens, the house upgrade (the kitchen: cooking needs it),
/// then a Barn. Days 1-120: buy_animal and upgrade_house were never called and build only listed once, while animals was
/// called 19 times with no animal on the farm, and 10,000g sat in AI2's wallet for three weeks. Shown by animals when there
/// is nothing to look after, by goals and in the morning briefing, with what is still missing (money, Wood, Stone).
/// </summary>
public static partial class AgentTools
{
    private sealed record PlanStep(string What, string Call, int Gold, List<(string ItemId, string Label, int Amount)> Materials, string Why);

    /// <summary>"Next farm buildings: ..." with the first two open steps and what each still lacks, or "" when all are done.</summary>
    internal static string FarmPlanText()
    {
        List<PlanStep> steps;
        try { steps = OpenPlanSteps(); }
        catch (Exception) { return ""; }
        if (steps.Count == 0) return "";
        var shown = steps.Take(3).ToList();
        var parts = shown.Select((s, i) => $"{i + 1}) {s.What} — {s.Call}: {CostText(s.Gold, s.Materials)}{Lacking(s)}. {s.Why}");
        return "Next farm buildings (save up for them; seeds for a season you can't finish and spare purchases come after): " + string.Join(" ", parts) + GatherAdvice(shown);
    }

    /// <summary>Where the missing materials come from: never a shop (decided 2026-11-01). Day 98: AI1 had 9,814g and 59 of the Coop's 300 Wood, and about 345 Wood came in over 44 days.</summary>
    private static string GatherAdvice(List<PlanStep> steps)
    {
        bool Short(string qid) => steps.Any(s => s.Materials.Any(m => ItemRegistry.QualifyItemId(m.ItemId) == qid
            && CountItem(m.ItemId) + ChestCount(m.ItemId) < m.Amount));
        var advice = "";
        if (Short("(O)388")) advice += " " + WoodAdvice();
        if (Short("(O)390")) advice += " Stone: mine, or clear_area over stones on the farm; never bought.";
        if (Short("(O)334")) advice += " Copper Bars: Copper Ore and Coal from mine, smelted in a Furnace (machine); never bought.";
        return advice;
    }

    /// <summary>The grown trees to chop on the farm, and tree seeds to plant when they run out.</summary>
    private static string WoodAdvice()
    {
        var farm = Game1.getFarm();
        var me = P.TilePoint;
        var grown = farm.terrainFeatures.Pairs.Where(kv => kv.Value is Tree { stump.Value: false } t && t.growthStage.Value >= 5)
            .Select(kv => new Point((int)kv.Key.X, (int)kv.Key.Y)).ToList();
        var text = "Wood: never bought. ";
        if (grown.Count > 0)
        {
            var near = grown.OrderBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y)).Take(5).Select(t => $"[{t.X},{t.Y}]");
            text += $"On the Farm stand {grown.Count} grown trees: use_tool(tool=\"Axe\", tiles=[{string.Join(",", near)}]) fells them for Wood; look shows more (T).";
        }
        if (grown.Count >= 10) return text;

        var seeds = new[] { "(O)311", "(O)309", "(O)310" }.Where(id => CountItem(id) + ChestCount(id) > 0).Select(id => ItemRegistry.GetData(id)?.InternalName ?? id).ToList();
        var spots = FreeSpots(farm, 1, 1, 40).Skip(30).Take(6).Select(t => $"[{t.X},{t.Y}]").ToList(); // away from the bin: those places are for buildings
        text += " Too few trees: " + (seeds.Count > 0
            ? $"take {string.Join(", ", seeds)} from your pack or chest and place(item, tiles=[{string.Join(",", spots)}]) on open ground, at least 2 tiles apart; they grow except in winter, chop them when grown."
            : "chopping and shaking trees drops Pine Cones, Acorns and Maple Seeds: keep them and place them on open ground to grow more.");
        return text;
    }

    private static List<PlanStep> OpenPlanSteps()
    {
        var farm = Game1.getFarm();
        var types = farm.buildings.Select(b => b.buildingType.Value ?? "").ToList();
        bool Has(string family) => types.Any(t => BuildingFamily(t) == BuildingFamily(family));
        bool building = farm.buildings.Any(b => b.isUnderConstruction());
        var catalog = RobinCatalog();
        PlanStep? Robin(string id, string why) =>
            catalog.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } spec
                ? new PlanStep($"a {spec.Name}", $"build(building=\"{spec.Id}\") at Robin's (ScienceHouse)", spec.Cost, spec.Materials, why)
                : null;

        var steps = new List<PlanStep?>();
        // The 12-slot pack came first in the third run: tools went into chests to make room and the Wheat stood ripe for days.
        if (P.MaxItems < 24)
            steps.Add(new PlanStep("the 24-slot backpack", "upgrade_backpack at Pierre's (SeedShop)", 2000, new(), "Twelve slots are full of tools and supplies; every other plan waits on room in the pack."));
        else if (P.MaxItems < 36)
            steps.Add(new PlanStep("the 36-slot backpack", "upgrade_backpack at Pierre's (SeedShop)", 10000, new(), "More room for the mines and the harvest."));
        if (!Has("Coop"))
            steps.Add(Robin("Coop", "Chickens lay an egg a day; eggs, mayonnaise and the animal achievements need it."));
        else if (!RanchText.AllFarmAnimals().Any(t => RanchText.IsMine(t.animal)) && !building)
            steps.Add(new PlanStep("chickens for your Coop", "buy_animal(type=\"Chicken\") at Marnie's (AnimalShop, 9:00-16:00)", 800, new(), "Pet them and collect eggs every day."));
        // Day 62: Clint's price was out of reach once and upgrade_tool never came back, with 10,000g idle at day 100.
        if (P.Items.Any(i => i is StardewValley.Tools.Pickaxe { UpgradeLevel: 0 }) && P.toolBeingUpgraded.Value == null)
            steps.Add(new PlanStep("the Copper Pickaxe", "upgrade_tool(tool=\"Pickaxe\") at Clint's (Blacksmith)", 2000, new() { ("(O)334", "Copper Bar", 5) }, "Rocks break with fewer swings, so the same energy goes more floors down the mines."));
        if (P.HouseUpgradeLevel == 0 && P.daysUntilHouseUpgrade.Value <= 0)
        {
            var (gold, itemId, count, label) = HouseUpgradeCost(0);
            var mats = itemId == null ? new List<(string, string, int)>() : new List<(string, string, int)> { ("(O)" + itemId, label, count) };
            steps.Add(new PlanStep("the house upgrade (kitchen)", "upgrade_house at Robin's (ScienceHouse)", gold, mats, "Cooking (Gourmet Chef and the other cooking goals) needs the kitchen; it also allows a spouse and children."));
        }
        if (!Has("Barn"))
            steps.Add(Robin("Barn", "Cows and goats: milk, cheese, and Full House needs more animals."));
        return steps.Where(s => s != null).Select(s => s!).ToList();
    }

    /// <summary>" (you have 3,200g: 800g short; Wood 120 of 300, 40 more in chests)": what the farmer still lacks, or " (you have everything: ...)".</summary>
    private static string Lacking(PlanStep s)
    {
        var missing = new List<string>();
        if (P.Money < s.Gold) missing.Add($"{s.Gold - P.Money:N0}g short (you have {P.Money:N0}g)");
        bool fetch = false;
        foreach (var (id, label, amount) in s.Materials)
        {
            int pack = P.Items.Where(i => i != null && i.QualifiedItemId == ItemRegistry.QualifyItemId(id)).Sum(i => i.Stack);
            int chests = ChestCount(id);
            if (pack >= amount) continue;
            if (pack + chests >= amount) { fetch = true; missing.Add($"{label}: {pack} in your pack, take {amount - pack} from the chests"); }
            else missing.Add($"{label}: {pack + chests} of {amount} (pack and chests)");
        }
        if (missing.Count == 0) return " (you have everything: go now)";
        return $" (missing: {string.Join("; ", missing)}{(fetch ? "; materials must be in your pack at Robin's" : "")})";
    }

    /// <summary>How many of this item the shared chests on the farm maps hold.</summary>
    private static int ChestCount(string itemId)
    {
        var qid = ItemRegistry.QualifyItemId(itemId);
        int n = 0;
        foreach (var name in new[] { "Farm", "FarmHouse", P.homeLocation.Value }.Where(s => !string.IsNullOrEmpty(s)).Distinct())
            if (Game1.getLocationFromName(name) is { } loc)
                n += loc.objects.Values.OfType<Chest>().Where(c => c.playerChest.Value).SelectMany(c => c.Items).Where(i => i?.QualifiedItemId == qid).Sum(i => i!.Stack);
        return n;
    }
}
