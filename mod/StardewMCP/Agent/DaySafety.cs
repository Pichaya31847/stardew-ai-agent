namespace StardewMCP.Agent;

/// <summary>Rules shared by the native game adapters and the built-in brain.</summary>
public static class DaySafety
{
    public static bool CanStartDay(bool ready, bool transitioning, bool anotherTransition, int day) =>
        ready && day > 0 && !transitioning && !anotherTransition;

    public static bool BlockEarlyBed(int time, float energy, int maxEnergy, int health, int maxHealth) =>
        time < 1700 && energy > maxEnergy / 4 && health > maxHealth / 3;

    public static bool CanInterruptForSocialEvent(string tool) => tool != "go_to_bed";

    /// <summary>Until this time a tired farmer is pointed at a rest in bed instead of the night's sleep: a rest back to full takes about three hours.</summary>
    public const int RestUntil = 1800;

    /// <summary>
    /// Out of energy early in the day (but not hurt): rest in bed and carry on rather than end the day. Only in co-op,
    /// where lying in bed gives energy back (Farmer.Update regenerates a multiplayer farmer on a bed tile).
    /// </summary>
    public static bool SuggestRest(int time, float energy, int maxEnergy, int health, int maxHealth, bool multiplayer) =>
        multiplayer && time < RestUntil && energy <= maxEnergy / 4 && health > maxHealth / 3;

    /// <summary>What to do about low energy: a rest in bed while it is early and others are online, else eat or sleep.</summary>
    public static string LowEnergyAdvice(int time, bool multiplayer) =>
        multiplayer && time < RestUntil
            ? "eat something, or rest in your bed (rest: about +14 energy every 10 minutes) and then get up and carry on; the day is not over"
            : "eat something or go to bed";
}
