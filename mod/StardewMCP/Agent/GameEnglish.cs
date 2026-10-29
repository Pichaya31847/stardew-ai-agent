using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.TokenizableStrings;

namespace StardewMCP.Agent;

/// <summary>
/// English for what the AIs read when the game runs in another language: the <see cref="NameMap"/> of localized names to
/// their English keys (built on save load), and English dialogue, letters, menu options and professions loaded from the
/// game's own English assets by their keys. Anything without a key (a few generated lines) stays in the game's language.
/// </summary>
internal static class GameEnglish
{
    private const LocalizedContentManager.LanguageCode En = LocalizedContentManager.LanguageCode.en;

    /// <summary>LevelUpMenu.getProfessionName (private), by profession id.</summary>
    private static readonly string[] ProfessionNames =
    {
        "Rancher", "Tiller", "Coopmaster", "Shepherd", "Artisan", "Agriculturist",
        "Fisher", "Trapper", "Angler", "Pirate", "Mariner", "Luremaster",
        "Forester", "Gatherer", "Lumberjack", "Tapper", "Botanist", "Tracker",
        "Miner", "Geologist", "Blacksmith", "Prospector", "Excavator", "Gemologist",
        "Fighter", "Scout", "Brute", "Defender", "Acrobat", "Desperado"
    };

    private static readonly Dictionary<string, Dictionary<string, string>?> Assets = new();

    public static bool English => LocalizedContentManager.CurrentLanguageCode == En;

    /// <summary>Every localized name of this game and its English key. Empty when the game is in English.</summary>
    public static NameMap Build()
    {
        Assets.Clear();
        var m = new NameMap();
        if (English) return m;

        Try(() => { foreach (var key in Game1.characterData.Keys) m.Add(NPC.GetDisplayName(key), key); });
        Try(() => { foreach (var n in Utility.getAllCharacters()) if (n != null && n is not Monster) m.Add(n.displayName, n.Name); });
        Try(() =>
        {
            foreach (var type in ItemRegistry.ItemTypes)
                foreach (var id in type.GetAllIds())
                    if (type.GetData(id) is { } d) m.Add(d.DisplayName, d.InternalName);
        });
        Try(() =>
        {
            foreach (var key in CraftingRecipe.craftingRecipes.Keys) m.Add(new CraftingRecipe(key, false).DisplayName, key);
            foreach (var key in CraftingRecipe.cookingRecipes.Keys) m.Add(new CraftingRecipe(key, true).DisplayName, key);
        });
        for (int i = 0; i < ProfessionNames.Length; i++)
        {
            int id = i;
            Try(() => m.Add(LevelUpMenu.getProfessionTitleFromNumber(id), ProfessionNames[id]));
        }
        Try(() =>
        {
            foreach (var (key, raw) in DataLoader.Monsters(Game1.content))
                if (raw.Split('/') is { Length: > 14 } f) m.Add(f[14], key);
        });
        Try(() => { foreach (var (key, q) in DataLoader.MonsterSlayerQuests(Game1.content)) m.Add(TokenParser.ParseText(q.DisplayName), key); });
        Try(() => { foreach (var (key, l) in DataLoader.Locations(Game1.content)) if (l?.DisplayName != null) m.Add(TokenParser.ParseText(l.DisplayName), key); });
        Try(() =>
        {
            var en = Game1.content.Load<Dictionary<int, string>>("Data\\Achievements", En);
            foreach (var (id, raw) in DataLoader.Achievements(Game1.content))
                if (en.TryGetValue(id, out var e)) m.Add(raw.Split('^')[0], e.Split('^')[0]);
        });
        Try(() => Pairs(m, "Data\\Quests", DataLoader.Quests(Game1.content), 1));
        Try(() => Pairs(m, "Data\\Festivals\\FestivalDates", Game1.content.Load<Dictionary<string, string>>("Data\\Festivals\\FestivalDates"), 0));
        return m;
    }

