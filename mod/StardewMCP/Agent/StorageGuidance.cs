using System.Linq;
using StardewValley;
using StardewValley.Objects;

namespace StardewMCP.Agent;

public static partial class AgentTools
{
    internal static string StorageHint()
    {
        int used = P.Items.Count(i => i != null && i.Stack > 0);
        int free = P.MaxItems - used;
        if (free > 2) return "";
        var chests = Loc.Objects.Pairs.Where(p => p.Value is Chest c && c.playerChest.Value).Take(4)
            .Select(p => $"({(int)p.Key.X},{(int)p.Key.Y})").ToList();
        string storage = chests.Count > 0
            ? $"FIRST store items not needed for the current trip/job. Storage chests on this map: {string.Join(", ", chests)}. Use chest(action=store,x,y,item/items,reason); keep necessary tools, food and current quest items. List first if ownership is uncertain."
            : "No storage chest found on this map. Look around your farm/home for one; if needed use craft() to inspect the Chest recipe, gather its ingredients (standard Chest needs 50 Wood), craft, place on a free reachable tile away from doors/paths, then chest(action=store).";
        int price = P.MaxItems < 24 ? 2000 : 10000;
        string pack = P.MaxItems >= 36 ? "Backpack is already fully upgraded."
            : $"Backpack: {P.MaxItems} slots now, the next size costs {price:N0}g (you have {P.Money:N0}g). " +
              (P.Money >= price + 1000
                  ? "You can afford it and still buy seeds: upgrade_backpack (walks to Pierre's)."
                  : $"Save up {price + 1000 - P.Money:N0}g more (the price plus seed money), then upgrade_backpack; until then use a chest.");
        return $"Inventory pressure: {free} free slots. Store unused items before gathering/buying more. Do not sleep early or sell reserves just to clear space. {storage} {pack}";
    }
}
