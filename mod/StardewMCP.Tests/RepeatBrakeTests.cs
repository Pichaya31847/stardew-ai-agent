using StardewMCP.Agent.Brain;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>The built-in AI stops a tool that keeps failing (AI2's 20 calls of name on the first morning).</summary>
public class RepeatBrakeTests
{
    [Fact]
    public void Warns_from_the_third_failure_and_blocks_from_the_fifth()
    {
        var brake = new RepeatBrake();
        for (int i = 1; i <= 2; i++) { Assert.Null(brake.Before("name")); Assert.Equal("", brake.After("name", false)); }
        Assert.Contains("3 times", brake.After("name", false));
        brake.After("name", false);
        brake.After("name", false);
        Assert.NotNull(brake.Before("name"));
        Assert.Null(brake.Before("status")); // other tools still run
    }

    [Fact]
    public void Another_tool_or_a_success_resets_the_streak()
    {
        var brake = new RepeatBrake();
        for (int i = 0; i < 5; i++) brake.After("name", false);
        brake.Other("status");
        Assert.Null(brake.Before("name"));

        for (int i = 0; i < 5; i++) brake.After("walk_to", false);
        brake.After("walk_to", true);
        Assert.Null(brake.Before("walk_to"));
        Assert.Equal("", brake.After("walk_to", false));
    }
}
