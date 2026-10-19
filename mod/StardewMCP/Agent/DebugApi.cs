using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// Test hooks for runner/smoke.py, served at POST /api/debug/{op} only when
/// ModConfig.EnableDebugApi is true. They are deliberately not MCP tools, so the player AI
/// never sees them. Each op runs on the game thread through the normal AgentRuntime queue.
/// </summary>
public static class DebugApi
{
    private static string? _pendingLoad;
    private static bool _exiting;
    private static int _titleTicks;
    private static int _hangSeconds;

    private static string S(JsonElement a, string name, string def = "") =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    private static int I(JsonElement a, string name, int def) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number;

    private static bool? B(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    public static Dictionary<string, ToolDef> All()
    {
        ToolDef Op(string name, ToolRunner run, bool needsWorld = true) =>
            new() { Name = name, Description = "debug", Schema = new { }, Instant = true, NeedsWorld = needsWorld, AppendStatus = false, Mail = false, Run = run };

        return new[]
        {
            Op("warp", Warp),
            Op("set_time", SetTime),
            Op("add_item", AddItem),
            Op("clear_items", ClearItems),
            Op("set_money", SetMoney),
            Op("add_mail", AddMail),
            Op("mail_received", AddMailReceived),
            Op("set_power", SetPower),
            Op("set_club_coins", SetClubCoins),
            Op("set_walnuts", SetWalnuts),
            Op("set_bus_ready", SetBusReady),
            Op("set_skill", SetSkill),
            Op("set_health", SetHealth),
            Op("set_stamina", SetStamina),
            Op("open_shop", OpenShop),
            Op("place_object", PlaceObject),
            Op("set_mine_level", SetMineLevel),
            Op("separate_wallets", SeparateWallets),
            Op("makeover", Makeover),
            Op("look", LookOp),
            Op("set_look", SetLook),
            Op("set_friendship", RomanceDebug.SetFriendship),
            Op("open_naming", RomanceDebug.OpenNaming),
            Op("last_named", RomanceDebug.LastNamed),
            Op("resolve_naming", RomanceDebug.ResolveNaming),
            Op("load_save", LoadSave, needsWorld: false),
            Op("build_cabins", BuildCabins),
            Op("starting_cabins", StartingCabins),
            Op("save", SaveNow),
            Op("place_chest", PlaceChest),
            Op("grow_crops", GrowCrops),
            Op("capture", Capture, needsWorld: false),
            Op("chat_log", ChatLog),
            Op("disconnect", Disconnect),
            Op("hang", Hang, needsWorld: false),
            Op("perception", PerceptionOp),
            Op("set_display", SetDisplay),
        }.ToDictionary(t => t.Name);
    }

    // ------------------------------------------------------------------ ops

    private static IEnumerator Warp(JsonElement a, CallContext c)
    {
        var name = S(a, "location");
        if (Game1.getLocationFromName(name) == null) { c.Fail($"Unknown location \"{name}\""); yield break; }
        int x = I(a, "x", 0), y = I(a, "y", 0);

        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        Game1.warpFarmer(name, x, y, false);
        for (int i = 0; i < 600 && (Game1.isWarping || Game1.fadeToBlack || Game1.currentLocation?.Name != name); i++)
            yield return null;
        if (Game1.currentLocation?.Name != name) { c.Fail($"Warp to {name} did not finish"); yield break; }
        yield return null;
        c.Result = $"Warped to {name} ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y})";
    }

    private static IEnumerator SetTime(JsonElement a, CallContext c)
    {
        int t = I(a, "time", -1);
        if (t < 600 || t > 2600 || t % 10 != 0 || t % 100 >= 60) { c.Fail("time must be HHMM in 600..2600 (e.g. 900, 1830)"); yield break; }
        Game1.timeOfDay = t;
        c.Result = $"Time set to {WorldText.FormatTime(t)}";
    }

    private static IEnumerator ClearItems(JsonElement a, CallContext c)
    {
        var items = Game1.player.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is StardewValley.Tools.Axe or StardewValley.Tools.Hoe or StardewValley.Tools.Pickaxe or StardewValley.Tools.WateringCan or StardewValley.Tools.MeleeWeapon)
                continue;
            items[i] = null;
        }

