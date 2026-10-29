using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// goals: the long-run target (every achievement for each farmer, no Joja) measured from the game's own records:
/// money earned, Museum pieces, hearts, recipes cooked and crafted, fish caught, items shipped, Marlon's monster goals,
/// mine depth, skills, house, tools and family. The same reading is written every night to progress_pN.json and
/// progress_NNN_pN.json next to the logs, so the run keeps a record of what was shipped, cooked, crafted and caught and what is still missing.
/// The achievement list and its wording are game-free in <see cref="GoalText"/>.
/// </summary>
public static partial class AgentTools
{
    private static readonly string[] GoalAreas = { "summary", "shipping", "cooking", "crafting", "fish", "museum", "friends", "monsters", "upgrades", "family" };

    private static ToolDef GoalsTool() => new()
    {
        Name = "goals",
        Description = "Your long-run goal: every achievement (except Joja), measured from the game's own records. area=summary (default): each achievement with your progress " +
                      "and the closest ones to finish. area=shipping: items never shipped (Full Shipment). cooking: dishes not cooked yet, the recipes you know and how to learn the rest. " +
                      "crafting: the same for crafting recipes. fish: fish never caught. museum: pieces still missing. friends: hearts with every villager. " +
                      "monsters: Marlon's Monster Eradication Goals. upgrades: tools, weapon, backpack, house. family: spouse and children. Costs no game time.",
        Schema = Obj(new { area = new { type = "string", @enum = GoalAreas, description = "summary (default), shipping, cooking, crafting, fish, museum, friends, monsters, upgrades or family" } }),
        Instant = true, AppendStatus = false,
        Run = (a, c) => KnowledgeTools.Report(c, "goals", () => GoalsReport(a))
    };

    // ------------------------------------------------------------------ reading the game

    /// <summary>Everything the goals tool and the progress file need, read once.</summary>
    internal sealed class GoalRead
    {
        public readonly Dictionary<GoalKind, (long have, long need)> Counts = new();
        public HashSet<string> Earned = new();
        public List<(string name, string category)> Shipped = new(), NotShipped = new();
        public List<string> Cooked = new(), KnownNotCooked = new();
        public List<(string name, string how)> UnknownDishes = new();
        public List<string> Crafted = new(), KnownNotCrafted = new();
        public List<(string name, string how)> UnknownCrafts = new();
        public List<string> FishCaught = new(), FishMissing = new();
        public List<string> MuseumMissing = new();
        public List<(string name, int hearts, int max)> Friends = new();
        public List<(string goal, int kills, int need, string targets)> Monsters = new();
    }

    internal static GoalRead ReadGoals(Farmer p)
    {
        var r = new GoalRead();
        try
        {
            // English names, so they match GoalText whatever language the game runs in.
            var achievements = Game1.content.Load<Dictionary<int, string>>("Data\\Achievements", LocalizedContentManager.LanguageCode.en);
            foreach (var (id, raw) in achievements)
                if (p.achievements.Contains(id)) r.Earned.Add(GoalText.Key(raw.Split('^')[0]));
        }
        catch (Exception) { /* earned flags are a bonus; the counts decide */ }

        // Money, quests, skills, house, mines, Community Center, stardrops.
        r.Counts[GoalKind.Money] = ((long)p.totalMoneyEarned, 0);
        r.Counts[GoalKind.Quests] = (p.stats.QuestsCompleted, 0);
        var levels = new[] { p.farmingLevel.Value, p.miningLevel.Value, p.foragingLevel.Value, p.fishingLevel.Value, p.combatLevel.Value };
        r.Counts[GoalKind.SkillOne] = (levels.Count(l => l >= 10) > 0 ? 1 : 0, 0);
        r.Counts[GoalKind.SkillAll] = (levels.Count(l => l >= 10), 0);
        r.Counts[GoalKind.House] = (p.HouseUpgradeLevel, 0);
        r.Counts[GoalKind.MineBottom] = (p.deepestMineLevel, 0);
        r.Counts[GoalKind.CommunityCenter] = (BundleReader.HasMail(p, "ccIsComplete") ? 1 : 0, 0);
        r.Counts[GoalKind.Stardrops] = (GoalText.Stardrops(p.MaxStamina), 0);
        r.Counts[GoalKind.FishTotal] = (p.stats.FishCaught, 0);
        r.Counts[GoalKind.FullHouse] = ((string.IsNullOrEmpty(p.spouse) && p.team.GetSpouse(p.UniqueMultiplayerID) == null ? 0 : 1) + Math.Min(2, p.getChildrenCount()), 0);
        r.Counts[GoalKind.Arcade] = (0, 0);

        ReadShipping(p, r);
        ReadCooking(p, r);
        ReadCrafting(p, r);
        ReadFish(p, r);
        ReadMuseum(r);
        ReadFriends(p, r);
        ReadMonsters(p, r);
        return r;
    }

