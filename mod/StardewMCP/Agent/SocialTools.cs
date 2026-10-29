using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StardewMCP.Agent;

/// <summary>
/// Social tools: say, emote and give. Kept apart from <see cref="AgentTools"/>. What is said reaches only the players
/// whose screen shows the speaker; it lands in their inbox and rides on that seat's next tool result (see
/// <see cref="TakeMail"/>). An @mention or a gift also raises a <see cref="Perception"/> event, which can stop the
/// target's running walk, wait or farm action.
/// </summary>
public static class SocialTools
{
    private const int MaxChat = 240, MaxInbox = 30, MaxMailLines = 20;

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Inbox = new();
    // Items handed over by another seat; added to the receiver's inventory on the receiver's own tick.
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<(Item item, string from)>> Gifts = new();
    // Said lines waiting for and showing their turn: a bubble above the speaker (the game only draws them for NPCs, so a
    // draw patch shows them) and a line in the chat box of the speaker's and the listeners' screens (each has its own).
    private static readonly SpeechQueue<Spoken> Speech = new();
    private sealed record Spoken(long Speaker, string Text, List<string> Lines, HashSet<string> Screens)
    {
        public readonly ConcurrentDictionary<string, ChatMessage> Shown = new();
    }
    private const int BubbleChars = 20, BubbleLines = 3, BubbleLineHeight = 60; // a line covers the tail of the one above it

