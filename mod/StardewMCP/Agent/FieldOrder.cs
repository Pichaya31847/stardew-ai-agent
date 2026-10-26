using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace StardewMCP.Agent;

/// <summary>
/// The order clear_area and till_area work a field in: row by row like a farmer mowing, from the end of the field nearest
/// the farmer, each row in the other direction from the last (a snake). Nearest-tile-first hopped all over the field and
/// left it patchy. Game-free, so the unit tests compile this file.
/// </summary>
public static class FieldOrder
{
    public static List<Point> Rows(IEnumerable<Point> tiles, Point from)
    {
        var all = tiles.Distinct().ToList();
        if (all.Count == 0) return all;
        int top = all.Min(t => t.Y), bottom = all.Max(t => t.Y);
        bool down = Math.Abs(from.Y - top) <= Math.Abs(from.Y - bottom);
        var rows = all.GroupBy(t => t.Y).OrderBy(g => down ? g.Key : -g.Key).ToList();

        int left = all.Min(t => t.X), right = all.Max(t => t.X);
        bool rightward = Math.Abs(from.X - left) <= Math.Abs(from.X - right);
        var order = new List<Point>(all.Count);
        foreach (var row in rows)
        {
            order.AddRange(rightward ? row.OrderBy(t => t.X) : row.OrderByDescending(t => t.X));
            rightward = !rightward;
        }
        return order;
    }
}
