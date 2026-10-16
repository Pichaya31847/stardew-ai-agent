using System.Collections;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace StardewMCP.Agent;

/// <summary>
/// Money rules for several farmers on one farm. Wallets are the game's own: with
/// <c>team.useSeparateWallets</c> every <c>Farmer.Money</c>, shop purchase and overnight shipping payout
/// is per farmer, so the existing tools already use each seat's own money.
/// </summary>
public static class EconomyTools
{
    /// <summary>Host, after the save loaded: warn when split-screen is on but all farmers share one pot of money.</summary>
    public static void CheckWallets(IMonitor monitor, ModConfig config)
    {
        if (!config.SplitScreen || !Context.IsMainPlayer || Game1.player.team.useSeparateWallets.Value)
            return;
        const string text = "Split-screen is on but this save has a shared wallet: every farmer spends the same money, so the AIs " +
                            "can't earn, owe or steal from each other. Use \"Separate wallets\" when creating the co-op farm, or on an " +
                            "existing one: host reads the ledger book in the Mayor's manor, picks separate wallets and sleeps (takes effect overnight).";
        monitor.Log(text, LogLevel.Warn);
        Players.Host.Runtime.Log.Add("system", text: text);
    }

    /// <summary>Debug op: switch the loaded smoke save to separate wallets right now (the game's own ledger code, minus the overnight wait).</summary>
    internal static IEnumerator DebugSeparateWallets(JsonElement a, CallContext c)
    {
        if (!Context.IsMainPlayer) { c.Fail("Run separate_wallets on p1 (the host)"); yield break; }
        ManorHouse.SeparateWallets();
        c.Result = Game1.player.team.useSeparateWallets.Value ? "Wallets are separate" : "Wallets are still shared";
    }
}
