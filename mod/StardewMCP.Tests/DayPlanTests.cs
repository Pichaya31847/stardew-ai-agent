using StardewMCP.Agent;
using Xunit;

public class DayPlanTests
{
    private static DayPlan Day58()
    {
        var p = new DayPlan();
        p.Set(58, new[] { "water the crops", "- chop wood for a Scarecrow", "clear the dead tomatoes", "talk to Alex", "mine 5 floors" });
        return p;
    }

    [Fact]
    public void Line_names_the_next_job_and_the_rest()
    {
        var line = Day58().Line(58);
        Assert.Contains("(0/5 done)", line);
        Assert.Contains("Next: water the crops.", line);
        Assert.Contains("mine 5 floors", line);
    }

    [Fact]
    public void Nothing_shows_on_another_day_or_without_a_plan()
    {
        Assert.Equal("", Day58().Line(59));
        Assert.Equal("", new DayPlan().Line(58));
    }

    [Fact]
    public void Jobs_are_ticked_off_by_number_or_words()
    {
        var p = Day58();
        Assert.Equal("water the crops", p.MarkDone(58, "1"));
        Assert.Equal("chop wood for a Scarecrow", p.MarkDone(58, "chop wood"));
        Assert.Equal("talk to Alex", p.MarkDone(58, "I talked to Alex: talk to Alex"));
        Assert.Null(p.MarkDone(58, "fish at the beach"));
        Assert.Contains("(3/5 done)", p.Line(58));
        Assert.Contains("Next: clear the dead tomatoes.", p.Line(58));
    }

    [Fact]
    public void All_done_asks_for_the_next_jobs()
    {
        var p = new DayPlan();
        p.Set(3, new[] { "water" });
        p.MarkDone(3, "water");
        Assert.Contains("all 1 done", p.Line(3));
    }

    [Fact]
    public void Lists_are_trimmed_capped_and_without_repeats()
    {
        var p = new DayPlan();
        p.Set(1, new[] { "a", "A", "", "b", "c", "d", "e", "f", "g", "h", "i" });
        Assert.Equal(DayPlan.MaxItems, p.Items.Count);
        Assert.False(p.Add(1, "b"));
        Assert.Equal("1. [ ] a", p.Checklist().Split('\n')[0]);
    }

    [Fact]
    public void Add_on_a_new_day_starts_a_new_list()
    {
        var p = Day58();
        Assert.True(p.Add(59, "ship the pumpkins"));
        Assert.Single(p.Items);
        Assert.Equal(59, p.Day);
    }
}
