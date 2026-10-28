using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

// Community Center bundles and Museum donations (#13).
//
// The bundle page is driven through the game's own JunimoNoteMenu by clicks (the game deposits the item, pays the
// reward, plays the room-complete scene); success is read back from Game1.netWorldState.Value.Bundles, never from
// the UI. Members of JunimoNoteMenu, Bundle and LibraryMuseum that this code could not check against the decompiled
// game are reached through LateBound: a missing member becomes a clear "tool failed" line instead of a crash.
// BundleReader, BundleLedger and LateBound are written to be reused by the Joja alternative (#26).

/// <summary>A game member this mod looks up by name at run time was not there (the game version differs).</summary>
internal sealed class LateBoundException : Exception
{
    public LateBoundException(string message) : base(message) { }
}

/// <summary>Reads, writes and calls game members by name, for the few ones that could not be checked at compile time.</summary>
internal static class LateBound
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Read a field or property by name (any visibility, base classes included); false when there is none.</summary>
    public static bool TryRead(object? target, Type type, string name, out object? value)
    {
        value = null;
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Any);
            if (f != null && (f.IsStatic || target != null))
            {
                value = f.GetValue(f.IsStatic ? null : target);
                return true;
            }
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod is { } getter && (getter.IsStatic || target != null))
            {
                value = getter.Invoke(getter.IsStatic ? null : target, null);
                return true;
            }
        }
        return false;
    }

    /// <summary>The member's value, or null when it is missing or null.</summary>
    public static object? TryGet(object? target, string name) =>
        target != null && TryRead(target, target.GetType(), name, out var v) ? v : null;

    public static object? TryGetStatic(Type type, string name) =>
        TryRead(null, type, name, out var v) ? v : null;

    /// <summary>The member's value (may be null); throws <see cref="LateBoundException"/> when the member does not exist.</summary>
    public static object? Require(object target, string name)
    {
        if (!TryRead(target, target.GetType(), name, out var v))
            throw new LateBoundException($"{target.GetType().Name} has no member \"{name}\" (the game version may differ from the one this mod was written for)");
        return v;
    }

    /// <summary>Write a field or property by name; false when there is none or it can't be written.</summary>
    public static bool TrySet(object? target, string name, object? value)
    {
        if (target == null) return false;
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Any);
            if (f != null && !f.IsStatic && !f.IsInitOnly)
            {
                f.SetValue(target, value);
                return true;
            }
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0 && p.SetMethod is { IsStatic: false } setter)
            {
                setter.Invoke(target, new[] { value });
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Call a method by name (any visibility, static or instance, ignoring case; missing optional arguments are filled in).
    /// False when no overload takes these arguments; a failure inside the game method is rethrown as <see cref="LateBoundException"/>.
    /// </summary>
    public static bool TryCall(object? target, Type type, string name, out object? result, params object?[] args)
    {
        result = null;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var m in t.GetMethods(Any))
            {
                if (!m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.IsGenericMethodDefinition) continue;
                if (!m.IsStatic && target == null) continue;
                var ps = m.GetParameters();
                if (args.Length > ps.Length) continue;

                var full = new object?[ps.Length];
                bool fits = true;
                for (int i = 0; i < ps.Length && fits; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt.IsByRef) { fits = false; break; }
                    if (i < args.Length)
                    {
                        fits = args[i] == null ? !pt.IsValueType || Nullable.GetUnderlyingType(pt) != null : pt.IsInstanceOfType(args[i]);
                        full[i] = args[i];
                    }
                    else if (ps[i].HasDefaultValue)
                        full[i] = ps[i].DefaultValue;
                    else
                        fits = false;
                }
                if (!fits) continue;

                try
                {
                    result = m.Invoke(m.IsStatic ? null : target, full);
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    throw new LateBoundException($"{type.Name}.{m.Name} failed: {(ex.InnerException ?? ex).Message}");
                }
            }
        }
        return false;
    }

    public static object? Call(object target, string name, params object?[] args)
    {
        if (TryCall(target, target.GetType(), name, out var r, args)) return r;
        throw new LateBoundException($"{target.GetType().Name} has no method \"{name}\" taking {args.Length} argument(s) of these types (the game version may differ from the one this mod was written for)");
    }
}

/// <summary>What one bundle ingredient asks for. <see cref="Id"/> is the bundle data's own id: -1 gold, below -1 an item category, else an object id.</summary>
internal sealed record BundleNeed(int Slot, string Id, int Count, int Quality, bool Done);

/// <summary>One bundle, parsed from <c>BundleData</c> and the donated flags in <c>Bundles</c>.</summary>
internal sealed class BundleInfo
{
    public int Index;
    public string Room = "";
    /// <summary>Position of <see cref="Room"/> in <see cref="BundleReader.Rooms"/>, or -1 for a room this mod does not drive (the Abandoned Joja Mart).</summary>
    public int Area = -1;
    public string Name = "";
    public string Reward = "";
    /// <summary>How many of <see cref="Needs"/> must be filled (some bundles take 4 of 6).</summary>
    public int Required;
    public List<BundleNeed> Needs = new();

    public int Filled => Needs.Count(n => n.Done);
    public bool Complete => Filled >= Required;
}

/// <summary>
/// Reads the Community Center bundles from the world state and describes them. Shared with the Joja alternative (#26),
/// which closes the same bundles: <see cref="ClosedReason"/> tells the two routes apart.
/// </summary>
internal static class BundleReader
{
    /// <summary>The six rooms in the order the game numbers their areas (0-5).</summary>
    public static readonly string[] Rooms = { "Pantry", "Crafts Room", "Fish Tank", "Boiler Room", "Vault", "Bulletin Board" };

    private static readonly Dictionary<int, string> Categories = new()
    {
        [-2] = "any Gem", [-4] = "any Fish", [-5] = "any Egg", [-6] = "any Milk", [-7] = "any Cooked dish", [-12] = "any Mineral",
        [-14] = "any Meat", [-15] = "any Metal", [-16] = "any Building material", [-18] = "any Animal product",
        [-19] = "any Fertilizer", [-20] = "any Junk", [-21] = "any Bait", [-22] = "any Tackle", [-25] = "any Ingredient",
        [-26] = "any Artisan good", [-27] = "any Syrup", [-28] = "any Monster loot", [-74] = "any Seed", [-75] = "any Vegetable",
        [-79] = "any Fruit", [-80] = "any Flower", [-81] = "any Forage"
    };

    /// <summary>The player's own mail flag, or the host's (world-wide unlocks such as the Joja membership live on the host).</summary>
    public static bool HasMail(Farmer who, string id) =>
        who.mailReceived.Contains(id) || Game1.MasterPlayer.mailReceived.Contains(id);

    /// <summary>
    /// This farmer can read the Junimo script. Per farmer, not the host's: the game's bundle page ignores clicks from a
    /// farmer the Wizard has not taught yet, even when the host can read it.
    /// </summary>
    public static bool CanRead(Farmer who) => who.hasOrWillReceiveMail("canReadJunimoText");

    /// <summary>The membership is bought (#26): received, or already queued for tomorrow's mail on the day it was bought.</summary>
    public static bool IsJoja(Farmer who) => HasMail(who, "JojaMember") || JojaRoute.Pending(who, "JojaMember");

    /// <summary>Why the bundles can't be used right now, or null when they can.</summary>
    public static string? ClosedReason(Farmer who)
    {
        if (IsJoja(who))
            return "This farm took a Joja membership, so the Community Center bundles are closed for good.";
        if (Game1.currentLocation is CommunityCenter || HasMail(who, "ccDoorUnlock") || HasMail(who, "canReadJunimoText") || HasMail(who, "ccIsComplete"))
            return null;
        return "The Community Center is not open yet. " + NextStep(who);
    }

