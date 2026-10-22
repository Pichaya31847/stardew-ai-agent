using System;
using Xunit;
using StardewMCP.Agent;

namespace StardewMCP.Tests;

public class BobberMathTests
{
    // ------------------------------------------------------------------ bar math

    [Fact]
    public void In_bar_window_edges()
    {
        float h = 120f, bar = 200f;
        // window is [bar-16, bar+h-44]
        Assert.True(BobberMath.FishInBar(bar - 16f, bar, h));
        Assert.False(BobberMath.FishInBar(bar - 16.5f, bar, h));
        Assert.True(BobberMath.FishInBar(bar + h - 44f, bar, h));
        Assert.False(BobberMath.FishInBar(bar + h - 43.5f, bar, h));
    }

    [Fact]
    public void Centered_bar_puts_fish_mid_window()
    {
        float[] heights = { 96f, 120f, 176f };
        float[] fishes = { 100f, 250f, 400f };

        foreach (var h in heights)
        {
            foreach (var fish in fishes)
            {
                float bar = BobberMath.CenteredBarPos(fish, h);
                float lo = bar - 16f, hi = bar + h - 44f;
                Assert.True(BobberMath.FishInBar(fish, bar, h));
                Assert.Equal(fish - lo, hi - fish, 4);
            }
        }
    }

    [Fact]
    public void Centered_bar_stays_on_track()
    {
        Assert.Equal(0f, BobberMath.CenteredBarPos(0f, 120f));
        Assert.Equal(BobberMath.MaxBarPos(120f), BobberMath.CenteredBarPos(530f, 120f));
    }

    [Fact]
    public void Accel_scaling()
    {
        Assert.Equal(0.25f, BobberMath.Accel(false, false), 5);
        Assert.Equal(0.15f, BobberMath.Accel(true, false), 5);
        Assert.Equal(0.075f, BobberMath.Accel(true, true), 5);
    }

    [Fact]
    public void Press_direction()
    {
        float h = 120f;
        // bar well below the fish and at rest: press (accelerate up)
        Assert.True(BobberMath.ShouldPress(400f, 0f, h, 100f, false, false, false));
        // bar well above the fish and at rest: release (accelerate down)
        Assert.False(BobberMath.ShouldPress(10f, 0f, h, 400f, false, false, true));

        // moving up fast toward a close target: it would overshoot, so release to brake
        float goal = BobberMath.CenteredBarPos(300f, h);
        Assert.False(BobberMath.ShouldPress(goal + 5f, -6f, h, 300f, false, false, true));
        // moving down fast toward a close target: press to brake
        Assert.True(BobberMath.ShouldPress(goal - 5f, 6f, h, 300f, false, false, false));
    }

    [Fact]
    public void Deadband_keeps_previous_decision()
    {
        float h = 120f;
        float goal = BobberMath.CenteredBarPos(300f, h);
        Assert.True(BobberMath.ShouldPress(goal + 0.5f, 0f, h, 300f, true, false, true));
        Assert.False(BobberMath.ShouldPress(goal + 0.5f, 0f, h, 300f, true, false, false));
    }

    [Fact]
    public void Treasure_hysteresis()
    {
        Assert.False(BobberMath.ChaseTreasure(false, 0f, false, 0.9f, false));
        Assert.False(BobberMath.ChaseTreasure(true, 100f, false, 0.9f, false)); // not appeared yet
        Assert.False(BobberMath.ChaseTreasure(true, 0f, true, 0.9f, true)); // already caught
        Assert.False(BobberMath.ChaseTreasure(true, 0f, false, 0.5f, false)); // not full enough to start
        Assert.True(BobberMath.ChaseTreasure(true, 0f, false, 0.6f, false));
        Assert.True(BobberMath.ChaseTreasure(true, 0f, false, 0.5f, true)); // keeps going while above the stop level
        Assert.False(BobberMath.ChaseTreasure(true, 0f, false, 0.3f, true));
    }

    // ------------------------------------------------------------------ cast math

    [Theory]
    [InlineData(1, 0, 4)]
    [InlineData(3, 2, 6)]
    [InlineData(0, 0, 3)]
    [InlineData(2, 4, 7)]
    public void Reach(int direction, int addedDistance, int expected)
    {
        Assert.Equal(expected, CastMath.ReachTiles(direction, addedDistance));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(7, 2)]
    [InlineData(8, 3)]
    [InlineData(14, 3)]
    [InlineData(15, 4)]
    public void Added_distance_by_level(int level, int expected)
    {
        Assert.Equal(expected, CastMath.AddedDistanceForLevel(level));
    }

    [Fact]
    public void Run_length()
    {
        // a vertical channel; stand at (5, 2) looking down
        bool Fishable(int x, int y) => x == 5 && y >= 3 && y <= 6;

        Assert.Equal(4, CastMath.RunLength(5, 2, 2, 10, Fishable));
        Assert.Equal(3, CastMath.RunLength(5, 2, 2, 3, Fishable));
        Assert.Equal(0, CastMath.RunLength(5, 2, 0, 10, Fishable));
    }

    [Theory]
    [InlineData(1, 5, 0)] // one tile of water: the minimum cast would overshoot
    [InlineData(2, 5, 2)]
    [InlineData(3, 5, 2)]
    [InlineData(8, 5, 5)] // wide water: as far as the rod reaches
    [InlineData(8, 3, 3)]
    [InlineData(6, 9, 5)]
    public void Aim_keeps_margin_before_far_bank(int run, int reach, int expected)
    {
        Assert.Equal(expected, CastMath.AimTiles(run, reach));
    }

    [Fact]
    public void Power_lands_inside_the_aimed_tile()
    {
        float[] reaches = { 3f, 4f, 5f, 8f };
        foreach (var reach in reaches)
        {
            for (int tiles = 2; tiles <= (int)reach; tiles++)
            {
                float p = CastMath.PowerFor(tiles, reach);
                Assert.True(p > 0f);
                Assert.True(p <= 1f);
                float landed = p * reach; // tiles out, in the model range = power * reach
                Assert.True(landed >= tiles - 1e-6f);
                Assert.True(landed < tiles + 1f + 1e-6f);
            }
        }
    }
}
