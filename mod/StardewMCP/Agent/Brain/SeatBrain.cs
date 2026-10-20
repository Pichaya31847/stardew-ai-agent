using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// The built-in AI loop of one seat: talks to a chat model on a background thread, runs the tools it asks for on the
/// seat's <see cref="AgentRuntime"/> (game thread), and plays day after day. It is the in-mod stand-in for an MCP
/// client plus the studio runner: morning briefing, free play (no meeting phase: farmers who want to plan together
/// go and find each other), the journal and bed, session breaks with a handoff, and retries around the model's HTTP errors.
/// Between a tool result and the next tool call the seat counts as thinking, so <see cref="WorldClock"/> freezes the world.
/// </summary>
public sealed class SeatBrain
{
    private const int MaxIdleTurns = 3;
    private const int MaxRetries = 8;

    private readonly PlayerSlot _slot;
    private readonly ILlmProvider _llm;
    private readonly ModConfig _cfg;
    private readonly IMonitor _mon;
    private readonly AgentLog _log;
    private readonly IReadOnlyDictionary<string, ToolDef> _tools;
    private readonly IReadOnlyList<LlmTool> _llmTools;
    private readonly string _assetDir;
    private CancellationTokenSource? _cts;

    public string Id => _slot.Id;
    public string Provider => _llm.Name;
    public string Model => _llm.Model;

    /// <summary>One line for the HUD and the console: what the brain is doing or why it stopped.</summary>
    public volatile string Status = "starting";
    public volatile bool Failed;
    public long InputTokens;
    public long OutputTokens;
    public int Calls;