    /// <summary>Field <paramref name="field"/> of each localized entry paired with the same field of the English asset.</summary>
    private static void Pairs(NameMap m, string asset, Dictionary<string, string> local, int field)
    {
        var en = Asset(asset);
        if (en == null) return;
        foreach (var (key, raw) in local)
            if (en.TryGetValue(key, out var e) && raw.Split('/') is { } lf && e.Split('/') is { } ef && lf.Length > field && ef.Length > field)
                m.Add(lf[field], ef[field]);
    }

    private static Dictionary<string, string>? Asset(string asset)
    {
        if (Assets.TryGetValue(asset, out var cached)) return cached;
        Dictionary<string, string>? dict = null;
        try { dict = Game1.content.Load<Dictionary<string, string>>(asset, En); }
        catch (Exception) { /* not a string table, or missing: callers keep the localized text */ }
        return Assets[asset] = dict;
    }

    /// <summary>"Characters\Dialogue\Abigail:Mon" → that entry of the English asset, or null.</summary>
    private static string? Raw(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        int colon = key.IndexOf(':');
        if (colon <= 0) return null;
        return Asset(key[..colon]) is { } dict && dict.TryGetValue(key[(colon + 1)..], out var raw) ? raw : null;
    }

    /// <summary>The dialogue box's current line in English, when the line came from a dialogue key without substitutions.</summary>
    public static string Said(DialogueBox box)
    {
        var local = box.getCurrentString() ?? "";
        if (English || !NameMap.HasNonAscii(local)) return local;
        try
        {
            var d = box.characterDialogue;
            if (d != null && Raw(d.TranslationKey) is { } raw && raw.IndexOf('{') < 0)
            {
                var en = new Dialogue(d.speaker, null, raw);
                en.currentDialogueIndex = Math.Clamp(d.currentDialogueIndex, 0, Math.Max(0, en.dialogues.Count - 1));
                var line = en.getCurrentDialogue();
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
        }
        catch (Exception) { /* keep the game's own line */ }
        return local;
    }

    /// <summary>A question's option: the response key ("Shop", "Upgrade", "Leave") when the shown text is not English.</summary>
    public static string Option(Response r)
    {
        var text = r.responseText ?? "";
        var key = r.responseKey ?? "";
        return !English && NameMap.HasNonAscii(text) && key.Any(char.IsLetter) && !NameMap.HasNonAscii(key) ? key : text;
    }

    /// <summary>An open letter in English (from Data\mail by its id), or the game's text.</summary>
    public static string Letter(LetterViewerMenu letter)
    {
        var local = letter.mailMessage != null ? string.Join(" ", letter.mailMessage) : "";
        if (English || !NameMap.HasNonAscii(local) || letter.mailTitle == null) return local;
        if (Asset("Data\\mail") is not { } mail || !mail.TryGetValue(letter.mailTitle, out var raw)) return local;
        int title = raw.IndexOf("[#]", StringComparison.Ordinal);
        if (title >= 0) raw = raw[..title];
        raw = Regex.Replace(raw, @"%[^%]*%%", " ");
        raw = Regex.Replace(raw, @"\[[a-zA-Z]+[^\]]*\]", " ");
        return raw.Replace("@", Game1.player.Name).Replace("^", " ").Trim();
    }

    /// <summary>The profession's English name and what it does.</summary>
    public static string Profession(int id)
    {
        var name = id >= 0 && id < ProfessionNames.Length ? ProfessionNames[id] : $"profession {id}";
        var text = Asset("Strings\\UI") is { } ui && ui.TryGetValue("LevelUp_ProfessionDescription_" + name, out var d) ? d : null;
        if (text == null || English)
        {
            var local = LevelUpMenu.getProfessionDescription(id);
            return $"{name}: {string.Join(" ", local.Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0))}";
        }
        return $"{name}: {string.Join(" ", text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0))}";
    }

    public static string ProfessionName(int id) => id >= 0 && id < ProfessionNames.Length ? ProfessionNames[id] : LevelUpMenu.getProfessionTitleFromNumber(id);

    private static void Try(Action add)
    {
        try { add(); }
        catch (Exception) { /* one missing table must not cost the others */ }
    }
}
