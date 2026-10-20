using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// The texts the built-in AI loop sends: the system prompt (assets/system_prompt.md with its placeholders filled from
/// the seat's memory files) and the day's messages (morning, meeting end, nudges, session handoff).
/// </summary>
public static class Prompts
{
    private const int MemoryMaxLines = 60;

    private static string? _templateCache;
    private static DateTime _templateStamp;

    public static string Template(string assetDir)
    {
        var path = Path.Combine(assetDir, "system_prompt.md");
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_templateCache == null || stamp != _templateStamp)
            {
                _templateCache = File.ReadAllText(path, Encoding.UTF8);
                _templateStamp = stamp;
            }
            return _templateCache;
        }
        catch (IOException)
        {
            return _templateCache ?? "You are {MODEL_NAME}, playing Stardew Valley as {PLAYER_NAME} ({SEAT}). Use the tools; think and write in {LANGUAGE}.\n{TIME_MODE}\n{OTHERS}\n{SELF}\n{PEOPLE}\n{LESSONS}\n{PLAN}\n{NOTES}";
        }
    }

    public sealed record SystemInputs(
        string Model, string PlayerName, string Seat, string Language, bool FreezeWhileThinking,
        IReadOnlyList<(string Seat, string Name, string Kind)> Others,
        string Self, string People, string Lessons, string Plan, string Notes);

    public static string System(string template, SystemInputs i)
    {
        string timeMode = i.FreezeWhileThinking
            ? "**Game time is frozen while you think** and only moves while an action runs. Take the time to plan well, but each action costs game time."
            : "**Game time keeps running while you think**, so decide quickly and prefer batch tools (`water_all`, `harvest_all`, `clear_area`) over many small calls. Standing still costs daylight.";

        string others;
        if (i.Others.Count == 0)
            others = "You are alone on the farm.";
        else
        {
            var sb = new StringBuilder("Other players on this farm:");
            foreach (var o in i.Others)
                sb.Append($"\n- {o.Name} ({o.Seat}, {o.Kind})");
            others = sb.ToString();
        }

        return template
            .Replace("{MODEL_NAME}", i.Model)
            .Replace("{PLAYER_NAME}", i.PlayerName)
            .Replace("{SEAT}", i.Seat)
            .Replace("{LANGUAGE}", i.Language)
            .Replace("{TIME_MODE}", timeMode)
            .Replace("{OTHERS}", others)
            .Replace("{SELF}", Or(i.Self, "(nothing yet: you are new here)"))
            .Replace("{PEOPLE}", Or(i.People, "(nothing yet)"))
            .Replace("{LESSONS}", Or(Tail(i.Lessons), "(none yet)"))
            .Replace("{PLAN}", Or(i.Plan, "(no plan yet: make one this morning)"))
            .Replace("{NOTES}", Or(Tail(i.Notes), "(none yet)"));
    }

    private static string Or(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

    /// <summary>The last lines of a growing file, so lessons and notes never crowd out the rest of the prompt.</summary>
    public static string Tail(string? text, int lines = MemoryMaxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var all = text.Replace("\r\n", "\n").Split('\n');
        if (all.Length <= lines) return text.Trim();
        return "(older lines left out)\n" + string.Join("\n", all.Skip(all.Length - lines)).Trim();
    }

    public static string Morning(int day, string briefing, int? pendingJournalDay, bool meeting, int meetingCalls, IEnumerable<string> otherNames, string time)
    {
        var sb = new StringBuilder();
        sb.Append($"Good morning. It is {time} on farm day {day}.\n");
        if (!string.IsNullOrWhiteSpace(briefing))
            sb.Append('\n').Append(briefing.Trim()).Append('\n');
        if (pendingJournalDay is int pd)
            sb.Append($"\nYou passed out on day {pd} and never wrote its journal. Write it first with `journal(day={pd})` (what happened, what it cost, what you will do differently).\n");
        var names = otherNames.ToList();
        if (meeting && names.Count > 0)
        {
            sb.Append($"\n[Morning meeting] The world is frozen while you and {Join(names)} talk. You have {meetingCalls} tool calls for this (`think` is free): " +
                      "`say` what you plan to do today, read the plan board (`plan_read`), claim work on it (`plan_edit`), ask the others for help or warn them. " +
                      "Only chat, the plan board and observations work now; farm work starts after the meeting. Start with a `think` about your goals, then talk. " +
                      "When you have nothing more to say, end your turn without calling a tool.");
        }
        else
        {
            sb.Append("\nStart the day: `think` about your plan, then get to work. End the day with `journal` and `go_to_bed`.");
        }
        return sb.ToString();
    }

    public static string FreePlay(string time) =>
        $"[Meeting over] It is {time}. Free play: carry out your plan. Talk with `say` or `whisper` whenever it helps. " +
        "When the work is done or it gets late, write your `journal` (diary, lessons, plan, and your `self` / `people` notes), then `go_to_bed`.";

    public static string MeetingLimit(int left) =>
        left > 0
            ? $"Morning meeting: only chat (`say`, `whisper`, `emote`), the plan board and observations work now. Farm work begins after the meeting ({left} meeting calls left)."
            : "Morning meeting: this was your last meeting call; the day begins in a moment.";

    public static string Nudge(string time, bool inBed) =>
        inBed
            ? "You are in bed; the day ends when every player sleeps. Nothing more to do: end your turn."
            : $"You ended your turn without an action. It is {time}. Continue the day with a tool call: work, talk, or `journal` and `go_to_bed` when you are done.";

    /// <summary>First message of a continued conversation after a session break (the mod asked the AI to stop and the context restarts).</summary>
    public static string Handoff(int day, string time, string plan, string? lastThink, IEnumerable<string> lastResults)
    {
        var sb = new StringBuilder($"Continuing farm day {day}; it is {time}. Your earlier context was cleared to keep the conversation small.\n");
        if (!string.IsNullOrWhiteSpace(lastThink))
            sb.Append("\nYour last thought:\n").Append(lastThink.Trim()).Append('\n');
        var today = PlanSection(plan, "Today");
        if (!string.IsNullOrWhiteSpace(today))
            sb.Append("\nYour plan for today:\n").Append(today).Append('\n');
        var results = lastResults.ToList();
        if (results.Count > 0)
        {
            sb.Append("\nYour last results:\n");
            foreach (var r in results)
                sb.Append("- ").Append(Clip(r, 500)).Append('\n');
        }
        sb.Append("\nCheck `status` and `look`, then carry on.");
        return sb.ToString();
    }

    /// <summary>The text under a `## {name}` heading of the plan file, or "".</summary>
    public static string PlanSection(string plan, string name)
    {
        if (string.IsNullOrWhiteSpace(plan)) return "";
        var lines = plan.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                if (inside) break;
                inside = line.Substring(3).Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inside) sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    public static string Clip(string s, int max)
    {
        s = s.Replace("\r", "").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static string Join(List<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
