using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Profession choices at skill level 5 and 10. They are permanent and shape the strategy, so the
/// overnight chooser is not clicked for the AI: the choice is saved on the seat, shown in status,
/// and made with choose_profession. A choice still open at bedtime falls back to
/// <see cref="ModConfig.DefaultProfession"/>.
/// </summary>
public static class Professions
{
    private static readonly string[] Skills = { "Farming", "Fishing", "Foraging", "Mining", "Combat" };

    public static string SkillName(int skill) => skill >= 0 && skill < Skills.Length ? Skills[skill] : $"skill {skill}";

    public static int SkillIndex(string name) => System.Array.FindIndex(Skills, s => s.Equals(name.Trim(), System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The two professions offered at (skill, level); null while a level 10 waits for the level 5 choice.</summary>
    public static int[]? Options(Farmer p, Point choice)
    {
        int b = choice.X * 6;
        if (choice.Y == 5) return new[] { b, b + 1 };
        if (p.professions.Contains(b)) return new[] { b + 2, b + 3 };
        if (p.professions.Contains(b + 1)) return new[] { b + 4, b + 5 };
        return null;
    }

    public static string Title(int id) => LevelUpMenu.getProfessionTitleFromNumber(id);

    private static string Text(int id)
    {
        var d = LevelUpMenu.getProfessionDescription(id);
        return $"{d[0]}: {string.Join(" ", d.Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0))}";
    }

    /// <summary>Status lines for the seat's open choices, or "" when there are none.</summary>
    public static string PendingText(AgentRuntime rt, Farmer p)
    {
        if (rt.PendingProfessions.Count == 0) return "";
        var lines = rt.PendingProfessions.Select(c =>
        {
            var opts = Options(p, c);
            var body = opts == null
                ? $"choose the {SkillName(c.X)} 5 profession first"
                : string.Join("  ", opts.Select((id, i) => $"{i}) {Text(id)}"));
            return $"Pending profession choice (permanent): {SkillName(c.X)} {c.Y} — {body}";
        });
        return string.Join("\n", lines) + "\nUse choose_profession(option 0 or 1) before bed, or the default is picked for you.";
    }

    /// <summary>The overnight chooser: save the choice for the AI and close the menu without picking.</summary>
    public static void Defer(LevelUpMenu lm, AgentRuntime rt)
    {
        var refl = rt.Helper.Reflection;
        var choice = new Point(refl.GetField<int>(lm, "currentSkill").GetValue(), refl.GetField<int>(lm, "currentLevel").GetValue());

        // A choice left from an earlier day (passed out instead of going to bed) is settled first.
        int today = Game1.Date.TotalDays;  // AgentLog.FarmDay reads 0 while the night menus are up
        if (rt.PendingProfessions.Count > 0 && rt.PendingProfessionDay != today)
            ResolveDefaults(rt, Game1.player, "not chosen before the day ended");
        rt.PendingProfessionDay = today;
        if (!rt.PendingProfessions.Contains(choice))
            rt.PendingProfessions.Add(choice);
        rt.Log.Add("system", text: $"Level up: {SkillName(choice.X)} {choice.Y} profession choice saved for the AI");

        // Same exit the menu takes after a click, minus adding a profession. Without the newLevels
        // entry the game won't show it again; after a restart Game1.fixProblems re-adds it.
        lm.RemoveLevelFromLevelList();
        lm.informationUp = false;
        lm.isProfessionChooser = false;
        lm.isActive = false;
    }

    /// <summary>Take option 0/1 for a pending choice. Returns the result text, or null with an error in <paramref name="error"/>.</summary>
    public static string? Choose(AgentRuntime rt, Farmer p, Point choice, int option, out string error)
    {
        error = "";
        var opts = Options(p, choice);
        if (opts == null) { error = $"Choose the {SkillName(choice.X)} 5 profession first."; return null; }
        if (option != 0 && option != 1) { error = "option must be 0 or 1."; return null; }
        int id = opts[option];
        if (!p.professions.Contains(id))
        {
            p.professions.Add(id);
            // The game's immediate perks: Fighter +15 and Defender +25 max health.
            int extra = id == 24 ? 15 : id == 27 ? 25 : 0;
            p.maxHealth += extra;
            p.health += extra;
        }
        rt.PendingProfessions.Remove(choice);
        return $"{SkillName(choice.X)} {choice.Y}: you are now a {Title(id)}. {Text(id)}";
    }

    /// <summary>Pick the configured default for every open choice and log it.</summary>
    public static void ResolveDefaults(AgentRuntime rt, Farmer p, string why)
    {
        foreach (var choice in rt.PendingProfessions.OrderBy(c => c.Y).ToList())
        {
            var text = Choose(rt, p, choice, rt.Config.DefaultProfession == 1 ? 1 : 0, out var err);
            rt.Log.Add("system", text: text != null ? $"Profession auto-picked ({why}): {text}" : $"Profession auto-pick failed: {err}");
            if (text == null) rt.PendingProfessions.Remove(choice);
        }
    }

    public static string Owned(Farmer p) =>
        string.Join(", ", p.professions.Where(id => id >= 0 && id < 30).Select(Title));
}
