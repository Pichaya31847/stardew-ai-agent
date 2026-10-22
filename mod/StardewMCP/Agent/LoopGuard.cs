using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// One seat's loop guard (#31): feeds <see cref="LoopDetector"/> from the seat's tool calls, writes every hint
/// to the day log as kind "guard", and counts hints per farm day for the stats (#25: <c>Rt.Guard.HintsToday</c>).
/// Each seat's <see cref="AgentRuntime"/> owns one, so state is per player. The camera seat (P4) never
/// receives AI calls, so its guard stays idle. Runs on the seat's own game-thread tick.
/// </summary>
public sealed class LoopGuard
{
    private readonly LoopDetector _core;
    private readonly PlayerLog _log;
    private readonly IMonitor _monitor;

    /// <summary>Hints, refusals and no-progress hints on the current farm day.</summary>
    public int HintsToday => _core.HintsToday;
    /// <summary>Refused calls on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int RefusalsToday => _core.RefusalsToday;
    /// <summary>No-progress hints on the current farm day (also counted in <see cref="HintsToday"/>).</summary>
    public int NoProgressHintsToday => _core.NoProgressHintsToday;

    public LoopGuard(ModConfig config, PlayerLog log, IMonitor monitor)
    {
        _log = log;
        _monitor = monitor;
        _core = new LoopDetector(config.RepeatHintAfter, config.RepeatBlockAfter, config.NoProgressAfter, () => AgentLog.FarmDay);
    }

    /// <summary>
    /// Before a call runs: true, with the text to fail it with, when the identical call has failed too often in a row.
    /// </summary>
    public bool Refuse(PendingCall call, out string text)
    {
        text = "";
        if (!Applies(call)) return false;
        try
        {
            var note = _core.Before(LoopDetector.KeyOf(call.Tool.Name, call.Args));
            if (note == null) return false;
            LogNote(call, note);
            text = note.Text;
            return true;
        }
        catch (Exception ex)
        {
            _monitor.Log($"LoopGuard.Refuse failed: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    /// <summary>After a call finished: the hints to append to its result (empty when there are none).</summary>
    public string End(PendingCall call, bool ok, string result)
    {
        if (!Applies(call)) return "";
        try
        {
            var notes = _core.After(LoopDetector.KeyOf(call.Tool.Name, call.Args), call.Instant, ok, result, Snapshot());
            foreach (var note in notes)
                LogNote(call, note);
            return string.Join("\n", notes.Select(n => n.Text));
        }
        catch (Exception ex)
        {
            // Never let a guard bug leave an AI's call unanswered.
            _monitor.Log($"LoopGuard.End failed: {ex.Message}", LogLevel.Warn);
            return "";
        }
    }

    /// <summary>Only the AI's own calls in a loaded world. Debug ops (Mail = false) are smoke-test hooks, not play.</summary>
    private static bool Applies(PendingCall call) => Context.IsWorldReady && call.Tool.Mail;

    private void LogNote(PendingCall call, GuardNote note) =>
        _log.Add("guard", call.Tool.Name, note.Text, new { type = note.Type, count = note.Count });

    /// <summary>
    /// What "progress" means: game time (days and minutes), inventory (item names and stacks), location and tile
    /// of this seat's farmer. Any change since the previous call counts.
    /// </summary>
    private static string Snapshot()
    {
        var p = Game1.player;
        int minutes = Game1.Date.TotalDays * 1440 + Utility.ConvertTimeToMinutes(Game1.timeOfDay);
        var inventory = new HashCode();
        foreach (var item in p.Items)
        {
            inventory.Add(item?.DisplayName);
            inventory.Add(item?.Stack ?? 0);
        }
        return $"{minutes}|{inventory.ToHashCode()}|{Game1.currentLocation?.Name}|{p.TilePoint.X},{p.TilePoint.Y}";
    }
}
