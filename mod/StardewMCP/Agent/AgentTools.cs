using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>The actions the AI can take. Mechanics (walking, aiming, button presses) are automated; decisions are the AI's.</summary>
public static partial class AgentTools
{
    /// <summary>Runtime of the seat whose screen is updating (tools run on that screen's tick).</summary>
    public static AgentRuntime Rt => Players.Current.Runtime;

    /// <summary>Shared settings; set once at startup so tool schemas can be built on any thread.</summary>
    public static ModConfig Config = new();
    private static Farmer P => Game1.player;
    private static GameLocation Loc => Game1.currentLocation;

    // ------------------------------------------------------------------ schema helpers

    // Str, Int, Tiles, Obj (schema) and Reason, ArgsForLog, S, I, Has, TileList (arguments) are in ToolArgs.cs.

    /// <summary>Language for text shown to viewers (config ThoughtLanguage).</summary>
    private static string Lang => Config.LanguageName();

    /// <summary>Language of action reasons and the memory files (config WorkLanguage, default ThoughtLanguage).</summary>
    private static string WorkLang => Config.WorkLanguageName();

    private static string ReasonDesc => WorkLang.Equals("Thai", StringComparison.OrdinalIgnoreCase)
        ? "Short reason IN THAI, in plain words with no coordinates or numbers (e.g. \"ถางหญ้าเพื่อเตรียมแปลงปลูก\")"
        : $"Short reason IN {WorkLang.ToUpperInvariant()}, in plain words with no coordinates or numbers";

    private static string StatusText()
    {
        var prof = Professions.PendingText(Rt, P);
        return prof.Length > 0 ? WorldText.Status() + "\n" + prof : WorldText.Status();
    }

