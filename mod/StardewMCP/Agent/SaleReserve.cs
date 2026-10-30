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

    /// <summary>How many of the <paramref name="inventory"/> may be sold: none of a basic resource, of seeds only on purpose.</summary>
    public static int Available(string qualifiedId, int inventory, bool plantingInput, bool allowReserved) =>
        IsBasicResource(qualifiedId) ? 0 : plantingInput && !allowReserved ? 0 : Math.Max(0, inventory);
}
