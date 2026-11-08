using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace StardewMCP.Agent;

/// <summary>
/// A tidy farm laid out around its scarecrows. A Scarecrow protects the tiles less than 9 tiles from it (the game's distance
/// check): 8 each way north, east, south and west, 6 on the diagonals, 249 tiles in a 17x17 square without its corners.
/// Scarecrows stand 16 tiles apart on one grid, so their circles touch north, east, south and west without overlapping, and
/// the fields are tilled inside a circle before ground nobody protects. Days 1-58: the scarecrows stood next to each other
/// and the fields lay wherever the model guessed a rectangle.
/// </summary>
public static class FieldLayout
{
    /// <summary>Tiles between neighbouring scarecrows in a row or a column.</summary>
    public const int Spacing = 16;

    /// <summary>A Scarecrow covers tiles closer than this; a Deluxe Scarecrow twice as far.</summary>
    public const int Radius = 9;
    public const int DeluxeRadius = 17;

    /// <summary>Whether a scarecrow at <paramref name="s"/> protects tile <paramref name="t"/>.</summary>
    public static bool Covers(Point s, Point t, int radius = Radius)
    {
        int dx = t.X - s.X, dy = t.Y - s.Y;
        return dx * dx + dy * dy < radius * radius;
    }

    /// <summary>The scarecrow that a new one at <paramref name="c"/> would stand too close to (under 16 tiles on both axes), or null.</summary>
    public static Point? TooClose(Point c, IEnumerable<Point> existing)
    {
        foreach (var s in existing)
            if (s != c && Math.Abs(s.X - c.X) < Spacing && Math.Abs(s.Y - c.Y) < Spacing)
                return s;
        return null;
    }

    /// <summary>
    /// The free grid spots next to the scarecrows already standing (16 tiles north, east, south, west, and the diagonals), the
    /// ones touching the most scarecrows first; none too close to any scarecrow.
    /// </summary>
    public static List<Point> NextSpots(IReadOnlyList<Point> existing)
    {
        var steps = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        return existing
            .SelectMany(s => steps.Select(d => new Point(s.X + d.Item1 * Spacing, s.Y + d.Item2 * Spacing)))
            .Distinct()
            .Where(p => TooClose(p, existing) == null && !existing.Contains(p))
            .OrderByDescending(p => existing.Count(s => Math.Abs(s.X - p.X) + Math.Abs(s.Y - p.Y) == Spacing))
            .ThenBy(p => existing.Min(s => Math.Abs(s.X - p.X) + Math.Abs(s.Y - p.Y)))
            .ToList();
    }

    // ------------------------------------------------------------------ sprinklers

    public enum Sprinkler { Basic, Quality, Iridium }

    /// <summary>Sprinkler kind by qualified item id ((O)599, (O)621, (O)645), or null.</summary>
    public static Sprinkler? SprinklerKind(string qualifiedId) => qualifiedId switch
    {
        "(O)599" => Sprinkler.Basic,
        "(O)621" => Sprinkler.Quality,
        "(O)645" => Sprinkler.Iridium,
        _ => null
    };

    /// <summary>Whether a sprinkler at <paramref name="s"/> waters <paramref name="t"/>: the 4 next to it, the 8 around it, or the 24 around it.</summary>
    public static bool Waters(Sprinkler kind, Point s, Point t)
    {
        int dx = Math.Abs(t.X - s.X), dy = Math.Abs(t.Y - s.Y);
        if (dx == 0 && dy == 0) return false;
        return kind switch
        {
            Sprinkler.Basic => dx + dy == 1,
            Sprinkler.Quality => dx <= 1 && dy <= 1,
            _ => dx <= 2 && dy <= 2
        };
    }

    private static int Mod(int a, int m) => (a % m + m) % m;

    /// <summary>
    /// Whether a tile is a sprinkler spot of the pattern around this scarecrow. The patterns tile the ground with no gap and no
    /// overlap: plus shapes on the lattice x + 2y = 1 (mod 5) for the Sprinkler, 3x3 squares for the Quality Sprinkler, 5x5 for
    /// the Iridium one. Each is shifted one tile east of the scarecrow, so the scarecrow stands in the square of the sprinkler
    /// beside it and no watered square is lost to it.
    /// </summary>
    public static bool IsSprinklerSpot(Sprinkler kind, Point scarecrow, Point t)
    {
        int dx = t.X - scarecrow.X, dy = t.Y - scarecrow.Y;
        return kind switch
        {
            Sprinkler.Basic => Mod(dx + 2 * dy, 5) == 1,
            Sprinkler.Quality => Mod(dx - 1, 3) == 0 && Mod(dy, 3) == 0,
            _ => Mod(dx - 1, 5) == 0 && Mod(dy, 5) == 0
        };
    }

    /// <summary>The sprinkler spots inside a scarecrow's circle, nearest the scarecrow first.</summary>
    public static List<Point> SprinklerSpots(Sprinkler kind, Point scarecrow, int radius = Radius) =>
        Disk(scarecrow, radius).Where(t => IsSprinklerSpot(kind, scarecrow, t)).ToList();

    /// <summary>
    /// The field around a scarecrow: every tile it protects except its own, nearest first, so the tilled ground grows as a
    /// circle around it (row by row at each distance).
    /// </summary>
    public static List<Point> Disk(Point s, int radius = Radius)
    {
        var tiles = new List<Point>();
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var t = new Point(s.X + dx, s.Y + dy);
                if (Covers(s, t, radius)) tiles.Add(t);
            }
        return tiles.OrderBy(t => (t.X - s.X) * (t.X - s.X) + (t.Y - s.Y) * (t.Y - s.Y)).ThenBy(t => t.Y).ThenBy(t => t.X).ToList();
    }
}
