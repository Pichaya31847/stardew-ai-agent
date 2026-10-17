using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// End of day with several farmers. In multiplayer the game starts the new day once every farmer in its
/// "sleep" ready check is in bed (or has passed out at 2:00 AM). Two rules keep that from stalling:
/// the camera seat is not asked to sleep, and an AI still up at bedtime is told the others are waiting.
/// Runs on the host screen, about once a second.
/// </summary>
public static class Bedtime
{
    private static readonly Dictionary<string, int> Warned = new();

    public static void Tick(ModConfig config)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !Players.SplitScreen)
            return;

        // The camera farmer has no AI to put it to bed, so without this the day could only end at 2:00 AM.
        // The new-day sync still carries it along; the game's dedicated host works the same way.
        var required = Game1.getOnlineFarmers().Where(f => Ownership.SeatOf(f.UniqueMultiplayerID) != null).ToList();
        if (required.Count > 0)
            Game1.netReady.SetLocalRequiredFarmers("sleep", required);

        if (config.BedtimeWarning <= 0 || Game1.timeOfDay < config.BedtimeWarning)
            return;
        int day = AgentLog.FarmDay;
        foreach (var seat in Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Connected && s.Active))
        {
            if (seat.State is not ScreenState { Ready: true, InBed: false } || Warned.GetValueOrDefault(seat.Id) == day)
                continue;
            Warned[seat.Id] = day;
            var time = WorldText.FormatTime(Game1.timeOfDay);
            SocialTools.Deliver(seat, $"[{time}] It is bedtime and you are not in bed. The day only ends when every player is asleep, so the others are waiting for you; " +
                                      "at 2:00 AM you pass out and lose gold (up to a tenth of your money). Finish up and go_to_bed.");
            seat.Runtime.Log.Add("system", text: $"Bedtime warning sent to {seat.Id} at {time}");
        }
    }
}
