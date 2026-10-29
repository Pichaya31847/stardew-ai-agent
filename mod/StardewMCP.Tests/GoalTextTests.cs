using System.Linq;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class GoalTextTests
{
    [Fact]
    public void Every_achievement_but_joja_is_listed_once()
    {
        var names = GoalText.Achievements.Select(g => GoalText.Key(g.Name)).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain(GoalText.Key("Joja Co. Member"), names);
        foreach (var name in new[] { "Full Shipment", "Gourmet Chef", "Craft Master", "Protector of the Valley", "The Bottom", "Full House", "Master Angler", "A Complete Collection", "Local Legend" })
            Assert.NotNull(GoalText.Find(name));
        Assert.All(GoalText.Achievements, g => Assert.False(string.IsNullOrWhiteSpace(g.How)));
    }

    [Fact]
    public void Lines_show_progress_and_done()
    {
        var cook = GoalText.Find("Cook")!;
        Assert.Equal("✓ Cook (10/10)", GoalText.Line(cook, 12, 10, earned: false));
        Assert.StartsWith("· Cook 3/10: ", GoalText.Line(cook, 3, 10, earned: false));
        Assert.StartsWith("✓ Cook", GoalText.Line(cook, 3, 10, earned: true));
        var arcade = GoalText.Find("Prairie King")!;
        Assert.StartsWith("– Prairie King", GoalText.Line(arcade, 0, 1, earned: false));
        Assert.Equal(0.3, GoalText.Fraction(cook, 3, 10), 3);
        Assert.Equal(0, GoalText.Fraction(arcade, 1, 1));
    }

    [Theory]
    [InlineData("default", "known from the start")]
    [InlineData("f Gus 5", "letter from Gus at 5 hearts")]
    [InlineData("s Fishing 3", "Fishing level 3")]
    [InlineData("Farming 2", "Farming level 2")]
    [InlineData("none", "")]
    [InlineData("", "")]
    public void Recipe_unlocks_in_words(string field, string expected) =>
        Assert.Equal(expected, GoalText.Unlock(field));

    [Theory]
    [InlineData(270, 0)]
    [InlineData(304, 1)]
    [InlineData(508, 7)]
    [InlineData(600, 7)]
    public void Stardrops_from_max_energy(int max, int expected) => Assert.Equal(expected, GoalText.Stardrops(max));

    [Fact]
    public void Lists_are_cut_short()
    {
        Assert.Equal("none", GoalText.List(new string[0], 3));
        Assert.Equal("a, b and 2 more", GoalText.List(new[] { "a", "b", "c", "d" }, 2));
        Assert.Equal("Artisan goods", GoalText.CategoryName(-26));
        Assert.Equal("Other", GoalText.CategoryName(12345));
    }
}
