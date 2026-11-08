using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>
/// Lying in bed gives an AI farmer energy and health back when it plays alone, at the rate the game gives a farmer in bed
/// in multiplayer (Farmer.Update: +1 energy and +1 health every 500 ms while time passes, about +14 per 10 game minutes).
/// The game itself does it only in multiplayer, so alone <c>rest</c> had nothing to wait for and a tired AI ended its day early.
/// A person's farmer resting for a /do order (Manual mode) rests at <see cref="FastRest"/> times the speed: the clock and the
/// energy both run that much faster, so the same game time passes for the same energy, without the wait.
/// </summary>
internal static class BedRest
{
    private const int StepMs = 500;
    /// <summary>How much faster time and energy run while a person's farmer rests for a /do order.</summary>
    internal const int FastRest = 10;
    private static int _timer = StepMs;

    /// <summary>Called on every update tick of every screen; acts only on a single-player farmer in bed.</summary>
    public static void Tick(ModConfig config, PlayerSlot seat)
    {
        bool forOrder = seat.Role == SeatRole.Human && seat.Runtime.CurrentTool == "rest";
        bool resting = seat.Role == SeatRole.Ai || forOrder;
        if (!config.BedRestAlone || Game1.IsMultiplayer || !Context.IsWorldReady || !resting) return;
        var p = Game1.player;
        // Same conditions as the game's: in bed and the clock running (no menu, no pause, the world not frozen).
        if (!p.isInBed.Value || Game1.activeClickableMenu != null || !Game1.shouldTimePass()) return;
        int elapsed = Game1.currentGameTime.ElapsedGameTime.Milliseconds;
        int speed = forOrder ? FastRest : 1;
        // The clock: the game adds the elapsed time once itself; the rest of the speed-up is added here.
        if (speed > 1) Game1.gameTimeInterval += elapsed * (speed - 1);
        _timer -= elapsed * speed;
        while (_timer < 0)
        {
            _timer += StepMs;
            if (p.Stamina < p.MaxStamina) p.Stamina = System.Math.Min(p.MaxStamina, p.Stamina + 1);
            if (p.health < p.maxHealth) p.health++;
        }
    }
}
