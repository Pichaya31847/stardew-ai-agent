namespace StardewMCP.Agent;

/// <summary>Rules shared by the native game adapters and the built-in brain.</summary>
public static class DaySafety
{
    public static bool CanStartDay(bool ready, bool transitioning, bool anotherTransition, int day) =>
        ready && day > 0 && !transitioning && !anotherTransition;

    public static bool BlockEarlyBed(int time, float energy, int maxEnergy, int health, int maxHealth) =>
        time < EarlyBedUntil && energy > maxEnergy / 4 && health > maxHealth / 3;

    public static bool CanInterruptForSocialEvent(string tool) => tool != "go_to_bed";

    /// <summary>The time from which <see cref="BlockEarlyBed"/> lets a rested farmer sleep.</summary>
    public const int EarlyBedUntil = 1700;

    /// <summary>
    /// go_to_bed's refusal while the farmer is rested: one thing to do now as a ready call, and when bed is allowed. The second
    /// refusal of a day is one line (94 refusals on days 3-56 of the second 9B run, each repeating the full list).
    /// </summary>
    public static string EarlyBedRefusal(string time, int energy, int maxEnergy, string task, bool repeat)
    {
        var when = $"You can sleep from {EarlyBedUntil / 100}:00, or once your energy is {maxEnergy / 4} or less.";
        if (repeat)
            return $"Still {time}, energy {energy}/{maxEnergy}: not bedtime. Do this now: {task}. {when}";
        return $"It's only {time} and you aren't tired at all (energy {energy}/{maxEnergy}): lying down now you'd just stare at the ceiling. " +
               $"Do this now: {task}. {when} Don't call go_to_bed again before then.";
    }

    /// <summary>Until this time a tired farmer is pointed at a rest in bed instead of the night's sleep: a rest back to full takes about three hours.</summary>
    public const int RestUntil = 1800;

    /// <summary>
    /// Out of energy early in the day (but not hurt): rest in bed and carry on rather than end the day. Only in co-op,
    /// where lying in bed gives energy back (Farmer.Update regenerates a multiplayer farmer on a bed tile).
    /// </summary>
    public static bool SuggestRest(int time, float energy, int maxEnergy, int health, int maxHealth, bool multiplayer) =>
        multiplayer && time < RestUntil && energy <= maxEnergy / 4 && health > maxHealth / 3;

    /// <summary>Real seconds a message box with no text may stay up before it is closed for the farmer.</summary>
    public const double EmptyMessageSeconds = 2;

    /// <summary>Real seconds a message box may hold a farmer up while another farmer waits in bed for the night.</summary>
    public const double StaleMessageSeconds = 20;

    /// <summary>
    /// Whether a message box (not a question) on a farmer's screen should be closed for it: at once when it has no text, and
    /// when someone already lies in bed waiting for this farmer and the box has stayed up too long (day 56: AI1 waited in bed
    /// about five real minutes while AI2 sat in a message in Pierre's shop). Questions are left for the farmer to answer.
    /// </summary>
    public static bool CloseStaleMessage(double secondsOpen, bool emptyText, bool isQuestion, bool othersWaitingInBed) =>
        !isQuestion && (emptyText ? secondsOpen >= EmptyMessageSeconds : othersWaitingInBed && secondsOpen >= StaleMessageSeconds);

    /// <summary>What to do about low energy: a rest in bed while it is early and others are online, else eat or sleep.</summary>
    public static string LowEnergyAdvice(int time, bool multiplayer) =>
        multiplayer && time < RestUntil
            ? "eat something, or rest in your bed (rest: about +14 energy every 10 minutes) and then get up and carry on; the day is not over"
            : "eat something or go to bed";
}
