# What it can do

[ภาษาไทย](features.th.md) · **English** · [← README](../README.en.md)

Everything an AI farmer can do with this mod, area by area. The tool names link the features to the
[tool reference](tools.md). The mod does the mechanics (pathing, aiming, button presses, the fishing bar); the AI
makes every choice.

> Many of the newer features are written but not yet played through in game. [coverage.md](coverage.md) shows the
> status of each one (✅ works · 🟡 reachable but untested or partial · ❌ missing).

## New-game setup

- Optional new-game form prefill supplies name, farm name and favorite thing, and selects Skip Intro. The form stays editable and waits for OK.

## 🌱 Farming

- Clear, till, plant and fertilize, tile by tile or a whole rectangle at once, row by row (`clear_area`, `till_area`, `plant`). Tilling stops at as many tiles as there are seeds; the Scythe clears the crops that died at the season change
- Pick up forage on the maps around the farm (`forage`)
- Water every dry crop on the map, refilling the can at the nearest water (`water_all`), or only your own crops
- Harvest everything that is ripe, forage included (`harvest_all`)
- A whole-farm report: what is dry, ready, dead or won't make it this season (`farm_summary`)
- Plan the season from the game's own data: growth days, last day to plant, gold per tile per day (`crops`)
- Place machines, chests, sprinklers, fences and furniture (`place`), load machines (`interact` holding an item)

## 🎣 Fishing

- Walk to the best shore for the rod, cast, play the BobberBar minigame and pick up treasure (`fish`)
- Play it perfectly or like a human who can lose the fish (`FishingAssist`)
- Attach bait and tackle (`equip`)

## ⛏️ Mines and combat

- Fight nearby monsters with the best weapon, eating when health drops (`fight`)
- Get out of the Mines from any floor, or warp out when it gets dangerous (`leave_mine`)
- Take the elevator, crack geodes at Clint's (`menu`, `crack_geode`)
- Long actions stop by themselves when a monster hurts you

## 🍳 Crafting, cooking and items

- Craft from known recipes, cook in the kitchen with the fridge's ingredients (`craft`, `cook`)
- Store and take items in chests and the fridge in one call (`chest`)
- Rings, boots, hats, shirts and pants (`equip`, `wear`, `wardrobe`)
- Look up any item: sell prices, what it restores, shops, recipes (`item_info`)
- Eat, throw things away (valuables need confirmation), watch TV and learn recipes (`eat`, `trash`, `watch_tv`)

## 🛒 Shops and money

- Read a shop, buy, sell for gold at once, ship for overnight pay (`shop_list`, `buy`, `sell`, `ship`)
- Tool upgrades at the blacksmith, backpack upgrades at Pierre's
- Separate wallets per farmer (or switch a shared-wallet save with `SeparateWallets`), and paying another farmer at any distance (`pay`)
- In the last week of a season the morning briefing says which seeds can no longer ripen and which seeds the next season wants
- `stats_NNN.json` adds a `progress` block: backpack size, Community Center bundles and next step, session breaks, rests, quests

## 🐔 Animals and buildings

- Buy animals from Marnie, pet them, collect eggs, milk and wool, fill the hay troughs (`buy_animal`, `animals`)
- Order a Coop, Barn, Silo, Well or Shed from Robin and upgrade the house (`build`, `upgrade_house`)
- Name pets, animals, the horse and children (`name`), ride the horse (`mount`)

## 💬 Villagers, romance and festivals

- Know everyone: where they are, hearts, birthdays, the gift tastes found out so far (`villagers`)
- Find a villager anywhere in the valley, talk and give gifts (`talk_to`)
- Date with a bouquet, propose with the Mermaid Pendant, marry (`bouquet`, `mermaid_pendant`)
- Heart events with their questions answered by the AI (opt-in: `PlayHeartEvents`)
- Festival calendar, shop hours, joining and leaving festivals (`calendar`, `festival`). In co-op the game lets the farmers in and out only together: an AI waits a minute for the others, tells them, and otherwise gives up and does something else instead of standing stuck in the waiting box
- The Egg Festival's egg hunt: the farmers see the eggs and race each other for them (`festival(eggs)`)

## 📜 Quests and the Community Center

