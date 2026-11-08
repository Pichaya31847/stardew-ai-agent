using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Lying in bed gives an AI farmer energy and health back when it plays alone, at the rate the game gives a farmer in bed
/// in multiplayer (Farmer.Update: +1 energy and +1 health every 500 ms while time passes, about +14 per 10 game minutes).
/// The game itself does it only in multiplayer, so alone <c>rest</c> had nothing to wait for and a tired AI ended its day early.
/// </summary>
internal static class BedRest
{
    private const int StepMs = 500;
    private static int _timer = StepMs;

    /// <summary>Called on every update tick of every screen; acts only on a single-player AI farmer in bed.</summary>
    public static void Tick(ModConfig config, PlayerSlot seat)
    {
        if (!config.BedRestAlone || Game1.IsMultiplayer || !Context.IsWorldReady || seat.Role != SeatRole.Ai) return;
        var p = Game1.player;
        // Same conditions as the game's: in bed and the clock running (no menu, no pause, the world not frozen).
        if (!p.isInBed.Value || Game1.activeClickableMenu != null || !Game1.shouldTimePass()) return;
        _timer -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
        if (_timer >= 0) return;
        _timer = StepMs;
        if (p.Stamina < p.MaxStamina) p.Stamina = System.Math.Min(p.MaxStamina, p.Stamina + 1);
        if (p.health < p.maxHealth) p.health++;
    }
}
