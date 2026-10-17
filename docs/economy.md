# Shared farm economy (#52)

How several farmers share one farm: wallets, ownership, payments, the end of the day and festivals.
Everything here was checked against the decompiled game (1.6) and, where marked, in the game.

## Wallets

- The game keeps one wallet per farmer when `Game1.player.team.useSeparateWallets` is on. `Farmer.Money`,
  `ShopMenu` purchases and the overnight shipping payout (`Farm.getShippingBin(who)` is the farmer's
  personal bin) are all per farmer, so `status`, `buy`, `shop_list` and `ship` already use each seat's own money.
- It is a host/world choice made when the co-op farm is created ("Separate wallets" on the character screen).
  It can also be changed later by the host in the Mayor's manor (ledger book, takes effect overnight).
  The mod cannot make it per run: **create the filming farm with separate wallets**, with 3 starting cabins.
- At load, with `SplitScreen` on and a shared wallet, the mod logs a warning (SMAPI console and the `system` log).
- Smoke: the debug op `separate_wallets` runs the game's own `ManorHouse.SeparateWallets()` on the smoke copy.
- The game already has a money transfer, but only at the ledger book in the manor. `pay` is the same three
  steps (payer `Money`, `team.AddIndividualMoney`, `stats.onMoneyGifted`) at any distance.

## Ownership and theft

- Chests and machines: `Object.owner` is set by the game when a farmer places them. `look` shows
  `Chest (3 items, owner p2)`, machines show `(owner p2)`, and `/api/state` has an `owned` list.
- Crops: the planter is written to the soil's `modData` when `plant` succeeds (saved with the game). `look`
  shows `Parsnip READY (owner p1)`. Soil that was planted some other way has no owner.
- `theft` log entry (kind `theft`, player = thief, args `{victim, what: crop|chest, item, location, tile}`) when a
  seat harvests a crop planted by another seat (`Crop.harvest`) or takes an item out of a chest another seat
  placed (`Chest.grabItemFromChest`, the callback the chest menu uses). The hooks are Harmony patches, so every
  path counts: `harvest`, `interact`, a scythe swing, and the future chest tool (#3) as long as it goes through the menu.
- Gap for the chest tool (#3): `interact` cannot open a player chest, because the game requires
  `didPlayerJustRightClick`. The debug op `chest_take(x, y)` takes items through the chest's own callback so
  the theft hook can be tested; a real chest tool needs the same path.
- Not recorded: opening someone's chest without taking anything, putting items into it, the starting plot as a
  fixed tile area. A plot is "whatever the seat planted"; a fixed area per seat would be a config of rectangles.

## Payments

`pay(to, amount, reason)`: needs separate wallets, any distance, the receiver sees it on its next tool result,
the viewers see a `pay` entry with both wallets before/after. Loans stay on the plan board.

## End of the day

Findings (game code):

1. In multiplayer, going to bed opens a `ReadyCheckDialog("sleep")`; the new day starts when every farmer
   **required by that check** is ready. By default that is every online farmer, including P4 (camera).
2. A farmer who is still up at 2:00 AM (or at -15 energy) passes out by himself (`player.startToPassOut`) and
   counts as ready; that costs up to a tenth of the money (max 1,000g).
3. Time in split-screen only stops when **every** farmer's screen asks for a pause (menu open, dialog, bed dialog).
   While one farmer plays, the others wait in their bed dialog and the clock keeps running.
4. `WorldClock` additionally freezes the world while a connected AI is between actions. That is bounded by
   `ThinkTimeoutSeconds` (300 s), after which the seat stops counting until it acts again.

So a lagging AI cannot block forever: it passes out at 2:00 AM, and the sleepers wait at most the in-game time
from their bedtime to 2:00 AM (about 40 s of real time per game hour while the world runs).

The real hole was P4: nobody puts the camera farmer to bed, so **every** day could only end at 2:00 AM.

Implemented: `Bedtime.Tick` (host, once a second) narrows the "sleep" ready check to the AI seats with
`Game1.netReady.SetLocalRequiredFarmers` (the movie theater uses the same call). The host-driven new-day
sync still carries P4 along, as with the game's dedicated host. And from `BedtimeWarning` (22:00) an AI that is
not in bed is told once per day that the others are waiting.

5. Found while testing: after `go_to_bed` passes the day change, the seat is still "between actions" for
   `WorldClock`, so the world froze until the 300 s think timeout and `sleep_together` took 321 s. Fixed with
   `AgentRuntime.WakingUp` (the world is not frozen while any seat is waking up); it now takes about 21 s.
   `go_to_bed` also closes any open menu first (a shop left open after a failed buy blocked the sleep).

Design decisions left open (not done):

- Excluding a **timed-out** AI (crashed CLI) from the check too, so the day ends when the healthy ones are in
  bed. Today it passes out at 2:00 AM instead; that is the game's own penalty and good story, but costs the
  sleepers up to a few real minutes.
- A hard stop earlier than 2:00 AM (for example forcing bed at 24:00).
- `go_to_bed` on P2/P3 walks to the host's `FarmHouse` and sleeps in the host's bed (it routes to the map
  named FarmHouse, not the player's cabin). It works for the ready check, but #22/#41 may want the cabin bed.

## Festivals with several farmers (investigation only, #11)

- Walking into the festival map opens a `ReadyCheckDialog("festivalStart")`: **all online farmers** must try
  to enter, including the camera farmer, or every AI is stuck in the dialog. Same for leaving
  (`festivalEnd`). Fix: narrow those two checks to the AI seats as `Bedtime` does for "sleep", only on festival
  days (`Game1.whereIsTodaysFest != null`). Untested, so not included.
- Each screen runs its own festival `Event`; time stops in the festival only while every farmer is in a menu or
  event. NPCs are `CurrentEvent.actors` (not `Loc.characters`), so `talk_to`/`look` do not see them yet (#11).
- Lewis' "start the main event" (`answerDialogueQuestion("yes")` -> `forceFestivalContinue`) broadcasts the
  `festivalEvent` message to the other screens only when answered by the **host** (`Game1.IsServer`, no dedicated
  host). Decision: P1 starts the main event; the others join through the plan board/chat.
- Minigames (egg hunt, fishing, grange) are per screen (`Game1.currentMinigame`).