    private static void ReadShipping(Farmer p, GoalRead r)
    {
        var cropIds = new HashSet<string>();
        try
        {
            foreach (var crop in Game1.cropData.Values)
                if (crop?.HarvestItemId is { } h) cropIds.Add(ItemRegistry.QualifyItemId(h)?.Replace("(O)", "") ?? h);
        }
        catch (Exception) { }
        int poly = 0, mono = 0, crops = 0;
        foreach (var (id, data) in Game1.objectData)
        {
            if (data == null || !SObjectShippable(id, data.Category, data.Type)) continue;
            var name = ItemRegistry.GetData("(O)" + id)?.DisplayName ?? data.Name ?? id;
            int shipped = p.basicShipped.TryGetValue(id, out var n) ? n : 0;
            (shipped > 0 ? r.Shipped : r.NotShipped).Add((name, GoalText.CategoryName(data.Category)));
            if (cropIds.Contains(id) && data.Category is -75 or -79 or -80)
            {
                crops++;
                if (shipped >= 15) poly++;
                mono = Math.Max(mono, shipped);
            }
        }
        r.Counts[GoalKind.ShippedAll] = (r.Shipped.Count, r.Shipped.Count + r.NotShipped.Count);
        r.Counts[GoalKind.Polyculture] = (poly, crops);
        r.Counts[GoalKind.Monoculture] = (mono, 0);
    }

    private static bool SObjectShippable(string id, int category, string type)
    {
        try { return StardewValley.Object.isPotentialBasicShipped(id, category, type); }
        catch (Exception) { return false; }
    }