Scythe actions verify live targets and their changes rather than treating every animation as success. Removed/depleted grass, empty ground and buildings are skipped; clearing stops after repeated ineffective cuts. A no-progress `use_tool` batch reports failure so the AI must refresh its observations.

Ordinary NPC dialogue and letters finish automatically, with their text retained in tool results and letter attachments/quests claimed through native UI. Explicit choices still require the AI to answer. Completed shop/chest/menu interactions are cleaned up before the next common movement/work action. The board handler no longer skips a Help Wanted tile adjacent to the calendar. Each built-in brain reads the actual journal/board report in its morning briefing, then decides whether to accept an achievable request.

Inventory management prioritizes storing items not needed for the current task in a chest. Backpack upgrades are separate investments: budget other planned work and a cash buffer first, and upgrade only from surplus funds. A full pack or merely covering the purchase price is not a purchase trigger; budgeting remains a model decision rather than an enforced fixed reserve.

Daily logs use lossless gzip compression, preserving every entry while reducing disk space. The live replay and summaries read compressed and legacy logs; existing logs migrate on their next write after byte verification.

`ship` and `sell` protect seeds and fertilizer and keep a minimum inventory reserve of wood (50), stone (25), fiber (20), clay/hardwood/coal/ores (10 each), and bars (5 each). Requests exceeding the safe surplus fail before selling and report how much is safe. Intentional sales can use `allow_reserved=true` with an explanation; it is not a reservation system for every quest or bundle item. The shopping guidance checks planting capacity and routes other needs to Robin, Clint, Marnie, Willy or Gus.

Small local models get their own settings: the AI can think, write its diary and speak in Thai while its reasons and memory notes are in English (`SpeechLanguage`, `WorkLanguage`), a compact tool list (`BrainTools: "compact"`) and fewer memory lines in the prompt (`BrainMemoryLines`). The system prompt makes the money loop (harvest, ship, buy seeds, plant, water) the backbone of every day, and an early `go_to_bed` names the farm work still waiting.

With two AIs and the bottom operator dashboard, only AI1 and AI2 join the game. The dashboard is a separate UI region without a farmer or cabin, and does not participate in sleeping or gameplay.

Overnight farm events and pass-out/fade phases finish before brains begin the next day. Thinking pauses cannot freeze these transitions, speech/events cannot cancel committed sleep, and the dashboard stays drawn while validated SMAPI updates are suspended. Repeated early-sleep calls no longer bypass the healthy/energetic before-17:00 guard. With two inventory slots or fewer, tool results give actionable chest/storage and backpack-upgrade guidance; the model still chooses and performs the purchase/crafting actions.

Built-in brains track daily social progress across conversation resets. Mornings request both NPC conversation and nearby player speech; one reminder a day follows when goals remain, and a fresh session after a break gets a one-line note of what is still open. Unheard speech and failed NPC calls do not complete them. Before 21:00, outside the mine and with energy above 20, an early bedtime request gets one social reminder; a second sleep request is allowed. The model chooses its words, destination and person, and these reminders do not guarantee compliance.

- Quest journal, Help Wanted board, Special Orders (`quests`, `accept_quest`, `special_orders`); each quest says how to finish it, deliveries are handed over with `talk_to` and their money is claimed straight away
- Every morning the built-in AI opens the gift boxes, reads the mail and claims quest money before it starts thinking (`morning_chores`); it can also take the Help Wanted request by itself (`BrainAcceptBoardQuest`)
- The Community Center story step by step: Lewis's scene (enter Town from the Bus Stop, no rain, spring 5+, 8:00-13:00; `quests` says when that works TODAY), the Junimo note, the Wizard, then the bundles
- Shops open at the counter by themselves once inside, `sell` names the shops that buy an item, and the backpack upgrade is one call (`upgrade_backpack`)
- Completed journal quests and Special Orders expose unclaimed money; `quests(action="claim_reward")` clicks the native journal reward button. The daily guidance covers objectives, delivery, reward collection, crafting shortages and reserving bundle items. These choices still depend on the model.
- Community Center bundles and donations, gold for the Vault (`bundles`, `donate_bundle`)
- The Joja route, with a warning before the membership that changes everyone's game (`joja`)
- Museum donations and Gunther's rewards (`museum`)

## 🛏️ Daily life

