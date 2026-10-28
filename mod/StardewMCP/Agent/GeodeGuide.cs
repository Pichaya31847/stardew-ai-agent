using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewMCP.Agent;

/// <summary>
/// What crack_geode tells the model about each kind of geode: where it comes from and what tends to come out
/// (the wiki's summary; the actual roll is the game's own Utility.getTreasureFromGeode). Game-free, linked into the tests.
/// </summary>
public static class GeodeGuide
{
    public sealed record Kind(string Id, string Name, string Where, string Gives);

    public static readonly IReadOnlyList<Kind> Kinds = new[]
    {
        new Kind("(O)535", "Geode", "rocks on Mines floors 1-39, fishing treasure chests",
            "ores, coal, clay, Earth Crystal and the 'Geode' minerals (Alamite, Calcite, Jamborite, Limestone, Malachite, Nekoite, Thunder Egg...)"),
        new Kind("(O)536", "Frozen Geode", "rocks on Mines floors 40-79, fishing treasure chests",
            "ores, Frozen Tear and the 'Frozen Geode' minerals (Aerinite, Esperite, Fluorapatite, Geminite, Ghost Crystal, Kyanite, Lunarite, Soapstone...)"),
        new Kind("(O)537", "Magma Geode", "rocks on Mines floors 80-120, fishing treasure chests",
            "gold and iron ore, Fire Quartz and the 'Magma Geode' minerals (Baryte, Basalt, Bixite, Fire Opal, Helvite, Jasper, Neptunite, Obsidian, Star Shards, Tigerseye...)"),
        new Kind("(O)749", "Omni Geode", "rocks anywhere in the Mines and the Skull Cavern (rarer), fishing treasure chests, panning, the Desert Trader",
            "anything the other three give, plus a small chance of a Prismatic Shard"),
        new Kind("(O)275", "Artifact Trove", "the Desert Trader, fishing treasure chests",
            "one artifact (Ancient Doll, Chipped Amphora, Arrowhead, Dwarf Gadget...): good Museum donations"),
        new Kind("(O)791", "Golden Coconut", "palm trees on Ginger Island (shake or chop them)",
            "island items (seeds, saplings, fossils); the very first one gives the Golden Helmet"),
        new Kind("(O)MysteryBox", "Mystery Box", "many daily jobs, once Mystery Boxes are unlocked",
            "a random prize: seeds, food, geodes, resources, sometimes rarer items"),
        new Kind("(O)GoldenMysteryBox", "Golden Mystery Box", "a rarer version of the Mystery Box",
            "a better random prize, sometimes an Artifact Trove or a Prismatic Shard")
    };

    /// <summary>The guide row for a qualified id, or null for a modded geode.</summary>
    public static Kind? For(string qualifiedId) => Kinds.FirstOrDefault(k => k.Id == qualifiedId);

    /// <summary>
    /// Best guess at which carried geode a loose name means ("Normal Geode", "geodes", "frozen"): an exact name first,
    /// then a name that contains the words, then plain "geode" for the basic Geode. Returns the index into <paramref name="names"/> or -1.
    /// </summary>
    public static int Match(IReadOnlyList<string> names, string query)
    {
        var q = Normalize(query);
        if (q.Length == 0)
            return -1;
        for (int i = 0; i < names.Count; i++)
            if (Normalize(names[i]) == q)
                return i;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("normal" or "basic" or "regular" or "plain")).ToArray();
        int best = -1;
        for (int i = 0; i < names.Count; i++)
        {
            var n = Normalize(names[i]);
            if (words.Length > 0 && words.All(w => n.Contains(w)) && (best < 0 || names[i].Length < names[best].Length))
                best = i;
        }
        return best;
    }

    private static string Normalize(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.EndsWith("es") && t.EndsWith("geodes")) t = t[..^1];
        return string.Join(' ', t.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
