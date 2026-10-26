using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// Chores a small model kept skipping in the local run: the day-1 gift box, the mailbox, unclaimed quest money, forage away
/// from the farm, the backpack upgrade, and opening a shop's counter instead of turning back at the door. Each is a macro
/// over the game's own entry points (interact, the journal's reward button, Backpack_Purchase); the decisions stay the AI's.
/// </summary>
public static partial class AgentTools
{
    private static List<ToolDef> ChoreToolDefs() => new()
    {
        new()
        {
            Name = "morning_chores",
            Description = "Start-of-day chores in one call: opens the gift boxes in your house, reads every letter in your mailbox (walks there; " +
                          "attached items and quests are claimed) and claims the money of finished quests. Returns the letters' text: a letter " +
                          "can invite you to a place at a time, so put that in today's plan.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = MorningChores
        },
        new()
        {
            Name = "forage",
            Description = "Pick up forage (wild plants, shells, mushrooms...) on the maps that have some today: you travel to each and collect it all, " +
                          "nearest map first. places limits it to these maps; max_places how many maps to visit (default 3). " +
                          "With list=true it only counts the forage on each map, without moving (no game time).",
            Schema = Obj(new
            {
                places = new { type = "array", items = new { type = "string" }, description = "Maps to search (default: Farm, BusStop, Forest, Town, Mountain, Beach, Backwoods, Railroad)" },
                max_places = Int("How many maps to visit (default 3)"),
                list = new { type = "boolean", description = "Only count forage per map" },
                reason = Str(ReasonDesc)
            }, "reason"),
            Run = Forage
        },
        new()
        {
            Name = "upgrade_backpack",
            Description = "Buy the next backpack size at Pierre's (SeedShop): 12 to 24 slots for 2,000g, then 24 to 36 for 10,000g. You walk to the shop " +
                          "(open 9:00-17:00, closed Wednesday) and to its backpack display. Worth it as soon as the money for this week's seeds is kept aside.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = UpgradeBackpack
        }
    };

    // ------------------------------------------------------------------ morning_chores

    private static IEnumerator MorningChores(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu != null)
            yield return FinishMessages(new CallContext());
        var parts = new List<string>();

        // The gift boxes of the house (day 1: 15 Parsnip Seeds). Only at home: the farmer wakes up there.
        if (Loc is FarmHouse house && Utility.getHomeOfFarmer(P) == house)
        {
            var boxes = house.objects.Pairs.Where(kv => kv.Value is Chest { giftbox.Value: true }).Select(kv => kv.Key).ToList();
            foreach (var v in boxes)
            {
                var before = Snapshot();
                var sub = new CallContext();
                yield return Interact(ArgsOf(new { x = (int)v.X, y = (int)v.Y, reason = Reason(a) }), sub);
                yield return Ticks(30);
                if (Game1.activeClickableMenu != null) yield return FinishMessages(new CallContext());
                var got = Diff(before);
                parts.Add(sub.Ok ? $"Opened the gift box at ({(int)v.X},{(int)v.Y}). {got}".Trim() : $"Couldn't open the gift box at ({(int)v.X},{(int)v.Y}): {sub.Result}");
            }
        }

        // The mailbox: one letter per press, so press until it is empty.
        if (P.mailbox.Count > 0)
        {
            var box = P.getMailboxPosition();
            if (Loc.Name != "Farm")
            {
                var trip = new CallContext();
                yield return TravelTo("Farm", trip);
                if (!trip.Ok) parts.Add($"Couldn't get to your mailbox: {trip.Result}");
            }
            for (int i = 0; i < 10 && P.mailbox.Count > 0 && Loc.Name == "Farm"; i++)
            {
                int left = P.mailbox.Count;
                var sub = new CallContext();
                yield return Interact(ArgsOf(new { x = box.X, y = box.Y, reason = Reason(a) }), sub);
                if (sub.Result.Length > 0) parts.Add(sub.Result.Trim());
                if (Game1.activeClickableMenu != null) yield return FinishMessages(new CallContext());
                if (P.mailbox.Count >= left) { parts.Add("The mailbox did not open; try interact on it."); break; }
            }
            parts.Add("If a letter asks you to be somewhere at a time, add it to today's plan and be there on time.");
        }
        else parts.Add("No letters today.");

