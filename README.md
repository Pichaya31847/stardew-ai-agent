# Stardew MCP

A [SMAPI](https://smapi.io) mod that lets an AI agent play Stardew Valley. The mod runs an
[MCP](https://modelcontextprotocol.io) server **inside the game**, so Claude (or any MCP client)
can look around, walk, farm, talk to villagers, shop and go to bed on its own.

- **Game time freezes while the AI thinks**, so a slow model loses no in-game time.
- **Stream-ready overlay** for OBS showing the AI's thoughts and actions live.
- **Replay page** and per-day JSONL logs + diaries for every run.
- **Runner scripts** to play many days unattended and record only the gameplay.

> ภาษาไทยอยู่ด้านล่าง ↓

## Support the project

If you enjoy this project, you can support development here:

- ☕ **Ko-fi:** _coming soon_ <!-- TODO(Duckxy): add link -->
- 💖 **GitHub Sponsors:** _coming soon_ <!-- TODO(Duckxy): add link -->

## Requirements

- Stardew Valley 1.6+
- [SMAPI](https://smapi.io) 4.0+
- An MCP client, e.g. [Claude Code](https://claude.com/claude-code)

## Install

1. Install SMAPI and run the game once through it.
2. Download the latest `StardewMCP` zip from the Releases page.
3. Unzip it into `Stardew Valley/Mods/`.
4. Start the game through SMAPI. The console should show
   `Agent MCP at http://localhost:8770/mcp`.

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
| `status`, `look`, `inventory` | Read the player, surroundings and items |
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
| `quests`, `accept_quest` | Quest journal with story, giver, progress and reward; today's Help Wanted request and who took it; Special Orders; `share` posts yours to the plan board; `accept_quest` takes the Help Wanted request |
| `crack_geode`, `trash`, `watch_tv` | Clint cracks geodes (25g each); throw items away (valuables need `confirm`); watch a TV channel (weather, fortune, Queen of Sauce, tips) |
| `shop_list`, `buy`, `sell`, `ship`, `eat` | Shopping, selling to an open shop for gold at once, shipping bin (pays overnight), food |
| `calendar` | Today and the next days: festivals, birthdays, the next festival, and the main shops' hours and closed days (no game time) |
| `festival` | On a festival day: `start` talks to Lewis and answers yes (waits for the other players), `leave` walks out; `talk_to` also reaches festival villagers and booths |
| `fish` | Walk to the best shore for the rod, cast `tries` times, play the BobberBar minigame and pick up treasure. Costs energy and game time per cast; one result line per cast. Needs a fishing rod (Willy, at the Beach) |
| `crops`, `item_info` | Game knowledge read from the game's own data, no game time: `crops` is a season planner (growth and regrow days, last day to plant, harvests that fit, seed and sell prices, gold per tile per day, marks the seeds you carry); `item_info` gives an object's sell prices by quality, what it restores, shops that sell it and recipes that use it |
| `wait`, `go_to_bed` | Let time pass, end the day (`go_to_bed` refuses until today's `journal` is written) |
| `journal` | The AI's memory across days, costs no game time: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections (replaced section by section) and optional facts. Written to `diary_NNN.md`, `lessons.md`, `plan.md`, `notes.md` in the log folder; `day` catches up a missed day |
| `say`, `whisper`, `emote`, `give` | Multi-player (split-screen): chat to everyone, tell one player privately, show an emote, hand an item to a nearby player. All cost no game time |
| `plan_read`, `plan_edit` | Shared plan board with an edit history (author and time) |

### Other endpoints

| URL | Use |
|---|---|
| `http://localhost:8770/overlay` | OBS browser source (thoughts + actions) |
| `http://localhost:8770/replay` | Day-by-day replay of the logs |
| `http://localhost:8770/api/state` | Raw game state JSON. After a day that ended without `go_to_bed` (2:00 AM, health 0), `passOut` says why and `pendingJournalDay` is the day whose journal is still owed until `journal(day=N)` is written |
| `http://localhost:8770/api/memory` | Each AI seat's lessons, plan and notes (what `journal` wrote) |
| `http://localhost:8770/api/relationships` | Every AI farmer's friendships (hearts, birthdays, gift tastes found out); `runner/run_days.py` turns it into `relationships.md` |
| `POST http://localhost:8770/api/release_freeze` | Runner only: stop an idle seat from freezing the world (`{"seat": "p1"}`, or `"all"`; `"on": false` takes it back) so game time runs until the farmer passes out. Ends with the farm day |
| `http://localhost:8770/api/briefing?seat=p1` | Morning briefing of one AI seat: overnight results, shipping by item, mail, weather, farm (the runner puts it in the first prompt of a day) |

## Configuration

`Mods/StardewMCP/config.json` is created on first launch:

| Setting | Default | Meaning |
|---|---|---|
| `HttpPort` | `8770` | Port for MCP, overlay and replay |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `FarmBatchMinEnergy` | `10` | `water_all`, `harvest_all`, `clear_area`, `till_area` and `plant` area mode stop when energy drops below this (0 = only stop at 0) |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks for `runner/smoke.py`; leave off for normal play |
| `FishingAssist` | `"perfect"` | How the `fish` tool plays the fishing minigame: `"perfect"` keeps the fish inside the bar (the safe fallback); `"natural"` presses the mouse button like a player would, so it can lose fish |
| `ThoughtLanguage` | `"Thai"` | Language the AI is asked to use for on-screen text (`think`, action reasons, journal), e.g. `"English"` |
| `SplitScreen` | `false` | Three AIs + a camera screen in split-screen co-op (see below) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Farmhand names for P2, P3, P4; an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | Split-screen: an AI silent for longer stops holding the world frozen (0 = never) |
| `PlayHeartEvents` | `false` | Let heart events (story cutscenes) play and stop at their questions for `respond`, instead of skipping them. Experimental, see `docs/coverage.md` |
| `RevealGiftTastes` | `false` | `villagers(name)` shows the game's real gift tastes. Off: a farmer only knows the tastes they found out by gifting |
| `LockHostInput` | `true` | Split-screen: ignore the real keyboard/mouse on P1 once a save is loaded |
| `CleanFrames` | `true` | Split-screen: frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Split-screen: zoom each screen to about 20 tiles across |

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
doing (running tool, its reason, game minutes so far) next to what it last said and wrote on the plan board.
An @name or @pN in `say`, a whisper, a gift, a player coming into view or entering your house, a theft from
your chest or plot, rain starting, or crows on the farm are added to your next tool result as `Events:` and
stop a running `wait`, `walk_to`, `travel_to` or farm action, so a model never sleeps through them.

`ThoughtLanguage` only changes what the MCP tools ask the AI to write. The overlay's own labels and
`runner/system_prompt.md` are still written for Thai viewers, so edit those too if you switch language.

## Unattended runs and recording

`runner/` contains Python scripts (standard library only):

- `run_days.py` runs one Claude Code session per in-game day, keeps the AI's memory between days
  (its lessons, plan, facts and recent diaries go into the next day's system prompt) and backs up the
  save. Example: `py runner/run_days.py --days 3`. It reads those files from `<out>/logs`, so set the
  mod's `LogDir` to that folder. After the last day of a batch, one short text-only session shortens
  `lessons.md` and `notes.md` (the old lists stay as `lessons_before_batchN.md`) and writes a digest
  of each finished week (`--skip-consolidation` skips it), so run in batches of about a week.
  If the subscription's usage limit ends a session, the runner waits for the reset (or retries every
  30 minutes when the time can't be read), shows "รอ usage limit รีเซ็ต (ถึง 03:00)" on the overlay and
  resumes the same day; that part doesn't count toward `--max-parts`. Logic and options (`--limit-*`,
  `--max-limit-waits`) are in `runner/usage_limit.py`; check a saved transcript with
  `py runner/usage_limit.py runs/logs/claude_day_001_part1.jsonl`.
- `memory.py` is the memory logic behind that (placeholders, consolidation); its tests run without the
  game: `py -m pytest runner/tests`.
- `relationships.py` (used by `run_days.py`) writes `relationships.md` next to the diaries each day from `/api/relationships`
  and puts it in the system prompt as `{RELATIONSHIPS}`. It is generated from game data, never written by the AI.
- `smoke.py` runs LLM-free checks of the tools (farm loop, shop, shipping, sleep) on a copy of your
  save, then reloads your save. `run_days.py` runs it before every batch and stops if a case fails.
  It needs `"EnableDebugApi": true` in `config.json`; see `py runner/smoke.py --list`. The debug
  endpoints are not MCP tools, so the AI cannot see or call them.
- `obs_recorder.py` pauses the OBS recording while the game is frozen, so the video only contains
  gameplay. Needs OBS with obs-websocket enabled.
- `game_watchdog.py` is used by `run_days.py` to survive a game crash or hang (below).

### Watchdog: crash and hang recovery

Stardew only saves overnight, so after a crash the only way on is to start the game again and replay
the day from the morning. `run_days.py` watches the game while it plays and does that by itself:

| Signal | Meaning |
|---|---|
| process gone | the SMAPI process is not running (two checks in a row) |
| no answer | `/api/state` fails for more than 2 minutes, or its `heartbeat` stops counting for that long (the game loop is blocked while the HTTP thread still answers) |
| stalled | an action is running (`frozen: false`) but the game clock has not moved for 3 minutes. Thinking time never counts; cutscenes, festivals and the overnight menus get 15 minutes |

On a fault it kills the `claude` session and the game, moves the attempt's files aside, starts the game
again, waits until the save is loaded and plays the day again with a fresh part-1 session. The AI is not
told. After 2 replays of the same day the third crash stops the batch with a message (probably a mod bug).

```
py runner/run_days.py --days 3 --game-cmd "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```

- `--game-cmd` (or env `STARDEW_GAME_CMD`) is the command that starts SMAPI; the working directory is its folder.
  Without it faults are still detected but the batch stops with a message instead of relaunching.
  `--no-watchdog` turns everything off.
- `--game-process` (default `StardewModdingAPI.exe` on Windows, `StardewModdingAPI` elsewhere) is the name used to
  find and kill the game. Windows uses `tasklist` / `taskkill /F /T`, other systems `pgrep` / `pkill`.
- **`AutoLoadSave`**: the mod reads `config.json` once at start, so before every relaunch the runner writes the run's
  save folder name into `AutoLoadSave` (all other keys stay as they are, the file is replaced atomically) and puts the
  old value back when the runner ends. The save is the one loaded when the batch starts (`/api/state.saveFolder`), or
  `--save-folder`. `--mod-config` names the file if it is not `Mods/StardewMCP/config.json` next to `--game-cmd`.
  The mod stays on the title screen and logs an error if that folder does not exist in the game's `Saves` folder.
- Other flags: `--crash-retries` (2), `--no-answer-secs` (120), `--stall-secs` (180), `--launch-timeout` (600).
- Files of a crashed attempt are kept: `day_005.jsonl` becomes `day_005_attempt1.jsonl`,
  `claude_day_005_part1.jsonl` becomes `claude_day_005_attempt1_part1.jsonl`, and the day's diaries/journals become
  `diary_005_attempt1.md`, so nothing a crashed attempt wrote reaches the AI's memory. `watchdog.jsonl` in the log
  folder lists every fault, relaunch and moved file. If the game crashed after the night save, the relaunched game is
  already on the next day: the day counts as done and its files are put back.
- Check the setup without any AI: `py runner/game_watchdog.py --relaunch-test --game-cmd "..."` kills the game, starts
  it again and checks that the save auto-loads.
- With `"EnableDebugApi": true`, `POST /api/debug/hang` with `{"seconds": 300}` blocks the whole game loop, to see the
  "no answer" signal fire for real. `/api/state` also carries `heartbeat`, `eventUp` and `festival` for the watchdog.

`studio/` contains the YouTube / OBS layer (`overlay.html`, `replay.html`, and `director.py` which switches OBS scenes from `/api/events`).

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your
game folder, copies the mod into `Mods/` and creates a release zip in `bin/`.

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

- เวลาในเกมหยุดเดินระหว่างที่ AI กำลังคิด
- มี overlay สำหรับ OBS แสดงความคิดและการกระทำของ AI แบบสด ๆ
- มีหน้า replay และ log/ไดอารี่รายวัน
- มีสคริปต์ให้ AI เล่นต่อเนื่องหลายวัน และอัดวิดีโอเฉพาะช่วงที่เล่นจริง

**ติดตั้ง:** ลง SMAPI → แตกไฟล์ zip จากหน้า Releases ไว้ใน `Stardew Valley/Mods/` → เปิดเกมผ่าน SMAPI

**ต่อกับ Claude Code:** เปิดเกม โหลดเซฟ แล้วรัน
`claude mcp add --transport http stardew http://localhost:8770/mcp`

**สนับสนุนผู้พัฒนา:** ดูหัวข้อ *Support the project* ด้านบน

**เครดิต:** เริ่มต้นจากการ fork [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)
แล้วเขียนใหม่ทั้งหมดเป็น mod ที่มี MCP server ในตัว

**สัญญาอนุญาต:** MIT — ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
