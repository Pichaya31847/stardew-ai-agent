using System.Linq;
using StardewMCP.Agent;
using Xunit;

public class ScreensTests
{
    private static SeatConfig[] LocalKit() => new[]
    {
        new SeatConfig { Role = "ai", Brain = "openai", Model = "stardew-local", BaseUrl = "http://127.0.0.1:11434/v1", Name = "AI1" },
        new SeatConfig { Role = "ai", Brain = "openai", Model = "stardew-local", BaseUrl = "http://127.0.0.1:11434/v1", Name = "AI2" },
        new SeatConfig { Role = "camera", Name = "Control" },
        new SeatConfig { Role = "off" }
    };

    private static string Roles(SeatConfig[] seats) => string.Join(",", seats.Select(s => s.Role));

    [Fact]
    public void One_screen_is_p1_alone_without_the_command_panel()
    {
        var seats = LocalKit();
        var (split, dashboard, _) = ScreenPlan.Apply(seats, 1);
        Assert.False(split);
        Assert.False(dashboard);
        Assert.Equal("ai,off,off,off", Roles(seats));
    }

    [Theory]
    [InlineData(2, "ai,ai,camera,off")]
    [InlineData(3, "ai,ai,ai,camera")]
    public void Two_and_three_screens_have_the_command_panel(int screens, string roles)
    {
        var seats = LocalKit();
        var (split, dashboard, _) = ScreenPlan.Apply(seats, screens);
        Assert.True(split);
        Assert.True(dashboard);
        Assert.Equal(roles, Roles(seats));
    }

    [Fact]
    public void Four_screens_are_four_farmers_without_the_command_panel()
    {
        var seats = LocalKit();
        var (split, dashboard, _) = ScreenPlan.Apply(seats, 4);
        Assert.True(split);
        Assert.False(dashboard);
        Assert.Equal("ai,ai,ai,ai", Roles(seats));
    }

    [Fact]
    public void A_new_farmer_takes_the_ai_of_the_seat_before_it_and_a_farmer_name()
    {
        var seats = LocalKit();
        var (_, _, notes) = ScreenPlan.Apply(seats, 4);
        Assert.All(seats, s => Assert.Equal("stardew-local", s.Model));
        Assert.Equal("AI3", seats[2].Name);
        Assert.Equal("AI4", seats[3].Name);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void Nothing_changes_the_second_time()
    {
        var seats = LocalKit();
        ScreenPlan.Apply(seats, 3);
        Assert.Empty(ScreenPlan.Apply(seats, 3).notes);
    }

    [Fact]
    public void An_external_mcp_seat_keeps_no_brain()
    {
        var seats = new[] { new SeatConfig(), new SeatConfig(), new SeatConfig(), new SeatConfig { Role = "camera" } };
        ScreenPlan.Apply(seats, 3);
        Assert.All(seats.Take(3), s => Assert.False(s.HasBrain));
        Assert.Equal("camera", seats[3].Role);
    }

    [Fact]
    public void Going_back_from_four_to_two_screens_brings_the_command_panel_back()
    {
        var seats = LocalKit();
        ScreenPlan.Apply(seats, 4);
        var (_, dashboard, _) = ScreenPlan.Apply(seats, 2);
        Assert.True(dashboard);
        Assert.Equal("ai,ai,camera,off", Roles(seats));
    }

    [Fact]
    public void A_human_p1_stays_human()
    {
        var seats = LocalKit();
        seats[0].Role = "human";
        ScreenPlan.Apply(seats, 2);
        Assert.Equal("human,ai,camera,off", Roles(seats));
    }
}
