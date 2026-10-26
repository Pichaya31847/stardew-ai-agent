using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// Starts and stops the built-in AI (<see cref="SeatBrain"/>) of every seat whose config names a provider, and holds
/// what the seats share: the HTTP client and the tool list.
/// </summary>
public static class Brains
{
    private static IModHelper _helper = null!;
    private static IMonitor _mon = null!;
    private static ModConfig _cfg = null!;
    private static AgentLog _log = null!;
    private static string _assetDir = "";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SeatBrain> Running = new();
    private static Dictionary<string, ToolDef>? _tools;
    private static List<LlmTool>? _llmTools;

    public static void Init(IModHelper helper, IMonitor monitor, ModConfig config, AgentLog log, string assetDir)
    {
        _helper = helper;
        _mon = monitor;
        _cfg = config;
        _log = log;
        _assetDir = assetDir;
    }

    public static IReadOnlyList<SeatBrain> All
    {
        get { lock (Gate) return Running.Values.ToList(); }
    }

    public static SeatBrain? For(string seatId)
    {
        lock (Gate) return Running.TryGetValue(seatId, out var b) ? b : null;
    }

    /// <summary>"anthropic/claude-haiku-5-5" for a seat the mod plays itself, else null.</summary>
    public static string? Describe(string seatId) => For(seatId) is { } b ? $"{b.Provider}/{b.Model}" : null;

    /// <summary>Called on the host screen once a save is loaded: one brain per configured seat.</summary>
    public static void StartAll()
    {
        StopAll();
        var tools = Tools();
        var llmTools = LlmTools(tools);
        lock (Gate)
        {
            foreach (var slot in Players.Slots)
            {
                if (slot.Role != SeatRole.Ai || slot.Screen >= Players.ScreenCount)
                    continue;
                var sc = _cfg.Seat(slot.Screen);
                if (!sc.HasBrain)
                    continue;
                var settings = new LlmSettings(sc.Brain, sc.Model, sc.ApiKey, sc.BaseUrl, _cfg.MaxOutputTokens);
                if (LlmProviders.Problem(settings) is { } problem)
                {
                    _mon.Log($"{slot.Id}: built-in AI not started: {problem}. Fix Seats[{slot.Screen}] in config.json (or the mod's config menu).", LogLevel.Error);
                    continue;
                }
                var provider = LlmProviders.Create(settings, Http);
                if (provider == null)
                    continue;
                var brain = new SeatBrain(slot, provider, _cfg, _mon, _log, tools, llmTools, _assetDir);
                Running[slot.Id] = brain;
                lock (_log.Meta)
                    _log.Meta[slot.Screen == 0 ? "model" : $"model_{slot.Id}"] = provider.Model;
                brain.Start();
                _mon.Log($"{slot.Id}: built-in AI {provider.Name} {provider.Model} started", LogLevel.Info);
            }
        }
    }

    public static void StopAll()
    {
        lock (Gate)
        {
            foreach (var b in Running.Values)
                b.Stop();
            Running.Clear();
        }
    }

    /// <summary>Config saved from the menu: restart the brains with the new settings when a save is open.</summary>
    public static void OnConfigSaved()
    {
        if (Context.IsWorldReady && Context.IsMainPlayer)
            StartAll();
    }

    private static Dictionary<string, ToolDef> Tools()
    {
        _tools ??= AgentTools.All().ToDictionary(t => t.Name);
        return _tools;
    }

    private static List<LlmTool> LlmTools(Dictionary<string, ToolDef> tools)
    {
        // Rebuilt on every start: BrainTools may have changed in the config menu.
        _llmTools = ToolProfile.Apply(_cfg.BrainTools, tools.Values
            .Select(t => new LlmTool(t.Name, t.Description, JsonSerializer.SerializeToElement(t.Schema, AgentLog.Json))));
        return _llmTools;
    }

    /// <summary>The other farmers on this farm, for the system prompt: who they are and what plays them.</summary>
    public static IReadOnlyList<(string Seat, string Name, string Kind)> OtherPlayers(PlayerSlot me)
    {
        var list = new List<(string, string, string)>();
        foreach (var s in Players.Slots)
        {
            if (s == me || s.Screen >= Players.ScreenCount || s.Role is SeatRole.Off or SeatRole.Camera)
                continue;
            string name = (s.State as ScreenState)?.Name ?? SplitScreen.NameFor(s.Screen);
            string kind = s.Role == SeatRole.Human ? "a human player at the keyboard"
                : For(s.Id) is { } b ? $"an AI ({b.Model})"
                : s.Connected ? "an AI" : "an AI (not connected yet)";
            list.Add((s.Id, name, kind));
        }
        return list;
    }

    /// <summary>SMAPI console: mcp_brain [status|start|stop].</summary>
    public static void Command(string[] args)
    {
        string op = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (op)
        {
            case "start":
                if (!Context.IsWorldReady) { _mon.Log("Load a save first.", LogLevel.Info); return; }
                StartAll();
                break;
            case "stop":
                StopAll();
                _mon.Log("Built-in AI stopped for every seat.", LogLevel.Info);
                break;
            default:
                var all = All;
                if (all.Count == 0)
                {
                    _mon.Log("No built-in AI is running. Set Seats[N].Brain and ApiKey in config.json, then load a save (or run mcp_brain start).", LogLevel.Info);
                    return;
                }
                foreach (var b in all)
                    _mon.Log($"{b.Id}: {b.Provider} {b.Model}: {b.Status} ({b.Calls} calls, {b.InputTokens}+{b.OutputTokens} tokens today)", LogLevel.Info);
                break;
        }
    }
}
