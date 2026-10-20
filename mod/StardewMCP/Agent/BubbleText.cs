using System.Collections.Generic;
using System.Globalization;

namespace StardewMCP.Agent;

/// <summary>Line breaking for the speech bubbles above farmers (game-free, tested).</summary>
public static class BubbleText
{
    /// <summary>
    /// Breaks text into at most <paramref name="maxLines"/> lines of about <paramref name="width"/> visible characters,
    /// at a space when one is near; Thai has none, so it breaks between letters, never before a vowel or tone mark
    /// (they sit on the letter before) or after a leading vowel (it belongs to the next letter). Cut text ends in "…".
    /// </summary>
    public static List<string> Wrap(string text, int width, int maxLines)
    {
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length && lines.Count < maxLines)
        {
            while (start < text.Length && text[start] == ' ') start++;
            int end = start, seen = 0;
            while (end < text.Length && seen < width)
            {
                if (!IsMark(text[end])) seen++;
                end++;
            }
            while (end < text.Length && IsMark(text[end])) end++;
            if (end < text.Length)
            {
                int space = text.LastIndexOf(' ', end, end - start);
                if (space > start + width / 2) end = space;
                else while (end > start + 1 && (IsMark(text[end]) || IsLeadingVowel(text[end - 1]))) end--;
            }
            lines.Add(text[start..end].TrimEnd());
            start = end;
        }
        while (start < text.Length && text[start] == ' ') start++;
        if (start < text.Length && lines.Count > 0) lines[^1] += "…";
        return lines;
    }

    private static bool IsMark(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsLeadingVowel(char ch) => ch >= 'เ' && ch <= 'ไ'; // เ แ โ ใ ไ
}
