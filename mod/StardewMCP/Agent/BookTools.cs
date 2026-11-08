using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewMCP.Agent;

/// <summary>
/// Books are read as soon as an AI farmer has one: skill books give their experience, power books their lasting perk, the
/// same as using the book from the toolbar (Object.performUseAction reads it, then one is used up). The two catalogues are
/// kept: they are reference books, not read once.
/// </summary>
internal static class BookTools
{
    private static readonly string[] Keep = { "Book_PriceCatalogue", "Book_AnimalCatalogue" };

    public static void Tick(PlayerSlot seat, uint ticks)
    {
        if (ticks % 60 != 0 || seat.Role != SeatRole.Ai || !Context.IsWorldReady || seat.Runtime.ActionRunning) return;
        var p = Game1.player;
        if (Game1.activeClickableMenu != null || Game1.eventUp || !p.CanMove || p.UsingTool || Game1.isWarping || Game1.fadeToBlack) return;
        int slot = p.Items.ToList().FindIndex(i => i is SObject o && (o.Category == SObject.skillBooksCategory || o.Category == SObject.booksCategory)
                                                    && !Keep.Contains(o.ItemId));
        if (slot < 0) return;
        var book = (SObject)p.Items[slot];
        int before = p.CurrentToolIndex;
        try
        {
            p.CurrentToolIndex = slot;
            if (book.performUseAction(p.currentLocation))
            {
                p.reduceActiveItemByOne();
                seat.Runtime.Log.Add("system", text: $"Read the book {book.DisplayName} as soon as it came into the pack.");
            }
        }
        catch (Exception ex)
        {
            seat.Runtime.Log.Add("system", text: $"Couldn't read {book.DisplayName}: {ex.Message}");
        }
        finally
        {
            if (before >= 0 && before < p.Items.Count) p.CurrentToolIndex = before;
        }
    }
}
