using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Objects;

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
        var parts = steps.Take(2).Select((s, i) => $"{i + 1}) {s.What} — {s.Call}: {CostText(s.Gold, s.Materials)}{Lacking(s)}. {s.Why}");
        return "Next farm buildings (save up for them; seeds for a season you can't finish and spare purchases come after): " + string.Join(" ", parts);
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