        var farm = Game1.getFarm();
        if (farm != null)
        {
            var allDirt = farm.terrainFeatures.Pairs
                .Where(kv => kv.Value is StardewValley.TerrainFeatures.HoeDirt)
                .Select(kv => kv.Key).ToList();
            foreach (var pos in allDirt) farm.terrainFeatures.Remove(pos);

            var toRemove = farm.Objects.Pairs
                .Where(kv => kv.Value is StardewValley.Objects.Chest or StardewValley.Object { Name: "Furnace" or "Wood Fence" }
                             || (Math.Abs(kv.Key.X - 64) <= 8 && Math.Abs(kv.Key.Y - 17) <= 6 && kv.Value is StardewValley.Object { IsSpawnedObject: true }))
                .Select(kv => kv.Key).ToList();
            foreach (var pos in toRemove) farm.Objects.Remove(pos);
        }

        c.Result = "Pack cleared (starter tools kept, farm clutter cleaned)";
        yield break;
    }

    private static IEnumerator AddItem(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        int qty = Math.Max(1, I(a, "qty", 1));
        if (id.Length == 0) { c.Fail("id is required (e.g. \"(O)472\" or \"472\")"); yield break; }
        if (!id.StartsWith("(")) id = "(O)" + id;

        Item? item;
        try { item = ItemRegistry.Create(id, qty); }
        catch (Exception ex) { c.Fail($"Cannot create {id}: {ex.Message}"); yield break; }
        if (item == null) { c.Fail($"Cannot create {id}"); yield break; }

        if (Has(a, "quality")) item.Quality = Math.Clamp(I(a, "quality", 0), 0, 4);
        if (!Game1.player.addItemToInventoryBool(item)) { c.Fail("Inventory is full"); yield break; }
        c.Result = $"Added {qty} x {item.DisplayName}";
        yield break;
    }

    private static IEnumerator SetMoney(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.Money = Math.Max(0, I(a, "n", 0));
        c.Result = $"Money set to {Game1.player.Money}g";
        yield break;
    }

    /// <summary>Put a letter in the mailbox (any id works for the briefing and footer checks; opening it needs a real Data/Mail id).</summary>
    private static IEnumerator AddMail(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        if (id.Length == 0) { c.Fail("id is required (a mail id, e.g. \"smoke_letter\")"); yield break; }
        Game1.player.mailbox.Add(id);
        c.Result = $"Mailbox now holds {Game1.player.mailbox.Count} letter(s)";
        yield break;
    }

    /// <summary>Add a mail flag (a world flag such as ccVault or willyBoatFixed) for late-game smoke setup (#55).</summary>
    private static IEnumerator AddMailReceived(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        if (id.Length == 0) { c.Fail("id is required (a mail flag, e.g. \"ccVault\")"); yield break; }
        Game1.MasterPlayer.mailReceived.Add(id);
        Game1.player.mailReceived.Add(id);
        if (id.Equals("willyBoatFixed", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var flag in new[] { "willyBoatTicketMachine", "willyBoatHull", "willyBoatAnchor" })
            {
                Game1.MasterPlayer.mailReceived.Add(flag);
                Game1.player.mailReceived.Add(flag);
            }
        }
        c.Result = $"Mail flag {id} set";
        yield break;
    }

    /// <summary>Turn on a late-game power (skull_key, club_card, magnifying_glass) for smoke setup (#55).</summary>
    private static IEnumerator SetPower(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim().ToLowerInvariant().Replace(" ", "_");
        bool on = I(a, "on", 1) != 0;
        switch (name)
        {
            case "skull_key": Game1.player.hasSkullKey = on; break;
            case "club_card": Game1.player.hasClubCard = on; break;
            case "magnifying_glass": Game1.player.hasMagnifyingGlass = on; break;
            default: c.Fail("name must be skull_key, club_card or magnifying_glass"); yield break;
        }
        c.Result = $"{name} {(on ? "on" : "off")}";
        yield break;
    }

    private static IEnumerator SetClubCoins(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.clubCoins = Math.Max(0, I(a, "n", 0));
        c.Result = $"Qi coins set to {Game1.player.clubCoins}";
        yield break;
    }

    private static IEnumerator SetWalnuts(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        int found = Math.Clamp(I(a, "n", 0), 0, 130);
        Game1.netWorldState.Value.GoldenWalnutsFound = found;
        Game1.netWorldState.Value.GoldenWalnuts = found;
        c.Result = $"Golden walnuts set to {found}/130";
        yield break;
    }

    /// <summary>Let the bus run without waiting for Pam's schedule (for smoke cases only).</summary>
    private static IEnumerator SetBusReady(JsonElement a, CallContext c)
    {
        Game1.netWorldState.Value.canDriveYourselfToday.Value = true;
        c.Result = "The bus can be driven now";
        yield break;
    }

    /// <summary>Open a shop by id even when its owner is away (set_time does not move NPCs).</summary>
    private static IEnumerator OpenShop(JsonElement a, CallContext c)
    {
        var id = S(a, "id");
        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
        if (!Utility.TryOpenShopMenu(id, Game1.currentLocation, forceOpen: true) || Game1.activeClickableMenu is not ShopMenu)
        {
            c.Fail($"Could not open shop \"{id}\"");
            yield break;
        }
        c.Result = $"Opened shop {id}";
    }

    private static readonly int[] XpForLevel = { 0, 100, 380, 770, 1300, 2150, 3300, 4800, 6900, 10000, 15000 };

    private static IEnumerator SetSkill(JsonElement a, CallContext c)
    {
        var skill = S(a, "skill").ToLowerInvariant();
        int level = Math.Clamp(I(a, "level", 0), 0, 10);
        var p = Game1.player;
        int index, old = skill switch
        {
            "farming" => p.farmingLevel.Value, "fishing" => p.fishingLevel.Value, "foraging" => p.foragingLevel.Value,
            "mining" => p.miningLevel.Value, "combat" => p.combatLevel.Value, _ => 0
        };
        switch (skill)
        {
            case "farming": index = 0; p.farmingLevel.Value = level; break;
            case "fishing": index = 1; p.fishingLevel.Value = level; break;
            case "foraging": index = 2; p.foragingLevel.Value = level; break;
            case "mining": index = 3; p.miningLevel.Value = level; break;
            case "combat": index = 4; p.combatLevel.Value = level; break;
            default: c.Fail("skill must be farming, fishing, foraging, mining or combat"); yield break;
        }
        p.experiencePoints[index] = XpForLevel[level];
        // Queue tonight's level-up screens (profession choices at 5 and 10) for the levels gained, like real play.
        for (int l = old + 1; l <= level; l++)
            if (!p.newLevels.Contains(new Microsoft.Xna.Framework.Point(index, l)))
                p.newLevels.Add(new Microsoft.Xna.Framework.Point(index, l));
        c.Result = $"{skill} level set to {level}";
        yield break;
    }

    private static IEnumerator SetHealth(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.health = Math.Clamp(I(a, "n", 0), 1, Game1.player.maxHealth);
        c.Result = $"Health set to {Game1.player.health}/{Game1.player.maxHealth}";
        yield break;
    }

    private static Func<bool>? _makeoverOpen;
    private static Action? _makeoverDone;

    /// <summary>Open the full character creator (the farmhand one: shirts and pants included, no farm options) on P1's
    /// screen for a human, with the mouse and keyboard unlocked until it closes (Tick puts the lock back). Name and
    /// favourite thing are restored then, so only the look changes. Farmhands can't take mouse input, so their look is
    /// made here, read with look and given to them with set_look.</summary>
    private static IEnumerator Makeover(JsonElement a, CallContext c)
    {
        if (Context.ScreenId != 0) { c.Fail("makeover only works on p1 (the only screen with a mouse); then look + set_look to the farmhand"); yield break; }
        if (_makeoverDone != null) { c.Fail("the creator is already open on p1"); yield break; }
        var p = Game1.player;
        var cfg = AgentTools.Config;
        (string name, string fav, bool locked, bool clean) saved = (p.Name, p.favoriteThing.Value, cfg.LockHostInput, cfg.CleanFrames);
        if (string.IsNullOrEmpty(p.favoriteThing.Value)) p.favoriteThing.Value = "-"; // OK refuses an empty favourite thing
        cfg.LockHostInput = false;
        cfg.CleanFrames = false;
        var menu = new CharacterCustomization(CharacterCustomization.Source.NewFarmhand);
        Game1.activeClickableMenu = menu;
        _makeoverOpen = () => Game1.activeClickableMenu == menu;
        _makeoverDone = () =>
        {
            p.Name = saved.name;
            p.displayName = saved.name;
            p.favoriteThing.Value = saved.fav;
            p.ConvertClothingOverrideToClothesItems();
            cfg.LockHostInput = saved.locked;
            cfg.CleanFrames = saved.clean;
        };
        c.Result = "Character creator open on P1's screen: mouse unlocked until OK";
    }

    /// <summary>This seat's look as JSON that set_look takes back, so a look made with makeover survives restarts and save
    /// restores (the game only writes looks at the end of a day).</summary>
    private static IEnumerator LookOp(JsonElement a, CallContext c)
    {
        var p = Game1.player;
        c.Result = JsonSerializer.Serialize(new
        {
            female = !p.IsMale, hair = p.hair.Value, hairColor = Hex(p.hairstyleColor.Value), skin = p.skin.Value,
            eyeColor = Hex(p.newEyeColor.Value), accessory = p.accessory.Value,
            shirt = p.shirtItem.Value?.ItemId, shirtColor = p.shirtItem.Value is { } s ? Hex(s.clothesColor.Value) : null,
            pants = p.pantsItem.Value?.ItemId, pantsColor = p.pantsItem.Value is { } t ? Hex(t.clothesColor.Value) : null,
        });
        yield break;
    }

    /// <summary>Apply a look from the look op (every field optional): {"female","hair","hairColor","skin","eyeColor",
    /// "accessory","shirt","shirtColor","pants","pantsColor"}, colours as #RRGGBB.</summary>
    private static IEnumerator SetLook(JsonElement a, CallContext c)
    {
        var p = Game1.player;
        if (B(a, "female") is bool female) p.changeGender(!female);
        if (Has(a, "hair")) p.changeHairStyle(I(a, "hair", 0));
        if (Color(a, "hairColor") is { } hair) p.changeHairColor(hair);
        if (Has(a, "skin")) p.changeSkinColor(I(a, "skin", 0), force: true);
        if (Color(a, "eyeColor") is { } eyes) p.changeEyeColor(eyes);
        if (Has(a, "accessory")) p.changeAccessory(I(a, "accessory", -1));
        if (S(a, "shirt") is { Length: > 0 } shirt)
        {
            if (!ItemRegistry.Exists("(S)" + shirt)) { c.Fail($"unknown shirt {shirt}"); yield break; }
            p.Equip(new StardewValley.Objects.Clothing(shirt), p.shirtItem);
            p.shirt.Value = "-1";
        }
        if (Color(a, "shirtColor") is { } sc && p.shirtItem.Value is { } worn) worn.clothesColor.Value = sc;
        if (S(a, "pants") is { Length: > 0 } pants)
        {
            if (!ItemRegistry.Exists("(P)" + pants)) { c.Fail($"unknown pants {pants}"); yield break; }
            p.Equip(new StardewValley.Objects.Clothing(pants), p.pantsItem);
            p.pants.Value = "-1";
        }
        if (Color(a, "pantsColor") is { } pc) p.changePantsColor(pc);
        p.FarmerRenderer.MarkSpriteDirty();
        c.Result = $"{p.Name}: {(p.IsMale ? "male" : "female")}, hairstyle {p.hair.Value}, shirt {p.shirtItem.Value?.Name ?? "none"}, " +
                   $"pants {p.pantsItem.Value?.Name ?? "none"}";
    }

    private static string Hex(Microsoft.Xna.Framework.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Microsoft.Xna.Framework.Color? Color(JsonElement a, string name)
    {
        string hex = S(a, name).TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)
            ? new Microsoft.Xna.Framework.Color((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) : null;
    }

    private static IEnumerator SetStamina(JsonElement a, CallContext c)
    {
        if (!Has(a, "n")) { c.Fail("n is required"); yield break; }
        Game1.player.Stamina = Math.Clamp(I(a, "n", 0), 0, Game1.player.MaxStamina);
        c.Result = $"Energy set to {(int)Game1.player.Stamina}/{Game1.player.MaxStamina}";
        yield break;
    }

    /// <summary>Put a machine, chest or other placeable item on a tile through the game's own placementAction.</summary>
    private static IEnumerator PlaceObject(JsonElement a, CallContext c)
    {
        var id = S(a, "id").Trim();
        int x = I(a, "x", -1), y = I(a, "y", -1);
        var loc = Game1.currentLocation;
        if (ItemRegistry.Create(id) is not StardewValley.Object obj) { c.Fail($"{id} is not a placeable object (e.g. \"(BC)13\")"); yield break; }
        if (loc.Objects.ContainsKey(new Microsoft.Xna.Framework.Vector2(x, y)) || !obj.placementAction(loc, x * 64, y * 64, Game1.player))
        { c.Fail($"Could not place {obj.DisplayName} at ({x},{y}) on {loc.Name}"); yield break; }
        c.Result = $"Placed {obj.DisplayName} at ({x},{y})";
    }

    /// <summary>Deepest mine floor reached, which decides the elevator's floors.</summary>
    private static IEnumerator SetMineLevel(JsonElement a, CallContext c)
    {
        StardewValley.Locations.MineShaft.lowestLevelReached = Math.Clamp(I(a, "n", 0), 0, 120);
        c.Result = $"Lowest mine level {StardewValley.Locations.MineShaft.lowestLevelReached}";
        yield break;
    }

    /// <summary>Switch to separate wallets now instead of overnight, the same call the ledger book's choice makes at night.</summary>
    private static IEnumerator SeparateWallets(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run separate_wallets on p1 (the host)"); yield break; }
        StardewValley.Locations.ManorHouse.SeparateWallets();
        c.Result = Game1.player.team.useSeparateWallets.Value ? $"Separate wallets on; you have {Game1.player.Money}g" : "Wallets are still shared";
    }

    private static IEnumerator LoadSave(JsonElement a, CallContext c)
    {
        var name = S(a, "name").Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
        { c.Fail("name must be a save folder name"); yield break; }
        if (!Directory.Exists(Path.Combine(Constants.SavesPath, name))) { c.Fail($"No save folder \"{name}\""); yield break; }

        _pendingLoad = name;
        _exiting = false;
        _titleTicks = 0;
        c.Result = $"Loading {name}; poll /api/state until saveFolder matches and ready is true";
        yield break;
    }

    /// <summary>Smoke saves only: add free cabins (with farmhand slots) so split-screen can join P2-P4.</summary>
    private static IEnumerator BuildCabins(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run build_cabins on p1 (the host)"); yield break; }
        if (SaveFolder.Name?.StartsWith("smoke_", StringComparison.OrdinalIgnoreCase) != true)
        { c.Fail("build_cabins only runs on smoke_* save copies"); yield break; }

        int want = Math.Clamp(I(a, "n", 3), 1, 7);
        var farm = Game1.getFarm();
        var built = new List<string>();
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
                        else if (farm.getBuildingAt(new Microsoft.Xna.Framework.Vector2(tx, ty)) != null || farm.resourceClumps.Any(r => r.occupiesTile(tx, ty)))
                            ok = false;
                    }
                if (!ok) continue;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 5; dx++)
                    {
                        var t = new Microsoft.Xna.Framework.Vector2(x + dx, y + dy);
                        farm.objects.Remove(t);
                        farm.terrainFeatures.Remove(t);
                    }
                if (!farm.buildStructure("Cabin", new Microsoft.Xna.Framework.Vector2(x, y), Game1.player, out var b, magicalConstruction: true, skipSafetyChecks: true))
                    continue;
                if (b.GetIndoors() is StardewValley.Locations.Cabin cabin && !cabin.HasOwner)
                    cabin.CreateFarmhand();
                built.Add($"({x},{y})");
                x += 6;
            }
        }
        if (built.Count < want) { c.Fail($"Built {built.Count} of {want} cabins: {string.Join(" ", built)}"); yield break; }
        c.Result = $"Built {built.Count} cabins at {string.Join(" ", built)}";
    }

    /// <summary>Turn a farm made without starting cabins into a co-op farm, through the game's own new-game builder:
    /// the farm map's starting-cabin spots (Paths tiles 29 close / 30 separate with an Order up to n), then
    /// <see cref="GameLocation.BuildStartingCabins"/>, then a farmhand slot in each. Saved with the farm at the end of the day.</summary>
    private static IEnumerator StartingCabins(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run starting_cabins on p1 (the host)"); yield break; }
        var farm = Game1.getFarm();
        if (farm.buildings.Any(b => b.GetIndoors() is StardewValley.Locations.Cabin)) { c.Fail("The farm already has cabins"); yield break; }
        int want = Math.Clamp(I(a, "n", 3), 1, 3);
        bool separate = B(a, "separate") ?? false;
        var paths = farm.map.GetLayer("Paths");
        var spots = (List<Microsoft.Xna.Framework.Vector2>)typeof(GameLocation)
            .GetField("_startingCabinLocations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(farm)!;
        spots.Clear();
        for (int y = 0; paths != null && y < paths.LayerHeight; y++)
            for (int x = 0; x < paths.LayerWidth; x++)
            {
                var tile = paths.Tiles[x, y];
                if (tile != null && tile.TileIndex == (separate ? 30 : 29) && tile.Properties.TryGetValue("Order", out var order)
                    && int.TryParse(order.ToString(), out int n) && n <= want)
                    spots.Add(new Microsoft.Xna.Framework.Vector2(x, y));
            }
        if (spots.Count < want) { c.Fail($"This farm map marks only {spots.Count} starting-cabin spot(s) for {want}"); spots.Clear(); yield break; }
        Game1.startingCabins = want;
        Game1.cabinsSeparate = separate;
        farm.BuildStartingCabins();
        var built = new List<string>();
        foreach (var b in farm.buildings)
            if (b.GetIndoors() is StardewValley.Locations.Cabin cabin)
            {
                if (!cabin.HasOwner) cabin.CreateFarmhand();
                built.Add($"({b.tileX.Value},{b.tileY.Value})");
            }
        c.Result = $"Built {built.Count} starting cabins at {string.Join(" ", built)}; split-screen joins P2-P4 next";
        yield break;
    }

    /// <summary>Write the save now (the game itself saves only overnight): cabins, wallets and looks set up before day 1 stay.
    /// Loading it starts this same day at 6:00. Host only.</summary>
    private static IEnumerator SaveNow(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run save on p1 (the host)"); yield break; }
        var steps = SaveGame.Save();
        while (steps.MoveNext()) yield return null;
        c.Result = $"Saved {Constants.SaveFolderName} (day {Game1.Date.TotalDays + 1}, {Game1.timeOfDay})";
    }

    /// <summary>Place a chest owned by this seat on the tile (x, y) of its current map, holding 3 Parsnips.</summary>
    private static IEnumerator PlaceChest(JsonElement a, CallContext c)
    {
        var tile = new Microsoft.Xna.Framework.Vector2(I(a, "x", 0), I(a, "y", 0));
        var chest = new StardewValley.Objects.Chest(true, tile);
        chest.owner.Value = Game1.player.UniqueMultiplayerID;
        chest.Items.Add(ItemRegistry.Create("(O)24", 3));
        Game1.currentLocation.objects[tile] = chest;
        c.Result = $"Chest placed at ({tile.X},{tile.Y}), owner {Players.Current.Id}";
        yield break;
    }

    /// <summary>Ripen every crop on the current map.</summary>
    private static IEnumerator GrowCrops(JsonElement a, CallContext c)
    {
        int n = 0;
        foreach (var tf in Game1.currentLocation.terrainFeatures.Values)
            if (tf is StardewValley.TerrainFeatures.HoeDirt { crop: not null } dirt) { dirt.crop.growCompletely(); n++; }
        c.Result = $"{n} crops ripened";
        yield break;
    }

    /// <summary>Forget that an AI is attached to this seat, so an idle seat stops holding the world frozen.</summary>
    private static IEnumerator Disconnect(JsonElement a, CallContext c)
    {
        Players.Current.Connected = false;
        c.Result = $"{Players.Current.Id} disconnected";
        yield break;
    }

    /// <summary>
    /// Test hook for runner/game_watchdog.py: block the whole game loop (every screen) for N seconds, starting on the
    /// next host tick so this call still answers. The HTTP server keeps running, like in a real hang, so /api/state
    /// answers with a stale snapshot whose "heartbeat" stops counting.
    /// </summary>
    private static IEnumerator Hang(JsonElement a, CallContext c)
    {
        _hangSeconds = Math.Clamp(I(a, "seconds", 300), 1, 1800);
        c.Result = $"Blocking the game loop for {_hangSeconds}s from the next tick";
        yield break;
    }

    /// <summary>
    /// Perception switches for the smoke runner: <c>interrupts</c> 0/1 stops/allows events from interrupting running actions
    /// (the runner turns them off so cases that walk past other seats are not cut short), <c>clear</c> 1 drops every seat's pending events.
    /// </summary>
    private static IEnumerator PerceptionOp(JsonElement a, CallContext c)
    {
        Perception.SyncSave(); // so a save change that has not been noticed yet cannot undo this call
        if (Has(a, "interrupts")) Perception.Interrupts = I(a, "interrupts", 1) != 0;
        if (Has(a, "clear") && I(a, "clear", 0) != 0) Perception.Clear();
        c.Result = $"Perception interrupts are {(Perception.Interrupts ? "on" : "off")}";
        yield break;
    }

    /// <summary>What this screen's chat box shows, one line per message (to prove a line reached this player's screen).</summary>
    private static IEnumerator ChatLog(JsonElement a, CallContext c)
    {
        var lines = Game1.chatBox?.messages.Select(m => string.Concat(m.message.Select(sn => sn.message))) ?? Enumerable.Empty<string>();
        c.Result = string.Join("\n", lines) is { Length: > 0 } t ? t : "(chat box is empty)";
        yield break;
    }

    /// <summary>Save the next composed game frame (every screen, never the desktop) as PNG.</summary>
    private static IEnumerator Capture(JsonElement a, CallContext c)
    {
        var path = S(a, "path");
        if (path.Length == 0) path = Path.Combine(Path.GetTempPath(), "stardew-capture.png");
        SplitScreenPatches.LastCapture = null;
        SplitScreenPatches.PendingCapture = path;
        for (int i = 0; i < 120 && SplitScreenPatches.LastCapture == null; i++)
            yield return null;
        var last = SplitScreenPatches.LastCapture;
        if (last == null || last.StartsWith("error")) { c.Fail(last ?? "No frame was drawn"); yield break; }
        c.Result = last;
    }

    /// <summary>Set music volume, fullscreen resolution, or unlock host input for settings adjustment.</summary>
    private static IEnumerator SetDisplay(JsonElement a, CallContext c)
    {
        if (a.TryGetProperty("music", out var m) && m.ValueKind == JsonValueKind.Number)
        {
            float vol = (float)m.GetDouble();
            Game1.options.musicVolumeLevel = vol;
            try { Game1.musicCategory?.SetVolume(vol); } catch { }
        }
        if (a.TryGetProperty("unlock_input", out var u) && u.GetBoolean())
        {
            AgentTools.Config.LockHostInput = false;
            AgentTools.Config.CleanFrames = false;
        }
        if (Has(a, "w") || Has(a, "h") || a.TryGetProperty("fullscreen", out _))
        {
            int w = I(a, "w", 3840), h = I(a, "h", 2160);
            Game1.options.preferredResolutionX = w;
            Game1.options.preferredResolutionY = h;
            Game1.graphics.PreferredBackBufferWidth = w;
            Game1.graphics.PreferredBackBufferHeight = h;
            if (a.TryGetProperty("fullscreen", out var fs))
            {
                bool fullscreen = fs.GetBoolean();
                Game1.options.windowedBorderlessFullscreen = false;
                Game1.options.fullscreen = fullscreen;
                Game1.graphics.IsFullScreen = fullscreen;
            }
            Game1.graphics.ApplyChanges();
        }
        c.Result = $"BackBuffer: {Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferWidth}x{Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferHeight}, FullScreen: {Game1.options.fullscreen}, Music: {Game1.options.musicVolumeLevel}, HostLocked: {AgentTools.Config.LockHostInput}";
        yield break;
    }

    // ------------------------------------------------------------------ save switching

    /// <summary>Called every tick from ModEntry: leave the current save, then load the requested one from the title screen.</summary>
    public static void Tick()
    {
        if (_hangSeconds > 0)
        {
            int seconds = _hangSeconds;
            _hangSeconds = 0;
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        if (_makeoverDone != null && Context.ScreenId == 0 && !_makeoverOpen!())
        {
            _makeoverDone();
            _makeoverDone = null;
            _makeoverOpen = null;
        }

        if (_pendingLoad == null) return;

        if (Context.IsWorldReady)
        {
            if (!_exiting)
            {
                _exiting = true;
                Game1.ExitToTitle();
            }
            return;
        }

        if (Game1.activeClickableMenu is TitleMenu && ++_titleTicks > 60)
        {
            var name = _pendingLoad;
            _pendingLoad = null;
            _exiting = false;
            SaveFolder.Pending = name;
            SaveGame.Load(name);
            Game1.exitActiveMenu();
        }
    }
}

/// <summary>
/// The folder name of the loaded save. SMAPI's own save name drops everything after the first underscore of a
/// custom folder name (smoke_x_123 becomes smoke_123), so remember what we asked the game to load.
/// </summary>
public static class SaveFolder
{
    /// <summary>Set right before SaveGame.Load; becomes <see cref="Loaded"/> when the save finishes loading.</summary>
    public static string? Pending;
    public static string? Loaded;

    public static string? Name => Context.IsWorldReady ? Loaded ?? Constants.SaveFolderName : null;

    public static void OnSaveLoaded()
    {
        Loaded = Pending;
        Pending = null;
    }
}
