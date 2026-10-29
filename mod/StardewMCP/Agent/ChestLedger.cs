using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewMCP.Agent;

/// <summary>
/// The chests are shared: every farmer may put things in and take them out (no theft). So the farmers know what the others
/// did, every change is written down: when a chest menu closes, what went in and what came out is compared with the
/// contents it had when it opened (any path counts: chest, menu, the mine's stash, a human's clicks). The changes go to the
/// day log (kind "chest") and to chests.jsonl next to the logs; <see cref="Report"/> lists every chest with what is inside
/// and the recent changes, for chest(action=all) and the morning briefing.
/// </summary>
internal static class ChestLedger
{
    public sealed record Change(int Day, string Time, string Seat, string Farmer, string Action, string Item, int Count, string Location, int X, int Y);

    private sealed record Opened(Chest Chest, Dictionary<string, (string label, int count)> Before, string Location, int X, int Y);

    private const int KeepChanges = 300;
    private const int MaxStacksShown = 15;
    private static readonly List<Change> Recent = new();
    private static readonly PerScreen<Opened?> Open = new();
    private static string? _loadedFrom;

    /// <summary>Once per tick on each screen: notices a chest menu opening and closing.</summary>
    public static void Tick(PlayerSlot seat)
    {
        if (!Game1.hasLoadedGame || Game1.player == null) return;
        var chest = Game1.activeClickableMenu is ItemGrabMenu { context: Chest c } ? c : null;
        var open = Open.Value;
        if (open != null && !ReferenceEquals(open.Chest, chest))
        {
            Record(open, seat);
            Open.Value = null;
        }
        if (chest != null && Open.Value == null)
        {
            var loc = chest.Location ?? Game1.currentLocation;
            Open.Value = new Opened(chest, Contents(chest), loc?.NameOrUniqueName ?? "?", (int)chest.TileLocation.X, (int)chest.TileLocation.Y);
        }
    }

    private static Dictionary<string, (string label, int count)> Contents(Chest chest)
    {
        var d = new Dictionary<string, (string label, int count)>();
        foreach (var it in chest.Items.Where(i => i != null))
        {
            var key = it.QualifiedItemId + "|" + it.Quality;
            d.TryGetValue(key, out var cur);
            d[key] = (Label(it), cur.count + it.Stack);
        }
        return d;
    }

    private static string Label(Item it) => it is Tool || it.Quality == 0 ? it.Name : $"{it.Name} ({AgentTools.QualityName(it.Quality)})";

    private static void Record(Opened open, PlayerSlot seat)
    {
        var after = Contents(open.Chest);
        var changes = new List<Change>();
        int day = AgentLog.FarmDay;
        var time = WorldText.FormatTime(Game1.timeOfDay);
        foreach (var key in open.Before.Keys.Union(after.Keys))
        {
            open.Before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);
            int delta = a.count - b.count;
            if (delta == 0) continue;
            changes.Add(new Change(day, time, seat.Id, Game1.player.Name, delta > 0 ? "put" : "took", a.label ?? b.label, Math.Abs(delta), open.Location, open.X, open.Y));
        }
        if (changes.Count == 0) return;

