# Stardew MCP

A [SMAPI](https://smapi.io) mod that lets AI play Stardew Valley. It runs an
[MCP](https://modelcontextprotocol.io) server **inside the game**, so an AI can look around, walk, farm, fish, talk to
villagers, shop and go to bed by itself.

> ภาษาไทยอยู่ด้านล่าง ↓

There are three ways to use it:

| You want to... | Use | You need |
|---|---|---|
| Watch AIs play your farm, or play alongside them | The mod's **built-in AI**: set it up in the config menu, no other programs | An API key (Anthropic, OpenAI, Gemini) or a local model (Ollama, LM Studio) |
| Give orders to your own AI assistant ("water the crops, then visit Abigail") | Any **MCP client** (Claude Code, Claude Desktop, Cursor...) connected to `http://localhost:8770/mcp` | That client |
| Build your own bot, stream overlay or automation | The **HTTP API** (`/api/state`, `/api/control`, `/overlay`, `/replay`...) | Any language that speaks HTTP |

Also included:

- **Up to three AIs on one farm** in split-screen co-op, with or without you. They talk (`say`), hand each other
  items, notice what the others are doing and can catch each other lying.
- **Time can freeze while an AI thinks**, so a slow model loses no game time. You can turn this off so the world keeps running.
- **Overlay and replay pages** served by the mod: the AI's thoughts and actions, live or day by day. You can add the overlay to OBS as a browser source.
- **Logs and diaries** for every day the AI plays.

## Support the project

If you enjoy this project, you can support development through
[GitHub Sponsors](https://github.com/sponsors/duckxy166).

## Requirements

- Stardew Valley 1.6+ (PC)
- [SMAPI](https://smapi.io) 4.0+
- Optional: [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098), to change settings in the game instead of editing `config.json`
- Either an API key from an AI provider / a local model server, or an MCP client

## Install

1. Install SMAPI and start the game through it once.
2. Download the latest `StardewMCP` zip from the [Releases](https://github.com/duckxy166/stardew-ai-agent/releases) page.
3. Unzip it into `Stardew Valley/Mods/`, so you get `Mods/StardewMCP/`.
4. Start the game through SMAPI. The SMAPI console should show `Agent MCP at http://localhost:8770/mcp`.

The first start creates `Mods/StardewMCP/config.json`. All the settings below live there; with Generic Mod Config
Menu installed you can also change them in the game (title screen → the cog → *Stardew MCP*).

## Quick start 1: let the AI play by itself

### One AI, one farmer

1. Open the mod's settings (in the game, or in `config.json`).
2. Give seat **P1** a provider, a model and your API key:

   | Provider (`Brain`) | Models | Key / endpoint |
   |---|---|---|
   | `anthropic` | `claude-haiku-5-5` (default), `claude-sonnet-5-5`, ... | [console.anthropic.com](https://console.anthropic.com) |
   | `openai` | `gpt-5-mini` (default), `gpt-5`, ... | [platform.openai.com](https://platform.openai.com), or any OpenAI-compatible server with a `BaseUrl`: OpenRouter (`https://openrouter.ai/api/v1`), Ollama (`http://localhost:11434/v1`, no key), LM Studio |
   | `gemini` | `gemini-3.8-flash` (default), ... | [aistudio.google.com](https://aistudio.google.com) |
   | `none` | | no built-in AI: an MCP client drives the seat (see *Quick start 2*) |

3. Load any save. The AI starts playing, and a small panel on its screen shows its thoughts, chat and current action.

```json
"Seats": [
  { "Role": "ai", "Brain": "anthropic", "ApiKey": "sk-ant-..." }
]
```

### Several AIs, or you plus AIs (split-screen)

Turn on `SplitScreen`. Up to four screens then share the farm in the game's 2×2 grid. **P1** is the save's own
farmer and **P2–P4** are farmhands. Each seat has a `Role`:

| Role | Meaning |
|---|---|
| `ai` | An AI plays this farmer: the built-in AI (`Brain` + `ApiKey`), or an MCP client on `/mcp/pN` |
| `human` | You, with keyboard and mouse (P1 only) |
| `camera` | A parked farmer whose screen just watches, e.g. for a video |
| `off` | No screen |

1. Start a **new co-op farm**, or load any save. If the farm has too few cabins, the mod builds them (`AutoBuildCabins`)
   and joins the farmhands by itself, so you don't need gamepads.
2. Choose the **time mode** with `FreezeTimeWhileThinking`:
   - **on**: the whole world (clock, farmers, villagers) stops while any AI is between actions, so slow models cost no game time.
   - **off**: the clock keeps running and the AIs keep up as well as they can. This is the natural choice when you play P1 yourself.
3. Load the save. The first time the game starts its co-op server, Windows Firewall may ask for permission once; allow it.

Example: "I play P1, two AIs play with me, and the world keeps running":

```json
"SplitScreen": true,
"FreezeTimeWhileThinking": false,
"Seats": [
  { "Role": "human" },
  { "Role": "ai", "Brain": "anthropic", "ApiKey": "sk-ant-...", "Name": "Claude" },
  { "Role": "ai", "Brain": "gemini", "ApiKey": "AIza...", "Name": "Gemini" },
  { "Role": "off" }
]
```

In the SMAPI console, `mcp_brain` shows the status of the built-in AIs, and `mcp_brain stop` / `mcp_brain start` pauses and resumes them.

### Good to know

- **Cost.** Every action is a model call that includes the game state. One in-game day costs roughly 50–300k input
  tokens per AI, depending on the model and how it plays, so cheap models (Haiku, Flash, GPT mini, a local model) are
  the sensible choice. `DailyTokenBudget` stops a seat for the rest of the day once it has used that many tokens
  (input + output; 0 = no limit).
- **Your keys** are stored as plain text in `config.json`. Don't share that file.
- **Language.** The AIs think and chat in the game's language; `ThoughtLanguage` (e.g. `"English"`) overrides it.
  If the game's font can't draw a script, its characters show as `·` on the HUD unless a font mod adds them.
- **Local models** work through `openai` + `BaseUrl`, but small models struggle with 70 tools, so expect some confusion.
- **How the AI plays** is set by its system prompt, `Mods/StardewMCP/assets/system_prompt.md`. Edit it to change their behaviour.
- **Performance.** Three or four screens are heavy: a 2560×1440 window at 60 FPS needs a mid-range GPU. Lower the resolution if the game stutters.

## Quick start 2: connect your own AI client

With the game running and a save loaded, add the mod as an MCP server. For [Claude Code](https://claude.com/claude-code):

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

Then ask something like *"Water my crops, then go talk to Abigail."*

Any client that speaks MCP over streamable HTTP can use the same URL. In split-screen, each farmer has its own
endpoint: `/mcp/p1` (same as `/mcp`), `/mcp/p2`, `/mcp/p3`. Give the seats you drive this way `"Brain": "none"`.

## Pause and stop

| Want | Do | What happens |
|---|---|---|
| Pause | Press `F8` in the game window (`PauseKey`), or `POST /api/control {"action": "pause"}` | The running action finishes, then the whole world freezes and new AI calls wait. `F8` again (or `resume`) continues |
| Stop after today | `POST /api/control {"action": "stop_after_day"}` | Sets `stopAfterDay` in `/api/state`, so the script driving the AI stops once the day ends in bed. `clear_stop` undoes it |
| Stop now | `POST /api/control {"action": "stop_now"}` | The running action finishes, new calls are refused and the game stays frozen until `resume` |

The SMAPI console command `mcp_freeze [on|off]` turns time freezing on or off while the game runs.

## Troubleshooting

| Problem | Try |
|---|---|
| The SMAPI console doesn't show `Agent MCP at ...` | Check that the folder is `Mods/StardewMCP/` (not nested twice) and read the SMAPI log for errors |
| The AI never starts | Read the SMAPI console after loading the save: a seat that can't start logs `pN: built-in AI not started: <problem>` (missing key, unknown provider...). `mcp_brain` lists the AIs that run |
| Port 8770 is already in use | Change `HttpPort` and use the new port in your MCP client's URL |
| The game seems stuck and the clock doesn't move | It is probably frozen while an AI thinks. `/api/state` shows who is thinking. An AI that stays silent longer than `ThinkTimeoutSeconds` stops holding the world |
| The AI repeats the same failing action | The loop guard adds hints and finally blocks the repeat (`RepeatHintAfter`, `RepeatBlockAfter`, `NoProgressAfter`) |
| Farmhands don't join in split-screen | Allow the Windows Firewall prompt, and keep `AutoBuildCabins` on (or build enough cabins yourself) |

To report a bug, see [CONTRIBUTING.md](CONTRIBUTING.md#reporting-a-bug). The SMAPI log link helps a lot.

## Reference

### Tools

What the AI can do. Every tool is available on each farmer's MCP endpoint.

| Tool | What it does |
|---|---|
| `status`, `look`, `inventory` | Read the player, surroundings and items. `look` shows a 17x17 window (radius 8) by default; `kinds` (e.g. `["crops","debris"]`) limits the object list and `diff=true` lists only the tiles that changed since the last look |
| `think` | Post a thought (shown on the overlay, costs no game time) |
| `walk_to`, `travel_to` | Pathfind to a tile, or to another location (a shop owner's or villager's name also works; a closed door says when it opens) |
| `farm_summary` | Whole-map farm report: empty tilled soil, crops by type with days to harvest, unwatered, ready and dead crops, crops that won't mature this season, forage, debris, watering can and nearest water. Costs no game time |
| `water_all`, `harvest_all` | Water every dry crop (refills at the nearest water, stops on low energy) and collect every ready crop and forage item. Both stop with a reason (low energy, menu, late hour, full inventory) and say what is left |
| `clear_area`, `till_area` | Clear a rectangle (up to 100 tiles) with the right tool, and till it with the Hoe; large stumps and boulders are skipped and reported. `plant` also takes `area: [x, y, w, h]` (or no tiles at all = every empty tilled tile on the map) |
| `use_tool`, `plant`, `harvest` | Farm work on a target tile |
| `fight` | Fight the monsters near you: equips the best weapon, walks to the nearest one and swings for a few seconds; stops when all clear, health is at 30% of max (at least 15) or a menu opens; `eat_below` (HP %) eats the best healing food on the way |
| `leave_mine` | Get out of the Mines from any floor: climbs the up ladders, or warps to the Mine entrance when health is low, it is 11 PM or the ladder cannot be reached. In the mines the footer shows HP, and any other long action is cut short with `Interrupted:` when a monster within 3 tiles hurts you |
| `interact`, `respond`, `close_menu` | Objects, dialogue and menus; `interact` can hold an item first (load a machine, use a totem); `close_menu` accepts a letter's quest and takes treasure or gift items that fit |
| `villagers` | Everyone: where they are, hearts, talked/gifts today and this week, birthdays; with a name also the gift tastes found out so far |
| `talk_to` | Find a villager (travels across maps unless `go=false`), talk, optionally give a gift; reports points before/after and their reaction |
| `bouquet`, `mermaid_pendant` | Ask a villager to date you (8 hearts), later propose (10 hearts, upgraded house); `villagers(name)` shows what is still missing |
| `propose`, `answer_proposal` | Propose marriage to another player (split-screen); the other player answers the Yes/No box with `answer_proposal` |
| `name` | Name the new pet, farm animal, horse or child the open naming box asks for (a box nobody answers gets a random name at bedtime) |
| `menu` | Read any open menu, then use it: take/put (chests), choose (mine elevator), amount (send money), type (names), ok/cancel, or click a listed button |
| `craft`, `place`, `chest` | Craft from the recipes you know (`craft()` lists them), put items, machines and chests on the ground, store or take items in a chest or the fridge in one call (by name and quality, several items, "everything") |
| `cook` | Cook the dishes you know in the farmhouse kitchen from your pack and the fridge (`cook()` lists them) |
| `quests`, `accept_quest` | Quest journal with story, giver, progress and reward; today's Help Wanted request and who took it; Special Orders; `accept_quest` takes the Help Wanted request |
| `crack_geode`, `trash`, `watch_tv` | Clint cracks geodes (25g each); throw items away (valuables need `confirm`); watch a TV channel (weather, fortune, Queen of Sauce, tips) |
| `shop_list`, `buy`, `sell`, `ship`, `eat` | Shopping, selling to an open shop for gold at once, shipping bin (pays overnight), food |
| `equip` | Put on a ring, boots or hat from the inventory (the old one goes back), or attach bait/tackle to the fishing rod; `status` and `inventory` show what is worn and what is on the rod |
| `special_orders` | The Special Orders board in Town: `list` the posted orders, `accept` one by index, `active` shows the accepted ones with progress |
| `build`, `upgrade_house` | Robin the carpenter: `build(building=list)` shows costs, `build(building, x, y)` orders a Coop, Barn, Silo, Well or Shed on the Farm; `upgrade_house` buys the next house level. Paid from the caller's own money and items |
| `buy_animal`, `animals` | Marnie's animals: `buy_animal(type=list)` and `buy_animal(type, building?, name?)`; `animals` lists yours and does `pet`, `collect` (eggs, milk, wool) and `feed` (hay troughs). Status warns when animals are not pet today |
| `calendar` | Today and the next days: festivals, birthdays, the next festival, and the main shops' hours and closed days (no game time) |
| `festival` | On a festival day: `start` talks to Lewis and answers yes (waits for the other players), `leave` walks out; `talk_to` also reaches festival villagers and booths |
| `fish` | Walk to the best shore for the rod, cast `tries` times, play the BobberBar minigame and pick up treasure. Costs energy and game time per cast; one result line per cast. Needs a fishing rod (Willy, at the Beach) |
| `crops`, `item_info` | Game knowledge read from the game's own data, no game time: `crops` is a season planner (growth and regrow days, last day to plant, harvests that fit, seed and sell prices, gold per tile per day, marks the seeds you carry); `item_info` gives an object's sell prices by quality, what it restores, shops that sell it and recipes that use it |
| `wait`, `go_to_bed` | Let time pass, end the day (`go_to_bed` refuses until today's `journal` is written) |
| `journal` | The AI's memory across days, costs no game time: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections (replaced section by section) and optional facts. Written to `diary_NNN.md`, `lessons.md`, `plan.md`, `notes.md` in the log folder; `day` catches up a missed day |
| `say`, `emote`, `give` | Multi-player (split-screen): chat heard by the players who can see you, show an emote, hand an item to a nearby player. All cost no game time |
| `wear`, `wardrobe` | Put on a hat, shirt or pants from the inventory; see what you wear and carry (rings and boots are `equip`) |
| `search_trash` | Dig through a trash can; a watching villager costs friendship and a `trash_dive` event with `seen_by` is logged |
| `mount`, `dismount` | Ride the horse (needs a Stable); also night events show up as an `Overnight:` line after `go_to_bed` |
| `bundles` | Community Center bundles room by room: what each needs, what is donated (and by whom), the reward, and which of your items fit. Costs no game time |
| `donate_bundle` | Donate an inventory item (or gold, for the Vault) to the first Community Center bundle that needs it, through the game's own bundle page |
| `joja` | The Joja route at the JojaMart: `projects` lists the five Community Development projects with price and state, `membership` buys the permanent 5,000g membership only with `confirm=true` (without it: a warning of what every player loses and gains), `buy_project` pays one project through the game's Joja form |
| `museum` | `list` the items the Museum still accepts, `donate` one (or all) to the next free display spot, collect `rewards` from Gunther |

### Tool results

Every tool reply keeps its plain text and adds a machine-readable part (MCP `structuredContent`). `isError` is `true` only when the tool failed.

```json
{ "content": [{ "type": "text", "text": "[partial · low_energy] Stopped: energy too low (2). ..." }],
  "isError": false,
  "structuredContent": { "ok": true, "reason": "low_energy", "partial": true, "data": { "done": 12, "remaining": 28, "skipped": 0 } } }
```

- The text starts with a short tag when the tool did only part of its work (`[partial · reason]`) or failed for a known reason (`[no_path]`). Plain successes have no tag.
- `reason` is one of: `ok`, `partial`, `low_energy`, `no_path`, `not_here`, `no_item`, `inventory_full`, `menu_open`, `too_late`, `shop_closed`, `wrong_season`, `needs_upgrade`, `interrupted`, `timeout`, plus `no_money`, `invalid_args`, `not_found`, `not_ready`, `error` and `other` (a failure that has no specific code yet). `too_late` and `wrong_season` are reserved: the game gives no reliable signal, so no tool sets them yet.
- `use_tool`, `plant` and `harvest` are batch tools: they always report `data` (`done`, `remaining` = tiles not reached, `skipped` = tiles tried and failed) and set `partial` when `remaining` or `skipped` is above 0. `reason` then says why the batch stopped (or, failing that, why tiles were skipped).
- The common failures (`walk_to`, `travel_to`, `use_tool`, `plant`, `harvest`, `interact`, `talk_to`, `buy`, `ship`, `eat`, `go_to_bed`, `shop_list`, `think`) have codes; other tools still return text only (`reason` is `other` on failure).
- Logs: result lines of `day_NNN.jsonl` carry `reason` and `partial` (the text there stays untagged). Next to each day's log the mod writes `day_NNN_summary.json` with the failure reasons per tool and per player. The overlay marks partial results `◐` and shows the reason, the replay shows a reason badge.

### HTTP API

All on `http://localhost:8770` (`HttpPort`).

| URL | Use |
|---|---|
| `/mcp`, `/mcp/p1` … `/mcp/p3` | MCP endpoints, one per farmer |
| `/overlay` | Live thoughts and actions; use it as an OBS browser source (`?player=p2` for one seat, `&mock=1` for sample text without the game) |
| `/replay` | Day-by-day replay of the logs |
| `/api/state` | Game state as JSON: every seat with its current action and thinking time, plus `saveFolder`, `paused`, `stopAfterDay`, `stopNow`. After a day that ended without `go_to_bed` (2:00 AM, health 0), `passOut` says why and `pendingJournalDay` is the day whose journal is still owed until `journal(day=N)` is written |
| `/api/control` | Run control: `POST {"action": "pause" \| "resume" \| "stop_after_day" \| "stop_now" \| "clear_stop"}`; `GET` reads the flags |
| `/api/memory` | Each AI seat's lessons, plan and notes (what `journal` wrote) |
| `/api/relationships` | Every AI farmer's friendships (hearts, birthdays, gift tastes found out) |
| `POST /api/release_freeze` | Stop an idle seat from freezing the world (`{"seat": "p1"}`, or `"all"`; `"on": false` takes it back) so game time runs until the farmer passes out. Ends with the farm day |
| `/api/briefing?seat=p1` | Morning briefing of one AI seat: overnight results, shipping by item, mail, weather, farm |
| `/api/stats` | Daily stats: the days that have a `stats_NNN.json`; `?n=3` returns day 3 of every AI seat |
| `POST /api/debug/*` | Test hooks (warp, set_time, add_item...), only with `EnableDebugApi` |

During a pause, a call that waits longer than 9 minutes is answered with an error (MCP clients time out at 10 minutes),
so the AI simply calls again and no stale call runs after `resume`. A script that watches for hangs should check
`paused` in `/api/state` before it treats a clock that isn't moving as a hang.

If an unknown menu stays open for more than 10 seconds while `go_to_bed` waits for the new day (the shipping screen,
level-ups, the save screen and the ready check are expected), the mod logs its type and closes it instead of letting
the call run into its 5 minute timeout.

### Configuration

`Mods/StardewMCP/config.json`:

| Setting | Default | Meaning |
|---|---|---|
| `Seats` | P1–P3 `ai` (no `Brain`), P4 `camera` | Who plays each screen: `Role`, and for an AI `Brain`, `Model`, `ApiKey`, `BaseUrl`, `Name` (see *Quick start 1*) |
| `ShowHud` | `true` | Show each AI's thoughts, chat and current action on its own screen |
| `MaxOutputTokens` | `4096` | Built-in AI: longest answer the model may write per turn |
| `DailyTokenBudget` | `0` | Built-in AI: tokens per seat per day before it stops until the next morning (0 = no limit) |
| `HttpPort` | `8770` | Port for MCP, overlay, replay and the API |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `ThoughtLanguage` | `""` | Language of the AI's on-screen text (`think`, action reasons, diary), e.g. `"English"`; empty = the game's language. The overlay and replay follow it (see below) |
| `FarmBatchMinEnergy` | `10` | `water_all`, `harvest_all`, `clear_area`, `till_area` and `plant` area mode stop when energy drops below this (0 = only stop at 0) |
| `FishingAssist` | `"perfect"` | How `fish` plays the minigame: `"perfect"` keeps the fish inside the bar; `"natural"` presses the button like a player, so it can lose fish |
| `DefaultProfession` | `0` | Profession picked at bedtime when the AI hasn't chosen one: 0 = left option, 1 = right |
| `PlayHeartEvents` | `false` | Let heart events (story cutscenes) play and stop at their questions for `respond`, instead of skipping them. Experimental, see [docs/coverage.md](docs/coverage.md) |
| `RevealGiftTastes` | `false` | `villagers(name)` shows the game's real gift tastes. Off: a farmer only knows the tastes it found out by gifting |
| `RepeatHintAfter` | `3` | Loop guard: add a "try something different" hint when the identical call has failed this many times in a row (0 = off) |
| `RepeatBlockAfter` | `5` | Loop guard: refuse the Nth identical call in a row without running it when the N-1 before it failed (0 = off) |
| `NoProgressAfter` | `25` | Loop guard: add a "pick an action" hint after this many calls in a row with no game time, inventory or position change (0 = off) |
| `PauseKey` | `"F8"` | Key (an XNA `Keys` name) that toggles pause / resume while the game window has focus; empty = off |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks for automated tests; leave off for normal play |
| `SplitScreen` | `false` | Up to four screens share the farm in split-screen co-op |
| `AutoBuildCabins` | `true` | Split-screen: build the cabins the farmhands need when the farm has too few (changes the save) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Split-screen: farmhand names for P2, P3, P4 (a seat's `Name` wins); an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | Split-screen: an AI silent for longer stops holding the world frozen (0 = never) |
| `BedtimeWarning` | `2200` | Split-screen: from this time an AI not in bed gets one warning that the others wait for it (0 = off) |
| `LockHostInput` | `true` | Split-screen: ignore the real keyboard/mouse on P1 once a save is loaded, when P1 is an AI |
| `CleanFrames` | `true` | Split-screen: frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Split-screen: zoom each screen to about 20 tiles across |
| `ChatSeconds` | `5` | Longest time an AI's `say` stays up, as the bubble above its head and its line in the chat box |
| `SessionBreakTimes` | `[1200,1800]` | For long-running scripts: from these game times a tool result carries `Session break:`, asking the AI to continue in a fresh session so its context stays small |
| `SessionBreakToolCalls` | `150` | Session break after this many tool calls in one session (0 = off) |
| `SessionBreakResultChars` | `120000` | Session break after this many characters of tool results in one session, about 30k tokens (0 = off) |
| `SessionBreaksForAll` | `false` | Session breaks for every MCP client; by default only clients that connect to `/mcp?breaks=1` |

### How split-screen AIs see each other

`look` and `status` list the other players in view with what each is *actually* doing (running tool, its reason,
game minutes so far) next to what it last said. An @name or @pN in `say`, a gift, a player coming into view or
entering your house, a theft from your chest or plot, rain starting, or crows on the farm are added to the next tool
result as `Events:` and stop a running `wait`, `walk_to`, `travel_to` or farm action, so a model never sleeps through them.

### Language of the overlay and replay

`ThoughtLanguage` sets one language for everything viewers read. The tools ask the AI to write in it, and the overlay
and replay pick their labels from it (`?lang=English` on either page overrides it). `"Thai"` and `"English"` have full
UI tables; any other language (e.g. `"Japanese"`) gets thoughts in that language with an English UI. To add a table,
copy the `en` block in the `i18n` section of `mod/StardewMCP/assets/overlay.html` and `replay.html`.

## Building on top of the mod

Scripts that play many days unattended (one AI session per day, memory between days, save backups, a crash watchdog,
OBS recording) are not part of this repository. Everything such a script needs is the HTTP API above: drive the
farmers through `/mcp/pN`, read `/api/state`, `/api/briefing`, `/api/stats`, `/api/memory` and `/api/relationships`,
control the run with `/api/control` and `/api/release_freeze`, and test with `/api/debug/*`. The guides in
[docs/](docs/) describe one recording setup as an example.

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your game folder, copies
the mod into `Mods/` and creates a release zip in `bin/`.

Tests that don't need the game (the mod build itself does, because ModBuildConfig references the game's DLLs):

```bash
dotnet test mod/StardewMCP.Tests                                  # C#: pathfinding, tool schemas, MCP requests
pip install pytest && python -m pytest -q mod/tests                 # Python: tool schema lint, overlay i18n, fishing and crop math
```

See [CONTRIBUTING.md](CONTRIBUTING.md#tests) for what they cover.

## Contributing

Pull requests and issues are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

Originally forked from [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp),
which first bridged Stardew Valley to MCP through a Go server and a WebSocket mod. This project has
since been rewritten as a standalone mod with its own in-game MCP server, and the original Go server
and WebSocket code have been removed.

## License

[MIT](LICENSE). Stardew Valley is a trademark of ConcernedApe; this project is a fan-made mod and
is not affiliated with ConcernedApe. No game files are included.

---

## ภาษาไทย

Mod สำหรับ Stardew Valley ที่ให้ AI เล่นเกมเองได้ ผ่าน MCP server ที่รันอยู่ในเกม ใช้ได้ 3 แบบ:

- **ให้ AI ในตัว mod เล่นเอง**: ใส่ API key ของตัวเอง (Anthropic, OpenAI, Gemini) หรือใช้โมเดลในเครื่อง (Ollama, LM Studio) ไม่ต้องมีโปรแกรมอื่น
- **สั่ง AI ของคุณเอง** ผ่าน MCP client เช่น Claude Code ที่ `http://localhost:8770/mcp`
- **เขียนบอท/overlay/สคริปต์ของตัวเอง** ผ่าน HTTP API (`/api/state`, `/api/control`, `/overlay`, `/replay`)

นอกจากนี้: AI สูงสุด 3 ตัวเล่นฟาร์มเดียวกันในจอแบ่ง (จะเล่นด้วยหรือไม่ก็ได้) คุยกัน ให้ของกัน และเห็นว่าอีกคนทำอะไรอยู่จริง ๆ
เวลาในเกมหยุดเดินระหว่างที่ AI คิดได้ (ปิดได้) มีหน้า overlay และ replay และเก็บ log กับไดอารี่ทุกวัน

**ติดตั้ง:** ลง SMAPI → แตกไฟล์ zip จากหน้า [Releases](https://github.com/duckxy166/stardew-ai-agent/releases) ไว้ใน
`Stardew Valley/Mods/` → เปิดเกมผ่าน SMAPI แล้วดูว่าคอนโซลขึ้น `Agent MCP at http://localhost:8770/mcp`

**ให้ AI เล่นเอง:** เปิดตั้งค่า *Stardew MCP* (ลง [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098)
แล้วกดฟันเฟืองที่หน้าไตเติล หรือแก้ `Mods/StardewMCP/config.json`) ใส่ผู้ให้บริการ (`Brain`), โมเดล และ API key ให้ที่นั่ง P1
แล้วโหลดเซฟ AI จะเริ่มเล่นเอง อยากให้หลายตัวเล่นด้วยกันก็เปิด `SplitScreen` แล้วเลือกว่าแต่ละที่นั่ง P1–P4 ใครเล่น
(`human` = คุณเอง, `ai`, `camera`, `off`) และเลือกโหมดเวลา *หยุดโลกตอน AI คิด* (เปิด = AI ช้าก็ไม่เสียเวลาในเกม,
ปิด = เวลาเดินต่อ เหมาะตอนคุณเล่นด้วย)

**ค่าใช้จ่าย:** คิดตามโทเคนของ key คุณเอง ประมาณวันละ 50–300k input tokens ต่อ AI แนะนำโมเดลราคาถูก เช่น Haiku, Flash,
GPT mini ตั้ง `DailyTokenBudget` เพื่อจำกัดได้ และ key ถูกเก็บเป็นข้อความธรรมดาใน `config.json` อย่าแชร์ไฟล์นี้

**ต่อกับ Claude Code:** เปิดเกม โหลดเซฟ แล้วรัน
`claude mcp add --transport http stardew http://localhost:8770/mcp`

**หยุดชั่วคราว:** กด `F8` ในเกม (กดอีกครั้งเพื่อเล่นต่อ)

**มีปัญหา:** ดูหัวข้อ *Troubleshooting* ด้านบน หรือเปิด issue พร้อมลิงก์ SMAPI log (ดู [CONTRIBUTING.md](CONTRIBUTING.md))

**สนับสนุนผู้พัฒนา:** ดูหัวข้อ *Support the project* ด้านบน

**เครดิต:** เริ่มต้นจากการ fork [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)
แล้วเขียนใหม่ทั้งหมดเป็น mod ที่มี MCP server ในตัว

**สัญญาอนุญาต:** MIT — ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
