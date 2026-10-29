using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.TokenizableStrings;

namespace StardewMCP.Agent;

/// <summary>
/// fish_guide: where, when and in what weather every fish bites, read from the game's own data (Data/Locations spawn
/// entries + Data/Fish times, weather and level), so modded fish are covered too. Legendary fish come with the exact
/// tiles their bobber has to land in; the fish tool aims there by itself (<see cref="BossTargets"/>).
/// The text helpers are game-free in <see cref="FishGuideText"/>.
/// </summary>
public static class FishGuide
{
    /// <summary>One place a fish spawns.</summary>
    internal sealed class Row
    {
        public string Qid = "", Id = "", Name = "", Map = "", Place = "", Area = "", Condition = "", Weather = "";
        public HashSet<string>? Seasons;
        public List<(int from, int to)> Times = new();
        public int MinLevel, CatchLimit;
        public bool Boss;
        public Rectangle? Bobber, Stand;
        public SpawnFishData Spawn = null!;
    }

    public static ToolDef Tool() => new()
    {
        Name = "fish_guide",
        Description = "Where and when every fish bites, from the game's own data: map, season, hours, weather and the Fishing level it needs, " +
                      "and which ones you have never caught. With no name: the fish you can still catch today, map by map, plus every legendary fish and how to get it. " +
                      "With a name: all the places and times of that fish. Legendary fish need an exact spot: walk_to near it and use fish, which aims at that spot by itself. Costs no game time.",
        Schema = AgentTools.Obj(new
        {
            name = AgentTools.Str("Fish name, e.g. Catfish or Legend (optional)"),
            season = AgentTools.Str("spring, summer, fall or winter: list that season instead of today (optional)")
        }),
        Instant = true, AppendStatus = false,
        Run = (a, c) => KnowledgeTools.Report(c, "fish_guide", () => Build(a))
    };

    private static Farmer P => Game1.player;

