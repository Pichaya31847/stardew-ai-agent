using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// The AI's lessons, kept useful. The journal appends "- Day N: ..." lines every night and the prompt only reads the last
/// BrainMemoryLines of them (20 on the local model), so early lessons fell out of sight while repeats piled up. Each night
/// after bed the model condenses them into at most <see cref="MaxLessons"/>, the most important first, and picks the
/// lasting ones (true on any farm, in any save) for the lasting file, which survives a new save and goes into every prompt.
/// Game-free: the prompt and the parsing; SeatBrain makes the call and writes the files.
/// </summary>
public static class LessonBook
{
    public const int MaxLessons = 20;
    public const int MaxLasting = 12;

    /// <summary>Condense once the lessons have grown this far past <see cref="MaxLessons"/>.</summary>
    public const int Slack = 5;

    private static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();

    public static bool ShouldCondense(string? lessons) => Lines(lessons).Count > MaxLessons + Slack;

    public const string System =
        "You keep a Stardew Valley farmer's notebook tidy. You answer with the two lists asked for and nothing else.";

    /// <summary>The one message of the condensing call.</summary>
    public static string Request(string lessons, string lasting, string language)
    {
        var sb = new StringBuilder();
        sb.Append($"Below are the farmer's lessons from past days, and the lasting lessons kept from earlier farms. Write in {language}.\n\n");
        sb.Append($"1. LESSONS: rewrite the lessons as at most {MaxLessons} short lines, the most useful first. Merge repeats and lessons that say the same thing, " +
                  "keep what still matters on this farm, drop tile coordinates, one-time events and anything a later lesson corrected.\n");
        sb.Append($"2. LASTING: at most {MaxLasting} lessons true on ANY farm in any save (how to play well, not this farm's places or dates). " +
                  "Keep the earlier lasting lessons unless a lesson shows one is wrong; add only clearly general ones.\n\n");
        sb.Append("Answer exactly in this form:\nLESSONS:\n- ...\nLASTING:\n- ...\n\n");
        sb.Append("Lessons:\n").Append(string.Join("\n", Lines(lessons))).Append("\n\n");
        sb.Append("Earlier lasting lessons:\n").Append(Lines(lasting).Count > 0 ? string.Join("\n", Lines(lasting)) : "(none)").Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The two lists from the model's answer, each as plain lesson texts (bullets, numbers and "Day N:" stripped), capped.
    /// Null when the answer has no LESSONS list, so the files stay as they were.
    /// </summary>
    public static (List<string> lessons, List<string> lasting)? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var lessons = new List<string>();
        var lasting = new List<string>();
        List<string>? into = null;
        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().Trim('*').Trim();
            if (line.StartsWith("LESSONS", StringComparison.OrdinalIgnoreCase)) { into = lessons; continue; }
            if (line.StartsWith("LASTING", StringComparison.OrdinalIgnoreCase)) { into = lasting; continue; }
            if (into == null || line.Length == 0) continue;
            var text = Clean(line);
            if (text.Length > 0 && !into.Contains(text, StringComparer.OrdinalIgnoreCase)) into.Add(text);
        }
        if (lessons.Count == 0) return null;
        return (lessons.Take(MaxLessons).ToList(), lasting.Take(MaxLasting).ToList());
    }

    private static string Clean(string line)
    {
        line = line.TrimStart('-', '•', '*', ' ');
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i > 0 && i < line.Length && (line[i] == '.' || line[i] == ')')) line = line.Substring(i + 1);
        line = line.Trim();
        if (line.StartsWith("Day ", StringComparison.OrdinalIgnoreCase) && line.IndexOf(':') is int c and > 0 and < 12) line = line.Substring(c + 1).Trim();
        return line.Length > 240 ? line.Substring(0, 240).TrimEnd() + "…" : line;
    }

    /// <summary>"- lesson" lines for a memory file.</summary>
    public static string Format(IEnumerable<string> lessons) => string.Join("\n", lessons.Select(l => "- " + l)) + "\n";
}