- Wait, rest in bed to recover, go to bed (`wait`, `rest`, `go_to_bed`)
- Mail and letters, with quests and attachments claimed (`interact`, `close_menu`)
- Profession choices at skill levels 5 and 10 (`choose_profession`)
- Night events (fairy, witch, meteorite...) reported the next morning
- Passing out at 2:00 AM or at 0 health is recorded, and the missed diary is caught up the next day

## 👥 Several AIs on one farm

- Up to three AIs (plus a camera seat) in split-screen co-op, joined without gamepads, cabins built when missing
- You can play P1 yourself next to them
- Chat that only reaches farmers whose screen shows the speaker; emotes; handing over items (`say`, `emote`, `give`)
- Each farmer sees what the others are *actually* doing next to what they last said, so lies can be caught
- Interrupts: an @mention, a gift, someone arriving or entering your house, a theft, rain or crows stop a long action
- Ownership: who planted a crop, who placed a chest, and a `theft` record when someone takes what isn't theirs
- Marriage between players (`propose`, `answer_proposal`), a bedtime warning when the others wait for you

## 🧠 Memory and character

- A diary for the viewers, lessons, a plan by season, week and day, and facts (`journal`)
- A `self` note each AI rewrites every evening, and one line on each of the other farmers
- A morning briefing: overnight results, shipping, mail, weather, the farm (`/api/briefing`)

## 🎥 For viewers and video

- An in-game panel per AI screen: name, model, current action or thinking time, last thought and chat line
- `/overlay` for OBS (live thoughts and actions, one seat or all) and `/replay` (day by day)
- JSONL logs, diaries, daily stats and failure summaries for every day
- Frozen frames that look live, speech bubbles timed to the running world, Thai and English UI

## ⏱️ Time and control

- The world freezes while any AI thinks, or keeps running (`FreezeTimeWhileThinking`)
- A thinking timeout so a stuck AI never holds the world forever
- Pause with `F8`, stop after today or right now (`/api/control`)
- A loop guard that hints and finally blocks an AI repeating the same failing call
- A daily token budget per seat, session breaks for long runs

## 🔌 Ways to drive it

- The built-in AI: Anthropic, OpenAI, Gemini, or any OpenAI-compatible server (OpenRouter, Ollama, LM Studio)
- Any MCP client over streamable HTTP: Claude Code, Claude Desktop, Cursor...
- The [HTTP API](api.md) for your own bots, overlays and scripts

## 🚧 Not yet

- Tailoring, the dye pots and the forge
- Crab pots
- The arcade, darts, the crane game and the casino; the Fair and ice-fishing games at festivals
- Upgrading a built coop or barn, house renovations
- Dancing and having a baby with another player

Want one of these? See [coverage.md → Keeping it complete](coverage.md#keeping-it-complete) and
[CONTRIBUTING.md](../CONTRIBUTING.md).

## Local operator dashboard
With OperatorDashboard enabled and four split-screen seats, a camera seat is replaced visually by a panel with all AI statuses, latest thoughts and speech, recent logs, and an instruction box. F9 focuses the box, Enter queues an instruction for the selected AI or all, and Escape releases the keyboard. Instructions apply before the next model request. The three farm views omit the old thought panels.

The local operator dashboard uses a warm timber frame, cream paper cards, individual farmer colors, a field-notes feed, and green recipient/send controls.

The operator panel clips every draw call, including text, to its own camera quadrant and restores the game's rendering state afterwards.

A farmer standing on the bed after waking up no longer ends the built-in AI's day when the model returns no tools. A queued operator instruction resumes an idle same-day brain, while preserving its token budget.

The replay/log page polls every second, reconnects automatically with request timeouts, preserves historical-day selection and scroll position, and includes speech and the fourth seat. Overlay requests bypass cache, time out, and support player=p4.

Two-AI bottom-dashboard layout: AI1 top left, AI2 top right, full-width wood dashboard below with side-by-side farmer cards and a log column. The command TextBox disables native width truncation so typed text is retained.

Cabin auto-join reuses cabins with an existing offline owner; an activated farmhand is no longer treated as requiring a new cabin on every reload.

Local AI recovery now releases the clock for up to 30 seconds after three tool-free replies, then restarts its context and retries the same day instead of abandoning the day. Operator orders wake that retry early. The local system prompt treats NPC visits and farmer conversations as daily goals without supplying scripted dialogue.
