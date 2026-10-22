# Stardew MCP

SMAPI mod (C#, net6.0) that hosts an MCP server inside Stardew Valley so AI agents can play.
MIT licensed, open source. This repository is **the mod only**: gameplay tools, the per-player
runtime, the HTTP API and the overlay / replay pages the mod serves itself.

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
  to what they last heard them say; mentions, gifts, theft, arrivals and weather interrupt a running
  long action and come back as `Events:`.
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
    WorldText.cs       Text renderings of status / surroundings / inventory for the model
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
mod/tests/             pytest lints over the C# source and the overlay pages (see Tests)
docs/coverage.md       Which game features the tools cover, and how to add one
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
                       test_crop_math.py, test_bobber_controller.py: Python twins of KnowledgeMath.cs and BobberController.cs
```

Game-free mod files (no `Game1`, `GameLocation`, `Farmer`; linked into the tests):

```
mod/StardewMCP/PathSearch.cs         A* over IWalkableGrid (Pathfinder.cs supplies the game's collision check)
mod/StardewMCP/Agent/ToolDef.cs      ToolDef, CallContext, ToolRunner
mod/StardewMCP/Agent/ToolArgs.cs     Schema and argument helpers (Obj, Str, Int, Tiles, S, I, TileList), part of AgentTools
mod/StardewMCP/Agent/ToolContract.cs Rules for tools/list (valid schema, required in properties, reason on actions); logged at startup
mod/StardewMCP/Agent/McpDispatcher.cs JSON-RPC handling over IMcpSeat; McpSeat.cs adapts a real seat for AgentHttp
mod/StardewMCP/Agent/Peek.cs         Fail-soft reflection and Netcode unwrapping for QuestTools / QuestEvents
mod/StardewMCP/Agent/KnowledgeMath.cs CropMath, RecipeMath behind crops / item_info (Python twin: mod/tests/test_crop_math.py)
mod/StardewMCP/Agent/BobberController.cs BobberMath, CastMath for fish (Python twin and BobberBar model: mod/tests/test_bobber_controller.py)
mod/StardewMCP/Agent/WorldText.Format.cs  FormatTime, DirName, part of WorldText
mod/StardewMCP/Agent/BubbleText.cs   Line breaking for the speech bubbles (Thai-aware)
mod/StardewMCP/Agent/SpeechQueue.cs  say lines shown one at a time per place, timed only while the world runs
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
- Reference: https://stardewvalleywiki.com/Modding:Modder_Guide/APIs