        // Money of finished quests waits in the journal until it is claimed.
        for (int i = 0; i < 5 && JournalRewards().Any(); i++)
        {
            var sub = new CallContext();
            yield return QuestJournal(ArgsOf(new { action = "claim_reward", reason = Reason(a) }), sub);
            parts.Add(sub.Result);
            if (!sub.Ok) break;
        }

        c.Result = string.Join("\n", parts.Where(p => p.Length > 0));
    }

    // ------------------------------------------------------------------ forage

    private static readonly string[] ForagePlaces = { "Farm", "BusStop", "Forest", "Town", "Mountain", "Beach", "Backwoods", "Railroad" };

    private static int ForageOn(GameLocation loc) => loc.Objects.Values.Count(o => o.isForage());

    private static IEnumerator Forage(JsonElement a, CallContext c)
    {
        var asked = StrList(a, "places");
        var names = asked.Count > 0 ? asked : ForagePlaces.ToList();
        var here = Loc.Name;
        var found = names
            .Select(n => Game1.getLocationFromName(n))
            .Where(l => l != null)
            .Select(l => (loc: l!, count: ForageOn(l!), hops: l!.Name == here ? 0 : FindRoute(here, l!.Name)?.Count ?? 99))
            .Where(x => x.count > 0 && x.hops < 99)
            .OrderBy(x => x.hops).ThenByDescending(x => x.count)
            .ToList();
        if (found.Count == 0) { c.Result = $"No forage on {string.Join(", ", names)} right now."; yield break; }
        var counts = string.Join(", ", found.Select(x => $"{x.loc.Name} {x.count}"));
        if (Has(a, "list") && a.GetProperty("list").ValueKind == JsonValueKind.True) { c.Result = "Forage today: " + counts + "."; yield break; }

        var before = Snapshot();
        var lines = new List<string> { "Forage found: " + counts + "." };
        int max = Math.Clamp(I(a, "max_places", 3), 1, 8);
        foreach (var (loc, _, _) in found.Take(max))
        {
            if (Game1.timeOfDay >= 2200) { lines.Add("Stopped: it is late."); break; }
            if (!P.Items.Any(i => i == null)) { lines.Add("Stopped: your pack is full."); break; }
            if (Loc.Name != loc.Name)
            {
                var trip = new CallContext();
                yield return TravelTo(loc.Name, trip);
                if (!trip.Ok) { lines.Add($"{loc.Name}: couldn't get there ({trip.Result})"); continue; }
            }
            var pick = new CallContext();
            yield return HarvestAll(ArgsOf(new { reason = Reason(a) }), pick);
            lines.Add($"{loc.Name}: {pick.Result.Split('\n')[0]}");
        }
        var got = Diff(before);
        if (got.Length > 0) lines.Add(got);
        c.Result = string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ upgrade_backpack

    private static IEnumerator UpgradeBackpack(JsonElement a, CallContext c)
    {
        int slots = P.MaxItems;
        if (slots >= 36) { c.Fail(Reasons.NotReady, "Your backpack already has 36 slots, the biggest Pierre sells."); yield break; }
        int price = slots < 24 ? 2000 : 10000;
        if (P.Money < price) { c.Fail(Reasons.NoMoney, $"The {slots + 12}-slot backpack costs {price:N0}g and you have {P.Money:N0}g."); yield break; }
        if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());

        if (Loc.Name != "SeedShop")
        {
            var trip = new CallContext();
            yield return TravelTo("SeedShop", trip);
            if (!trip.Ok) { c.Fail(trip.Reason ?? Reasons.ShopClosed, $"Couldn't get into Pierre's shop: {trip.Result}"); yield break; }
        }
        var me = P.TilePoint;
        var display = FindActionTiles(Loc, act => act.StartsWith("BuyBackpack", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y)).Cast<Point?>().FirstOrDefault();
        if (display is Point tile)
        {
            var walk = new CallContext();
            yield return WalkAdjacent(tile, walk);
        }

        // The answer the game's own "Buy a backpack?" question gives (GameLocation.answerDialogueAction).
        Loc.answerDialogueAction("Backpack_Purchase", Array.Empty<string>());
        yield return Ticks(90);
        yield return FinishMessages(new CallContext());
        yield return WaitIdle();
        if (P.MaxItems <= slots) { c.Fail(Reasons.NotReady, $"The purchase did not go through (still {P.MaxItems} slots, {P.Money:N0}g)."); yield break; }
        c.Result = $"Bought the bigger backpack: {slots} → {P.MaxItems} slots for {price:N0}g. Money left {P.Money:N0}g.";
    }

    // ------------------------------------------------------------------ shop counters

    private static bool IsShopCounter(string action)
    {
        var token = action.Split(' ')[0];
        return token.Contains("Shop", StringComparison.OrdinalIgnoreCase) || token is "Blacksmith" or "Carpenter" or "AnimalShop" or "Saloon";
    }

    /// <summary>
    /// Open this map's shop counter when no shop menu is open: walk to the nearest counter tile, press it, and take the
    /// shop option of a counter that asks first (Clint, Robin, Marnie). The local run walked into shops and straight out again.
    /// </summary>
    internal static IEnumerator OpenShopHere(CallContext c)
    {
        if (Game1.activeClickableMenu is ShopMenu) yield break;
        if (Game1.activeClickableMenu != null) { c.Fail(Reasons.MenuOpen, "Close the open menu first. " + WorldText.MenuState().Trim()); yield break; }
        var me = P.TilePoint;
        var counters = FindActionTiles(Loc, IsShopCounter).OrderBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y)).Take(3).ToList();
        if (counters.Count == 0) { c.Fail(Reasons.NotHere, $"There is no shop counter on {Loc.Name}."); yield break; }

        string last = "";
        foreach (var t in counters)
        {
            var sub = new CallContext();
            yield return Interact(ArgsOf(new { x = t.X, y = t.Y }), sub);
            for (int i = 0; i < 60 && Game1.activeClickableMenu == null; i++) yield return null;
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } box && box.responses != null)
            {
                int pick = Array.FindIndex(box.responses, r => r.responseKey is { } k &&
                    (k.Contains("Shop", StringComparison.OrdinalIgnoreCase) || k.Contains("Supplies", StringComparison.OrdinalIgnoreCase) || k.Equals("Buy", StringComparison.OrdinalIgnoreCase)));
                if (pick >= 0)
                {
                    yield return Respond(ArgsOf(new { option = pick }), new CallContext());
                    for (int i = 0; i < 60 && Game1.activeClickableMenu is not ShopMenu; i++) yield return null;
                }
            }
            if (Game1.activeClickableMenu is ShopMenu) yield break;
            last = Game1.activeClickableMenu != null ? WorldText.MenuState().Trim() : sub.Result;
            if (Game1.activeClickableMenu != null) yield return FinishMessages(new CallContext());
        }
        c.Fail(Reasons.ShopClosed, $"The counter on {Loc.Name} did not open a shop (the shopkeeper may be away or it is outside opening hours). {last}".Trim());
    }

    private static readonly Dictionary<string, string> ShopPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SeedShop"] = "Pierre (SeedShop)", ["FishShop"] = "Willy (FishShop, Beach)", ["Blacksmith"] = "Clint (Blacksmith)",
        ["AnimalShop"] = "Marnie (AnimalShop)", ["Carpenter"] = "Robin (ScienceHouse)", ["Saloon"] = "Gus (Saloon)",
        ["Joja"] = "JojaMart", ["Sandy"] = "Sandy (SandyHouse, Desert)", ["DesertTrade"] = "Desert Trader", ["Krobus"] = "Krobus (Sewer)"
    };

    /// <summary>"Shops that buy Parsnip: Pierre (SeedShop), JojaMart.", from the shops' own SalableItemTags; "" when none or unreadable.</summary>
    internal static string WhoBuys(Item item)
    {
        try
        {
            var buyers = DataLoader.Shops(Game1.content)
                .Where(kv => kv.Value.SalableItemTags?.Any(tag => item.HasContextTag(tag)) == true)
                .Select(kv => ShopPlaces.TryGetValue(kv.Key, out var place) ? place : kv.Key)
                .Distinct()
                .ToList();
            return buyers.Count == 0
                ? $" No shop buys {item.DisplayName}; ship it from the Farm's shipping bin instead."
                : $" Shops that buy {item.DisplayName}: {string.Join(", ", buyers)}. travel_to there during opening hours and sell; the counter opens by itself. Or ship it (paid overnight).";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
