using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StardewValley;

namespace StardewMCP.Agent;

public static partial class AgentTools
{
    private static string? SaleProtectionError(Item item, int quantity, JsonElement args)
    {
        bool allow = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("allow_reserved", out var value) && value.ValueKind == JsonValueKind.True;
        int count = P.Items.Where(i => i != null && i.QualifiedItemId == item.QualifiedItemId).Sum(i => i.Stack);
        bool planting = item.Category == StardewValley.Object.SeedsCategory || item.Category == StardewValley.Object.fertilizerCategory;
        if (SaleReserve.IsBasicResource(item.QualifiedItemId))
            return $"Not sold: {item.DisplayName} is a basic resource (Wood, Stone, Fiber, Coal, Clay, Hardwood, Sap, ores, bars). Buildings, " +
                   "upgrades, machines and crafting use them by the hundred, so they are never sold: store them with chest(action=\"store\") " +
                   "or chest(action=\"tidy\"), and sell crops, forage, fish or artisan goods instead.";
        int available = SaleReserve.Available(item.QualifiedItemId, count, planting, allow);
        if (Math.Min(quantity, count) <= available) return null;
        return $"Sale prevented: {item.DisplayName} is reserved for planting/crafting. You carry {count}; safe surplus to sell is {available}. " +
            "Keep seeds/fertilizer for planting and store spare supplies in a chest. Sell harvested produce or other genuine surplus instead. " +
            "Only for an intentional sale (e.g. unusable seasonal seeds or excess reserves), call again with allow_reserved=true and explain why in reason. Do not set this just to clear an error. Check active quests, bundles and your plan first.";
    }

    /// <summary>Read nearby map actions, within look's maximum radius. Never opens a menu or performs a purchase.</summary>
    private static string ShopCounterHint()
    {
        if (Loc?.Map == null) return "";
        var tiles = new List<string>();
        var position = P.TilePoint;
        int width = Loc.Map.Layers[0].LayerWidth, height = Loc.Map.Layers[0].LayerHeight;
        for (int y = Math.Max(0, position.Y - 20); y <= Math.Min(height - 1, position.Y + 20) && tiles.Count < 8; y++)
        for (int x = Math.Max(0, position.X - 20); x <= Math.Min(width - 1, position.X + 20) && tiles.Count < 8; x++)
        {
            var action = Loc.doesTileHaveProperty(x, y, "Action", "Buildings")
                         ?? Loc.doesTileHaveProperty(x, y, "Action", "Back");
            if (string.IsNullOrEmpty(action)) continue;
            string token = action.Split(' ')[0];
            if (!token.Contains("Shop", StringComparison.OrdinalIgnoreCase) &&
                token != "Blacksmith" && token != "Carpenter" && token != "Saloon") continue;
            tiles.Add($"({x},{y}) {ActionTiles.Label(action)}");
            if (tiles.Count == 8) break;
        }
        if (tiles.Count == 0) return "";
        return "\nNearby shop action tiles: " + string.Join("; ", tiles) +
               ". travel_to only reaches the building. Use look to check the counter, then interact(x,y,reason) on its actual tile; interact walks you next to it. Only after ShopMenu opens use shop_list / buy / sell, then close_menu.";
    }
}