    private static (bool ok, string text) Build(JsonElement a)
    {
        var rows = Rows();
        var name = AgentTools.S(a, "name").Trim();
        if (!string.IsNullOrEmpty(name))
            return OneFish(rows, name);
        var season = AgentTools.S(a, "season").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(season) && !FishGuideText.SeasonNames.Contains(season))
            return (false, $"Unknown season \"{season}\": use spring, summer, fall or winter.");
        return (true, Overview(rows, string.IsNullOrEmpty(season) ? null : season));
    }

    // ------------------------------------------------------------------ data

    internal static List<Row> Rows()
    {
        var fish = DataLoader.Fish(Game1.content);
        var rows = new List<Row>();
        foreach (var (map, data) in DataLoader.Locations(Game1.content))
        {
            if (data?.Fish == null)
                continue;
            string place = map == "Default" ? "any water" : PlaceName(map, data);
            foreach (var spawn in data.Fish)
            {
                if (spawn?.ItemId == null)
                    continue;
                string? qid;
                try { qid = ItemRegistry.QualifyItemId(spawn.ItemId); }
                catch (Exception) { qid = null; }
                if (qid == null || !qid.StartsWith("(O)"))
                    continue;
                string id = qid[3..];
                if (!fish.TryGetValue(id, out var raw))
                    continue; // trash, algae and other non-fish catches
                var f = raw.Split('/');
                if (f.Length > 1 && f[1] == "trap")
                    continue;
                var row = new Row
                {
                    Qid = qid, Id = id, Map = map, Place = place, Spawn = spawn,
                    Name = ItemRegistry.GetData(qid)?.DisplayName ?? (f.Length > 0 ? f[0] : id),
                    Condition = spawn.Condition ?? "",
                    Seasons = spawn.Season is Season s ? new HashSet<string> { s.ToString().ToLowerInvariant() } : FishGuideText.ConditionSeasons(spawn.Condition),
                    MinLevel = spawn.MinFishingLevel,
                    CatchLimit = spawn.CatchLimit,
                    Boss = spawn.IsBossFish || FishGuideText.LegendaryIds.Contains(id),
                    Bobber = spawn.BobberPosition,
                    Stand = spawn.PlayerPosition
                };
                if (spawn.FishAreaId != null && data.FishAreas != null && data.FishAreas.TryGetValue(spawn.FishAreaId, out var area))
                    row.Area = Parse(area.DisplayName) ?? "";
                if (!spawn.IgnoreFishDataRequirements)
                {
                    if (f.Length > 5) row.Times = FishGuideText.TimeWindows(f[5]);
                    if (f.Length > 7) row.Weather = f[7];
                    if (f.Length > 12 && int.TryParse(f[12], out int lvl)) row.MinLevel = Math.Max(row.MinLevel, lvl);
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    private static string PlaceName(string map, LocationData data)
    {
        var shown = Parse(data.DisplayName);
        return string.IsNullOrWhiteSpace(shown) || shown.Equals(map, StringComparison.OrdinalIgnoreCase) ? map : $"{map} ({shown})";
    }

    private static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try { return TokenParser.ParseText(text); }
        catch (Exception) { return null; }
    }

    private static int Caught(string qid) =>
        P.fishCaught.TryGetValue(qid, out var v) && v != null && v.Length > 0 ? v[0] : 0;

    private static GameLocation? Loc(string map) => map == "Default" ? Game1.currentLocation : Game1.getLocationFromName(map);

    /// <summary>Why the fish of this row can't be caught today (empty = it can, now or later today).</summary>
    private static string NotToday(Row r)
    {
        var loc = Loc(r.Map);
        var season = (loc != null ? Game1.GetSeasonForLocation(loc) : Game1.season).ToString().ToLowerInvariant();
        if (r.Seasons != null && !r.Seasons.Contains(season))
            return "not this season";
        if (r.CatchLimit > 0 && Caught(r.Qid) >= r.CatchLimit)
            return "already caught (only once)";
        if (P.fishingLevel.Value < r.MinLevel)
            return $"needs Fishing level {r.MinLevel} (you have {P.fishingLevel.Value})";
        bool raining = loc != null ? Game1.IsRainingHere(loc) : Game1.isRaining;
        if (!FishGuideText.WeatherFits(r.Weather, raining))
            return raining ? "only when it is not raining" : "only in rain";
        if (!FishGuideText.InWindow(Game1.timeOfDay, r.Times, laterToday: true))
            return "too late today";
        if (r.Condition.Length > 0 && !ConditionHolds(r.Condition, loc))
            return "its condition is not met today";
        return "";
    }

    private static bool ConditionHolds(string condition, GameLocation? loc)
    {
        try { return GameStateQuery.CheckConditions(condition, loc, P, null, null, null, new HashSet<string>(FishGuideText.RandomKeys)); }
        catch (Exception) { return true; }
    }

    /// <summary>Season, hours, weather, level and conditions of a row, joined with " Â· ".</summary>
    private static string When(Row r, bool withSeason = true)
    {
        var parts = new List<string>();
        if (withSeason)
            parts.Add(r.Seasons == null || r.Seasons.Count == 4 ? "all seasons" : string.Join("/", FishGuideText.SeasonNames.Where(r.Seasons.Contains)));
        parts.Add(FishGuideText.FormatWindows(r.Times));
        var weather = FishGuideText.WeatherText(r.Weather);
        if (weather.Length > 0) parts.Add(weather);
        if (r.MinLevel > 0) parts.Add($"Fishing level {r.MinLevel}+");
        parts.AddRange(FishGuideText.ConditionNotes(r.Condition));
        return string.Join(" Â· ", parts);
    }

    private static string Spot(Row r)
    {
        var parts = new List<string>();
        if (r.Bobber is Rectangle b)
            parts.Add(b.Width <= 1 && b.Height <= 1 ? $"cast at tile ({b.X},{b.Y})" : $"cast into tiles ({b.X},{b.Y})-({b.Right - 1},{b.Bottom - 1})");
        if (r.Stand is Rectangle s)
            parts.Add($"stand within ({s.X},{s.Y})-({s.Right - 1},{s.Bottom - 1})");
        return string.Join(", ", parts);
    }

    private static string PlaceOf(Row r) => r.Area.Length > 0 ? $"{r.Place}, {r.Area}" : r.Place;

    // ------------------------------------------------------------------ reports

    private static (bool ok, string text) OneFish(List<Row> rows, string query)
    {
        var names = rows.Select(r => r.Name).Distinct().ToList();
        var match = names.FirstOrDefault(n => n.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? names.Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n.Length).FirstOrDefault();
        if (match == null)
        {
            var fish = DataLoader.Fish(Game1.content);
            foreach (var (id, raw) in fish)
            {
                var f = raw.Split('/');
                var shown = ItemRegistry.GetData("(O)" + id)?.DisplayName ?? f[0];
                if (!shown.Equals(query, StringComparison.OrdinalIgnoreCase) && !f[0].Equals(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (f.Length > 1 && f[1] == "trap")
                    return (true, $"{shown}: caught with a crab pot (Fishing level 3 recipe) in {(f.Length > 4 && f[4] == "ocean" ? "ocean water (the Beach)" : "fresh water (rivers, lakes, ponds)")}, not with a rod. Caught so far: {Caught("(O)" + id)}.");
                if (FishGuideText.CodePlaces.TryGetValue(id, out var where))
                    return (true, $"{shown}: {where} Â· {FishGuideText.FormatWindows(FishGuideText.TimeWindows(f.Length > 5 ? f[5] : ""))}. Caught so far: {Caught("(O)" + id)}.");
                return (true, $"{shown}: the game data names no place where it bites.");
            }
            return (false, $"No fish called \"{query}\". Call fish_guide without a name for the list.");
        }

        var mine = rows.Where(r => r.Name == match).ToList();
        var first = mine[0];
        var sb = new StringBuilder();
        int caught = Caught(first.Qid);
        sb.Append($"{match}{(mine.Any(r => r.Boss) ? " (LEGENDARY)" : "")}: caught {caught} time(s) so far.");
        foreach (var r in mine.OrderBy(r => r.Place))
        {
            sb.Append($"\n- {PlaceOf(r)}: {When(r)}");
            var spot = Spot(r);
            if (spot.Length > 0) sb.Append($" Â· {spot}");
            if (r.CatchLimit > 0) sb.Append($" Â· can be caught {r.CatchLimit} time(s) only");
            var why = NotToday(r);
            sb.Append(why.Length == 0 ? " Â· TODAY: yes" : $" Â· today: no, {why}");
        }
        if (mine.Any(r => r.Boss))
            sb.Append("\nLegendary fish are hard and bite rarely: walk_to the shore next to the spot, then fish (it aims at the legendary spot when it is in reach). Bring food for energy.");
        return (true, sb.ToString());
    }

    private static string Overview(List<Row> rows, string? season)
    {
        var sb = new StringBuilder();
        var normal = rows.Where(r => !r.Boss).ToList();
        if (season == null)
        {
            sb.Append($"Fish you can still catch today (Fishing level {P.fishingLevel.Value}, {WorldText.FormatTime(Game1.timeOfDay)}{(Game1.isRaining ? ", raining" : "")}). NEW = never caught:");
            normal = normal.Where(r => NotToday(r).Length == 0).ToList();
        }
        else
        {
            sb.Append($"Fish of {season} by map (hours and weather still apply). NEW = never caught:");
            normal = normal.Where(r => r.Seasons == null || r.Seasons.Contains(season)).ToList();
        }
        foreach (var g in normal.GroupBy(r => r.Place).OrderBy(g => g.Key == "any water" ? 1 : 0).ThenBy(g => g.Key))
        {
            var fish = g.GroupBy(r => r.Name).Select(f =>
            {
                var r = f.First();
                var bits = new List<string>();
                if (Caught(r.Qid) == 0) bits.Add("NEW");
                var hours = FishGuideText.FormatWindows(r.Times);
                if (hours != "any time") bits.Add(hours);
                var weather = FishGuideText.WeatherText(r.Weather);
                if (weather.Length > 0) bits.Add(weather);
                return (r, text: bits.Count > 0 ? $"{r.Name} ({string.Join(", ", bits)})" : r.Name);
            }).OrderBy(x => Caught(x.r.Qid) == 0 ? 0 : 1).ThenBy(x => x.r.Name).Select(x => x.text);
            sb.Append($"\n- {g.Key}: {string.Join(", ", fish)}");
        }
        if (normal.Count == 0)
            sb.Append("\n- none");

        var withRows = new HashSet<string>(rows.Select(r => r.Id));
        var other = FishGuideText.CodePlaces.Where(kv => !withRows.Contains(kv.Key))
            .Select(kv => $"{ItemRegistry.GetData("(O)" + kv.Key)?.DisplayName ?? kv.Key}: {kv.Value}").ToList();
        if (other.Count > 0)
            sb.Append("\nOther places: ").Append(string.Join("; ", other)).Append('.');

        sb.Append("\nLegendary fish (one catch each; hard):");
        foreach (var f in rows.Where(r => r.Boss).GroupBy(r => r.Name).OrderBy(g => g.Key))
        {
            var r = f.First();
            bool family = r.Condition.IndexOf("LEGENDARY_FAMILY", StringComparison.OrdinalIgnoreCase) >= 0;
            if (family && NotToday(r).Length > 0)
                continue; // the Extended Family versions only matter while Qi's order runs
            int caught = Caught(r.Qid);
            var why = NotToday(r);
            sb.Append($"\n- {r.Name}: {PlaceOf(r)} Â· {When(r)}");
            var spot = Spot(r);
            if (spot.Length > 0) sb.Append($" Â· {spot}");
            sb.Append(caught > 0 && r.CatchLimit > 0 ? " Â· CAUGHT" : why.Length == 0 ? " Â· TODAY: yes" : $" Â· today: no, {why}");
        }
        sb.Append("\nCrab pots catch shellfish without a rod (ocean ones at the Beach, the rest in fresh water). fish_guide(name=\"...\") gives one fish's places and times.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the fish tool

    /// <summary>Legendary spots on this map the farmer can catch today: the fish tool aims its bobber into them.</summary>
    internal static List<(string name, Rectangle bobber, Rectangle? stand)> BossTargets(GameLocation loc)
    {
        var list = new List<(string, Rectangle, Rectangle?)>();
        try
        {
            foreach (var r in Rows())
                if (r.Boss && r.Bobber is Rectangle b && r.Map == loc.Name && NotToday(r).Length == 0)
                    list.Add((r.Name, b, r.Stand));
        }
        catch (Exception)
        {
            // no targets: the fish tool fishes as usual
        }
        return list;
    }
}
