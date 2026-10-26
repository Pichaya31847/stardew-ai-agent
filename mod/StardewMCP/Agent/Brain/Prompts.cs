using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// The texts the built-in AI loop sends: the system prompt (assets/system_prompt.md with its placeholders filled from
/// the seat's memory files) and the day's messages (morning, nudges, session handoff).
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
            return _templateCache ?? "You are {MODEL_NAME}, playing Stardew Valley as {PLAYER_NAME}. Use the tools; think and write in {LANGUAGE}.\n{TIME_MODE}\n{OTHERS}\n{SELF}\n{PEOPLE}\n{LESSONS}\n{PLAN}\n{NOTES}";
        }
    }

    /// <param name="Speech">Language of `say` ({SPEECH_LANGUAGE}); empty = <paramref name="Language"/>.</param>
    /// <param name="MemoryLines">How many of the last lessons and notes lines go into the prompt.</param>
    /// <param name="Work">Language of reasons and the memory files ({WORK_LANGUAGE}); empty = <paramref name="Language"/>.</param>
    public sealed record SystemInputs(
        string Model, string PlayerName, string Seat, string Language, bool FreezeWhileThinking,
        IReadOnlyList<(string Seat, string Name, string Kind)> Others,
        string Self, string People, string Lessons, string Plan, string Notes,
        string Speech = "", int MemoryLines = MemoryMaxLines, string Work = "");

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
                sb.Append($"\n- {o.Name} ({o.Kind})");
            others = sb.ToString();
        }

        return template
            .Replace("{MODEL_NAME}", i.Model)
            .Replace("{PLAYER_NAME}", i.PlayerName)
            .Replace("{SEAT}", i.Seat)
            .Replace("{SPEECH_LANGUAGE}", Or(i.Speech, i.Language))
            .Replace("{WORK_LANGUAGE}", Or(i.Work, i.Language))
            .Replace("{LANGUAGE}", i.Language)
            .Replace("{TIME_MODE}", timeMode)
            .Replace("{OTHERS}", others)
            .Replace("{SELF}", Or(i.Self, "(nothing yet: you are new here)"))
            .Replace("{PEOPLE}", Or(i.People, "(nothing yet)"))
            .Replace("{LESSONS}", Or(Tail(i.Lessons, Math.Max(1, i.MemoryLines)), "(none yet)"))
            .Replace("{PLAN}", Or(i.Plan, "(no plan yet: make one this morning)"))
            .Replace("{NOTES}", Or(Tail(i.Notes, Math.Max(1, i.MemoryLines)), "(none yet)"));
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

    /// <summary>The day's first message. There is no meeting phase: farmers who want to plan together go and find each other.</summary>
    public static string Morning(int day, string briefing, int? pendingJournalDay, IEnumerable<string> otherNames, string time)
    {
        var sb = new StringBuilder();
        sb.Append($"Good morning. It is {time} on farm day {day}.\n");
        if (!string.IsNullOrWhiteSpace(briefing))
            sb.Append('\n').Append(briefing.Trim()).Append('\n');
        if (pendingJournalDay is int pd)
            sb.Append($"\nYou passed out on day {pd} and never wrote its journal. Write it first with `journal(day={pd})` (what happened, what it cost, what you will do differently).\n");
        var names = otherNames.ToList();
        if (names.Count > 0)
        {
            if (day <= 1)
                sb.Append($"\nThis is your first morning here. {Join(names)} {(names.Count == 1 ? "lives" : "live")} in the cabins near yours on this farm.");
            sb.Append("\n`say` is heard only by people who can see you, so to talk with someone, go where they are.");
        }
        sb.Append("\nStart the day: `think` about your plan, then get to work. When the work is done or it gets late, write your `journal` " +
                  "(diary, lessons, plan, and your `self` / `people` notes), then `go_to_bed`.");
        sb.Append("\nInclude social actions TODAY, not only on the first day: use villagers() and talk_to a reachable NPC you have not greeted today. " +
                  "AI companions are players: find them with look/status and use say nearby, not talk_to. If say reports nobody heard, move within view before trying again. " +
                  "Reply to a companion's greeting when practical. Private think text and diary entries are not speech. Safety and urgent timed tasks take priority.");
        sb.Append("\nDo not sleep in the morning merely because your pack is full or one route failed. FIRST put items not needed for the current job in a chest; build storage if necessary. Buy the bigger backpack (upgrade_backpack) once you have its price plus this week's seed money; until then use a chest. Continue quests or useful work. " +
                  "A changing date, explosion or overnight cutscene is not proof you are awake: wait until the night event and fade finish before say/respond/movement.");
        sb.Append("\nIf a letter asks you to come to a place at a time (a villager wants to see you, an event), put it in today's plan and be there in time: these scenes unlock tools, recipes and the story.");
        sb.Append("\nReview today's Help Wanted board and your journal EVERY day. If a request is available and achievable before its deadline, visit the actual Help Wanted board in Town and call accept_quest, then verify it appears in quests. The adjacent calendar is a different board. If no request is feasible, explain why and continue an existing quest. Do not merely describe accepting a quest without calling the tool. Close/read all ordinary messages and finish explicit dialogue choices before starting your next job.");
        return sb.ToString();
    }

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