        Load();
        lock (Recent)
        {
            Recent.AddRange(changes);
            if (Recent.Count > KeepChanges) Recent.RemoveRange(0, Recent.Count - KeepChanges);
        }
        var text = $"{Game1.player.Name} {Describe(changes)} the chest at {open.Location} ({open.X},{open.Y})";
        seat.Runtime.Log.Add("chest", text: text, args: new { location = open.Location, tile = new[] { open.X, open.Y }, changes = changes.Select(c => new { c.Action, c.Item, c.Count }) });
        try
        {
            if (File() is { } file)
                System.IO.File.AppendAllLines(file, changes.Select(c => JsonSerializer.Serialize(c, AgentLog.Json)));
        }
        catch (IOException) { /* the day log still has it */ }
    }

    /// <summary>"put 30 Pumpkin, 2 Wood into / took 5 Coal from"</summary>
    private static string Describe(IEnumerable<Change> changes)
    {
        var put = changes.Where(c => c.Action == "put").Select(c => $"{c.Count} {c.Item}").ToList();
        var took = changes.Where(c => c.Action == "took").Select(c => $"{c.Count} {c.Item}").ToList();
        var parts = new List<string>();
        if (put.Count > 0) parts.Add("put " + string.Join(", ", put) + " into");
        if (took.Count > 0) parts.Add("took " + string.Join(", ", took) + " from");
        return string.Join(" and ", parts);
    }

    private static string? File() => DayStats.Log?.Dir is { } dir ? Path.Combine(dir, "chests.jsonl") : null;

    /// <summary>The last changes from chests.jsonl, once per log folder (the run goes on after a restart).</summary>
    private static void Load()
    {
        var file = File();
        if (file == null || _loadedFrom == file) return;
        _loadedFrom = file;
        lock (Recent)
        {
            Recent.Clear();
            try
            {
                if (!System.IO.File.Exists(file)) return;
                foreach (var line in System.IO.File.ReadLines(file).TakeLast(KeepChanges))
                    if (JsonSerializer.Deserialize<Change>(line, AgentLog.Json) is { } c) Recent.Add(c);
            }
            catch (Exception) { /* a damaged ledger starts over */ }
        }
    }

    /// <summary>Every placed chest (the fridges too) on the farm maps with what it holds, then the recent changes by everyone.</summary>
    public static string Report(int changes = 15)
    {
        Load();
        var sb = new StringBuilder("Shared chests (anyone may put in or take out):");
        int n = 0;
        foreach (var name in new[] { "Farm", "FarmHouse", "Greenhouse" }.Concat(Game1.getFarm().buildings.Select(b => b.GetIndoorsName()).Where(s => s != null)!).Distinct())
        {
            var loc = Game1.getLocationFromName(name);
            if (loc == null) continue;
            foreach (var (tile, obj) in loc.objects.Pairs)
            {
                if (obj is not Chest { playerChest.Value: true } chest) continue;
                n++;
                var inside = Contents(chest).Values.OrderByDescending(v => v.count).Select(v => $"{v.count} {v.label}").ToList();
                var kind = chest.fridge.Value ? "fridge" : "chest";
                var shown = inside.Count == 0 ? "empty" : string.Join(", ", inside.Take(MaxStacksShown)) + (inside.Count > MaxStacksShown ? $" and {inside.Count - MaxStacksShown} more" : "");
                sb.Append($"\n- {kind} at {loc.NameOrUniqueName} ({(int)tile.X},{(int)tile.Y}): {shown}");
            }
        }
        if (n == 0) sb.Append(" none placed yet (craft a Chest: 50 Wood).");
        List<Change> last;
        lock (Recent) last = Recent.TakeLast(changes).ToList();
        if (last.Count > 0)
        {
            sb.Append("\nRecent chest changes:");
            foreach (var g in last.GroupBy(c => (c.Day, c.Time, c.Farmer, c.Location, c.X, c.Y)))
                sb.Append($"\n- day {g.Key.Day} {g.Key.Time}: {g.Key.Farmer} {Describe(g)} the chest at {g.Key.Location} ({g.Key.X},{g.Key.Y})");
        }
        return sb.ToString();
    }

    /// <summary>The changes others made to this chest since this farmer last touched it, for the chest tool's result; "" when none.</summary>
    public static string OthersChanged(string location, int x, int y, string me)
    {
        Load();
        List<Change> mine;
        lock (Recent) mine = Recent.Where(c => c.Location == location && c.X == x && c.Y == y).ToList();
        int lastOwn = mine.FindLastIndex(c => c.Seat == me);
        var others = mine.Skip(lastOwn + 1).Where(c => c.Seat != me).ToList();
        if (others.Count == 0) return "";
        return " Since you last used it: " + string.Join("; ", others.GroupBy(c => (c.Day, c.Time, c.Farmer))
            .Select(g => $"{g.Key.Farmer} {Describe(g).Replace(" into", "").Replace(" from", "")} (day {g.Key.Day} {g.Key.Time})")) + ".";
    }
}
