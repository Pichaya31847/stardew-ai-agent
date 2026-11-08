using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>One villager as a player sees them right now. Build it on the game thread.</summary>
internal class VillagerInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Location { get; set; } = "?";
    public int X { get; set; }
    public int Y { get; set; }
    public bool Met { get; set; }
    public int Points { get; set; }
    public int Hearts { get; set; }
    public bool TalkedToToday { get; set; }
    public int GiftsToday { get; set; }
    public int GiftsThisWeek { get; set; }
    /// <summary>friendly, dating, engaged, married... (lower case); empty before you met them.</summary>
    public string Status { get; set; } = "";
    public string BirthdaySeason { get; set; } = "";
    public int BirthdayDay { get; set; }
    /// <summary>0 = today; -1 = unknown.</summary>
    public int DaysToBirthday { get; set; } = -1;
    /// <summary>Can be dated and married (#53).</summary>
    public bool Datable { get; set; }
}

/// <summary>
/// Pure helpers behind villagers(), talk_to and travel_to for people (#9): finding villagers, their
/// friendship state, birthdays, place aliases. Game thread only. Tool coroutines live in <see cref="AgentTools"/>.
/// </summary>
internal static class NpcInfo
{
    /// <summary>Points per heart.</summary>
    public const int PointsPerHeart = 250;

    private static readonly string[] SeasonNames = { "spring", "summer", "fall", "winter" };

    /// <summary>Items the game treats as more than a gift; they have no taste.</summary>
    public static readonly HashSet<string> SpecialGifts = new(StringComparer.OrdinalIgnoreCase) { "Bouquet", "Mermaid's Pendant" };

