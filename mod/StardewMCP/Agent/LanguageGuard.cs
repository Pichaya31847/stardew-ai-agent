using System;
using System.Linq;
using System.Text;

namespace StardewMCP.Agent;

/// <summary>
/// Catches Chinese characters in text that must be in another language. The local Qwen model drifts into Chinese mid-sentence
/// (AI2 day 56: "อ่าน Junimo note และ捐赠 items") although the prompt names the language, so think, say and the diary send such
/// text back to be written again. Game-free, so the tests compile it.
/// </summary>
public static class LanguageGuard
{
    /// <summary>A CJK unified ideograph (U+4E00-U+9FFF), the block the drift lands in.</summary>
    public static bool IsHan(char ch) => ch >= '一' && ch <= '鿿';

    public static bool HasHan(string? text) => !string.IsNullOrEmpty(text) && text.Any(IsHan);

    /// <summary>Languages written with these characters, where they are expected.</summary>
    public static bool UsesHan(string? language)
    {
        var l = (language ?? "").Trim().ToLowerInvariant();
        return l.StartsWith("zh") || l.StartsWith("ja") || l.Contains("chinese") || l.Contains("japanese") ||
               l.Contains("中文") || l.Contains("日本") || l.Contains("汉") || l.Contains("漢");
    }

    /// <summary>The runs of Chinese characters in <paramref name="text"/>, joined with ", " (at most a few).</summary>
    public static string HanRuns(string text, int max = 4)
    {
        var runs = new System.Collections.Generic.List<string>();
        var cur = new StringBuilder();
        foreach (var ch in text)
        {
            if (IsHan(ch)) { cur.Append(ch); continue; }
            if (cur.Length > 0) { runs.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) runs.Add(cur.ToString());
        return string.Join(", ", runs.Distinct().Take(max));
    }

    /// <summary>Why <paramref name="text"/> must be written again, or null when it is fine for <paramref name="language"/>.</summary>
    public static string? Reject(string? text, string language, string what)
    {
        if (!HasHan(text) || UsesHan(language)) return null;
        return $"Not done: your {what} contains Chinese characters ({HanRuns(text!)}). Write it again entirely in {language}, with no Chinese words.";
    }
}
