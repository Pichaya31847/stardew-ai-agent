using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Closes a message box that holds up the night: one with no text at once, any other (never a question) once another farmer
/// has waited in bed for <see cref="DaySafety.StaleMessageSeconds"/> real seconds. The farmer hears about it as an event and the
/// day log keeps a line. Ticked on each seat's own screen, where Game1.activeClickableMenu is that seat's menu.
/// </summary>
public static class SleepWaitWatch
{
    private static readonly Dictionary<AgentRuntime, (IClickableMenu menu, DateTime since)> Seen = new();

    public static void Tick(AgentRuntime rt)
    {
        // A person at the keyboard reads at their own pace.
        if (Game1.activeClickableMenu is not DialogueBox box || Players.Slots.FirstOrDefault(s => s.Runtime == rt) is not { Role: SeatRole.Ai })
        {
            Seen.Remove(rt);
            return;
        }
        var now = DateTime.UtcNow;
        if (!Seen.TryGetValue(rt, out var seen) || !ReferenceEquals(seen.menu, box))
        {
            Seen[rt] = (box, now);
            return;
        }
        var text = (box.getCurrentString() ?? "").Replace("\n", " ").Trim();
        bool othersInBed = !Game1.player.isInBed.Value && Game1.getOnlineFarmers().Any(f => f != Game1.player && f.isInBed.Value);
        if (box.transitioning || !DaySafety.CloseStaleMessage((now - seen.since).TotalSeconds, text.Length == 0, box.isQuestion, othersInBed))
            return;

        box.closeDialogue();
        Game1.dialogueUp = false;
        Seen.Remove(rt);
        var why = text.Length == 0 ? "an empty message box" : $"the message \"{text}\" (open {(int)(now - seen.since).TotalSeconds}s while another farmer waited in bed)";
        rt.Log.Add("system", text: $"Watchdog closed {why} at {WorldText.FormatTime(Game1.timeOfDay)}");
        if (text.Length > 0 && Players.Slots.FirstOrDefault(s => s.Runtime == rt) is { } seat)
            SocialTools.Deliver(seat, $"[{WorldText.FormatTime(Game1.timeOfDay)}] A message was left open while another farmer waits in bed for the night, so it was closed for you: \"{text}\". Finish up and go_to_bed.");
    }
}
