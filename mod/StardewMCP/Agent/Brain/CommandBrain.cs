using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// /do for a person playing P1 (Role "human") with a Brain configured: one command typed in chat, in any language, becomes
/// the tool calls that do it, run on P1's own runtime with every guard the AIs have (reserved items, kept tools, loop
/// guard...). One model request per command and no second round: the model translates, it does not plan or carry on.
/// A running action is carried to its end (an interrupted one is resumed); "/do stop" ends it.
/// </summary>
public sealed class CommandBrain
{
    private const int MaxCalls = 5, MaxResumes = 3, ChatChars = 280;
    private static readonly string[] StopWords = { "stop", "cancel", "หยุด", "ยกเลิก" };
    private static readonly ConcurrentQueue<(string text, Color color)> Chat = new();

    private readonly PlayerSlot _slot;
    private readonly ILlmProvider _llm;
    private readonly IMonitor _mon;
    private readonly Dictionary<string, ToolDef> _tools;
    private readonly List<LlmTool> _llmTools;
    private CancellationTokenSource? _cts;
    private volatile bool _stopped;

    public string Provider => _llm.Name;
    public string Model => _llm.Model;
    public bool Busy => _cts != null;

    /// <summary>The model is turning a command into tool calls: <see cref="WorldClock"/> holds the clock meanwhile.</summary>
    public volatile bool Translating;

    public CommandBrain(PlayerSlot slot, ILlmProvider llm, IMonitor monitor, Dictionary<string, ToolDef> tools, IEnumerable<LlmTool> llmTools)
    {
        _slot = slot;
        _llm = llm;
        _mon = monitor;
        _tools = tools;
        // think / journal / name are for an AI's own mind and paperwork, not for orders.
        _llmTools = llmTools.Where(t => t.Name is not ("think" or "name" or "journal")).ToList();
    }

    /// <summary>Chat lines are queued from any thread and shown on the host screen's tick.</summary>
    public static void Say(string text, Color? color = null) =>
        Chat.Enqueue((text.Length > ChatChars ? text.Substring(0, ChatChars) + "…" : text, color ?? new Color(60, 120, 200)));

    /// <summary>Game thread, host screen: put the queued lines into the chat box.</summary>
    public static void Tick()
    {
        while (Chat.TryDequeue(out var line))
            Game1.chatBox?.addMessage(line.text, line.color);
    }

    /// <summary>A command from the chat box (game thread).</summary>
    public void Do(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            Say("/do <what to do>, e.g. /do water the crops, /do รดน้ำผัก. /do stop ends the running one.");
            return;
        }
        if (StopWords.Any(w => text.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            Stop();
            return;
        }
        if (Busy)
        {
            Say($"Still doing {_slot.Runtime.CurrentTool ?? "the last command"}: wait, or /do stop.", Color.OrangeRed);
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        _stopped = false;
        string context = Prompt(text);
        Task.Run(async () =>
        {
            try { await Run(context, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Say("/do failed: " + ex.Message, Color.Red);
                _mon.Log($"/do failed: {ex}", LogLevel.Error);
            }
            finally
            {
                cts.Dispose();
                _cts = null;
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

    /// <summary>Where the farmer is and the command, for the one model request. Game thread.</summary>
    private static string Prompt(string command)
    {
        var p = Game1.player;
        return $"Farmer {p.Name} is at {Game1.currentLocation?.Name} ({p.TilePoint.X},{p.TilePoint.Y}), {WorldText.FormatTime(Game1.timeOfDay)}, " +
               $"{Game1.currentSeason} {Game1.dayOfMonth}, {p.Money}g, energy {(int)p.Stamina}/{p.MaxStamina}.\nCommand: {command}";
    }

    private const string System =
        "You turn one command from the person playing Stardew Valley into tool calls for their farmer. The command may be in Thai or " +
        "English. Call the tool (or the few tools, in order) that do exactly what was asked, with the arguments it needs, and nothing " +
        "more: do not plan the day, do not add steps nobody asked for, do not look around first unless the command asks to look. " +
        "Item, place and villager names are the game's English names. If no tool fits or the command is unclear, call no tool and " +
        "answer in one short sentence in the command's language.";

    private async Task Run(string context, CancellationToken ct)
    {
        Say("…", Color.Gray);
        LlmResponse reply;
        Translating = true;
        try { reply = await _llm.Complete(System, new List<ChatMessage> { ChatMessage.User(context) }, _llmTools, ct); }
        finally { Translating = false; }
        if (reply.ToolCalls.Count == 0)
        {
            Say(string.IsNullOrWhiteSpace(reply.Text) ? "No tool fits that command." : reply.Text.Trim(), Color.OrangeRed);
            return;
        }
        foreach (var call in reply.ToolCalls.Take(MaxCalls))
        {
            if (ct.IsCancellationRequested || _stopped) return;
            if (!_tools.TryGetValue(call.Name, out var tool) || !_llmTools.Any(t => t.Name == call.Name))
            {
                Say($"Unknown tool {call.Name}.", Color.Red);
                return;
            }
            JsonElement args;
            using (var doc = JsonDocument.Parse(LlmHttp.ArgsOrEmpty(call.ArgumentsJson)))
                args = doc.RootElement.Clone();
            Say($"> {call.Name}({ShortArgs(args)})", Color.Gray);

            // Carry the action to its end: an interrupt (someone talked, a gift...) resumes it; the player's stop does not.
            for (int attempt = 0; ; attempt++)
            {
                _slot.MarkContact();
                var pending = _slot.Runtime.EnqueueCall(tool, args, observation: true);
                var (ok, text) = await pending.Done.Task.WaitAsync(ct);
                bool resume = !_stopped && attempt < MaxResumes && pending.Outcome?.Reason is Reasons.Interrupted or Reasons.Timeout;
                Say(FirstLines(text), ok ? new Color(40, 140, 60) : Color.OrangeRed);
                if (!resume) break;
            }
            if (_stopped) return;
        }
        if (reply.ToolCalls.Count > MaxCalls) Say($"Only the first {MaxCalls} steps were done.", Color.OrangeRed);
    }

    private static string ShortArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        var parts = args.EnumerateObject().Where(p => p.Name != "reason").Select(p => $"{p.Name}={p.Value}");
        var s = string.Join(", ", parts);
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }

    private static string FirstLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2);
        return string.Join(" ", lines);
    }
}
