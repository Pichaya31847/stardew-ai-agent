namespace StardewMCP.Agent;

// The part of WorldText that needs no game types, moved here unchanged so the unit tests
// (mod/StardewMCP.Tests) can compile it.
public static partial class WorldText
{
    /// <summary>Game clock (e.g. 1350, or 2600 for 2 AM) as "13:50".</summary>
    public static string FormatTime(int t)
    {
        int h = t / 100, m = t % 100;
        return $"{h:00}:{m:00}";
    }

    public static string DirName(int d) => d switch { 0 => "up", 1 => "right", 2 => "down", 3 => "left", _ => "?" };

    /// <summary>
    /// A mine level as the player sees it: floors 1-120 in the Mines, and floor 1+ in the Skull Cavern where
    /// the game's level 121 is the first Skull Cavern floor (#55).
    /// </summary>
    public static string MineFloorName(int level) =>
        level >= 121 ? $"Skull Cavern floor {level - 120}" : level == 0 ? "Mine entrance" : $"floor {level}";
}
