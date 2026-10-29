using System;
using System.Collections.Generic;
using System.Text;

namespace StardewMCP.Agent;

/// <summary>
/// Localized names (villagers, items, professions, monsters, achievements, places, quests) and the English keys they stand
/// for. Every text an AI reads goes through <see cref="Apply"/>, so a model never reads or types a Thai name: in the Thai run
/// a 9B model misspelled villager names it had only seen in Thai and talk_to found nobody. Only names with a non-ASCII
/// character are replaced, longest first, so English text is never touched. Game-free; GameEnglish fills it on save load.
/// </summary>
public sealed class NameMap
{
    private readonly Dictionary<char, List<(string from, string to)>> _byFirst = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public int Count => _known.Count;

    /// <summary>Adds localized → English. Skipped: blanks, one-letter names, names without a non-ASCII letter, non-ASCII "English".</summary>
    public void Add(string? localized, string? english)
    {
        localized = localized?.Trim();
        english = english?.Trim();
        if (string.IsNullOrEmpty(localized) || string.IsNullOrEmpty(english) || localized.Length < 2) return;
        if (!HasNonAscii(localized) || HasNonAscii(english) || !_known.Add(localized)) return;
        if (!_byFirst.TryGetValue(localized[0], out var list)) _byFirst[localized[0]] = list = new();
        int at = list.FindIndex(p => p.from.Length < localized.Length);
        list.Insert(at < 0 ? list.Count : at, (localized, english));
    }

    /// <summary>The text with every known localized name replaced by its English key, spaced apart from neighbouring letters.</summary>
    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text) || _byFirst.Count == 0 || !HasNonAscii(text)) return text ?? "";
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            var hit = Match(text, i);
            if (hit == null)
            {
                sb.Append(text[i++]);
                continue;
            }
            var (from, to) = hit.Value;
            if (sb.Length > 0 && char.IsLetterOrDigit(sb[^1])) sb.Append(' ');
            sb.Append(to);
            i += from.Length;
            if (i < text.Length && char.IsLetterOrDigit(text[i])) sb.Append(' ');
        }
        return sb.ToString();
    }

    private (string from, string to)? Match(string text, int i)
    {
        if (!_byFirst.TryGetValue(text[i], out var list)) return null;
        foreach (var p in list)
            if (i + p.from.Length <= text.Length && string.CompareOrdinal(text, i, p.from, 0, p.from.Length) == 0)
                return p;
        return null;
    }

    public static bool HasNonAscii(string s)
    {
        foreach (var ch in s)
            if (ch > 127) return true;
        return false;
    }
}

/// <summary>The map in use: empty until a save is loaded (and in an English game, where nothing differs).</summary>
public static class EnglishNames
{
    public static NameMap Map { get; set; } = new();

    public static string Apply(string? text) => Map.Apply(text);
}
