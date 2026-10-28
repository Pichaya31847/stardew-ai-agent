using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// upgrade_tool: Clint's tool upgrades through the game's own ClintUpgrade shop (Data/Shops). The purchase is the shop
/// menu's own tryToPurchaseItem (via buy), so the tool's actionWhenPurchased hands it to Clint for two days; picking it up
/// is the Blacksmith counter's own action. Nothing is copied from the game.
/// </summary>
public static partial class AgentTools
{
    private const string ClintUpgradeShop = "ClintUpgrade";

    private static ToolDef UpgradeToolTool() => new()
    {
        Name = "upgrade_tool",
        Description = "Have Clint upgrade a tool (Blacksmith, 9:00-16:00, not Fridays; you walk there yourself). Copper 2,000g + 5 Copper Bars, Steel 5,000g + 5 Iron Bars, " +
                      "Gold 10,000g + 5 Gold Bars, Iridium 25,000g + 5 Iridium Bars (bars: smelt ore with coal in a Furnace, see machine). He keeps the tool for 2 days, one tool at a time. " +
                      "tool: Axe, Pickaxe, Hoe, Watering Can or Trash Can; empty lists what he offers you now. When an upgrade is ready, upgrade_tool() picks it up.",
        Schema = Obj(new { tool = Str("Axe, Pickaxe, Hoe, Watering Can or Trash Can; empty to list or to pick up a finished upgrade"), reason = Str(ReasonDesc) }, "reason"),
        Run = UpgradeTool
    };

    private static IEnumerator UpgradeTool(JsonElement a, CallContext c)
    {
        var want = S(a, "tool").Trim();
        var pending = P.toolBeingUpgraded.Value;
        if (pending != null && P.daysLeftForToolUpgrade.Value > 0)
        {
            c.Fail(Reasons.NotReady, $"Clint is still upgrading your {pending.DisplayName}: {P.daysLeftForToolUpgrade.Value} day(s) left. He takes one tool at a time.");
            yield break;
        }

        if (Loc.Name != "Blacksmith")
        {
            var trip = new CallContext();
            yield return TravelTo("Blacksmith", trip);
            if (!trip.Ok || Loc.Name != "Blacksmith") { c.Fail(trip.Reason ?? Reasons.NoPath, $"Couldn't get to Clint at the Blacksmith: {trip.Result}"); yield break; }
        }
        var clint = Loc.characters.FirstOrDefault(n => n.Name == "Clint");
        if (clint == null) { c.Fail(Reasons.ShopClosed, "Clint is not here right now (9:00-16:00, closed Fridays and festival days)."); yield break; }

        // A finished upgrade: the counter hands it over (GameLocation.blacksmith).
        if (pending != null)
        {
            var counter = FindActionTiles(Loc, s => s.StartsWith("Blacksmith", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (counter == Microsoft.Xna.Framework.Point.Zero) counter = clint.TilePoint;
            yield return WalkAdjacent(counter, new CallContext());
            PressAction(counter);
            for (int i = 0; i < 120 && P.toolBeingUpgraded.Value != null; i++) yield return null;
            yield return Ticks(30);
            yield return FinishMessages(new CallContext());
            if (P.toolBeingUpgraded.Value != null) { c.Fail(Reasons.InventoryFull, $"Clint could not hand over your {pending.DisplayName}: make room in your pack and try again."); yield break; }
            c.Result = $"Picked up your upgraded {pending.DisplayName} from Clint.";
            if (want.Length == 0) yield break;
        }

        yield return WalkAdjacent(clint.TilePoint, new CallContext());
        if (Game1.activeClickableMenu != null) yield return FinishMessages(new CallContext());
        Utility.TryOpenShopMenu(ClintUpgradeShop, "Clint");
        for (int i = 0; i < 60 && Game1.activeClickableMenu is not ShopMenu; i++) yield return null;
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail(Reasons.ShopClosed, "Clint's upgrade counter did not open."); yield break; }

        var offers = shop.forSale.Select(s => (sale: s, st: shop.itemPriceAndStock.TryGetValue(s, out var st) ? st : null)).ToList();
        string Offer((ISalable sale, ItemStockInformation? st) o) =>
            o.st == null ? o.sale.DisplayName
                : $"{o.sale.DisplayName}: {o.st.Price}g{(o.st.TradeItem != null ? $" + {o.st.TradeItemCount ?? 5} {ItemRegistry.GetData(o.st.TradeItem)?.DisplayName ?? o.st.TradeItem}" : "")}";
        if (want.Length == 0)
        {
            shop.exitThisMenu();
            c.Result = (c.Result.Length > 0 ? c.Result + "\n" : "") +
                       (offers.Count == 0 ? "Clint offers no upgrade for the tools you carry (each one needs the tool in your pack)." : "Clint can upgrade: " + string.Join("; ", offers.Select(Offer)) + ". upgrade_tool(tool=...) orders one.");
            yield break;
        }

        // "Axe" must not pick the Pickaxe: an exact name, then a name ending in the word.
        var w = want.ToLowerInvariant();
        var match = offers.FirstOrDefault(o => o.sale.DisplayName.Equals(want, StringComparison.OrdinalIgnoreCase)).sale
                    ?? offers.FirstOrDefault(o => o.sale.DisplayName.ToLowerInvariant().EndsWith(" " + w) || o.sale.DisplayName.ToLowerInvariant().EndsWith(w) && !(w == "axe" && o.sale.DisplayName.ToLowerInvariant().EndsWith("pickaxe"))).sale;
        if (match == null)
        {
            shop.exitThisMenu();
            c.Fail(Reasons.NotFound, $"Clint offers no upgrade called \"{want}\". " + (offers.Count == 0 ? "Carry the tool in your pack first." : "He offers: " + string.Join("; ", offers.Select(Offer)) + "."));
            yield break;
        }
        var buy = new CallContext();
        yield return Buy(ArgsOf(new { item = match.DisplayName, quantity = 1, reason = "tool upgrade" }), buy);
        if (Game1.activeClickableMenu is ShopMenu open) open.exitThisMenu();
        if (P.toolBeingUpgraded.Value == null)
        {
            c.Fail(buy.Reason ?? Reasons.NoMoney, $"Clint did not take the upgrade: {buy.Result}");
            yield break;
        }
        c.Result = (c.Result.Length > 0 ? c.Result + " " : "") +
                   $"Clint is upgrading your tool to {match.DisplayName}: pick it up in {Math.Max(1, P.daysLeftForToolUpgrade.Value)} day(s) with upgrade_tool(). {P.Money}g left.";
    }
}
