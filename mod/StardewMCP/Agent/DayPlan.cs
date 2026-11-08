using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP.Agent;

/// <summary>
/// A farmer's checklist for one farm day, written with the todo tool. Day 58: AI1 planned five jobs in a morning think, did
/// one, then chased Coal for an hour; after the 12:00 and 16:40 session breaks the think was gone (the handoff only had the
/// plan file's "Today", written the night before) and it cut weeds for two hours. The open items now follow every action
/// result and the handoff, so the plan outlives one job and a context reset.
/// </summary>
public sealed class DayPlan
{
    public const int MaxItems = 8;
    public const int MaxChars = 120;

    private readonly List<(string Text, bool Done)> _items = new();

    public int Day { get; private set; } = -1;

    public IReadOnlyList<(string Text, bool Done)> Items => _items;

    /// <summary>A checklist exists for this day.</summary>
    public bool Active(int day) => Day == day && _items.Count > 0;

    private static string Clean(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim().TrimStart('-', '*', ' ').Trim();
        return s.Length > MaxChars ? s.Substring(0, MaxChars).TrimEnd() + "…" : s;
    }

    /// <summary>Replaces today's checklist.</summary>
    public void Set(int day, IEnumerable<string> items)
    {
        Day = day;
        _items.Clear();
        foreach (var t in items.Select(Clean).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems))
            _items.Add((t, false));
    }

    /// <summary>Adds one job at the end (starting a new list on a new day). False when the list is full or the job is there.</summary>
    public bool Add(int day, string item)
    {
        if (Day != day) { Day = day; _items.Clear(); }
        var t = Clean(item);
        if (t.Length == 0 || _items.Count >= MaxItems || _items.Any(i => i.Text.Equals(t, StringComparison.OrdinalIgnoreCase))) return false;
        _items.Add((t, false));
        return true;
    }

    /// <summary>
    /// Ticks off a job by its number (1 = first) or its words: the exact text, else the first open job that contains the words or
    /// is contained in them. Returns the job's text, or null when nothing matched.
    /// </summary>
    public string? MarkDone(int day, string what)
    {
        if (!Active(day)) return null;
        what = Clean(what);
        if (what.Length == 0) return null;
        int index = -1;
        if (int.TryParse(what, out var n) && n >= 1 && n <= _items.Count) index = n - 1;
        if (index < 0) index = _items.FindIndex(i => i.Text.Equals(what, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = _items.FindIndex(i => !i.Done && (i.Text.Contains(what, StringComparison.OrdinalIgnoreCase) || what.Contains(i.Text, StringComparison.OrdinalIgnoreCase)));
        if (index < 0) return null;
        _items[index] = (_items[index].Text, true);
        return _items[index].Text;
    }

    /// <summary>"1. [x] water the crops" lines, for the todo result and the handoff.</summary>
    public string Checklist() =>
        string.Join("\n", _items.Select((i, k) => $"{k + 1}. [{(i.Done ? "x" : " ")}] {i.Text}"));

    /// <summary>
    /// The line under every action result: the next open job and the rest, "" with no checklist today. Once everything is done
    /// it says so, so the farmer plans the rest of the day instead of drifting.
    /// </summary>
    public string Line(int day)
    {
        if (!Active(day)) return "";
        var open = _items.Where(i => !i.Done).Select(i => i.Text).ToList();
        int done = _items.Count - open.Count;
        if (open.Count == 0)
            return $"Today's plan: all {_items.Count} done. Add the next jobs with todo(add=...), or write the journal and go_to_bed if it is evening.";
        var rest = open.Count > 1 ? $" Then: {string.Join("; ", open.Skip(1))}." : "";
        return $"Today's plan ({done}/{_items.Count} done). Next: {open[0]}.{rest} When a job is finished: todo(done=\"...\").";
    }
}

/// <summary>Each seat's <see cref="DayPlan"/>, by seat id.</summary>
public static class DayPlans
{
    private static readonly Dictionary<string, DayPlan> Seats = new(StringComparer.OrdinalIgnoreCase);

    public static DayPlan For(string seat)
    {
        lock (Seats)
            return Seats.TryGetValue(seat, out var p) ? p : Seats[seat] = new DayPlan();
    }
}
