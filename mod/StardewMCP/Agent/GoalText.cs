using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP.Agent;

/// <summary>What an achievement is measured by (<see cref="Goals"/> reads the number from the game).</summary>
public enum GoalKind
{
    Money, MuseumPieces, MuseumAll, FriendsAt5, FriendsAt10, Cooked, CookedAll, House, Crafted, CraftedAll,
    FishKinds, FishAll, FishTotal, Quests, Polyculture, Monoculture, FullHouse, SkillOne, SkillAll,
    MonsterGoals, ShippedAll, MineBottom, CommunityCenter, Stardrops, Arcade
}

/// <summary>
/// The goal of the long run: every achievement for each farmer, except Joja Co. Member (the Community Center route is taken).
/// The list, its targets and the plain-words "how" are game-free so the tests can check them; the numbers come from <see cref="Goals"/>.
/// </summary>
public static class GoalText
{
    public sealed record Goal(string Name, GoalKind Kind, long Target, string How);

    /// <summary>Every achievement worth chasing, in the order the goals tool lists them. Target 0 = "all of them" (the game decides the total).</summary>
    public static readonly IReadOnlyList<Goal> Achievements = new[]
    {
        new Goal("Greenhorn", GoalKind.Money, 15_000, "earn money (shipping and selling)"),
        new Goal("Cowpoke", GoalKind.Money, 50_000, "earn money"),
        new Goal("Homesteader", GoalKind.Money, 250_000, "earn money: artisan goods (machine) sell best"),
        new Goal("Millionaire", GoalKind.Money, 1_000_000, "earn money: kegs, preserves jars, animals"),
        new Goal("Legend", GoalKind.Money, 10_000_000, "earn money over the whole run"),
        new Goal("A New Friend", GoalKind.FriendsAt5, 1, "5 hearts with one villager (talk_to daily, give gifts they like)"),
        new Goal("Cliques", GoalKind.FriendsAt5, 4, "5 hearts with 4 villagers"),
        new Goal("Networking", GoalKind.FriendsAt5, 10, "5 hearts with 10 villagers"),
        new Goal("Popular", GoalKind.FriendsAt5, 20, "5 hearts with 20 villagers"),
        new Goal("Best Friends", GoalKind.FriendsAt10, 1, "10 hearts with one villager"),
        new Goal("The Beloved Farmer", GoalKind.FriendsAt10, 8, "10 hearts with 8 villagers (spouse and dated villagers count up to 14)"),
        new Goal("Cook", GoalKind.Cooked, 10, "cook 10 different dishes (cook, in your kitchen)"),
        new Goal("Sous Chef", GoalKind.Cooked, 25, "cook 25 different dishes"),
        new Goal("Gourmet Chef", GoalKind.CookedAll, 0, "cook every dish once: learn the recipes (TV, letters, shops) and cook each"),
        new Goal("Moving Up", GoalKind.House, 1, "upgrade the house once (upgrade_house at Robin's)"),
        new Goal("Living Large", GoalKind.House, 2, "upgrade the house twice"),
        new Goal("D.I.Y.", GoalKind.Crafted, 15, "craft 15 different items (craft)"),
        new Goal("Artisan", GoalKind.Crafted, 30, "craft 30 different items"),
        new Goal("Craft Master", GoalKind.CraftedAll, 0, "craft every crafting recipe once"),
        new Goal("Fisherman", GoalKind.FishKinds, 10, "catch 10 different fish (fish, fish_guide)"),
        new Goal("Ol' Mariner", GoalKind.FishKinds, 24, "catch 24 different fish"),
        new Goal("Master Angler", GoalKind.FishAll, 0, "catch every fish, crab pot ones and legendaries too"),
        new Goal("Mother Catch", GoalKind.FishTotal, 100, "catch 100 fish"),
        new Goal("Treasure Trove", GoalKind.MuseumPieces, 40, "donate 40 artifacts and minerals to the Museum (museum)"),
        new Goal("A Complete Collection", GoalKind.MuseumAll, 0, "donate every artifact and mineral"),
        new Goal("Gofer", GoalKind.Quests, 10, "finish 10 requests from the Help Wanted board (quests, accept_quest)"),
        new Goal("A Big Help", GoalKind.Quests, 40, "finish 40 requests"),
        new Goal("Polyculture", GoalKind.Polyculture, 15, "ship 15 of each crop (about 28 crops)"),
        new Goal("Monoculture", GoalKind.Monoculture, 300, "ship 300 of one crop"),
        new Goal("Full House", GoalKind.FullHouse, 3, "marry (bouquet, mermaid_pendant) and have 2 children (say yes when your spouse asks)"),
        new Goal("Singular Talent", GoalKind.SkillOne, 1, "reach level 10 in one skill"),
        new Goal("Master Of The Five Ways", GoalKind.SkillAll, 5, "reach level 10 in all five skills (farming, mining, foraging, fishing, combat)"),
        new Goal("Protector Of The Valley", GoalKind.MonsterGoals, 0, "finish every Monster Eradication Goal of Marlon's guild (mine, fight)"),
        new Goal("Full Shipment", GoalKind.ShippedAll, 0, "ship every item once (ship)"),
        new Goal("The Bottom", GoalKind.MineBottom, 120, "reach floor 120 of the Mines (mine)"),
        new Goal("Local Legend", GoalKind.CommunityCenter, 1, "restore the Community Center (bundles)"),
        new Goal("Mystery Of The Stardrops", GoalKind.Stardrops, 7, "find every Stardrop (Fair star tokens, Krobus, mine floor 100, museum reward, spouse, Old Master Cannoli, Master Angler)"),
        new Goal("Prairie King", GoalKind.Arcade, 1, "beat Journey of the Prairie King (Saloon arcade): an arcade minigame the tools cannot play"),
        new Goal("Fector's Challenge", GoalKind.Arcade, 1, "beat Journey of the Prairie King without dying: an arcade minigame the tools cannot play")
    };