    /// <summary>
    /// The next step of the Community Center story, from the mail flags the game sets along it: Lewis's scene (entering Town
    /// from the Bus Stop, no rain, spring 5+, 8:00-13:00) unlocks the door (ccDoorUnlock), the Junimo note inside brings the Wizard's letter (wizardJunimoNote), his tower teaches the
    /// script (canReadJunimoText), then the bundles until every room is restored (ccIsComplete).
    /// </summary>
    public static string NextStep(Farmer who)
    {
        if (IsJoja(who)) return "none: the farm chose Joja.";
        if (HasMail(who, "ccIsComplete")) return "done: the Community Center is restored.";
        if (!HasMail(who, "ccDoorUnlock"))
        {
            const string how = "enter Pelican Town (Town) from the Bus Stop on a day without rain, spring 5 or later, between 8:00 and 13:00: " +
                               "Mayor Lewis's cutscene shows you the old Community Center and unlocks its door. travel_to Town from the Farm goes through the Bus Stop; if you are already in Town, walk out to the Bus Stop and back in.";
            bool today = Game1.Date.TotalDays >= 4 && !Game1.isRaining && Game1.timeOfDay < 1300;
            bool laterToday = today && Game1.timeOfDay < 800;
            return today
                ? $"TODAY the conditions are met: {how} {(laterToday ? "Be in Town after 8:00." : "Go now, before 13:00.")}"
                : how + (Game1.Date.TotalDays < 4 ? " It is too early in the year yet." : Game1.isRaining ? " Not today: it is raining." : " Too late for today; try tomorrow morning.");
        }
        // Past this point the door is open: say so, because a model's older notes may still plan the Bus Stop trip.
        const string open = "The Community Center door is ALREADY UNLOCKED (Lewis's scene is done; ignore older notes about going to the Bus Stop for it). Next: ";
        if (!CanRead(who) && !NoteRead(who))
            return open + "read the glowing Junimo note inside the Community Center: call community_center (it walks there and reads it); the Wizard writes to you the next day.";
        if (!CanRead(who))
            return open + (who.mailForTomorrow.Contains("wizardJunimoNote")
                ? "the Junimo note is read: the Wizard's letter comes tomorrow morning, then community_center takes you to his tower."
                : "visit the Wizard yourself: call community_center (it goes to his tower, west of Cindersap Forest): he teaches you to read the Junimo notes. Each farmer must learn it; until then the bundles take nothing from you.");
        return open + "you can read the Junimo notes. Fill the bundles: bundles lists what the open rooms still need and which of your items fit; community_center (or donate_bundle) walks to the room's note and hands them in. " +
               "Plan crops, fish and forage by season, because many items only exist in one season.";
    }

    /// <summary>The first Junimo note was read: the Wizard's letter is queued, waiting in the mailbox or already opened.</summary>
    public static bool NoteRead(Farmer who) =>
        who.mailForTomorrow.Contains("wizardJunimoNote") || who.mailbox.Contains("wizardJunimoNote") || who.mailReceived.Contains("wizardJunimoNote");

    public static CommunityCenter? Center => Game1.getLocationFromName("CommunityCenter") as CommunityCenter;

    /// <summary>The room's Junimo note is on the wall: its bundles take donations now (rooms open one by one as bundles complete).</summary>
    public static bool RoomOpen(int area) => area >= 0 && Center is { } cc && cc.shouldNoteAppearInArea(area);

    /// <summary>The tile of the room's Junimo note.</summary>
    public static Point NoteTile(int area) => Center is { } cc ? NoteTile(cc, area) : Point.Zero;

