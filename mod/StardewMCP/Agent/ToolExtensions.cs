using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace StardewMCP.Agent;

/// <summary>
/// Tools other SMAPI mods add through the mod API (<see cref="IStardewMcpApi"/>). Each one is an ordinary
/// <see cref="ToolDef"/>: its handler runs on the game thread for the calling seat's screen and answers in one call. A
/// handler reports a failure by throwing; the exception's message is the result. A tool with a built-in tool's name takes
/// its place (the mod's own code that calls the built-in coroutine directly keeps using it). Also the lines other mods add
/// to the morning briefing. Game-free, so the unit tests compile it.
/// </summary>
public static class ToolExtensions
{
    private static readonly List<ToolDef> _tools = new();

    /// <summary>Raised for each tool added after the tool list was read (AgentHttp adds it to its table).</summary>
    public static event Action<ToolDef>? Added;

    public static IReadOnlyList<ToolDef> Tools => _tools;

    /// <summary>Tool names sent whole in the compact tool profile.</summary>
    public static readonly HashSet<string> Core = new(StringComparer.Ordinal);

    private static readonly List<Func<string>> _morningNotes = new();

    /// <summary>A line another mod adds to each farmer's morning briefing (empty: nothing that day).</summary>
    public static void AddMorningNote(Func<string> note) => _morningNotes.Add(note);

    /// <summary>The added morning lines for the farmer being briefed; a note that throws is left out.</summary>
    public static List<string> MorningNotesText()
    {
        var lines = new List<string>();
        foreach (var note in _morningNotes)
        {
            try
            {
                var text = note()?.Trim();
                if (!string.IsNullOrEmpty(text)) lines.Add(text);
            }
            catch (Exception) { }
        }
        return lines;
    }

    private static readonly List<(Func<string, int> count, Func<string, string> howToUse)> _holders = new();

    /// <summary>Another mod keeps items for the farmer outside the pack and the chests (machines in its slots).</summary>
    public static void AddItemHolder(Func<string, int> count, Func<string, string> howToUse) => _holders.Add((count, howToUse));

    /// <summary>How many of this qualified item id the other mods hold for the farmer; a holder that throws counts 0.</summary>
    public static int Held(string qualifiedId)
    {
        int n = 0;
        foreach (var (count, _) in _holders)
            try { n += Math.Max(0, count(qualifiedId)); }
            catch (Exception) { }
        return n;
    }

    /// <summary>Where the other mods hold this item and how to use it ("" when none).</summary>
    public static string HeldNote(string qualifiedId)
    {
        var lines = new List<string>();
        foreach (var (count, howToUse) in _holders)
            try
            {
                if (count(qualifiedId) > 0 && howToUse(qualifiedId)?.Trim() is { Length: > 0 } line) lines.Add(line);
            }
            catch (Exception) { }
        return string.Join(" ", lines);
    }

    /// <summary>Another mod pays building and upgrade materials out of the chests by itself.</summary>
    public static bool StoragePays { get; set; }

    /// <summary>Whether a tool of this name was added (it then takes the place of a built-in tool of the same name).</summary>
    public static bool Has(string name) => _tools.Exists(t => t.Name == name);

    /// <summary>The added tool of this name, or null.</summary>
    public static ToolDef? Find(string name) => _tools.Find(t => t.Name == name);

    public static ToolDef Add(string name, string description, string inputSchemaJson, Func<string, string> run, bool instant, bool core)
    {
        if (_tools.Exists(t => t.Name == name)) throw new ArgumentException($"A tool called {name} was already added.");
        var tool = new ToolDef
        {
            Name = name,
            Description = description,
            Schema = JsonDocument.Parse(inputSchemaJson).RootElement.Clone(),
            Instant = instant,
            Run = (args, ctx) => Call(run, args, ctx)
        };
        _tools.Add(tool);
        if (core) Core.Add(name);
        Added?.Invoke(tool);
        return tool;
    }

    private static IEnumerator Call(Func<string, string> run, JsonElement args, CallContext ctx)
    {
        try { ctx.Result = run(args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText()); }
        catch (Exception ex) { ctx.Fail((ex.InnerException ?? ex).Message); }
        yield break;
    }
}
