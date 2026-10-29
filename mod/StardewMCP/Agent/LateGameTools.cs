using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Late-game travel (#55): the bus to Calico Desert, Willy's boat to Ginger Island and the Skull Cavern door.
/// Every step calls the game's own handler (BusStop.checkAction, BoatTunnel.checkAction, the Desert and
/// IslandSouth touch actions, and GameLocation's SkullDoor action), so the fare, Pam's schedule, the boat
/// repairs and the mine level are the game's, not a re-implementation.
/// </summary>
public static partial class AgentTools
{
    /// <summary>Maps joined by a paid ride instead of a door or warp; TravelTo rides when a route uses one.</summary>
    private static readonly (string From, string To)[] TransitLinks =
    {
        ("BusStop", "Desert"),
        ("Desert", "BusStop"),
        ("FishShop", "BoatTunnel"),
        ("BoatTunnel", "IslandSouth"),
        ("IslandSouth", "BoatTunnel"),
        ("Town", "MovieTheater"),
        ("MovieTheater", "Town"),
    };

    internal static IEnumerable<string> TransitTargets(string from) =>
        TransitLinks.Where(l => l.From.Equals(from, StringComparison.OrdinalIgnoreCase)).Select(l => l.To);

    private static bool IsTransit(string from, string to) =>
        TransitLinks.Any(l => l.From.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                              l.To.Equals(to, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names that mean the Skull Cavern, which has no map of its own (it is a mine level).</summary>
    internal static bool IsSkullCavernName(string name)
    {
        var compact = name.Replace(" ", "").Replace("_", "").Trim().ToLowerInvariant();
        return compact is "skullcavern" or "skullcaverns" or "skullcave";
    }

    // ------------------------------------------------------------------ transit steps

    private static IEnumerator TakeTransit(string to, CallContext c)
    {
        switch ($"{Loc?.Name}>{to}")
        {
            case "BusStop>Desert": yield return RideBusToDesert(c); break;
            case "Desert>BusStop": yield return RideBusToValley(c); break;
            case "FishShop>BoatTunnel": yield return OpenBoatBackRoom(c); break;
            case "BoatTunnel>IslandSouth": yield return SailToIsland(c); break;
            case "IslandSouth>BoatTunnel": yield return SailToValley(c); break;
            case "Town>MovieTheater": yield return EnterMovieTheater(c); break;
            case "MovieTheater>Town": yield return ExitMovieTheater(c); break;
            default: c.Fail(Reasons.NoPath, $"There is no ride from {Loc?.Name} to {to}."); break;
        }
    }

    /// <summary>Buy a ticket at the bus ticket machine and ride. The game checks Pam, the fare and the repair.</summary>
    private static IEnumerator RideBusToDesert(CallContext c)
    {
        if (!Game1.MasterPlayer.mailReceived.Contains("ccVault"))
        {
            c.Fail(Reasons.ShopClosed, "The bus to Calico Desert is not repaired yet. Complete the Vault bundle in the Community Center, or buy the Bus project from Joja.");
            yield break;
        }

        var loc = Loc;
        var tile = FindTileIndex(loc, 1057);
        if (tile == null) { c.Fail(Reasons.NotHere, "The bus ticket machine was not found in the Bus Stop."); yield break; }

        var walk = new CallContext();
        yield return WalkAdjacent(tile.Value, walk);
        if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Can't reach the bus ticket machine: {walk.Result}"); yield break; }

        if (!PressAction(tile.Value)) { c.Fail(Reasons.NotHere, "Nothing happened at the bus ticket machine."); yield break; }
        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;

        if (P.Money < 500)
        {
            CloseDialogue();
            c.Fail(Reasons.NoMoney, $"A bus ticket to Calico Desert costs 500g; you have {P.Money}g.");
            yield break;
        }
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return Ticks(30);
        yield return WaitForTransit("Desert", 1800, c);
    }

    /// <summary>Walk onto the bus in the Desert; the game's touch action asks the return question.</summary>
    private static IEnumerator RideBusToValley(CallContext c)
    {
        var loc = Loc;
        var tile = FindActionTile(loc, "DesertBus");
        if (tile == null) { c.Fail(Reasons.NotHere, "The return bus was not found in Calico Desert."); yield break; }

        var walk = new CallContext();
        yield return WalkTo(tile.Value, walk);
        yield return Ticks(20);
        if (Game1.activeClickableMenu == null && Loc == loc)
        {
            // The path may stop beside the bus tile. performTouchAction is the same entry point a step onto it uses.
            loc.performTouchAction(new[] { "DesertBus" }, P.getStandingPosition());
            yield return Ticks(30);
        }
        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return WaitForTransit("BusStop", 1800, c);
    }

    /// <summary>Open the back-room door in Willy's shop (the game's WarpBoatTunnel action).</summary>
    private static IEnumerator OpenBoatBackRoom(CallContext c)
    {
        if (!Game1.player.mailReceived.Contains("willyBackRoomInvitation"))
        {
            c.Fail(Reasons.ShopClosed, "Willy has not shown you the back room yet. Wait for his letter about the old boat, then travel here again.");
            yield break;
        }

        var tile = FindActionTile(Loc, "WarpBoatTunnel");
        if (tile == null) { c.Fail(Reasons.NotHere, "The door to Willy's back room was not found."); yield break; }
        var walk = new CallContext();
        yield return WalkAdjacent(tile.Value, walk);
        if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Can't reach Willy's back room: {walk.Result}"); yield break; }
        PressAction(tile.Value);
        yield return WaitForTransit("BoatTunnel", 600, c);
    }

    /// <summary>Buy a 1,000g boat ticket and sail. The game checks the repairs and takes the fare.</summary>
    private static IEnumerator SailToIsland(CallContext c)
    {
        if (!Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatFixed"))
        {
            c.Fail(Reasons.ShopClosed, "Willy's boat is not repaired yet: donate 5 batteries to the ticket machine, 200 hardwood to the hull and 5 iridium bars to the anchor in his back room.");
            yield break;
        }

        var loc = Loc;
        var tile = FindActionTile(loc, "BoatTicket");
        if (tile == null) { c.Fail(Reasons.NotHere, "The boat ticket machine was not found in the boat tunnel."); yield break; }

        var walk = new CallContext();
        yield return WalkAdjacent(tile.Value, walk);
        if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Can't reach the boat ticket machine: {walk.Result}"); yield break; }
        if (!PressAction(tile.Value)) { c.Fail(Reasons.NotHere, "Nothing happened at the boat ticket machine."); yield break; }

        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;
        if (P.Money < 1000)
        {
            CloseDialogue();
            c.Fail(Reasons.NoMoney, $"A boat ticket to Ginger Island costs 1,000g; you have {P.Money}g.");
            yield break;
        }
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return Ticks(30);
        yield return WaitForTransit("IslandSouth", 3600, c);
    }

    /// <summary>Walk onto the island dock; the game's LeaveIsland touch action asks the return question.</summary>
    private static IEnumerator SailToValley(CallContext c)
    {
        var loc = Loc;
        var tile = FindActionTile(loc, "LeaveIsland");
        if (tile == null) { c.Fail(Reasons.NotHere, "The boat back to the valley was not found on Ginger Island."); yield break; }

        var walk = new CallContext();
        yield return WalkTo(tile.Value, walk);
        yield return Ticks(20);
        if (Game1.activeClickableMenu == null && Loc == loc)
        {
            loc.performTouchAction(new[] { "LeaveIsland" }, P.getStandingPosition());
            yield return Ticks(30);
        }
        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return WaitForTransit("BoatTunnel", 3600, c);
    }

    // ------------------------------------------------------------------ Skull Cavern

    /// <summary>Travel to Calico Desert and open the Skull Door, which calls Game1.enterMine(121).</summary>
    private static IEnumerator TravelToSkullCavern(CallContext c)
    {
        if (Loc is MineShaft here && here.mineLevel >= 121)
        {
            c.Result = $"You are already in the Skull Cavern ({WorldText.MineFloorName(here.mineLevel)}).";
            yield break;
        }
        if (!P.hasSkullKey)
        {
            c.Fail(Reasons.ShopClosed, "The Skull Cavern door is locked: you need the Skull Key from floor 120 of the Mines.");
            yield break;
        }

        if (Loc?.Name != "SkullCave")
        {
            var travel = new CallContext();
            yield return TravelTo("SkullCave", travel);
            if (!travel.Ok) { c.Fail(travel.Reason ?? Reasons.NoPath, travel.Result); yield break; }
        }

        yield return WaitIdle();
        var cave = Loc;
        if (cave == null) { c.Fail(Reasons.NotHere, "The Skull Cavern door could not be reached (no current map)."); yield break; }
        var tile = FindActionTile(cave, "SkullDoor");
        if (tile == null) { c.Fail(Reasons.NotHere, "The Skull Cavern door was not found in the Skull Cave entrance."); yield break; }

        var walk = new CallContext();
        yield return WalkAdjacent(tile.Value, walk);
        if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Can't reach the Skull Cavern door: {walk.Result}"); yield break; }

        PressAction(tile.Value);
        yield return Ticks(40);
        // The first use with the key only unlocks the door and prints a message; close it and press again to enter.
        if (Game1.activeClickableMenu is DialogueBox box)
        {
            box.closeDialogue();
            for (int i = 0; i < 120 && Game1.activeClickableMenu != null; i++) yield return null;
            PressAction(tile.Value);
        }

        for (int i = 0; i < 600 && !(Loc is MineShaft shaft && shaft.mineLevel >= 121); i++) yield return null;
        yield return WaitIdle();
        if (Loc is not MineShaft mine || mine.mineLevel < 121)
        {
            c.Fail(Reasons.NotHere, $"The Skull Cavern door did not open (you are in {Loc?.Name}).");
            yield break;
        }
        c.Result = $"Arrived in the Skull Cavern ({WorldText.MineFloorName(mine.mineLevel)}) at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    /// <summary>Enter the Movie Theater from Town (buying a 1,000g ticket if needed).</summary>
    private static IEnumerator EnterMovieTheater(CallContext c)
    {
        if (!Game1.MasterPlayer.hasOrWillReceiveMail("ccMovieTheater"))
        {
            c.Fail(Reasons.ShopClosed, "The movie theater is not restored yet. Complete the Movie Theater bundle in the Community Center, or buy it from Joja.");
            yield break;
        }

        var loc = Loc;
        if (loc == null) { c.Fail(Reasons.NotHere, "No current location."); yield break; }

        // Check if player already has a Movie Ticket
        int ticketSlot = FindItem("Movie Ticket");
        if (ticketSlot < 0)
        {
            if (P.Money < 1000)
            {
                c.Fail(Reasons.NoMoney, $"A movie ticket costs 1,000g; you have {P.Money}g.");
                yield break;
            }
            if (P.isInventoryFull())
            {
                c.Fail(Reasons.InventoryFull, "Your inventory is full; free a slot for the movie ticket.");
                yield break;
            }
            var boxOffice = FindActionTile(loc, "Theater_BoxOffice") ?? new Point(98, 51);
            var walkBox = new CallContext();
            yield return WalkAdjacent(boxOffice, walkBox);
            if (!walkBox.Ok) { c.Fail(walkBox.Reason ?? Reasons.NoPath, $"Can't reach the theater box office: {walkBox.Result}"); yield break; }
            if (PressAction(boxOffice))
            {
                yield return Ticks(30);
                if (Game1.activeClickableMenu is ShopMenu shop)
                {
                    P.Money -= 1000;
                    Game1.playSound("purchaseClick");
                    var ticket = ItemRegistry.Create("(O)809", 1);
                    P.addItemToInventoryBool(ticket);
                    shop.exitThisMenu(false);
                    yield return Ticks(20);
                }
            }
        }

        // Now walk to the theater door (default 95, 50 in Town)
        var doorTile = loc.doors.Pairs.FirstOrDefault(kv => kv.Value == "JojaMart").Key;
        if (doorTile == default) doorTile = new Point(95, 50);
        var walkDoor = new CallContext();
        yield return WalkAdjacent(doorTile, walkDoor);
        if (!walkDoor.Ok) { c.Fail(walkDoor.Reason ?? Reasons.NoPath, $"Can't reach the theater door: {walkDoor.Result}"); yield break; }
        PressAction(doorTile);
        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return WaitForTransit("MovieTheater", 600, c);
    }

    /// <summary>Leave the Movie Theater back to Town.</summary>
    private static IEnumerator ExitMovieTheater(CallContext c)
    {
        var exitTile = new Point(13, 16);
        var walk = new CallContext();
        yield return WalkTo(exitTile, walk);
        yield return Ticks(20);
        if (Game1.activeClickableMenu == null && Loc.Name == "MovieTheater")
        {
            PressAction(exitTile);
            yield return Ticks(20);
        }
        yield return WaitForQuestion(180, c);
        if (!c.Ok) yield break;
        yield return AnswerQuestion("Yes", c);
        if (!c.Ok) yield break;
        yield return WaitForTransit("Town", 600, c);
    }

    /// <summary>The golden walnut counter once the island is open (#55); empty before that.</summary>
    internal static string GoldenWalnutsLine()
    {
        bool unlocked = Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatFixed") ||
                        Game1.MasterPlayer.mailReceived.Contains("willyBackRoomInvitation") ||
                        Loc is IslandLocation;
        if (!unlocked) return "";
        var net = Game1.netWorldState.Value;
        return $"Golden Walnuts {net.GoldenWalnutsFound}/130 found · {net.GoldenWalnuts} unspent";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The closest tile whose Buildings index is <paramref name="index"/>; the bus ticket machine is 1057.</summary>
    private static Point? FindTileIndex(GameLocation loc, int index, string layer = "Buildings", string layerContext = "outdoors")
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        Point me = P.TilePoint;
        Point? best = null;
        int bestDist = int.MaxValue;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (loc.getTileIndexAt(new xTile.Dimensions.Location(x, y), layer, layerContext) != index) continue;
            int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (d < bestDist) { bestDist = d; best = new Point(x, y); }
        }
        return best;
    }

    /// <summary>The closest tile with an Action tile property whose first word is <paramref name="action"/>.</summary>
    private static Point? FindActionTile(GameLocation loc, string action)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        Point me = P.TilePoint;
        Point? best = null;
        int bestDist = int.MaxValue;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var value = loc.doesTileHaveProperty(x, y, "Action", "Buildings")
                     ?? loc.doesTileHaveProperty(x, y, "Action", "Back")
                     ?? loc.doesTileHaveProperty(x, y, "TouchAction", "Back")
                     ?? loc.doesTileHaveProperty(x, y, "TouchAction", "Buildings");
            if (value == null) continue;
            if (!value.Split(' ')[0].Equals(action, StringComparison.OrdinalIgnoreCase)) continue;
            int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
            if (d < bestDist) { bestDist = d; best = new Point(x, y); }
        }
        return best;
    }

    /// <summary>Wait for the ticket question the game opens after the action button.</summary>
    private static IEnumerator WaitForQuestion(int ticks, CallContext c)
    {
        for (int i = 0; i < ticks; i++)
        {
            if (Game1.activeClickableMenu is DialogueBox box && box.isQuestion && !box.transitioning) yield break;
            if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not DialogueBox) yield break;
            yield return null;
        }
        c.Fail(Reasons.MenuOpen, "The ticket dialogue did not open.");
    }

    /// <summary>Answer the open yes/no question by response key or text, retrying until safetyTimer passes.</summary>
    internal static IEnumerator AnswerQuestion(string key, CallContext c)
    {
        DialogueBox? box = null;
        for (int i = 0; i < 60; i++)
        {
            if (Game1.activeClickableMenu is DialogueBox b && b.isQuestion && b.responses != null)
            {
                box = b;
                break;
            }
            yield return Ticks(2);
        }
        if (box == null) { c.Fail(Reasons.MenuOpen, "No question dialogue opened."); yield break; }

        int index = Array.FindIndex(box.responses, r => r.responseKey.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                                         r.responseKey.EndsWith(key, StringComparison.OrdinalIgnoreCase) ||
                                                         (r.responseText ?? "").StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            c.Fail(Reasons.MenuOpen, $"The dialogue question had no '{key}' answer.");
            yield break;
        }

        for (int i = 0; i < 30; i++)
        {
            if (Game1.activeClickableMenu != box) yield break;
            box.selectedResponse = index;
            box.safetyTimer = 0;
            box.transitioning = false;
            box.receiveLeftClick(0, 0);
            yield return Ticks(5);
            if (Game1.activeClickableMenu != box || box.transitioning) yield break;
        }
    }

    private static void CloseDialogue()
    {
        if (Game1.activeClickableMenu is DialogueBox box) box.closeDialogue();
        else if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
    }

    /// <summary>
    /// Wait for a ride to change the map. A plain dialogue that appears after answering means the game refused
    /// (no driver, not enough money, locked door); it is closed and reported instead of waiting for the timeout.
    /// </summary>
    private static IEnumerator WaitForTransit(string target, int ticks, CallContext c)
    {
        for (int i = 0; i < ticks; i++)
        {
            if (Loc?.Name == target) yield break;
            if (Game1.activeClickableMenu is DialogueBox box && !box.isQuestion && !box.transitioning)
            {
                var text = GameEnglish.Said(box).Replace("\n", " ").Replace("^", " ").Trim();
                box.closeDialogue();
                c.Fail(Reasons.ShopClosed, text.Length > 0 ? text : $"The trip to {target} did not start.");
                yield break;
            }
            yield return null;
        }
        c.Fail(Reasons.Timeout, $"The trip to {target} did not finish in time (still in {Loc?.Name}).");
    }
}

/// <summary>
/// Story events for world changes that affect every seat (#55): the bus and Willy's boat being repaired.
/// The first tick remembers the state, so loading an old save does not announce repairs that already happened.
/// </summary>
internal static class WorldProgress
{
    private static bool? _bus;
    private static bool? _boat;

    internal static void Reset()
    {
        _bus = null;
        _boat = null;
    }

    internal static void Tick(AgentRuntime rt)
    {
        if (!StardewModdingAPI.Context.IsWorldReady) return;

        bool bus = Game1.MasterPlayer.mailReceived.Contains("ccVault");
        if (_bus == null) _bus = bus;
        else if (bus && !_bus.Value)
        {
            _bus = true;
            NpcStory.Emit(rt.Log, "bus_repaired", "The bus to Calico Desert is repaired and running.");
        }

        bool boat = Game1.MasterPlayer.mailReceived.Contains("willyBoatFixed");
        if (_boat == null) _boat = boat;
        else if (boat && !_boat.Value)
        {
            _boat = true;
            NpcStory.Emit(rt.Log, "boat_repaired", "Willy's boat is repaired: Ginger Island can be reached from his back room.");
        }
    }
}
