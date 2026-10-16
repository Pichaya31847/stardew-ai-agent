# Stardew MCP

SMAPI mod (C#, net6.0) that hosts an MCP server inside Stardew Valley so AI agents can play.
MIT licensed, open source.

## Project goal: three AIs play together

Three different AIs (Claude, Gemini, GPT) share one farm in split-screen co-op and play as
characters for YouTube videos: they talk, plan, split work, help each other, slack off, lie,
get caught and betray each other. The story must emerge from the game, not from a script.

### Requirements

**Players and backends**
- One game instance, split-screen local co-op, 3 farmers: P1 = host, P2/P3 = local farmhands.
- Each farmer is driven by one AI through its own MCP endpoint: `/mcp/p1`, `/mcp/p2`, `/mcp/p3`.
- Backends are the CLIs the user already subscribes to: `claude` (Claude), `agy` (Gemini),
  `opencode` (GPT). Each session gets only the stardew MCP tools (no shell, no file edits).
  A direct API backend may be added later per player if a CLI proves unreliable.
- Each player has a persona file (name, traits, optional secret goal) loaded by the runner.

**Time: event-driven, no fixed rounds**
- Each AI acts independently, one action at a time; nobody waits for a turn.
- The world (clock *and* movement) is frozen whenever any AI is thinking, i.e. between its last
  tool result and its next action. Thinking speed never costs game time, so a farmer standing
  still has chosen to (`wait`, slacking), never "the model is slow".
- A thinking timeout (config) makes a stuck or crashed AI's farmer idle so the world can continue.
- Day shape: morning meeting (chat + plan board only, world frozen, message cap) → free play →
  evening private diary → everyone goes to bed.

**Communication (three levels)**
- `say`: real in-game chat box + text above the farmer's head. All AIs and viewers see it.
- `whisper`: to one player only; viewers see it too.
- `think`: private thought, viewers only (overlay). This is where secret plans and lies show.
- `emote` (game emotes), `give` (hand an item to another player).
- Shared plan board (`plan_read`, `plan_edit`): every edit is logged with author and time.

**Perception and interrupts**
- Observations of nearby players show what they are *actually* doing (current tool + time spent),
  next to what they claimed on the plan board or in chat, so lies can be caught.
- These events interrupt a running long action (walk, wait, farm work) and return control to the
  AI with the event list: being @mentioned or whispered to, another player coming into view,
  someone taking from your chest or harvesting your plot, receiving a gift, weather or crop damage.

**Things to compete over**
- Separate wallets (game multiplayer option), a personal plot and chest per player.

**Separation: game vs. YouTube vs. AI orchestration**
- `mod/` is gameplay only: per-player MCP, scheduler, chat, plan board, `/api/state`, and an
  event stream `/api/events` (SSE). It never references OBS or video concerns.
- `studio/` is the YouTube layer and optional: overlay (per-player think/chat/whisper), replay,
  OBS director, recorder, story log for editing. It only reads the mod's HTTP API.
  `assets/overlay.html` and `assets/replay.html` move here.
- `runner/` launches and supervises the three AI sessions and the day loop.

**Video**
- OBS scenes: "all three" plus "focus P1/P2/P3" (crop of that player's screen, others as
  picture-in-picture). The director switches scenes from `/api/events`: focus whoever speaks,
  whispers or triggers a key event (theft, gift, caught slacking); grid during plain work.
- Record at 1440p or higher so a cropped screen is still sharp. The recorder keeps cutting
  frozen time, holding a few seconds for chat bubbles and thoughts.

### Phases

0. **Spike: split-screen with 3 mod-driven farmers** (#37). Spawn P2/P3 from the mod without gamepads,
   run tools per screen, freeze all three at once. Go/no-go; fallback is LAN co-op with 3 windows.
1. Multi-player core (#38): per-player endpoints and runtime, freeze-while-anyone-thinks scheduler,
   thinking timeout, per-player logs.
2. Social tools (#39): say, whisper, think, emote, give, plan board.
3. Perception and interrupts (#40).
4. Runner for claude / agy / opencode with personas and day phases (#41).
5. Studio (#42): `/api/events`, overlay and replay moved out, OBS director and scenes, recorder.

## Layout (current, single player)

```
mod/StardewMCP/
  ModEntry.cs          SMAPI entry: config, events, auto-load, mcp_freeze command
  Pathfinder.cs        A* walking routes; IsWalkable uses the game's own collision check
  Agent/
    AgentHttp.cs       HttpListener on localhost:8770: /mcp, /overlay, /replay, /api/*
    AgentRuntime.cs    Runs tool calls on the game thread as coroutines; freezes time between calls
    AgentTools.cs      Tool definitions (ToolDef) and their coroutines
    WorldText.cs       Text renderings of status / surroundings / inventory for the model
    AgentLog.cs        Per-day JSONL logs and diaries
    ModConfig.cs       config.json settings
    DebugApi.cs        /api/debug/* test hooks (warp, set_time, add_item, load_save...), off by default
  assets/              overlay.html (OBS source), replay.html
runner/
  smoke.py             LLM-free tool tests on a smoke_* copy of the save (gate for run_days.py)
  run_days.py          One `claude -p` session per in-game day
  obs_recorder.py      Pauses OBS recording while the game is frozen
  system_prompt.md     System prompt used by run_days.py
```

## How it works

- MCP requests arrive on the HTTP thread; `AgentRuntime` queues them and runs one at a time on
  the game thread from `UpdateTicked`.
- Tools are `IEnumerator` coroutines: `yield return null` waits a tick, `yield return <IEnumerator>`
  runs a nested step. Report results through `CallContext` (`Result`, `Fail`).
- `Instant` tools only observe and keep game time frozen; other tools unfreeze it while they run.
- The model makes decisions; mechanics (pathing, aiming, button presses) are automated in the mod.

## Build

```
cd mod/StardewMCP
dotnet build -c Release                                    # builds, deploys to Mods/, makes zip
dotnet build -p:EnableModDeploy=false -p:EnableModZip=false   # compile check only
```

## Rules

- Keep everything original: don't copy code from other mods or repos unless their license
  allows it, and credit them if so.
- Never commit game files, saves, logs or personal paths.
- Reference: https://stardewvalleywiki.com/Modding:Modder_Guide/APIs
