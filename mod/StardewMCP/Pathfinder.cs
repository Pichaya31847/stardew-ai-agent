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
public partial class Pathfinder
{
    // The search itself (and the IWalkableGrid it runs over) lives in PathSearch.cs, which has no
    // game types so the unit tests can compile it. This half asks the game's collision check.

    /// <summary>
    /// Route from <paramref name="start"/> to <paramref name="goal"/> as the tiles to step on in
    /// order, excluding the start tile and ending on the goal. Empty when already there, null when
    /// the goal can't be stood on or can't be reached.
    /// </summary>
    public List<Vector2>? FindPath(GameLocation location, Vector2 start, Vector2 goal) =>
        FindPath(new LocationGrid(location), start, goal);

    /// <summary>As <see cref="FindPath(GameLocation, Vector2, Vector2)"/>, never stepping on <paramref name="avoid"/> (where villagers stand).</summary>
    public List<Vector2>? FindPath(GameLocation location, Vector2 start, Vector2 goal, ISet<Point> avoid) =>
        FindPath(new AvoidingGrid(new LocationGrid(location), avoid), start, goal);

    private sealed class AvoidingGrid : IWalkableGrid
    {
        private readonly IWalkableGrid _inner;
        private readonly ISet<Point> _avoid;

        public AvoidingGrid(IWalkableGrid inner, ISet<Point> avoid) { _inner = inner; _avoid = avoid; }

        public bool IsWalkable(int x, int y) => !_avoid.Contains(new Point(x, y)) && _inner.IsWalkable(x, y);
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
        if (!location.isCollidingPosition(feet, Game1.viewport, true, 0, false, Game1.player))
            return true;

        return CanPassDoor(location, x, y, Game1.player);
    }

    public static bool CanPassDoor(GameLocation location, int x, int y, Farmer? who)
    {
        if (who == null) return false;
        var action = location.doesTileHaveProperty(x, y, "Action", "Buildings");
        if (string.IsNullOrEmpty(action)) return false;
        var parts = action.Split(' ');
        if (!parts[0].Equals("Door", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == 1) return true;
        for (int i = 1; i < parts.Length; i++)
        {
            var name = parts[i];
            if (int.TryParse(name, out _)) continue;
            if (who.getFriendshipHeartLevelForNPC(name) >= 2 || who.spouse == name)
                return true;
        }
        return false;
    }

    /// <summary>The game's collision check as an <see cref="IWalkableGrid"/>.</summary>
    private sealed class LocationGrid : IWalkableGrid
    {
        private readonly GameLocation _location;

        public LocationGrid(GameLocation location) => _location = location;

        public bool IsWalkable(int x, int y) => Pathfinder.IsWalkable(_location, x, y);
    }
}
