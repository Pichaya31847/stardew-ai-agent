using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewMCP.Agent;

/// <summary>
/// The journal (#21): the AI's memory across days, written by one instant tool. It costs no game time and can be
/// called any time; go_to_bed refuses until today's journal is written.
///   diary    for the viewers  -> diary_NNN.md (replaced when written again the same day)
///   lessons  what it learned  -> lessons.md, appended as "- Day N: ..." (that day's lines are replaced when rewritten)
///   plan     what comes next  -> plan.md, sections ## Season / ## Week / ## Today; a section left out keeps its old text
///   notes    facts it found   -> notes.md, appended as "- [topic, day N] ..."
/// Files are per seat through <see cref="AgentLog.MemoryFile"/> (p1 keeps the plain names), so per-seat character
/// notes (#48) can sit next to them. runner/memory.py reads them back into the next day's system prompt.
/// </summary>
public static class JournalTools
{
    public const int MinDiary = 20, MaxLessonLines = 5, MaxLessonChars = 300, MaxNoteLines = 8, MaxNoteChars = 200, MaxPlanChars = 1500;
    public const int MaxSelfChars = 1000, MaxPeopleLines = 6, MaxPeopleLineChars = 250;

    /// <summary>Plan sections in file order. "Today" is the plan for when the player next wakes up.</summary>
    private static readonly string[] Scopes = { "Season", "Week", "Today" };

    private static readonly string[] NoteTopics = { "places", "shops", "farm", "mines", "people", "other" };

    // "## Season", "# Week plan:", "### Today - water first". "Tomorrow" and "Next" mean Today.
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*(season|week|today|tomorrow|next)\b(?:\s+plan)?\s*[:\-–]?\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Bullet = new(@"^(?:[-*•]+|\d+[.)])\s+", RegexOptions.CultureInvariant);

