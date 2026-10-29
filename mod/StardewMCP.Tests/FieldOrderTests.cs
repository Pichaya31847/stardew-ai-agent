using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

/// <summary>clear_area / till_area work a field row by row, snaking, from the end nearest the farmer.</summary>
public class FieldOrderTests
{
    private static List<Point> Field(int x, int y, int w, int h) =>
        Enumerable.Range(y, h).SelectMany(ty => Enumerable.Range(x, w).Select(tx => new Point(tx, ty))).ToList();

    [Fact]
    public void Snakes_from_the_nearest_corner()
    {
        var order = FieldOrder.Rows(Field(10, 20, 3, 2), new Point(9, 19));
        Assert.Equal(new[] { new Point(10, 20), new Point(11, 20), new Point(12, 20), new Point(12, 21), new Point(11, 21), new Point(10, 21) }, order);
    }

    [Fact]
    public void Starts_at_the_bottom_right_when_the_farmer_stands_there()
    {
        var order = FieldOrder.Rows(Field(10, 20, 3, 3), new Point(14, 25));
        Assert.Equal(new Point(12, 22), order[0]);
        Assert.Equal(new Point(10, 22), order[2]);
        Assert.Equal(new Point(10, 21), order[3]);
        Assert.Equal(new Point(10, 20), order[^1]);
    }

    [Fact]
    public void Every_tile_once_even_with_gaps_and_duplicates()
    {
        var tiles = new List<Point> { new(1, 1), new(5, 1), new(3, 4), new(3, 4) };
        var order = FieldOrder.Rows(tiles, new Point(0, 0));
        Assert.Equal(3, order.Count);
        Assert.Equal(new[] { new Point(1, 1), new Point(5, 1), new Point(3, 4) }, order);
        Assert.Empty(FieldOrder.Rows(new List<Point>(), new Point(0, 0)));
    }
}