    /// <summary>Joja Co. Member is left out on purpose: it closes the Community Center for both farmers.</summary>
    public static readonly string[] Skipped = { "Joja Co. Member" };

    /// <summary>Lower-case letters and digits only, so "Ol' Mariner" and "ol mariner" match.</summary>
    public static string Key(string name) => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static Goal? Find(string name) => Achievements.FirstOrDefault(g => Key(g.Name) == Key(name));

    /// <summary>"✓ Cook (12/10)" or "· Sous Chef 12/25: how". A target of 0 means the game's total, passed as <paramref name="need"/>.</summary>
    public static string Line(Goal g, long have, long need, bool earned)
    {
        if (g.Kind == GoalKind.Arcade)
            return $"– {g.Name}: {g.How}";
        bool done = earned || need > 0 && have >= need;
        var count = need > 0 ? $"{Math.Min(have, need)}/{need}" : $"{have}";
        return done ? $"✓ {g.Name} ({count})" : $"· {g.Name} {count}: {g.How}";
    }

    /// <summary>How far along a goal is, 0..1 (1 when done); arcade goals count as 0.</summary>
    public static double Fraction(Goal g, long have, long need) =>
        g.Kind == GoalKind.Arcade || need <= 0 ? 0 : Math.Clamp((double)have / need, 0, 1);

    /// <summary>The cooking recipe's unlock field (Data/CookingRecipes field 3) in words; "" when it says nothing useful.</summary>
    public static string Unlock(string? field)
    {
        var w = (field ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0 || w[0] is "none" or "null") return "";
        if (w[0] == "default") return "known from the start";
        if (w[0] == "f" && w.Length >= 3) return $"letter from {w[1]} at {w[2]} hearts";
        if (w[0] == "s" && w.Length >= 3) return $"{w[1]} level {w[2]}";
        if (w[0] == "l" && w.Length >= 2) return $"level {w[1]} of any skill";
        if (w.Length == 2 && int.TryParse(w[1], out var lvl)) return $"{w[0]} level {lvl}";
        return "";
    }

    /// <summary>Stardrops found from the maximum energy (270 at the start, +34 per stardrop).</summary>
    public static int Stardrops(int maxStamina) => Math.Clamp((maxStamina - 270) / 34, 0, 7);

    /// <summary>An object category number in words, for grouping the never-shipped list.</summary>
    public static string CategoryName(int category) => category switch
    {
        -2 => "Gems", -4 => "Fish", -5 => "Eggs", -6 => "Milk", -7 => "Cooked dishes", -8 => "Crafted items", -12 => "Minerals",
        -14 => "Meat", -15 => "Metal bars and ores", -16 => "Building materials", -17 => "Rare crops", -18 => "Animal products",
        -19 => "Fertilizer", -20 => "Trash", -21 => "Bait", -22 => "Tackle", -23 => "Shellfish and sea items", -24 => "Decor",
        -25 => "Cooking ingredients", -26 => "Artisan goods", -27 => "Syrups", -28 => "Monster loot", -74 => "Seeds",
        -75 => "Vegetables", -79 => "Fruit", -80 => "Flowers", -81 => "Forage", _ => "Other"
    };

    /// <summary>Names as a short comma list: the first <paramref name="max"/>, then "and N more".</summary>
    public static string List(IEnumerable<string> names, int max)
    {
        var all = names.ToList();
        if (all.Count == 0) return "none";
        var shown = string.Join(", ", all.Take(max));
        return all.Count > max ? $"{shown} and {all.Count - max} more" : shown;
    }
}
