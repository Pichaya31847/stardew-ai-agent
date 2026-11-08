using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Quests;

namespace StardewMCP.Agent;

/// <summary>
/// do_quest: carry out a journal quest from what the game says it asks for, by its type: greet everyone on an introductions
/// list, deliver an item (from the pack or the storage), gather ore in the mines and hand it over, catch the fish, defeat the
/// monsters, pick up a lost item, go to a place or craft an item; then claim the money. Built from the tools the AIs use, so
/// their guards hold. A quest of another kind gets its objective and the how-to line back.
/// </summary>
public static partial class AgentTools
{
    private const int QuestRounds = 8;

    private static ToolDef DoQuestTool() => new()
    {
        Name = "do_quest",
        Description = "Do a quest in your journal from the game's own data, start to finish: an introductions quest talks to everyone still on its list; " +
                      "a delivery takes the item (pack or storage) to the person; a collection quest gathers the ore in the mines, then hands it over; " +
                      "a fishing quest fishes where that fish bites today; a monster quest goes down the mines until enough are defeated; " +
                      "a lost item is picked up where it lies and brought back; a place quest travels there; a crafting quest crafts the item. " +
                      "Then the money is claimed. quest: its title or part of it (quests lists them); empty = the first open quest. Stops at 22:00 or when a step fails, saying why.",
        Schema = Obj(new
        {
            quest = Str("Quest title or part of it, e.g. Introductions; empty = the first open quest"),
            reason = Str(ReasonDesc)
        }, "reason"),
        Run = DoQuest
    };

    /// <summary>The open journal quests as short lines for /do's model: title, kind and what it asks.</summary>
    internal static List<string> OpenQuestLines() =>
        P.questLog.Where(q => q != null && !q.completed.Value && !q.destroy.Value).Take(8)
            .Select(q => $"\"{q.GetName()}\" ({q.GetType().Name.Replace("Quest", "")}): {Peek.Clip(Peek.Clean(QText(q, "currentObjective")) is { Length: > 0 } o ? o : QuestTask(q), 100)}")
            .ToList();