    public static string Footer()
    {
        var sb = new StringBuilder($"[{WorldText.FormatTime(Game1.timeOfDay)} · {Loc?.Name} ({P.TilePoint.X},{P.TilePoint.Y}) · energy {(int)P.Stamina}/{P.MaxStamina}{MineHealthFooter()} · {P.Money}g]");
        sb.Append(MineLateWarning());
        var menu = WorldText.MenuState().Trim();
        if (menu.Length > 0) sb.Append('\n').Append(menu);
        var prof = Professions.PendingText(Rt, P);
        if (prof.Length > 0) sb.Append('\n').Append(prof);
        var mail = Briefing.MailHint();
        if (mail.Length > 0) sb.Append('\n').Append(mail);
        var ranch = RanchText.FooterNote();
        if (ranch.Length > 0) sb.Append('\n').Append(ranch);
        var night = NightEvents.PendingText(Rt.Log.Player);
        if (night.Length > 0) sb.Append('\n').Append(night);
        var storage = StorageHint();
        if (storage.Length > 0) sb.Append('\n').Append(storage);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ registry

    public static List<ToolDef> All()
    {
        return new List<ToolDef>(SocialTools.All().Concat(JournalTools.All()).Concat(EconomyTools.All()).Concat(KnowledgeTools.All()).Concat(VillagerTools()).Concat(RomanceTools()).Concat(NamingTools()).Concat(Core()).Concat(CraftingToolDefs()).Concat(QolToolDefs()).Concat(CalendarToolDefs()).Concat(CommunityTools()).Concat(JojaTools()).Concat(BuildingToolDefs()).Concat(AnimalToolDefs()).Concat(ClothingToolDefs()).Concat(HorseToolDefs()).Concat(ChoreToolDefs()));
    }

    private static List<ToolDef> Core()
    {
        return new List<ToolDef>
        {
            new()
            {
                Name = "think",
                Description = $"Say what you are thinking/planning, IN {Lang.ToUpperInvariant()}, 1-3 sentences. It is your private thought: nobody else in the game sees it. Talk to yourself like a person, with no tile coordinates, decimals or tool names. Only for the morning plan and after a surprise: " +
                              "otherwise write your thought as the text of the reply that calls the action, which the viewers see the same way. Game time is frozen while you think.",
                Schema = Obj(new { text = Str($"Your thought in {Lang}") }, "text"),
                Instant = true, NeedsWorld = false, AppendStatus = false,
                Run = Think
            },
            new()
            {
                Name = "status",
                Description = "Current day, time, weather (and tomorrow's), daily luck, location, position, energy, money, held item and any open dialogue/menu.",
                Schema = Obj(new { }),
                Instant = true, AppendStatus = false,
                Run = (a, c) => Instant(c, StatusText())
            },
            new()
            {
                Name = "look",
                Description = "ASCII map of the area around you with coordinates, plus a list of everything in view (trees, stones, crops, doors, villagers...) with exact (x,y) tiles. Use it to choose targets. " +
                              "Keep it small: the default radius is 8, kinds limits the list to what you need, and diff=true reports only the tiles that changed since your last look (use it to check the result of your work).",
                Schema = Obj(new
                {
                    radius = Int($"Tiles around you, 5-20 (default {Observation.DefaultRadius})"),
                    kinds = new { type = "array", description = $"Only list these kinds of things: {Observation.KindNames} (default: all). The map itself is always complete.", items = new { type = "string" } },
                    diff = new { type = "boolean", description = "Only report the tiles that changed since your last look on this map (falls back to the full view when there is nothing to compare with)" }
                }),
                Instant = true,
                Run = (a, c) => Instant(c, WorldText.Look(Math.Clamp(I(a, "radius", Observation.DefaultRadius), 5, 20), Observation.Kinds(a), Observation.Flag(a, "diff")))
            },
            new()
            {
                Name = "inventory",
                Description = "Your inventory slots, item names, counts, and which items are edible.",
                Schema = Obj(new { }),
                Instant = true,
                Run = (a, c) => Instant(c, WorldText.Inventory())
            },
            new()
            {
                Name = "walk_to",
                Description = "Walk to a tile on the current map (pathfinding is automatic). If the tile is blocked or someone stands on it you stop next to it. Time passes while walking.",
                Schema = Obj(new { x = Int("Tile x"), y = Int("Tile y"), reason = Str(ReasonDesc) }, "x", "y", "reason"),
                Run = WalkToTool
            },
            new()
            {
                Name = "use_tool",
                Description = "Use a tool on one or more tiles, in order. You automatically walk next to each tile, face it and swing. " +
                              "Axe/pickaxe/scythe keep swinging until the target (tree, stone, weeds, stump...) is gone or max_hits is reached. " +
                              "Hoe tills each tile once. Watering can waters each tile once; use it on a water tile (~) to refill. Stops early if energy runs out.",
                Schema = Obj(new
                {
                    tool = Str("Tool name: Axe, Pickaxe, Hoe, Watering Can, Scythe"),
                    tiles = Tiles("List of [x,y] tiles, max 40"),
                    max_hits = Int("Max swings per tile for axe/pickaxe (default 20)"),
                    reason = Str(ReasonDesc)
                }, "tool", "tiles", "reason"),
                Run = UseTool
            },
            CombatTool(),
            MineTool(),
            LeaveMineTool(),
            new()
            {
                Name = "plant",
                Description = "Plant seeds (or apply fertilizer) on tilled soil tiles (=). You walk to each tile automatically. " +
                              "Give tiles, or area [x,y,w,h] to fill the empty tilled soil inside it, or neither to fill every empty tilled tile on the map until the seeds run out.",
                Schema = Obj(new
                {
                    item = Str("Seed/fertilizer name, e.g. \"Parsnip Seeds\""),
                    tiles = Tiles("List of [x,y] tilled tiles, max 40"),
                    area = new { type = "array", description = "Alternative to tiles: rectangle [x, y, w, h], x,y = top-left tile", items = new { type = "integer" }, minItems = 4, maxItems = 4 },
                    reason = Str(ReasonDesc)
                }, "item", "reason"),
                Run = PlantTool
            },
            new()
            {
                Name = "harvest",
                Description = "Harvest ready crops (!) or pick up forage items (F) on the given tiles.",
                Schema = Obj(new { tiles = Tiles("List of [x,y] tiles, max 40"), reason = Str(ReasonDesc) }, "tiles", "reason"),
                Run = Harvest
            },
            FarmSummaryTool(), WaterAllTool(), HarvestAllTool(), ClearAreaTool(), TillAreaTool(), // FarmTools.cs
            new()
            {
                Name = "interact",
                Description = "Walk next to a tile and press the action button on it: open doors, shop counters (A tiles), chests, machines, mailbox, read signs, check objects. " +
                              "With holding, hold that item while pressing: load a machine with it (e.g. Copper Ore into a Furnace), give it to the villager on that tile. " +
                              "With holding and no tile, use the item where you stand (totems, books). Without holding you hold a tool, so nothing is loaded or gifted by accident. " +
                              "You MUST supply BOTH x and y from look for a counter, chest, door or object. A reason alone never presses anything. travel_to a shop stops at its entrance, not its counter. Letters are read and closed with attachments/quests claimed; ordinary dialogue is completed and its text returned. Explicit questions remain for respond(option).",
                Schema = Obj(new
                {
                    x = Int("Tile x"),
                    y = Int("Tile y"),
                    holding = Str("Optional item from your inventory to hold while pressing"),
                    reason = Str(ReasonDesc)
                }, "reason"),
                Run = Interact
            },
            new()
            {
                Name = "respond",
                Description = "Continue the open dialogue box, or answer a question by option number.",
                Schema = Obj(new { option = Int("Option number for questions; omit to just continue"), reason = Str(ReasonDesc) }),
                Run = Respond
            },
            new()
            {
                Name = "close_menu",
                Description = "Close the open menu, shop, letter or dialogue. Closing a letter accepts its quest and hands over its attachments; " +
                              "closing a treasure or gift menu first takes whatever fits in your inventory (chests keep their items).",
                Schema = Obj(new { }),
                Run = CloseMenu
            },
            MenuTool(),
            FishTool(),
            new()
            {
                Name = "travel_to",
                Description = "Walk to another map through doors and exits: Farm, FarmHouse, BusStop, Town, SeedShop (Pierre's), Forest (Marnie), Beach, Mountain, Mine, Saloon, Blacksmith, etc. " +
                              "You can also name a shop keeper (Pierre, Clint, Robin, Marnie, Willy, Gus: their shop), any villager or another farmer (the map they are in now). " +
                              "Late game: Desert and Calico Desert (500g bus from the Bus Stop, after the Vault bundle or Joja Bus), Skull Cavern (the Skull Key opens the desert door), " +
                              "Ginger Island (1,000g boat from Willy's back room, after the boat is repaired), Casino, MovieTheater and Oasis. " +
                              "Locked doors stay locked outside opening hours; the failure says when they open.",
                Schema = Obj(new { location = Str("Destination map name, shop keeper, villager or farmer"), reason = Str(ReasonDesc) }, "location", "reason"),
                Run = TravelTo
            },
            new()
            {
                Name = "shop_list",
                Description = "List the items for sale with prices. Inside a shop it opens the counter first (walks to it), like buy and sell.",
                Schema = Obj(new { reason = Str("Optional: why you look at the shop") }),
                Run = ShopList
            },
            new()
            {
                Name = "buy",
                Description = "Buy an item from the open shop menu.",
                Schema = Obj(new { item = Str("Item name as listed by shop_list"), quantity = Int("How many"), reason = Str(ReasonDesc) }, "item", "quantity", "reason"),
                Run = Buy
            },
            new()
            {
                Name = "ship",
                Description = "Put surplus items into the farm shipping bin (travels to the Farm from anywhere and walks to the bin). Shipped items are sold overnight. Seeds, fertilizer and minimum crafting supplies are protected by default. Use a chest for storage, not the bin.",
                Schema = Obj(new { item = Str("Item name"), quantity = Int("How many (default: all)"), allow_reserved = new { type = "boolean", description = "Default false. Only true for a deliberate sale of reserved supplies; explain why in reason, never just to bypass an error." }, reason = Str(ReasonDesc) }, "item", "reason"),
                Run = Ship
            },
            new()
            {
                Name = "eat",
                Description = "Eat an edible item from your inventory to restore energy.",
                Schema = Obj(new { item = Str("Item name"), reason = Str(ReasonDesc) }, "item", "reason"),
                Run = Eat
            },
            EquipTool(), SpecialOrdersTool(),
            new()
            {
                Name = "wait",
                Description = "Let game time pass (e.g. until a shop opens).",
                Schema = Obj(new { minutes = Int("Game minutes, 10-240"), reason = Str(ReasonDesc) }, "minutes", "reason"),
                Run = Wait
            },
            new()
            {
                Name = "rest",
                Description = "Lie down on your own bed to get energy and health back while game time passes (in bed the game gives about " +
                              "+14 energy per 10 minutes). Walks home first. A rest, not the night's sleep: it ends when your energy reaches energy (default full), " +
                              "after the minutes you give, or when someone calls you, gives you something or comes into view; then you get up and walk to the door, " +
                              "ready to carry on. Use it when you run out of energy before the evening. go_to_bed ends the day.",
                Schema = Obj(new
                {
                    minutes = Int("At most this many game minutes, 10-240 (default: until energy is reached)"),
                    energy = Int("Get up once energy is at least this much (default: full)"),
                    reason = Str(ReasonDesc)
                }, "reason"),
                Run = Rest
            },
            new()
            {
                Name = "choose_profession",
                Description = "Make a pending profession choice (offered at skill level 5 and 10, shown in status). It is permanent and changes what pays off, so read both options first.",
                Schema = Obj(new { option = Int("0 or 1, as numbered in status"), skill = Str("Skill name if more than one choice is pending (Farming, Fishing, Foraging, Mining, Combat)"), reason = Str(ReasonDesc) }, "option", "reason"),
                Run = ChooseProfession
            },
            new()
            {
                Name = "go_to_bed",
                Description = "End the day: walk home to your bed and sleep. Write today's journal first (the journal tool: diary, lessons and plan); go_to_bed refuses until it is written. Crops grow and shipped items are sold overnight.",
                Schema = Obj(new { reason = Str(ReasonDesc) }, "reason"),
                Run = GoToBed
            }
        };
    }

    // ------------------------------------------------------------------ simple tools

    internal static IEnumerator Instant(CallContext c, string text)
    {
        c.Result = text;
        yield break;
    }

    private static IEnumerator Think(JsonElement a, CallContext c)
    {
        var text = TextArg(a, "text");
        if (text.Length == 0)
        {
            // An empty think is a model that wants to pause, not a mistake worth a failure (11 of them on days 21-28): move it on.
            c.Result = "Nothing was written. Don't think again now: call the tool for your next step.";
            yield break;
        }
        if (LanguageGuard.Reject(text, Lang, "thought") is { } drift) { c.Fail(Reasons.InvalidArgs, drift); yield break; }
        Rt.Log.Add("think", text: text);
        c.Result = "ok";
    }

    private static IEnumerator WalkToTool(JsonElement a, CallContext c)
    {
        var target = new Point(I(a, "x", -1), I(a, "y", -1));
        if (Pathfinder.IsWalkable(Loc, target.X, target.Y) && !FarmerOn(Loc, target))
            yield return WalkTo(target, c);
        else
            yield return WalkAdjacent(target, c);
        if (!c.Ok && c.Reason == Reasons.NoPath && NearestReachable(target) is Point near)
        {
            // Days 46-56: 46 walk_to calls failed with "No reachable tile next to", most toward a pond to refill the can.
            var sub = new CallContext();
            yield return WalkTo(near, sub);
            if (sub.Ok)
            {
                bool water = Loc.isWaterTile(target.X, target.Y);
                c.Ok = true;
                c.Partial = true;
                c.Reason = Reasons.Partial;
                c.Result = $"({target.X},{target.Y}) can't be reached; stopped at the closest reachable tile ({P.TilePoint.X},{P.TilePoint.Y})." +
                           (water ? " The watering can refills itself in water_all and use_tool when it runs dry." : "");
                yield break;
            }
        }
        if (c.Ok)
            c.Result = $"Arrived at ({P.TilePoint.X},{P.TilePoint.Y}).";
    }

    /// <summary>A walkable tile within 6 of <paramref name="target"/> with a path from here, nearest to the target first; null if none.</summary>
    private static Point? NearestReachable(Point target)
    {
        var loc = Loc;
        var me = P.TilePoint;
        var finder = new Pathfinder();
        int tried = 0;
        for (int r = 1; r <= 6 && tried < 12; r++)
            foreach (var p in TilesAtDistance(target, r))
            {
                if (!Pathfinder.IsWalkable(loc, p.X, p.Y) || FarmerOn(loc, p)) continue;
                if (p == me) return p;
                if (++tried > 12) return null;
                if (finder.FindPath(loc, me.ToVector2(), p.ToVector2()) != null) return p;
            }
        return null;
    }

    private static IEnumerable<Point> TilesAtDistance(Point c, int r)
    {
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
                if (Math.Abs(dx) + Math.Abs(dy) == r) yield return new Point(c.X + dx, c.Y + dy);
    }

    private static IEnumerator Wait(JsonElement a, CallContext c)
    {
        int minutes = Math.Clamp(I(a, "minutes", 30), 10, 240);
        int start = Utility.ConvertTimeToMinutes(Game1.timeOfDay);
        int day = Game1.Date.TotalDays;
        while (Game1.Date.TotalDays == day && Utility.ConvertTimeToMinutes(Game1.timeOfDay) - start < minutes)
            yield return null;
        c.Result = $"Waited until {WorldText.FormatTime(Game1.timeOfDay)}.";
    }

    /// <summary>Lie on the bed while time passes: the game itself regenerates a multiplayer farmer standing on a bed tile.</summary>
    private static IEnumerator Rest(JsonElement a, CallContext c)
    {
        // Farmer.Update regenerates a farmer in bed only in multiplayer (and alone, the bed's question would stop the clock)
        if (!Game1.IsMultiplayer)
        {
            c.Fail(Reasons.NotReady, "Lying in bed only gives energy back while other farmers are in the game. Eat something, or go_to_bed to end the day.");
            yield break;
        }
        int minutes = Math.Clamp(I(a, "minutes", 240), 10, 240);
        int target = Math.Clamp(I(a, "energy", P.MaxStamina), 1, P.MaxStamina);
        if (Loc is not FarmHouse)
        {
            var sub = new CallContext();
            yield return TravelTo("FarmHouse", sub);
            if (!sub.Ok) { c.Fail(sub.Reason ?? Reasons.NoPath, $"Couldn't get home: {sub.Result}"); yield break; }
        }
        var walk = new CallContext();
        yield return WalkTo(((FarmHouse)Loc).GetPlayerBedSpot(), walk);
        if (!walk.Ok) { c.Fail(walk.Reason ?? Reasons.NoPath, $"Couldn't reach your bed: {walk.Result}"); yield break; }
        yield return Ticks(10); // the bed's question opens a moment after stepping on it
        DismissBedQuestion();

        int energy = (int)P.Stamina, health = P.health, start = Utility.ConvertTimeToMinutes(Game1.timeOfDay), day = Game1.Date.TotalDays;
        while (Game1.Date.TotalDays == day && Utility.ConvertTimeToMinutes(Game1.timeOfDay) - start < minutes
               && (P.Stamina < target || !Has(a, "energy") && P.health < P.maxHealth))
        {
            DismissBedQuestion();
            yield return null;
        }

        // Get up and step off the bed, or its "Go to sleep?" question comes back and the farmer stays put.
        DismissBedQuestion();
        Rt.Stats.Count("rest");
        if (Game1.Date.TotalDays == day && Loc is FarmHouse home)
            yield return WalkTo(home.getEntryLocation(), new CallContext());
        c.Result = $"Rested in bed until {WorldText.FormatTime(Game1.timeOfDay)}: energy {energy} → {(int)P.Stamina}/{P.MaxStamina}, " +
                   $"health {health} → {P.health}/{P.maxHealth}. You are up again by the door: carry on with the day. " + PendingFarmWork();
    }

    /// <summary>Close the bed's own "Go to sleep for the night?" question (stepping onto a bed opens it): a rest is not the night's
    /// sleep, and an open box would keep every interrupt and early reply away (Perception.SafeToStop).</summary>
    private static void DismissBedQuestion()
    {
        if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } box && Loc.lastQuestionKey == "Sleep")
        {
            box.closeDialogue();
            Game1.dialogueUp = false;
        }
    }

    // ------------------------------------------------------------------ movement helpers

    /// <summary>Wait until the farmer can act again (tool animations, warps, fades).</summary>
    private static IEnumerator WaitIdle(int maxTicks = 600)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if (!P.UsingTool && P.CanMove && !Game1.isWarping && !Game1.fadeToBlack && !P.isEating && Game1.activeClickableMenu == null)
                yield break;
            if (Game1.activeClickableMenu != null && !P.UsingTool && !Game1.isWarping)
                yield break; // a menu/dialogue opened: let the AI handle it
            yield return null;
        }
    }

    private static IEnumerator Ticks(int n)
    {
        for (int i = 0; i < n; i++) yield return null;
    }

    /// <param name="stopWhen">Checked every tick: true ends the walk early without a failure (the caller re-plans, e.g. a villager moved).</param>
    public static IEnumerator WalkTo(Point target, CallContext c, Func<bool>? stopWhen = null)
    {
        yield return WaitIdle();
        var loc = Loc;
        var start = P.TilePoint;
        if (start == target) yield break;

        var path = new Pathfinder().FindPath(loc, start.ToVector2(), target.ToVector2());
        if (path == null && !_clearingWay && ClearsWayOn(loc))
        {
            // Walled in by weeds, stones and twigs (days 22-26: no way out of the Farm or Backwoods): cut a way through them.
            var cleared = new CallContext();
            yield return ClearWayTo(loc, target, cleared);
            if (cleared.Ok && Loc == loc)
                path = new Pathfinder().FindPath(loc, P.TilePoint.ToVector2(), target.ToVector2());
            if (path == null && cleared.Result.Length > 0)
            {
                c.Fail(Reasons.NoPath, $"No walkable path to ({target.X},{target.Y}) on {loc.Name}: {cleared.Result}");
                yield break;
            }
        }
        if (path == null)
        {
            c.Fail(Reasons.NoPath, $"No walkable path to ({target.X},{target.Y}) on {loc.Name}.");
            yield break;
        }

        foreach (var step in path)
        {
            int sx = (int)step.X, sy = (int)step.Y;
            if (Pathfinder.CanPassDoor(loc, sx, sy, P))
                loc.openDoor(new xTile.Dimensions.Location(sx, sy), true);
        }

        var stack = new Stack<Point>();
        for (int i = path.Count - 1; i >= 0; i--) stack.Push(new Point((int)path[i].X, (int)path[i].Y));
        P.controller = new PathFindController(stack, P, loc) { NPCSchedule = false };
        P.setRunning(true, true);

        int limit = 60 * 5 + path.Count * 40;
        int stuck = 0, replans = 0;
        var last = P.Position;
        var menuAtStart = Game1.activeClickableMenu;
        for (int t = 0; t < limit; t++)
        {
            if (Game1.currentLocation != loc) yield break; // walked through a warp
            // Stepped onto a warp: stop pushing into it, or the game re-warps (restarting the fade) every tick.
            if (Game1.isWarping) { P.controller = null; yield break; }
            if (stopWhen?.Invoke() == true) { P.controller = null; P.Halt(); yield break; }
            // A message that pops up mid-walk is a locked door the farmer bumped into ("not close enough to enter her room").
            // Day 56: talk_to Caroline pushed AI2 into Pierre's bedroom door for 6 real minutes while AI1 waited in bed.
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: false } bump && !ReferenceEquals(bump, menuAtStart))
            {
                var said = GameEnglish.Said(bump).Replace("\n", " ").Trim();
                bump.closeDialogue();
                Game1.dialogueUp = false;
                P.controller = null;
                P.Halt();
                c.Fail(Reasons.NoPath, $"{LockedDoorNote} toward ({target.X},{target.Y}); stopped at ({P.TilePoint.X},{P.TilePoint.Y}). {(said.Length > 0 ? $"The door said: \"{said}\"" : "")}".Trim());
                yield break;
            }
            if (P.controller == null) break;
            if (Vector2.DistanceSquared(P.Position, last) < 0.01f) stuck++; else stuck = 0;
            last = P.Position;
            if (stuck > 90)
            {
                // Something moved into the way (villager, animal), or the farmer is wedged against a wall: re-plan from here.
                // Days 10 and 28: AI1 stood under Maru's locked door in Robin's house and every walk out stopped at (7,11)
                // without moving, 21 times. Stand square on the tile, put away the door's message and route around villagers.
                if (++replans > 3) break;
                P.controller = null;
                P.Halt();
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: false } msg) { msg.closeDialogue(); Game1.dialogueUp = false; }
                var here = P.TilePoint;
                if (Pathfinder.IsWalkable(loc, here.X, here.Y)) P.setTileLocation(here.ToVector2());
                var avoid = new HashSet<Point>(loc.characters.Select(n => n.TilePoint).Where(t => t != target && t != here));
                var re = new Pathfinder().FindPath(loc, here.ToVector2(), target.ToVector2(), avoid)
                         ?? new Pathfinder().FindPath(loc, here.ToVector2(), target.ToVector2());
                if (re == null) break;
                var s2 = new Stack<Point>();
                for (int i = re.Count - 1; i >= 0; i--) s2.Push(new Point((int)re[i].X, (int)re[i].Y));
                P.controller = new PathFindController(s2, P, loc) { NPCSchedule = false };
                stuck = -200;
            }
            yield return null;
        }
        P.controller = null;
        P.Halt();
        if (P.TilePoint != target && Game1.currentLocation == loc)
            c.Fail(Reasons.NoPath, $"Could not reach ({target.X},{target.Y}); stopped at ({P.TilePoint.X},{P.TilePoint.Y}).");
    }

    /// <summary>How a walk that ran into a locked door fails; talk_to stops chasing on it.</summary>
    internal const string LockedDoorNote = "A locked door is in the way";

    /// <summary>True while <see cref="ClearWayTo"/> clears: its own walks must not start another clearing.</summary>
    private static bool _clearingWay;

    private const int ClearWayMaxTiles = 15;

    /// <summary>Walkable, or covered by something the basic tools clear (weeds, grass, twigs, stones, stumps, dead crops). Young
    /// trees are not cut on the way: someone may have planted them.</summary>
    private sealed class DebrisGrid : IWalkableGrid
    {
        private readonly GameLocation _loc;
        public DebrisGrid(GameLocation loc) => _loc = loc;
        public bool IsWalkable(int x, int y) => Pathfinder.IsWalkable(_loc, x, y) ||
            (_loc.isTileOnMap(x, y) && FarmCoverAt(_loc, x, y).cover is var cover && cover != FarmCover.Sapling && FarmClearable(cover));
    }

    /// <summary>
    /// No route to <paramref name="target"/>: find one that crosses clearable debris and clear those tiles in route order.
    /// Fails with an empty result when even that route doesn't exist (the caller then reports a plain no path).
    /// </summary>
    private static IEnumerator ClearWayTo(GameLocation loc, Point target, CallContext c)
    {
        var rough = new Pathfinder().FindPath(new DebrisGrid(loc), P.TilePoint.ToVector2(), target.ToVector2());
        if (rough == null) { c.Fail(Reasons.NoPath, ""); yield break; }
        var blockers = rough.Select(v => new Point((int)v.X, (int)v.Y)).Where(t => !Pathfinder.IsWalkable(loc, t.X, t.Y)).ToList();
        if (blockers.Count == 0) yield break;
        if (blockers.Count > ClearWayMaxTiles)
        {
            c.Fail(Reasons.NoPath, $"the only way there crosses {blockers.Count} tiles of weeds, stones and twigs, starting at {FarmTilesText(blockers, 4)}; clear_area them first.");
            yield break;
        }

        _clearingWay = true;
        var rep = new FarmReport();
        float energyBefore = P.Stamina;
        int startTime = Game1.timeOfDay;
        try
        {
            foreach (var t in blockers)
            {
                yield return FarmClear(loc, new List<Point> { t }, rep);
                if (rep.Stop != null || Loc != loc) break;
                if (!Pathfinder.IsWalkable(loc, t.X, t.Y)) break;
            }
        }
        finally { _clearingWay = false; }

        if (blockers.Any(t => !Pathfinder.IsWalkable(loc, t.X, t.Y)))
        {
            var left = blockers.Where(t => !Pathfinder.IsWalkable(loc, t.X, t.Y)).ToList();
            c.Fail(Reasons.NoPath, $"tried to clear the way but {left.Count} tile(s) still block it ({FarmTilesText(left, 4)})" +
                                   (rep.Stop != null ? $": {rep.Stop}" : rep.MissingTools.Count > 0 ? $": missing {string.Join(", ", rep.MissingTools)}" : "") + ".");
            yield break;
        }
        // The farmer pays for the clearing in energy and time, so its result says so (it used to reach only the system log).
        var cost = $"Cleared a way through {blockers.Count} tile(s) on {loc.Name} ({FarmCounts(rep.Cleared)}): energy {(int)energyBefore} → {(int)P.Stamina}, " +
                   $"{WorldText.FormatTime(startTime)} → {WorldText.FormatTime(Game1.timeOfDay)}.";
        Rt.Log.Add("system", text: cost);
        SocialTools.Notify(Rt.Log.Player, cost);
    }

    /// <summary>Only the farm and the Backwoods next to it are walled in by debris worth cutting through on the way.</summary>
    private static bool ClearsWayOn(GameLocation loc) => loc is Farm || loc.Name == "Backwoods";

    /// <summary>Another farmer stands on this tile: farmers don't collide in the game, so walking there stacks them.</summary>
    private static bool FarmerOn(GameLocation loc, Point tile) =>
        Game1.getOnlineFarmers().Any(f => f != P && f.currentLocation == loc && f.TilePoint == tile);

    private static readonly Point[] Dirs = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) }; // up right down left

    /// <summary>Walk to the best tile next to target and face it.</summary>
    public static IEnumerator WalkAdjacent(Point target, CallContext c) => WalkAdjacent(target, c, null);

    /// <param name="stopWhen">Passed to <see cref="WalkTo"/>: ends the walk early, without facing the target.</param>
    public static IEnumerator WalkAdjacent(Point target, CallContext c, Func<bool>? stopWhen)
    {
        var loc = Loc;
        var me = P.TilePoint;
        int dist = Math.Abs(me.X - target.X) + Math.Abs(me.Y - target.Y);
        if (dist != 1)
        {
            Point? best = null;
            int bestLen = int.MaxValue;
            foreach (var d in Dirs)
            {
                var n = new Point(target.X + d.X, target.Y + d.Y);
                if (!Pathfinder.IsWalkable(loc, n.X, n.Y) || FarmerOn(loc, n)) continue;
                if (n == me) { best = n; bestLen = 0; break; }
                var path = new Pathfinder().FindPath(loc, me.ToVector2(), n.ToVector2());
                if (path != null && path.Count < bestLen) { best = n; bestLen = path.Count; }
            }
            if (best == null)
            {
                c.Fail(Reasons.NoPath, $"No reachable tile next to ({target.X},{target.Y}).");
                yield break;
            }
            yield return WalkTo(best.Value, c, stopWhen);
            if (!c.Ok || stopWhen?.Invoke() == true) yield break;
        }
        Face(target);
    }

    private static void Face(Point target)
    {
        var me = P.TilePoint;
        int dx = target.X - me.X, dy = target.Y - me.Y;
        int dir = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 1 : 3) : (dy > 0 ? 2 : 0);
        P.faceDirection(dir);
        P.FarmerSprite.StopAnimation();
    }

    // ------------------------------------------------------------------ items

    internal static int FindItem(string name, Func<Item, bool>? filter = null) => FindIn(P.Items, name, filter);

    /// <summary>Index of the item called <paramref name="name"/> (exact name first, else the first partial match), or -1.</summary>
    private static int FindIn(IList<Item> items, string name, Func<Item, bool>? filter = null)
    {
        name = name.Trim().ToLowerInvariant();
        int partial = -1;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null || (filter != null && !filter(it))) continue;
            var dn = it.DisplayName.ToLowerInvariant();
            var n = it.Name.ToLowerInvariant();
            if (dn == name || n == name) return i;
            if (partial < 0 && (dn.Contains(name) || n.Contains(name))) partial = i;
        }
        return partial;
    }

    private static Dictionary<string, int> Snapshot() =>
        P.Items.Where(i => i != null).GroupBy(i => i.DisplayName).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));

    private static string Diff(Dictionary<string, int> before)
    {
        var after = Snapshot();
        var parts = new List<string>();
        foreach (var kv in after)
        {
            before.TryGetValue(kv.Key, out var b);
            if (kv.Value != b) parts.Add($"{kv.Key} {(kv.Value - b > 0 ? "+" : "")}{kv.Value - b}");
        }
        foreach (var kv in before)
            if (!after.ContainsKey(kv.Key)) parts.Add($"{kv.Key} -{kv.Value}");
        return parts.Count == 0 ? "" : "Inventory change: " + string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ tool use

    private enum Target { None, Object, Clump, Tree, Grass, Dirt, Other }

    private static Target TargetAt(GameLocation loc, Point t)
    {
        var v = t.ToVector2();
        if (loc.Objects.ContainsKey(v)) return Target.Object;
        if (loc.resourceClumps.Any(rc => rc.occupiesTile(t.X, t.Y))) return Target.Clump;
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            if (tf is Tree || tf is FruitTree) return Target.Tree;
            if (tf is Grass) return Target.Grass;
            if (tf is HoeDirt) return Target.Dirt;
            return Target.Other;
        }
        return Target.None;
    }

    // A dead crop is a scythe target too: the game's HoeDirt.performToolAction destroys it (crops die when the season changes).
    private static bool ScytheTargetAt(GameLocation loc, Point tile) =>
        loc.Objects.TryGetValue(tile.ToVector2(), out var obj) && obj.IsWeeds() ||
        loc.terrainFeatures.TryGetValue(tile.ToVector2(), out var feature) && feature is Grass grass && grass.numberOfWeeds.Value > 0 ||
        FarmRipeCrop(loc, tile.X, tile.Y) || FarmDeadCrop(loc, tile.X, tile.Y);

    private static (object? target, int amount) ScytheTargetState(GameLocation loc, Point tile)
    {
        if (loc.Objects.TryGetValue(tile.ToVector2(), out var obj) && obj.IsWeeds()) return (obj, obj.Stack);
        if (loc.terrainFeatures.TryGetValue(tile.ToVector2(), out var feature) && feature is Grass grass && grass.numberOfWeeds.Value > 0) return (grass, grass.numberOfWeeds.Value);
        if (feature is HoeDirt dirt && (FarmRipeCrop(loc, tile.X, tile.Y) || FarmDeadCrop(loc, tile.X, tile.Y))) return (dirt.crop, 1);
        return (null, 0);
    }

    private static IEnumerator Swing(Point target, Func<bool>? stillValid = null)
    {
        yield return WaitIdle();
        // Another farmer or the preceding scythe arc may have removed it while we waited.
        if (stillValid != null && !stillValid()) yield break;
        Face(target);
        P.lastClick = new Vector2(target.X * 64 + 32, target.Y * 64 + 32);
        P.BeginUsingTool();
        yield return Ticks(6);
        yield return WaitIdle();
    }

    private static IEnumerator UseTool(JsonElement a, CallContext c)
    {
        var toolName = S(a, "tool");
        var tiles = TileList(a, "tiles").Take(40).ToList();
        int maxHits = Math.Clamp(I(a, "max_hits", 20), 1, 60);
        if (tiles.Count == 0) { c.Fail(Reasons.InvalidArgs, "No tiles given."); yield break; }

        int slot = FindItem(toolName, it => it is Tool);
        var fetched = new List<string>();
        if (slot < 0 && MainToolNamed(toolName) is { } mainKind)
        {
            yield return FetchMainTools(Loc, fetched, mainKind);
            slot = P.Items.ToList().FindIndex(i => MainToolKind(i) == mainKind);
        }
        if (slot < 0) { c.Fail(Reasons.NoItem, $"You don't have a tool called \"{toolName}\". {string.Join(" ", fetched)}".TrimEnd()); yield break; }
        P.CurrentToolIndex = slot;
        var tool = (Tool)P.Items[slot];
        bool repeatTool = tool is Axe || tool is Pickaxe;
        bool scythe = tool is MeleeWeapon blade && blade.isScythe();
        // English kind of tool for RightToolHint (DisplayName is localized, e.g. Thai).
        string used = scythe ? "Scythe" : tool is Axe ? "Axe" : tool is Pickaxe ? "Pickaxe" : tool is Hoe ? "Hoe" : "Watering Can";
        var before = Snapshot();
        var lines = new List<string>();
        var loc = Loc;
        var batch = new BatchTally();

        foreach (var t in FarmRoute(tiles, tool))
        {
            if (scythe && !ScytheTargetAt(loc, t))
            {
                var hint = RightToolHint(loc, t, used);
                lines.Add(hint.Length > 0
                    ? $"({t.X},{t.Y}) skipped: nothing for the Scythe here{hint}."
                    : $"({t.X},{t.Y}) skipped: no grass, weeds, ripe or dead crop here. Refresh look/farm_summary; do not reuse guessed coordinates.");
                batch.Skip(Reasons.NotHere);
                continue;
            }
            if (P.Stamina <= 2)
            {
                lines.Add($"Stopped: energy too low ({(int)P.Stamina}): {DaySafety.LowEnergyAdvice(Game1.timeOfDay, Game1.IsMultiplayer)}.");
                batch.Stop = Reasons.LowEnergy;
                break;
            }
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu/dialogue opened."); batch.Stop = Reasons.MenuOpen; break; }

            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); batch.Skip(sub.Reason); continue; }
            P.CurrentToolIndex = slot;

            var (sym, label) = WorldText.Describe(loc, t.X, t.Y);
            var kind = TargetAt(loc, t);

            if (tool is WateringCan can)
            {
                bool water = loc.isWaterTile(t.X, t.Y) || loc.doesTileHaveProperty(t.X, t.Y, "Water", "Back") != null;
                if (!water && can.WaterLeft <= 0)
                {
                    // Refill the way water_all does (days 46-56: 27 "can is EMPTY" skips, and the AIs then guessed water tiles they couldn't reach).
                    var refill = new CallContext();
                    yield return FarmRefill(slot, refill);
                    if (!refill.Ok) { lines.Add($"({t.X},{t.Y}) skipped: watering can is EMPTY and {refill.Result.Replace("the can is empty and ", "")}."); batch.Skip(); continue; }
                    lines.Add($"refilled the watering can at the nearest water ({can.WaterLeft}/{can.waterCanMax})");
                    var back = new CallContext();
                    yield return WalkAdjacent(t, back);
                    if (!back.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {back.Result}"); batch.Skip(back.Reason); continue; }
                    P.CurrentToolIndex = slot;
                }
                yield return Swing(t);
                bool wet = loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf2) && tf2 is HoeDirt hd && hd.isWatered();
                lines.Add(water ? $"({t.X},{t.Y}) refilled watering can ({can.WaterLeft}/{can.waterCanMax})" : $"({t.X},{t.Y}) watered{(wet ? "" : " (nothing to water here)")} · can {can.WaterLeft}/{can.waterCanMax}");
                if (water || wet) batch.Done++; else batch.Skip(Reasons.NotHere);
                continue;
            }

            if (tool is Hoe)
            {
                if (kind == Target.Dirt) { lines.Add($"({t.X},{t.Y}) already tilled"); batch.Done++; continue; }
                yield return Swing(t);
                var ok = loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf3) && tf3 is HoeDirt;
                var tillFix = RightToolHint(loc, t, used);
                var tillWhy = tillFix.Length > 0 ? tillFix + " first" : " — clear it first or pick a ',' tile";
                lines.Add(ok ? $"({t.X},{t.Y}) tilled" : $"({t.X},{t.Y}) could not till ({label ?? sym.ToString()}){tillWhy}");
                if (ok) batch.Done++; else batch.Skip();
                continue;
            }

            if (!repeatTool)
            {
                if (scythe)
                {
                    var cutBefore = ScytheTargetState(loc, t);
                    if (cutBefore.target == null) { batch.Skip(Reasons.NotHere); lines.Add($"({t.X},{t.Y}) target already gone; no swing."); continue; }
                    yield return Swing(t, () => Loc == loc && ScytheTargetAt(loc, t));
                    var cutAfter = ScytheTargetState(loc, t);
                    if (cutAfter == cutBefore)
                    {
                        batch.Skip(Reasons.NotReady);
                        lines.Add($"({t.X},{t.Y}) Scythe had no visible effect; not counted as success. Inspect look before retrying.");
                    }
                    else { batch.Done++; lines.Add($"({t.X},{t.Y}) cut {label ?? "grass/weeds/crop"}; target changed."); }
                    continue;
                }
                yield return Swing(t);
                lines.Add($"({t.X},{t.Y}) used {tool.DisplayName} on {label ?? "tile"}");
                batch.Done++;
                continue;
            }

            if (kind == Target.None || kind == Target.Dirt || kind == Target.Grass)
            {
                lines.Add($"({t.X},{t.Y}) nothing for {tool.DisplayName} here ({label ?? sym.ToString()}){RightToolHint(loc, t, used)}");
                batch.Skip(Reasons.NotHere);
                continue;
            }

            int hits = 0;
            while (hits < maxHits && TargetAt(loc, t) != Target.None && P.Stamina > 2 && Game1.activeClickableMenu == null)
            {
                yield return Swing(t);
                hits++;
            }
            var after = TargetAt(loc, t);
            if (after == Target.None) batch.Done++;
            else batch.Skip(P.Stamina <= 2 ? Reasons.LowEnergy : Game1.activeClickableMenu != null ? Reasons.MenuOpen : kind == Target.Clump ? Reasons.NeedsUpgrade : null);
            lines.Add(after == Target.None
                ? $"({t.X},{t.Y}) {label} cleared in {hits} hits"
                : $"({t.X},{t.Y}) {label} NOT cleared after {hits} hits (still: {WorldText.Describe(loc, t.X, t.Y).label}){(kind == Target.Clump ? " — may need an upgraded tool" : RightToolHint(loc, t, used))}");
        }

        yield return Ticks(45); // let dropped items fly into the inventory
        var diff = Diff(before);
        c.Result = string.Join("\n", lines) + (diff.Length > 0 ? "\n" + diff : "");
        batch.Apply(c, tiles.Count);
        if (batch.Done == 0)
            c.Fail(c.Reason ?? Reasons.NotHere, c.Result + "\nNo targets were completed. Refresh look/farm_summary and select existing targets before another use_tool call.");
    }

    private static IEnumerator Plant(JsonElement a, CallContext c)
    {
        var name = S(a, "item");
        var tiles = RouteOrder(TileList(a, "tiles").Take(40), P.TilePoint);
        int slot = FindItem(name, it => it is SObject);
        if (slot < 0) { c.Fail(Reasons.NoItem, $"No \"{name}\" in inventory."); yield break; }
        var loc = Loc;
        var lines = new List<string>();
        var batch = new BatchTally();

        foreach (var t in tiles)
        {
            if (P.Items[slot] == null || P.Items[slot].Stack <= 0) { lines.Add("Ran out of seeds."); batch.Stop = Reasons.NoItem; break; }
            if (!loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf) || tf is not HoeDirt dirt)
            {
                lines.Add($"({t.X},{t.Y}) not tilled soil — use the Hoe first");
                batch.Skip(Reasons.NotHere);
                continue;
            }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); batch.Skip(sub.Reason); continue; }

            P.CurrentToolIndex = slot;
            var item = (SObject)P.Items[slot];
            var hadCrop = dirt.crop != null;
            var hadFert = dirt.fertilizer.Value;
            Utility.tryToPlaceItem(loc, item, t.X * 64 + 32, t.Y * 64 + 32);
            yield return Ticks(8);
            bool sown = !hadCrop && dirt.crop != null;
            bool ok = sown || dirt.fertilizer.Value != hadFert;
            if (sown) Rt.Stats.CropsPlanted(1);
            if (ok) { batch.Done++; Ownership.ClaimCrop(dirt, Rt.Log.Player); }
            else
            {
                lines.Add($"({t.X},{t.Y}) could not plant {item.DisplayName} (occupied, wrong season, or not plantable here)");
                batch.Skip(); // the game gives no way to tell occupied from wrong season, so no reason code
            }
        }
        c.Result = $"Planted {batch.Done}/{tiles.Count} tiles." + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
        batch.Apply(c, tiles.Count);
    }

    private static IEnumerator Harvest(JsonElement a, CallContext c)
    {
        var tiles = RouteOrder(TileList(a, "tiles").Take(40), P.TilePoint);
        var before = Snapshot();
        var loc = Loc;
        var lines = new List<string>();
        var batch = new BatchTally();
        foreach (var t in tiles)
        {
            if (Game1.activeClickableMenu != null) { lines.Add("Stopped: a menu opened (inventory full?)."); batch.Stop = Reasons.MenuOpen; break; }
            var sub = new CallContext();
            yield return WalkAdjacent(t, sub);
            if (!sub.Ok) { lines.Add($"({t.X},{t.Y}) skipped: {sub.Result}"); batch.Skip(sub.Reason); continue; }
            var (symBefore, label) = WorldText.Describe(loc, t.X, t.Y);
            loc.checkAction(new xTile.Dimensions.Location(t.X, t.Y), Game1.viewport, P);
            yield return Ticks(20);
            yield return WaitIdle(120);
            var now = WorldText.Describe(loc, t.X, t.Y);
            Rt.Stats.HarvestTile(symBefore, now.sym);
            lines.Add($"({t.X},{t.Y}) {label ?? "empty"} → now: {now.label ?? "empty"}");
            if (label != now.label) batch.Done++; else batch.Skip(); // the tile looks the same: nothing was picked up
        }
        yield return Ticks(30);
        var diff = Diff(before);
        // Wheat, Amaranth and the like don't come off by hand (days 32-34: 20 hand harvests of ripe Wheat did nothing).
        if (tiles.Any(t => loc.terrainFeatures.TryGetValue(t.ToVector2(), out var tf) && tf is HoeDirt { crop: { } cr } && cr.GetHarvestMethod() == StardewValley.GameData.Crops.HarvestMethod.Scythe))
            lines.Add("Some of these crops are cut with the Scythe, not picked by hand: harvest_all cuts them (and takes the Scythe from a chest if it is there).");
        c.Result = string.Join("\n", lines) + "\n" + (diff.Length > 0 ? diff : "Nothing was collected.");
        batch.Apply(c, tiles.Count);
    }

    /// <summary>True while interact presses the action button; see <see cref="JustRightClick_Prefix"/>.</summary>
    private static bool _pressingAction;

    public static void Apply(IManifest manifest) =>
        new Harmony(manifest.UniqueID + ".input").Patch(AccessTools.Method(typeof(Game1), nameof(Game1.didPlayerJustRightClick)),
            prefix: new HarmonyMethod(typeof(AgentTools), nameof(JustRightClick_Prefix)));

    /// <summary>Player chests, the shipping bin, fish ponds, bushes and furniture only answer a fresh action-button press.</summary>
    private static bool JustRightClick_Prefix(ref bool __result)
    {
        if (!_pressingAction) return true;
        __result = true;
        return false;
    }

    /// <summary>Game1.tryToCheckAt with the action button seen as just pressed.</summary>
    private static bool PressAction(Point tile)
    {
        _pressingAction = true;
        try { return Game1.tryToCheckAt(tile.ToVector2(), P); }
        finally { _pressingAction = false; }
    }

    /// <summary>The action button, as in Game1.pressActionButton: check the tile with the held item, or use the held item in place.</summary>
    /// <summary>The closest things interact can press (doors, counters, chests, objects, villagers), so a call that left out x and y is fixed in one go.</summary>
    private static string NearbyActionTiles()
    {
        var loc = Loc;
        var me = P.TilePoint;
        var found = new List<(int d, string text)>();
        for (int dy = -8; dy <= 8; dy++)
            for (int dx = -8; dx <= 8; dx++)
            {
                int x = me.X + dx, y = me.Y + dy;
                if (!loc.isTileOnMap(x, y)) continue;
                var (sym, label) = WorldText.Describe(loc, x, y);
                if (label != null && sym is 'A' or 'D' or 'O' or 'N' or 'b' or 'Z' or 'E')
                    found.Add((Math.Abs(dx) + Math.Abs(dy), $"({x},{y}) {label}"));
            }
        return found.Count == 0 ? "" : "\nNearest things to interact with: " + string.Join("; ", found.OrderBy(f => f.d).Take(6).Select(f => f.text)) + ".";
    }

    private static IEnumerator Interact(JsonElement a, CallContext c)
    {
        var holding = S(a, "holding").Trim();
        int slot = holding.Length > 0 ? FindItem(holding) : -1;
        if (holding.Length > 0 && slot < 0) { c.Fail(Reasons.NoItem, $"No \"{holding}\" in inventory."); yield break; }
        bool atTile = Has(a, "x") && Has(a, "y");
        // interact() with nothing in a shop building means "open the shop" (83 failures in the second 9B run, most in Pierre's).
        if (!atTile && slot < 0 && Game1.activeClickableMenu == null && ShopOfPlace.ContainsKey(Loc.Name))
        {
            var shop = new CallContext();
            yield return OpenShopHere(shop);
            if (Game1.activeClickableMenu is ShopMenu)
            {
                c.Result = "Opened the shop. " + WorldText.MenuState().Trim() + "\nBuy with buy(item, quantity) or sell with sell(item); close_menu when done.";
                yield break;
            }
            c.Fail(shop.Reason ?? Reasons.ShopClosed, shop.Result);
            yield break;
        }
        if (!atTile && slot < 0) { c.Fail(Reasons.InvalidArgs, "Nothing was pressed: supply BOTH x and y from look, or holding to use an item where you stand." + ShopCounterHint() + NearbyActionTiles()); yield break; }

        var before = Snapshot();
        var startLoc = Loc;
        var t = new Point(I(a, "x", -1), I(a, "y", -1));
        if (atTile)
        {
            yield return WalkAdjacent(t, c);
            if (!c.Ok) yield break;
        }
        else
            yield return WaitIdle();

        // Without holding, put away a held object so nothing gets loaded or gifted by accident.
        if (slot >= 0) P.CurrentToolIndex = slot;
        else if (P.ActiveObject != null) P.CurrentToolIndex = FirstToolSlot();
        var held = P.CurrentItem?.DisplayName;
        var was = atTile ? WorldText.Describe(Loc, t.X, t.Y).label : null;

        string what;
        if (atTile)
            what = PressAction(t) ? $"Interacted with ({t.X},{t.Y})" : $"Nothing happened at ({t.X},{t.Y})";
        else if (P.ActiveObject is { } obj and not StardewValley.Objects.Furniture && obj.performUseAction(Loc))
        {
            // Secret notes and journal scraps open a letter but stay in the pack (#55); other usable items are consumed.
            if (obj.QualifiedItemId is not ("(O)79" or "(O)842")) P.reduceActiveItemByOne();
            what = $"Used {held}";
        }
        else
        {
            c.Fail(Reasons.NotHere, $"{held} can't be used where you stand.");
            yield break;
        }
        yield return Ticks(30);
        yield return WaitIdle(atTile ? 180 : 600);
        if (!atTile)
        {
            // After its animation a totem hides the farmer and warps after a delayed fade.
            for (int i = 0; i < 600 && (!Game1.displayFarmer || Game1.globalFade || P.freezePause > 0); i++) yield return null;
            yield return WaitIdle();
        }

        if (slot >= 0 && atTile) what += $" holding {held}";
        var now = atTile && Loc == startLoc ? WorldText.Describe(Loc, t.X, t.Y).label : was;
        var changed = now != was ? $" Now: {now ?? "empty"}." : "";
        var moved = Loc != startLoc ? $" You are now in {Loc.Name}." : "";
        var finished = new CallContext();
        yield return FinishMessages(finished);
        c.Result = $"{what}.{changed}{moved} {Diff(before)}".Trim();
        if (finished.Result.Length > 0) c.Result += "\n" + finished.Result;
    }

    // talk_to lives in NpcTools.cs (TalkToNpc).

    private static int FirstToolSlot()
    {
        for (int i = 0; i < P.Items.Count; i++) if (P.Items[i] is Tool) return i;
        return 0;
    }

    private static IEnumerator Respond(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu is not DialogueBox box)
        {
            // A heart event is playing between two lines: wait for the next line or question (NpcStory.cs).
            if (Rt.Story.Active) { yield return Rt.Story.WaitForNext(c); yield break; }
            // Say what is open instead (42 bare "No dialogue is open" in the local Qwen run, most after another farmer's say).
            var open = WorldText.MenuState().Trim();
            c.Fail(Reasons.NotHere, "No dialogue is open. " + (open.Length > 0 ? open : "Nothing is open: carry on with your work. Another farmer's say is answered with say, not respond."));
            yield break;
        }
        var startText = box.getCurrentString();
        bool question = box.isQuestion;
        int option = I(a, "option", -1);
        if (question && (box.responses == null || option < 0 || option >= box.responses.Length))
        {
            c.Fail($"This is a question; pass option 0-{(box.responses?.Length ?? 1) - 1}.");
            yield break;
        }
        for (int i = 0; i < 40; i++)
        {
            if (Game1.activeClickableMenu != box) break;
            if (!box.transitioning)
            {
                if (question) box.selectedResponse = option;
                box.receiveLeftClick(0, 0);
            }
            yield return Ticks(10);
            if (Game1.activeClickableMenu != box || box.getCurrentString() != startText || (question && box.transitioning)) break;
        }
        yield return Ticks(30);
        var finished = new CallContext();
        yield return FinishMessages(finished);
        c.Result = Game1.activeClickableMenu == null ? "Dialogue closed." : "Dialogue continued.";
        if (finished.Result.Length > 0) c.Result += "\n" + finished.Result;
    }

    private static IEnumerator CloseMenu(JsonElement a, CallContext c)
    {
        if (Game1.activeClickableMenu == null) { c.Result = "No menu open."; yield break; }
        var notes = new List<string>();
        yield return ClaimBeforeClose(Game1.activeClickableMenu, notes);
        if (Game1.activeClickableMenu != null)
        {
            if (Game1.activeClickableMenu is DialogueBox box)
                box.closeDialogue();
            else
                Game1.activeClickableMenu.exitThisMenu();
            yield return Ticks(20);
            if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        }
        c.Result = string.Join(" ", notes.Prepend("Menu closed."));
    }

    // ------------------------------------------------------------------ travel

    private static IEnumerator TravelTo(JsonElement a, CallContext c)
    {
        var asked = S(a, "location").Trim();
        // 19 calls in the second 9B run sent only a reason, or the place under another key.
        if (asked.Length == 0 && a.ValueKind == JsonValueKind.Object)
            asked = a.EnumerateObject().Where(p => p.Name != "reason" && p.Value.ValueKind == JsonValueKind.String)
                .Select(p => (p.Value.GetString() ?? "").Trim()).FirstOrDefault(s => s.Length > 0) ?? "";
        if (asked.Length == 0)
        {
            c.Fail(Reasons.InvalidArgs, "No destination given: put the place in location, e.g. travel_to(location=\"Town\", reason=\"...\"). " +
                                        "location takes a map name (Farm, Town, SeedShop...), a shop keeper's or villager's name, or another farmer's name.");
            yield break;
        }
        if (IsSkullCavernName(asked)) { yield return TravelToSkullCavern(c); yield break; }
        var (dest, note) = NpcInfo.ResolvePlace(asked); // map names, shop keepers (Pierre = SeedShop), villagers (where they are now)
        var target = dest == null ? null : Game1.getLocationFromName(dest);
        // Another farmer's name: the map they are on now (33 failures in the local Qwen run went to "AI2").
        if (target == null && SocialTools.OtherAis().FirstOrDefault(s => string.Equals(SocialTools.Label(s), asked, StringComparison.OrdinalIgnoreCase)) is { } mate
            && Romance.FarmerOf(mate)?.currentLocation is { } theirs)
        {
            target = theirs;
            var t = Romance.FarmerOf(mate)!.TilePoint;
            note = $"{SocialTools.Label(mate)} was at ({t.X},{t.Y}) when you set off; look to find them.";
        }
        if (target == null)
        {
            c.Fail(Reasons.NotFound, $"Unknown location \"{asked}\". Use a map name (Farm, Town, SeedShop...), a shop keeper's name (Pierre, Clint, Robin, Marnie, Willy, Gus), any villager's name or another farmer's name.");
            yield break;
        }
        if (target.Name == "Club" && !P.hasClubCard)
        {
            c.Fail(Reasons.ShopClosed, "The casino is behind the Oasis back door and the bouncer only lets in holders of the Club Card.");
            yield break;
        }
        if (target.Name == "MovieTheater" && !Game1.MasterPlayer.hasOrWillReceiveMail("ccMovieTheater"))
        {
            c.Fail(Reasons.ShopClosed, "The movie theater is not restored yet. Complete the Movie Theater bundle in the Community Center, or buy it from Joja.");
            yield break;
        }
        yield return TravelTo(target.Name, c);
        if (c.Ok) c.Result = $"Arrived in {Loc.Name} at ({P.TilePoint.X},{P.TilePoint.Y}).{(note.Length > 0 ? " " + note : "")}" + ShopCounterHint();
    }

    /// <summary>A way out of a map: a door/building door (press action on it) or an edge warp (walk off the map).</summary>
    private record Exit(string Target, Point Tile, bool IsDoor, Warp? Warp);

    private static List<Exit> Exits(GameLocation loc)
    {
        var list = new List<Exit>();
        foreach (var kv in loc.doors.Pairs)
            list.Add(new Exit(kv.Value, kv.Key, true, null));
        foreach (var b in loc.buildings)
        {
            var inside = b.GetIndoorsName();
            if (!string.IsNullOrEmpty(inside) && b.humanDoor.Value.X >= 0)
                list.Add(new Exit(inside, b.getPointForHumanDoor(), true, null));
        }
        foreach (var w in loc.warps)
            list.Add(new Exit(w.TargetName, new Point(w.X, w.Y), false, w));
        return list;
    }

    /// <summary>Breadth-first search over maps connected by exits.</summary>
    private static List<string>? FindRoute(string from, string to)
    {
        var prev = new Dictionary<string, string> { [from] = "" };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                var route = new List<string>();
                for (var n = cur; n != ""; n = prev[n]) route.Insert(0, n);
                return route;
            }
            var loc = Game1.getLocationFromName(cur);
            if (loc == null) continue;
            foreach (var e in Exits(loc))
            {
                if (prev.ContainsKey(e.Target) || Game1.getLocationFromName(e.Target) == null) continue;
                prev[e.Target] = cur;
                queue.Enqueue(e.Target);
            }
            // Paid rides (bus, boat) connect maps that have no door or warp between them.
            foreach (var t in TransitTargets(cur))
            {
                if (prev.ContainsKey(t) || Game1.getLocationFromName(t) == null) continue;
                prev[t] = cur;
                queue.Enqueue(t);
            }
        }
        return null;
    }

    private static IEnumerator TravelTo(string dest, CallContext c)
    {
        // The festival map is a temporary copy with no route out: leave through the festival itself (69 travel_to failures on day 13).
        if (Game1.eventUp && Game1.CurrentEvent is { isFestival: true })
        {
            yield return FestivalLeave(dest, c);
            yield break;
        }
        yield return PrepareForTravel(c);
        if (!c.Ok) yield break;
        yield return WaitIdle();
        var route = FindRoute(Loc.Name, dest);
        if (route == null) { c.Fail(Reasons.NoPath, $"No known route from {Loc.Name} to {dest}."); yield break; }

        for (int hop = 1; hop < route.Count; hop++)
        {
            yield return WaitIdle();
            var loc = Loc;
            var next = route[hop];
            if (IsTransit(loc.Name, next))
            {
                var transit = new CallContext();
                yield return TakeTransit(next, transit);
                if (!transit.Ok) { c.Fail(transit.Reason ?? Reasons.NoPath, transit.Result); yield break; }
                if (Loc.Name != next) { c.Fail(Reasons.NoPath, $"The ride ended in {Loc.Name} instead of {next}."); yield break; }
                yield return Ticks(20);
                continue;
            }
            var me = P.TilePoint;
            // Reachable exits first, then the nearest: the closest way out can sit behind a locked bedroom door
            // (day 21: Robin's house, AI2 walked into the bedroom door until bedtime).
            var exits = Exits(loc).Where(e => e.Target == next)
                .OrderBy(e => ExitReachable(loc, me, e) ? 0 : 1)
                .ThenBy(e => Math.Abs(e.Tile.X - me.X) + Math.Abs(e.Tile.Y - me.Y)).ToList();
            if (exits.Count == 0) { c.Fail(Reasons.NoPath, $"No exit from {loc.Name} to {next}."); yield break; }

            Exit exit = exits[0];
            string? lastFail = null;
            string? lastReason = null;
            foreach (var cand in exits)
            {
                exit = cand;
                var sub = new CallContext();
                lastFail = null;
                if (exit.IsDoor)
                {
                    // Stand below the door and open it; the game checks opening hours/locks.
                    var d = exit.Tile;
                    var stand = new Point(d.X, d.Y + 1);
                    if (Pathfinder.IsWalkable(loc, stand.X, stand.Y)) yield return WalkTo(stand, sub);
                    else yield return WalkAdjacent(d, sub);
                    if (Loc == loc)
                    {
                        if (!sub.Ok) { lastFail = $"Can't reach the door to {next}: {sub.Result}"; lastReason = sub.Reason; continue; }
                        Face(d);
                        Game1.tryToCheckAt(d.ToVector2(), P);
                    }
                }
                else
                {
                    // Edge exits sit just off the walkable map: get as close as possible, then step through.
                    var near = Pathfinder.IsWalkable(loc, exit.Tile.X, exit.Tile.Y) ? exit.Tile : NearestWalkable(loc, exit.Tile, 3);
                    if (near == null) { lastFail = $"Can't find a walkable tile near the exit to {next}."; lastReason = Reasons.NoPath; continue; }
                    yield return WalkTo(near.Value, sub);
                    if (Loc == loc && !Game1.isWarping)
                    {
                        // The last step onto an edge exit is often blocked by the map edge itself, so being right next to it is enough.
                        var here = P.TilePoint;
                        bool atExit = Math.Abs(here.X - exit.Tile.X) + Math.Abs(here.Y - exit.Tile.Y) <= 1;
                        if (!sub.Ok && !atExit) { lastFail = $"Can't reach the exit to {next}: {sub.Result}"; lastReason = sub.Reason; continue; }
                        var w = exit.Warp!;
                        Game1.warpFarmer(w.TargetName, w.TargetX, w.TargetY, false);
                    }
                }
                break;
            }
            if (lastFail != null)
            {
                // A locked door's "not close enough" line stays up otherwise.
                if (Game1.activeClickableMenu is DialogueBox) yield return FinishMessages(new CallContext());
                c.Fail(lastReason ?? Reasons.NoPath, lastFail + (exits.Count > 1 ? $" (tried all {exits.Count} ways out to {next})" : ""));
                yield break;
            }

            for (int i = 0; i < 300 && Loc == loc && Game1.activeClickableMenu is not ReadyCheckDialog; i++) yield return null;
            if (Game1.activeClickableMenu is ReadyCheckDialog { checkName: "festivalStart" })
            {
                // Co-op festivals let the farmers in only together: wait for the others a while, never answer it as a question.
                var together = new CallContext();
                yield return WaitReadyCheck($"travel_to {next}", together);
                if (!together.Ok) { c.Fail(together.Reason ?? Reasons.NotReady, $"Couldn't go from {loc.Name} to {next}: {together.Result}"); yield break; }
                for (int i = 0; i < 300 && Loc == loc; i++) yield return null;
            }
            yield return WaitIdle();
            if (Loc == loc)
            {
                bool npcInterrupted = Game1.activeClickableMenu is DialogueBox { characterDialogue: not null };
                var msg = Game1.activeClickableMenu != null ? WorldText.MenuState().Trim() : "the door may be locked (check opening hours)";
                var finished = new CallContext();
                yield return FinishMessages(finished);
                // Day 56: before a festival opens the game shows "being set up" and sends the farmer back; the closed dialogue's
                // "use respond()" line had both AIs alternate travel_to and respond for an hour.
                if (CalendarData.Today() is { Start: > 0 } fest && fest.Where == next && Game1.timeOfDay < fest.Start)
                {
                    c.Fail(Reasons.ShopClosed, $"{next} is closed until the {fest.Name} opens at {CalendarData.Clock(fest.Start)}: the villagers are setting it up. " +
                        $"Do other work meanwhile and travel_to {next} at {CalendarData.Clock(fest.Start)} or later (it runs until {CalendarData.Clock(fest.End)}). Nothing to respond to.");
                    yield break;
                }
                c.Fail(Game1.activeClickableMenu != null ? Reasons.MenuOpen : npcInterrupted ? Reasons.Interrupted : Reasons.ShopClosed, $"Couldn't go from {loc.Name} to {next}: {NpcInfo.DoorHours(loc, exit.Tile, next)}{msg}\n{finished.Result}");
                yield break;
            }
            if (Loc.Name != next) { c.Fail($"Ended up in {Loc.Name} instead of {next}."); yield break; }
            yield return Ticks(20);
        }
    }

    /// <summary>A walking route exists from <paramref name="from"/> to where this exit is used.</summary>
    private static bool ExitReachable(GameLocation loc, Point from, Exit e)
    {
        Point? goal;
        if (e.IsDoor)
        {
            var below = new Point(e.Tile.X, e.Tile.Y + 1);
            goal = Pathfinder.IsWalkable(loc, below.X, below.Y) ? below
                : Dirs.Select(d => new Point(e.Tile.X + d.X, e.Tile.Y + d.Y)).Where(n => Pathfinder.IsWalkable(loc, n.X, n.Y)).Cast<Point?>().FirstOrDefault();
        }
        else
            goal = Pathfinder.IsWalkable(loc, e.Tile.X, e.Tile.Y) ? e.Tile : NearestWalkable(loc, e.Tile, 3);
        if (goal == null) return false;
        return goal.Value == from || new Pathfinder().FindPath(loc, from.ToVector2(), goal.Value.ToVector2()) != null;
    }

    private static Point? NearestWalkable(GameLocation loc, Point p, int radius)
    {
        Point? best = null;
        int bestD = int.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            var q = new Point(p.X + dx, p.Y + dy);
            if (!Pathfinder.IsWalkable(loc, q.X, q.Y)) continue;
            int d = Math.Abs(dx) + Math.Abs(dy);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    // ------------------------------------------------------------------ shop / ship / eat

    private static IEnumerator ShopList(JsonElement a, CallContext c)
    {
        // Inside a shop building it opens the counter itself, like buy and sell (shop_list failed 26 times on days 21-28 with no menu open).
        var opening = new CallContext();
        if (Game1.activeClickableMenu is not ShopMenu) yield return OpenShopHere(opening);
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail(opening.Reason ?? Reasons.NotHere, $"No shop is open here: {opening.Result} travel_to the shop during opening hours (Pierre's is SeedShop), then shop_list." + ShopCounterHint()); yield break; }
        var cur = CurrencyUnit(shop.currency);
        var sb = new StringBuilder($"Shop {shop.ShopId} — you have {ShopMenu.getPlayerCurrencyAmount(P, shop.currency)}{cur}\n");
        foreach (var s in shop.forSale)
        {
            if (!shop.itemPriceAndStock.TryGetValue(s, out var st)) continue;
            var stock = st.Stock == int.MaxValue ? "" : $" (stock {st.Stock})";
            var trade = st.TradeItem != null ? $" + {st.TradeItemCount ?? 5}x {ItemRegistry.GetData(st.TradeItem)?.DisplayName}" : "";
            var recipe = s.IsRecipe ? " [recipe: buying teaches it]" : "";
            sb.AppendLine($"  {s.DisplayName}: {st.Price}{cur}{trade}{stock}{recipe}");
        }
        c.Result = sb.ToString().TrimEnd();
    }

    private static string CurrencyUnit(int currency) => currency switch
    {
        1 => " star tokens",
        2 => " Qi coins",
        4 => " Qi gems",
        _ => "g"
    };

    /// <summary>Buys through the game's own ShopMenu.tryToPurchaseItem, so recipes are learned, trade items
    /// are consumed, purchase actions run and limited stock is synced, exactly like a click in the menu.</summary>
    private static IEnumerator Buy(JsonElement a, CallContext c)
    {
        var opening = new CallContext();
        if (Game1.activeClickableMenu is not ShopMenu) yield return OpenShopHere(opening);
        if (Game1.activeClickableMenu is not ShopMenu shop) { c.Fail(opening.Reason ?? Reasons.NotHere, $"No shop is open: {opening.Result} travel_to the shop first (Pierre's is SeedShop)."); yield break; }
        if (shop.readOnly) { c.Fail("This shop menu is read-only; nothing can be bought here."); yield break; }
        var name = S(a, "item").Trim().ToLowerInvariant();
        int qty = Math.Clamp(I(a, "quantity", 1), 1, 999);
        var sal = shop.forSale.FirstOrDefault(s => s.DisplayName.ToLowerInvariant() == name)
                  ?? shop.forSale.FirstOrDefault(s => s.DisplayName.ToLowerInvariant().Contains(name));
        if (sal == null || !shop.itemPriceAndStock.TryGetValue(sal, out var st)) { c.Fail(Reasons.NotFound, $"\"{name}\" is not sold here. Use shop_list."); yield break; }
        if (shop.canPurchaseCheck != null && !shop.canPurchaseCheck(shop.forSale.IndexOf(sal))) { c.Fail($"{sal.DisplayName} can't be bought right now."); yield break; }
        if (SeedWouldDie(sal) is { } die) { c.Fail(die); yield break; }

        // Friendly pre-checks; the game's purchase would only play a "cancel" sound.
        if (sal.IsRecipe) qty = 1;
        qty = Math.Min(qty, st.Stock);
        if (qty <= 0) { c.Fail(Reasons.NoItem, $"{sal.DisplayName} is sold out."); yield break; }
        var cur = CurrencyUnit(shop.currency);
        int have = ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
        if (have < st.Price * qty) { c.Fail(Reasons.NoMoney, $"Not enough money: {qty}x {sal.DisplayName} costs {st.Price * qty}{cur}, you have {have}{cur}."); yield break; }
        if (st.TradeItem != null)
        {
            int need = (st.TradeItemCount ?? 5) * qty;
            if (!shop.HasTradeItem(st.TradeItem, need)) { c.Fail(Reasons.NoItem, $"{sal.DisplayName} also needs {need}x {ItemRegistry.GetData(st.TradeItem)?.DisplayName ?? st.TradeItem}."); yield break; }
        }
        // Move the shop's held item into the inventory; whatever doesn't fit is dropped at the player's feet.
        bool dropped = false;
        void PutAwayHeld()
        {
            if (shop.heldItem is not Item held) return;
            shop.heldItem = null;
            if (P.addItemToInventory(held) is Item rest)
            {
                Game1.createItemDebris(rest, P.getStandingPosition(), P.FacingDirection);
                dropped = true;
            }
        }

        // A leftover held item (e.g. from a manual click) would be stacked onto; put it away before the space check.
        PutAwayHeld();
        var probe = sal.GetSalableInstance();
        probe.Stack = qty * sal.Stack;
        if (!sal.IsRecipe && !sal.IsInfiniteStock() && !probe.CanBuyItem(P)) { c.Fail(Reasons.InventoryFull, $"Can't buy {sal.DisplayName}: inventory is full or you can't hold it. " + StorageHint()); yield break; }

        var purchase = Rt.Helper.Reflection.GetMethod(shop, "tryToPurchaseItem");
        int maxChunk = Math.Max(1, sal.GetSalableInstance().maximumStackSize());
        int bought = 0, spent = 0;
        while (bought < qty)
        {
            int n = Math.Min(qty - bought, maxChunk);
            int before = ShopMenu.getPlayerCurrencyAmount(P, shop.currency), stockBefore = st.Stock;
            bool soldOut = purchase.Invoke<bool>(sal, null, n, 0, 0);
            int paid = before - ShopMenu.getPlayerCurrencyAmount(P, shop.currency);
            if (soldOut)
            {
                // Same bookkeeping the menu does after a click that empties the stock.
                shop.itemPriceAndStock.Remove(sal);
                shop.forSale.Remove(sal);
            }
            // Free items charge nothing: count them only if something visibly happened.
            bool got = paid > 0 || shop.heldItem != null || st.Stock < stockBefore ||
                       (sal.IsRecipe && sal is Item r && (P.cookingRecipes.ContainsKey(r.BaseName) || P.craftingRecipes.ContainsKey(r.BaseName)));
            PutAwayHeld();
            if (!got) break;
            bought += n;
            spent += paid;
            if (soldOut || Game1.activeClickableMenu != shop) break;
        }
        yield return Ticks(10);

        if (bought == 0) { c.Fail($"The shop refused to sell {sal.DisplayName}."); yield break; }
        var left = $"Money left {ShopMenu.getPlayerCurrencyAmount(P, shop.currency)}{cur}.";
        if (sal.IsRecipe)
        {
            var key = (sal as Item)?.BaseName ?? sal.Name;
            bool known = P.cookingRecipes.ContainsKey(key) || P.craftingRecipes.ContainsKey(key);
            c.Result = known ? $"Learned recipe: {key} for {spent}{cur}. {left}" : $"Paid {spent}{cur} for {sal.DisplayName}, but the recipe is not in your recipe list. {left}";
            yield break;
        }
        var note = dropped ? " Inventory was full: the rest was dropped at your feet." : "";
        c.Result = $"Bought {bought * sal.Stack}x {sal.DisplayName} for {spent}{cur}.{note} {left}";
    }

    /// <summary>
    /// Why seeds that grow this season but can't ripen before it ends (and don't grow next season) are not bought, or null.
    /// Fall 28, day 84: AI1 paid 8000g for 200 Wheat and 100 Yam seeds that could only die. Next season's seeds stay buyable.
    /// </summary>
    private static string? SeedWouldDie(ISalable sal)
    {
        if (sal.GetSalableInstance() is not Item seed || seed.Category != SObject.SeedsCategory || !Crop.TryGetData(seed.ItemId, out var data)) return null;
        if (!IsSeasonCropSeed(seed)) return null;
        var next = (Season)(((int)Game1.season + 1) % 4);
        if (data.Seasons?.Contains(next) == true) return null;
        var late = Game1.dayOfMonth >= CropMath.DaysPerSeason ? "it is the last day of the season" : SeedTooLate(seed);
        return late == null ? null
            : $"Not bought: {sal.DisplayName} planted now would die before harvest ({late}), and it does not grow in {next.ToString().ToLowerInvariant()} either. " +
              "Buy a faster crop, or wait and buy next season's seeds.";
    }

    private static IEnumerator Ship(JsonElement a, CallContext c)
    {
        // Days 59-119: 37 ship calls failed off the Farm; walk there like sell walks to a shop.
        if (Loc is not Farm)
        {
            if (FindItem(S(a, "item")) < 0) { c.Fail(Reasons.NoItem, $"No \"{S(a, "item")}\" in inventory."); yield break; }
            var trip = new CallContext();
            yield return TravelTo("Farm", trip);
            if (!trip.Ok || Loc is not Farm) { c.Fail(trip.Reason ?? Reasons.NoPath, $"The shipping bin is on the Farm, and the way there failed: {trip.Result}"); yield break; }
        }
        if (Loc is not Farm farm) yield break;
        var bin = farm.buildings.FirstOrDefault(b => b.buildingType.Value == "Shipping Bin");
        if (bin == null) { c.Fail(Reasons.NotFound, "No shipping bin found."); yield break; }
        var name = S(a, "item");
        int slot = FindItem(name);
        if (slot < 0) { c.Fail(Reasons.NoItem, $"No \"{name}\" in inventory."); yield break; }
        var item = P.Items[slot];
        if (item is Tool || !item.canBeShipped()) { c.Fail($"{item.DisplayName} can't be shipped."); yield break; }
        int requested = Has(a, "quantity") ? Math.Clamp(I(a, "quantity", item.Stack), 1, item.Stack) : item.Stack;
        var protection = SaleProtectionError(item, requested, a);
        if (protection != null) { c.Fail(protection); yield break; }

        var binTile = new Point(bin.tileX.Value, bin.tileY.Value);
        var sub = new CallContext();
        yield return WalkAdjacent(binTile, sub);
        if (!sub.Ok) yield return WalkAdjacent(new Point(binTile.X + 1, binTile.Y), sub = new CallContext());
        if (!sub.Ok) { c.Fail(sub.Reason ?? Reasons.NoPath, $"Can't reach the shipping bin: {sub.Result}"); yield break; }

        int qty = Has(a, "quantity") ? Math.Clamp(I(a, "quantity", item.Stack), 1, item.Stack) : item.Stack;
        var toShip = item.getOne();
        toShip.Stack = qty;
        item.Stack -= qty;
        if (item.Stack <= 0) P.Items[slot] = null;
        farm.getShippingBin(P).Add(toShip);
        farm.lastItemShipped = toShip;
        farm.showShipment(toShip, false);
        yield return Ticks(20);
        int value = toShip.sellToStorePrice() * qty;
        c.Result = $"Shipped {qty}x {toShip.DisplayName} (worth about {value}g, paid overnight).";
    }

    private static IEnumerator Eat(JsonElement a, CallContext c)
    {
        var name = S(a, "item");
        int slot = FindItem(name, it => it is SObject o && o.Edibility > -300);
        if (slot < 0) { c.Fail(Reasons.NoItem, $"No edible \"{name}\" in inventory."); yield break; }
        yield return WaitIdle();
        var before = (int)P.Stamina;
        var itemName = P.Items[slot].DisplayName;
        yield return EatSlot(slot);
        c.Result = $"Ate {itemName}: energy {before} → {(int)P.Stamina}.";
    }

    /// <summary>Eat the item in a slot with the game's own <c>Farmer.eatHeldObject</c> and wait for the animation to end (shared by eat and fight's eat_below).</summary>
    internal static IEnumerator EatSlot(int slot)
    {
        P.CurrentToolIndex = slot;
        // eatHeldObject eats the last item picked up, not the held slot: right after a catch it ate that fish and left a 0 stack
        P.mostRecentlyGrabbedItem = P.Items[slot];
        P.eatHeldObject();
        yield return Ticks(30);
        for (int i = 0; i < 400 && P.isEating; i++) yield return null;
        yield return WaitIdle();
    }

    // ------------------------------------------------------------------ sleep

    /// <summary>Press OK on the end-of-day shipping screen once. okClicked restarts the 800 ms outro fade,
    /// so clicking again during the outro (two clickers a second apart) keeps the menu open forever.</summary>
    public static void ClickShippingOk(ShippingMenu sm)
    {
        if (!Rt.Helper.Reflection.GetField<bool>(sm, "outro").GetValue())
            Rt.Helper.Reflection.GetMethod(sm, "okClicked").Invoke();
    }

    private static IEnumerator ChooseProfession(JsonElement a, CallContext c)
    {
        var pending = Rt.PendingProfessions;
        if (pending.Count == 0) { c.Fail("No profession choice is pending. They come with skill levels 5 and 10."); yield break; }
        var skillName = S(a, "skill");
        Point choice;
        if (skillName.Length > 0)
        {
            int skill = Professions.SkillIndex(skillName);
            var match = pending.Where(p => p.X == skill).OrderBy(p => p.Y).ToList();
            if (match.Count == 0) { c.Fail($"No pending choice for \"{skillName}\". Pending: {string.Join(", ", pending.Select(p => $"{Professions.SkillName(p.X)} {p.Y}"))}."); yield break; }
            choice = match[0];
        }
        else choice = pending.OrderBy(p => p.Y).First();
        var text = Professions.Choose(Rt, P, choice, I(a, "option", -1), out var err);
        if (text == null) { c.Fail(err); yield break; }
        Rt.Log.Add("system", text: $"Profession chosen: {text}");
        c.Result = text;
    }

    /// <summary>Walk home to the bed and ask to sleep (the sleep ready check opens). <paramref name="first"/>: tonight's first time,
    /// which also takes the morning briefing's snapshot and starts the sleep stats.</summary>
    private static IEnumerator LieDown(CallContext c, bool first)
    {
        // A menu left open (a shop after a failed buy) would block the walk home and hold the others' sleep check.
        if (Game1.activeClickableMenu != null)
            yield return CloseMenu(default, new CallContext());

        if (Loc is not FarmHouse)
        {
            var sub = new CallContext();
            yield return TravelTo("FarmHouse", sub);
            if (!sub.Ok) { c.Fail(sub.Reason ?? Reasons.NoPath, $"Couldn't get home: {sub.Result}"); yield break; }
        }
        var house = (FarmHouse)Loc;
        var spot = house.GetPlayerBedSpot();
        var walk = new CallContext();
        yield return WalkTo(spot, walk);
        // A pet asleep between the door and the bed blocks the only path (day 21: lying down at the door held the sleep check
        // for good). Move it aside and walk again; if the bed still can't be reached, step into it the way passing out does.
        if (!walk.Ok && MovePetsAside(house))
        {
            walk = new CallContext();
            yield return WalkTo(spot, walk);
        }
        if (!walk.Ok)
        {
            Rt.Log.Add("system", text: $"Bed blocked ({walk.Result}): stepped into bed at ({spot.X},{spot.Y})");
            P.controller = null;
            P.Halt();
            P.setTileLocation(spot.ToVector2());
        }
        if (Game1.activeClickableMenu is DialogueBox db)
            db.closeDialogue();
        else if (Game1.activeClickableMenu != null)
            Game1.exitActiveMenu();
        Game1.dialogueUp = false;
        if (first)
            Briefing.CaptureForSleep();
        P.isInBed.Value = true;
        if (first)
            Rt.Stats.OnSleepStart();
        house.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
        Rt.Log.Add("system", text: "Sleeping...");
        c.Result = "In bed";
    }

    /// <summary>Move the pets in the house to a random open spot. True when one moved.</summary>
    private static bool MovePetsAside(FarmHouse house)
    {
        bool moved = false;
        foreach (var pet in house.characters.OfType<StardewValley.Characters.Pet>().ToList())
        {
            var to = house.getRandomOpenPointInHouse(Game1.random, 1);
            if (to == Point.Zero || to == pet.TilePoint) continue;
            pet.Halt();
            pet.setTileLocation(to.ToVector2());
            Rt.Log.Add("system", text: $"{pet.Name} was in the way to the bed: moved it to ({to.X},{to.Y})");
            moved = true;
        }
        return moved;
    }

    private static IEnumerator GoToBed(JsonElement a, CallContext c)
    {
        if (!Rt.Log.HasJournal(AgentLog.FarmDay))
        {
            c.Fail(Reasons.NotReady, $"Write today's journal first: call journal with your diary, lessons and plan (it costs no game time), then call go_to_bed again. Farm day {AgentLog.FarmDay} has no journal yet.");
            yield break;
        }

        // Repeating the call must not bypass the morning-work guard. Rest/eat/store items
        // and do quests or socialize instead; exhausted or seriously hurt farmers may sleep.
        if (DaySafety.BlockEarlyBed(Game1.timeOfDay, P.Stamina, P.MaxStamina, P.health, P.maxHealth))
        {
            bool repeat = Rt.EarlyBedDay == AgentLog.FarmDay;
            Rt.EarlyBedDay = AgentLog.FarmDay;
            var refusal = DaySafety.EarlyBedRefusal(WorldText.FormatTime(Game1.timeOfDay), (int)P.Stamina, P.MaxStamina, NextTask(), repeat);
            if (!repeat && PendingFarmWork() is { Length: > 0 } work) refusal += " " + work.Trim();
            c.Fail(Reasons.NotReady, refusal);
            yield break;
        }

        // Out of energy in the early afternoon: a rest in bed gives it back and the day goes on (asked once a day; a second call sleeps).
        if (DaySafety.SuggestRest(Game1.timeOfDay, P.Stamina, P.MaxStamina, P.health, P.maxHealth, Game1.IsMultiplayer) && Rt.RestSuggestedDay != AgentLog.FarmDay)
        {
            Rt.RestSuggestedDay = AgentLog.FarmDay;
            c.Fail(Reasons.NotReady, $"You are tired (energy {(int)P.Stamina}/{P.MaxStamina}), but it is only {WorldText.FormatTime(Game1.timeOfDay)}. " +
                                     "Instead of ending the day, rest in your bed (rest: about +14 energy every 10 minutes, it stops when you are full or after the minutes you give) " +
                                     "and then get up and carry on. " + PendingFarmWork() + "If you really want to end the day, call go_to_bed again.");
            yield break;
        }

        int dayBefore = Game1.Date.TotalDays;
        int moneyBefore = P.Money;
        Professions.ResolveDefaults(Rt, P, "not chosen before bed");
        Naming.ResolveDefaults(Rt, "not named before bed");
        var lie = new CallContext();
        yield return LieDown(lie, first: true);
        if (!lie.Ok) { c.Fail(lie.Reason ?? Reasons.NoPath, lie.Result ?? "Couldn't get to bed."); yield break; }

        // Wait for the new day; click through end-of-day screens.
        var menuWatch = new UnknownMenuWatch(); // an unexpected menu open for 10 s is closed (RunControl.cs)
        int outOfBed = 0;
        try
        {
            // An early sleeper waits out the others' whole day (about 15 running minutes up to 2:00 AM): giving up after 5 got it
            // out of bed for good once its session had ended (day 9). An hour of running time is only a safety net.
            for (int t = 0; t < 60 * 60 * 60; t++)
            {
                menuWatch.Tick(Rt);
                switch (Game1.activeClickableMenu)
                {
                    case ShippingMenu sm when t % 60 == 0:
                        ClickShippingOk(sm);
                        break;
                    case LevelUpMenu lm when t % 60 == 0 && lm.isActive:
                        if (lm.isProfessionChooser)
                            Professions.Defer(lm, Rt);
                        else
                            lm.okButtonClicked();
                        break;
                }
                // A question or a naming box in the night (a child): hand it to the AI instead of waiting on it (#53). An AI already
                // told its day is over (Perception.ReplyInTime) has gone and can't answer: the night answers for it.
                if (t > 30 && Rt.CurrentCall?.Done.Task.IsCompleted == true)
                    AnswerNightAlone(Rt, t);
                else if (t > 30 && (Romance.NightQuestion(c, Rt, t) || Naming.NightPrompt(c)))
                    yield break;
                if (Game1.Date.TotalDays > dayBefore && Context.IsWorldReady && Game1.activeClickableMenu == null && !Game1.fadeToBlack && !Game1.newDay && Game1.farmEvent == null && !Game1.game1.isLocalMultiplayerNewDayActive && !P.passedOut && P.CanMove)
                    break;
                // Pulled out of bed by something else (a town cutscene took a sleeper away on day 7): lie down again.
                bool up = Game1.Date.TotalDays == dayBefore && !P.isInBed.Value && Game1.activeClickableMenu == null && !Game1.eventUp &&
                          !Game1.fadeToBlack && !Game1.newDay && !Game1.isWarping && P.CanMove;
                outOfBed = up ? outOfBed + 1 : 0;
                if (outOfBed > 120)
                {
                    outOfBed = 0;
                    Rt.Log.Add("system", text: $"Out of bed at {Loc.Name}: going back to bed");
                    yield return LieDown(new CallContext(), first: false);
                }
                yield return null;
            }
            yield return Ticks(60);
        }
        finally
        {
            // Woken while waiting for the others (Perception's interrupt disposes this coroutine): get up the way the
            // ready check's Cancel button does. Never once the night has begun (the check is no longer cancelable then).
            if (Game1.activeClickableMenu is ReadyCheckDialog { checkName: "sleep" } check && check.isCancelable())
            {
                check.cancel();
                Briefing.Unseal();
                Rt.Stats.OnSleepCancelled();
                Rt.Log.Add("system", text: "Got out of bed");
            }
        }

        if (Game1.Date.TotalDays <= dayBefore) { Briefing.Unseal(); c.Fail("Tried to sleep but the day did not change."); yield break; }
        Rt.Log.Add("system", text: $"New day: farm day {AgentLog.FarmDay}");
        c.Result = $"Good morning! It's now farm day {AgentLog.FarmDay}. Money {moneyBefore}g → {P.Money}g (shipping income {P.Money - moneyBefore}g). Your day is over — stop here.";
    }

    /// <summary>The night of a farmer whose AI was already told its day is over: a naming box gets a random name and a question
    /// the game's last answer ("No" comes last), so the night never waits on an AI that has gone.</summary>
    private static void AnswerNightAlone(AgentRuntime rt, int tick)
    {
        switch (Game1.activeClickableMenu)
        {
            case NamingMenu nm:
                Naming.AutoName(rt, nm, "its day was already over");
                break;
            case DialogueBox { isQuestion: true } box when box.responses?.Length > 0:
                if (tick % 90 != 0 || box.transitioning) break;
                box.selectedResponse = box.responses.Length - 1;
                rt.Log.Add("system", text: $"Night question answered \"{box.responses[^1].responseText}\": its day was already over");
                box.receiveLeftClick(0, 0);
                break;
            default:
                Romance.NightQuestion(new CallContext(), rt, tick); // a text-only box is read out to the log and clicked through
                break;
        }
    }
}
