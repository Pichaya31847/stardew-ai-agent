using System.Linq;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class FishGuideTextTests
{
    [Fact]
    public void Time_windows_are_read_in_pairs()
    {
        var w = FishGuideText.TimeWindows("600 1100 1900 2600");
        Assert.Equal(new[] { (600, 1100), (1900, 2600) }, w);
        Assert.Equal("06:00-11:00 and 19:00-26:00", FishGuideText.FormatWindows(w));
        Assert.Equal("any time", FishGuideText.FormatWindows(FishGuideText.TimeWindows("600 2600")));
        Assert.Empty(FishGuideText.TimeWindows("junk"));
    }

    [Theory]
    [InlineData(700, false, true)]
    [InlineData(1200, false, false)]
    [InlineData(1200, true, true)]
    [InlineData(2600, true, false)]
    public void In_window_now_or_later_today(int time, bool later, bool expected) =>
        Assert.Equal(expected, FishGuideText.InWindow(time, FishGuideText.TimeWindows("600 1100 1900 2600"), later));

    [Fact]
    public void Weather_field()
    {
        Assert.Equal("rain only", FishGuideText.WeatherText("rainy"));
        Assert.Equal("", FishGuideText.WeatherText("both"));
        Assert.True(FishGuideText.WeatherFits("rainy", raining: true));
        Assert.False(FishGuideText.WeatherFits("sunny", raining: true));
        Assert.True(FishGuideText.WeatherFits("both", raining: false));
    }

    [Fact]
    public void Seasons_come_from_the_condition()
    {
        Assert.Null(FishGuideText.ConditionSeasons(null));
        Assert.Null(FishGuideText.ConditionSeasons("WEATHER Here rain"));
        Assert.Equal(new[] { "fall", "spring" }, FishGuideText.ConditionSeasons("LOCATION_SEASON Here spring fall, WEATHER Here rain")!.OrderBy(s => s));
        Assert.Equal(new[] { "winter" }, FishGuideText.ConditionSeasons("SEASON Winter"));
        Assert.Null(FishGuideText.ConditionSeasons("!SEASON winter"));
    }

    [Fact]
    public void Condition_notes_name_rain_and_qis_order()
    {
        Assert.Equal(new[] { "rain only" }, FishGuideText.ConditionNotes("LOCATION_SEASON Here spring, WEATHER Here rain"));
        Assert.Equal(new[] { "only during Mr. Qi's Extended Family special order" },
            FishGuideText.ConditionNotes("PLAYER_SPECIAL_ORDER_RULE_ACTIVE Current LEGENDARY_FAMILY"));
        Assert.Equal(new[] { "other conditions" }, FishGuideText.ConditionNotes("PLAYER_HAS_MAIL Current x, PLAYER_STAT Current y 1"));
        Assert.Empty(FishGuideText.ConditionNotes(""));
    }

    [Fact]
    public void Legendary_ids_include_the_five_and_their_family()
    {
        foreach (var id in new[] { "159", "160", "163", "682", "775", "898", "902" })
            Assert.Contains(id, FishGuideText.LegendaryIds);
        Assert.DoesNotContain("128", FishGuideText.LegendaryIds);
    }
}
