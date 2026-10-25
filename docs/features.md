# What it can do

[ภาษาไทย](features.th.md) · **English** · [← README](../README.en.md)

Everything an AI farmer can do with this mod, area by area. The tool names link the features to the
[tool reference](tools.md). The mod does the mechanics (pathing, aiming, button presses, the fishing bar); the AI
makes every choice.

> Many of the newer features are written but not yet played through in game. [coverage.md](coverage.md) shows the
> status of each one (✅ works · 🟡 reachable but untested or partial · ❌ missing).

## 🌱 Farming

- Clear, till, plant and fertilize, tile by tile or a whole rectangle at once (`clear_area`, `till_area`, `plant`)
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
- Separate wallets per farmer, and paying another farmer at any distance (`pay`)

## 🐔 Animals and buildings

- Buy animals from Marnie, pet them, collect eggs, milk and wool, fill the hay troughs (`buy_animal`, `animals`)
- Order a Coop, Barn, Silo, Well or Shed from Robin and upgrade the house (`build`, `upgrade_house`)
- Name pets, animals, the horse and children (`name`), ride the horse (`mount`)

## 💬 Villagers, romance and festivals

- Know everyone: where they are, hearts, birthdays, the gift tastes found out so far (`villagers`)
- Find a villager anywhere in the valley, talk and give gifts (`talk_to`)
- Date with a bouquet, propose with the Mermaid Pendant, marry (`bouquet`, `mermaid_pendant`)
- Heart events with their questions answered by the AI (opt-in: `PlayHeartEvents`)
- Festival calendar, shop hours, joining and leaving festivals (`calendar`, `festival`)
- The Egg Festival's egg hunt: the farmers see the eggs and race each other for them (`festival(eggs)`)

## 📜 Quests and the Community Center

- Quest journal, Help Wanted board, Special Orders (`quests`, `accept_quest`, `special_orders`)
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
