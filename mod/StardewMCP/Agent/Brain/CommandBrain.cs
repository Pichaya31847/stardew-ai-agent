using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent.Brain;

/// <summary>One /do command kept in the history: what was typed, the tool calls it became, what they did and the area.</summary>
public sealed class DoRecord
{
    public int N { get; set; }
    public string Command { get; set; } = "";
    public string Date { get; set; } = "";
    public List<DoCall> Calls { get; set; } = new();
    public List<string> Did { get; set; } = new();
    /// <summary>x, y, w, h of the area it worked on, or null.</summary>
    public int[]? Area { get; set; }
}

public sealed class DoCall
{
    public string Tool { get; set; } = "";
    public string Args { get; set; } = "{}";
}

/// <summary>
/// /do for a person playing P1 (Role "human") with a Brain configured: one command typed in chat, in any language, becomes
/// the tool calls that do it, run on P1's own runtime with every guard the AIs have (reserved items, kept tools, loop
/// guard...). One model request per command and no second round: the model translates, it does not plan or carry on.
/// A running action is carried to its end (an interrupted one is resumed); "/do stop" ends it. Every command is kept per
/// save (data/do_history_*.json): the model sees the recent ones (so "there" or "the middle of it" work), "/do history"
/// lists them and "/do again [N]" runs one again from its kept tool calls, without asking the model.
/// </summary>
public sealed class CommandBrain
{
    private const int MaxCalls = 5, MaxResumes = 3, MaxParts = 30, ChatChars = 280, Kept = 200, InPrompt = 5, Listed = 10;
    private static readonly string[] StopWords = { "stop", "cancel", "หยุด", "ยกเลิก" };
    private static readonly string[] HistoryWords = { "history", "list", "ประวัติ", "รายการ" };
    private static readonly Regex AgainWords = new(@"^(?:again|repeat|ซ้ำ|ทำซ้ำ|อีกครั้ง|#)\s*#?\s*(\d*)$", RegexOptions.IgnoreCase);
    private static readonly ConcurrentQueue<(string text, Color color, bool hud)> Chat = new();
    private static IMonitor? _log;

    private readonly PlayerSlot _slot;
    private readonly ILlmProvider _llm;
    private readonly IMonitor _mon;
    private readonly IModHelper _helper;
    private readonly Dictionary<string, ToolDef> _tools;
    private readonly List<LlmTool> _llmTools;
    private readonly object _gate = new();
    private List<DoRecord> _history = new();
    private string _historyFile = "";
    private CancellationTokenSource? _cts;
    private volatile bool _stopped;

    public string Provider => _llm.Name;
    public string Model => _llm.Model;
    public bool Busy => _cts != null;

    /// <summary>The command or queue step being done, for the on-screen panel (Hud); stale once <see cref="Busy"/> is false.</summary>
    public volatile string? NowDoing;

    /// <summary>Steps waiting in the queue after the one being done.</summary>
    public int QueueLeft => Math.Max(0, QueueCount - (Busy && _inQueue && QueueCount > 0 ? 1 : 0));
    private volatile bool _inQueue;

    /// <summary>The tool the farmer is running now, or null.</summary>
    public string? CurrentTool => _slot.Runtime.CurrentTool;

    /// <summary>The model is turning a command into tool calls: <see cref="WorldClock"/> holds the clock meanwhile.</summary>
    public volatile bool Translating;

    public CommandBrain(PlayerSlot slot, ILlmProvider llm, IMonitor monitor, IModHelper helper, Dictionary<string, ToolDef> tools, IEnumerable<LlmTool> llmTools)
    {
        _slot = slot;
        _llm = llm;
        _mon = monitor;
        _log = monitor;
        _helper = helper;
        _tools = tools;
        // think / journal / name are for an AI's own mind and paperwork, not for orders.
        _llmTools = llmTools.Where(t => t.Name is not ("think" or "name" or "journal")).ToList();
        LoadHistory();
        LoadQueue();
    }

    // ------------------------------------------------------------------ chat

    /// <summary>
    /// Chat lines are queued from any thread and shown on the host screen's tick, and go to the SMAPI console. With
    /// <paramref name="hud"/> the line also pops up on screen: alone, the chat box's lines fade before you look at them.
    /// </summary>
    public static void Say(string text, Color? color = null, bool hud = false)
    {
        text = text.Length > ChatChars ? text.Substring(0, ChatChars) + "…" : text;
        Chat.Enqueue((text, color ?? new Color(60, 120, 200), hud));
        _log?.Log("/do " + text, LogLevel.Info);
    }

    private static readonly ConcurrentQueue<Action> OnGameThread = new();