    /// <summary>Shop keepers whose name means their shop (travel_to Pierre). Any other villager means where they are now.</summary>
    public static readonly Dictionary<string, string> PlaceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pierre"] = "SeedShop",
        ["Clint"] = "Blacksmith",
        ["Robin"] = "ScienceHouse",
        ["Marnie"] = "AnimalShop",
        ["Willy"] = "FishShop",
        ["Gus"] = "Saloon",
        // Late-game areas (#55). Skull Cavern is a mine level, so travel_to handles that name itself.
        ["Sandy"] = "SandyHouse",
        ["Oasis"] = "SandyHouse",
        ["Casino"] = "Club",
        ["MovieTheater"] = "MovieTheater",
        ["Movie Theater"] = "MovieTheater",
        ["Cinema"] = "MovieTheater",
        ["Theater"] = "MovieTheater",
        ["Calico Desert"] = "Desert",
        ["CalicoDesert"] = "Desert",
        ["Calico"] = "Desert",
        ["Desert Trader"] = "Desert",
        ["DesertTrader"] = "Desert",
        ["Ginger Island"] = "IslandSouth",
        ["GingerIsland"] = "IslandSouth",
        ["Island"] = "IslandSouth",
        ["Island Farm"] = "IslandWest",
        ["IslandFarm"] = "IslandWest",
        ["Island FarmHouse"] = "IslandFarmHouse",
        ["IslandFarmHouse"] = "IslandFarmHouse"
    };

    /// <summary>Aliases that name an area rather than a person, so ResolvePlace does not say "X works in Y".</summary>
    public static readonly HashSet<string> AreaAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Oasis", "Casino",
        "MovieTheater", "Movie Theater", "Cinema", "Theater",
        "Calico Desert", "CalicoDesert", "Calico", "Desert Trader", "DesertTrader",
        "Ginger Island", "GingerIsland", "Island",
        "Island Farm", "IslandFarm", "Island FarmHouse", "IslandFarmHouse"
    };

    // ------------------------------------------------------------------ finding people

    /// <summary>Every villager the player can befriend (one per name).</summary>
    public static List<NPC> Social() =>
        Utility.getAllCharacters()
            .Where(n => n != null && n is not Monster && n.CanSocialize)
            .GroupBy(n => n.Name)
            .Select(g => g.First())
            .ToList();

    /// <summary>The NPC called <paramref name="name"/> (name or display name, any case) anywhere in the world, or null.</summary>
    public static NPC? ByName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if (AgentTools.FestivalActor(name) is { } fest) return fest;
        var all = Utility.getAllCharacters().Where(n => n != null && n is not Monster).ToList();
        return all.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
               all.FirstOrDefault(n => n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool OnMap(NPC npc, GameLocation? loc) =>
        (loc != null && npc.currentLocation != null && npc.currentLocation.NameOrUniqueName == loc.NameOrUniqueName) ||
        AgentTools.FestivalActors().Contains(npc);

    public static string WhereIs(NPC npc) =>
        npc.currentLocation == null ? "an unknown place" : $"{npc.currentLocation.NameOrUniqueName} ({npc.TilePoint.X},{npc.TilePoint.Y})";

    /// <summary>
    /// The nearest villager this farmer has not talked to today, with where they are and how far: someone on this map first
    /// (by tiles), then someone outdoors in town, then anyone. Null when everyone has been talked to. Day 56: the social
    /// reminder sent AI2 to find the other farmer (300 game minutes of travel_to) while villagers stood nearby.
    /// </summary>
    public static (string name, string where)? NearestUntalked()
    {
        var me = Game1.player;
        var here = me.currentLocation;
        var best = Social()
            .Where(n => n.currentLocation != null && !(me.friendshipData.TryGetValue(n.Name, out var f) && f.TalkedToToday))
            .Select(n =>
            {
                bool same = n.currentLocation.NameOrUniqueName == here?.NameOrUniqueName;
                int tiles = same ? Math.Abs(n.TilePoint.X - me.TilePoint.X) + Math.Abs(n.TilePoint.Y - me.TilePoint.Y) : int.MaxValue;
                int rank = same ? 0 : n.currentLocation.IsOutdoors ? 1 : 2;
                return (npc: n, rank, tiles);
            })
            .OrderBy(x => x.rank).ThenBy(x => x.tiles)
            .FirstOrDefault();
        if (best.npc == null) return null;
        var where = best.rank == 0 ? $"{best.tiles} tiles away here on {here!.Name}" : $"in {WhereIs(best.npc)}";
        return (best.npc.Name, where);
    }

    /// <summary>
    /// The nearest villager who can still take a gift today (none yet today, fewer than 2 this week; a birthday first), or
    /// with <paramref name="forGift"/> false who has not been talked to today. Same ranking as NearestUntalked.
    /// </summary>
    public static NPC? NearestFor(Farmer me, bool forGift)
    {
        var here = me.currentLocation;
        return Social()
            .Where(n => n.currentLocation != null && !AtHome(n) || n.currentLocation == here)
            .Where(n =>
            {
                me.friendshipData.TryGetValue(n.Name, out var f);
                return forGift ? f == null || (f.GiftsToday == 0 && f.GiftsThisWeek < 2) : f == null || !f.TalkedToToday;
            })
            .Select(n =>
            {
                bool same = n.currentLocation.NameOrUniqueName == here?.NameOrUniqueName;
                int tiles = same ? Math.Abs(n.TilePoint.X - me.TilePoint.X) + Math.Abs(n.TilePoint.Y - me.TilePoint.Y) : int.MaxValue;
                int rank = forGift && n.isBirthday() ? -1 : same ? 0 : n.currentLocation.IsOutdoors ? 1 : 2;
                return (npc: n, rank, tiles);
            })
            .OrderBy(x => x.rank).ThenBy(x => x.tiles)
            .Select(x => x.npc).FirstOrDefault();
    }

    private static readonly int[] GiftCategories =
    {
        StardewValley.Object.GreensCategory, StardewValley.Object.FruitsCategory, StardewValley.Object.VegetableCategory,
        StardewValley.Object.flowersCategory, StardewValley.Object.FishCategory, StardewValley.Object.GemCategory,
        StardewValley.Object.mineralsCategory, StardewValley.Object.CookingCategory, StardewValley.Object.artisanGoodsCategory
    };

    /// <summary>
    /// The gift to give from the pack: one this farmer already saw the villager love, then like (their own book; tastes are
    /// found out by gifting, not known); else the most valuable forage, crop, fish, gem, dish or artisan good, never one
    /// they were seen to dislike or hate. Null when the pack holds nothing giftable.
    /// </summary>
    public static Item? BestGift(Farmer me, NPC npc)
    {
        var book = NpcKnowledge.Book(me);
        book.Tastes.TryGetValue(npc.Name, out var known);
        int Taste(Item i) => known != null && known.TryGetValue(i.QualifiedItemId, out var t) ? t : -1;
        var giftable = me.Items.Where(i => i is StardewValley.Object { bigCraftable.Value: false } && Array.IndexOf(GiftCategories, i.Category) >= 0
                                           && Taste(i) is not (4 or 6)).ToList();
        return giftable.FirstOrDefault(i => Taste(i) == 0) ?? giftable.FirstOrDefault(i => Taste(i) == 2)
               ?? giftable.OrderByDescending(i => i.salePrice()).FirstOrDefault();
    }

    // ------------------------------------------------------------------ friendship state

    public static VillagerInfo Of(Farmer who, NPC npc)
    {
        var info = new VillagerInfo
        {
            Name = npc.Name,
            DisplayName = npc.displayName,
            Location = npc.currentLocation?.NameOrUniqueName ?? "?",
            X = npc.TilePoint.X,
            Y = npc.TilePoint.Y,
            BirthdaySeason = (Convert.ToString(npc.Birthday_Season) ?? "").Trim().ToLowerInvariant(),
            BirthdayDay = Convert.ToInt32(npc.Birthday_Day),
            Datable = npc.datable.Value
        };
        info.DaysToBirthday = DaysToBirthday(info.BirthdaySeason, info.BirthdayDay);
        if (who.friendshipData.TryGetValue(npc.Name, out var f))
        {
            info.Met = true;
            info.Points = f.Points;
            info.Hearts = f.Points / PointsPerHeart;
            info.TalkedToToday = f.TalkedToToday;
            info.GiftsToday = f.GiftsToday;
            info.GiftsThisWeek = f.GiftsThisWeek;
            info.Status = f.Status.ToString().ToLowerInvariant();
        }
        return info;
    }

    // ------------------------------------------------------------------ birthdays

    public static int SeasonIndex(string season)
    {
        var s = season.Trim().ToLowerInvariant();
        if (s == "autumn") s = "fall";
        return Array.IndexOf(SeasonNames, s);
    }

    /// <summary>Days from today until the next birthday: 0 = today, -1 = unknown. A year is 4 x 28 days.</summary>
    public static int DaysToBirthday(string season, int day)
    {
        int s = SeasonIndex(season);
        if (s < 0 || day < 1 || day > 28) return -1;
        int now = Math.Max(0, SeasonIndex(Game1.currentSeason)) * 28 + Game1.dayOfMonth;
        int birthday = s * 28 + day;
        return ((birthday - now) % 112 + 112) % 112;
    }

    public static string BirthdayText(VillagerInfo v)
    {
        if (v.BirthdayDay < 1 || v.DaysToBirthday < 0) return "birthday unknown";
        var s = $"birthday {v.BirthdaySeason} {v.BirthdayDay}";
        if (v.DaysToBirthday == 0) return s + " (TODAY: a gift counts 8x)";
        if (v.DaysToBirthday <= 3) return s + $" (in {v.DaysToBirthday} day{(v.DaysToBirthday == 1 ? "" : "s")})";
        return s;
    }

    // ------------------------------------------------------------------ gift tastes

    /// <summary>The game's taste codes: 0 love, 2 like, 4 dislike, 6 hate, anything else neutral.</summary>
    public static string TasteWord(int code) => code switch { 0 => "loved", 2 => "liked", 4 => "disliked", 6 => "hated", _ => "neutral" };

    public static string TasteVerb(int code) => code switch
    {
        0 => "loved",
        2 => "liked",
        4 => "disliked",
        6 => "hated",
        _ => "was neutral about"
    };

    public static string ItemName(string id)
    {
        try
        {
            return ItemRegistry.GetData(id)?.InternalName ?? id;
        }
        catch (Exception)
        {
            return id;
        }
    }

    public static string Signed(int n) => n >= 0 ? $"+{n}" : n.ToString();

    // ------------------------------------------------------------------ today's schedule

    /// <summary>Where the villager's schedule takes them today: (start time, map, tile), in order. Empty when they stay put.</summary>
    internal static List<(int time, string place, Point tile)> ScheduleToday(NPC npc)
    {
        var list = new List<(int, string, Point)>();
        try
        {
            if (npc.Schedule == null) return list;
            foreach (var kv in npc.Schedule.OrderBy(k => k.Key))
                if (kv.Value != null) list.Add((kv.Key, kv.Value.targetLocationName, kv.Value.targetTile));
        }
        catch (Exception) { /* no schedule readable: none */ }
        return list;
    }

    /// <summary>"09:00 Town (43,57) → 12:30 Saloon (14,19) → ..." or that they stay where they are.</summary>
    internal static string ScheduleText(NPC npc)
    {
        var stops = ScheduleToday(npc);
        return stops.Count == 0 ? "no schedule today (stays where they are)"
            : string.Join(" → ", stops.Select(s => $"{WorldText.FormatTime(s.time)} {s.place} ({s.tile.X},{s.tile.Y})"));
    }

    /// <summary>The villager is in their own home map (house or room), where a visit may hit a locked door.</summary>
    internal static bool AtHome(NPC npc) => npc.currentLocation != null && npc.currentLocation.Name == npc.DefaultMap;

    /// <summary>When the villager is next out of their home today (a schedule stop elsewhere, plus 30 minutes to get there), or null.</summary>
    internal static int? NextOut(NPC npc, int now)
    {
        foreach (var (time, place, _) in ScheduleToday(npc))
            if (time >= now && place != npc.DefaultMap)
                return Utility.ModifyTime(time, 30);
        return null;
    }

    /// <summary>
    /// The villager's whole schedule file from the game (Characters/schedules/NAME): every season, weekday, rain, heart and
    /// marriage variant the game picks from, each stop as "time map (x,y)". GOTO / NOT / MAIL lines are shown as written.
    /// </summary>
    public static string FullSchedule(NPC npc)
    {
        Dictionary<string, string> data;
        try { data = Game1.content.Load<Dictionary<string, string>>("Characters\\schedules\\" + npc.Name); }
        catch (Exception) { return $"{npc.Name} has no schedule file: they stay where they are."; }
        var sb = new StringBuilder($"{npc.Name}'s schedules from the game ({data.Count} variants; the game picks one each morning by date, weather and hearts). Today: {ScheduleText(npc)}.\n");
        foreach (var (key, raw) in data.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"- {key}: {ReadSchedule(raw)}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>"610 Town 47 87 2/1300 Saloon 14 19 2 ..." as "06:10 Town (47,87) → 13:00 Saloon (14,19)".</summary>
    internal static string ReadSchedule(string raw)
    {
        var stops = new List<string>();
        foreach (var part in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            var t = p[0].TrimStart('a');
            if (int.TryParse(t, out int time) && p.Length >= 4)
                stops.Add($"{(p[0].StartsWith("a") ? "arrive " : "")}{WorldText.FormatTime(time)} {p[1]} ({p[2]},{p[3]})");
            else
                stops.Add(part.Trim()); // GOTO spring, NOT friendship Abigail 6, MAIL ...
        }
        return string.Join(" → ", stops);
    }

    /// <summary>Everyone's plan for today, one line each: for planning visits (villagers(schedule=true)).</summary>
    public static string Schedules(Farmer who)
    {
        var sb = new StringBuilder($"Today's schedules ({Game1.currentSeason} {Game1.dayOfMonth}, now {WorldText.FormatTime(Game1.timeOfDay)}), from the game's own data:\n");
        foreach (var npc in Social().OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"{npc.Name} (now {npc.currentLocation?.Name ?? "away"}{(AtHome(npc) ? ", at home" : "")}): {ScheduleText(npc)}");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ text for the model

    public static string Line(VillagerInfo v)
    {
        var status = v.Status.Length > 0 && v.Status != "friendly" ? $" | {v.Status}" : "";
        return $"{v.Name}: {v.Location} ({v.X},{v.Y}) | {v.Hearts} hearts ({v.Points} pts) | talked today: {(v.TalkedToToday ? "yes" : "no")}" +
               $" | gifts {v.GiftsToday} today, {v.GiftsThisWeek} this week | {BirthdayText(v)}{status}{Romance.Brief(v)}";
    }

    public static string Roster(Farmer who)
    {
        var infos = Social().Select(n => Of(who, n)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Villagers on farm day {AgentLog.FarmDay} ({Game1.currentSeason} {Game1.dayOfMonth}, {Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)}). " +
                      "250 points = 1 heart. Talking: +20 points, once a day. Gifts: at most 1 per day and 2 per week per villager; a birthday gift counts 8x.");
        var relationships = Romance.SelfLine(who);
        if (relationships.Length > 0) sb.AppendLine(relationships);
        foreach (var met in infos.Where(i => i.Met))
            sb.AppendLine(Line(met));
        var unmet = infos.Where(i => !i.Met).ToList();
        if (unmet.Count > 0)
            sb.AppendLine("Not met yet (talk_to introduces you): " + string.Join(", ", unmet.Select(i => $"{i.Name} ({i.Location} {i.X},{i.Y})")));
        sb.AppendLine("Romance: villagers marked \"can date\" can be dated and married. bouquet(name) at 8+ hearts starts dating, mermaid_pendant(name) at 10+ hearts proposes " +
                      "(it needs an upgraded house). propose(to) asks another player to marry you.");
        sb.Append("villagers(name) shows one villager's gift tastes and what dating or marriage still needs; talk_to(name) finds them and talks.");
        return sb.ToString();
    }

    /// <summary>Details for one villager: state, your last gift, what other players were seen doing, tastes found out so far.</summary>
    public static string Detail(Farmer who, NPC npc, string viewerSeat, bool revealTastes)
    {
        var v = Of(who, npc);
        var book = NpcKnowledge.Book(who);
        var sb = new StringBuilder();
        sb.AppendLine(Line(v));
        if (!v.Met) sb.AppendLine("You have not met them yet: talk_to introduces you.");
        sb.AppendLine("Today: " + ScheduleText(npc) + (AtHome(npc) && NextOut(npc, Game1.timeOfDay) is int o ? $". At home now; out from about {WorldText.FormatTime(o)}." : "."));
        sb.AppendLine(Romance.Requirements(who, npc, v));

        if (book.LastGift.TryGetValue(npc.Name, out var last))
            sb.AppendLine($"Your last gift to them: {ItemName(last.ItemId)} on farm day {last.Day}; they {TasteVerb(last.Taste)} it ({Signed(last.Points)} points).");
        var seen = NpcKnowledge.SeenText(npc.Name, viewerSeat);
        if (seen != null) sb.AppendLine("Seen: " + seen + ".");

        if (book.Tastes.TryGetValue(npc.Name, out var known) && known.Count > 0)
        {
            sb.AppendLine("What you found out about their tastes by gifting:");
            foreach (var word in new[] { "loved", "liked", "neutral", "disliked", "hated" })
            {
                var names = known.Where(kv => TasteWord(kv.Value) == word).Select(kv => ItemName(kv.Key)).OrderBy(x => x).ToList();
                if (names.Count > 0) sb.AppendLine($"  {word}: {string.Join(", ", names)}");
            }
        }
        else
            sb.AppendLine("You do not know any of their tastes yet: gifts show them, and other players may tell you (or lie).");

        var stacks = who.Items.OfType<SObject>().GroupBy(i => i.QualifiedItemId).ToList();
        if (stacks.Count > 0)
        {
            sb.AppendLine(revealTastes ? "Their reaction to what you carry (RevealGiftTastes is on, straight from the game data):" : "Their reaction to what you carry:");
            foreach (var g in stacks)
            {
                var first = g.First();
                string taste;
                if (SpecialGifts.Contains(first.Name))
                    taste = "a special item, not a normal gift";
                else if (revealTastes)
                    taste = TasteWord((int)npc.getGiftTasteForThisItem(first));
                else if (NpcKnowledge.TryTaste(book, npc.Name, g.Key, out var code))
                    taste = TasteWord(code) + " (you gave it before)";
                else
                    taste = "unknown (give one to find out)";
                sb.AppendLine($"  {first.DisplayName} x{g.Sum(i => i.Stack)}: {taste}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ places

    /// <summary>
    /// Turn what the AI typed into a map name: a real map first, then a shop keeper's name (Pierre = SeedShop),
    /// then any villager (where they are now). Map is null when nothing fits; Note says what was assumed.
    /// </summary>
    public static (string? Map, string Note) ResolvePlace(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return (null, "");
        if (Game1.getLocationFromName(input) != null) return (input, "");

        var key = input.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? input[..^2] : input;
        if (PlaceAliases.TryGetValue(key, out var shop) && Game1.getLocationFromName(shop) != null)
            return (shop, AreaAliases.Contains(key) ? "" : $"{key} works in {shop}.");

        var npc = ByName(key);
        if (npc?.currentLocation != null)
            return (npc.currentLocation.Name, $"{npc.Name} was in {WhereIs(npc)} when you set off; they may have moved.");
        return (null, "");
    }

    /// <summary>
    /// When the door tile is a timed door ("LockedDoorWarp x y Map open close") and it is outside those hours,
    /// a sentence naming the opening hours; otherwise "". Best effort: the action text is parsed as the game writes it.
    /// </summary>
    public static string DoorHours(GameLocation loc, Point doorTile, string next)
    {
        try
        {
            var action = loc.doesTileHaveProperty(doorTile.X, doorTile.Y, "Action", "Buildings");
            if (action == null) return "";
            var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || !parts[0].Equals("LockedDoorWarp", StringComparison.OrdinalIgnoreCase)) return "";
            if (!int.TryParse(parts[4], out var open) || !int.TryParse(parts[5], out var close)) return "";
            int now = Game1.timeOfDay;
            if (now >= open && now < close) return "";
            return $"{next} is closed now (it is {WorldText.FormatTime(now)}; it opens at {WorldText.FormatTime(open)} and closes at {WorldText.FormatTime(close)}). ";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ who is watching

    /// <summary>Other AI seats whose farmer stands on the same map within 12 tiles of <paramref name="self"/>: they can see what it does.</summary>
    public static string[] WhoCanSee(string mySeat, Farmer self)
    {
        var seen = new List<string>();
        foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != mySeat && s.Active))
        {
            var name = (seat.State as ScreenState)?.Name;
            if (name == null) continue;
            var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == name);
            if (other?.currentLocation == null || self.currentLocation == null) continue;
            if (other.currentLocation.NameOrUniqueName != self.currentLocation.NameOrUniqueName) continue;
            if (Vector2.Distance(other.Tile, self.Tile) <= 12f) seen.Add(seat.Id);
        }
        return seen.ToArray();
    }
}

/// <summary>villagers() and the people-aware talk_to (#9). New tools live here, registered in <see cref="AgentTools.All"/>.</summary>
public static partial class AgentTools
{
    internal static List<ToolDef> VillagerTools() => new()
    {
        new()
        {
            Name = "villagers",
            Description = "Everyone you can befriend: where they are right now (map and tile), hearts and friendship points, whether you already talked to them today, " +
                          "gifts given today and this week (max 1 per day and 2 per week), birthday (flagged when it is today or within 3 days), whether you can date them and whether you are dating or married. " +
                          "With name: also what dating or marriage still needs, your last gift to them, what you found out about their tastes and what they would make of the items you carry. " +
                          "Tastes are discovered by giving gifts, nobody knows them from the start. With a name it also gives their schedule for today; schedule=true lists everyone's schedule for today, and with a name their whole schedule file from the game (every season, weekday, rain and heart variant). Costs no game time.",
            Schema = Obj(new
            {
                name = Str("Optional villager name for details, e.g. Abigail"),
                schedule = new { type = "boolean", description = "Everyone's schedule for today (where they go and when), from the game's data" }
            }),
            Instant = true, AppendStatus = false,
            Run = VillagersTool
        },
        new()
        {
            Name = "talk_to",
            Description = "Find a villager, walk to them and talk. Optionally give them an item from your inventory as a gift. " +
                          "If they are on another map you travel there first (go=false: just tell me where they are). " +
                          "Reports where they were, your friendship points before and after, their first line and the reaction to a gift. " +
                          "Talking counts once a day; at most 1 gift per day and 2 per week. Time spent travelling is game time. Ordinary dialogue is read through and closed automatically; the result retains its text. Questions remain open for respond(option).",
            Schema = Obj(new
            {
                name = Str("Villager name, e.g. Pierre"),
                gift_item = Str("Optional item name to give as a gift"),
                go = new { type = "boolean", description = "Travel to them when they are on another map (default true)" },
                reason = Str(ReasonDesc)
            }, "name", "reason"),
            Run = TalkToNpc
        }
    };

    /// <summary>Not an MCP tool: the runner reaches it through GET /api/relationships, which queues it on each seat's observation lane.</summary>
    internal static ToolDef RelationshipsOp() => new()
    {
        Name = "relationships_report",
        Description = "Friendship data of this seat's farmer as JSON (runner only).",
        Schema = Obj(new { }),
        Instant = true, AppendStatus = false, Mail = false,
        Run = (a, c) => Instant(c, JsonSerializer.Serialize(NpcStory.Report(P, Rt.Log.Player), AgentLog.Json))
    };

    private static IEnumerator VillagersTool(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("schedule", out var sch) && sch.ValueKind == JsonValueKind.True)
        {
            if (name.Length == 0) { c.Result = NpcInfo.Schedules(P); yield break; }
            if (NpcInfo.ByName(name) is { } who) { c.Result = NpcInfo.FullSchedule(who); yield break; }
        }
        if (name.Length == 0)
        {
            c.Result = NpcInfo.Roster(P);
            yield break;
        }
        var npc = NpcInfo.ByName(name);
        if (npc == null)
        {
            c.Fail(PlayerSocialHint(name) ?? $"No villager called \"{name}\". Call villagers() without a name for the list.");
            yield break;
        }
        c.Result = NpcInfo.Detail(P, npc, Rt.Log.Player, Config.RevealGiftTastes);
    }

    private static bool NpcFlag(JsonElement a, string name, bool def)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def };
    }

    /// <summary>Game minutes since the farm began; for "time spent" reports.</summary>
    private static int NpcMinutes() => Game1.Date.TotalDays * 1440 + Utility.ConvertTimeToMinutes(Game1.timeOfDay);

    private static (int Points, bool Talked, int GiftsToday) NpcFriendship(string name) =>
        P.friendshipData.TryGetValue(name, out var f) ? (f.Points, f.TalkedToToday, f.GiftsToday) : (0, false, 0);

    /// <summary>Why talking or gifting is pointless right now, or null. Only refuses what the game would refuse for sure.</summary>
    private static string? TalkRefusal(NPC npc, Item? gift)
    {
        if (Game1.eventUp && Game1.CurrentEvent is { isFestival: true } && gift == null) return null;
        if (!P.friendshipData.TryGetValue(npc.Name, out var f)) return null; // not met yet: the first talk introduces you
        if (gift == null)
        {
            return f.TalkedToToday
                ? $"You already talked to {npc.Name} today: talking gives friendship once a day. Give a gift (gift_item) or come back tomorrow."
                : null;
        }
        // A bouquet or Mermaid's Pendant is not an ordinary gift (#53): the game decides whether it is accepted today.
        if (gift is SObject special && NpcInfo.SpecialGifts.Contains(special.Name)) return null;
        if (f.GiftsToday >= 1)
            return $"You already gave {npc.Name} a gift today (limit 1 per day). Try again tomorrow.";
        // Birthdays and spouses are exempt from the weekly limit in the vanilla game, so the game decides those.
        bool spouse = f.Status.ToString().Equals("Married", StringComparison.OrdinalIgnoreCase);
        bool birthday = NpcInfo.Of(P, npc).DaysToBirthday == 0;
        if (f.GiftsThisWeek >= 2 && !spouse && !birthday)
            return $"You already gave {npc.Name} 2 gifts this week (the limit; it starts over on Monday). Talk to them instead, or wait.";
        return null;
    }

    internal static IEnumerator TalkTo(JsonElement a, CallContext c) => TalkToNpc(a, c);

    /// <summary>talk_to for another farmer: go to their map and stand next to them; players talk with say, not talk_to.</summary>
    private static IEnumerator WalkUpToFarmer(PlayerSlot mate, CallContext c)
    {
        static int Dist(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        var label = SocialTools.Label(mate);
        var them = Romance.FarmerOf(mate);
        if (them?.currentLocation == null) { c.Fail(Reasons.NotHere, $"{label} is not in the world right now."); yield break; }
        if (them.currentLocation.NameOrUniqueName != Loc.NameOrUniqueName)
        {
            var trip = new CallContext();
            yield return TravelTo(them.currentLocation.Name, trip);
            if (!trip.Ok) { c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get to {label} in {them.currentLocation.Name}: {trip.Result}"); yield break; }
        }
        for (int leg = 0; leg < 6; leg++)
        {
            them = Romance.FarmerOf(mate);
            if (them?.currentLocation == null || them.currentLocation.NameOrUniqueName != Loc.NameOrUniqueName) break;
            var aim = them.TilePoint;
            if (Dist(P.TilePoint, aim) <= 1) break;
            var walk = new CallContext();
            yield return WalkAdjacent(aim, walk, () => Romance.FarmerOf(mate) is { } f && Dist(f.TilePoint, aim) >= 2);
            if (!walk.Ok) yield return Ticks(30);
        }
        them = Romance.FarmerOf(mate);
        var near = them != null && them.currentLocation?.NameOrUniqueName == Loc.NameOrUniqueName && Dist(P.TilePoint, them.TilePoint) <= 3;
        if (!near) { c.Fail(Reasons.NoPath, $"Couldn't catch up with {label}; they are in {them?.currentLocation?.Name ?? "?"}. look shows them when they are near."); yield break; }
        c.Result = $"You are next to {label} now. Farmers talk with say, not talk_to: call say(text=...) now and they will hear you.";
    }

    private static string? PlayerSocialHint(string name)
    {
        var other = Players.Slots.FirstOrDefault(s => (s.Role is SeatRole.Ai or SeatRole.Human) &&
            (string.Equals((s.State as ScreenState)?.Name, name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Config.Seat(s.Screen).Name, name, StringComparison.OrdinalIgnoreCase)));
        if (other == null) return null;
        var state = other.State as ScreenState;
        string position = state is { Location: not null, Tile: { Length: >= 2 } }
            ? $" Last known position: {state.Location} ({state.Tile[0]},{state.Tile[1]})." : "";
        return $"{name} is a PLAYER, not a villager. talk_to and villagers(name) are for NPCs only.{position} " +
            $"travel_to(location=\"{name}\") takes you to their map; then walk nearby and say(text=...) so they can hear you. For NPC friendship call villagers() and choose an actual villager.";
    }

    /// <summary>
    /// Walk up to a villager who may be walking their schedule: aim at where they are, and re-aim as soon as they have moved
    /// two tiles from there, instead of walking to a tile they left long ago. Stops next to them, facing them.
    /// </summary>
    private static IEnumerator ChaseNpc(string name, CallContext c)
    {
        static int Dist(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        string last = "";
        for (int leg = 0; leg < 12; leg++)
        {
            var npc = NpcInfo.ByName(name);
            if (npc == null || !NpcInfo.OnMap(npc, Loc)) { c.Fail(Reasons.NotHere, $"{name} left {Loc.Name}"); yield break; }
            var aim = npc.TilePoint;
            if (Dist(P.TilePoint, aim) == 1) { Face(aim); yield break; }
            var walk = new CallContext();
            yield return WalkAdjacent(aim, walk, () =>
            {
                var now = NpcInfo.ByName(name)?.TilePoint ?? aim;
                return Dist(now, aim) >= 2 || Dist(P.TilePoint, now) == 1;
            });
            if (!walk.Ok)
            {
                last = walk.Result;
                if (last.Contains(LockedDoorNote)) break; // more legs only bump into the same door
                yield return Ticks(30); // they may be in a doorway or a narrow path: give them a moment to move on
            }
        }
        var at = NpcInfo.ByName(name)?.TilePoint;
        if (at is Point p && Dist(P.TilePoint, p) == 1) { Face(p); yield break; }
        c.Fail(Reasons.NoPath, last.Length > 0 ? last : $"couldn't catch up with {name}");
    }

    /// <summary>These talk_to arguments with the villager (and the gift, when given) filled in.</summary>
    private static JsonElement WithGift(JsonElement a, string name, string? gift)
    {
        var d = new Dictionary<string, object?>();
        if (a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) d[p.Name] = p.Value.Clone();
        d["name"] = name;
        if (gift != null) d["gift_item"] = gift;
        return JsonSerializer.SerializeToElement(d);
    }

    private static readonly string[] AnyoneWords = { "nearest", "anyone", "someone", "somebody", "any" };
    private static readonly string[] BestGiftWords = { "best", "any", "something", "anything" };

    private static IEnumerator TalkToNpc(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        var giftAsked = S(a, "gift_item").Trim();
        // "Give someone a gift" (2026-11-08: the model guessed Weeds for Sebastian): the nearest villager who can still
        // take a gift (or talk) today, and the best gift the farmer knows of.
        if (name.Length == 0 || AnyoneWords.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            bool forGift = giftAsked.Length > 0;
            if (NpcInfo.NearestFor(P, forGift) is not { } pick)
            {
                c.Fail(forGift ? "Everyone had their gift today (or two this week)." : "You talked to everyone you can reach today.");
                yield break;
            }
            name = pick.Name;
        }
        if (BestGiftWords.Contains(giftAsked, StringComparer.OrdinalIgnoreCase) && NpcInfo.ByName(name) is { } forWhom)
        {
            if (NpcInfo.BestGift(P, forWhom) is not { } best) { c.Fail(Reasons.NoItem, "You carry nothing that makes a good gift: forage, crops, fish, gems or a cooked dish."); yield break; }
            a = WithGift(a, name, best.Name);
        }
        else if (!string.Equals(S(a, "name").Trim(), name, StringComparison.Ordinal)) a = WithGift(a, name, giftAsked.Length > 0 ? giftAsked : null);
        if (name.Length == 0) { c.Fail("name is empty. villagers() lists who you can talk to."); yield break; }
        if (Game1.activeClickableMenu != null)
        {
            c.Fail("A menu or dialogue is open. Finish it with respond() or close_menu first.");
            yield break;
        }
        var npc = NpcInfo.ByName(name);
        bool go = NpcFlag(a, "go", true);
        if (npc == null)
        {
            // Another farmer: walk up to them, so the next say is heard (the 14B run asked talk_to for AI2 twelve times).
            var mate = go ? SocialTools.OtherAis().FirstOrDefault(s => string.Equals(SocialTools.Label(s), name, StringComparison.OrdinalIgnoreCase)) : null;
            if (mate != null) { yield return WalkUpToFarmer(mate, c); yield break; }
            c.Fail(PlayerSocialHint(name) ?? $"No villager called \"{name}\". villagers() lists who you can talk to.");
            yield break;
        }

        Item? giftItem = null;
        int giftSlot = -1;
        var gift = S(a, "gift_item").Trim();
        if (gift.Length > 0)
        {
            giftSlot = FindItem(gift, it => it is SObject);
            if (giftSlot < 0 && RemoteChest is { } remote)
            {
                // The storage hands it over where you stand (another mod's chest tool).
                var took = new CallContext();
                yield return RemoteChestCall(remote, new { action = "take", item = gift, quantity = 1, reason = $"a gift for {S(a, "name")}" }, took);
                giftSlot = FindItem(gift, it => it is SObject);
            }
            if (giftSlot < 0) { c.Fail($"No \"{gift}\" in inventory{(RemoteChest != null ? " or the storage" : "")} to gift."); yield break; }
            giftItem = P.Items[giftSlot];
        }

        var refusal = TalkRefusal(npc, giftItem);
        if (refusal != null) { c.Fail(refusal); yield break; }

        int startMinutes = NpcMinutes();
        bool traveled = false, opened = false;
        string failure = "";
        var before = (Points: 0, Talked: false, GiftsToday: 0);
        int tasteCode = -1;
        int stackBefore = giftItem?.Stack ?? 0;
        bool birthdayToday = false;

        // Find them (they walk around, so look them up again after every trip or failed approach), go to them, talk.
        for (int cycle = 0; cycle < 3 && !opened; cycle++)
        {
            npc = NpcInfo.ByName(name) ?? npc;
            if (!NpcInfo.OnMap(npc, Loc))
            {
                if (npc.currentLocation == null) { failure = "they can't be found right now"; break; }
                if (!go)
                {
                    c.Fail($"{npc.Name} is not on this map ({Loc.Name}). {npc.Name} is in {NpcInfo.WhereIs(npc)}. " +
                           $"travel_to {npc.currentLocation.Name}, or call talk_to with go=true to be taken there.");
                    yield break;
                }
                var trip = new CallContext();
                yield return TravelTo(npc.currentLocation.Name, trip);
                traveled = true;
                npc = NpcInfo.ByName(name) ?? npc;
                if (!NpcInfo.OnMap(npc, Loc))
                {
                    if (!trip.Ok) { failure = $"couldn't reach {npc.Name} in {NpcInfo.WhereIs(npc)}: {trip.Result}"; break; }
                    failure = $"{npc.Name} moved on to {NpcInfo.WhereIs(npc)}";
                    continue;
                }
            }

            for (int attempt = 0; attempt < 4 && !opened; attempt++)
            {
                npc = NpcInfo.ByName(name) ?? npc;
                if (!NpcInfo.OnMap(npc, Loc)) break; // they left this map: the outer loop follows them
                var walk = new CallContext();
                yield return ChaseNpc(name, walk);
                npc = NpcInfo.ByName(name) ?? npc;
                if (!walk.Ok)
                {
                    failure = walk.Result.TrimEnd('.');
                    // Behind a bedroom door the farmer may not open yet: stop here instead of bumping into it until the timeout.
                    if (walk.Result.Contains(LockedDoorNote))
                    {
                        c.Fail(Reasons.NoPath, $"{npc.Name} is behind a door you may not open yet (their bedroom needs more friendship). " +
                                               $"Talk to someone else now, or to {npc.Name} later when they come out. {walk.Result}");
                        yield break;
                    }
                    continue;
                }
                var me = P.TilePoint;
                if (Math.Abs(me.X - npc.TilePoint.X) + Math.Abs(me.Y - npc.TilePoint.Y) != 1) continue; // they moved
                Face(npc.TilePoint);
                if (giftSlot >= 0) P.CurrentToolIndex = giftSlot;
                else if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();

                before = NpcFriendship(npc.Name);
                if (giftItem != null)
                {
                    tasteCode = (int)npc.getGiftTasteForThisItem(giftItem);
                    birthdayToday = NpcInfo.Of(P, npc).DaysToBirthday == 0;
                }
                bool handled = Game1.tryToCheckAt(npc.TilePoint.ToVector2(), P);
                if (!handled && Game1.activeClickableMenu == null && !Game1.dialogueUp) FestivalAction(npc.TilePoint); // festival booths/actors
                yield return Ticks(20);
                opened = Game1.activeClickableMenu != null || Game1.dialogueUp;
            }
        }

        int spent = NpcMinutes() - startMinutes;
        var spentText = spent >= 10 ? $" Took {spent} game minutes (now {WorldText.FormatTime(Game1.timeOfDay)})." : "";
        if (!opened)
        {
            npc = NpcInfo.ByName(name) ?? npc;
            var why = failure.Length > 0 ? failure : $"they may be busy, asleep or moving; {npc.Name} is in {NpcInfo.WhereIs(npc)} now";
            c.Fail($"Couldn't start a conversation with {npc.Name} ({why}).{spentText}");
            yield break;
        }

        var after = NpcFriendship(npc.Name);
        int delta = after.Points - before.Points;
        var sb = new StringBuilder($"Talking to {npc.Name} (friendship {P.getFriendshipHeartLevelForNPC(npc.Name)} hearts).");
        if (traveled) sb.Append($" You went to {Loc.Name}.");
        sb.Append($" Points {before.Points} -> {after.Points} ({NpcInfo.Signed(delta)}).");
        // Days 58-100: about 100 talks and one gift, so nobody got past 3 hearts.
        if (giftItem == null) sb.Append(" A gift moves hearts far faster: next visit bring forage, a crop or a mine find as gift_item.");

        bool accepted = false;
        if (giftItem != null)
        {
            // A bouquet or pendant may change neither the points nor the gift counter: the item leaving the pack counts too.
            bool taken = giftSlot >= P.Items.Count || !ReferenceEquals(P.Items[giftSlot], giftItem) || giftItem.Stack < stackBefore;
            accepted = after.GiftsToday > before.GiftsToday || delta != 0 || taken;
            var itemName = giftItem.DisplayName;
            if (!accepted)
                sb.Append($" The gift ({itemName}) was not accepted.");
            else if (NpcInfo.SpecialGifts.Contains(giftItem.Name))
            {
                sb.Append($" You gave {itemName}.");
                if (giftItem.Name.Equals("Bouquet", StringComparison.OrdinalIgnoreCase))
                    NpcStory.Emit(Rt.Log, "bouquet_given", $"{P.Name} gave {npc.Name} a bouquet", new { villager = npc.Name });
            }
            else
            {
                bool firstTime = !NpcKnowledge.TryTaste(NpcKnowledge.Book(P), npc.Name, giftItem.QualifiedItemId, out _);
                sb.Append($" Gift: {npc.Name} {NpcInfo.TasteVerb(tasteCode)} {itemName}{(firstTime ? " (now you know this taste)" : "")}.");
                NpcKnowledge.Learn(P, npc.Name, giftItem.QualifiedItemId, tasteCode, AgentLog.FarmDay, delta);
                NpcStory.Emit(Rt.Log, "gift", $"{P.Name} gave {npc.Name} {itemName}: {NpcInfo.TasteWord(tasteCode)}",
                    new { villager = npc.Name, item = itemName, taste = NpcInfo.TasteWord(tasteCode), points = delta, birthday = birthdayToday });
                if (birthdayToday)
                    NpcStory.Emit(Rt.Log, "birthday", $"{P.Name} gave {npc.Name} a birthday gift ({itemName})", new { villager = npc.Name, item = itemName, points = delta });
            }
        }

        if (accepted || (!before.Talked && after.Talked))
            NpcKnowledge.RecordPublic(npc.Name, new PublicTalk(AgentLog.FarmDay, WorldText.FormatTime(Game1.timeOfDay), Rt.Log.Player,
                P.Name, accepted ? "gift" : "talk", NpcInfo.WhoCanSee(Rt.Log.Player, P)));

        if (Game1.activeClickableMenu is DialogueBox box)
        {
            var line = GameEnglish.Said(box).Replace("\n", " ").Replace("^", " ").Trim();
            if (line.Length > 0) sb.Append($" {npc.Name} says: \"{line}\"");
        }
        sb.Append(spentText);
        var finished = new CallContext();
        yield return FinishMessages(finished);
        if (finished.Result.Length > 0) sb.Append('\n').Append(finished.Result);
        // A delivery finished by this talk leaves its money in the journal: take it now, the way a player clicks the reward.
        for (int i = 0; i < 3 && Game1.activeClickableMenu == null && JournalRewards().Any(); i++)
        {
            var claim = new CallContext();
            yield return QuestJournal(ArgsOf(new { action = "claim_reward" }), claim);
            sb.Append('\n').Append(claim.Result);
            if (!claim.Ok) break;
        }
        c.Result = sb.ToString();
    }
}