    public SeatBrain(PlayerSlot slot, ILlmProvider llm, ModConfig cfg, IMonitor mon, AgentLog log,
        IReadOnlyDictionary<string, ToolDef> tools, IReadOnlyList<LlmTool> llmTools, string assetDir)
    {
        _slot = slot;
        _llm = llm;
        _cfg = cfg;
        _mon = mon;
        _log = log;
        _tools = tools;
        _llmTools = llmTools;
        _assetDir = assetDir;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => Run(ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _slot.Connected = false;
        Status = "stopped";
    }

    private ScreenState? State => _slot.State as ScreenState;
    private int Day => State?.Day ?? 0;
    private string Time => State?.Time ?? "";
    private PlayerLog Log => _slot.Runtime.Log;

    // ------------------------------------------------------------------ the day loop

    private async Task Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Status = "waiting for the game";
                while (!ct.IsCancellationRequested && !(_slot.Active && State is { Ready: true } && Day > 0))
                    await Task.Delay(500, ct);

                int day = Day;
                _slot.MarkContact();
                Log.Add("system", text: $"{Id}: {_llm.Name} {_llm.Model} starts day {day}");
                bool alive = await PlayDay(day, ct);
                if (!alive)
                    return;

                // In bed (or idle and released, or over its token budget): do not hold the world for the others, wait for the morning.
                _slot.SetReleased(true);
                while (!ct.IsCancellationRequested && Day == day)
                    await Task.Delay(1000, ct);
                Log.Add("system", text: $"{Id}: day {day} used {InputTokens} input + {OutputTokens} output tokens in {Calls} tool calls");
                InputTokens = OutputTokens = 0;
                Calls = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failed = true;
            Status = "crashed: " + Prompts.Clip(ex.Message, 120);
            _mon.Log($"[{Id}] built-in AI crashed: {ex}", LogLevel.Error);
            Log.Add("system", text: $"{Id}: built-in AI crashed: {ex.Message}");
            _slot.Connected = false;
        }
    }

    /// <summary>One in-game day. False when the model is unusable (bad key, model not found) and the brain stops.</summary>
    private async Task<bool> PlayDay(int day, CancellationToken ct)
    {
        string system = BuildSystem();
        var others = Brains.OtherPlayers(_slot);
        NewSession();

        string briefing = await BriefingText(ct);
        var pass = PassOut.View(Id);
        var messages = new List<ChatMessage>
        {
            ChatMessage.User(Prompts.Morning(day, briefing, pass.PendingDay, others.Select(o => o.Name), Time))
        };
        int idle = 0;
        bool dayDone = false;
        string? lastThink = null;
        var lastResults = new Queue<string>();
        int hardCap = Math.Max(60_000, _cfg.SessionBreakResultChars * 2);

        while (!dayDone && !ct.IsCancellationRequested)
        {
            while (RunControl.Holding && !ct.IsCancellationRequested)
            {
                Status = "paused";
                await Task.Delay(500, ct);
            }
            if (Day != day)
            {
                Status = "the day ended";
                return true;
            }
            if (_cfg.DailyTokenBudget > 0 && InputTokens + OutputTokens >= _cfg.DailyTokenBudget)
            {
                Log.Add("system", text: $"{Id}: daily token budget reached ({InputTokens} input + {OutputTokens} output, budget {_cfg.DailyTokenBudget}): it stops for today and the world runs on without it");
                Status = "token budget reached";
                return true;
            }

            Status = "thinking";
            var resp = await Ask(system, messages, ct);
            if (resp == null)
                return false;

            if (!string.IsNullOrWhiteSpace(resp.Text))
            {
                lastThink = resp.Text;
                Log.Add("think", text: Prompts.Clip(resp.Text, 1200));
            }
            messages.Add(ChatMessage.Assistant(resp.Text, resp.ToolCalls));

            if (resp.ToolCalls.Count == 0)
            {
                if (State?.InBed == true)
                {
                    Status = "in bed";
                    return true;
                }
                if (++idle > MaxIdleTurns)
                {
                    Log.Add("system", text: $"{Id} ended its turn {MaxIdleTurns} times without acting: the world runs on without it today");
                    Status = "idle (released)";
                    return true;
                }
                messages.Add(ChatMessage.User(Prompts.Nudge(Time, false)));
                continue;
            }
            idle = 0;

            var results = new List<LlmToolResult>();
            bool breakNow = false;
            foreach (var call in resp.ToolCalls)
            {
                var (ok, text) = await RunTool(call, ct);
                results.Add(new LlmToolResult(call.Id, call.Name, text, !ok));
                lastResults.Enqueue($"{call.Name}: {Prompts.Clip(text, 400)}");
                while (lastResults.Count > 3) lastResults.Dequeue();
                if (text.Contains(SessionBreaks.Marker))
                    breakNow = true;
                if (ok && call.Name == "go_to_bed")
                    dayDone = true;
                Calls++;
            }

            messages.Add(new ChatMessage { Role = "tool", ToolResults = results });
            if (dayDone)
                break;

            if (breakNow || messages.Sum(m => m.Chars) > hardCap)
            {
                messages = new List<ChatMessage> { ChatMessage.User(Prompts.Handoff(day, Time, Log.ReadPlan(), lastThink, lastResults)) };
                NewSession();
                Log.Add("system", text: $"{Id}: conversation restarted with a handoff ({(breakNow ? "session break" : "context too large")})");
            }
        }

        Status = dayDone ? "in bed" : "stopped";
        return true;
    }

    // ------------------------------------------------------------------ pieces

    private string BuildSystem()
    {
        var inputs = new Prompts.SystemInputs(
            Model: _llm.Model,
            PlayerName: State?.Name ?? Id,
            Seat: Id,
            Language: _cfg.LanguageName(),
            FreezeWhileThinking: _cfg.FreezeTimeWhileThinking,
            Others: Brains.OtherPlayers(_slot),
            Self: Log.ReadSelf(),
            People: Log.ReadPeople(),
            Lessons: _log.ReadMemoryText(_log.LessonsFile(Id)),
            Plan: Log.ReadPlan(),
            Notes: _log.ReadMemoryText(_log.NotesFile(Id)));
        return Prompts.System(Prompts.Template(_assetDir), inputs);
    }

    /// <summary>A fresh MCP-style session for <see cref="SessionBreaks"/>: counters restart and breaks are armed.</summary>
    private void NewSession()
    {
        using var doc = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        SessionBreaks.Observe(Id, doc.RootElement, "1");
    }

    private async Task<string> BriefingText(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 15 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var (status, body) = await Briefing.Serve(Id, _cfg.SplitScreen);
                if (status == 200 && !string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    bool ready = !root.TryGetProperty("ready", out var r) || r.ValueKind != JsonValueKind.False;
                    string text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (ready)
                        return text;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                _mon.Log($"[{Id}] briefing unreadable: {ex.Message}", LogLevel.Trace);
            }
            await Task.Delay(1000, ct);
        }
        return "";
    }

    private async Task<LlmResponse?> Ask(string system, List<ChatMessage> messages, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var r = await _llm.Complete(system, messages, _llmTools, ct);
                InputTokens += r.InputTokens;
                OutputTokens += r.OutputTokens;
                _mon.Log($"[{Id}] {_llm.Model}: {r.ToolCalls.Count} tool call(s), {r.InputTokens}+{r.OutputTokens} tokens, {sw.ElapsedMilliseconds} ms", LogLevel.Trace);
                return r;
            }
            catch (LlmException ex)
            {
                if (ex.Fatal || ++attempt > MaxRetries)
                {
                    Failed = true;
                    Status = "AI error: " + Prompts.Clip(ex.Message, 160);
                    _mon.Log($"[{Id}] built-in AI stopped: {ex.Message}", LogLevel.Error);
                    Log.Add("system", text: $"{Id}: AI stopped: {ex.Message}");
                    _slot.Connected = false;
                    return null;
                }
                int wait = Math.Min(120, 5 << Math.Min(attempt - 1, 4));
                Status = $"AI error, retry in {wait}s: {Prompts.Clip(ex.Message, 80)}";
                _mon.Log($"[{Id}] {ex.Message}; retry {attempt}/{MaxRetries} in {wait}s", LogLevel.Warn);
                await Task.Delay(wait * 1000, ct);
            }
        }
    }

    private async Task<(bool ok, string text)> RunTool(LlmToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return (false, $"Unknown tool {call.Name}. Use only the tools you were given.");

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(LlmHttp.ArgsOrEmpty(call.ArgumentsJson));
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (false, "The arguments were not a JSON object.");
        }

        if (!_slot.Active)
            return (false, "You are not in the game yet. Wait a moment and try again.");

        _slot.MarkContact();
        Status = tool.Name;
        var pending = _slot.Runtime.EnqueueCall(tool, args, observation: true);
        var (ok, text) = await pending.Done.Task.WaitAsync(ct);

        // The same text an MCP client gets: the [partial · reason] / [no_path] tag in front, if any.
        try
        {
            var reply = JsonSerializer.SerializeToElement(ToolResult.McpReply(ok, text, pending.Outcome), AgentLog.Json);
            if (reply.TryGetProperty("content", out var content) && content.GetArrayLength() > 0 &&
                content[0].TryGetProperty("text", out var t) && t.GetString() is { } tagged)
                text = tagged;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return (ok, text);
    }
}
