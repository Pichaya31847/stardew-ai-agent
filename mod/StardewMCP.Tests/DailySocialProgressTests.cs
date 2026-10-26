using StardewMCP.Agent.Brain;
using Xunit;

namespace StardewMCP.Tests;

public class DailySocialProgressTests
{
    [Fact]
    public void LaterMorningsStillIncludeBothSocialTools()
    {
        var text = Prompts.Morning(31, "", null, new[] { "AI2" }, "06:00");
        Assert.Contains("villagers()", text);
        Assert.Contains("talk_to", text);
        Assert.Contains("use say nearby", text);
        Assert.Contains("not only on the first day", text);
    }

    [Fact]
    public void UnheardSpeechAndFailedNpcCallsDoNotCompleteGoals()
    {
        var progress = new DailySocialProgress();
        progress.Begin(2, true);
        progress.Observe("say", true, "You said hello, but nobody was close enough to hear it.");
        progress.Observe("talk_to", false, "No villager called AI2.");
        Assert.Contains("say", progress.Pending);
        Assert.Contains("talk_to", progress.Pending);
        progress.Observe("say", true, "You said hello. Heard by AI2.");
        Assert.DoesNotContain("Find a fellow", progress.Pending);
        progress.Observe("talk_to", true, "Talking to Lewis.");
        Assert.Equal("", progress.Pending);
        Assert.Equal("", progress.Brief);
    }

    [Fact]
    public void BriefNamesOnlyWhatIsStillOpen()
    {
        var progress = new DailySocialProgress();
        progress.Begin(4, true);
        Assert.Equal("Social goal still open today: talk with a fellow farmer (say, in their view) and talk_to a villager.", progress.Brief);
        progress.Observe("say", true, "Heard by AI2.");
        Assert.Equal("Social goal still open today: talk_to a villager.", progress.Brief);
        progress.Begin(5, false);
        Assert.Equal("Social goal still open today: talk_to a villager.", progress.Brief);
    }

    [Fact]
    public void GoalsSurviveSameDayRestartAndResetNextMorning()
    {
        var progress = new DailySocialProgress();
        progress.Begin(2, true);
        progress.Observe("talk_to", true, "Talking to Lewis.");
        progress.Begin(2, true);
        Assert.DoesNotContain("villagers()", progress.Pending);
        progress.Begin(3, true);
        Assert.Contains("villagers()", progress.Pending);
        Assert.Contains("say", progress.Pending);
    }

    [Fact]
    public void RemindersAreBoundedAndSleepIsNeverBlockedTwice()
    {
        var progress = new DailySocialProgress();
        progress.Begin(5, false);
        Assert.DoesNotContain("say", progress.Pending);
        Assert.Equal("", progress.Reminder());
        int reminders = 0;
        for (int i = 0; i < 100; i++)
        {
            progress.Observe("look", true, "Farm");
            if (progress.Reminder().Length > 0) reminders++;
        }
        Assert.Equal(1, reminders);
        Assert.NotEmpty(progress.BeforeBed());
        Assert.Equal("", progress.BeforeBed());
        progress.Begin(6, false);
        Assert.NotEmpty(progress.BeforeBed());
    }
}