    private static void ReadCooking(Farmer p, GoalRead r)
    {
        CraftingRecipe.InitShared();
        var tv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var v in Game1.content.Load<Dictionary<string, string>>("Data\\TV\\CookingChannel").Values)
                tv.Add(v.Split('/')[0].Trim());
        }
        catch (Exception) { }
        var shops = RecipeShops();
        foreach (var (key, data) in CraftingRecipe.cookingRecipes)
        {
            string name = key, outId = "";
            try
            {
                var cr = new CraftingRecipe(key, true);
                name = cr.DisplayName;
                outId = cr.itemToProduce.FirstOrDefault() ?? "";
            }
            catch (Exception) { }
            bool cooked = outId.Length > 0 && p.recipesCooked.ContainsKey(outId);
            if (cooked) r.Cooked.Add(name);
            else if (p.cookingRecipes.ContainsKey(key)) r.KnownNotCooked.Add(name);
            else
            {
                var how = new List<string>();
                var unlock = GoalText.Unlock(data.Split('/').ElementAtOrDefault(3));
                if (unlock.Length > 0) how.Add(unlock);
                if (tv.Contains(key)) how.Add("The Queen of Sauce on TV (watch_tv on Sundays, reruns on Wednesdays)");
                if (shops.TryGetValue(key, out var shop)) how.Add($"buy the recipe at {shop}");
                r.UnknownDishes.Add((name, how.Count > 0 ? string.Join(" or ", how) : "a letter, event or shop"));
            }
        }
        r.Counts[GoalKind.Cooked] = (r.Cooked.Count, 0);
        r.Counts[GoalKind.CookedAll] = (r.Cooked.Count, CraftingRecipe.cookingRecipes.Count);
    }

    /// <summary>Recipe name → where a shop sells it (Data/Shops items marked IsRecipe).</summary>
    private static Dictionary<string, string> RecipeShops()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (shopId, shop) in DataLoader.Shops(Game1.content))
                foreach (var item in shop.Items ?? new())
                    if (item.IsRecipe && item.ItemId != null && ItemRegistry.GetData(item.ItemId) is { } d && !map.ContainsKey(d.InternalName))
                        map[d.InternalName] = ShopPlaces.TryGetValue(shopId, out var place) ? place : shopId;
        }
        catch (Exception) { }
        return map;
    }

    private static void ReadCrafting(Farmer p, GoalRead r)
    {
        CraftingRecipe.InitShared();
        foreach (var (key, data) in CraftingRecipe.craftingRecipes)
        {
            string name = key;
            try { name = new CraftingRecipe(key, false).DisplayName; } catch (Exception) { }
            if (p.craftingRecipes.TryGetValue(key, out int made) && made > 0) r.Crafted.Add(name);
            else if (p.craftingRecipes.ContainsKey(key)) r.KnownNotCrafted.Add(name);
            else r.UnknownCrafts.Add((name, HowToLearn(data)));
        }
        r.Counts[GoalKind.Crafted] = (r.Crafted.Count, 0);
        r.Counts[GoalKind.CraftedAll] = (r.Crafted.Count, CraftingRecipe.craftingRecipes.Count);
    }

    private static void ReadFish(Farmer p, GoalRead r)
    {
        foreach (var (id, raw) in DataLoader.Fish(Game1.content))
        {
            if (FishGuideText.LegendaryIds.Contains(id) && int.TryParse(id, out var n) && n >= 898) continue; // Qi's Extended Family: not in the collection
            var name = ItemRegistry.GetData("(O)" + id)?.DisplayName ?? raw.Split('/')[0];
            (p.fishCaught.ContainsKey("(O)" + id) ? r.FishCaught : r.FishMissing).Add(name);
        }
        r.Counts[GoalKind.FishKinds] = (r.FishCaught.Count, 0);
        r.Counts[GoalKind.FishAll] = (r.FishCaught.Count, r.FishCaught.Count + r.FishMissing.Count);
    }

    private static void ReadMuseum(GoalRead r)
    {
        int donated = 0, total = 0;
        if (Game1.getLocationFromName("ArchaeologyHouse") is LibraryMuseum museum)
            donated = museum.museumPieces.Count();
        foreach (var (id, data) in Game1.objectData)
        {
            bool any;
            try { any = LibraryMuseum.IsItemSuitableForDonation("(O)" + id, false); }
            catch (Exception) { any = false; }
            if (!any) continue;
            total++;
            bool missing;
            try { missing = LibraryMuseum.IsItemSuitableForDonation("(O)" + id, true); }
            catch (Exception) { missing = false; }
            if (missing) r.MuseumMissing.Add(ItemRegistry.GetData("(O)" + id)?.DisplayName ?? data.Name ?? id);
        }
        r.Counts[GoalKind.MuseumPieces] = (donated, 0);
        r.Counts[GoalKind.MuseumAll] = (donated, total);
    }

    private static void ReadFriends(Farmer p, GoalRead r)
    {
        int at5 = 0, at10 = 0;
        foreach (var (name, data) in Game1.characterData)
        {
            if (data == null || string.Equals(data.CanSocialize, "FALSE", StringComparison.OrdinalIgnoreCase)) continue;
            if (!p.friendshipData.ContainsKey(name) && data.CanSocialize != null) continue; // conditional villagers (Krobus, Leo...) count once met
            int hearts = p.getFriendshipHeartLevelForNPC(name);
            bool dateable = data.CanBeRomanced;
            bool partner = p.friendshipData.TryGetValue(name, out var f) && (f.IsDating() || f.IsMarried());
            int max = partner && f!.IsMarried() ? 14 : dateable && !partner ? 8 : 10;
            r.Friends.Add((NpcShownName(name), hearts, max));
            if (hearts >= 5) at5++;
            if (hearts >= 10) at10++;
        }
        r.Counts[GoalKind.FriendsAt5] = (at5, 0);
        r.Counts[GoalKind.FriendsAt10] = (at10, 0);
    }

    private static string NpcShownName(string name)
    {
        try { return Game1.getCharacterFromName(name)?.Name ?? name; }
        catch (Exception) { return name; }
    }

    private static void ReadMonsters(Farmer p, GoalRead r)
    {
        int done = 0, all = 0;
        try
        {
            foreach (var (key, q) in DataLoader.MonsterSlayerQuests(Game1.content))
            {
                if (q?.Targets == null) continue;
                all++;
                int kills = q.Targets.Sum(t => p.stats.getMonstersKilled(t));
                if (kills >= q.Count) done++;
                r.Monsters.Add((key, kills, q.Count, string.Join(", ", q.Targets)));
            }
        }
        catch (Exception) { }
        r.Counts[GoalKind.MonsterGoals] = (done, all);
    }

    // ------------------------------------------------------------------ the tool

    private static (bool ok, string text) GoalsReport(System.Text.Json.JsonElement a)
    {
        var area = S(a, "area").Trim().ToLowerInvariant();
        if (area.Length == 0) area = "summary";
        if (!GoalAreas.Contains(area)) return (false, $"Unknown area \"{area}\". Use: {string.Join(", ", GoalAreas)}.");
        var r = ReadGoals(P);
        try { if (DayStats.Log is { } log) WriteProgress(log, Rt.Log.Player, r, null); } catch (Exception) { }
        return (true, area switch
        {
            "shipping" => ShippingText(r),
            "cooking" => CookingText(r),
            "crafting" => CraftingText(r),
            "fish" => $"Fish caught {r.FishCaught.Count}/{r.FishCaught.Count + r.FishMissing.Count}. Never caught: {GoalText.List(r.FishMissing, 80)}.\nfish_guide(name) says where and when each one bites; crab pot fish need a Crab Pot.",
            "museum" => $"Museum: {r.Counts[GoalKind.MuseumAll].have}/{r.Counts[GoalKind.MuseumAll].need} on display. Still missing: {GoalText.List(r.MuseumMissing, 80)}.\nArtifacts come from artifact spots (hoe the wriggling worms), fishing chests, geodes and Artifact Troves; minerals from geodes (crack_geode) and the mines.",
            "friends" => FriendsText(r),
            "monsters" => MonstersText(r),
            "upgrades" => UpgradesText(),
            "family" => FamilyText(),
            _ => SummaryText(r)
        });
    }

    private static (long have, long need) GoalCount(GoalRead r, GoalText.Goal g)
    {
        var (have, total) = r.Counts.TryGetValue(g.Kind, out var c) ? c : (0, 0);
        return (have, g.Target > 0 ? g.Target : total);
    }

    private static string SummaryText(GoalRead r)
    {
        var sb = new StringBuilder($"Goal: every achievement for {P.Name} (no Joja). ");
        var rows = GoalText.Achievements.Select(g => (g, c: GoalCount(r, g), earned: r.Earned.Contains(GoalText.Key(g.Name)))).ToList();
        int done = rows.Count(x => x.earned || x.g.Kind != GoalKind.Arcade && x.c.need > 0 && x.c.have >= x.c.need);
        sb.Append($"{done}/{rows.Count(x => x.g.Kind != GoalKind.Arcade)} done.");
        foreach (var x in rows)
            sb.Append('\n').Append(GoalText.Line(x.g, x.c.have, x.c.need, x.earned));
        var next = rows.Where(x => x.g.Kind != GoalKind.Arcade && !x.earned && !(x.c.need > 0 && x.c.have >= x.c.need))
            .OrderByDescending(x => GoalText.Fraction(x.g, x.c.have, x.c.need)).Take(4).Select(x => x.g.Name);
        sb.Append($"\nClosest: {string.Join(", ", next)}. Details: goals(area=shipping|cooking|crafting|fish|museum|friends|monsters|upgrades|family).");
        var plan = FarmPlanText();
        if (plan.Length > 0) sb.Append('\n').Append(plan);
        sb.Append("\nEvery day: farm chores first, then one long goal (mine floors, a new dish, new items to ship, gifts and talks), and bed before 01:00.");
        return sb.ToString();
    }

    private static string ShippingText(GoalRead r)
    {
        var sb = new StringBuilder($"Full Shipment: shipped {r.Shipped.Count}/{r.Shipped.Count + r.NotShipped.Count} kinds of items. One of each is enough.");
        var carried = P.Items.Where(i => i is StardewValley.Object o && !o.bigCraftable.Value && r.NotShipped.Any(n => n.name == i.DisplayName)).Select(i => i.DisplayName).Distinct().ToList();
        if (carried.Count > 0) sb.Append($"\nIn your pack and never shipped: {GoalText.List(carried, 20)}: ship(item, quantity=1) at the Farm bin.");
        foreach (var g in r.NotShipped.GroupBy(n => n.category).OrderByDescending(g => g.Count()))
            sb.Append($"\n- {g.Key}: {GoalText.List(g.Select(n => n.name), 30)}");
        sb.Append("\nArtisan goods (wine, cheese, bars...) come from machines (machine), fish from fish, minerals from the mines and geodes.");
        return sb.ToString();
    }

    private static string CookingText(GoalRead r)
    {
        CraftingRecipe.InitShared();
        var sb = new StringBuilder($"Cooking: {r.Cooked.Count}/{CraftingRecipe.cookingRecipes.Count} dishes cooked. All cooking is done with cook at the stove of your own house (it needs the kitchen: the first house upgrade); the fridge counts as your pack.");
        if (r.KnownNotCooked.Count > 0)
        {
            sb.Append("\nYou know these but never cooked them (cook each once):");
            foreach (var name in r.KnownNotCooked.Take(40))
            {
                var key = FindRecipeKey(name, true);
                string need = "";
                try { if (key != null) need = Ingredients(new CraftingRecipe(key, true)); } catch (Exception) { }
                sb.Append($"\n- {name}{(need.Length > 0 ? ": " + need : "")}");
            }
        }
        sb.Append($"\nNot learned yet ({r.UnknownDishes.Count}):");
        foreach (var (name, how) in r.UnknownDishes.Take(60))
            sb.Append($"\n- {name}: {how}");
        sb.Append($"\nAlready cooked: {GoalText.List(r.Cooked, 60)}.");
        return sb.ToString();
    }

    private static string CraftingText(GoalRead r)
    {
        CraftingRecipe.InitShared();
        var sb = new StringBuilder($"Crafting: {r.Crafted.Count}/{CraftingRecipe.craftingRecipes.Count} recipes crafted at least once. craft works anywhere; ingredients come from your pack and your chests.");
        if (r.KnownNotCrafted.Count > 0)
        {
            sb.Append("\nKnown, never crafted (craft each once, then place or ship it):");
            foreach (var name in r.KnownNotCrafted.Take(40))
            {
                var key = FindRecipeKey(name, false);
                string need = "";
                try { if (key != null) need = Ingredients(new CraftingRecipe(key, false)); } catch (Exception) { }
                sb.Append($"\n- {name}{(need.Length > 0 ? ": " + need : "")}");
            }
        }
        sb.Append($"\nNot learned yet ({r.UnknownCrafts.Count}):");
        foreach (var (name, how) in r.UnknownCrafts.Take(60))
            sb.Append($"\n- {name}: {how}");
        return sb.ToString();
    }

    private static string FriendsText(GoalRead r)
    {
        var sb = new StringBuilder("Hearts (max 10; 8 for a single you are not dating, 14 for a spouse). Talk to everyone every day and give gifts they like (villagers(name) shows known tastes); birthdays count 8x:");
        foreach (var (name, hearts, max) in r.Friends.OrderBy(f => f.hearts).ThenBy(f => f.name))
            sb.Append($"\n- {name}: {hearts}/{max}");
        return sb.ToString();
    }

    private static string MonstersText(GoalRead r)
    {
        var sb = new StringBuilder($"Protector of the Valley: {r.Counts[GoalKind.MonsterGoals].have}/{r.Counts[GoalKind.MonsterGoals].need} Monster Eradication Goals done (Marlon's board, AdventureGuild). Kill them with fight while you descend with mine.");
        foreach (var (goal, kills, need, targets) in r.Monsters)
            sb.Append($"\n{(kills >= need ? "✓" : "·")} {goal}: {Math.Min(kills, need)}/{need} ({targets})");
        sb.Append($"\nDeepest mine floor: {P.deepestMineLevel}. Slimes are on every floor, bats and Dust Sprites from floor 40, Skeletons around 70-80, Lava creatures past 80; Skull Cavern creatures need the Skull Key (floor 120).");
        return sb.ToString();
    }

    private static string UpgradesText()
    {
        var sb = new StringBuilder("Upgrades (max: Iridium tools, 36-slot backpack, house level 3):");
        string Level(int l) => l switch { 0 => "basic", 1 => "Copper", 2 => "Steel", 3 => "Gold", 4 => "Iridium", _ => $"level {l}" };
        foreach (var t in P.Items.OfType<Tool>().Where(t => t is Axe or Pickaxe or Hoe or WateringCan))
            sb.Append($"\n- {t.DisplayName}: {Level(t.UpgradeLevel)}{(t.UpgradeLevel < 4 ? " (upgrade_tool at Clint's)" : " ✓")}");
        if (P.toolBeingUpgraded.Value is { } up)
            sb.Append($"\n- Clint is upgrading your {up.DisplayName}: {(P.daysLeftForToolUpgrade.Value <= 0 ? "READY, upgrade_tool() picks it up" : $"{P.daysLeftForToolUpgrade.Value} day(s) left")}");
        sb.Append($"\n- Trash can: {Level(P.trashCanLevel)}{(P.trashCanLevel < 4 ? " (upgrade_tool(tool=\"Trash Can\"))" : " ✓")}");
        var rod = P.Items.OfType<FishingRod>().OrderByDescending(r => r.UpgradeLevel).FirstOrDefault();
        sb.Append($"\n- Fishing rod: {rod?.DisplayName ?? "none"} (Willy sells Fiberglass at Fishing 2 and Iridium at Fishing 6)");
        var weapon = P.Items.OfType<MeleeWeapon>().Where(w => !w.isScythe()).OrderByDescending(w => w.maxDamage.Value).FirstOrDefault();
        sb.Append($"\n- Weapon: {(weapon == null ? "none" : $"{weapon.DisplayName} ({weapon.minDamage.Value}-{weapon.maxDamage.Value} damage)")} (better ones: Marlon's shop in the AdventureGuild, mine chests on floors 10, 20... and the deeper floors)");
        sb.Append($"\n- Backpack: {P.MaxItems} slots{(P.MaxItems < 36 ? " (upgrade_backpack at Pierre's)" : " ✓")}");
        sb.Append($"\n- House: level {P.HouseUpgradeLevel}{(P.HouseUpgradeLevel < 3 ? " (upgrade_house at Robin's; level 1 = kitchen, 2 = nursery for children)" : " ✓")}");
        return sb.ToString();
    }

    private static string FamilyText()
    {
        var sb = new StringBuilder("Full House needs a spouse and two children.");
        var spouse = P.spouse;
        var team = P.team.GetSpouse(P.UniqueMultiplayerID);
        if (!string.IsNullOrEmpty(spouse))
            sb.Append($"\nSpouse: {NpcShownName(spouse)}{(P.friendshipData.TryGetValue(spouse, out var f) && f.IsEngaged() ? " (engaged: the wedding comes by itself)" : "")}.");
        else if (team != null)
            sb.Append($"\nMarried to another farmer: {Game1.getFarmerMaybeOffline(team.Value)?.Name}.");
        else
            sb.Append("\nNot married. Dating needs 8 hearts and a Bouquet (bouquet, from Pierre's), marriage 10 hearts, the Mermaid's Pendant (Old Mariner on the Beach on rainy days, 5,000g) and a house with a kitchen (mermaid_pendant).");
        sb.Append($"\nChildren: {P.getChildrenCount()}/2. They need the house at level 2 (nursery) and 10+ hearts with your spouse; your spouse asks at night and the mod answers yes for you.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the progress file

    /// <summary>
    /// progress_pN.json (always the latest) and progress_NNN_pN.json (the state at the end of farm day NNN, written at rollover):
    /// every achievement with its count, and the full lists of shipped / not shipped, cooked / not cooked, crafted, fish, Museum and hearts.
    /// </summary>
    internal static void WriteProgress(AgentLog log, string player, GoalRead? r, int? day)
    {
        r ??= ReadGoals(P);
        var doc = new
        {
            farmer = P.Name,
            player,
            day = AgentLog.FarmDay,
            date = $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}",
            achievements = GoalText.Achievements.Select(g =>
            {
                var (have, need) = GoalCount(r, g);
                bool earned = r.Earned.Contains(GoalText.Key(g.Name));
                return new { name = g.Name, have, need, done = earned || g.Kind != GoalKind.Arcade && need > 0 && have >= need, earned };
            }).ToList(),
            shipping = new { shipped = r.Shipped.Select(s => s.name).OrderBy(n => n).ToList(), notShipped = r.NotShipped.Select(s => s.name).OrderBy(n => n).ToList() },
            cooking = new { cooked = r.Cooked.OrderBy(n => n).ToList(), knownNotCooked = r.KnownNotCooked.OrderBy(n => n).ToList(), notLearned = r.UnknownDishes.Select(d => new { name = d.name, how = d.how }).ToList() },
            crafting = new { crafted = r.Crafted.OrderBy(n => n).ToList(), knownNotCrafted = r.KnownNotCrafted.OrderBy(n => n).ToList(), notLearned = r.UnknownCrafts.Select(d => new { name = d.name, how = d.how }).ToList() },
            fish = new { caught = r.FishCaught.OrderBy(n => n).ToList(), notCaught = r.FishMissing.OrderBy(n => n).ToList() },
            museum = new { onDisplay = r.Counts[GoalKind.MuseumAll].have, total = r.Counts[GoalKind.MuseumAll].need, missing = r.MuseumMissing.OrderBy(n => n).ToList() },
            friends = r.Friends.OrderBy(f => f.name).ToDictionary(f => f.name, f => new { hearts = f.hearts, max = f.max }),
            monsters = r.Monsters.Select(m => new { goal = m.goal, kills = m.kills, need = m.need }).ToList(),
            mine = new { deepestFloor = P.deepestMineLevel },
            house = P.HouseUpgradeLevel,
            children = P.getChildrenCount(),
            spouse = string.IsNullOrEmpty(P.spouse) ? null : P.spouse
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions(AgentLog.Json) { WriteIndented = true });
        void Put(string file)
        {
            var path = Path.Combine(log.Dir, file);
            File.WriteAllText(path + ".tmp", json, Encoding.UTF8);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        Put($"progress_{player}.json");
        if (day is int d) Put($"progress_{d:000}_{player}.json");
    }
}
