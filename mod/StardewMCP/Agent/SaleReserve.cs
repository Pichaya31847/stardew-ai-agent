using System;

namespace StardewMCP.Agent;

/// <summary>Conservative inventory reserves. Explicit intentional sales can override them.</summary>
public static class SaleReserve
{
    public static int Minimum(string qualifiedId) => qualifiedId switch
    {
        "(O)388" => 50, // wood
        "(O)390" => 25, // stone
        "(O)330" or "(O)709" or "(O)382" => 10, // clay, hardwood, coal
        "(O)771" => 20, // fiber
        "(O)378" or "(O)380" or "(O)384" or "(O)386" or "(O)909" => 10, // ores
        "(O)334" or "(O)335" or "(O)336" or "(O)337" or "(O)910" => 5, // bars
        _ => 0
    };

    public static int Available(string qualifiedId, int inventory, bool plantingInput, bool allowReserved) =>
        allowReserved ? Math.Max(0, inventory) : plantingInput ? 0 : Math.Max(0, inventory - Minimum(qualifiedId));
}
