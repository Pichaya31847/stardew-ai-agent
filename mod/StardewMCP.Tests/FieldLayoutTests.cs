using System.Linq;
using Microsoft.Xna.Framework;
using StardewMCP.Agent;
using Xunit;

public class FieldLayoutTests
{
    [Fact]
    public void A_scarecrow_covers_249_tiles_8_each_way_and_6_diagonally()
    {
        var s = new Point(50, 50);
        Assert.Equal(248, FieldLayout.Disk(s).Count); // 249 with its own tile
        Assert.True(FieldLayout.Covers(s, new Point(58, 50)));
        Assert.False(FieldLayout.Covers(s, new Point(59, 50)));
        Assert.True(FieldLayout.Covers(s, new Point(56, 56)));
        Assert.False(FieldLayout.Covers(s, new Point(57, 57)));
    }

    [Fact]
    public void Scarecrows_closer_than_16_on_both_axes_are_too_close()
    {
        var existing = new[] { new Point(50, 50) };
        Assert.Equal(new Point(50, 50), FieldLayout.TooClose(new Point(51, 50), existing));
        Assert.Equal(new Point(50, 50), FieldLayout.TooClose(new Point(65, 60), existing));
        Assert.Null(FieldLayout.TooClose(new Point(66, 50), existing));
        Assert.Null(FieldLayout.TooClose(new Point(50, 34), existing));
        Assert.Null(FieldLayout.TooClose(new Point(66, 66), existing));
    }

    [Fact]
    public void Neighbouring_circles_share_only_the_middle_column()
    {
        var a = new Point(50, 50);
        var b = new Point(50 + FieldLayout.Spacing, 50);
        var shared = FieldLayout.Disk(a).Where(t => FieldLayout.Covers(b, t)).ToList();
        Assert.NotEmpty(shared);
        Assert.All(shared, t => Assert.Equal(58, t.X)); // 8 east of a and 8 west of b
    }

    [Fact]
    public void Next_spots_lie_on_the_16_grid_and_prefer_the_straight_neighbours()
    {
        var spots = FieldLayout.NextSpots(new[] { new Point(50, 50) });
        Assert.Equal(8, spots.Count);
        Assert.All(spots, p => Assert.True((p.X - 50) % 16 == 0 && (p.Y - 50) % 16 == 0));
        Assert.Contains(spots.First(), new[] { new Point(66, 50), new Point(34, 50), new Point(50, 66), new Point(50, 34) });
    }

    [Fact]
    public void Next_spots_between_two_scarecrows_come_first()
    {
        var spots = FieldLayout.NextSpots(new[] { new Point(50, 50), new Point(82, 50) });
        Assert.Equal(new Point(66, 50), spots.First());
        Assert.DoesNotContain(new Point(50, 50), spots);
    }

    [Theory]
    [InlineData(FieldLayout.Sprinkler.Basic, 4)]
    [InlineData(FieldLayout.Sprinkler.Quality, 8)]
    [InlineData(FieldLayout.Sprinkler.Iridium, 24)]
    public void Sprinklers_water_4_8_or_24_tiles(FieldLayout.Sprinkler kind, int tiles)
    {
        var s = new Point(20, 20);
        int n = 0;
        for (int x = 15; x <= 25; x++)
            for (int y = 15; y <= 25; y++)
                if (FieldLayout.Waters(kind, s, new Point(x, y))) n++;
        Assert.Equal(tiles, n);
    }

    [Theory]
    [InlineData(FieldLayout.Sprinkler.Basic)]
    [InlineData(FieldLayout.Sprinkler.Quality)]
    [InlineData(FieldLayout.Sprinkler.Iridium)]
    public void Each_pattern_waters_every_other_tile_exactly_once(FieldLayout.Sprinkler kind)
    {
        var scarecrow = new Point(40, 40);
        for (int x = 30; x <= 50; x++)
            for (int y = 30; y <= 50; y++)
            {
                var t = new Point(x, y);
                if (FieldLayout.IsSprinklerSpot(kind, scarecrow, t)) continue;
                int by = 0;
                for (int sx = x - 2; sx <= x + 2; sx++)
                    for (int sy = y - 2; sy <= y + 2; sy++)
                    {
                        var s = new Point(sx, sy);
                        if (FieldLayout.IsSprinklerSpot(kind, scarecrow, s) && FieldLayout.Waters(kind, s, t)) by++;
                    }
                Assert.Equal(1, by);
            }
    }

    [Theory]
    [InlineData(FieldLayout.Sprinkler.Basic)]
    [InlineData(FieldLayout.Sprinkler.Quality)]
    [InlineData(FieldLayout.Sprinkler.Iridium)]
    public void The_scarecrow_tile_is_never_a_sprinkler_spot(FieldLayout.Sprinkler kind)
    {
        var scarecrow = new Point(40, 40);
        Assert.False(FieldLayout.IsSprinklerSpot(kind, scarecrow, scarecrow));
        Assert.All(FieldLayout.SprinklerSpots(kind, scarecrow), p => Assert.True(FieldLayout.Covers(scarecrow, p)));
    }

    [Fact]
    public void The_field_grows_from_the_scarecrow_outwards()
    {
        var disk = FieldLayout.Disk(new Point(10, 10));
        Assert.Equal(1, System.Math.Abs(disk[0].X - 10) + System.Math.Abs(disk[0].Y - 10));
        Assert.DoesNotContain(new Point(10, 10), disk);
    }
}
