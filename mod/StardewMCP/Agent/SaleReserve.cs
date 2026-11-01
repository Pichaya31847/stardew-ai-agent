using System;
using System.Collections.Generic;

namespace StardewMCP.Agent;

/// <summary>
/// What may not be sold. Basic resources (Wood, Stone, Fiber, Coal, Clay, Hardwood, Sap, ores, bars, Refined Quartz) are never
/// sold: buildings, upgrades, machines and crafting use them by the hundred, so they go into a chest instead (decided
/// 2026-10-30). Seeds and fertilizer are kept for planting unless the sale is deliberate (allow_reserved).
/// </summary>
public static class SaleReserve
{
    /// <summary>Wood, Stone, Fiber, Coal, Clay, Hardwood, Sap, the ores, the bars and Refined Quartz.</summary>
    public static readonly IReadOnlySet<string> BasicResources = new HashSet<string>(StringComparer.Ordinal)
    {
        "(O)388", "(O)390", "(O)771", "(O)382", "(O)330", "(O)709", "(O)92",
        "(O)378", "(O)380", "(O)384", "(O)386", "(O)909",
        "(O)334", "(O)335", "(O)336", "(O)337", "(O)910", "(O)338"
    };

    public static bool IsBasicResource(string qualifiedId) => BasicResources.Contains(qualifiedId);

    /// <summary>Why a shop's basic resource is not bought (decided 2026-11-01: they are gathered, days 10-89 the AIs bought ore), or null for anything else.</summary>
    public static string? BuyRefusal(string qualifiedId, string name) => !IsBasicResource(qualifiedId) ? null
        : qualifiedId is "(O)388" or "(O)709" or "(O)92"
            ? $"Not bought: {name} is never bought, you gather it. Chop trees with use_tool(tool=\"Axe\", tiles=[[x,y]]); when the trees run out, place Pine Cones, Acorns or Maple Seeds on open ground, wait until they are grown, then chop them."
            : qualifiedId is "(O)771" or "(O)330"
                ? $"Not bought: {name} is never bought, you gather it: clear_area on weeds gives Fiber, digging with the Hoe gives Clay."
                : $"Not bought: {name} is never bought, you gather it: stones, ores and Coal come from mine (and clear_area for stones on the farm); bars are smelted from ore you mined.";

    /// <summary>How many of the <paramref name="inventory"/> may be sold: none of a basic resource, of seeds only on purpose.</summary>
    public static int Available(string qualifiedId, int inventory, bool plantingInput, bool allowReserved) =>
        IsBasicResource(qualifiedId) ? 0 : plantingInput && !allowReserved ? 0 : Math.Max(0, inventory);
}
