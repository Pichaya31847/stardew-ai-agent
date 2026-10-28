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
    private string? _nearestName, _nearestWhere;

    /// <summary>The nearest villager not talked to today and where they are ("12 tiles away here on Town"), or null when unknown.</summary>
    public void SetNearestVillager(string? name, string? where)
    {
        _nearestName = string.IsNullOrWhiteSpace(name) ? null : name;
        _nearestWhere = where;
    }

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

    // Day 56: "find a fellow farmer" sent AI2 after AI1 with travel_to for 300 game minutes. A farmer is met while working (the
    // shared farm, the shops); the villager part names who is closest instead.
    public string Pending => (_hasOthers && !_heard ?
        "When you meet a fellow farmer while working, say a short greeting or work update to them (it counts once they hear it); don't travel just to find them. " : "") +
        (!_villager ? (_nearestName != null
            ? $"Talk to the nearest villager you haven't talked to today: talk_to(name=\"{_nearestName}\") ({_nearestWhere}). The other farmers are players, not villagers. "
            : "Use villagers() to choose a real NPC you have not talked to today, then talk_to(name, reason). The other farmers are players, not NPCs. ") : "");

    /// <summary>One line for a session handoff: what is still open, without the instructions the full reminder gives.</summary>
    public string Brief
    {
        get
        {
            var open = (_hasOthers && !_heard ? "say a word to a fellow farmer when you meet them" : "") +
                       (_hasOthers && !_heard && !_villager ? " and " : "") +
                       (!_villager ? (_nearestName != null ? $"talk_to a villager (nearest: {_nearestName})" : "talk_to a villager") : "");
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
