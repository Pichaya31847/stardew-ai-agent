using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public static List<ToolDef> All() => new()
    {
        new()
        {
            Name = "pay",
            Description = "Send money from your wallet to another player, at any distance (a loan, a debt, a payment for work). They are told right away. Money is not recoverable unless they pay it back.",
            Schema = AgentTools.Obj(new { to = AgentTools.Str("Their name"), amount = AgentTools.Int("Gold to send"), reason = AgentTools.Str("What the payment is for; the receiver sees it") }, "to", "amount"),
            Instant = true, AppendStatus = false, Run = Pay
        }
    };

    private static IEnumerator Pay(JsonElement a, CallContext c)
    {
        var me = Game1.player;
        if (!me.team.useSeparateWallets.Value) { c.Fail("Wallets are shared on this farm: everyone spends the same money, so there is nothing to pay."); yield break; }
        var to = SocialTools.Target(AgentTools.S(a, "to"), c);
        if (to == null) yield break;
        int amount = AgentTools.I(a, "amount", 0);
        if (amount < 1) { c.Fail("amount must be at least 1."); yield break; }
        if (amount > me.Money) { c.Fail($"You only have {me.Money}g."); yield break; }
        var other = Game1.getOnlineFarmers().FirstOrDefault(f => f.Name == SocialTools.NameOf(to));
        if (other == null) { c.Fail($"{SocialTools.Label(to)} is not in the world right now."); yield break; }

        var reason = AgentTools.S(a, "reason").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (reason.Length > 120) reason = reason[..120];
        int meBefore = me.Money, otherBefore = me.team.GetIndividualMoney(other);
        // The same three steps as the ledger book in the Mayor's manor.
        me.Money -= amount;
        me.team.AddIndividualMoney(other, amount);
        me.stats.onMoneyGifted((uint)amount);
        int otherAfter = me.team.GetIndividualMoney(other);

        var from = SocialTools.Label(Players.ById(SocialTools.Me)!);
        SocialTools.Deliver(to, $"[{WorldText.FormatTime(Game1.timeOfDay)}] {from} paid you {amount}g{(reason.Length > 0 ? $" ({reason})" : "")}. You now have {otherAfter}g.");
        AgentTools.Rt.Log.Add("pay", text: reason, args: new
        {
            to = to.Id, amount,
            from = new { before = meBefore, after = me.Money },
            receiver = new { before = otherBefore, after = otherAfter }
        });
        c.Result = $"You paid {amount}g to {SocialTools.Label(to)}{(reason.Length > 0 ? $" ({reason})" : "")}. You now have {me.Money}g.";
    }

    /// <summary>
    /// Host, after the save loaded: with SeparateWallets on, ask for separate wallets the way the ledger book in the Mayor's
    /// manor does (changeWalletTypeTonight; the game splits the money evenly overnight in ManorHouse.SeparateWallets).
    /// Otherwise warn when split-screen is on but all farmers share one pot of money.
    /// </summary>
    public static void CheckWallets(IMonitor monitor, ModConfig config)
    {
        if (!config.SplitScreen || !Context.IsMainPlayer || Game1.player.team.useSeparateWallets.Value)
            return;
        if (config.SeparateWallets)
        {
            if (!Game1.player.changeWalletTypeTonight.Value)
                Game1.player.changeWalletTypeTonight.Value = true;
            const string asked = "SeparateWallets is on: the wallets become separate tonight, as if the host had picked it in the Mayor's ledger. " +
                                 "The money is split evenly between the farmers when the day ends.";
            monitor.Log(asked, LogLevel.Info);
            Players.Host.Runtime.Log.Add("system", text: asked);
            return;
        }
        const string text = "Split-screen is on but this save has a shared wallet: every farmer spends the same money, so the AIs " +
                            "can't earn, owe or steal from each other. Use \"Separate wallets\" when creating the co-op farm, or on an " +
                            "existing one: host reads the ledger book in the Mayor's manor, picks separate wallets and sleeps (takes effect overnight), " +
                            "or set SeparateWallets to true in config.json.";
        monitor.Log(text, LogLevel.Warn);
        Players.Host.Runtime.Log.Add("system", text: text);
    }
}
