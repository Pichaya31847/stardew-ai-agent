using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;

namespace StardewMCP.Agent;

/// <summary>
/// Builds the cabins split-screen farmhands live in, on plain buildable farm ground (wild debris is cleared, no cost).
/// Used by <see cref="SplitScreen"/> when <c>AutoBuildCabins</c> is on and by the debug API's <c>build_cabins</c>.
/// Runs on real saves too, so it never builds over anything a player made or planted. Host only, game thread.
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
                        ok = Free(farm, x + dx, y + dy);
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

    /// <summary>
    /// Buildable ground that is empty or holds only wild debris (weeds, twigs, stones, grass, saplings, stumps, empty
    /// tilled soil). Chests, machines, fences, paths, crops, grown trees, fruit trees and bushes rule the tile out.
    /// </summary>
    private static bool Free(Farm farm, int x, int y)
    {
        if (farm.doesTileHaveProperty(x, y, "Buildable", "Back") == null && farm.doesTileHaveProperty(x, y, "Diggable", "Back") == null)
            return false;
        var v = new Vector2(x, y);
        if (farm.getBuildingAt(v) != null || farm.resourceClumps.Any(r => r.occupiesTile(x, y)))
            return false;
        if (farm.objects.TryGetValue(v, out var obj) && !(obj.IsWeeds() || obj.IsTwig() || obj.IsBreakableStone()))
            return false;
        if (farm.terrainFeatures.TryGetValue(v, out var tf))
        {
            bool wild = tf switch
            {
                Grass => true,
                HoeDirt dirt => dirt.crop == null,
                Tree tree => tree.stump.Value || tree.growthStage.Value < 5,
                _ => false
            };
            if (!wild)
                return false;
        }
        var rect = new Rectangle(x * 64, y * 64, 64, 64);
        return !farm.largeTerrainFeatures.Any(f => f.getBoundingBox().Intersects(rect));
    }
}