    public static void Apply(IManifest manifest)
    {
        new Harmony(manifest.UniqueID + ".social").Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.draw), new[] { typeof(SpriteBatch) }),
            postfix: new HarmonyMethod(typeof(SocialTools), nameof(DrawBubble_Postfix)));
    }

    private static void DrawBubble_Postfix(Farmer __instance, SpriteBatch b)
    {
        // a postfix runs even when Farmer.draw drew nothing (hidden in a cutscene or on the bus, viewing another map)
        if (__instance.hidden.Value || __instance.currentLocation != Game1.currentLocation ||
            (__instance.IsLocalPlayer && __instance.viewingLocation.Value != null)) return;
        float lift = DrawName(__instance, b);
        if (__instance.currentLocation is not { } loc || Speech.Showing(loc.NameOrUniqueName) is not { } line
            || line.Speaker != __instance.UniqueMultiplayerID) return;
        var widest = line.Lines.MaxBy(l => SpriteText.getWidthOfString(l))!; // every line's box gets this width
        // at its real place in the world, the same on every screen, cut at the edge like the game's own bubbles: kept
        // on screen it was pushed over other farmers and stood where the speaker wasn't (2026-10-19)
        float x = __instance.StandingPixel.X, bottom = __instance.StandingPixel.Y - 192 - lift;
        for (int i = 0; i < line.Lines.Count; i++)
        {
            var at = Game1.GlobalToLocal(new Vector2(x, bottom - (line.Lines.Count - 1 - i) * BubbleLineHeight));
            SpriteText.drawStringWithScrollCenteredAt(b, line.Lines[i], (int)at.X, (int)at.Y, widest, 1f, null, 1, 0.99f);
        }
    }

    /// <summary>
    /// The AI farmer's name above its head, so viewers can tell who is who; drawn the way the game's own
    /// <c>Farmer.DrawUsername</c> does (white small font, black outline), which the game never shows in split-screen.
    /// Returns how far a bubble must rise to clear it (0 when no name is drawn). Drawn under the bubbles (0.99): where a
    /// bubble still meets a name (a neighbour's), the words win.
    /// </summary>
    private static float DrawName(Farmer f, SpriteBatch b)
    {
        if (!Players.Slots.Any(s => s.Role == SeatRole.Ai && NameOf(s) == f.Name)) return 0;
        var size = Game1.smallFont.MeasureString(f.Name);
        var at = f.getLocalPosition(Game1.viewport) + new Vector2(32f, -104f) - size / 2f;
        for (int i = -1; i <= 1; i++)
        for (int j = -1; j <= 1; j++)
            if (i != 0 || j != 0)
                b.DrawString(Game1.smallFont, f.Name, at + new Vector2(i, j) * 2f, Color.Black, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.98f);
        b.DrawString(Game1.smallFont, f.Name, at, Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9801f);
        return size.Y + 24; // the scroll's lower edge and tail hang below its text
    }

    // All of these are marked Instant: talking costs no game time, so they run even while the world is
    // frozen and never count as "acting" in WorldClock. They log their own entries.
    public static List<ToolDef> All() => new()
    {
        new()
        {
            Name = "say",
            Description = $"Say something out loud, IN {AgentTools.Config.SpeechLanguageName().ToUpperInvariant()}: it appears above your head and in the chat box. Only players who can see you " +
                          "hear it (the same place, close enough to be in their view, the way look lists players in view). To talk to " +
                          "someone, go and find them first. Keep it short. Write @name to call a player who can hear you: it " +
                          "stops what they are doing (walking, waiting, farming) so they read it at once.",
            Schema = AgentTools.Obj(new { text = AgentTools.Str($"What you say, in {AgentTools.Config.SpeechLanguageName()}") }, "text"),
            Instant = true, AppendStatus = false, Run = Say
        },
        new()
        {
            Name = "emote",
            Description = $"Show an emote bubble above your head. Names: {string.Join(", ", EmoteNames())}.",
            Schema = AgentTools.Obj(new { name = AgentTools.Str("Emote name") }, "name"),
            Instant = true, AppendStatus = false, Run = Emote
        },
        new()
        {
            Name = "give",
            Description = "Hand an item from your inventory to another player standing within 4 tiles of you (same map). Tools can't be given.",
            Schema = AgentTools.Obj(new { to = AgentTools.Str("Their name"), item = AgentTools.Str("Item name"), count = AgentTools.Int("How many (default 1)") }, "to", "item"),
            Instant = true, AppendStatus = false, Run = Give
        }
    };

    private static IEnumerable<string> EmoteNames() => Farmer.EMOTES.Where(e => !e.hidden).Select(e => e.emoteString);

    // ------------------------------------------------------------------ seats

    internal static string Me => AgentTools.Rt.Log.Player;

    internal static string? NameOf(PlayerSlot s) => (s.State as ScreenState)?.Name;

    /// <summary>"Gemini": how players refer to each other.</summary>
    internal static string Label(PlayerSlot s) => NameOf(s) ?? "?";

    internal static IEnumerable<PlayerSlot> OtherAis() =>
        Players.Slots.Where(s => s.Role == SeatRole.Ai && s.Id != Me && s.Active && NameOf(s) != null);

    /// <summary>The AI seat named by <paramref name="to"/> (id or name); otherwise fails <paramref name="c"/> with the valid choices.</summary>
    internal static PlayerSlot? Target(string to, CallContext c)
    {
        to = to.Trim();
        var seat = OtherAis().FirstOrDefault(s => s.Id.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(NameOf(s), to, StringComparison.OrdinalIgnoreCase));
        if (seat == null)
            c.Fail($"No player \"{to}\". Players you can address: {string.Join(", ", OtherAis().Select(Label))}.");
        return seat;
    }

    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ------------------------------------------------------------------ inbox

    internal static void Deliver(PlayerSlot to, string line)
    {
        var q = Inbox.GetOrAdd(to.Id, _ => new());
        q.Enqueue(line);
        while (q.Count > MaxInbox) q.TryDequeue(out _);
    }

    /// <summary>Put a line in another seat's inbox (it rides on that seat's next tool result), e.g. a marriage proposal (#53).</summary>
    internal static void Notify(string player, string line)
    {
        var slot = Players.ById(player);
        if (slot != null) Deliver(slot, line);
    }

    /// <summary>Messages for this seat since its last tool result, as a compact block ("" when none).</summary>
    public static string TakeMail(string player)
    {
        if (!Inbox.TryGetValue(player, out var q) || q.IsEmpty) return "";
        var lines = new List<string>();
        while (q.TryDequeue(out var l)) lines.Add(l);
        var shown = lines.TakeLast(MaxMailLines).ToList();
        var sb = new StringBuilder("Messages:");
        if (shown.Count < lines.Count) sb.Append($"\n  ({lines.Count - shown.Count} older not shown)");
        foreach (var l in shown) sb.Append("\n  ").Append(l);
        return sb.ToString();
    }

    /// <summary>Per-tick work on the seat's own screen: show what it heard in its chat box, add items other players gave it.</summary>
    public static void Tick(string player)
    {
        if (!Context.IsWorldReady) return;
        Speech.Advance(Environment.TickCount64, WorldClock.WorldPaused);
        foreach (var line in Speech.AllShowing().Where(l => l.Screens.Contains(player)))
        {
            if (!line.Shown.ContainsKey(player) && ShowChat(line.Speaker, line.Text) is { } msg) line.Shown[player] = msg;
            if (line.Shown.TryGetValue(player, out var shown))
                shown.timeLeftToDisplay = Math.Max(shown.timeLeftToDisplay, 60); // up while its bubble shows, then fades
        }
        if (!Gifts.TryGetValue(player, out var q) || q.IsEmpty) return;
        var me = Game1.player;
        while (q.TryDequeue(out var g))
        {
            var label = $"{g.item.Stack} x {g.item.DisplayName}";
            if (me.addItemToInventoryBool(g.item))
                Deliver(Players.ById(player)!, $"{g.from} gave you {label}.");
            else
            {
                Game1.createItemDebris(g.item, me.getStandingPosition(), me.FacingDirection);
                Deliver(Players.ById(player)!, $"{g.from} gave you {label}, but your inventory was full: it fell at your feet.");
            }
            Perception.Notify(Players.ById(player)!, "gift", $"{g.from} gave you {label}");
        }
    }

    /// <summary>
    /// A notice from the game to everybody (for example the Joja membership, #26): the in-game chat box and every other AI's inbox.
    /// Unlike <see cref="Say"/> it shows no bubble and is not logged here; the caller logs the event itself.
    /// </summary>
    public static void Announce(string text)
    {
        text = Clip(Clean(text), MaxChat);
        var me = Game1.player;
        Game1.Multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, text, Multiplayer.AllPlayers);
        Game1.chatBox?.receiveChatMessage(me.UniqueMultiplayerID, 0, LocalizedContentManager.CurrentLanguageCode, text);
        foreach (var s in OtherAis()) Deliver(s, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {text}");
    }

    // ------------------------------------------------------------------ tools

    private static IEnumerator Say(JsonElement a, CallContext c)
    {
        if (Game1.newDay || Game1.fadeToBlack || Game1.isWarping || Game1.farmEvent != null || Game1.player.passedOut || Game1.game1.isLocalMultiplayerNewDayActive ||
            Game1.activeClickableMenu is ReadyCheckDialog { checkName: "sleep" } ||
            AgentTools.Rt.CurrentTool == "go_to_bed")
        {
            c.Fail("Sleep/warp transition is in progress. Do not speak or respond to player chat now; wait until the new day or warp finishes. Existing messages remain in your inbox.");
            yield break;
        }
        var text = Clip(Clean(AgentTools.S(a, "text")), MaxChat);
        if (text.Length == 0) { c.Fail("text is empty"); yield break; }
        if (LanguageGuard.Reject(text, AgentTools.Config.SpeechLanguageName(), "line") is { } drift) { c.Fail(Reasons.InvalidArgs, drift); yield break; }
        var me = Game1.player;

        // Heard only on screens that show the speaker: in the video a line appears on the speaker's and the listeners' screens.
        var heard = OtherAis().Where(s => s.State is ScreenState st && st.Sees(me)).ToList();
        int maxMs = Math.Max(1, AgentTools.Config.ChatSeconds) * 1000;
        var screens = heard.Select(s => s.Id).Append(Me).ToHashSet();
        Speech.Add(me.currentLocation.NameOrUniqueName, new Spoken(me.UniqueMultiplayerID, text, BubbleText.Wrap(text, BubbleChars, BubbleLines), screens),
            Math.Clamp(1500 + text.Length * 60, Math.Min(2500, maxMs), maxMs));

        var from = Label(Players.ById(Me)!);
        foreach (var s in heard) Deliver(s, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {from}: {text}");
        Perception.OnSay(Players.ById(Me)!, text, heard);
        AgentTools.Rt.Log.Add("say", text: text, args: new { heard = heard.Select(s => s.Id).ToList() });
        c.Result = heard.Count == 0
            ? $"You said: \"{text}\", but nobody was close enough to hear it. Find the others first."
            : $"You said: \"{text}\". Heard by {string.Join(", ", heard.Select(Label))}.";
    }

    /// <summary>A chat line in this screen's own chat box; <see cref="Tick"/> keeps it up while its bubble shows.</summary>
    private static ChatMessage? ShowChat(long speaker, string text)
    {
        Game1.chatBox?.receiveChatMessage(speaker, 0, LocalizedContentManager.CurrentLanguageCode, text);
        int ticks = Math.Max(1, AgentTools.Config.ChatSeconds) * 60;
        var line = Game1.chatBox?.messages.LastOrDefault();
        if (line != null) line.timeLeftToDisplay = Math.Min(line.timeLeftToDisplay, ticks);
        return line;
    }

    private static IEnumerator Emote(JsonElement a, CallContext c)
    {
        var name = AgentTools.S(a, "name").Trim().ToLowerInvariant();
        var match = Farmer.EMOTES.FirstOrDefault(e => !e.hidden && e.emoteString == name);
        if (match == null) { c.Fail($"Unknown emote \"{name}\". Names: {string.Join(", ", EmoteNames())}."); yield break; }
        Game1.player.netDoEmote(match.emoteString);
        AgentTools.Rt.Log.Add("emote", text: name);
        c.Result = $"You show the \"{name}\" emote.";
        yield break;
    }

    private static IEnumerator Give(JsonElement a, CallContext c)
    {
        var to = Target(AgentTools.S(a, "to"), c);
        if (to == null) yield break;
        var me = Game1.player;
        int count = Math.Max(1, AgentTools.I(a, "count", 1));

        int slot = AgentTools.FindItem(AgentTools.S(a, "item"), it => it is not Tool);
        if (slot < 0) { c.Fail($"You have no giftable item called \"{AgentTools.S(a, "item")}\" (tools can't be given)."); yield break; }
        var held = me.Items[slot];
        var same = me.Items.Where(i => i != null && i.QualifiedItemId == held.QualifiedItemId && i.Quality == held.Quality).ToList();
        int have = same.Sum(i => i.Stack);
        if (have < count) { c.Fail($"You only have {have} x {held.DisplayName}."); yield break; }

        // The other seat's farmer is a synced copy: read where it is, never write to it.
        var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == NameOf(to));
        if (other?.currentLocation == null) { c.Fail($"{Label(to)} is not in the world right now."); yield break; }
        float dist = Vector2.Distance(other.Tile, me.Tile);
        if (other.currentLocation.NameOrUniqueName != Game1.currentLocation.NameOrUniqueName || dist > 4.5f)
        {
            var where = other.currentLocation.NameOrUniqueName == Game1.currentLocation.NameOrUniqueName
                ? $"{dist:0} tiles away" : $"on {other.currentLocation.NameOrUniqueName}";
            c.Fail($"{Label(to)} is too far to hand an item ({where}, at ({other.TilePoint.X},{other.TilePoint.Y})). Walk next to them first (within 4 tiles).");
            yield break;
        }

        var gift = held.getOne();
        gift.Stack = count;
        if (!other.couldInventoryAcceptThisItem(gift)) { c.Fail($"{Label(to)}'s inventory is full."); yield break; }

        for (int left = count; left > 0;)
        {
            var s = same.First(i => i.Stack > 0);
            int take = Math.Min(left, s.Stack);
            s.Stack -= take;
            left -= take;
            if (s.Stack <= 0) me.removeItemFromInventory(s);
        }
        Gifts.GetOrAdd(to.Id, _ => new()).Enqueue((gift, Label(Players.ById(Me)!)));
        AgentTools.Rt.Log.Add("give", text: $"{count} x {gift.DisplayName}", args: new { to = to.Id });
        c.Result = $"You gave {count} x {gift.DisplayName} to {Label(to)}.";
    }
}