    /// <summary>CommunityCenter.getNotePosition is not public.</summary>
    public static Point NoteTile(CommunityCenter cc, int area) =>
        typeof(CommunityCenter).GetMethod("getNotePosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
            ?.Invoke(cc, new object[] { area }) is Point p ? p : Point.Zero;

    /// <summary>Bundles taking donations right now: unfinished, in a room whose note is up.</summary>
    public static List<BundleInfo> Active(IEnumerable<BundleInfo> all) => all.Where(b => !b.Complete && RoomOpen(b.Area)).ToList();

    /// <summary>"Parsnip → Spring Crops (Crafts Room)" for each inventory stack that fills an active bundle slot now.</summary>
    public static List<string> Donatable(IReadOnlyList<BundleInfo> all, Farmer who)
    {
        var lines = new List<string>();
        foreach (var b in Active(all))
            foreach (var n in b.Needs.Where(n => !n.Done))
            {
                bool ok = n.Id == "-1" ? who.Money >= n.Count : who.Items.Any(i => i != null && Fits(i, n) && i.Stack >= n.Count);
                if (ok) lines.Add($"{Describe(n)} → {b.Name} ({b.Room})");
            }
        return lines;
    }

    /// <summary>Every bundle, rooms in game order. Empty when the world has no bundle data.</summary>
    public static List<BundleInfo> Read()
    {
        var world = Game1.netWorldState.Value;
        var list = new List<BundleInfo>();
        foreach (var kv in world.BundleData)
        {
            var keyParts = kv.Key.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out var index)) continue;
            var f = kv.Value.Split('/');
            if (f.Length < 3) continue;

            var info = new BundleInfo
            {
                Index = index,
                Room = keyParts[0],
                Area = Array.IndexOf(Rooms, keyParts[0]),
                Name = f[0],
                Reward = f[1]
            };
            var flags = Flags(index);
            var t = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int slot = i / 3;
                int.TryParse(t[i + 1], out var count);
                int.TryParse(t[i + 2], out var quality);
                info.Needs.Add(new BundleNeed(slot, t[i], Math.Max(1, count), quality, slot < flags.Length && flags[slot]));
            }
            info.Required = f.Length > 4 && int.TryParse(f[4], out var req) && req > 0 ? Math.Min(req, info.Needs.Count) : info.Needs.Count;
            list.Add(info);
        }
        return list.OrderBy(b => b.Area < 0 ? 99 : b.Area).ThenBy(b => b.Index).ToList();
    }

    /// <summary>The donated flags of one bundle (one per ingredient); empty when the bundle is unknown.</summary>
    public static bool[] Flags(int index) =>
        Game1.netWorldState.Value.Bundles.TryGetValue(index, out var f) && f != null ? f : Array.Empty<bool>();

    /// <summary>True when this item can fill the ingredient (item or category, quality at least the asked one). The stack size is checked separately.</summary>
    public static bool Fits(Item item, BundleNeed need)
    {
        if (need.Done || need.Id == "-1" || item.Quality < need.Quality) return false;
        if (need.Id.StartsWith("(")) return item.QualifiedItemId == need.Id;
        if (int.TryParse(need.Id, out var cat) && cat < -1) return item.Category == cat;
        return item.QualifiedItemId == "(O)" + need.Id;
    }

    public static bool BundleMatches(BundleInfo b, string want)
    {
        if (want.Length == 0) return true;
        if (int.TryParse(want, out var n) && n == b.Index) return true;
        return b.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(want, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RoomMatches(string room, string want) =>
        room.Equals(want, StringComparison.OrdinalIgnoreCase) || room.Contains(want, StringComparison.OrdinalIgnoreCase);

    public static string ItemName(string id)
    {
        if (id == "-1") return "Gold";
        if (int.TryParse(id, out var n) && n < -1) return Categories.TryGetValue(n, out var cat) ? cat : $"category {n}";
        return ItemRegistry.GetData(id.StartsWith("(") ? id : "(O)" + id)?.DisplayName ?? id;
    }

    public static string Describe(BundleNeed n)
    {
        if (n.Id == "-1") return $"{n.Count}g";
        var quality = n.Quality switch { 1 => " (silver or better)", 2 => " (gold or better)", 4 => " (iridium)", _ => "" };
        return $"{(n.Count > 1 ? n.Count + " x " : "")}{ItemName(n.Id)}{quality}";
    }

    /// <summary>"O 465 20" in bundle data: type letter, item id, count.</summary>
    public static string RewardText(string reward)
    {
        var p = reward.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return reward;
        string? prefix = p[0] switch { "O" or "R" => "(O)", "BO" => "(BC)", "F" => "(F)", "W" => "(W)", "H" => "(H)", "B" => "(B)", _ => null };
        var count = p.Length > 2 && int.TryParse(p[2], out var n) && n > 1 ? n + " x " : "";
        var name = prefix == null ? null : ItemRegistry.GetData(prefix + p[1])?.DisplayName;
        return count + (name ?? reward);
    }

    /// <summary>"(you have it)" when one inventory stack covers the ingredient, "(you hold 3 of 5)" when it is short, else "".</summary>
    private static string Carry(Farmer who, BundleNeed n)
    {
        if (n.Id == "-1") return who.Money >= n.Count ? " (you can pay it)" : "";
        int best = who.Items.Where(i => i != null && Fits(i, n)).Select(i => i.Stack).DefaultIfEmpty(0).Max();
        if (best >= n.Count) return " (you have it)";
        return best > 0 ? $" (you hold {best} of {n.Count})" : "";
    }

    /// <summary>The text of the bundles tool: rooms, bundles, what is donated (and by whom, when this mod saw it) and what is missing.</summary>
    /// <param name="every">false: only the rooms whose note is up and their unfinished bundles (what to gather now).</param>
    public static string Report(IReadOnlyList<BundleInfo> all, Farmer who, string? room, bool every = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Community Center: {all.Count(b => b.Complete)}/{all.Count} bundles complete. community_center (or donate_bundle(item)) walks to the room's note and donates.");
        if (!CanRead(who))
            sb.AppendLine("(You can't read Junimo script yet: the Wizard teaches each farmer himself, and no bundle takes items from you before that. The data below is exact anyway.)");
        var ready = Donatable(all, who);
        if (ready.Count > 0 && CanRead(who))
            sb.AppendLine("You can donate NOW: " + string.Join("; ", ready) + ". Call community_center.");
        if (!every && room == null)
        {
            var locked = all.Where(b => b.Area >= 0 && !b.Complete && !RoomOpen(b.Area)).Select(b => b.Room).Distinct().ToList();
            if (locked.Count > 0)
                sb.AppendLine($"Rooms still sealed (the Community Center itself is open; these rooms' notes appear as more bundles are completed): {string.Join(", ", locked)}. Below: only the rooms with a note up; bundles(all=true) lists every room.");
        }
        foreach (var g in all.GroupBy(b => b.Room))
        {
            if (room != null && !RoomMatches(g.Key, room)) continue;
            int area = g.First().Area;
            if (!every && room == null && (area < 0 || !RoomOpen(area))) continue;
            var note = area >= 0 ? NoteTile(area) : Point.Zero;
            sb.AppendLine($"== {g.Key}: {g.Count(b => b.Complete)}/{g.Count()} bundles complete" + (note != Point.Zero ? $" (note at {note.X},{note.Y})" : ""));
            foreach (var b in g)
            {
                if (b.Complete)
                {
                    if (!every) continue;
                    sb.AppendLine($"  [{b.Index}] {b.Name} COMPLETE → {RewardText(b.Reward)}");
                    continue;
                }
                var done = b.Needs.Where(n => n.Done)
                    .Select(n => Describe(n) + (BundleLedger.Who(b.Index, n.Slot) is { } w ? $" (by {w})" : ""));
                var open = b.Needs.Where(n => !n.Done).ToList();
                int remaining = b.Required - b.Filled;
                var lead = remaining < open.Count ? $"needs {remaining} more of: " : "needs: ";
                var doneText = done.Any() ? " | done: " + string.Join(", ", done) : "";
                sb.AppendLine($"  [{b.Index}] {b.Name} {b.Filled}/{b.Required} → {RewardText(b.Reward)}{doneText} | {lead}{string.Join(", ", open.Select(n => Describe(n) + Carry(who, n)))}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Who donated what. The game keeps only "slot filled" flags, so this remembers the donations made through
/// donate_bundle in memory (lost on restart); a donation made by hand is not listed.
/// </summary>
internal static class BundleLedger
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(int bundle, int slot), (string who, int day)> Donors = new();

    public static void Record(int bundle, int slot, string who, int day)
    {
        lock (Gate) Donors[(bundle, slot)] = (who, day);
    }

    public static string? Who(int bundle, int slot)
    {
        lock (Gate) return Donors.TryGetValue((bundle, slot), out var d) ? $"{d.who} day {d.day}" : null;
    }
}

public static partial class AgentTools
{
    private static List<ToolDef> CommunityTools() => new()
    {
        new()
        {
            Name = "bundles",
            Description = "Community Center bundles that take donations now: the rooms whose Junimo note is up, each unfinished bundle's missing items (item, quality, count), " +
                          "the reward, and which of your inventory items fit (\"You can donate NOW\"). all=true lists every room, finished bundles too. " +
                          "Game time stays frozen.",
            Schema = Obj(new
            {
                room = Str("Optional: only this room (Pantry, Crafts Room, Fish Tank, Boiler Room, Vault, Bulletin Board)"),
                all = new { type = "boolean", description = "Optional: every room and bundle, also locked rooms and finished bundles" }
            }),
            Instant = true,
            Run = (a, c) => CommunityGuard(BundlesRun(a, c), c)
        },
        new()
        {
            Name = "community_center",
            Description = "Do the next Community Center step yourself: read the first Junimo note (walks into the Community Center to it), visit the Wizard once his letter came, " +
                          "then hand in every item you carry that an open bundle needs (walks to each room's note). With nothing to donate it lists what the open bundles still need.",
            Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
            Run = (a, c) => CommunityGuard(CommunityCenterRun(c), c)
        },
        new()
        {
            Name = "donate_bundle",
            Description = "Donate an item from your inventory to the first open Community Center bundle that needs it: it walks into the Community Center to the room's note. " +
                          "It uses the game's own bundle page, so rewards and room-complete scenes happen as for a player. " +
                          "item \"all\" donates everything you carry that fits; \"gold\" pays a Vault bundle. bundle (name or number from bundles) picks the bundle if several take the item. " +
                          "Reports the bundle's progress and any bundle or room it completes.",
            Schema = Obj(new
            {
                item = Str("Item name from your inventory, \"all\", or \"gold\" for a Vault bundle"),
                bundle = Str("Optional: bundle name or number (see bundles)"),
                reason = Str(ReasonDesc)
            }, "item", "reason"),
            Run = (a, c) => CommunityGuard(DonateBundleRun(a, c), c)
        },
        new()
        {
            Name = "museum",
            Description = "The Museum (ArchaeologyHouse in Town, Gunther). It takes one of each artifact, mineral and gem; Gunther gives rewards as the collection grows. " +
                          "action list (default): the items you carry that the Museum still needs. " +
                          "donate: walks you to the Museum and puts an item (default \"all\": every piece it still needs) on display. " +
                          "rewards: talk to Gunther and take the rewards you have earned. list keeps game time frozen.",
            Schema = Obj(new
            {
                action = new { type = "string", @enum = new[] { "list", "donate", "rewards" }, description = "list (default), donate or rewards" },
                item = Str("donate: item name from your inventory, or \"all\" (default)"),
                reason = Str(ReasonDesc)
            }),
            InstantFor = a => S(a, "action").Trim().ToLowerInvariant() is "" or "list",
            Run = (a, c) => CommunityGuard(MuseumRun(a, c), c)
        }
    };

    /// <summary>Runs a tool body and turns a missing game member (<see cref="LateBoundException"/>) into a plain tool failure.</summary>
    private static IEnumerator CommunityGuard(IEnumerator inner, CallContext c)
    {
        try
        {
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (LateBoundException ex) { c.Fail(ex.Message); more = false; }
                if (!more) yield break;
                yield return inner.Current;
            }
        }
        finally
        {
            (inner as IDisposable)?.Dispose();
        }
    }

    // ------------------------------------------------------------------ bundles

    private static IEnumerator BundlesRun(JsonElement a, CallContext c)
    {
        var closed = BundleReader.ClosedReason(P);
        if (closed != null) { c.Fail(closed); yield break; }
        var all = BundleReader.Read();
        if (all.Count == 0) { c.Fail("The game has no bundle data yet."); yield break; }
        var room = S(a, "room").Trim();
        bool every = Has(a, "all") && a.GetProperty("all").ValueKind == JsonValueKind.True;
        if (room.Equals("all", StringComparison.OrdinalIgnoreCase) || room.Equals("every", StringComparison.OrdinalIgnoreCase)) { room = ""; every = true; }
        if (room.Length > 0 && !all.Any(b => BundleReader.RoomMatches(b.Room, room)))
        {
            c.Fail($"No room \"{room}\". Rooms: {string.Join(", ", all.Select(b => b.Room).Distinct())}");
            yield break;
        }
        c.Result = BundleReader.Report(all, P, room.Length > 0 ? room : null, every);
    }

    /// <summary>The next Community Center step: the first Junimo note, the Wizard, then every donation the farmer can make now.</summary>
    private static IEnumerator CommunityCenterRun(CallContext c)
    {
        if (BundleReader.IsJoja(P)) { c.Fail("This farm took a Joja membership: the Community Center bundles are closed for good."); yield break; }
        if (BundleReader.HasMail(P, "ccIsComplete")) { c.Result = "The Community Center is already restored."; yield break; }
        if (!BundleReader.HasMail(P, "ccDoorUnlock")) { c.Fail(Reasons.NotReady, "The Community Center is still locked: " + BundleReader.NextStep(P)); yield break; }

        if (!BundleReader.CanRead(P))
        {
            if (BundleReader.NoteRead(P))
            {
                if (P.mailForTomorrow.Contains("wizardJunimoNote"))
                {
                    c.Fail(Reasons.NotReady, "You already read the Junimo note. The Wizard's letter arrives tomorrow morning; call community_center again after it came. Carry on with farm work today.");
                    yield break;
                }
                var go = new CallContext();
                yield return TravelTo("WizardHouse", go);
                if (!go.Ok) { c.Fail(go.Reason ?? Reasons.NoPath, $"Couldn't get to the Wizard's tower: {go.Result}"); yield break; }
                for (int i = 0; i < 600 && (Game1.eventUp || Game1.activeClickableMenu != null || Game1.isWarping); i++)
                {
                    if (Game1.activeClickableMenu is DialogueBox db && i % 30 == 0) db.receiveLeftClick(0, 0);
                    yield return null;
                }
                c.Result = BundleReader.CanRead(P)
                    ? "The Wizard taught you to read the Junimo script: the bundles take donations now. bundles shows what the open rooms need."
                    : "You are in the Wizard's tower, but he has not taught you yet. talk_to Wizard to hear him out.";
                yield break;
            }

            var sub = new CallContext();
            yield return TravelTo("CommunityCenter", sub);
            if (!sub.Ok) { c.Fail(sub.Reason ?? Reasons.NoPath, $"Couldn't get into the Community Center: {sub.Result}"); yield break; }
            var cc = (CommunityCenter)Loc;
            int area = Enumerable.Range(0, 6).FirstOrDefault(a => cc.isJunimoNoteAtArea(a), 1);
            var tile = BundleReader.NoteTile(cc, area);
            if (tile == Point.Zero) { c.Fail("Couldn't find the Junimo note's position in this game version: look for it and interact with its x and y."); yield break; }
            var walk = new CallContext();
            yield return WalkAdjacent(tile, walk);
            if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Couldn't reach the Junimo note at ({tile.X},{tile.Y}): {walk.Result}"); yield break; }
            PressAction(tile);
            yield return Ticks(30);
            var done = new CallContext();
            yield return FinishMessages(done);
            if (BundleReader.NoteRead(P))
            {
                Rt.Stats.Count("junimo_note_read");
                c.Result = $"You read the glowing Junimo note at ({tile.X},{tile.Y}): strange writing you can't understand. The Wizard will write to you tomorrow; then call community_center again.";
            }
            else
                c.Fail($"Pressed the Junimo note at ({tile.X},{tile.Y}) but nothing happened. {done.Result}".Trim());
            yield break;
        }

        var all = BundleReader.Read();
        // Bundle items wait in the chests too (they are kept out of the shipping bin for this): bring them along first.
        var fetched = new List<string>();
        yield return FetchBundleItems(all, fetched);
        var prefix = fetched.Count > 0 ? string.Join("\n", fetched) + "\n" : "";
        if (BundleReader.Donatable(all, P).Count == 0)
        {
            c.Result = prefix + "Neither your pack nor your chests hold anything an open bundle needs right now. Gather these, then call community_center again:\n" + BundleReader.Report(all, P, null);
            yield break;
        }
        yield return DonateAll(c);
        if (prefix.Length > 0) c.Result = prefix + c.Result;
    }

    /// <summary>
    /// Takes out of this farmer's chests (farm and house, its own or nobody's) the stacks that fill an open bundle slot the pack
    /// can't fill. Days 44-58 of the second 9B run: 27 calls said "you carry nothing an open bundle needs" while chests held some.
    /// </summary>
    private static IEnumerator FetchBundleItems(IReadOnlyList<BundleInfo> all, List<string> notes)
    {
        var me = Rt.Log.Player;
        var plan = new List<(string map, Point tile, string name, int quality, int count)>();
        var claimed = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        var chests = new List<(string map, Point tile, StardewValley.Objects.Chest chest)>();
        foreach (var loc in new[] { Game1.getFarm(), Utility.getHomeOfFarmer(P) as GameLocation })
        {
            if (loc == null) continue;
            foreach (var (t, obj) in loc.objects.Pairs)
                if (obj is StardewValley.Objects.Chest { playerChest.Value: true, fridge.Value: false } ch && Ownership.OwnerOf(ch) is var o && (o == null || o == me))
                    chests.Add((loc.Name, new Point((int)t.X, (int)t.Y), ch));
        }
        if (chests.Count == 0) yield break;
        foreach (var b in BundleReader.Active(all))
            foreach (var n in b.Needs.Where(n => !n.Done && n.Id != "-1"))
            {
                if (P.Items.Any(i => i != null && BundleReader.Fits(i, n) && i.Stack >= n.Count)) continue;
                foreach (var (map, tile, chest) in chests)
                {
                    var it = chest.Items.FirstOrDefault(i => i != null && BundleReader.Fits(i, n) && i.Stack - claimed.GetValueOrDefault(i) >= n.Count);
                    if (it == null) continue;
                    claimed[it] = claimed.GetValueOrDefault(it) + n.Count;
                    plan.Add((map, tile, it.DisplayName, it.Quality, n.Count));
                    break;
                }
            }
        // One map at a time, this one first, so the house and the farm are each visited once.
        foreach (var (map, tile, name, quality, count) in plan.Take(8).OrderBy(p => p.map == Loc.Name ? 0 : 1).ThenBy(p => p.map).ThenBy(p => p.tile.X).ThenBy(p => p.tile.Y).ToList())
        {
            if (Loc.Name != map)
            {
                var trip = new CallContext();
                yield return TravelTo(map, trip);
                if (!trip.Ok) { notes.Add($"Couldn't get to your chest in {map} for {name}: {trip.Result}"); yield break; }
            }
            var sub = new CallContext();
            yield return ChestTool(ArgsOf(new { x = tile.X, y = tile.Y, action = "take", item = name, quality = QualityName(quality), quantity = count, reason = "bundle item for the Community Center" }), sub);
            notes.Add(sub.Ok ? $"For the bundles: {sub.Result.Split('\n')[0]}" : $"Couldn't take {name} from your chest at {map} ({tile.X},{tile.Y}): {sub.Result}");
        }
    }

    /// <summary>Donates every inventory stack (and no gold) that fills an open bundle slot, one donation at a time.</summary>
    private static IEnumerator DonateAll(CallContext c)
    {
        var lines = new List<string>();
        int donated = 0;
        string? failReason = null;
        for (int round = 0; round < 24; round++)
        {
            var all = BundleReader.Read();
            DonationPick? pick = null;
            foreach (var b in BundleReader.Active(all))
            {
                foreach (var n in b.Needs.Where(n => !n.Done && n.Id != "-1"))
                {
                    int slot = Enumerable.Range(0, P.Items.Count).Where(i => P.Items[i] is { } it && BundleReader.Fits(it, n) && it.Stack >= n.Count)
                        .OrderBy(i => P.Items[i].Quality).DefaultIfEmpty(-1).First();
                    if (slot >= 0) { pick = new DonationPick(b, n, slot); break; }
                }
                if (pick != null) break;
            }
            if (pick == null) break;
            var one = new CallContext();
            yield return DonateOne(P.Items[pick.InvSlot].Name, pick.Bundle.Index.ToString(), one);
            lines.Add(one.Result ?? "");
            if (!one.Ok) { failReason = one.Reason; break; }
            donated++;
        }
        if (lines.Count == 0) { c.Fail(Reasons.NoItem, "Nothing you carry fits an open bundle. bundles shows what they need."); yield break; }
        // Nothing went in: a failure, not a result the model reads as done.
        if (donated == 0) { c.Fail(failReason ?? Reasons.Other, string.Join("\n", lines)); yield break; }
        c.Result = string.Join("\n", lines);
    }

    private sealed record DonationPick(BundleInfo Bundle, BundleNeed Need, int InvSlot);

    /// <summary>Inventory slots holding an item called <paramref name="name"/>: exact name matches, else partial ones.</summary>
    private static List<int> StacksNamed(string name)
    {
        name = name.Trim().ToLowerInvariant();
        var exact = new List<int>();
        var loose = new List<int>();
        for (int i = 0; i < P.Items.Count; i++)
        {
            var it = P.Items[i];
            if (it == null) continue;
            var dn = it.DisplayName.ToLowerInvariant();
            var n = it.Name.ToLowerInvariant();
            if (dn == name || n == name) exact.Add(i);
            else if (dn.Contains(name) || n.Contains(name)) loose.Add(i);
        }
        return exact.Count > 0 ? exact : loose;
    }

    /// <summary>The first open bundle slot (rooms in game order) that the named item, or gold, can fill. Lower-quality stacks go first.</summary>
    private static (DonationPick? pick, string? error) PickDonation(List<BundleInfo> all, string want, bool gold, string bundleArg)
    {
        var open = BundleReader.Active(all).Where(b => BundleReader.BundleMatches(b, bundleArg)).ToList();
        if (open.Count == 0)
            return (null, bundleArg.Length > 0 ? $"No bundle in an open room matches \"{bundleArg}\" (its room's note may not be up yet). Use bundles to see the open ones."
                                                : "No room has an unfinished bundle open right now. Use bundles(all=true).");

        var stacks = gold ? new List<int>() : StacksNamed(want);
        if (!gold && stacks.Count == 0)
            return (null, $"No \"{want}\" in your inventory.");

        foreach (var b in open)
        {
            var missing = b.Needs.Where(x => !x.Done).ToList();
            foreach (var n in missing)
            {
                if (gold)
                {
                    if (n.Id == "-1") return (new DonationPick(b, n, -1), null);
                    continue;
                }
                foreach (var slot in stacks.OrderBy(s => P.Items[s].Quality))
                {
                    var stack = P.Items[slot];
                    if (BundleReader.Fits(stack, n) && stack.Stack >= n.Count) return (new DonationPick(b, n, slot), null);
                }
            }
        }

        if (gold) return (null, "No unfinished bundle takes gold.");
        return (null, $"\"{want}\" fits no open bundle slot: either no bundle needs it, its quality is too low, or you hold fewer than the bundle asks. Use bundles to see what is needed.");
    }

    private static IEnumerator DonateBundleRun(JsonElement a, CallContext c)
    {
        var closed = BundleReader.ClosedReason(P);
        if (closed != null) { c.Fail(closed); yield break; }
        if (!BundleReader.CanRead(P))
        {
            c.Fail(Reasons.NotReady, "The bundles take nothing before the Wizard teaches you the Junimo script. Next: " + BundleReader.NextStep(P));
            yield break;
        }
        var want = S(a, "item").Trim();
        if (want.Length == 0) { c.Fail("item is required: an inventory item name, \"all\", or \"gold\"."); yield break; }
        if (want.Equals("all", StringComparison.OrdinalIgnoreCase) || want.Equals("everything", StringComparison.OrdinalIgnoreCase))
            yield return DonateAll(c);
        else
            yield return DonateOne(want, S(a, "bundle").Trim(), c);
    }

    private static IEnumerator DonateOne(string want, string bundleArg, CallContext c)
    {
        var all = BundleReader.Read();
        if (all.Count == 0) { c.Fail("The game has no bundle data yet."); yield break; }
        bool gold = want.Equals("gold", StringComparison.OrdinalIgnoreCase) || want.Equals("money", StringComparison.OrdinalIgnoreCase);

        var (pick, error) = PickDonation(all, want, gold, bundleArg);
        if (pick == null) { c.Fail(error!); yield break; }
        var b = pick.Bundle;
        var need = pick.Need;
        if (gold && P.Money < need.Count) { c.Fail($"{b.Name} costs {need.Count}g and you have {P.Money}g."); yield break; }

        // Walk to the room's note like a player: into the Community Center, up to the note on the wall.
        if (Loc is not CommunityCenter)
        {
            var go = new CallContext();
            yield return TravelTo("CommunityCenter", go);
            if (!go.Ok) { c.Fail(go.Reason ?? Reasons.NoPath, $"Couldn't get into the Community Center: {go.Result}"); yield break; }
        }
        var note = BundleReader.NoteTile(b.Area);
        if (note != Point.Zero && Math.Abs(P.TilePoint.X - note.X) + Math.Abs(P.TilePoint.Y - note.Y) > 1)
            yield return WalkAdjacent(note, new CallContext()); // best effort: the page opens from anywhere in the building
        if (Game1.activeClickableMenu != null || Game1.eventUp)
        {
            c.Fail("Close the open menu or finish the scene first.");
            yield break;
        }
        if (P.Items.ElementAtOrDefault(pick.InvSlot) is { } still && !gold && !BundleReader.Fits(still, need))
        {
            c.Fail($"Your inventory changed on the way; call donate_bundle again.");
            yield break;
        }

        var itemName = gold ? "gold" : P.Items[pick.InvSlot].DisplayName;
        int doneBefore = BundleReader.Flags(b.Index).Count(x => x);
        var before = Snapshot();
        bool success = false;
        bool rewardTaken = false;

        var menu = OpenJunimoMenu(b.Area);
        Game1.activeClickableMenu = menu;
        try
        {
            // The menu ignores clicks while its screen swipe plays.
            for (int i = 0; i < 180 && LateBound.TryGetStatic(typeof(JunimoNoteMenu), "canClick") is false; i++) yield return null;
            yield return Ticks(6);

            Click(menu, FindBundleComponent(menu, b.Index));
            for (int i = 0; i < 90 && LateBound.TryGet(menu, "specificBundlePage") is not true; i++) yield return null;
            if (LateBound.TryGet(menu, "specificBundlePage") is not true)
            {
                c.Fail($"The {b.Name} bundle page did not open.");
                yield break;
            }
            yield return Ticks(6);

            if (gold)
            {
                if (LateBound.TryGet(menu, "purchaseButton") is not ClickableComponent buy)
                {
                    c.Fail($"The {b.Name} page has no purchase button.");
                    yield break;
                }
                Click(menu, buy);
                for (int i = 0; i < 30 && !success; i++)
                {
                    yield return null;
                    success = BundleReader.Flags(b.Index).Count(x => x) > doneBefore;
                }
            }
            else
            {
                var invMenu = LateBound.TryGet(menu, "inventory") as InventoryMenu;
                var slotCc = invMenu?.inventory.FirstOrDefault(s => s.name == pick.InvSlot.ToString());
                if (slotCc == null)
                {
                    c.Fail($"Your {itemName} (slot {pick.InvSlot}) is not shown on the bundle page.");
                    yield break;
                }
                Click(menu, slotCc); // pick the stack up
                yield return Ticks(3);
                if (LateBound.TryGet(menu, "heldItem") == null)
                {
                    c.Fail($"Could not pick up {itemName}.");
                    yield break;
                }

                // Empty slots first; the game matches the item to its ingredient itself, so any free slot works.
                var slots = IngredientSlots(menu).OrderBy(s => LateBound.TryGet(s, "item") == null ? 0 : 1).ToList();
                if (slots.Count == 0)
                {
                    c.Fail($"The {b.Name} page shows no ingredient slots.");
                    yield break;
                }
                foreach (var slot in slots)
                {
                    Click(menu, slot);
                    for (int i = 0; i < 12 && !success; i++)
                    {
                        yield return null;
                        success = BundleReader.Flags(b.Index).Count(x => x) > doneBefore;
                    }
                    if (success || LateBound.TryGet(menu, "heldItem") == null) break;
                }
            }

            if (success && BundleReader.Flags(b.Index).Count(x => x) >= b.Required)
            {
                // The game plays the completion animation, then returns to the room's page.
                for (int i = 0; i < 600 && LateBound.TryGet(menu, "specificBundlePage") is true; i++) yield return null;
                yield return Ticks(40);
                if (Game1.activeClickableMenu == menu && LateBound.TryGet(menu, "presentButton") is ClickableComponent gift && gift.visible)
                {
                    // Best effort: the gift icon on the room's page opens the reward grab menu.
                    Click(menu, gift);
                    for (int i = 0; i < 60 && Game1.activeClickableMenu is not ItemGrabMenu; i++) yield return null;
                    if (Game1.activeClickableMenu is ItemGrabMenu)
                    {
                        var rewardBefore = Snapshot();
                        yield return CommunityTakeAll();
                        rewardTaken = Diff(rewardBefore).Length > 0;
                    }
                }
            }
        }
        finally
        {
            ReturnJunimoHeldItem(menu);
            if (Game1.activeClickableMenu == menu)
            {
                menu.exitThisMenu();
                if (Game1.activeClickableMenu == menu) Game1.exitActiveMenu();
            }
            else if (Game1.activeClickableMenu is ItemGrabMenu rewards && rewards.ItemsToGrabMenu.actualInventory.All(i => i == null))
                Game1.exitActiveMenu();
        }
        if (!c.Ok) yield break;
        yield return Ticks(10);

        var after = BundleReader.Read();
        var now = after.FirstOrDefault(x => x.Index == b.Index) ?? b;
        if (!success)
        {
            var diff = Diff(before);
            c.Fail($"Nothing was donated to {b.Name}: the game did not take {itemName}. {diff}".Trim());
            yield break;
        }

        var filled = now.Needs.Where(n => n.Done && !b.Needs[n.Slot].Done).ToList();
        foreach (var n in filled) BundleLedger.Record(b.Index, n.Slot, P.Name, AgentLog.FarmDay);
        var sb = new StringBuilder();
        sb.Append(gold ? $"Paid {need.Count}g into {b.Name} ({b.Room})" : $"Donated {itemName} to {b.Name} ({b.Room})");
        sb.Append($": {now.Filled}/{now.Required} filled.");
        Rt.Log.Add("bundle_donated", text: $"{P.Name} donated {itemName} to {b.Name}",
            args: new { player = Rt.Log.Player, bundle = b.Name, room = b.Room, item = itemName, filled = now.Filled, required = now.Required });

        if (now.Complete)
        {
            var reward = BundleReader.RewardText(b.Reward);
            sb.Append($" Bundle complete! Reward: {reward}{(rewardTaken ? " (in your inventory now)" : "; if it is not in your inventory, open the Junimo note in this room and click the gift")}.");
            Rt.Log.Add("bundle_completed", text: $"{P.Name} completed the {b.Name} bundle",
                args: new { player = Rt.Log.Player, bundle = b.Name, room = b.Room, reward });
            var roomBundles = after.Where(x => x.Room == b.Room).ToList();
            if (roomBundles.All(x => x.Complete))
            {
                sb.Append($" The {b.Room} is complete!");
                Rt.Log.Add("room_completed", text: $"{P.Name} completed the {b.Room}", args: new { player = Rt.Log.Player, room = b.Room });
            }
            if (after.All(x => x.Complete)) sb.Append(" The whole Community Center is complete!");
        }
        else
        {
            var left = now.Needs.Where(n => !n.Done).Select(BundleReader.Describe).ToList();
            int remaining = now.Required - now.Filled;
            sb.Append(left.Count == 0 ? "" : $" Still needs {(remaining < left.Count ? $"{remaining} more of" : "")}: {string.Join(", ", left)}.");
        }
        var inv = Diff(before);
        if (inv.Length > 0) sb.Append(' ').Append(inv);
        c.Result = sb.ToString();
    }

    /// <summary>The bundle page for one room, opened as the game does when a player uses the room's Junimo note.</summary>
    private static IClickableMenu OpenJunimoMenu(int area)
    {
        var dict = new Dictionary<int, bool[]>();
        foreach (var kv in Game1.netWorldState.Value.Bundles.Pairs)
            dict[kv.Key] = kv.Value;
        object? menu;
        try
        {
            menu = Activator.CreateInstance(typeof(JunimoNoteMenu), BindingFlags.Public | BindingFlags.Instance | BindingFlags.OptionalParamBinding,
                null, new object[] { area, dict }, null);
        }
        catch (MissingMethodException)
        {
            throw new LateBoundException("JunimoNoteMenu has no (area, bundles) constructor in this game version; stand at the room's Junimo note and use interact instead.");
        }
        catch (TargetInvocationException ex)
        {
            throw new LateBoundException($"The bundle page could not be opened: {(ex.InnerException ?? ex).Message}");
        }
        return (IClickableMenu)menu!;
    }

    private static ClickableComponent FindBundleComponent(IClickableMenu menu, int index)
    {
        if (LateBound.Require(menu, "bundles") is IEnumerable list)
            foreach (var o in list)
                if (o is ClickableComponent cc && LateBound.TryGet(o, "bundleIndex") is int bi && bi == index)
                    return cc;
        throw new LateBoundException($"Bundle {index} is not on the opened room page (JunimoNoteMenu.bundles / Bundle.bundleIndex differ in this game version).");
    }

    private static List<ClickableComponent> IngredientSlots(IClickableMenu menu) =>
        (LateBound.TryGet(menu, "ingredientSlots") as IEnumerable)?.OfType<ClickableComponent>().ToList() ?? new List<ClickableComponent>();

    /// <summary>Whatever the menu still holds on the cursor goes back into the inventory (or the ground), so closing the menu loses nothing.</summary>
    private static void ReturnJunimoHeldItem(IClickableMenu menu)
    {
        if (LateBound.TryGet(menu, "heldItem") is not Item held) return;
        LateBound.TrySet(menu, "heldItem", null);
        if (P.addItemToInventory(held) is Item rest)
            Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
    }

    /// <summary>Click every item of the open reward/grab menu into the inventory until it is empty or the inventory is full.</summary>
    private static IEnumerator CommunityTakeAll()
    {
        for (int i = 0; i < 60; i++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now) yield break;
            var list = now.ItemsToGrabMenu.actualInventory;
            var item = list.FirstOrDefault(x => x != null);
            if (item == null || !P.couldInventoryAcceptThisItem(item)) yield break;
            int slot = IndexOf(list, item);
            var cc = now.ItemsToGrabMenu.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) yield break;
            Click(now, cc);
            yield return Ticks(3);
        }
    }

    // ------------------------------------------------------------------ museum

    private static IEnumerator MuseumRun(JsonElement a, CallContext c)
    {
        var action = S(a, "action").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "list";
        switch (action)
        {
            case "list":
                var museum = Game1.getLocationFromName("ArchaeologyHouse") as LibraryMuseum;
                if (museum == null) { c.Fail("The Museum (ArchaeologyHouse) is not available."); yield break; }
                c.Result = MuseumListText(museum);
                break;
            case "donate":
                yield return MuseumDonate(a, c);
                break;
            case "rewards":
                yield return MuseumRewards(c);
                break;
            default:
                c.Fail($"Unknown action \"{action}\". Use list, donate or rewards.");
                break;
        }
    }

    /// <summary>LibraryMuseum.isItemSuitableForDonation: true for an artifact or mineral that is not on display yet.</summary>
    private static bool MuseumAccepts(LibraryMuseum museum, Item item)
    {
        // Looked up by name: depending on the game version it takes the Item or its item id.
        foreach (object arg in new object[] { item, item.QualifiedItemId })
            if (LateBound.TryCall(museum, museum.GetType(), "isItemSuitableForDonation", out var r, arg) && r is bool accepted)
                return accepted;
        throw new LateBoundException("LibraryMuseum.isItemSuitableForDonation takes neither an Item nor an item id in this game version.");
    }

    /// <summary>Rewards Gunther holds for this player (LibraryMuseum.getRewardsForPlayer), or -1 when that can't be asked.</summary>
    private static int MuseumRewardCount(LibraryMuseum museum)
    {
        try
        {
            if (LateBound.TryCall(museum, museum.GetType(), "getRewardsForPlayer", out var r, P) && r is ICollection list)
                return list.Count;
        }
        catch (LateBoundException) { }
        return -1;
    }

    private static string MuseumListText(LibraryMuseum museum)
    {
        var sb = new StringBuilder();
        var rows = new List<string>();
        for (int i = 0; i < P.Items.Count; i++)
        {
            var it = P.Items[i];
            if (it is SObject && MuseumAccepts(museum, it))
                rows.Add($"{it.DisplayName} x{it.Stack} (slot {i})");
        }
        sb.Append(rows.Count == 0
            ? "The Museum accepts none of the items you carry. It takes one of each artifact and mineral that is not on display yet."
            : $"The Museum still needs: {string.Join(", ", rows)}. museum(action=donate) walks you there and puts them all on display (one of each).");
        int rewards = MuseumRewardCount(museum);
        if (rewards > 0) sb.Append($"\nGunther has {rewards} reward(s) waiting for you: museum(action=rewards).");
        return sb.ToString();
    }

    /// <summary>One line for status / inventory: the carried pieces the Museum still needs and any rewards waiting; empty when there are none.</summary>
    internal static string MuseumHint()
    {
        try
        {
            if (Game1.getLocationFromName("ArchaeologyHouse") is not LibraryMuseum museum) return "";
            var wanted = P.Items.Where(it => it is SObject && MuseumAccepts(museum, it)).Select(it => it.DisplayName).Distinct().ToList();
            int rewards = MuseumRewardCount(museum);
            var parts = new List<string>();
            if (wanted.Count > 0) parts.Add($"the Museum still needs your {string.Join(", ", wanted.Take(6))}{(wanted.Count > 6 ? $" and {wanted.Count - 6} more" : "")} (museum(action=donate), don't sell them)");
            if (rewards > 0) parts.Add($"Gunther holds {rewards} reward(s) for you (museum(action=rewards))");
            var geodes = P.Items.Where(it => it != null && IsGeode(it)).Sum(it => it.Stack);
            if (geodes > 0) parts.Add($"{geodes} geode(s) to crack at Clint's (crack_geode)");
            var unshipped = P.Items.Where(it => it is SObject o && !o.bigCraftable.Value && !P.basicShipped.ContainsKey(o.ItemId)
                                                 && SObject.isPotentialBasicShipped(o.ItemId, o.Category, o.Type) && !wanted.Contains(o.DisplayName))
                .Select(it => it.DisplayName).Distinct().ToList();
            if (unshipped.Count > 0) parts.Add($"never shipped yet: {string.Join(", ", unshipped.Take(6))}{(unshipped.Count > 6 ? $" and {unshipped.Count - 6} more" : "")} (ship one of each for Full Shipment)");
            return parts.Count > 0 ? "Finds: " + string.Join("; ", parts) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Puts items on the next free display spot, the way MuseumMenu does (museumPieces gets the item id, the stack goes down by one).
    /// Left out on purpose: MuseumMenu's UI, its global chat line and its sounds beyond one.
    /// </summary>
    private static IEnumerator MuseumDonate(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        var want = S(a, "item").Trim();
        if (want.Length == 0) want = "all";
        if (Loc is not LibraryMuseum)
        {
            // Check before the walk that there is something to give.
            if (Game1.getLocationFromName("ArchaeologyHouse") is LibraryMuseum far && want.Equals("all", StringComparison.OrdinalIgnoreCase)
                && !P.Items.Any(it => it is SObject && MuseumAccepts(far, it)))
            {
                c.Fail(Reasons.NoItem, "The Museum accepts none of the items you carry. It takes one of each artifact and mineral that is not on display yet.");
                yield break;
            }
            var trip = new CallContext();
            yield return TravelTo("ArchaeologyHouse", trip);
            if (!trip.Ok || Loc is not LibraryMuseum)
            {
                c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get into the Museum (ArchaeologyHouse, open 8:00-18:00): {trip.Result}");
                yield break;
            }
        }
        var museum = (LibraryMuseum)Loc;

        var items = new List<Item>();
        if (want.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var carried in P.Items)
                if (carried is SObject && MuseumAccepts(museum, carried)) items.Add(carried);
            if (items.Count == 0) { c.Fail("The Museum accepts none of the items you carry (museum action=list)."); yield break; }
        }
        else
        {
            int slot = FindItem(want, x => x is SObject);
            if (slot < 0) { c.Fail($"No \"{want}\" in your inventory."); yield break; }
            var named = P.Items[slot];
            if (!MuseumAccepts(museum, named))
            {
                c.Fail($"The Museum does not need {named.DisplayName}: it only takes artifacts and minerals, one of each, that are not on display yet.");
                yield break;
            }
            items.Add(named);
        }

        var pieces = LateBound.Require(museum, "museumPieces");
        if (pieces == null) { c.Fail("The Museum has no display list in this game version."); yield break; }
        var donated = new List<string>();
        string? stopped = null;
        foreach (var it in items)
        {
            if (!MuseumAccepts(museum, it)) continue; // an earlier piece of the same kind went up
            var spot = FreeMuseumSpot(museum, pieces);
            if (spot == null) { stopped = "The Museum has no free display spot left."; break; }
            var name = it.DisplayName;
            LateBound.Call(pieces, "Add", spot.Value, it.ItemId);
            it.Stack--;
            if (it.Stack <= 0) P.removeItemFromInventory(it);
            Game1.playSound("stoneStep");
            donated.Add(name);
            Rt.Log.Add("museum_donated", text: $"{P.Name} donated {name} to the Museum",
                args: new { player = Rt.Log.Player, item = name, x = (int)spot.Value.X, y = (int)spot.Value.Y });
            yield return Ticks(8);
        }
        if (donated.Count == 0) { c.Fail(stopped ?? "Nothing was donated."); yield break; }

        TryCheckArchaeologyAchievements();
        var text = $"Donated to the Museum: {string.Join(", ", donated)}.";
        if (stopped != null) text += " " + stopped;
        int rewardsAfter = MuseumRewardCount(museum);
        if (rewardsAfter > 0)
        {
            // Collect them on the spot: the farmer is standing in the Museum already.
            var collect = new CallContext();
            yield return MuseumRewards(collect);
            text += collect.Ok ? " " + collect.Result : $" Gunther has {rewardsAfter} reward(s) for you: museum(action=rewards) ({collect.Result}).";
        }
        c.Result = text;
    }

    /// <summary>The first display tile (row by row) that LibraryMuseum.isTileSuitableForMuseumPiece accepts and nothing stands on yet.</summary>
    private static Vector2? FreeMuseumSpot(LibraryMuseum museum, object pieces)
    {
        var layer = museum.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight; y++)
            for (int x = 0; x < layer.LayerWidth; x++)
            {
                if (LateBound.Call(museum, "isTileSuitableForMuseumPiece", x, y) is not true) continue;
                var v = new Vector2(x, y);
                if (LateBound.Call(pieces, "ContainsKey", v) is not true) return v;
            }
        return null;
    }

    /// <summary>MuseumMenu asks the stats to check the archaeology achievements after each piece; best effort.</summary>
    private static void TryCheckArchaeologyAchievements()
    {
        try { LateBound.TryCall(Game1.stats, Game1.stats.GetType(), "checkForArchaeologyAchievements", out _); }
        catch (LateBoundException) { }
    }

    /// <summary>Gunther's "Collect" answer opens a grab menu with the earned rewards; take them all.</summary>
    private static IEnumerator MuseumRewards(CallContext c)
    {
        if (Loc is not LibraryMuseum)
        {
            var trip = new CallContext();
            yield return TravelTo("ArchaeologyHouse", trip);
            if (!trip.Ok || Loc is not LibraryMuseum)
            {
                c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get into the Museum (ArchaeologyHouse, open 8:00-18:00): {trip.Result}");
                yield break;
            }
        }
        var museum = (LibraryMuseum)Loc;
        if (Game1.activeClickableMenu != null || Game1.eventUp) { c.Fail("Close the open menu or finish the scene first."); yield break; }
        if (MuseumRewardCount(museum) == 0)
        {
            c.Fail("Gunther has no reward for you yet. Rewards come at donation milestones; museum(action=list) shows what you can donate.");
            yield break;
        }

        // Gunther answers at his desk: an Action tile that names him, else his own tile.
        Point? at = null;
        var layer = museum.Map.Layers[0];
        for (int y = 0; y < layer.LayerHeight && at == null; y++)
            for (int x = 0; x < layer.LayerWidth; x++)
                if (museum.doesTileHaveProperty(x, y, "Action", "Buildings") is { } act && act.Contains("Gunther", StringComparison.OrdinalIgnoreCase))
                {
                    at = new Point(x, y);
                    break;
                }
        if (at == null && museum.characters.FirstOrDefault(n => n.Name == "Gunther") is { } gunther) at = gunther.TilePoint;
        if (at == null) { c.Fail("Gunther is not in the Museum right now (it may be closed)."); yield break; }

        var before = Snapshot();
        yield return WalkAdjacent(at.Value, c);
        if (!c.Ok) yield break;
        PressAction(at.Value);
        for (int i = 0; i < 90 && Game1.activeClickableMenu is not DialogueBox; i++) yield return null;
        yield return Ticks(15);

        var box = Game1.activeClickableMenu as DialogueBox;
        if (box == null || !box.isQuestion || box.responses == null)
        {
            var said = box != null ? box.getCurrentString() : "nothing opened";
            c.Fail($"Gunther did not offer a choice ({said}). Stand next to his desk and use interact on it, then menu/respond.");
            yield break;
        }
        int option = Array.FindIndex(box.responses, r => r.responseKey.Equals("Collect", StringComparison.OrdinalIgnoreCase)
                                                        || r.responseText.Contains("collect", StringComparison.OrdinalIgnoreCase));
        if (option < 0)
        {
            box.closeDialogue();
            c.Fail("Gunther did not offer to hand out rewards (none waiting).");
            yield break;
        }

        using (var doc = JsonDocument.Parse($"{{\"option\":{option}}}"))
        {
            var pickOption = doc.RootElement.Clone();
            yield return Respond(pickOption, c);
        }
        if (!c.Ok) yield break;
        for (int i = 0; i < 90 && Game1.activeClickableMenu is not ItemGrabMenu; i++) yield return null;
        if (Game1.activeClickableMenu is ItemGrabMenu)
        {
            yield return CommunityTakeAll();
            if (Game1.activeClickableMenu is ItemGrabMenu g && g.ItemsToGrabMenu.actualInventory.All(i => i == null))
            {
                g.exitThisMenu();
                yield return Ticks(10);
            }
        }

        var diff = Diff(before);
        if (diff.Length == 0)
        {
            c.Fail("No reward reached your inventory." + (Game1.activeClickableMenu is ItemGrabMenu ? " The reward menu is still open: free inventory space, then menu(action=take) or close_menu." : ""));
            yield break;
        }
        var leftover = Game1.activeClickableMenu is ItemGrabMenu ? " Some rewards are still in the open menu (inventory full?): menu(action=take) after freeing space." : "";
        Rt.Log.Add("museum_rewards", text: $"{P.Name} collected the Museum rewards", args: new { player = Rt.Log.Player });
        c.Result = $"Collected the Museum rewards from Gunther. {diff}{leftover}".Trim();
    }
}
