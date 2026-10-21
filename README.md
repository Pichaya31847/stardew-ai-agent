# Stardew MCP

A [SMAPI](https://smapi.io) mod that lets an AI agent play Stardew Valley. The mod runs an
[MCP](https://modelcontextprotocol.io) server **inside the game**, so Claude (or any MCP client)
can look around, walk, farm, talk to villagers, shop and go to bed on its own.

- **Up to three AIs play the farm with you** in split-screen (Claude, GPT, Gemini or a local model, with your own
  API key), or any MCP client drives a farmer from outside.
- **Game time freezes while the AI thinks**, so a slow model loses no in-game time (or let the world run on).
- **Overlay and replay pages** served by the mod (`/overlay`, `/replay`): the AI's thoughts and actions live, usable as an OBS browser source.
- **Replay page** and per-day JSONL logs + diaries for every run.

> ภาษาไทยอยู่ด้านล่าง ↓

## Support the project

If you enjoy this project, you can support development through
[GitHub Sponsors](https://github.com/sponsors/duckxy166).

## Requirements

- Stardew Valley 1.6+
- [SMAPI](https://smapi.io) 4.0+
- An API key for a provider (anthropic, openai or gemini), or an MCP client such as [Claude Code](https://claude.com/claude-code)

## Install

1. Install SMAPI and run the game once through it.
2. Download the latest `StardewMCP` zip from the Releases page.
3. Unzip it into `Stardew Valley/Mods/`.
4. Start the game through SMAPI. The console should show
   `Agent MCP at http://localhost:8770/mcp`.

## Play with the AIs (no OBS, no scripts)

The mod can play its farmers by itself with your own API key. Up to four screens share the farm in
split-screen: **P1** is the save's farmer (you, or an AI), **P2–P4** are farmhands (AIs, a camera, or off).

1. Start a **new co-op farm** (the mod builds the missing cabins by itself; `AutoBuildCabins`), or use any save.
2. Open the mod's settings. With [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) installed:
   title screen → the cog → *Stardew MCP*. Otherwise edit `Mods/StardewMCP/config.json` (created on first start).
3. For each seat choose **who plays it** and, for an AI, the **provider**, **model** and **API key**:

   | Provider | Models | Key / endpoint |
   |---|---|---|
   | `anthropic` | `claude-haiku-5-5` (default), `claude-sonnet-5-5`, ... | [console.anthropic.com](https://console.anthropic.com) |
   | `openai` | `gpt-5-mini` (default), `gpt-5`, ... | [platform.openai.com](https://platform.openai.com); or any OpenAI-compatible server with a `BaseUrl`: OpenRouter (`https://openrouter.ai/api/v1`), Ollama (`http://localhost:11434/v1`, no key), LM Studio |
   | `gemini` | `gemini-3.8-flash` (default), ... | [aistudio.google.com](https://aistudio.google.com) |
   | `none` | | an external MCP client drives the seat (see *Connect an AI*) |

4. Pick the **time mode**: *Freeze the world while an AI thinks* (on: a slow model costs no game time and
   nobody moves while any AI is between actions; off: the clock keeps running and the AIs keep up as well as
   they can). With a human at P1, off is the natural choice.
5. Load the save. Each AI screen shows a small panel with the farmer's thoughts, chat and current action
   (`ShowHud`); the SMAPI console command `mcp_brain` prints their status, `mcp_brain stop` / `start` pauses them.

`config.json` for "I play P1, two AIs play with me, world keeps running":

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

The default `Seats` is three AI seats and a camera; give P1–P3 a `Brain` and `ApiKey` to let the mod play them.
Things to know:

- **Cost.** Every action is a model call with the game state in it. A day costs roughly 50–300k input tokens per
  AI depending on the model and how it plays; cheap models (Haiku, Flash, GPT mini, a local model) are the
  sensible choice. `DailyTokenBudget` stops a seat for the rest of the day once it has used that many tokens
  (input + output; 0 = no limit). Keys are stored as plain text in `config.json`; keep that file to yourself.
- **Language.** The AIs think and chat in the game's language (`ThoughtLanguage` overrides it). Scripts the
  game's font cannot draw show as `·` on the HUD unless a font mod adds them.
- **Local models** work through `openai` + `BaseUrl`, but small models struggle with 70 tools; expect confusion.
- The system prompt is `Mods/StardewMCP/assets/system_prompt.md`; edit it to change how the AIs play.
- Three screens are heavy: a 2560×1440 window at 60 FPS needs a mid-range GPU; lower the resolution if it stutters.

## Connect an AI

With the game running and a save loaded:

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

Then ask Claude something like *"Water my crops, then go talk to Abigail."*

Any client that speaks MCP over streamable HTTP can use the same URL.

### Tools

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
| `wear`, `wardrobe` | Put on a hat, shirt or pants from the inventory; see what you wear and carry (rings and boots are `equip`, #27) |
| `search_trash` | Dig through a trash can; a watching villager costs friendship and a `trash_dive` event with `seen_by` is logged |
| `mount`, `dismount` | Ride the horse (needs a Stable); also night events show up as an `Overnight:` line after `go_to_bed` |
| `bundles` | Community Center bundles room by room: what each needs, what is donated (and by whom), the reward, and which of your items fit. Costs no game time |
| `donate_bundle` | Donate an inventory item (or gold, for the Vault) to the first Community Center bundle that needs it, through the game's own bundle page |
| `joja` | The Joja route at the JojaMart: `projects` lists the five Community Development projects with price and state, `membership` buys the permanent 5,000g membership only with `confirm=true` (without it: a warning of what every player loses and gains), `buy_project` pays one project through the game's Joja form |
| `museum` | `list` the items the Museum still accepts, `donate` one (or all) to the next free display spot, collect `rewards` from Gunther |

### Tool results

Every tool reply keeps its plain text and adds a machine-readable part (MCP `structuredContent`). `isError` is unchanged: it is `true` only when the tool failed.

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

### Other endpoints

| URL | Use |
|---|---|
| `http://localhost:8770/overlay` | OBS browser source (thoughts + actions) |
| `http://localhost:8770/replay` | Day-by-day replay of the logs |
| `http://localhost:8770/api/state` | Raw game state JSON (includes `saveFolder`, `paused`, `stopAfterDay`, `stopNow`). After a day that ended without `go_to_bed` (2:00 AM, health 0), `passOut` says why and `pendingJournalDay` is the day whose journal is still owed until `journal(day=N)` is written |
| `http://localhost:8770/api/control` | Run control: `POST {"action": "pause" \| "resume" \| "stop_after_day" \| "stop_now" \| "clear_stop"}`; `GET` reads the flags |
| `http://localhost:8770/api/memory` | Each AI seat's lessons, plan and notes (what `journal` wrote) |
| `http://localhost:8770/api/relationships` | Every AI farmer's friendships (hearts, birthdays, gift tastes found out), for a runner to turn into a `relationships.md` |
| `POST http://localhost:8770/api/release_freeze` | Runner only: stop an idle seat from freezing the world (`{"seat": "p1"}`, or `"all"`; `"on": false` takes it back) so game time runs until the farmer passes out. Ends with the farm day |
| `http://localhost:8770/api/briefing?seat=p1` | Morning briefing of one AI seat: overnight results, shipping by item, mail, weather, farm (the runner puts it in the first prompt of a day) |
| `http://localhost:8770/api/stats` | Daily stats: the days that have a `stats_NNN.json`; `?n=3` returns day 3 of every AI seat |

## Configuration

`Mods/StardewMCP/config.json` is created on first launch:

| Setting | Default | Meaning |
|---|---|---|
| `HttpPort` | `8770` | Port for MCP, overlay and replay |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `FarmBatchMinEnergy` | `10` | `water_all`, `harvest_all`, `clear_area`, `till_area` and `plant` area mode stop when energy drops below this (0 = only stop at 0) |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks (warp, set_time, add_item...) for automated tests; leave off for normal play |
<<<<<<< HEAD
| `FishingAssist` | `"perfect"` | How the `fish` tool plays the fishing minigame: `"perfect"` keeps the fish inside the bar (the safe fallback); `"natural"` presses the mouse button like a player would, so it can lose fish |
| `ThoughtLanguage` | `"Thai"` | Language the AI is asked to use for on-screen text (`think`, action reasons, journal), e.g. `"English"` |
=======
| `ThoughtLanguage` | `"Thai"` | Language of the AI's on-screen text (`think`, action reasons, diary); the overlay and replay follow it (see below) |
>>>>>>> origin/fix/issue-34
| `SplitScreen` | `false` | Three AIs + a camera screen in split-screen co-op (see below) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Farmhand names for P2, P3, P4; an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | Split-screen: an AI silent for longer stops holding the world frozen (0 = never) |
| `PlayHeartEvents` | `false` | Let heart events (story cutscenes) play and stop at their questions for `respond`, instead of skipping them. Experimental, see `docs/coverage.md` |
| `RevealGiftTastes` | `false` | `villagers(name)` shows the game's real gift tastes. Off: a farmer only knows the tastes they found out by gifting |
| `RepeatHintAfter` | `3` | Loop guard: add a "try something different" hint when the identical call has failed this many times in a row (0 = off) |
| `RepeatBlockAfter` | `5` | Loop guard: refuse the Nth identical call in a row without running it when the N-1 before it failed (0 = off) |
| `NoProgressAfter` | `25` | Loop guard: add a "pick an action" hint after this many calls in a row with no game time, inventory or position change (0 = off) |
| `LockHostInput` | `true` | Split-screen: ignore the real keyboard/mouse on P1 once a save is loaded |
| `CleanFrames` | `true` | Split-screen: frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Split-screen: zoom each screen to about 20 tiles across |
| `SessionBreakTimes` | `[1200,1800]` | Planned session breaks: from these game times a tool result carries `Session break:` and the AI is asked to continue in a new session (see below) |
| `SessionBreakToolCalls` | `150` | Planned break after this many tool calls in one session (0 = off) |
| `SessionBreakResultChars` | `120000` | Planned break after this many characters of tool results in one session, about 30k tokens (0 = off) |
| `SessionBreaksForAll` | `false` | Planned breaks for every MCP client; by default only clients that connect to `/mcp?breaks=1` |
| `PauseKey` | `"F8"` | Key (an XNA `Keys` name) that toggles pause / resume while the game window has focus; empty = off |
| `ChatSeconds` | `5` | Longest time an AI's `say` stays up, as the bubble above its head and its line in the chat box |

The SMAPI console command `mcp_freeze [on|off]` toggles time freezing at runtime.

### Split-screen (three AIs)

With `SplitScreen: true`, load a farm that has **3 free cabins** (a co-op farm with starting cabins).
The mod joins P2–P4 itself, no gamepads needed, in the game's 2×2 grid:

| Seat | Endpoint | Role |
|---|---|---|
| P1 (host) | `/mcp/p1` (or `/mcp`) | AI |
| P2 | `/mcp/p2` | AI |
| P3 | `/mcp/p3` | AI |
| P4 | none | camera / summary screen for the video |

The whole world (clock, farmers, NPCs) is frozen while any connected AI is thinking, so a slow model
never loses game time. `/api/state` lists every seat with its current action and thinking time.
The first time the game starts its local co-op server, Windows Firewall may ask once; allow it.

AI players notice each other. `look` and `status` list the other players in view with what each is *actually*
doing (running tool, its reason, game minutes so far) next to what you last heard it say.
An @name or @pN in `say`, a gift, a player coming into view or entering your house, a theft from
your chest or plot, rain starting, or crows on the farm are added to your next tool result as `Events:` and
stop a running `wait`, `walk_to`, `travel_to` or farm action, so a model never sleeps through them.

`ThoughtLanguage` sets one language for everything viewers read. The MCP tools ask the AI to write in it, the
overlay and replay pick their labels from it (`thoughtLanguage` in `/api/feed`, and in `/api/state` once a save is
loaded; `?lang=English` on either page overrides it). `"Thai"` and `"English"` have full UI tables; any other language
(e.g. `"Japanese"`) gets thoughts in that language with an English UI. To add a table, copy the `en` block in the
`i18n` section of `mod/StardewMCP/assets/overlay.html` and `replay.html`.

## Pause and stop

| Want | Do | What happens |
|---|---|---|
| Pause | `F8` in the game window (`PauseKey`), or `POST /api/control {"action": "pause"}` | The running tool call finishes, then every new AI call waits and the whole world is frozen (`frozen` and `paused` are true in `/api/state`). The overlay shows "หยุดชั่วคราว". `F8` or `resume` continues |
| Stop after today | `POST /api/control {"action": "stop_after_day"}` | Sets `stopAfterDay` in `/api/state` so whatever drives the AI stops once the day has ended with `go_to_bed`; `clear_stop` takes it back |
| Stop now | `POST /api/control {"action": "stop_now"}` | The running tool call finishes, new calls are refused and the game stays frozen until `resume` |

A call that waits longer than 9 minutes during a pause is answered with an error (MCP clients time out
at 10 minutes), so the AI simply calls again and no stale call runs after `resume`.
A watchdog (#24) must read `paused` in `/api/state` before it treats a standing clock as a hang.

If an unknown menu stays open for more than 10 seconds while `go_to_bed` waits for the new day (the shipping screen,
level-ups, the save screen and the ready check are expected), the mod logs its type and closes it with
`exitThisMenu` instead of letting the call run into its 5 minute timeout.

## Unattended runs and recording

The scripts that play many days unattended (one AI session per in-game day, memory between days, save backups
and restore, a crash watchdog), record with OBS and switch its scenes are not part of this repository: they belong
to the YouTube production and live in a separate repository. Everything they use is this mod's HTTP API:
`/api/state`, `/api/control`, `/api/events` (SSE), `/api/briefing`, `/api/stats`, `/api/memory`, `/api/relationships`,
`/api/release_freeze` and, with `EnableDebugApi`, `/api/debug/*`.

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your
game folder, copies the mod into `Mods/` and creates a release zip in `bin/`.

### Tests

Tests that don't need the game (the mod build does, because ModBuildConfig references its DLLs):

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

Mod สำหรับ Stardew Valley ที่ให้ AI (เช่น Claude) เล่นเกมเองได้ ผ่าน MCP server ที่รันอยู่ในเกม

- เวลาในเกมหยุดเดินระหว่างที่ AI กำลังคิด (ปิดได้ ถ้าอยากให้โลกเดินต่อ)
- ให้ AI เล่นเองได้สูงสุด 3 ตัว (Claude, GPT, Gemini หรือโมเดลในเครื่อง) พร้อมคุณในจอแบ่ง หรือต่อกับ MCP client ภายนอกก็ได้
- มีหน้า overlay (`/overlay`) แสดงความคิดและการกระทำของ AI แบบสด ๆ ใช้เป็น browser source ใน OBS ได้
- มีหน้า replay และ log/ไดอารี่รายวัน

**ติดตั้ง:** ลง SMAPI → แตกไฟล์ zip จากหน้า Releases ไว้ใน `Stardew Valley/Mods/` → เปิดเกมผ่าน SMAPI

**ให้ AI เล่นเอง (ไม่ต้องมี OBS หรือสคริปต์):** ลง [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098)
แล้วเปิดตั้งค่าของ *Stardew MCP* ที่หน้าไตเติล (หรือแก้ `Mods/StardewMCP/config.json`) เลือกว่าแต่ละที่นั่ง P1–P4
ใครเล่น (คุณเอง / AI / กล้อง / ปิด) เลือกผู้ให้บริการ AI (`anthropic`, `openai` หรือเซิร์ฟเวอร์ที่เข้ากันได้เช่น Ollama,
`gemini`) ใส่ API key ของตัวเอง แล้วเลือกโหมดเวลา: *หยุดโลกตอน AI คิด* (เปิด = AI ช้าก็ไม่เสียเวลาในเกม แต่ทุกคนหยุดด้วย,
ปิด = เวลาเดินต่อ AI ต้องตามให้ทัน) โหลดเซฟแล้ว AI จะเล่นเอง มีกล่องแสดงความคิดและแชตบนจอของมัน
ค่าใช้จ่ายคิดตามโทเคนของ key คุณเอง (วันละประมาณ 50–300k input tokens ต่อ AI) แนะนำโมเดลราคาถูก เช่น Haiku, Flash, GPT mini

**ต่อกับ Claude Code:** เปิดเกม โหลดเซฟ แล้วรัน
`claude mcp add --transport http stardew http://localhost:8770/mcp`

**สนับสนุนผู้พัฒนา:** ดูหัวข้อ *Support the project* ด้านบน

**เครดิต:** เริ่มต้นจากการ fork [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)
แล้วเขียนใหม่ทั้งหมดเป็น mod ที่มี MCP server ในตัว

**สัญญาอนุญาต:** MIT — ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