    private static Quest? FindQuest(string query)
    {
        var open = P.questLog.Where(q => q != null && !q.completed.Value && !q.destroy.Value).ToList();
        if (query.Length == 0) return open.FirstOrDefault();
        return open.FirstOrDefault(q => q.GetName().Equals(query, StringComparison.OrdinalIgnoreCase))
               ?? open.FirstOrDefault(q => q.GetName().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? open.FirstOrDefault(q => QuestTask(q).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IEnumerator DoQuest(JsonElement a, CallContext c)
    {
        var query = S(a, "quest").Trim();
        var q = FindQuest(query);
        if (q == null)
        {
            var titles = P.questLog.Where(x => x != null && !x.completed.Value).Select(x => x.GetName()).ToList();
            c.Fail(Reasons.NotFound, titles.Count == 0 ? "Your journal has no open quest." : $"No open quest like \"{query}\". Open: {string.Join(", ", titles)}.");
            yield break;
        }
        var name = q.GetName();
        var log = new List<string> { $"Quest \"{name}\" ({q.GetType().Name})." };
        var step = new CallContext();
        switch (q.GetType().Name)
        {
            case "SocializeQuest": yield return QuestGreet(q, log, step); break;
            case "ItemDeliveryQuest": yield return QuestDeliver(q, log, step); break;
            case "ResourceCollectionQuest": yield return QuestCollect(q, log, step); break;
            case "FishingQuest": yield return QuestFish(q, log, step); break;
            case "SlayMonsterQuest": yield return QuestSlay(q, log, step); break;
            case "LostItemQuest": yield return QuestLostItem(q, log, step); break;
            default: yield return QuestBasic(q, log, step); break;
        }

        // The money of a finished quest is claimed in the journal.
        if (JournalRewards().Any(r => r.GetName() == name))
        {
            var claim = new CallContext();
            yield return QuestJournal(ArgsOf(new { action = "claim_reward", quest = name, reason = "claim the quest's money" }), claim);
            log.Add(claim.Result);
        }
        var text = string.Join("\n", log.Where(l => l.Length > 0));
        if (!step.Ok && !q.completed.Value) c.Fail(step.Reason ?? Reasons.Other, text);
        else c.Result = text + (q.completed.Value || !P.questLog.Contains(q) ? "\nThe quest is done." : "");
    }

    /// <summary>A quest field read through <see cref="Peek"/> (names differ between game versions).</summary>
    private static string QText(Quest q, params string[] names) => Peek.Text(q, names);
    private static int QInt(Quest q, int fallback, params string[] names) => Peek.Int(q, names) ?? fallback;
    private static string ItemName(string id) => ItemRegistry.GetData(id)?.DisplayName ?? id;
    private static int Carried(string id) => P.Items.Where(i => i != null && (i.QualifiedItemId == id || i.ItemId == id)).Sum(i => i.Stack);
    private static bool QuestLate(List<string> log)
    {
        if (Game1.timeOfDay < 2200) return false;
        log.Add($"Stopped: it is {WorldText.FormatTime(Game1.timeOfDay)}. do_quest again tomorrow carries on.");
        return true;
    }

    private static IEnumerator QuestTalk(string who, string? gift, List<string> log, CallContext step)
    {
        var talk = new CallContext();
        yield return TalkToNpc(gift == null
            ? ArgsOf(new { name = who, reason = "for the quest" })
            : ArgsOf(new { name = who, gift_item = gift, reason = "for the quest" }), talk);
        log.Add($"{who}: {talk.Result.Split('\n')[0]}");
        if (!talk.Ok) step.Fail(talk.Reason ?? Reasons.Other, talk.Result);
    }

    /// <summary>The storage hands over what the pack lacks, where the farmer stands (another mod's chest tool).</summary>
    private static IEnumerator QuestTakeFromStorage(string id, int want, List<string> log)
    {
        int lack = want - Carried(id);
        if (lack <= 0 || RemoteChest is not { } remote) yield break;
        var took = new CallContext();
        yield return RemoteChestCall(remote, new { action = "take", item = ItemName(id), quantity = lack, reason = "for the quest" }, took);
        if (took.Result.Length > 0) log.Add(took.Result);
    }

    private static IEnumerator QuestGreet(Quest q, List<string> log, CallContext step)
    {
        var names = Peek.Items(Peek.Get(q, "whoToGreet")).Select(o => o?.ToString() ?? "").Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) { log.Add("Nobody is left to greet."); yield break; }
        // Who stands on this map first, then the rest map by map.
        string here = Loc.Name;
        var order = names.OrderBy(n => Game1.getCharacterFromName(n)?.currentLocation?.Name == here ? 0 : 1)
            .ThenBy(n => Game1.getCharacterFromName(n)?.currentLocation?.Name ?? "~").ToList();
        int met = 0, missed = 0;
        // Who is at home now (a locked door, a bedroom) is met when their schedule takes them out: 2026-11-08 the farmer
        // stood 140 minutes outside Sebastian's room.
        var later = new List<(string who, int at)>();
        foreach (var who in order)
        {
            if (QuestLate(log) || q.completed.Value) break;
            var npc = Game1.getCharacterFromName(who);
            if (npc?.currentLocation == null) { missed++; log.Add($"{who}: not out today"); continue; }
            if (NpcInfo.AtHome(npc) && NpcInfo.NextOut(npc, Game1.timeOfDay) is int outAt)
            {
                later.Add((who, outAt));
                continue;
            }
            var talk = new CallContext();
            yield return TalkToNpc(ArgsOf(new { name = who, reason = "greet for the introductions quest" }), talk);
            if (talk.Ok) met++;
            else if (NpcInfo.NextOut(npc, Game1.timeOfDay) is int retry) later.Add((who, retry));
            else { missed++; log.Add($"{who}: {talk.Result.Split('\n')[0]}"); }
        }
        int left = Peek.Items(Peek.Get(q, "whoToGreet")).Count();
        log.Add($"Greeted {met}; {(left > 0 ? $"{left} still to meet" : "everyone is met")}.");
        if (left > 0 && later.Count > 0)
        {
            // The quest waits for the first of them to be out; /do starts it again then.
            int first = later.Min(l => l.at);
            var msg = $"At home now, met later today: {string.Join(", ", later.OrderBy(l => l.at).Select(l => $"{l.who} (out ~{WorldText.FormatTime(l.at)})"))}. [wait_until {first}]";
            log.Add(msg);
            step.Fail(Reasons.NotReady, msg);
            yield break;
        }
        if (met == 0 && left > 0) step.Fail(Reasons.NotHere, "Nobody on the list could be reached today.");
    }

    private static IEnumerator QuestDeliver(Quest q, List<string> log, CallContext step)
    {
        var id = QText(q, "ItemId", "item");
        var who = QText(q, "target", "npcName");
        int n = QInt(q, 1, "number");
        if (id.Length == 0 || who.Length == 0) { yield return QuestBasic(q, log, step); yield break; }
        yield return QuestTakeFromStorage(id, n, log);
        // Not in the pack or the storage: go and get it, by its kind (asked 2026-11-08).
        if (Carried(id) < n) yield return QuestFetch(id, n, log, step);
        if (!step.Ok) yield break;
        if (Carried(id) < n) { step.Fail(Reasons.NoItem, $"You need {n} {ItemName(id)} and have {Carried(id)} (pack and storage). item_info(\"{ItemName(id)}\") says where it comes from."); yield break; }
        yield return QuestTalk(who, ItemName(id), log, step);
    }

    /// <summary>
    /// Get what a delivery asks for: a fish is fished where it bites today (a fish that bites later fails with its
    /// "[wait_until ...]" time, so /do waits for it), forage is foraged around the valley, ores, gems and minerals come from
    /// the mines, anything a shop sells is bought where it is sold (Pierre's first). Stops when enough is carried.
    /// </summary>
    private static IEnumerator QuestFetch(string id, int n, List<string> log, CallContext step)
    {
        var qid = ItemRegistry.QualifyItemId(id) ?? id;
        var data = ItemRegistry.GetData(qid);
        int category = data?.Category ?? 0;
        string name = ItemName(qid);
        for (int round = 0; Carried(qid) < n && round < QuestRounds; round++)
        {
            if (QuestLate(log)) { step.Fail(Reasons.TooLate, $"{name} for the delivery: it is late."); yield break; }
            var get = new CallContext();
            string how;
            switch (category)
            {
                case StardewValley.Object.FishCategory:
                    how = "fishing";
                    yield return FishAt(ArgsOf(new { fish = name, tries = 5, reason = $"{name} for the delivery" }), get);
                    break;
                case StardewValley.Object.GemCategory or StardewValley.Object.mineralsCategory or StardewValley.Object.metalResources or StardewValley.Object.monsterLootCategory:
                    how = "the mines";
                    yield return Mine(ArgsOf(new { floors = 5, reason = $"{name} for the delivery" }), get);
                    break;
                case StardewValley.Object.GreensCategory or StardewValley.Object.FruitsCategory or StardewValley.Object.flowersCategory when round == 0:
                    how = "foraging";
                    yield return Forage(ArgsOf(new { max_places = 8, reason = $"{name} for the delivery" }), get);
                    break;
                default:
                    if (round > 0) { step.Fail(Reasons.NoItem, $"Couldn't get {name}: {log.LastOrDefault()}"); yield break; }
                    how = "a shop";
                    yield return QuestBuy(name, n - Carried(qid), get);
                    break;
            }
            log.Add($"{how}: {get.Result.Split('\n')[0]}");
            if (!get.Ok) { step.Fail(get.Reason ?? Reasons.NoItem, $"{name} for the delivery, from {how}: {get.Result}"); yield break; }
        }
        if (Loc is StardewValley.Locations.MineShaft) yield return LeaveMine(ArgsOf(new { reason = "back up with the delivery's item" }), new CallContext());
    }

    /// <summary>Buy <paramref name="count"/> of an item at the shop that sells it: Pierre's, then the other shops the game lists it in.</summary>
    private static IEnumerator QuestBuy(string name, int count, CallContext c)
    {
        foreach (var shop in new[] { "SeedShop", "Saloon", "Blacksmith", "FishShop", "AnimalShop", "ScienceHouse", "JojaMart" })
        {
            if (!ShopSells(shop, name)) continue;
            var trip = new CallContext();
            yield return TravelTo(shop, trip);
            if (!trip.Ok) { c.Fail(trip.Reason ?? Reasons.NoPath, trip.Result); yield break; }
            yield return Buy(ArgsOf(new { item = name, quantity = count, reason = "for the delivery" }), c);
            if (Game1.activeClickableMenu != null) yield return CloseMenu(default, new CallContext());
            yield break;
        }
        c.Fail(Reasons.NoItem, $"No shop sells {name}: item_info(\"{name}\") says where it comes from.");
    }

    /// <summary>The shop's data (Data/Shops) lists an item of this name.</summary>
    private static bool ShopSells(string shopId, string name)
    {
        try
        {
            var shops = DataLoader.Shops(Game1.content);
            if (!shops.TryGetValue(shopId, out var shop) || shop.Items == null) return false;
            return shop.Items.Any(i => i.ItemId != null && ItemRegistry.GetData(i.ItemId)?.DisplayName is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private static readonly HashSet<string> MineResources = new() { "(O)378", "(O)380", "(O)384", "(O)386", "(O)382", "(O)390" }; // ores, coal, stone

    private static IEnumerator QuestCollect(Quest q, List<string> log, CallContext step)
    {
        var id = QText(q, "ItemId", "item");
        var who = QText(q, "target", "npcName");
        int n = QInt(q, 1, "number");
        if (id.Length == 0) { yield return QuestBasic(q, log, step); yield break; }
        var qid = ItemRegistry.QualifyItemId(id) ?? id;
        // Collected counts what is picked up after taking the quest; the mines give ore, coal and stone.
        for (int round = 0; QInt(q, 0, "numberCollected") < n && round < QuestRounds; round++)
        {
            if (QuestLate(log)) yield break;
            if (!MineResources.Contains(qid)) { step.Fail(Reasons.NoItem, $"Gather {n} {ItemName(id)} (collected {QInt(q, 0, "numberCollected")}): {QuestHow(q)}"); yield break; }
            var dig = new CallContext();
            yield return Mine(ArgsOf(new { floors = 5, reason = $"{ItemName(id)} for the quest" }), dig);
            log.Add($"mines: {dig.Result.Split('\n')[0]}");
            if (!dig.Ok) { step.Fail(dig.Reason ?? Reasons.Other, dig.Result); yield break; }
        }
        if (Loc is MineShaft) yield return LeaveMine(ArgsOf(new { reason = "back up with the quest's items" }), new CallContext());
        yield return QuestTakeFromStorage(id, n, log);
        if (who.Length > 0) yield return QuestTalk(who, null, log, step);
    }

    private static IEnumerator QuestFish(Quest q, List<string> log, CallContext step)
    {
        var id = QText(q, "ItemId", "item");
        var who = QText(q, "target", "npcName");
        int n = QInt(q, 1, "numberToFish");
        for (int round = 0; QInt(q, 0, "numberFished") < n && round < QuestRounds; round++)
        {
            if (QuestLate(log)) yield break;
            var cast = new CallContext();
            yield return FishAt(id.Length > 0 ? ArgsOf(new { fish = ItemName(id), tries = 5, reason = "for the quest" }) : ArgsOf(new { tries = 5, reason = "for the quest" }), cast);
            log.Add($"fishing: {cast.Result.Split('\n')[0]}");
            if (!cast.Ok) { step.Fail(cast.Reason ?? Reasons.Other, cast.Result); yield break; }
        }
        if (QInt(q, 0, "numberFished") < n) { step.Fail(Reasons.Partial, $"Caught {QInt(q, 0, "numberFished")} of {n}: do_quest again carries on."); yield break; }
        if (who.Length > 0) yield return QuestTalk(who, null, log, step);
    }

    private static IEnumerator QuestSlay(Quest q, List<string> log, CallContext step)
    {
        var who = QText(q, "target", "npcName");
        var monster = QText(q, "monsterName");
        int n = QInt(q, 1, "numberToKill");
        for (int round = 0; QInt(q, 0, "numberKilled") < n && round < QuestRounds; round++)
        {
            if (QuestLate(log)) yield break;
            var dig = new CallContext();
            yield return Mine(ArgsOf(new { floors = 5, reason = $"{monster} for the quest" }), dig);
            log.Add($"mines ({QInt(q, 0, "numberKilled")}/{n} {monster}): {dig.Result.Split('\n')[0]}");
            if (!dig.Ok) { step.Fail(dig.Reason ?? Reasons.Other, dig.Result); yield break; }
        }
        if (QInt(q, 0, "numberKilled") < n) { step.Fail(Reasons.Partial, $"Defeated {QInt(q, 0, "numberKilled")} of {n} {monster}: do_quest again carries on."); yield break; }
        if (Loc is MineShaft) yield return LeaveMine(ArgsOf(new { reason = "back up after the quest's monsters" }), new CallContext());
        if (who.Length > 0) yield return QuestTalk(who, null, log, step);
    }

    private static IEnumerator QuestLostItem(Quest q, List<string> log, CallContext step)
    {
        var who = QText(q, "npcName", "target");
        var where = QText(q, "locationOfItem");
        int? x = Peek.Int(q, "tileX"), y = Peek.Int(q, "tileY");
        if (Peek.Flag(q, "itemFound") != true)
        {
            if (where.Length == 0 || x == null || y == null) { yield return QuestBasic(q, log, step); yield break; }
            if (Loc.Name != where)
            {
                var trip = new CallContext();
                yield return TravelTo(where, trip);
                if (!trip.Ok) { step.Fail(trip.Reason ?? Reasons.NoPath, trip.Result); yield break; }
            }
            var pick = new CallContext();
            yield return Interact(ArgsOf(new { x = x.Value, y = y.Value, reason = "pick up the lost item" }), pick);
            yield return Ticks(30);
            log.Add($"searched ({x},{y}) in {where}: {pick.Result.Split('\n')[0]}");
        }
        if (who.Length > 0) yield return QuestTalk(who, null, log, step);
    }

    /// <summary>Story quests of the base type: go to a place (questType 3) or craft an item (2); anything else: what it asks, as text.</summary>
    private static IEnumerator QuestBasic(Quest q, List<string> log, CallContext step)
    {
        int type = QInt(q, 0, "questType");
        var goal = QText(q, "completionString");
        if (type == 3 && goal.Length > 0)
        {
            var trip = new CallContext();
            yield return TravelTo(goal, trip);
            log.Add($"went to {goal}: {trip.Result.Split('\n')[0]}");
            if (!trip.Ok) step.Fail(trip.Reason ?? Reasons.NoPath, trip.Result);
            yield break;
        }
        if (type == 2 && goal.Length > 0)
        {
            var make = new CallContext();
            yield return Craft(ArgsOf(new { recipe = ItemName(goal), quantity = 1, reason = "for the quest" }), make);
            log.Add($"crafted: {make.Result.Split('\n')[0]}");
            if (!make.Ok) step.Fail(make.Reason ?? Reasons.Other, make.Result);
            yield break;
        }
        var objective = Peek.Clean(QText(q, "currentObjective"));
        var how = QuestHow(q);
        step.Fail(Reasons.Other, $"This quest is not one do_quest can carry out by itself. It asks: {(objective.Length > 0 ? objective : QuestTask(q))}{(how.Length > 0 ? $" How: {how}" : "")}");
    }
}