    /// <summary>Run <paramref name="f"/> on the game thread (the next host tick) and wait for its answer.</summary>
    private static Task<T> OnGame<T>(Func<T> f)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnGameThread.Enqueue(() =>
        {
            try { done.TrySetResult(f()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    /// <summary>Game thread, host screen: put the queued lines into the chat box (and the HUD).</summary>
    public static void Tick()
    {
        while (OnGameThread.TryDequeue(out var act)) act();
        Brains.Commands?.QueueTick();
        while (Chat.TryDequeue(out var line))
        {
            Game1.chatBox?.addMessage(line.text, line.color);
            if (line.hud && Context.IsWorldReady)
                Game1.addHUDMessage(new HUDMessage(line.text.Length > 90 ? line.text.Substring(0, 90) + "…" : line.text, line.color == Color.Red || line.color == Color.OrangeRed ? 3 : 2));
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>A command from the chat box (game thread).</summary>
    public void Do(string text)
    {
        text = text.Trim();
        _mon.Log($"/do command: {text}", LogLevel.Info); // the /do line has no chat trace of its own
        if (text.Length > 0) RememberTyped(text);
        if (text.Length == 0)
        {
            Say("/do <what to do>, e.g. /do water the crops, /do รดน้ำผัก. /do history lists the past ones, /do again [N] repeats one, /do stop ends the running one.");
            return;
        }
        if (StopWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            Stop();
            return;
        }
        if (HistoryWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            ShowHistory();
            return;
        }
        if (QueueWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase))) { ShowQueue(); return; }
        if (SkipWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase))) { Skip(); return; }
        if (ClearQueueWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            lock (_gate) { _queue.Clear(); _deferred.Clear(); }
            SaveQueue();
            Say("The queue is empty." + (Busy ? " The step running now still finishes (/do stop ends it)." : ""), hud: true);
            return;
        }
        if (ResumeWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            if (Busy) Say("Already running: the queue carries on by itself.");
            else if (QueueCount == 0) Say("The queue is empty.");
            else StartQueue();
            return;
        }
        // "clear area 32*32 from 76 17 > chop_trees > till_area > plant > water_all": a queue, step after step, across days.
        var steps = text.Split(new[] { '>', '→' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        // A queue (steps joined with >), or anything typed while something runs: added after what is there (asked
        // 2026-11-08: "add to the queue after what it is doing"). "> water_all" adds one step.
        if (steps.Count > 1 || Busy || text.StartsWith(">") || text.StartsWith("→"))
        {
            if (steps.Count == 0) return;
            _queuePaused = false; // queued on purpose: they run
            lock (_gate) _queue.AddRange(steps);
            SaveQueue();
            Say(Busy
                ? $"Added {steps.Count} step(s) after what runs now ({QueueCount} in the queue). /do queue shows it."
                : $"Queued {steps.Count} step(s) ({QueueCount} in the queue). /do queue shows it, /do clear queue drops it.", hud: true);
            if (!Busy) StartQueue();
            return;
        }

        DoRecord? again = null;
        if (AgainWords.Match(text) is { Success: true } m)
        {
            lock (_gate)
                again = m.Groups[1].Value.Length > 0
                    ? _history.FirstOrDefault(r => r.N == int.Parse(m.Groups[1].Value))
                    : _history.LastOrDefault(r => r.Calls.Count > 0);
            if (again == null || again.Calls.Count == 0)
            {
                Say(m.Groups[1].Value.Length > 0 ? $"No command #{m.Groups[1].Value} in the history (/do history)." : "Nothing to repeat yet.", Color.OrangeRed, hud: true);
                return;
            }
        }

        var cts = _cts = new CancellationTokenSource();
        _stopped = false;
        var record = new DoRecord { Command = again?.Command ?? text, Date = $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year} {WorldText.FormatTime(Game1.timeOfDay)}" };
        NowDoing = record.Command;
        string context = again == null ? Prompt(text) : "";
        Task.Run(async () =>
        {
            try
            {
                List<(string name, string json)>? calls = again != null
                    ? again.Calls.Select(c => (c.Tool, c.Args)).ToList()
                    : BareStep(text) ?? await Translate(context, cts.Token);
                if (calls == null) return;
                if (again != null) Say($"again #{again.N}: {again.Command}", Color.Gray, hud: true);
                record.Calls = calls.Select(c => new DoCall { Tool = c.name, Args = c.json }).ToList();
                Remember(record);
                if (!await Execute(record, calls, cts.Token) && _waitUntil is int at && at > Game1.timeOfDay && !_stopped)
                    Defer(record.Command, at);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Say("/do failed: " + ex.Message, Color.Red, hud: true);
                _mon.Log($"/do failed: {ex}", LogLevel.Error);
            }
            finally
            {
                SaveHistory();
                cts.Dispose();
                _cts = null;
                _preempt = false; // a timed step that cut in is first in the queue now
                AgentTools.ClearFishStop(_slot.Runtime);
                // Steps added while this one ran go on now; a queue that stopped on a problem stays stopped (2026-11-08: a
                // failed "accept quest" step came back after every command).
                if (!_stopped && QueueCount > 0 && !_queuePaused) StartQueue();
            }
        });
    }

    public void Stop()
    {
        if (!Busy)
        {
            Say("Nothing is running.");
            return;
        }
        _stopped = true;
        _slot.Runtime.Interrupt("Stopped by the player.");
        _cts?.Cancel();
        Say("Stopped.", Color.OrangeRed);
    }

    // ------------------------------------------------------------------ queue

    private static readonly string[] SkipWords = { "skip", "next", "ข้าม", "ข้ามคิว", "ข้ามขั้น", "ข้ามไป" };
    /// <summary>The player skipped the step being done: it is dropped (cancelled), the queue goes on with the next.</summary>
    private volatile bool _skip;

    private void Skip()
    {
        if (Busy && _inQueue)
        {
            _skip = true;
            _slot.Runtime.Interrupt("Skipped by the player.");
            Say($"Skipped \"{NowDoing}\": it is cancelled, the queue goes on.", Color.OrangeRed, hud: true);
            return;
        }
        if (Busy) { Stop(); return; } // a single command: skipping it is stopping it
        string? dropped;
        lock (_gate) { dropped = _queue.FirstOrDefault(); if (dropped != null) _queue.RemoveAt(0); }
        if (dropped == null) { Say("Nothing to skip."); return; }
        SaveQueue();
        Say($"Skipped \"{dropped}\": it is cancelled.", Color.OrangeRed, hud: true);
        if (QueueCount > 0) { _queuePaused = false; StartQueue(); }
    }

    /// <summary>A skip came: the queue's first step (the one being done) is dropped. True when it did.</summary>
    private bool TakeSkip(string step)
    {
        if (!_skip) return false;
        _skip = false;
        lock (_gate) if (_queue.Count > 0 && _queue[0] == step) _queue.RemoveAt(0);
        SaveQueue();
        return true;
    }

    private static readonly string[] QueueWords = { "queue", "คิว", "ดูคิว" };
    private static readonly string[] ClearQueueWords = { "clear queue", "queue clear", "ล้างคิว", "ลบคิว" };
    private static readonly string[] ResumeWords = { "continue", "resume", "ต่อ", "ทำต่อ" };
    /// <summary>
    /// Bed only after midnight (asked 2026-11-08): a step starts until 24:00; one the evening stops (the mines close at
    /// 22:00...) waits for tomorrow, and the farmer is free until midnight, then goes to bed.
    /// </summary>
    private const int QueueSleepTime = 2400, QueueLateTime = 2100, StepDays = 3;
    private readonly List<string> _queue = new();
    private string _queueFile = "";
    private bool _queueAutoResume;
    private int _stepTries;

    private int QueueCount { get { lock (_gate) return _queue.Count; } }

    // Steps that must wait for a time today (a fish's hours, a shop's opening): they start by themselves then (QueueTick).
    private readonly List<(int at, string step)> _deferred = new();
    /// <summary>A step with a set time came while another ran: that one stops where it is and is done again after.</summary>
    private volatile bool _preempt;
    /// <summary>The queue stopped with steps left: only /do continue, new queued steps or a timed step start it again.</summary>
    private volatile bool _queuePaused;
    /// <summary>Set by Execute when a call failed only because it is too early: the time it can start (HHMM).</summary>
    private int? _waitUntil;
    private static readonly Regex WaitUntilText = new(@"\[wait_until (\d{3,4})\]|opens at (\d{1,2}):(\d{2})", RegexOptions.IgnoreCase);

    /// <summary>The steps waiting for their time, for the panel.</summary>
    public List<(int at, string step)> Waiting { get { lock (_gate) return _deferred.ToList(); } }

    private void Defer(string step, int at)
    {
        lock (_gate) _deferred.Add((at, step));
        Say($"\"{step}\" can't start before {WorldText.FormatTime(at)}. Want something done first? Type it now; at {WorldText.FormatTime(at)} it starts by itself.", new Color(60, 120, 200), hud: true);
    }

    private void LoadQueue()
    {
        // Loading a save starts clean: a queue left from last time is dropped, nothing carries on by itself (asked 2026-11-08).
        _queueFile = $"data/do_queue_{(string.IsNullOrEmpty(Constants.SaveFolderName) ? "nosave" : Constants.SaveFolderName)}.json";
        _queue.Clear();
        _queueAutoResume = false;
        SaveQueue();
    }

    private void SaveQueue()
    {
        try
        {
            List<string> copy;
            lock (_gate) copy = _queue.ToList();
            _helper.Data.WriteJsonFile(_queueFile, copy);
        }
        catch (Exception ex) { _mon.Log($"/do queue could not be saved: {ex.Message}", LogLevel.Warn); }
    }

    private void ShowQueue()
    {
        List<string> copy;
        lock (_gate) copy = _queue.ToList();
        if (copy.Count == 0) { Say("The queue is empty."); return; }
        for (int i = 0; i < copy.Count; i++) Say($"{i + 1}. {copy[i]}{(i == 0 && Busy ? "  (now)" : "")}");
        Say("/do continue runs it, /do clear queue drops it.", Color.Gray);
    }

    /// <summary>Game thread: a queue left from last time (the game was closed) carries on once the farmer can move.</summary>
    internal void QueueTick()
    {
        // A waiting step whose time came: next in line (right after what runs now), and the queue starts if idle.
        (int at, string step)? due = null;
        lock (_gate)
        {
            int i = _deferred.FindIndex(d => Game1.timeOfDay >= d.at && Game1.timeOfDay < QueueSleepTime);
            if (i >= 0) { due = _deferred[i]; _deferred.RemoveAt(i); }
        }
        if (due is { } d0)
        {
            // The step with a set time goes first, now: what runs is put on hold right after it, or the time would pass
            // (asked 2026-11-08). In a queue the held step is still the queue's next; a single command goes back in line.
            lock (_gate)
            {
                if (Busy && !_inQueue && NowDoing is { Length: > 0 } single) _queue.Insert(0, single);
                _queue.Insert(0, d0.step);
            }
            SaveQueue();
            _queuePaused = false;
            if (Busy)
            {
                _preempt = true;
                // Fishing: the fish on the line is landed first, then the farmer goes (asked 2026-11-08).
                if (AgentTools.IsFishing(_slot.Runtime))
                {
                    AgentTools.RequestFishStop(_slot.Runtime);
                    Say($"It is {WorldText.FormatTime(Game1.timeOfDay)}: \"{d0.step}\" is next, right after the fish in hand; \"{NowDoing}\" carries on after it.", hud: true);
                }
                else
                {
                    _slot.Runtime.Interrupt($"\"{d0.step}\" has its time now and goes first; this carries on after it.");
                    Say($"It is {WorldText.FormatTime(Game1.timeOfDay)}: \"{d0.step}\" goes first; \"{NowDoing}\" waits and carries on after it.", hud: true);
                }
            }
            else
            {
                Say($"It is {WorldText.FormatTime(Game1.timeOfDay)}: \"{d0.step}\" starts now.", hud: true);
                StartQueue();
            }
            return;
        }
        if (!_queueAutoResume || Busy || !Context.IsPlayerFree || Game1.timeOfDay >= QueueSleepTime) return;
        _queueAutoResume = false;
        Say($"Carrying on with the queue ({QueueCount} step(s) left).", hud: true);
        StartQueue();
    }

    private void StartQueue()
    {
        var cts = _cts = new CancellationTokenSource();
        _stopped = false;
        _stepTries = 0;
        _inQueue = true;
        Task.Run(async () =>
        {
            try { await RunQueue(cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Say("/do queue failed: " + ex.Message, Color.Red, hud: true);
                _mon.Log($"/do queue failed: {ex}", LogLevel.Error);
            }
            finally
            {
                SaveHistory();
                SaveQueue();
                cts.Dispose();
                _inQueue = false;
                _queuePaused = QueueCount > 0; // steps left when the runner ends: it paused (a problem, or /do stop)
                _cts = null;
            }
        });
    }

    /// <summary>
    /// The queue's steps in order, each translated and done like a /do command. A step that is not done when the evening
    /// comes is tried again after a night's sleep; past 22:00 the farmer goes to bed and the queue goes on in the morning.
    /// </summary>
    private async Task RunQueue(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_stopped)
        {
            string? step;
            int left;
            lock (_gate) { step = _queue.FirstOrDefault(); left = _queue.Count; }
            if (step == null) { Say("The queue is done.", new Color(40, 140, 60), hud: true); return; }
            if (TakeSkip(step)) continue; // skipped while it waited for the night or the morning
            if (Game1.timeOfDay >= QueueSleepTime)
            {
                if (!await SleepNight(ct)) return;
                continue;
            }
            Say($"queue ({left} left): {step}", Color.Gray, hud: true);
            NowDoing = step;
            bool done = false;
            // Day 65: "clear area ... " from the Beach became travel_to(Farm) alone and counted as done. A step that only got
            // there (travel, a closed menu, a look) is translated once more from where the farmer now stands.
            for (int pass = 0; pass < 2; pass++)
            {
                var calls = BareStep(step) ?? await Translate(await OnGame(() => Prompt(step)), ct);
                if (calls == null) { Say($"Queue paused: \"{step}\" became no action. /do continue tries it again, /do clear queue drops it.", Color.OrangeRed, hud: true); return; }
                var record = new DoRecord { Command = step, Date = await OnGame(() => $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year} {WorldText.FormatTime(Game1.timeOfDay)}") };
                record.Calls = calls.Select(c => new DoCall { Tool = c.name, Args = c.json }).ToList();
                Remember(record);
                done = await Execute(record, calls, ct);
                SaveHistory();
                if (!done || !OnlyGettingThere(calls) || TravelStep.IsMatch(step) || LookWords.IsMatch(step)) break;
                done = false;
                if (pass == 0) Say($"\"{step}\" only got there: now the step itself.", Color.Gray);
            }
            if (_stopped || ct.IsCancellationRequested) return;
            // The player skipped it: cancelled, on to the next.
            if (TakeSkip(step)) continue;
            // A step with a set time cut in: it is first in the queue now; this one waits behind it.
            if (_preempt) { _preempt = false; AgentTools.ClearFishStop(_slot.Runtime); continue; }
            if (done)
            {
                lock (_gate) if (_queue.Count > 0 && _queue[0] == step) _queue.RemoveAt(0);
                _stepTries = 0;
                SaveQueue();
                continue;
            }
            // Not its time yet (the fish bites from 18:00, the shop opens at 9:00): it waits for that time, the rest goes on.
            if (_waitUntil is int at && at > Game1.timeOfDay)
            {
                lock (_gate) if (_queue.Count > 0 && _queue[0] == step) _queue.RemoveAt(0);
                SaveQueue();
                Defer(step, at);
                continue;
            }
            // Not finished: in the evening that is the day running out (sleep, then the same step again); otherwise a problem.
            if (Game1.timeOfDay >= QueueLateTime && ++_stepTries < StepDays)
            {
                Say($"\"{step}\" is not finished: it goes on tomorrow.", Color.Gray, hud: true);
                if (!await SleepNight(ct)) return;
                continue;
            }
            Say($"Queue paused at \"{step}\": it did not finish. /do continue tries it again, /do clear queue drops the queue.", Color.OrangeRed, hud: true);
            return;
        }
    }

    private static readonly Regex FishStep = new(
        @"^(?:go\s+)?(?:fish|fishing|ตกปลา|ไปตกปลา)(?:\s*(?:until|till|to|ถึง|จนถึง|จน)\s*(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm|โมง|น\.?)?)?$",
        RegexOptions.IgnoreCase);

    private static readonly Regex GiftStep = new(
        @"^(?:give|ให้)\s*(?:(?!(?:a|an|the)\s)(?<who>[A-Za-z]+)\s+)?(?:(?:a|an|the)\s+)?(?:gift|ของขวัญ|ของ)(?:\s*(?:to|กับ|แก่)?\s*(?<who2>[A-Za-z]+))?\s*$|^gift\s+(?<who2>[A-Za-z]+)\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex ScheduleStep = new(
        @"^(?:show\s+)?(?:schedules?|ตารางเวลา(?:ทั้งหมด)?|ตาราง)(?:\s+(?:of\s+)?(?<n>[A-Za-z]+))?\s*$|^(?<n>[A-Za-z]+)(?:'s)?\s+schedule$",
        RegexOptions.IgnoreCase);

    private static readonly Regex AcceptQuestStep = new(
        @"^(?:go\s+)?(?:accept|receive|take|get|pick\s+up|รับ|ไปรับ)\s*(?:a\s+|the\s+|today'?s\s+)?(?:new\s+)?(?:quest|request|help\s+wanted|เควส|เควสต์|งาน)(?:\s+(?:at|from|on)\s+(?:the\s+)?board|\s*(?:ที่|จาก)?\s*บอร์ด)?\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex QuestStep = new(
        @"^(?:(?:do|finish|complete|ทำ|ไปทำ)\s*)?(?:(?:the\s+)?quest|เควส|เควสต์)\s*(?<a>[A-Za-z][A-Za-z:' ]*)?$|^(?:do\s+)?(?:the\s+)?(?<b>[A-Za-z][A-Za-z:' ]*?)\s+quest$",
        RegexOptions.IgnoreCase);

    private static readonly Regex FishHowTo = new(
        @"^(?:how\s+(?:to|do\s+i|can\s+i)|where\s+(?:to|do\s+i|can\s+i)|วิธี|ทำยังไง|ที่ไหน)\s*(?:fish|catch|ตกปลา|ตก|จับ)(?:\s+(?:for\s+)?(?<fish>[A-Za-z][A-Za-z' ]*))?\s*\??$",
        RegexOptions.IgnoreCase);

    private static readonly Regex FishNamed = new(
        @"^(?:go\s+)?(?:fish|fishing|catch|ตกปลา|ไปตกปลา|ตก|จับ)(?:\s+(?!until\b|till\b|to\b|ถึง|จน|at\b|ที่)(?<fish>[A-Za-z][A-Za-z' ]*?))?(?:\s+(?:at|in|ที่)\s*(?<place>[A-Za-z]+))?\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TravelStep = new(@"\b(go|travel|walk|head|return)\b|ไป|เดิน|กลับ", RegexOptions.IgnoreCase);
    private static readonly string[] AreaTools = { "clear_area", "till_area", "chop_trees" };

    /// <summary>Calls that only move the farmer, close a menu or look: they do not do a step by themselves.</summary>
    private bool OnlyGettingThere(List<(string name, string json)> calls) =>
        calls.All(c => c.name is "travel_to" or "walk_to" or "close_menu" || _tools.TryGetValue(c.name, out var t) && t.Instant);

    /// <summary>
    /// A step that is a bare tool name runs without the model: the area tools on the newest command's area (day 65: a bare
    /// "chop_trees" was translated to chop_trees() and felled trees elsewhere), plant with all the seeds, buy seeds with this
    /// season's. Null for anything else.
    /// </summary>
    private List<(string name, string json)>? BareStep(string step)
    {
        var s = step.Trim().ToLowerInvariant().Replace(' ', '_');
        const string reason = "\"reason\":\"the player's queued step\"";
        if (s is "plant" or "plant_all" or "plant_seeds") return new() { ("plant", $"{{\"item\":\"all\",{reason}}}") };
        if (s is "buy_seeds" or "buy_seed" or "seeds") return new() { ("buy", $"{{\"item\":\"seeds\",{reason}}}") };
        // water_all is the whole map unless an area is named (2026-11-08): a bare one never takes the last area.
        if (s is "water_all" or "water" or "watering" or "รดน้ำ" or "รดน้ำผัก" or "รดน้ำทั้งหมด") return new() { ("water_all", $"{{{reason}}}") };
        // "schedule Abigail", "ตารางเวลา Abigail": her whole schedule file; "schedules", "ตารางเวลา": everyone's today.
        if (ScheduleStep.Match(step.Trim()) is { Success: true } sc)
        {
            var who = sc.Groups["n"].Value.Trim();
            return new() { ("villagers", who.Length > 0 ? $"{{\"name\":{JsonSerializer.Serialize(who)},\"schedule\":true}}" : "{\"schedule\":true}") };
        }
        // "give someone a gift", "gift Abigail", "ให้ของขวัญ Haley": the best gift the farmer carries, to them or the nearest
        // villager who can still take one today (2026-11-08: the model guessed Weeds for Sebastian).
        if (GiftStep.Match(step.Trim()) is { Success: true } g)
        {
            var who = (g.Groups["who"].Value + g.Groups["who2"].Value).Trim();
            if (who.Length == 0 || who is "someone" or "anyone" or "somebody") who = "nearest";
            return new() { ("talk_to", $"{{\"name\":{JsonSerializer.Serialize(who)},\"gift_item\":\"best\",{reason}}}") };
        }
        // "accept quest", "receive quest at board", "รับเควส": today's Help Wanted request (2026-11-08: it became do_quest("accept")).
        if (AcceptQuestStep.IsMatch(step.Trim())) return new() { ("accept_quest", $"{{{reason}}}") };
        // "delivery quest", "quest Introductions", "ทำเควส ส่งของ": the quest in the journal whose title fits.
        if (QuestStep.Match(step.Trim()) is { Success: true } q)
        {
            var title = (q.Groups["a"].Value + q.Groups["b"].Value).Trim();
            return new() { ("do_quest", title.Length > 0 ? $"{{\"quest\":{JsonSerializer.Serialize(title)},{reason}}}" : $"{{{reason}}}") };
        }
        // "fishing", "fishing until 10am", "ตกปลาถึง 10 โมง": where the farmer is, no trip (day 1: the model read an old
        // "no rod" result and walked to Willy with a rod in the pack).
        // "how to fish Anchovy", "วิธีตก Salmon": only then the fish guide (where and when it bites), nothing is fished.
        if (FishHowTo.Match(step.Trim()) is { Success: true } how)
            return new() { ("fish_guide", how.Groups["fish"].Value.Trim() is { Length: > 0 } n ? $"{{\"name\":{JsonSerializer.Serialize(n)}}}" : "{}") };
        // "fish Anchovy", "ตกปลา Salmon ที่ Beach", "fishing at Beach": the fish tool goes where that fish bites today.
        if (FishNamed.Match(step.Trim()) is { Success: true } named)
        {
            var target = named.Groups["fish"].Value.Trim();
            var place = named.Groups["place"].Value.Trim();
            var args = new List<string> { "\"tries\":5" };
            if (target.Length > 0) args.Add($"\"fish\":{JsonSerializer.Serialize(target)}");
            if (place.Length > 0) args.Add($"\"place\":{JsonSerializer.Serialize(place)}");
            return new() { ("fish", $"{{{string.Join(",", args)},{reason}}}") };
        }
        if (FishStep.Match(step.Trim()) is { Success: true } f)
        {
            if (f.Groups[1].Value.Length == 0) return new() { ("fish", $"{{\"tries\":5,{reason}}}") };
            int hour = int.Parse(f.Groups[1].Value), min = f.Groups[2].Value.Length > 0 ? int.Parse(f.Groups[2].Value) : 0;
            if (f.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour < 12) hour += 12;
            if (f.Groups[3].Value.Equals("am", StringComparison.OrdinalIgnoreCase) && hour == 12) hour = 0;
            if (hour <= 5) hour += 24; // past midnight: the game's clock goes on to 2:00 AM as 2600
            return new() { ("fish", $"{{\"tries\":5,\"until\":{hour * 100 + Math.Min(59, min)},{reason}}}") };
        }
        // The usual words for the area tools, any language: "till soil", "ขุดดิน", "cut trees", "เคลียร์พื้นที่".
        s = s switch
        {
            "till" or "till_soil" or "till_the_soil" or "hoe" or "ขุดดิน" or "พรวนดิน" or "ไถดิน" => "till_area",
            "chop" or "chop_tree" or "cut_trees" or "cut_tree" or "ตัดต้นไม้" or "ตัดไม้" => "chop_trees",
            "clear" or "clear_area" or "clear_ground" or "เคลียร์พื้นที่" or "เคลียพื้นที่" or "เคลียร์" => "clear_area",
            _ => s
        };
        if (!AreaTools.Contains(s)) return null;
        int[]? area;
        lock (_gate) area = _history.LastOrDefault(r => r.Area is { Length: 4 })?.Area;
        if (area == null) return new() { (s, $"{{{reason}}}") };
        return new() { (s, $"{{\"x\":{area[0]},\"y\":{area[1]},\"x2\":{area[0] + area[2] - 1},\"y2\":{area[1] + area[3] - 1},{reason}}}") };
    }

    /// <summary>go_to_bed (a person needs no journal), then wait for the morning until the farmer can move. False when it failed.</summary>
    private async Task<bool> SleepNight(CancellationToken ct)
    {
        if (!_tools.TryGetValue("go_to_bed", out var bed)) return false;
        if (Game1.timeOfDay < QueueSleepTime)
        {
            Say($"The rest of the queue waits for tomorrow; bed at {WorldText.FormatTime(QueueSleepTime)} (you are free until then).", Color.Gray, hud: true);
            NowDoing = "waiting for midnight, then bed";
            while (Game1.timeOfDay < QueueSleepTime && Game1.timeOfDay >= 600)
            {
                if (_stopped) return false;
                if (_preempt) { _preempt = false; return true; } // a step with a set time comes first: the queue picks it up
                if (_skip) return true; // the waiting step was skipped: the queue drops it and goes on
                await Task.Delay(1000, ct);
            }
            // The player may be in a menu or a dialogue: bed waits for a free farmer.
            for (int i = 0; i < 120 && !await OnGame(() => Context.IsPlayerFree); i++) await Task.Delay(500, ct);
        }
        Say("> go_to_bed (the queue carries on tomorrow)", Color.Gray, hud: true);
        using var doc = JsonDocument.Parse("{\"reason\":\"the day is over; the player's queue carries on tomorrow\"}");
        _slot.MarkContact();
        var pending = _slot.Runtime.EnqueueCall(bed, doc.RootElement.Clone(), observation: true);
        var (ok, text) = await pending.Done.Task.WaitAsync(ct);
        Say(FirstLines(text), ok ? new Color(40, 140, 60) : Color.OrangeRed, hud: true);
        if (!ok) { Say("Couldn't go to bed: the queue is paused. /do continue carries on.", Color.OrangeRed, hud: true); return false; }
        for (int i = 0; i < 240 && !await OnGame(() => Context.IsPlayerFree && Game1.timeOfDay < QueueLateTime); i++)
            await Task.Delay(500, ct);
        return !_stopped;
    }

    // ------------------------------------------------------------------ history

    private void LoadHistory()
    {
        _historyFile = $"data/do_history_{(string.IsNullOrEmpty(Constants.SaveFolderName) ? "nosave" : Constants.SaveFolderName)}.json";
        try { _history = _helper.Data.ReadJsonFile<List<DoRecord>>(_historyFile) ?? new List<DoRecord>(); }
        catch (Exception ex)
        {
            _mon.Log($"/do history {_historyFile} could not be read: {ex.Message}", LogLevel.Warn);
            _history = new List<DoRecord>();
        }
    }

    private void SaveHistory()
    {
        try
        {
            List<DoRecord> copy;
            lock (_gate) copy = _history.ToList();
            _helper.Data.WriteJsonFile(_historyFile, copy);
        }
        catch (Exception ex) { _mon.Log($"/do history could not be saved: {ex.Message}", LogLevel.Warn); }
    }

    private void Remember(DoRecord record)
    {
        lock (_gate)
        {
            record.N = _history.Count == 0 ? 1 : _history[^1].N + 1;
            _history.Add(record);
            if (_history.Count > Kept) _history.RemoveRange(0, _history.Count - Kept);
        }
    }

    /// <summary>The commands typed before, oldest first, each once: Up and Down in the /do line bring them back.</summary>
    internal List<string> RecentCommands()
    {
        // The lines as typed (a whole queue "a > b > c" is one line), then the single steps kept in the history.
        List<string> typed;
        try { typed = _helper.Data.ReadJsonFile<List<string>>(TypedFile) ?? new List<string>(); }
        catch (Exception) { typed = new List<string>(); }
        lock (_gate)
            return _history.Select(r => r.Command).Concat(typed).Reverse().Distinct().Take(50).Reverse().ToList();
    }

    private string TypedFile => _historyFile.Replace("do_history_", "do_typed_");

    /// <summary>Keep a typed line (the last 50) so Up in the /do line brings back a whole queue as it was typed.</summary>
    private void RememberTyped(string text)
    {
        try
        {
            var typed = _helper.Data.ReadJsonFile<List<string>>(TypedFile) ?? new List<string>();
            typed.Remove(text);
            typed.Add(text);
            if (typed.Count > 50) typed.RemoveRange(0, typed.Count - 50);
            _helper.Data.WriteJsonFile(TypedFile, typed);
        }
        catch (Exception ex) { _mon.Log($"/do typed lines could not be kept: {ex.Message}", LogLevel.Trace); }
    }

    private void ShowHistory()
    {
        List<DoRecord> last;
        lock (_gate) last = _history.Where(r => r.Calls.Count > 0).TakeLast(Listed).ToList();
        if (last.Count == 0) { Say("No commands yet."); return; }
        foreach (var r in last)
            Say($"#{r.N} {r.Command}  ->  {string.Join(", ", r.Calls.Select(c => c.Tool))}");
        Say("/do again N repeats one (no model needed).", Color.Gray);
    }

    private static string AreaText(int[] a) =>
        $"({a[0]},{a[1]}) to ({a[0] + a[2] - 1},{a[1] + a[3] - 1}), center tile ({a[0] + a[2] / 2},{a[1] + a[3] / 2})";

    /// <summary>Where the farmer is, the recent commands with what they did and their areas, and the command. Game thread.</summary>
    private string Prompt(string command)
    {
        var p = Game1.player;
        var sb = new StringBuilder(
            $"Farmer {p.Name} is at {Game1.currentLocation?.Name} ({p.TilePoint.X},{p.TilePoint.Y}), {WorldText.FormatTime(Game1.timeOfDay)}, " +
            $"{Game1.currentSeason} {Game1.dayOfMonth}, {p.Money}g, energy {(int)p.Stamina}/{p.MaxStamina}.");
        try
        {
            var quests = AgentTools.OpenQuestLines();
            if (quests.Count > 0) sb.Append("\nQuests in the journal: ").Append(string.Join("; ", quests));
        }
        catch (Exception) { /* the journal could not be read: the command still goes */ }
        List<DoRecord> recent;
        lock (_gate) recent = _history.Where(r => r.Calls.Count > 0).TakeLast(InPrompt).ToList();
        if (recent.Count > 0)
        {
            sb.Append("\nEarlier commands (newest last):");
            foreach (var r in recent)
            {
                sb.Append($"\n#{r.N} \"{r.Command}\" ({r.Date})");
                foreach (var d in r.Did.Take(3)) sb.Append($"\n   did: {FirstLines(d)}"); // older records still hold the AIs' advice
                if (r.Area is { Length: 4 } a) sb.Append($"\n   area: {AreaText(a)}");
            }
        }
        return sb.Append($"\nCommand: {command}").ToString();
    }

    /// <summary>The area a call worked on: from its arguments (x,y + w,h or x2,y2), else from its result text.</summary>
    private static int[]? AreaOf(JsonElement args, string result)
    {
        int? Get(string k) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.TryGetInt32(out int i) ? i : null;
        // The tool says where the area really went (re-placed, or cut to the map): that comes first.
        foreach (var said in new[] { @"Only the part on the map: \((\d+),(\d+)\) to \((\d+),(\d+)\)", @"area with \(\d+,\d+\) as its [^:]*: \((\d+),(\d+)\) to \((\d+),(\d+)\)" })
            if (Regex.Match(result, said) is { Success: true } m)
            {
                int mx = int.Parse(m.Groups[1].Value), my = int.Parse(m.Groups[2].Value);
                return new[] { mx, my, int.Parse(m.Groups[3].Value) - mx + 1, int.Parse(m.Groups[4].Value) - my + 1 };
            }
        // With an anchor x,y is not the top-left corner: the tool's result says where the area went.
        bool anchored = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("anchor", out var an) && an.ValueKind == JsonValueKind.String && an.GetString() is { Length: > 0 } s && s != "top_left";
        if (!anchored && Get("x") is int x && Get("y") is int y)
        {
            if (Get("x2") is int x2 && Get("y2") is int y2)
                return new[] { Math.Min(x, x2), Math.Min(y, y2), Math.Abs(x2 - x) + 1, Math.Abs(y2 - y) + 1 };
            if (Get("w") is int w && Get("h") is int h && w > 0 && h > 0) return new[] { x, y, w, h };
        }
        var placed = Regex.Match(result, @"area with \(\d+,\d+\) as its [^:]*: \((\d+),(\d+)\) to \((\d+),(\d+)\)");
        if (placed.Success)
        {
            int ax = int.Parse(placed.Groups[1].Value), ay = int.Parse(placed.Groups[2].Value);
            return new[] { ax, ay, int.Parse(placed.Groups[3].Value) - ax + 1, int.Parse(placed.Groups[4].Value) - ay + 1 };
        }
        var rect = Regex.Match(result, @"\((\d+),(\d+)\) (\d+)x(\d+)");
        if (rect.Success)
            return new[] { int.Parse(rect.Groups[1].Value), int.Parse(rect.Groups[2].Value), int.Parse(rect.Groups[3].Value), int.Parse(rect.Groups[4].Value) };
        var around = Regex.Match(result, @"around \((\d+),(\d+)\)");
        return around.Success ? new[] { int.Parse(around.Groups[1].Value), int.Parse(around.Groups[2].Value), 1, 1 } : null;
    }

    // ------------------------------------------------------------------ translate and run

    private const string System =
        "You turn one command from the person playing Stardew Valley into tool calls for their farmer. The command may be in Thai or " +
        "English. Call the tool (or the few tools, in order) that do exactly what was asked, with the arguments it needs, and nothing " +
        "more: do not plan the day, do not add steps nobody asked for, do not look around first unless the command asks to look. " +
        "Item, place and villager names are the game's English names. Never ask the player for coordinates or details: leave " +
        "optional arguments out so the tool uses its own defaults, and pick the tool whose description fits best; the player rarely " +
        "gives tiles or coordinates, so prefer tools that work without them. For example clearing ground for planting is " +
        "clear_area() with no area (the mod picks the field), hoeing a field for the seeds you carry is till_area() with no area, " +
        "watering is water_all(), harvesting is harvest_all(), getting wood is chop_trees(), going down the mines is mine(). " +
        "When the player does give tiles as \"x y to x y\" (e.g. \"clear 40 20 to 50 28\"), pass the first corner as x, y and " +
        "the second as x2, y2. A size from one tile (\"clear 10*8 from 50 20\", \"เคลียพื้นที่ 10*8 เริ่มจาก 50 20\") is " +
        "clear_area(x=50, y=20, w=10, h=8, anchor): anchor \"center\" when the player says around/รอบๆ, a corner (top_left, " +
        "top_right, bottom_left, bottom_right) only when the player names that corner (ซ้ายบน, ขวาบน, ซ้ายล่าง, ขวาล่าง), else \"auto\" " +
        "(the mod picks the placement with the most open ground): \"start\", \"from\" or \"เริ่มจาก\" alone is \"auto\", never top_left. " +
        "w and h are exactly the numbers typed (32*32 is w=32, h=32). till_area takes the same. " +
        "clear_area, till_area and chop_trees all take an area as corners (chop_trees with an area fells " +
        "every tree in it). Never call look, status or other looking tools unless the command asks to see something: the mod " +
        "finds the trees, weeds and soil itself. A chained command (\"cut the trees there, then dig, plant and water\") is " +
        "several calls in order, e.g. chop_trees(x,y,x2,y2), till_area(x,y,x2,y2), plant(item), water_all(). More: " +
        "cutting trees is chop_trees (with an area when one is named); fishing for a fish is fish(fish=\"Salmon\"), fishing at a " +
        "place is fish(place=\"Beach\"); going down the mines to a floor is mine(to_floor=40); picking up forage on this map is " +
        "forage(places=[\"<this map>\"]), around the valley forage(); a gift is talk_to(name, gift_item); watering a place is " +
        "water_all(place=\"Greenhouse\"), an area water_all(x,y,x2,y2); a scarecrow where the ground around is open is " +
        "place(item=\"Scarecrow\") with no tiles (the mod picks the spot), at a named spot place(item, tiles=[[x,y]]); buying " +
        "this season's seeds is buy(item=\"seeds\") (the mod goes to Pierre's, picks the best seeds and the amount); planting " +
        "all the seeds, or planting where the soil is already dug, is plant(item=\"all\") with no tiles, or plant(item=\"<seed name>\") " +
        "when one is named. A command that is only a tool name (\"chop_trees\", \"till_area\", \"plant\") is that " +
        "tool, on the newest earlier command's area when the tool takes an area (as x, y, x2, y2), plant with item=\"all\". " +
        "water_all waters the whole map; it takes an area only when the command names one. " +
        "The earlier commands' results may be out of date (the player buys, gets and stores things meanwhile): never travel to " +
        "a shop or a person, and never fetch anything first, unless the command says so; the tools fetch what they need " +
        "themselves and say when something is missing. Fishing until a time is fish(until=HHMM), e.g. until 10am is until=1000. " +
        "Planting trees or tree seeds (\"plant trees\", \"ปลูกต้นไม้\") is plant(item=\"Acorn\" / \"Maple Seed\" / \"Pine Cone\") " +
        "or chop_trees(replant=true); plant(item=\"all\") is crop seeds only. Area tools go to the Farm themselves: no travel_to " +
        "before them. Doing a quest (\"do quest ...\", \"ทำเควส ...\") is do_quest(quest=\"<its title from the journal " +
        "list below>\"): match the player's words (any language, e.g. แนะนำตัว = Introductions) to the closest title, empty when " +
        "none is named. The earlier commands are listed with what they did and their areas: words like \"there\", " +
        "\"the same place\", \"the middle of what you just dug\" or \"like #3\" mean those, the newest unless a number is given; " +
        "use an area's center tile for its middle (e.g. place(item=\"Scarecrow\", tiles=[[cx,cy]])). " +
        "Only when no tool can do it at all, call no tool and say so in one short sentence in the command's language.";

    private const string Retry =
        "Call a tool now. Do not ask questions: use the closest tool and leave out what the command did not say.";

    private const string LookRetry =
        "Looking does not do the command. Call the tools that do it now (not look or status); the mod finds the tiles itself.";

    private bool OnlyLooks(IEnumerable<LlmToolCall> calls) => calls.All(c => _tools.TryGetValue(c.Name, out var t) && t.Instant);

    private static readonly Regex LookWords = new(@"\b(look|see|show|status|check|inventory|what|where|how many|how to|how do|list|guide|info)\b|ดู|เช็ค|สถานะ|กระเป๋า|มีอะไร|อะไรบ้าง|ที่ไหน|เท่าไหร่|กี่|วิธี|ยังไง|อย่างไร|\?",
        RegexOptions.IgnoreCase);

    /// <summary>The command (the last line of the request) asks to see something, so looking tools are the answer.</summary>
    private static bool AsksToLook(string context) => LookWords.IsMatch(context[(context.LastIndexOf("\nCommand: ", StringComparison.Ordinal) + 1)..]);

    /// <summary>The command as tool calls (name, JSON arguments), or null when the model found none.</summary>
    private async Task<List<(string name, string json)>?> Translate(string context, CancellationToken ct)
    {
        Say("…", Color.Gray);
        LlmResponse reply;
        Translating = true;
        try
        {
            var messages = new List<ChatMessage> { ChatMessage.User(context) };
            reply = await _llm.Complete(System, messages, _llmTools, ct);
            if (reply.ToolCalls.Count == 0)
            {
                // A small model likes to ask where or how much: ask once more for the call itself, with the tools' defaults.
                _mon.Log($"/do: no tool call, the model said: {reply.Text.Trim()}", LogLevel.Debug);
                messages.Add(ChatMessage.Assistant(reply.Text, Array.Empty<LlmToolCall>()));
                messages.Add(ChatMessage.User(Retry));
                reply = await _llm.Complete(System, messages, _llmTools, ct);
            }
            else if (OnlyLooks(reply.ToolCalls) && !AsksToLook(context))
            {
                // Day 65: "cut the trees there, then dig, plant, water" became look() alone and nothing was done.
                _mon.Log($"/do: only looked ({string.Join(", ", reply.ToolCalls.Select(c => c.Name))}): asking for the action", LogLevel.Debug);
                messages.Add(ChatMessage.User(LookRetry));
                var again = await _llm.Complete(System, messages, _llmTools, ct);
                if (again.ToolCalls.Count > 0 && !OnlyLooks(again.ToolCalls)) reply = again;
                else
                {
                    // Looking twice is not the order done (day 1: "fish Anchovy" became fish_guide and nothing was caught).
                    Say($"Couldn't turn that into an action (it only looked: {string.Join(", ", reply.ToolCalls.Select(c => c.Name))}). Say it another way, e.g. with the tool's words.", Color.OrangeRed, hud: true);
                    return null;
                }
            }
        }
        finally { Translating = false; }
        if (reply.ToolCalls.Count == 0)
        {
            Say(string.IsNullOrWhiteSpace(reply.Text) ? "No tool fits that command." : reply.Text.Trim(), Color.OrangeRed, hud: true);
            return null;
        }
        if (reply.ToolCalls.Count > MaxCalls) Say($"Only the first {MaxCalls} steps are done.", Color.OrangeRed);
        var command = context[(context.LastIndexOf("\nCommand: ", StringComparison.Ordinal) + 10)..];
        // A small model sometimes pads the name (" morning_chores", 2026-11-08): trimmed.
        var calls = reply.ToolCalls.Take(MaxCalls).Select(c => (c.Name.Trim(), TypedSize(command, LlmHttp.ArgsOrEmpty(c.ArgumentsJson)))).ToList();
        // Day 1 (Manual): "go fishing" without a rod became wait(170) until Willy's shop opened: the model planning ahead.
        // Waiting happens only when the player asks for it.
        if (!WaitWords.IsMatch(command) && calls.RemoveAll(c => c.Item1 == "wait") > 0)
        {
            _mon.Log("/do: dropped a wait the player did not ask for", LogLevel.Debug);
            if (calls.Count == 0)
            {
                Say("That can't be done right now, and waiting was not asked for: " + (reply.Text.Trim().Length > 0 ? reply.Text.Trim() : "try again later."), Color.OrangeRed, hud: true);
                return null;
            }
        }
        return calls;
    }

    private static readonly Regex WaitWords = new(@"\b(wait|until|later|pass time)\b|รอ|จนถึง|ถึงเวลา", RegexOptions.IgnoreCase);

    private static readonly Regex SizeTyped = new(@"(\d+)\s*[*xX×]\s*(\d+)");

    /// <summary>A size typed in the command ("32*32") wins over the w and h the model wrote (day 65: it sent h=29).</summary>
    internal static string TypedSize(string command, string json)
    {
        if (SizeTyped.Match(command) is not { Success: true } m) return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || !o.ContainsKey("w") || !o.ContainsKey("h")) return json;
            o["w"] = int.Parse(m.Groups[1].Value);
            o["h"] = int.Parse(m.Groups[2].Value);
            return o.ToJsonString();
        }
        catch (Exception) { return json; }
    }

    /// <summary>Runs the calls (and the parts of a big area). True when every one finished; false when one failed, ran out of day or was stopped.</summary>
    private async Task<bool> Execute(DoRecord record, List<(string name, string json)> calls, CancellationToken ct)
    {
        var queue = new LinkedList<(string name, string json, bool part)>(calls.Select(c => (c.name, c.json, false)));
        int parts = 0;
        bool complete = true;
        _waitUntil = null;
        while (queue.First is { } node)
        {
            queue.RemoveFirst();
            var (name, json, part) = node.Value;
            if (ct.IsCancellationRequested || _stopped || _preempt || _skip) return false;
            if (!_tools.TryGetValue(name, out var tool) || !_llmTools.Any(t => t.Name == name))
            {
                Say($"Unknown tool {name}.", Color.Red);
                return false;
            }
            JsonElement args;
            using (var doc = JsonDocument.Parse(json))
                args = doc.RootElement.Clone();
            Say($"> {name}({ShortArgs(args)})", Color.Gray, hud: !part); // the parts of a big area: their results are enough on screen

            // Carry the action to its end: an interrupt (someone talked, a gift...) resumes it; the player's stop does not.
            // Out of energy: a rest in bed (not the night's sleep), then the same step again.
            string text = "";
            bool ok = false, outOfDay = false;
            for (int attempt = 0, rests = 0; ; )
            {
                if (_preempt || _skip) return false;
                _slot.MarkContact();
                var pending = _slot.Runtime.EnqueueCall(tool, args, observation: true);
                (ok, text) = await pending.Done.Task.WaitAsync(ct);
                Say(FirstLines(text), ok ? new Color(40, 140, 60) : Color.OrangeRed, hud: true);
                if (_stopped || _preempt || _skip) return false;
                if (name != "rest" && Tired(pending.Outcome?.Reason, text))
                {
                    if (rests >= MaxRests) { Say($"Still out of energy after {MaxRests} rests: stopped here.", Color.OrangeRed, hud: true); outOfDay = true; break; }
                    if (Game1.timeOfDay >= LastRestTime) { Say("Out of energy and it is late: go to bed to end the day; /do again carries on tomorrow.", Color.OrangeRed, hud: true); outOfDay = true; break; }
                    rests++;
                    // Where the work stopped: after the rest, back there before the same step goes on (day 65: the Farm's
                    // tiles were cleared "in the FarmHouse" right after getting out of bed).
                    string? workedAt = Game1.currentLocation?.NameOrUniqueName;
                    if (!await RestInBed(ct)) break;
                    if (!await GoBack(workedAt, ct)) break;
                    continue;
                }
                if (attempt++ < MaxResumes && pending.Outcome?.Reason is Reasons.Interrupted or Reasons.Timeout) continue;
                // Stopped half way because the day ran out (22:00 in the mines, late night in the fields).
                if (pending.Outcome?.Partial == true && Game1.timeOfDay >= QueueLateTime) outOfDay = true;
                break;
            }
            if (_stopped) return false;
            // A long answer to a question (schedules, a guide, the bundles...) does not fit the chat: it opens as a letter
            // to page through.
            if (ok && tool.Instant && text.Length > ChatChars)
            {
                var letter = text;
                OnGameThread.Enqueue(() =>
                {
                    if (Game1.activeClickableMenu == null)
                        Game1.activeClickableMenu = new StardewValley.Menus.LetterViewerMenu(letter.Replace("^", " ").Replace("\n", "^"));
                });
            }
            // Too early for it ("[wait_until 1800]", "opens at 09:00"): the whole command waits for that time.
            if (!ok && WaitUntilText.Match(text) is { Success: true } w)
            {
                _waitUntil = w.Groups[1].Success ? int.Parse(w.Groups[1].Value) : int.Parse(w.Groups[2].Value) * 100 + int.Parse(w.Groups[3].Value);
                return false;
            }
            if (!ok || outOfDay) complete = false;
            lock (_gate)
            {
                if (!part) record.Did.Add($"{name}({ShortArgs(args)}) -> {FirstLines(text)}");
                if (!part && AreaOf(args, text) is { } area) record.Area = area;
            }

            // An area over the tool's size limit comes back with "The rest: clear_area(x=.., y=.., w=.., h=..)": do those parts too.
            if (ok)
                foreach (var rest in RestCalls(name, text).Reverse())
                    if (++parts <= MaxParts) queue.AddFirst((name, rest, true));
            if (outOfDay) return false; // the rest of the command waits for tomorrow
        }
        return complete;
    }

    private const int MaxRests = 3, LastRestTime = 2400;
    private static readonly Regex LowEnergyText = new(@"energy (is )?(low|\d+)|keep energy|low on energy|too tired|exhausted", RegexOptions.IgnoreCase);

    /// <summary>The step stopped because the farmer ran low on energy (its reason, or its words with the energy really low).</summary>
    private bool Tired(string? reason, string text)
    {
        if (reason == Reasons.LowEnergy) return true;
        return LowEnergyText.IsMatch(text) && _slot.State is ScreenState { MaxEnergy: > 0 } s && s.Energy <= s.MaxEnergy * 0.35;
    }

    /// <summary>
    /// After a rest: travel back to the map the step was working on. The mine's floors are left to the mine tool (it takes
    /// the elevator itself). False when the way back failed or the player stopped it.
    /// </summary>
    private async Task<bool> GoBack(string? map, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(map) || map.StartsWith("UndergroundMine", StringComparison.Ordinal) || map == "FarmHouse") return !_stopped;
        if (Game1.currentLocation?.NameOrUniqueName == map || !_tools.TryGetValue("travel_to", out var travel)) return !_stopped;
        Say($"> travel_to({map}) (back to work)", Color.Gray);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { location = map, reason = "back to where the player's order was being done" }));
        _slot.MarkContact();
        var pending = _slot.Runtime.EnqueueCall(travel, doc.RootElement.Clone(), observation: true);
        var (ok, text) = await pending.Done.Task.WaitAsync(ct);
        if (!ok) Say(FirstLines(text), Color.OrangeRed, hud: true);
        return ok && !_stopped;
    }

    /// <summary>The rest tool: walk home, lie in bed until energy is full, get up. False when it could not rest.</summary>
    private async Task<bool> RestInBed(CancellationToken ct)
    {
        if (!_tools.TryGetValue("rest", out var rest)) return false;
        Say("> rest (out of energy: a rest in bed, then carrying on)", Color.Gray, hud: true);
        using var doc = JsonDocument.Parse("{\"reason\":\"out of energy: rest in bed, then carry on with the player's order\"}");
        _slot.MarkContact();
        var pending = _slot.Runtime.EnqueueCall(rest, doc.RootElement.Clone(), observation: true);
        var (ok, text) = await pending.Done.Task.WaitAsync(ct);
        Say(FirstLines(text), ok ? new Color(40, 140, 60) : Color.OrangeRed, hud: true);
        return ok && !_stopped;
    }

    /// <summary>The "tool(x=1, y=2, w=3, h=4)" calls named after "The rest:" in a result of <paramref name="tool"/>, as JSON arguments.</summary>
    internal static IEnumerable<string> RestCalls(string tool, string text)
    {
        int at = text.IndexOf("The rest:", StringComparison.Ordinal);
        if (at < 0) yield break;
        foreach (Match m in Regex.Matches(text.Substring(at),
                     Regex.Escape(tool) + @"\(x=(-?\d+), y=(-?\d+), w=(\d+), h=(\d+)\)"))
            yield return $"{{\"x\":{m.Groups[1].Value},\"y\":{m.Groups[2].Value},\"w\":{m.Groups[3].Value},\"h\":{m.Groups[4].Value},\"reason\":\"the rest of the area the player asked for\"}}";
    }

    private static string ShortArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        var parts = args.EnumerateObject().Where(p => p.Name != "reason").Select(p => $"{p.Name}={p.Value}");
        var s = string.Join(", ", parts);
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }

    /// <summary>
    /// The first lines of a result, without the AIs' standing advice ("| Inventory pressure: ... FIRST store items ..."):
    /// kept in the history it made the model store items instead of taking a quest (2026-11-08).
    /// </summary>
    private static string FirstLines(string text)
    {
        int advice = text.IndexOf("| Inventory pressure", StringComparison.Ordinal);
        if (advice >= 0) text = text[..advice];
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2);
        return string.Join(" ", lines);
    }
}
