using System.Linq;
using StardewMCP.Agent.Brain;
using Xunit;

public class LessonBookTests
{
    [Fact]
    public void Condenses_only_past_the_slack()
    {
        string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"- Day {i}: lesson {i}"));
        Assert.False(LessonBook.ShouldCondense(Lines(LessonBook.MaxLessons + LessonBook.Slack)));
        Assert.True(LessonBook.ShouldCondense(Lines(LessonBook.MaxLessons + LessonBook.Slack + 1)));
        Assert.False(LessonBook.ShouldCondense(""));
    }

    [Fact]
    public void Parses_both_lists_and_strips_bullets_numbers_and_days()
    {
        var reply = "**LESSONS:**\n1. Water before noon\n- Day 12: Rest in bed when tired\n- Water before noon\n\nLASTING:\n- Plant only as many seeds as tilled tiles\n";
        var p = LessonBook.Parse(reply);
        Assert.NotNull(p);
        Assert.Equal(new[] { "Water before noon", "Rest in bed when tired" }, p!.Value.lessons);
        Assert.Equal(new[] { "Plant only as many seeds as tilled tiles" }, p.Value.lasting);
    }

    [Fact]
    public void An_answer_without_lessons_changes_nothing()
    {
        Assert.Null(LessonBook.Parse("I can't do that."));
        Assert.Null(LessonBook.Parse("LASTING:\n- something"));
        Assert.Null(LessonBook.Parse(null));
    }

    [Fact]
    public void Lists_are_capped()
    {
        var reply = "LESSONS:\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"- lesson {i}")) +
                    "\nLASTING:\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => $"- rule {i}"));
        var p = LessonBook.Parse(reply)!.Value;
        Assert.Equal(LessonBook.MaxLessons, p.lessons.Count);
        Assert.Equal(LessonBook.MaxLasting, p.lasting.Count);
    }

    [Fact]
    public void The_request_carries_the_lessons_and_the_earlier_lasting_ones()
    {
        var text = LessonBook.Request("- Day 3: chop trees early", "- Rest instead of sleeping early", "English");
        Assert.Contains("chop trees early", text);
        Assert.Contains("Rest instead of sleeping early", text);
        Assert.Contains("LASTING:", text);
        Assert.Equal("- a\n- b\n", LessonBook.Format(new[] { "a", "b" }));
    }

    [Fact]
    public void Lasting_lessons_go_into_the_system_prompt()
    {
        var inputs = new Prompts.SystemInputs("m", "AI1", "p1", "English", true, new (string, string, string)[0],
            "", "", "", "", "", Lasting: "- Never sell Wood");
        Assert.Contains("Never sell Wood", Prompts.System("{LASTING}\n{LESSONS}", inputs));
        var empty = inputs with { Lasting = "" };
        Assert.DoesNotContain("{LASTING}", Prompts.System("{LASTING}", empty));
    }
}
