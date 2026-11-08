using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    private IReadOnlyList<LlmTool>? _withoutNaming;
    private bool _namingOpen;
    private bool _lastWasThinkOnly;
    private readonly RepeatBrake _brake = new();
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
    private readonly ConcurrentQueue<string> _operatorCommands = new();
    private readonly DailySocialProgress _social = new();

    public void Order(string text) => _operatorCommands.Enqueue(text);

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
    private static bool OvernightTransition => Players.Slots.Any(s => s.Role == SeatRole.Ai && s.State is ScreenState { Transitioning: true });

    /// <summary>This seat's todo checklist for the handoff after a session break, or null when it wrote none today.</summary>
    private string? TodayChecklist(int day) => DayPlans.For(Id) is { } plan && plan.Active(day) ? plan.Checklist() : null;

    // ------------------------------------------------------------------ the day loop

    private async Task Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Status = "waiting for the game";
                while (!ct.IsCancellationRequested && !(_slot.Active && DaySafety.CanStartDay(State?.Ready == true, State?.Transitioning == true, OvernightTransition, Day)))
                    await Task.Delay(500, ct);

                int day = Day;
                _slot.MarkContact();
                Log.Add("system", text: $"{Id}: {_llm.Name} {_llm.Model} starts day {day}");
                bool alive = await PlayDay(day, ct);
                if (!alive)
                    return;

                // In bed (or idle and released, or over its token budget): do not hold the world for the others, wait for the morning.
                _slot.SetReleased(true);
                while (!ct.IsCancellationRequested && Day == day &&
                       (_operatorCommands.IsEmpty || (_cfg.DailyTokenBudget > 0 && InputTokens + OutputTokens >= _cfg.DailyTokenBudget)))
                    await Task.Delay(1000, ct);
                if (Day == day && !ct.IsCancellationRequested)
                {
                    Log.Add("system", text: $"{Id}: operator instruction resumes this day");
                    continue; // retain today's token usage and let MarkContact reclaim this seat
                }
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
        _social.Begin(day, others.Count > 0);
        NewSession();

        string briefing = await BriefingText(ct);
        // Chores the model kept putting off (the day-1 gift box, letters, quest money) are done before its first turn.
        if (_cfg.BrainMorningChores)
        {
            var (_, chores) = await RunTool(new LlmToolCall("morning_chores", "morning_chores", "{\"reason\":\"morning chores\"}"), ct);
            briefing += "\nDone for you this morning (gift boxes, mailbox, quest money):\n" + chores;
        }
        var (questsOk, questReport) = await RunTool(new LlmToolCall("morning_quests", "quests", "{}"), ct);
        // Only a request it can already do: the walk to Town costs about an hour of the morning.
        if (questsOk && _cfg.BrainAcceptBoardQuest && questReport.Contains("still FREE", StringComparison.Ordinal) && questReport.Contains("Doable: yes", StringComparison.Ordinal))
        {
            var (accepted, acceptText) = await RunTool(new LlmToolCall("morning_board", "accept_quest", "{\"reason\":\"take today's Help Wanted request\"}"), ct);
            briefing += $"\nToday's Help Wanted request was taken for you{(accepted ? "" : " (it failed)")}: {Prompts.Clip(acceptText, 600)}";
            (questsOk, questReport) = await RunTool(new LlmToolCall("morning_quests", "quests", "{}"), ct);
        }
        if (questsOk) briefing += "\nToday's actual journal and board report:\n" + questReport;
        // No chest listing here any more: a chest(action=all) call every morning cost an action and a long text, and the
        // tools that need what the chests hold (craft, build, the farm plan) read them themselves.
        var pass = PassOut.View(Id);
        var messages = new List<ChatMessage>
        {
            ChatMessage.User(EnglishNames.Apply(Prompts.Morning(day, briefing, pass.PendingDay, others.Select(o => o.Name), Time)))
        };
        int idle = 0;
        bool dayDone = false;
        string? lastThink = null;
        var lastResults = new Queue<string>();
        int hardCap = Math.Max(60_000, _cfg.SessionBreakResultChars * 2);

        while (!dayDone && !ct.IsCancellationRequested)
        {
            if (OvernightTransition)
            {
                Status = "waiting for night event / fade to finish";
                _slot.SetReleased(true);
                while (OvernightTransition && !ct.IsCancellationRequested) await Task.Delay(250, ct);
                _slot.MarkContact();
            }
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

            while (_operatorCommands.TryDequeue(out var order))
                messages.Add(ChatMessage.User("The human operator gives you this instruction. Prioritize it in your next decisions, subject to the game's rules: " + order));
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
                // Farmers spawn on a bed each morning. Standing on that tile is not proof that
                // the day's work is finished; only a successful go_to_bed ends the action loop.
                if (++idle >= MaxIdleTurns)
                {
                    Log.Add("system", text: $"{Id}: no tool calls after {MaxIdleTurns} replies; releasing the clock and retrying within 30 seconds");
                    Status = "idle (retry within 30s)";
                    _slot.SetReleased(true);
                    for (int wait = 0; wait < 60 && Day == day && _operatorCommands.IsEmpty; wait++)
                        await Task.Delay(500, ct);
                    if (Day != day) return true;
                    _slot.MarkContact();
                    messages = new List<ChatMessage>
                    {
                        ChatMessage.User(EnglishNames.Apply(Prompts.Handoff(day, Time, Log.ReadPlan(), lastThink, lastResults, TodayChecklist(day)) +
                            "\nYour last replies contained no tool calls. The day is not finished. Call status now, then use a real tool to continue. End the day only with journal and go_to_bed.\n" + _social.Pending))
                    };
                    NewSession();
                    idle = 0;
                    continue;
                }
                messages.Add(ChatMessage.User(Prompts.Nudge(Time, false)));
                continue;
            }
            idle = 0;

            var results = new List<LlmToolResult>();
            bool breakNow = false;
            _social.SetNearestVillager(State?.NearestVillager?.name, State?.NearestVillager?.where);
            foreach (var call in resp.ToolCalls)
            {
                // One bounded reminder, never a hard social quota or a delay when it is late/in the mines.
                string bedtime = call.Name == "go_to_bed" && State is { Energy: > 20 } &&
                    !(State.Location?.StartsWith("UndergroundMine", StringComparison.Ordinal) ?? false) &&
                    int.TryParse(Time.Split(':')[0], out var hour) && hour < 21
                    ? _social.BeforeBed() : "";
                _brake.Other(call.Name);
                var refused = _brake.Before(call.Name);
                var (ok, text) = refused != null ? (false, refused) : bedtime.Length > 0 ? (false, bedtime) : await RunTool(call, ct);
                if (refused == null) text += _brake.After(call.Name, ok);
                // A reply spent on thinking alone, right after another one, costs a whole model turn (14B: 0.9 per action).
                if (call.Name == "think" && resp.ToolCalls.Count == 1 && _lastWasThinkOnly)
                    text += "\nYou already thought. Now act: call the next tool, and put any further thought in that reply's text.";
                _lastWasThinkOnly = call.Name == "think" && resp.ToolCalls.Count == 1;
                if (text.Contains("Naming: \"", StringComparison.Ordinal)) _namingOpen = true; // a naming box opened: offer the name tool
                else if (ok && call.Name == "name") _namingOpen = false;
                _social.Observe(call.Name, ok, text);
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
                messages = new List<ChatMessage> { ChatMessage.User(EnglishNames.Apply(Prompts.Handoff(day, Time, Log.ReadPlan(), lastThink, lastResults, TodayChecklist(day)) + "\n" + _social.Brief)) };
                NewSession();
                _slot.Runtime.Stats.Count("session_break");
                Log.Add("system", text: $"{Id}: conversation restarted with a handoff ({(breakNow ? "session break" : "context too large")})");
            }
            var reminder = _social.Reminder();
            if (reminder.Length > 0)
            {
                messages.Add(ChatMessage.User(EnglishNames.Apply(reminder)));
                Log.Add("system", text: "Daily social reminder: " + reminder);
            }
        }

        if (dayDone) await CondenseLessons(ct);
        Status = dayDone ? "in bed" : "stopped";
        return true;
    }

    // ------------------------------------------------------------------ lessons (LessonBook)

    /// <summary>
    /// Lessons that hold on any farm, shared by every seat and every save: Mods/StardewMCP/memory/lasting_lessons.md, outside the
    /// log folder, so clearing the logs for a new save keeps them. The first time it starts from assets/lasting_lessons_seed.md.
    /// </summary>
    private string LastingFile =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_assetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, "memory", "lasting_lessons.md");

    private string ReadLasting()
    {
        try
        {
            var file = LastingFile;
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var seed = Path.Combine(_assetDir, "lasting_lessons_seed.md");
                if (File.Exists(seed)) File.Copy(seed, file);
            }
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// After bed: once the lessons outgrow the prompt, one model call (no tools) condenses them to the most useful
    /// <see cref="LessonBook.MaxLessons"/> and updates the lasting lessons. The old text goes to lessons_archive.md.
    /// </summary>
    private async Task CondenseLessons(CancellationToken ct)
    {
        var path = _log.LessonsFile(Id);
        var lessons = _log.ReadMemoryText(path);
        if (!LessonBook.ShouldCondense(lessons)) return;
        try
        {
            Status = "tidying the lessons";
            var language = string.IsNullOrWhiteSpace(_cfg.WorkLanguageName()) ? _cfg.LanguageName() : _cfg.WorkLanguageName();
            var r = await _llm.Complete(LessonBook.System,
                new List<ChatMessage> { ChatMessage.User(LessonBook.Request(lessons, ReadLasting(), language)) }, Array.Empty<LlmTool>(), ct);
            InputTokens += r.InputTokens;
            OutputTokens += r.OutputTokens;
            if (LessonBook.Parse(r.Text) is not { } p)
            {
                Log.Add("system", text: $"{Id}: lessons kept as they are (the condensing answer had no LESSONS list)");
                return;
            }
            int before = lessons.Split('\n').Count(l => l.Trim().Length > 0);
            _log.ReplaceLessons(p.lessons, lessons, Id);
            if (p.lasting.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastingFile)!);
                File.WriteAllText(LastingFile, "# Lessons that hold on every farm (kept across saves)\n" + LessonBook.Format(p.lasting));
            }
            Log.Add("system", text: $"{Id}: lessons condensed from {before} lines to {p.lessons.Count}; {p.lasting.Count} lasting lesson(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Add("system", text: $"{Id}: lessons not condensed: {Prompts.Clip(ex.Message, 200)}");
        }
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
            Notes: _log.ReadMemoryText(_log.NotesFile(Id)),
            Speech: _cfg.SpeechLanguageName(),
            MemoryLines: _cfg.BrainMemoryLines,
            Work: _cfg.WorkLanguageName(),
            Automate: AgentTools.AutomateLoaded,
            Lasting: ReadLasting());
        return EnglishNames.Apply(Prompts.System(Prompts.Template(_assetDir), inputs));
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

    /// <summary>
    /// The tool list for this request. name is only offered while a naming box is open: a local Qwen3 called it 20 times in
    /// a row with nothing to name (its tool-call format has a "name" key of its own).
    /// </summary>
    private IReadOnlyList<LlmTool> ToolsNow() =>
        _namingOpen ? _llmTools : _withoutNaming ??= _llmTools.Where(t => t.Name != "name").ToList();

    private async Task<LlmResponse?> Ask(string system, List<ChatMessage> messages, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var r = await _llm.Complete(system, messages, ToolsNow(), ct);
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
        if (OvernightTransition)
            return (false, "A night event, pass-out or fade is still playing. The new date does not mean you are awake. Wait for the transition to finish; do not say, respond, move or sleep again yet.");

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
