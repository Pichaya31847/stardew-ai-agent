using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace StardewMCP.Agent;

/// <summary>
/// Builds the cabins split-screen farmhands live in, on plain buildable farm ground (debris is cleared, no cost).
/// Used by <see cref="SplitScreen"/> when <c>AutoBuildCabins</c> is on and by the debug API's <c>build_cabins</c>.
/// Host only, game thread.
/// </summary>
public static class Cabins
{
    /// <summary>Build up to <paramref name="want"/> cabins, each with a new farmhand; returns the tiles built at.</summary>
    public static List<string> Build(int want)
    {
        var built = new List<string>();
        if (!Context.IsMainPlayer || want <= 0)
            return built;
        var farm = Game1.getFarm();
        int w = farm.Map.Layers[0].LayerWidth, h = farm.Map.Layers[0].LayerHeight;
        for (int y = 8; y < h - 6 && built.Count < want; y++)
        {
            for (int x = 8; x < w - 8 && built.Count < want; x++)
            {
                // A cabin is 5x3 plus a front row; only use plain buildable ground.
                bool ok = true;
                for (int dy = 0; dy < 4 && ok; dy++)
                    for (int dx = 0; dx < 5 && ok; dx++)
                    {
                        int tx = x + dx, ty = y + dy;
                        if (farm.doesTileHaveProperty(tx, ty, "Buildable", "Back") == null && farm.doesTileHaveProperty(tx, ty, "Diggable", "Back") == null)
                            ok = false;
                        else if (farm.getBuildingAt(new Vector2(tx, ty)) != null || farm.resourceClumps.Any(r => r.occupiesTile(tx, ty)))
                            ok = false;
                    }
                if (!ok) continue;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 5; dx++)
                    {
                        var t = new Vector2(x + dx, y + dy);
                        farm.objects.Remove(t);
                        farm.terrainFeatures.Remove(t);
                    }
                if (!farm.buildStructure("Cabin", new Vector2(x, y), Game1.player, out var b, magicalConstruction: true, skipSafetyChecks: true))
                    continue;
                if (b.GetIndoors() is Cabin cabin && !cabin.HasOwner)
                    cabin.CreateFarmhand();
                built.Add($"({x},{y})");
                x += 6;
            }
        }
        return built;
    }
}
