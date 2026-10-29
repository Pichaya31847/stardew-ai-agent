using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP.Agent;

/// <summary>
/// What close_menu does for the player before it closes a menu that would otherwise lose something:
/// a letter's quest is accepted (its attached items are handed over by the letter's own exit), and a one-shot
/// grab menu (fishing treasure, gift, fish pond output) gives up what fits in the inventory. Chests are left alone.
/// </summary>
public static partial class AgentTools
{
    private static readonly HashSet<string> ClearMenusBeforeAction = new()
    {
        "walk_to", "travel_to", "talk_to", "interact", "use_tool", "fish", "plant", "harvest",
        "water_all", "harvest_all", "clear_area", "till_area", "place", "craft", "ship", "eat", "rest", "accept_quest"
    };

    internal static IEnumerator WithMenuLifecycle(string name, bool instant, IEnumerator action, CallContext c)
    {
        if (!instant && ClearMenusBeforeAction.Contains(name))
        {
            yield return PrepareForTravel(c);
            if (!c.Ok) yield break;
        }
        yield return action;
    }

    /// <summary>Finish ordinary messages through native UI, retaining the full text in the tool result.
    /// Questions, shops, chests and cutscenes remain open for an explicit decision/use.</summary>
    private static IEnumerator FinishMessages(CallContext c)
    {
        var notes = new List<string>();
        string? previous = null;
        for (int step = 0; step < 160; step++)
        {
            if (Game1.eventUp || Game1.farmEvent != null || Game1.newDay || Game1.fadeToBlack) break;
            if (Game1.activeClickableMenu is LetterViewerMenu letter)
            {
                var text = letter.mailMessage == null ? "" : string.Join("\n", letter.mailMessage);
                notes.Add("Letter read: " + text);
                var closed = new CallContext();
                yield return CloseMenu(default, closed);
                notes.Add(closed.Result);
                if (ReferenceEquals(Game1.activeClickableMenu, letter)) break;
                continue;
            }
            if (Game1.activeClickableMenu is not DialogueBox box) break;
            var line = box.getCurrentString() ?? "";
            // A box with no text has nothing to read and clicks may never advance it (day 56: AI2 sat in one in Pierre's shop
            // while AI1 waited in bed): close it.
            if (line.Trim().Length == 0 && !box.isQuestion && !box.transitioning)
            {
                box.closeDialogue();
                Game1.dialogueUp = false;
                yield return Ticks(4);
                continue;
            }
            if (line != previous && line.Length > 0) notes.Add("Dialogue: " + line);
            previous = line;
            if (box.isQuestion)
            {
                notes.Add(WorldText.MenuState().Trim());
                break; // purchases, proposals and story choices must be read and answered explicitly
            }
            if (!box.transitioning) box.receiveLeftClick(0, 0);
            yield return Ticks(4);
        }
        if (Game1.activeClickableMenu is DialogueBox { isQuestion: false })
            notes.Add("Dialogue still open; use respond to finish it before moving.");
        c.Result = string.Join("\n", notes);
    }

    private static IEnumerator PrepareForTravel(CallContext c)
    {
        // A ready check that can't be cancelled any more (leaving a festival together) only waits on the other farmers: wait with it
        // instead of failing every call at once (36 failed calls in one game minute at the Flower Dance, day 24).
        for (int i = 0; i < 60 * 120 && Game1.activeClickableMenu is ReadyCheckDialog rc && !rc.isCancelable(); i++)
            yield return null;
        var messages = new CallContext();
        yield return FinishMessages(messages);
        if (Game1.activeClickableMenu is ShopMenu or ItemGrabMenu or GameMenu or QuestLog or Billboard)
            yield return CloseMenu(default, new CallContext());
        if (Game1.activeClickableMenu != null)
            c.Fail(Reasons.MenuOpen, messages.Result + "\nResolve the open choice/menu before travelling. " + WorldText.MenuState().Trim());
        else if (messages.Result.Length > 0)
            Rt.Log.Add("system", text: "Finished messages before travel:\n" + messages.Result);
    }

    /// <summary>Claims the menu's contents; whatever it did is added to <paramref name="notes"/> for close_menu's result.</summary>
    private static IEnumerator ClaimBeforeClose(IClickableMenu? menu, List<string> notes)
    {
        if (menu is LetterViewerMenu letter)
            AcceptLetterQuest(letter, notes);
        else if (menu is ItemGrabMenu { shippingBin: false, showReceivingMenu: true } grab && grab.source != ItemGrabMenu.source_chest)
            yield return ClaimGrabMenu(notes);
    }

    /// <summary>
    /// The letter's own AcceptQuest, as its "Accept quest" button does. A letter without a quest should do nothing;
    /// if the game throws anyway, the letter still closes and the AI is told.
    /// </summary>
    private static void AcceptLetterQuest(LetterViewerMenu letter, List<string> notes)
    {
        int quests = P.questLog.Count;
        try { letter.AcceptQuest(); }
        catch (Exception ex)
        {
            notes.Add($"Could not accept a quest from the letter ({ex.GetType().Name}).");
            return;
        }
        if (P.questLog.Count > quests)
            notes.Add($"Accepted the quest: {P.questLog[P.questLog.Count - 1].GetName()}.");
    }

    /// <summary>
    /// Takes every item that fits by clicking its slot, the way <c>menu take</c> does, so the game runs the
    /// menu's own grab handler and removes the item from the source. Items that don't fit are named, not lost silently.
    /// </summary>
    private static IEnumerator ClaimGrabMenu(List<string> notes)
    {
        var before = Snapshot();
        var skipped = new List<Item>();
        for (int guard = 0; guard < 80; guard++)
        {
            if (Game1.activeClickableMenu is not ItemGrabMenu now) break;
            var items = now.ItemsToGrabMenu.actualInventory;
            int slot = -1;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && IndexOf(skipped, items[i]) < 0 && P.couldInventoryAcceptThisItem(items[i])) { slot = i; break; }
            if (slot < 0) break;

            var item = items[slot];
            var cc = now.ItemsToGrabMenu.inventory.FirstOrDefault(s => s.name == slot.ToString());
            if (cc == null) { skipped.Add(item); continue; }
            Click(now, cc);
            yield return null;
            // A click that took nothing must not be repeated forever.
            if (Game1.activeClickableMenu is ItemGrabMenu after && IndexOf(after.ItemsToGrabMenu.actualInventory, item) >= 0)
                skipped.Add(item);
        }

        var got = Diff(before);
        if (got.Length > 0) notes.Add($"Took what the menu held. {got}.");
        if (Game1.activeClickableMenu is ItemGrabMenu rest)
        {
            var left = rest.ItemsToGrabMenu.actualInventory.Where(i => i != null).ToList();
            if (left.Count > 0) notes.Add($"Left behind (no room in your inventory): {Names(left)}.");
        }
    }
}