    // "[shops, day 6] " or "[day 2] " at the start of a note: a tag copied from notes.md, not part of the fact.
    private static readonly Regex CopiedTag = new(@"^\s*\[\s*([^\],\s]+)?\s*,?\s*(?:day\s*\d+)?\s*\]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The diary is for the viewers (ThoughtLanguage); lessons, plan, notes, self and people come back into every prompt (WorkLanguage).</summary>
    private static string Lang => AgentTools.Config.LanguageName();
    private static string WorkLang => AgentTools.Config.WorkLanguageName();

    public static List<ToolDef> All() => new()
    {
        new()
        {
            Name = "journal",
            Description = "Write your journal: your memory across days. A diary, the lessons you learned today and your plan. " +
                          "At the start of every later day you get your lessons, plan and notes back, so write what future you needs to know. " +
                          "Costs no game time. Call it any time, and at the latest before go_to_bed, which refuses until today's journal is written. " +
                          "Calling it again the same day replaces that day's diary and lessons and updates the plan. " +
                          "Write optional self and people to record who you are becoming and what you think of the others on the farm.",
            Schema = AgentTools.Obj(new
            {
                diary = AgentTools.Str($"Today's diary in {Lang}, 3-8 sentences, written as yourself: what you did, what went well or badly"),
                lessons = AgentTools.Str($"1-3 short lessons from today in {WorkLang}, one per line, in your own words: what worked, what failed and why, a rule for next time. They are added to your lessons list"),
                plan = AgentTools.Str($"Your plans and follow-ups in {WorkLang}: what to do next, when crops are ready, when to pick up a tool, goals. " +
                                      "Use the headings \"## Season\" (long goal), \"## Week\" and \"## Today\" (what to do when you wake up); " +
                                      "a section you leave out keeps its old text, a heading with nothing under it clears that section"),
                notes = AgentTools.Str($"Optional facts you found out, in {WorkLang}, one per line, e.g. \"shops: Pierre is closed on Wednesday\". " +
                                       $"Start a line with a topic and colon ({string.Join(", ", NoteTopics)}) to file it. They are added to your facts list"),
                self = AgentTools.Str($"Optional note in {WorkLang} on who you are and what you live for, max ~150 words. Overwritten each evening; cite today's events as evidence."),
                people = AgentTools.Str($"Optional notes in {WorkLang} on the others on the farm: one line per person (current stance + latest evidence + day)."),
                day = AgentTools.Int("Optional farm day to write this journal for, from 1 to today. Only for catching up a day you did not write (for example after passing out). Default: today")
            }, "diary", "lessons", "plan"),
            Instant = true, AppendStatus = false, Run = Journal
        }
    };

    private static IEnumerator Journal(JsonElement a, CallContext c)
    {
        var log = AgentTools.Rt.Log;
        int today = AgentLog.FarmDay;
        int day = AgentTools.I(a, "day", today);
        if (day < 1 || day > today)
        {
            c.Fail($"day must be a farm day from 1 to {today} (today is farm day {today}). Leave day out to write today's journal.");
            yield break;
        }

        var diary = AgentTools.S(a, "diary").Trim();
        if (diary.Length < MinDiary) { c.Fail($"Write a real diary entry in {Lang} first (diary parameter, at least {MinDiary} characters)."); yield break; }

        var lessons = Lines(AgentTools.S(a, "lessons"), MaxLessonChars);
        if (lessons.Count == 0) { c.Fail($"lessons is empty. Write 1-3 short lessons from day {day} in {WorkLang}, one per line (what worked, what failed and why)."); yield break; }
        int lessonsDropped = Math.Max(0, lessons.Count - MaxLessonLines);
        lessons = lessons.Take(MaxLessonLines).ToList();

        var plan = AgentTools.S(a, "plan").Trim();
        if (plan.Length == 0) { c.Fail($"plan is empty. Write what you plan to do next in {WorkLang}, with ## Season, ## Week and ## Today headings if you can."); yield break; }
        if (plan.Length > MaxPlanChars) { c.Fail($"plan is {plan.Length} characters; the limit is {MaxPlanChars}. Shorten it."); yield break; }

        var notes = Notes(AgentTools.S(a, "notes")).Take(MaxNoteLines).ToList();

        string self = AgentTools.Has(a, "self") ? AgentTools.S(a, "self").Trim() : "";
        if (self.Length > MaxSelfChars)
        {
            c.Fail($"self is {self.Length} characters; the limit is {MaxSelfChars} (about 150 words). Shorten it.");
            yield break;
        }

        string peopleRaw = AgentTools.Has(a, "people") ? AgentTools.S(a, "people").Trim() : "";
        var peopleLines = Lines(peopleRaw, MaxPeopleLineChars);
        if (peopleLines.Count > MaxPeopleLines)
        {
            c.Fail($"people has {peopleLines.Count} lines; the limit is {MaxPeopleLines} (one line per other player).");
            yield break;
        }

        string prevSelf = log.ReadSelf();
        string prevPeople = log.ReadPeople();

        log.WriteDiary(day, diary);
        int lessonLines = log.WriteLessons(day, lessons);
        var written = SplitPlan(plan);
        var merged = MergePlan(log.ReadPlan(), plan);
        log.WritePlan(merged);
        var (notesAdded, noteLines) = notes.Count > 0 ? log.AppendNotes(day, notes) : (0, 0);
        if (self.Length > 0) log.WriteSelf(self);
        if (peopleLines.Count > 0) log.WritePeople(string.Join("\n", peopleLines));
        log.MarkJournal(day);
        log.Add("journal", text: string.Join("\n", lessons), args: new
        {
            day,
            plan = merged,
            notes = notes.Select(n => $"{n.topic}: {n.text}").ToList(),
            self = self.Length > 0 ? self : null,
            self_before = self.Length > 0 ? prevSelf : null,
            people = peopleLines.Count > 0 ? string.Join("\n", peopleLines) : null,
            people_before = peopleLines.Count > 0 ? prevPeople : null
        });

        var sb = new StringBuilder($"Journal saved for farm day {day}{(day == today ? "" : " (a day you had missed)")}.");
        sb.Append($"\n- diary: {diary.Length} characters");
        sb.Append($"\n- lessons: {lessons.Count} added to your lessons list ({lessonLines} lines in all)");
        if (lessonsDropped > 0) sb.Append($"; only the first {MaxLessonLines} lines were kept, so make each lesson count");
        if (notes.Count > 0) sb.Append($"\n- notes: {notesAdded} new fact{(notesAdded == 1 ? "" : "s")} added ({noteLines} lines in all)");
        sb.Append($"\n- plan: updated {string.Join(", ", Scopes.Where(written.ContainsKey))}");
        var kept = Scopes.Where(s => !written.ContainsKey(s) && PlanHas(merged, s)).ToList();
        if (kept.Count > 0) sb.Append($"; kept your earlier {string.Join(", ", kept)}");
        if (self.Length > 0) sb.Append($"\n- self: updated identity statement ({self.Length} chars)");
        if (peopleLines.Count > 0) sb.Append($"\n- people: updated notes on {peopleLines.Count} player(s)");
        if (day == today)
            sb.Append("\nWhen your day is done, call go_to_bed.");
        else if (!log.HasJournal(today))
            sb.Append($"\nToday's journal (farm day {today}) is still to be written before go_to_bed.");
        c.Result = sb.ToString();
    }

    // ------------------------------------------------------------------ parsing

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Non-empty lines of <paramref name="text"/> without list bullets, each cut to <paramref name="maxChars"/>.</summary>
    internal static List<string> Lines(string text, int maxChars) =>
        text.Replace("\r", "").Split('\n')
            .Select(l => Bullet.Replace(l.Trim(), ""))
            .Where(l => l.Length > 0)
            .Select(l => Clip(l, maxChars))
            .ToList();

    /// <summary>Facts as (topic, text). A leading "topic:" from <see cref="NoteTopics"/> files the fact; anything else is "other".</summary>
    internal static List<(string topic, string text)> Notes(string text)
    {
        var notes = new List<(string topic, string text)>();
        foreach (var raw in Lines(text, MaxNoteChars))
        {
            // The model copies the "[topic, day N]" tags it reads in its facts list back into new notes ("[other, day 6] [shops, day 6] ...").
            string topic = "other", line = raw;
            for (var m = CopiedTag.Match(line); m.Success; m = CopiedTag.Match(line))
            {
                var tagged = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (NoteTopics.Contains(tagged) && tagged != "other") topic = tagged;
                line = line[m.Length..];
            }
            string body = line;
            int colon = line.IndexOf(':');
            if (colon > 0 && colon <= 8)
            {
                var head = line[..colon].Trim().ToLowerInvariant();
                if (NoteTopics.Contains(head))
                {
                    topic = head;
                    body = line[(colon + 1)..].Trim();
                }
            }
            if (body.Length > 0) notes.Add((topic, body));
        }
        return notes;
    }

    /// <summary>
    /// Split a plan into its Season / Week / Today sections. Text with none of those headings is all "Today".
    /// Text before the first heading joins the first section; other headings stay inside the section they are in.
    /// </summary>
    internal static Dictionary<string, string> SplitPlan(string plan)
    {
        var lines = new Dictionary<string, List<string>>();
        var preface = new List<string>();
        string? current = null;
        foreach (var raw in plan.Replace("\r", "").Split('\n'))
        {
            var m = Heading.Match(raw);
            if (m.Success)
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                current = name == "season" ? "Season" : name == "week" ? "Week" : "Today";
                if (!lines.ContainsKey(current)) lines[current] = new List<string>();
                if (m.Groups[2].Value.Trim().Length > 0) lines[current].Add(m.Groups[2].Value.Trim());
            }
            else if (current == null)
                preface.Add(raw);
            else
                lines[current].Add(raw);
        }

        var sections = new Dictionary<string, string>();
        if (lines.Count == 0)
        {
            var all = string.Join("\n", preface).Trim();
            if (all.Length > 0) sections["Today"] = all;
            return sections;
        }
        var first = Scopes.First(lines.ContainsKey);
        foreach (var scope in Scopes.Where(lines.ContainsKey))
        {
            var body = scope == first ? preface.Concat(lines[scope]) : lines[scope];
            sections[scope] = string.Join("\n", body).Trim();
        }
        return sections;
    }

    /// <summary>The new plan's sections replace the old ones of the same name; sections it leaves out are kept; an empty section is dropped.</summary>
    internal static string MergePlan(string existing, string incoming)
    {
        var old = SplitPlan(existing);
        var now = SplitPlan(incoming);
        var sb = new StringBuilder();
        foreach (var scope in Scopes)
        {
            var text = now.TryGetValue(scope, out var n) ? n : old.GetValueOrDefault(scope, "");
            if (text.Length > 0)
                sb.Append("## ").Append(scope).Append('\n').Append(text).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static bool PlanHas(string plan, string scope) => SplitPlan(plan).ContainsKey(scope);
}
