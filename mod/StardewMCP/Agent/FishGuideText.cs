using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP.Agent;

/// <summary>
/// The game-free part of <see cref="FishGuide"/>: reading the time, weather and condition fields of the fish data
/// into plain words, and the places the game decides in code rather than in Data/Locations. Linked into the tests.
/// </summary>
public static class FishGuideText
{
    public static readonly string[] SeasonNames = { "spring", "summer", "fall", "winter" };

    /// <summary>Data/Fish field 5 ("600 1900 2100 2600") as start/end pairs; empty when it cannot be read.</summary>
    public static List<(int from, int to)> TimeWindows(string? field)
    {
        var list = new List<(int, int)>();
        var parts = (field ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i += 2)
            if (int.TryParse(parts[i], out int a) && int.TryParse(parts[i + 1], out int b) && b > a)
                list.Add((a, b));
        return list;
    }

    /// <summary>"06:00-19:00" or "any time" (a window from 06:00 to 02:00 covers the whole day).</summary>
    public static string FormatWindows(IReadOnlyList<(int from, int to)> windows)
    {
        if (windows.Count == 0 || windows.Any(w => w.from <= 600 && w.to >= 2600))
            return "any time";
        return string.Join(" and ", windows.Select(w => $"{WorldText.FormatTime(w.from)}-{WorldText.FormatTime(w.to)}"));
    }

    /// <summary>The fish bites at <paramref name="time"/>, or later today when <paramref name="laterToday"/>.</summary>
    public static bool InWindow(int time, IReadOnlyList<(int from, int to)> windows, bool laterToday = false) =>
        windows.Count == 0 || windows.Any(w => laterToday ? time < w.to : time >= w.from && time < w.to);

    /// <summary>Data/Fish field 7: "sunny", "rainy" or "both".</summary>
    public static string WeatherText(string? field) => field?.Trim().ToLowerInvariant() switch
    {
        "sunny" => "sun only",
        "rainy" => "rain only",
        _ => ""
    };

    public static bool WeatherFits(string? field, bool raining) => field?.Trim().ToLowerInvariant() switch
    {
        "sunny" => !raining,
        "rainy" => raining,
        _ => true
    };

    /// <summary>The seasons a condition string limits a spawn to (LOCATION_SEASON / SEASON), or null when it names none.</summary>
    public static HashSet<string>? ConditionSeasons(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;
        HashSet<string>? found = null;
        foreach (var clause in condition.Split(','))
        {
            var words = clause.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[0].StartsWith("!"))
                continue;
            int start = words[0].Equals("SEASON", StringComparison.OrdinalIgnoreCase) ? 1
                : words[0].Equals("LOCATION_SEASON", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
            if (start < 0)
                continue;
            found ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in words.Skip(start))
                if (SeasonNames.Contains(w.ToLowerInvariant()))
                    found.Add(w.ToLowerInvariant());
        }
        return found;
    }

    /// <summary>Plain words for the parts of a condition the model should know about (weather, Qi's quest, other rules).</summary>
    public static List<string> ConditionNotes(string? condition)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(condition))
            return notes;
        foreach (var raw in condition.Split(','))
        {
            var clause = raw.Trim();
            var key = clause.Split(' ')[0].ToUpperInvariant();
            bool not = key.StartsWith("!");
            key = key.TrimStart('!');
            if (key is "SEASON" or "LOCATION_SEASON")
                continue;
            if (key is "WEATHER" or "LOCATION_WEATHER" or "IS_RAINING")
                notes.Add(clause.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0 || key == "IS_RAINING"
                    ? (not ? "not in rain" : "rain only") : (not ? "not in that weather" : "only in some weather"));
            else if (clause.IndexOf("LEGENDARY_FAMILY", StringComparison.OrdinalIgnoreCase) >= 0)
                notes.Add(not ? "not during Mr. Qi's Extended Family order" : "only during Mr. Qi's Extended Family special order");
            else if (key is "RANDOM" or "SYNCED_RANDOM" or "SYNCED_CHOICE" or "SYNCED_DAY_RANDOM" or "SYNCED_SUMMER_RAIN_RANDOM")
                notes.Add("only some days");
            else if (key is "YEAR")
                notes.Add("from a later year");
            else if (key.Length > 0 && !notes.Contains("other conditions"))
                notes.Add("other conditions");
        }
        return notes;
    }

    /// <summary>Condition keys that change by chance: ignored when asking the game whether a spawn is on today.</summary>
    public static readonly string[] RandomKeys = { "RANDOM", "SYNCED_RANDOM", "SYNCED_CHOICE", "SYNCED_DAY_RANDOM", "SYNCED_SUMMER_RAIN_RANDOM" };

    /// <summary>Fish the game places in code (not in Data/Locations): unqualified object id → where to catch it.</summary>
    public static readonly IReadOnlyDictionary<string, string> CodePlaces = new Dictionary<string, string>
    {
        ["158"] = "the Mines, floor 20 lake (any season)",
        ["161"] = "the Mines, floor 60 lake (any season)",
        ["162"] = "the Mines, floor 100 lake, or the Volcano on Ginger Island (any season)",
        ["798"] = "the Night Market submarine (winter 15-17, from the Beach)",
        ["799"] = "the Night Market submarine (winter 15-17, from the Beach)",
        ["800"] = "the Night Market submarine (winter 15-17, from the Beach)",
        ["154"] = "the Night Market submarine, or the ocean"
    };

    /// <summary>The legendary fish by unqualified id, so they are listed as legendary even when the data has no boss flag.</summary>
    public static readonly ISet<string> LegendaryIds = new HashSet<string> { "159", "160", "163", "682", "775", "898", "899", "900", "901", "902" };
}
