using System;

namespace StardewMCP.Agent.Brain;

/// <summary>Social goals survive conversation resets, but reset each farm day. Never invents dialogue or moves a farmer.</summary>
public sealed class DailySocialProgress
{
    /// <summary>One full reminder a day: three a day plus the goal in every session handoff read as nagging (112 in 19 days on the local run).</summary>
    private const int MaxReminders = 1;
    private const int FirstReminderAfter = 20;

    private int _day, _calls, _nextReminder, _reminders;
    private bool _hasOthers, _heard, _villager, _bedReminder;

    public void Begin(int day, bool hasOthers)
    {
        _hasOthers = hasOthers;
        if (_day == day) return;
        _day = day;
        _calls = _reminders = 0;
        _nextReminder = FirstReminderAfter;
        _heard = _villager = _bedReminder = false;
    }

    public void Observe(string tool, bool ok, string result)
    {
        _calls++;
        if (!ok) return;
        if (tool == "talk_to") _villager = true;
        if (tool == "say" && result.Contains("Heard by ", StringComparison.Ordinal)) _heard = true;
    }

    public string Pending => (_hasOthers && !_heard ?
        "Find a fellow farmer using look/status and their current location; travel/walk close enough, then say a short meaningful greeting or work update. A line with nobody in hearing range does not count. " : "") +
        (!_villager ? "Use villagers() to choose a real NPC you have not talked to today, then talk_to(name, reason). The other farmers are players, not NPCs. " : "");

    /// <summary>One line for a session handoff: what is still open, without the instructions the full reminder gives.</summary>
    public string Brief
    {
        get
        {
            var open = (_hasOthers && !_heard ? "talk with a fellow farmer (say, in their view)" : "") +
                       (_hasOthers && !_heard && !_villager ? " and " : "") +
                       (!_villager ? "talk_to a villager" : "");
            return open.Length == 0 ? "" : $"Social goal still open today: {open}.";
        }
    }

    public string Reminder()
    {
        if (_calls < _nextReminder || _reminders >= MaxReminders || Pending.Length == 0) return "";
        _nextReminder = _calls + 12;
        _reminders++;
        return "Today's social actions are still pending: " + Pending +
            "Do a pending social action next if safe and reachable; choose your own words/person. Finish urgent work or retreat from danger first. Avoid repeatedly chasing an unavailable person.";
    }

    public string BeforeBed()
    {
        if (_bedReminder || Pending.Length == 0) return "";
        _bedReminder = true;
        return "Before ending this day, social actions are still pending: " + Pending +
            "If safe, do one now and check the result. If you must rest or nobody is reachable, call go_to_bed again; this reminder will not block sleep a second time.";
    }
}
