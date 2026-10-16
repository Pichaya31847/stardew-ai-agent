# Stardew MCP

SMAPI mod (C#, net6.0) that hosts an MCP server inside Stardew Valley so an AI agent can play.
MIT licensed, open source.

## Layout

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
