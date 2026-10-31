# Stardew MCP

SMAPI mod (C#, net6.0) that hosts an MCP server inside Stardew Valley so AI agents can play.
MIT licensed, open source. This repository is **the mod only**: gameplay tools, the per-player
runtime, the HTTP API and the overlay / replay pages the mod serves itself. The project's words
(seat, brain, freeze, interrupt, self note...) are defined in `CONTEXT.md`; use them.

The YouTube production around it (the runner that drives `claude` / `agy` / `opencode` sessions
through the day, OBS director and recorder, personas, prompts) lives in a separate private
repository (`stardew-ai-studio`). It only talks to this mod over HTTP, so nothing here may depend
on it: no OBS, video, recording or runner concerns in `mod/`. When the runner needs something new,
add an HTTP endpoint or an event here and consume it there.

## What the mod does

- One game instance, optional split-screen co-op with **4 screens** in the game's 2×2 grid:
  P1 = host, P2/P3 = local farmhands, each with its own MCP endpoint (`/mcp/p1`, `/mcp/p2`,
  `/mcp/p3`); **P4 = camera seat** (no AI, parked out of the way, excluded from the scheduler,
  perception and interrupts). Single-player mode is `/mcp` alone.
- The mod joins P2–P4 itself (`GameRunner.AddGameInstance`, no gamepads), locks physical input on
  P1 and keeps frozen frames looking live (no Paused banner, HUD kept, no gamepad hint).
- **Time is event-driven.** Each AI acts independently, one action at a time. The world (clock and
  movement) is frozen whenever any connected AI is thinking, i.e. between its last tool result and
  its next action, so a slow model never loses game time. A thinking timeout (config) lets the
  world continue when an AI is stuck.
- **Communication** (decided 2026-10-19: no whisper, no shared plan board): `say` (in-game chat + bubble,
  heard only by the AIs whose screen shows the speaker, so plans and deals are made face to face; a line
  stays up `ChatSeconds`), `think` (private, overlay only), `emote`, `give`.
- **Perception and interrupts**: observations show what nearby players are *actually* doing next
  to what they last heard them say; mentions, gifts, theft, someone entering your house and weather interrupt a running
  long action and come back as `Events:` (an arrival in view only comes back with the next result).
- The AIs only ever read farmer names, never seat ids (`p1`, `p2`...), in prompts, results and tool descriptions.
- **Memory**: `journal` (diary, lessons, plan, notes), `self` / `people` notes that emerge from play.
- **Seats** (`ModConfig.Seats`, `SeatRole`): each of P1–P4 is `ai`, `human` (P1 only, keyboard and mouse,
  never frozen by the clock), `camera` or `off`. An `ai` seat is driven either by an external MCP client
  (`Brain: none`, the studio runner) or by the mod's own loop (`Brain: anthropic | openai | gemini`).
- Separate wallets, a personal plot and chest per player are game options the save provides.

## Layout

```
mod/StardewMCP/
  ModEntry.cs          SMAPI entry: config, events, auto-load, mcp_freeze command
  Pathfinder.cs        A* walking routes; IsWalkable uses the game's own collision check
  assets/              overlay.html and replay.html (served at /overlay and /replay), system_prompt.md (the built-in AI's prompt)
  Agent/
    AgentHttp.cs       HttpListener on localhost:8770: /mcp, /overlay, /replay, /api/*
    AgentRuntime.cs    Runs tool calls on the game thread as coroutines; freezes time between calls
    AgentTools.cs      Tool definitions (ToolDef) and their coroutines
    MenuTools.cs       menu: reads any open menu and clicks it through per-menu adapters
    SocialTools.cs     say (heard only by screens that show the speaker) / emote / give; per-seat inbox appended to the next tool result
    Perception.cs      Players in view with what they actually do vs. what they claimed; per-seat interrupt events
    CommunityTools.cs  bundles / donate_bundle / museum; BundleReader and LateBound shared with Joja
    JojaTools.cs       joja: membership (confirm guard) / projects / buy_project
    GoalTools.cs       goals: every achievement (no Joja) measured from the game; progress_pN.json each night
    AutomateTools.cs   Optional Automate mod: machine chests (inputs from Data/Machines), notes for place / machine, chest refuses other items
    ChestTidy.cs       chest(action=tidy): one main storage chest, Automate machine chests left alone
    ToolFetch.cs       The six main tools stay in the pack: chest refuses them, farm tools fetch a missing one from a chest
    FarmPlan.cs        Next farm buildings to save up for (Coop + chickens, house upgrade, Barn) and what is missing
    ChestLedger.cs     Shared chests: every put / take logged (kind chest, chests.jsonl), chest(action=all) and the morning report
    MachineTools.cs    machine: list / load / collect placed machines through the game's own drop-in and click
    UpgradeTools.cs    upgrade_tool: Clint's ClintUpgrade shop and the pickup at his counter
    MineTools.cs       mine (down floor by floor), leave_mine, the damage interrupt
    WorldText.cs       Text renderings of status / surroundings / inventory for the model
    GameEnglish.cs     English for the AIs in a localized game: builds the EnglishNames map, English dialogue / letters / options / professions by key
    AgentLog.cs        Per-day JSONL logs and diaries; per-seat lessons / plan / notes files (MemoryFile)
    JournalTools.cs    journal(diary, lessons, plan, notes, day); go_to_bed needs today's
    ToolResult.cs      Reason codes, partial flag and counts; MCP reply tag + structuredContent; day_XXX_summary.json
    ModConfig.cs       config.json settings
    DebugApi.cs        /api/debug/* and /api/debug/p{N}/* test hooks (warp, build_cabins, capture...), off by default
    Players.cs         The 4 split-screen seats (P1-P3 AI, P4 camera) and their per-screen state
    WorldClock.cs      Freezes the world while any connected AI thinks (host pause); thinking timeout
    DayStats.cs        Per-seat daily stats (stats_NNN[_pN].json at day rollover, GET /api/stats)
    SplitScreen.cs     Joins P2-P4 without gamepads, per-screen zoom, gamepad-hint cleanup
    SplitScreenPatches.cs  Harmony: P1 input lock, clean frozen frames, hidden join lines, frame capture
    RunControl.cs      Pause / stop_after_day / stop_now (F8, POST /api/control), unknown-menu watch for go_to_bed
    Hud.cs             In-game panel per AI screen (name, model, action or thinking time, last think / say); fed by AgentLog.Added
    ConfigMenu.cs      Generic Mod Config Menu pages (optional dependency; IGenericModConfigMenuApi.cs is the copied API)
    Cabins.cs          Builds the farmhands' cabins (AutoBuildCabins, debug build_cabins)
    Brain/             The built-in AI loop ("viewer edition": the mod plays a seat itself with the user's API key)
      Llm.cs           Provider-neutral chat types, ILlmProvider, LlmProviders factory, SchemaFilter (game-free)
      AnthropicProvider.cs / OpenAiProvider.cs / GeminiProvider.cs   Wire formats (game-free; OpenAI = any compatible server)
      Prompts.cs       System prompt from assets/system_prompt.md + memory files; morning / nudge / handoff texts (game-free)
      SeatBrain.cs     One seat's day loop: model call -> tool calls on the seat's runtime -> results; breaks, retries (no meeting phase)
      Brains.cs        Starts a SeatBrain per configured seat on save load; mcp_brain console command
mod/StardewMCP.Tests/  xUnit, game-free (see Tests)
mod/tests/             pytest lints over the C# source, the overlay pages and the docs (see Tests)
local/                 Local-model kit (Windows): setup.ps1 (game, SMAPI, mod, Ollama, stardew-local), start.cmd/.ps1,
                       Modelfile, config.example.json, automate-config.json, mods/ThaiFontInEnglish (CP pack, fonts copied
                       from the Thai pack at setup, never committed) (docs/local-ai.md)
README.md, README.en.md   Landing page (Thai = the front page / English): the EP.1 video, install, quick start, links into docs/
CONTEXT.md             Glossary: the project's words
docs/
  features.md, features.th.md   What the AI can do, area by area, and what is missing (for players)
  tools.md, tools.th.md         Every MCP tool with its arguments, grouped by job
  api.md               HTTP endpoints, run control, tool results and reason codes, log files
  configuration.md     Every config.json setting, seats and roles, overlay language
  local-ai.md, local-ai.th.md   Playing on a local model: local/setup.ps1, the stardew-local model, publishing it
  coverage.md          Which game features the tools cover, and how to add one
  economy.md           Shared farm economy: wallets, ownership, theft, payments
  recording.md, howto-record-day.md   One recording setup, as an example of building on the API
  assets/              Images for the docs (ep01-cover.png = the README's video thumbnail)
```

## How it works

- SMAPI raises `UpdateTicked` once per split-screen screen (`Context.ScreenId`); `Game1.player`,
  `currentLocation` and menus are per screen. Each seat has its own `AgentRuntime`, ticked on its
  screen; `AgentTools.Rt` is the seat of the screen being updated.
- MCP requests arrive on the HTTP thread; the seat's `AgentRuntime` runs actions one at a time and
  instant observations in a second lane, so `look`/`status` answer during the seat's own action.
- Tools are `IEnumerator` coroutines: `yield return null` waits a tick, `yield return <IEnumerator>`
  runs a nested step. Report results through `CallContext` (`Result`, `Fail`).
- `Instant` tools only observe and keep the world frozen. `WorldClock` freezes the world while any
  connected AI has no action running; running actions hold still meanwhile.
- The model makes decisions; mechanics (pathing, aiming, button presses) are automated in the mod.

## Build

```
cd mod/StardewMCP
dotnet build -c Release                                    # builds, deploys to Mods/, makes zip
dotnet build -p:EnableModDeploy=false -p:EnableModZip=false   # compile check only
```

## Tests

The mod build needs the game's DLLs (ModBuildConfig), so CI and the test projects never build it.

```
dotnet test mod/StardewMCP.Tests          # C# xUnit, game-free
pip install pytest && python -m pytest -q mod/tests   # Python lints over the source and the pages
```

```
mod/StardewMCP.Tests/  Does NOT reference the mod project; compiles the game-free mod files below via
                       <Compile Include Link> and shims Point/Vector2 (Shims/XnaTypes.cs)
mod/tests/             test_tool_sources.py: tool schemas read from the C# source (snake_case names, reason
                       on actions, required in properties); test_i18n.py: overlay/replay language tables (needs node);
                       test_crop_math.py, test_bobber_controller.py: Python twins of KnowledgeMath.cs and BobberController.cs;
                       test_docs.py: docs/tools.md and docs/tools.th.md list exactly the tools the source defines
```

Game-free mod files (no `Game1`, `GameLocation`, `Farmer`; linked into the tests):

```
mod/StardewMCP/PathSearch.cs         A* over IWalkableGrid (Pathfinder.cs supplies the game's collision check)
mod/StardewMCP/Agent/ToolDef.cs      ToolDef, CallContext, ToolRunner
mod/StardewMCP/Agent/ToolArgs.cs     Schema and argument helpers (Obj, Str, Int, Tiles, S, I, TileList), part of AgentTools
mod/StardewMCP/Agent/ToolContract.cs Rules for tools/list (valid schema, required in properties, reason on actions); logged at startup
mod/StardewMCP/Agent/LoopDetector.cs GuardKind, GuardNote, LoopDetector: repeat-failure and no-progress rules (LoopGuard.cs feeds it from the game)
mod/StardewMCP/Agent/McpDispatcher.cs JSON-RPC handling over IMcpSeat; McpSeat.cs adapts a real seat for AgentHttp
mod/StardewMCP/Agent/Peek.cs         Fail-soft reflection and Netcode unwrapping for QuestTools / QuestEvents
mod/StardewMCP/Agent/KnowledgeMath.cs CropMath, RecipeMath behind crops / item_info (Python twin: mod/tests/test_crop_math.py)
mod/StardewMCP/Agent/BobberController.cs BobberMath, CastMath for fish (Python twin and BobberBar model: mod/tests/test_bobber_controller.py)
mod/StardewMCP/Agent/WorldText.Format.cs  FormatTime, DirName, part of WorldText
mod/StardewMCP/Agent/BubbleText.cs   Line breaking for the speech bubbles (Thai-aware)
mod/StardewMCP/Agent/SpeechQueue.cs  say lines shown one at a time per place, timed only while the world runs
mod/StardewMCP/Agent/CompressedLogStore.cs  lossless appendable gzip logs, legacy migration and day discovery
mod/StardewMCP/Agent/SaleReserve.cs  basic resources that are never sold, protected planting inputs
mod/StardewMCP/Agent/DaySafety.cs  morning readiness, early sleep (and its refusal text), committed-sleep social interruption, stale message boxes at bedtime
mod/StardewMCP/Agent/GoalText.cs     the achievement goal list (no Joja), its targets and wording, recipe unlocks in words
mod/StardewMCP/Agent/GeodeGuide.cs   crack_geode's guide per geode kind (where it comes from, what it holds) and loose name matching
mod/StardewMCP/Agent/FishGuideText.cs  fish_guide's time/weather/condition wording, code-placed and legendary fish ids
mod/StardewMCP/Agent/EnglishNames.cs  NameMap: localized names → English keys in every text an AI reads (AgentRuntime, SeatBrain)
mod/StardewMCP/Agent/LanguageGuard.cs  Chinese characters in think / say / diary text that must be in another language
mod/StardewMCP/Agent/Brain/DailySocialProgress.cs  per-day social progress and bounded reminders across context resets
mod/StardewMCP/Agent/Brain/Llm.cs, *Provider.cs, Prompts.cs   Built-in AI wire formats and prompt texts
```

New logic that can run without the game goes into such a file with a test; keep moves small and leave a
forwarder behind. A tool's `reason`, snake_case name and `required` list are checked by `ToolContract` and
`mod/tests/test_tool_sources.py`.

In-game behaviour is checked by the studio repo's smoke tests (`runner/smoke.py`, LLM-free, through
`/api/debug/*` on a `smoke_*` copy of a save). A feature row in `docs/coverage.md` becomes ✅ only with such a case.

## Rules

- Keep everything original: don't copy code from other mods or repos unless their license
  allows it, and credit them if so.
- Never commit game files, saves, logs or personal paths.
- New game features follow `docs/coverage.md`: reuse the game's own entry points, and update the
  feature's row in the same PR.
- The HTTP API (`/api/*`, `/api/events`) is the contract with the studio repo: add to it, don't break it.
- Docs change with the code, in the same PR: a tool → `docs/tools.md` + `docs/tools.th.md`; an endpoint or log
  file → `docs/api.md`; a setting → `docs/configuration.md`; something players notice → `docs/features.md` +
  `docs/features.th.md`; a new term → `CONTEXT.md` (table in `AGENTS.md`). The READMEs stay a short landing page
  without reference tables. English and Thai pages are twins with the same sections; the Thai is written as natural
  Thai, not word for word.
- Reference: https://stardewvalleywiki.com/Modding:Modder_Guide/APIs
