using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

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
    private static int _stalledSeconds;
    private static int _readySeconds;

    public static void Tick(ModConfig config)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !Players.SplitScreen)
            return;

        // The camera farmer has no AI to put it to bed, so without this the day could only end at 2:00 AM.
        // The new-day sync still carries it along; the game's dedicated host works the same way.
        var required = Game1.getOnlineFarmers().Where(f => Ownership.SeatOf(f.UniqueMultiplayerID) != null).ToList();
        if (required.Count > 0)
            Game1.netReady.SetLocalRequiredFarmers("sleep", required);
        StartStalledNight();

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

    /// <summary>Every farmer is ready to sleep but the night never begins: only the host's sleep box starts it
    /// (GameLocation.doSleep calls Game1.NewDay on the master game), and it was gone when the check completed.
    /// Days 6 and 7 stalled that way (2026-10-20, farmers waking each other right at bedtime), so do what the box would have done.</summary>
    private static void StartStalledNight()
    {
        var p = Game1.player;
        bool stalled = Game1.netReady.IsReady("sleep") && p.isInBed.Value && Players.Host.Runtime.CurrentTool == "go_to_bed" &&
                       p.timeWentToBed.Value > 0 && Game1.timeOfDay >= p.timeWentToBed.Value && // tonight's bedtime, never the morning after
                       !Game1.newDay && Game1.newDaySync?.hasInstance() != true &&
                       Game1.activeClickableMenu is not ReadyCheckDialog { checkName: "sleep" };
        _stalledSeconds = stalled ? _stalledSeconds + 1 : 0;
        bool allInBed = Game1.netReady.IsReady("sleep") && Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Active).All(s => s.State is ScreenState { InBed: true });
        _readySeconds = allInBed && !Game1.newDay ? _readySeconds + 1 : 0;
        if (_readySeconds == 10)
        {
            // Where a stalled night waits, once: the heal below only covers a night that never began.
            var sync = Game1.newDaySync;
            var seats = string.Join("; ", Players.Slots.Select(s => $"{s.Id} bed:{(s.State as ScreenState)?.InBed} menu:{(s.State as ScreenState)?.Menu ?? "-"} night:{(s.State as ScreenState)?.Night ?? "-"} tool:{s.Runtime.CurrentTool ?? "-"}"));
            Players.Host.Runtime.Log.Add("system", text: $"Night not begun after 10 s: host newDay:{Game1.newDay} fade:{Game1.fadeToBlack} " +
                $"sync:{sync?.hasInstance()}/{(sync?.hasInstance() == true && sync.hasStarted())} menu:{Game1.activeClickableMenu?.GetType().Name ?? "-"} " +
                $"bed:{p.isInBed.Value} wentToBed:{p.timeWentToBed.Value} tool:{Players.Host.Runtime.CurrentTool ?? "-"} | {seats}");
        }
        if (_stalledSeconds < 5)
            return;
        _stalledSeconds = 0;
        Players.Host.Runtime.Log.Add("system", text: $"Everyone is ready to sleep but the night did not begin at {WorldText.FormatTime(Game1.timeOfDay)}: starting it");
        p.lastSleepLocation.Value = Game1.currentLocation.NameOrUniqueName;
        p.lastSleepPoint.Value = p.TilePoint;
        p.mostRecentBed = p.Position;
        Game1.NewDay(0f);
    }
}
