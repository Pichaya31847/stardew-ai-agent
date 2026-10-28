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
/// craft and place, plus the chest tool (see ChestTools.cs). craft makes items from the recipes the farmer knows,
/// place puts an item on the ground. Crafted machines, chests and sprinklers are ordinary inventory items, so
/// place is the step that puts them in the world; interact(holding) then loads a machine and chest/menu use a chest.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> CraftingToolDefs() => new()
    {
        new()
        {
            Name = "craft",
            Description = "Craft items from the crafting recipes you know, using ingredients in your inventory (costs no energy). " +
                          "craft() lists your recipes with their ingredients, how many you can make right now and what the useful ones are for " +
                          "(Scarecrow, Sprinkler, Fertilizer, Furnace, Bee House, Preserves Jar, Keg...), plus useful recipes you can still learn. craft(recipe, quantity) makes that many " +
                          "(one craft can make several items, e.g. torches). Crafted machines, chests, sprinklers and fences are inventory items: put them in the world with place. " +
                          "Cooking recipes are not included: dishes are made with cook.",
            Schema = Obj(new
            {
                recipe = Str("Recipe name, e.g. \"Chest\" or \"Furnace\"; empty or \"list\" lists your recipes"),
                quantity = Int("How many crafts (default 1)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => { var r = S(a, "recipe").Trim(); return r.Length == 0 || r.Equals("list", StringComparison.OrdinalIgnoreCase); },
            Run = Craft
        },
        new()
        {
            Name = "place",
            Description = "Put an item from your inventory on the ground at one or more tiles: machines (Furnace...), chests, sprinklers, scarecrows, fences, paths, torches, furniture, bombs, crab pots. " +
                          "You walk next to each tile automatically. The tile must be free and the item must be allowed there (a crab pot needs water, a sprinkler goes on open ground). " +
                          "Seeds and fertilizer on tilled soil also work here, but plant is the usual tool for them.",
            Schema = Obj(new
            {
                item = Str("Item name, e.g. \"Chest\" or \"Furnace\""),
                tiles = Tiles("List of [x,y] tiles, max 40"),
                reason = Str(ReasonDesc)
            }, "item", "tiles", "reason"),
            Run = Place
        },
        ChestToolDef()
    };

    // ------------------------------------------------------------------ craft

    private static IEnumerator Craft(JsonElement a, CallContext c)
    {
        CraftingRecipe.InitShared();
        var want = S(a, "recipe").Trim();
        if (want.Length == 0 || want.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            c.Result = RecipeList();
            yield break;
        }

        var key = FindRecipeKey(want);
        if (key == null)
        {
            c.Fail($"You don't know a recipe called \"{want}\". craft() lists the recipes you know.");
            yield break;
        }

        int qty = Math.Clamp(I(a, "quantity", 1), 1, 99);
        var first = new CraftingRecipe(key, false);
        var me = Rt.Log.Player;
        var notes = new List<string>();
        if (!first.doesFarmerHaveIngredientsInInventory())
        {
            // What the chests hold comes out first; the model left the Wood in its chest every time.
            yield return FetchIngredients(first, qty, notes, Reason(a));
            if (!first.doesFarmerHaveIngredientsInInventory())
            {
                var pack = PackSignature();
                var shortText = Shortfall(first);
                var prefix = notes.Count > 0 ? string.Join("\n", notes) + "\n" : "";
                if (LastShortCraft.TryGetValue(me, out var last) && last.key == key && last.pack == pack)
                {
                    c.Fail($"{prefix}Same as your last try: still can't craft {first.DisplayName}, and your pack hasn't changed since ({shortText}). " +
                           "Don't craft it again until you have gathered or bought what is short; do other work now.");
                    yield break;
                }
                LastShortCraft[me] = (key, pack);
                c.Fail($"{prefix}Could not craft {first.DisplayName}: missing ingredients: {shortText}.");
                yield break;
            }
        }
        LastShortCraft.Remove(me);
        var before = Snapshot();
        string name = key, why = "";
        int made = 0, perCraft = 1;
        for (int n = 0; n < qty; n++)
        {
            var cr = new CraftingRecipe(key, false);
            name = cr.DisplayName;
            perCraft = Math.Max(1, cr.numberProducedPerCraft);
            if (!cr.doesFarmerHaveIngredientsInInventory())
            {
                why = $"missing ingredients: {Shortfall(cr)}";
                break;
            }
            Item created = cr.createItem();
            // Check the room before the ingredients are used up, so a full pack never eats them.
            if (!P.couldInventoryAcceptThisItem(created))
            {
                // Said 17 times in a row in the 14B run without a way out: a full pack is why the farmer wanted a chest.
                why = "your inventory is full: free one slot first (ship surplus Stone, Fiber or Sap from the Farm's shipping bin, eat something, or trash a stack you don't need), then craft again";
                break;
            }
            cr.consumeIngredients(null);
            P.addItemToInventoryBool(created);
            // What the crafting page does after a craft: count it (recipe counters feed the crafting achievements).
            if (P.craftingRecipes.ContainsKey(key)) P.craftingRecipes[key] += perCraft;
            made++;
        }
        if (made > 0) Game1.stats.checkForCraftingAchievements();
        yield return Ticks(2);

        if (made == 0)
        {
            c.Fail($"Could not craft {name}: {why}.");
            yield break;
        }
        var items = made * perCraft;
        var text = (notes.Count > 0 ? string.Join("\n", notes) + "\n" : "") + $"Crafted {made}x {name}{(perCraft > 1 ? $" ({items} items)" : "")}.";
        if (made < qty) text += $" Stopped after {made} of {qty}: {why}.";
        var diff = Diff(before);
        c.Result = diff.Length > 0 ? text + " " + diff : text;
    }

    /// <summary>Key of the known recipe called <paramref name="want"/> (key or display name, exact first, else the first partial match).</summary>
    private static string? FindRecipeKey(string want, bool cooking = false)
    {
        string? partial = null;
        foreach (var key in cooking ? P.cookingRecipes.Keys.ToList() : P.craftingRecipes.Keys.ToList())
        {
            CraftingRecipe cr;
            try { cr = new CraftingRecipe(key, cooking); }
            catch (Exception) { continue; }
            if (key.Equals(want, StringComparison.OrdinalIgnoreCase) || cr.DisplayName.Equals(want, StringComparison.OrdinalIgnoreCase))
                return key;
            if (partial == null && (key.Contains(want, StringComparison.OrdinalIgnoreCase) || cr.DisplayName.Contains(want, StringComparison.OrdinalIgnoreCase)))
                partial = key;
        }
        return partial;
    }

    /// <summary>
    /// What the crafts worth making are for. In the 9B run the farmers only ever crafted Chests (the one item every hint named)
    /// and never listed their recipes, so the list and the morning advice say what each of these does for the farm.
    /// </summary>
    private static readonly Dictionary<string, string> CraftUses = new()
    {
        ["Scarecrow"] = "keeps crows off the crops within 8 tiles: one in the middle of each field",
        ["Deluxe Scarecrow"] = "keeps crows off the crops within 16 tiles",
        ["Basic Fertilizer"] = "better crop quality (sells for more): plant it on tilled soil before the seed",
        ["Quality Fertilizer"] = "much better crop quality: plant it on tilled soil before the seed",
        ["Speed-Gro"] = "crops grow 10% faster: plant it on tilled soil before the seed",
        ["Basic Retaining Soil"] = "soil may stay watered overnight",
        ["Sprinkler"] = "waters the 4 tiles around it every morning",
        ["Quality Sprinkler"] = "waters the 8 tiles around it every morning",
        ["Iridium Sprinkler"] = "waters the 24 tiles around it every morning",
        ["Bee House"] = "honey every few days (more near flowers); place it outside on the Farm",
        ["Furnace"] = "smelts 5 ore + 1 Coal into a bar, for tool upgrades and machines",
        ["Charcoal Kiln"] = "turns 10 Wood into Coal",
        ["Preserves Jar"] = "turns a vegetable or fruit into pickles or jelly worth about twice as much",
        ["Keg"] = "turns fruit into wine and vegetables into juice, worth much more",
        ["Cheese Press"] = "milk into cheese",
        ["Mayonnaise Machine"] = "eggs into mayonnaise",
        ["Loom"] = "wool into cloth",
        ["Oil Maker"] = "truffles or sunflowers into oil",
        ["Seed Maker"] = "a crop back into seeds",
        ["Recycling Machine"] = "trash from fishing into useful items",
        ["Tapper"] = "place on a grown tree: sap, maple syrup, oak resin or pine tar",
        ["Crab Pot"] = "place in water with bait: catches something every day",
        ["Bait"] = "fish bite sooner",
        ["Lightning Rod"] = "batteries from storms",
        ["Worm Bin"] = "bait every day",
        ["Spring Seeds"] = "forage seeds; grow into spring forage",
        ["Summer Seeds"] = "forage seeds; grow into summer forage",
        ["Fall Seeds"] = "forage seeds; grow into fall forage",
        ["Winter Seeds"] = "forage seeds; the only crop that grows in winter",
        ["Field Snack"] = "a snack that gives back energy",
        ["Chest"] = "storage for 36 stacks",
        ["Cherry Bomb"] = "clears rocks in the mines",
        ["Staircase"] = "goes down one mine level at once",
    };

    /// <summary>The recipes you know. With <paramref name="cooking"/> the cooking recipes; <paramref name="extra"/> adds ingredients outside the pack (the fridge).</summary>
    private static string RecipeList(bool cooking = false, IList<Item>? extra = null)
    {
        var ready = new List<(bool useful, string line)>();
        var missing = new List<(bool useful, string line)>();
        foreach (var key in cooking ? P.cookingRecipes.Keys.ToList() : P.craftingRecipes.Keys.ToList())
        {
            CraftingRecipe cr;
            try { cr = new CraftingRecipe(key, cooking); }
            catch (Exception) { continue; }
            int can = cr.getCraftableCount(extra);
            var makes = cr.numberProducedPerCraft > 1 ? $" (makes {cr.numberProducedPerCraft})" : "";
            bool useful = !cooking && CraftUses.TryGetValue(key, out _);
            var use = useful ? $" — {CraftUses[key]}" : "";
            var line = $"  {cr.DisplayName}{makes}: {Ingredients(cr)}{use}";
            if (can > 0) ready.Add((useful, $"{line} [you can {(cooking ? "cook" : "craft")} {can}]"));
            else missing.Add((useful, line));
        }
        static string Lines(List<(bool useful, string line)> l) =>
            l.Count == 0 ? "  none" : string.Join("\n", l.OrderBy(x => x.useful ? 0 : 1).Select(x => x.line));
        var sb = new StringBuilder($"{(cooking ? "Cooking recipes" : "Crafting recipes")} you know ({ready.Count + missing.Count}):\n");
        sb.AppendLine(cooking ? "Cookable now:" : "Craftable now:");
        sb.AppendLine(Lines(ready));
        sb.AppendLine("Missing ingredients:");
        sb.Append(Lines(missing));
        if (!cooking)
        {
            var later = UnknownUsefulRecipes();
            if (later.Count > 0) sb.Append("\nUseful recipes you don't know yet:\n" + string.Join("\n", later));
        }
        return sb.ToString();
    }

    /// <summary>"  Sprinkler (at Farming level 2) — ..." for the useful crafting recipes this farmer hasn't learned.</summary>
    private static List<string> UnknownUsefulRecipes()
    {
        var lines = new List<string>();
        foreach (var (key, use) in CraftUses)
        {
            if (P.craftingRecipes.ContainsKey(key) || !CraftingRecipe.craftingRecipes.TryGetValue(key, out var data)) continue;
            lines.Add($"  {key} ({HowToLearn(data)}) — {use}");
        }
        return lines.Take(12).ToList();
    }

    /// <summary>The recipe's unlock condition (field 4 of Data/CraftingRecipes) in words.</summary>
    private static string HowToLearn(string data)
    {
        var parts = data.Split('/');
        var cond = parts.Length > 4 ? parts[4].Trim() : "";
        var w = cond.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 2 && int.TryParse(w[1], out var lvl)) return $"at {w[0]} level {lvl}";
        if (w.Length == 3 && w[0] == "s" && int.TryParse(w[2], out lvl)) return $"at {w[1]} level {lvl}";
        if (w.Length == 3 && w[0] == "f" && int.TryParse(w[2], out var hearts)) return $"{hearts} hearts with {w[1]}";
        return "from a shop, a letter or an event";
    }

    /// <summary>Where the common crafting materials come from, by qualified id: said next to a shortfall so the farmer can go and get it.</summary>
    private static readonly Dictionary<string, string> IngredientSources = new()
    {
        ["(O)388"] = "chop trees and branches with the Axe",
        ["(O)390"] = "break rocks with the Pickaxe",
        ["(O)771"] = "cut weeds with the Scythe",
        ["(O)92"] = "chop trees with the Axe",
        ["(O)382"] = "break rocks in the Mines, or buy it at Clint's Blacksmith in Town",
        ["(O)378"] = "Mines floors 1-39, or buy it at Clint's Blacksmith",
        ["(O)380"] = "Mines floors 40-79, or buy it at Clint's Blacksmith",
        ["(O)384"] = "Mines floors 80+, or buy it at Clint's Blacksmith",
        ["(O)330"] = "hoe untilled ground or dig up artifact spots",
        ["(O)334"] = "smelt 5 Copper Ore + 1 Coal in a Furnace",
        ["(O)335"] = "smelt 5 Iron Ore + 1 Coal in a Furnace",
        ["(O)336"] = "smelt 5 Gold Ore + 1 Coal in a Furnace",
        ["(O)709"] = "chop large stumps with a Copper Axe or better",
        ["(O)766"] = "defeat slimes in the Mines",
        ["(O)80"] = "pick it up in the Mines",
        ["(O)338"] = "smelt Quartz in a Furnace",
    };

    /// <summary>
    /// The last craft each farmer failed for lack of ingredients and what its pack held then: the same craft from the same pack
    /// is refused at once (day 6-58 of the second 9B run: 102 Scarecrow crafts failed for want of Coal, often several in a row).
    /// </summary>
    private static readonly Dictionary<string, (string key, string pack)> LastShortCraft = new();

    private static string PackSignature() =>
        string.Join(",", P.Items.Where(i => i != null).GroupBy(i => i!.QualifiedItemId).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Sum(i => i!.Stack)}"));

    /// <summary>How many of ingredient <paramref name="id"/> (an item id, or a negative category id) the pack holds.</summary>
    private static int InPack(string id)
    {
        bool category = int.TryParse(id, out var cat) && cat < 0;
        var qid = category ? "" : ItemRegistry.QualifyItemId(id);
        return P.Items.Where(i => i != null && (category ? i.Category == cat : i.QualifiedItemId == qid)).Sum(i => i!.Stack);
    }

    /// <summary>"have 12/50 Wood (...)" for each ingredient short for one craft, with your chests that hold it and where to get it.</summary>
    private static string Shortfall(CraftingRecipe cr)
    {
        var parts = new List<string>();
        foreach (var (id, need) in cr.recipeList)
        {
            int have = InPack(id);
            if (have >= need) continue;
            var line = $"have {have}/{need} {IngredientName(id)}";
            bool category = int.TryParse(id, out var cat) && cat < 0;
            if (!category)
            {
                var stored = StoredInOwnChests(id);
                if (stored.Count > 0)
                    line += $" (your chest{(stored.Count > 1 ? "s" : "")} hold {stored.Sum(s => s.n)} more, but not enough)";
                if (IngredientSources.TryGetValue(ItemRegistry.QualifyItemId(id), out var src))
                    line += $" — {src}";
            }
            parts.Add(line);
        }
        return parts.Count == 0 ? Ingredients(cr) : string.Join("; ", parts) + ". Get what is short first, or craft something else from craft()";
    }

    /// <summary>How many of item <paramref name="id"/> the farm and house chests of this seat (or nobody's) hold, per chest.</summary>
    private static List<(int n, string place, string map, Point tile)> StoredInOwnChests(string id)
    {
        var me = Rt.Log.Player;
        var qid = ItemRegistry.QualifyItemId(id);
        var found = new List<(int n, string place, string map, Point tile)>();
        foreach (var loc in new[] { Game1.getFarm(), Utility.getHomeOfFarmer(P) as GameLocation })
        {
            if (loc == null) continue;
            foreach (var (tile, obj) in loc.objects.Pairs)
            {
                if (obj is not Chest { playerChest.Value: true, fridge.Value: false } chest) continue;
                var owner = Ownership.OwnerOf(chest);
                if (owner != null && owner != me) continue;
                int n = chest.Items.Where(i => i != null && i.QualifiedItemId == qid).Sum(i => i!.Stack);
                var at = new Point((int)tile.X, (int)tile.Y);
                if (n > 0) found.Add((n, $"{loc.Name} ({at.X},{at.Y})", loc.Name, at));
            }
        }
        return found;
    }

    /// <summary>
    /// For <paramref name="crafts"/> crafts of <paramref name="cr"/>: when the pack plus this farmer's chests hold every missing
    /// ingredient, take them out of the chests (walking there). Lines of what moved go to <paramref name="notes"/>. Category
    /// ingredients ("any fish") are left to the farmer. The model never took Wood out of its chest by itself in the 9B run.
    /// </summary>
    private static IEnumerator FetchIngredients(CraftingRecipe cr, int crafts, List<string> notes, string reason)
    {
        var plan = new List<(string name, int n, string map, Point tile)>();
        foreach (var (id, need) in cr.recipeList)
        {
            int missing = need * crafts - InPack(id);
            if (missing <= 0) continue;
            if (int.TryParse(id, out var cat) && cat < 0) yield break;
            var stored = StoredInOwnChests(id);
            // At least one craft's worth must be reachable, else fetching is pointless.
            if (stored.Sum(s => s.n) < need - InPack(id)) yield break;
            var name = ItemRegistry.GetData(id)?.DisplayName ?? id;
            foreach (var s in stored)
            {
                if (missing <= 0) break;
                int take = Math.Min(missing, s.n);
                plan.Add((name, take, s.map, s.tile));
                missing -= take;
            }
        }
        // One map at a time, this one first: Wood from the house and Coal from the farm took two round trips otherwise.
        foreach (var (name, n, map, tile) in plan.OrderBy(p => p.map == Loc.Name ? 0 : 1).ThenBy(p => p.map).ThenBy(p => p.tile.X).ThenBy(p => p.tile.Y).ToList())
        {
            if (Loc.Name != map)
            {
                var trip = new CallContext();
                yield return TravelTo(map, trip);
                if (!trip.Ok) { notes.Add($"Couldn't get to your chest in {map}: {trip.Result}"); yield break; }
            }
            var sub = new CallContext();
            yield return ChestTool(ArgsOf(new { x = tile.X, y = tile.Y, action = "take", item = name, quantity = n, reason }), sub);
            notes.Add(sub.Ok ? sub.Result.Split('\n')[0] : $"Couldn't take {name} from your chest at {map} ({tile.X},{tile.Y}): {sub.Result}");
            if (!sub.Ok) yield break;
        }
    }

    /// <summary>
    /// Morning crafting advice: crops without a scarecrow, and the useful recipes that can be made from the pack now. "" when none.
    /// </summary>
    internal static string CraftAdvice()
    {
        CraftingRecipe.InitShared();
        var lines = new List<string>();
        var farm = Game1.getFarm();
        var me = Rt.Log.Player;
        var crops = farm.terrainFeatures.Pairs
            .Where(kv => kv.Value is StardewValley.TerrainFeatures.HoeDirt { crop: not null } d && Ownership.OwnerOf(d) is var o && (o == null || o == me))
            .Select(kv => kv.Key).ToList();
        var scarecrows = farm.objects.Pairs.Where(kv => kv.Value.IsScarecrow())
            .Select(kv => (tile: kv.Key, r: kv.Value.Name.Contains("Deluxe") ? 16 : 8)).ToList();
        int bare = crops.Count(t => !scarecrows.Any(s => Vector2.Distance(s.tile, t) < s.r + 1));
        if (bare >= 6)
        {
            // Say what is short instead of "craft one" when it can't be made (the advice sent both farmers to a failing craft daily).
            string how;
            if (!P.craftingRecipes.ContainsKey("Scarecrow")) how = "learn the Scarecrow recipe (Farming level 1) or buy one";
            else
            {
                var sc = new CraftingRecipe("Scarecrow", false);
                bool fromChests = sc.recipeList.All(kv => InPack(kv.Key) + (int.TryParse(kv.Key, out var cat) && cat < 0 ? 0 : StoredInOwnChests(kv.Key).Sum(s => s.n)) >= kv.Value);
                how = fromChests
                    ? "craft(\"Scarecrow\") (it takes what is short from your chests) and place it in the middle of the field"
                    : $"a Scarecrow needs {Ingredients(sc)}; short now: {Shortfall(sc)}";
            }
            lines.Add($"{bare} of your crops have no Scarecrow near them, so crows can eat them: {how}.");
        }
        var ready = new List<string>();
        foreach (var key in P.craftingRecipes.Keys.ToList())
        {
            if (key == "Chest" || !CraftUses.TryGetValue(key, out var use)) continue;
            CraftingRecipe cr;
            try { cr = new CraftingRecipe(key, false); }
            catch (Exception) { continue; }
            int can = cr.getCraftableCount((IList<Item>?)null);
            if (can > 0) ready.Add($"{cr.DisplayName} x{can} ({use})");
        }
        if (ready.Count > 0) lines.Add("You can craft now: " + string.Join("; ", ready.Take(5)) + ". craft(recipe, quantity), then place it.");
        return string.Join("\n", lines);
    }

    private static string Ingredients(CraftingRecipe cr) =>
        string.Join(", ", cr.recipeList.Select(kv => $"{kv.Value} {IngredientName(kv.Key)}"));

    /// <summary>Recipe ingredient keys are item ids, or a negative category id for "any item of this kind".</summary>
    private static string IngredientName(string key)
    {
        if (int.TryParse(key, out var category) && category < 0)
        {
            return category switch
            {
                -4 => "any fish",
                -5 => "any egg",
                -6 => "any milk",
                -75 => "any vegetable",
                -79 => "any fruit",
                -80 => "any flower",
                -81 => "any forage green",
                _ => $"any item of category {category}"
            };
        }
        return ItemRegistry.GetData(key)?.DisplayName ?? key;
    }

    // ------------------------------------------------------------------ place

    private static IEnumerator Place(JsonElement a, CallContext c)
    {
        var name = S(a, "item").Trim();
        var tiles = TileList(a, "tiles").Take(40).ToList();
        if (tiles.Count == 0) { c.Fail("No tiles given."); yield break; }
        int slot = FindItem(name, it => it is SObject);
        if (slot < 0) { c.Fail($"No placeable \"{name}\" in inventory. Craft it (craft) or buy it first."); yield break; }

        var first = P.Items[slot];
        string qid = first.QualifiedItemId, label = first.DisplayName;
        bool IsIt(Item it) => it is SObject && it.QualifiedItemId == qid;
        var loc = Loc;
        var lines = new List<string>();
        int placed = 0;

        foreach (var t in tiles)
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu/dialogue opened."); break; }
            if (FindItem(label, IsIt) < 0) { lines.Add($"Ran out of {label}."); break; }
            if (loc.Objects.TryGetValue(t.ToVector2(), out var there))
            {
                lines.Add($"({t.X},{t.Y}) already has {there.DisplayName}");
                continue;
            }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); continue; }
            if (Loc != loc) { lines.Add("Stopped: you left the map."); break; }

            slot = FindItem(label, IsIt);
            if (slot < 0) { lines.Add($"Ran out of {label}."); break; }
            P.CurrentToolIndex = slot;
            int had = CountOfId(P.Items, qid);
            Utility.tryToPlaceItem(loc, (SObject)P.Items[slot], t.X * 64 + 32, t.Y * 64 + 32);
            yield return Ticks(8);
            if (CountOfId(P.Items, qid) < had) placed++;
            else lines.Add($"({t.X},{t.Y}) could not place {label} (blocked, not placeable here, or not allowed on this map)");
        }

        var text = $"Placed {placed}/{tiles.Count} {label}." + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
        if (placed > 0 && first is Chest) text += "\nUse the chest tool with its (x, y) to fill or empty it.";
        if (placed == 0) c.Fail(text);
        else c.Result = text;
    }

    /// <summary>Total stack size of the item with this qualified id, whatever its quality.</summary>
    private static int CountOfId(IEnumerable<Item?> items, string qid) =>
        items.Where(i => i != null && i.QualifiedItemId == qid).Sum(i => i!.Stack);
}
