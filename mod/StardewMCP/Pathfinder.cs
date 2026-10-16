using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewMCP;

/// <summary>
/// Plans walking routes for the farmer inside one location: A* over the four cardinal
/// moves, where a tile counts as walkable when the game's own collision check lets the
/// farmer stand on it.
/// </summary>
public class Pathfinder
{
    /// <summary>Tiles expanded before giving up; big maps (Farm, Forest) need tens of thousands.</summary>
    private const int ExpandLimit = 60000;

    private static readonly Point[] Moves = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    private readonly Dictionary<Point, bool> _walkable = new();

    /// <summary>
    /// Route from <paramref name="start"/> to <paramref name="goal"/> as the tiles to step on in
    /// order, excluding the start tile and ending on the goal. Empty when already there, null when
    /// the goal can't be stood on or can't be reached.
    /// </summary>
    public List<Vector2>? FindPath(GameLocation location, Vector2 start, Vector2 goal)
    {
        var from = new Point((int)start.X, (int)start.Y);
        var to = new Point((int)goal.X, (int)goal.Y);
        if (from == to)
            return new List<Vector2>();
        if (!Walkable(location, to))
            return null;

        var steps = new Dictionary<Point, int> { [from] = 0 };
        var parent = new Dictionary<Point, Point>();
        var done = new HashSet<Point>();
        var frontier = new PriorityQueue<Point, int>();
        frontier.Enqueue(from, Distance(from, to));

        while (frontier.TryDequeue(out var tile, out _))
        {
            if (tile == to)
                return Unwind(parent, from, to);
            if (!done.Add(tile))
                continue; // stale queue entry
            if (done.Count > ExpandLimit)
                break;

            int cost = steps[tile] + 1;
            foreach (var move in Moves)
            {
                var next = new Point(tile.X + move.X, tile.Y + move.Y);
                if (done.Contains(next) || (steps.TryGetValue(next, out int known) && known <= cost))
                    continue;
                if (!Walkable(location, next))
                    continue;
                steps[next] = cost;
                parent[next] = tile;
                frontier.Enqueue(next, cost + Distance(next, to));
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the farmer could stand on this tile right now: inside the map and not colliding
    /// with anything the game would stop the player on (walls, water, objects, crops with
    /// trellises, trees, buildings, furniture, villagers, animals).
    /// </summary>
    public static bool IsWalkable(GameLocation location, int x, int y)
    {
        if (!location.isTileOnMap(x, y))
            return false;

        // Same footprint the game uses for a farmer standing on the tile.
        var feet = new Rectangle(x * Game1.tileSize + 8, y * Game1.tileSize + 16, 48, 32);
        return !location.isCollidingPosition(feet, Game1.viewport, true, 0, false, Game1.player);
    }

    /// <summary>Cached <see cref="IsWalkable"/>, since one search probes the same tiles repeatedly.</summary>
    private bool Walkable(GameLocation location, Point tile)
    {
        if (!_walkable.TryGetValue(tile, out bool ok))
            _walkable[tile] = ok = IsWalkable(location, tile.X, tile.Y);
        return ok;
    }

    private static int Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static List<Vector2> Unwind(Dictionary<Point, Point> parent, Point from, Point to)
    {
        var route = new List<Vector2>();
        for (var tile = to; tile != from; tile = parent[tile])
            route.Add(tile.ToVector2());
        route.Reverse();
        return route;
    }
}
