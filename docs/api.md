# HTTP API

← README: [ภาษาไทย](../README.md) · [English](../README.en.md) · [Tools](tools.md) · [Configuration](configuration.md)

Everything the mod serves, on `http://localhost:8770` (`HttpPort`). Scripts, bots and stream overlays use this API;
nothing else in the mod is a public contract.

## Endpoints

| URL | Use |
|---|---|
| `/mcp`, `/mcp/p1` … `/mcp/p3` | MCP endpoints (streamable HTTP), one per farmer. `/mcp` is `/mcp/p1`. `?breaks=1` turns on [session breaks](configuration.md#long-runs) for that client |
| `/overlay` | Live thoughts and actions; add it to OBS as a browser source (`?player=p2` for one seat, `&mock=1` for sample text without the game, `?lang=English` for the UI language) |
| `/replay` (also `/`) | Day-by-day replay of the logs |
| `/api/state` | Game state as JSON: every seat with its current action and thinking time, plus `saveFolder`, `paused`, `stopAfterDay`, `stopNow`. After a day that ended without `go_to_bed` (2:00 AM, health 0), `passOut` says why and `pendingJournalDay` is the day whose journal is still owed until `journal(day=N)` is written |
| `/api/control` | [Run control](#run-control): `POST {"action": "pause" \| "resume" \| "stop_after_day" \| "stop_now" \| "clear_stop"}`; `GET` reads the flags |
| `/api/feed?after=N` | The live log feed the overlay reads: state, thought language, meta and every log entry after id `N` |
| `/api/days` | The day numbers that have a log |
| `/api/day?n=N` | One day's log entries and every AI seat's diary (what the replay reads) |
| `/api/memory` | Each AI seat's lessons, plan and notes (what `journal` wrote) |
| `/api/relationships` | Every AI farmer's friendships (hearts, birthdays, gift tastes found out) |
| `/api/briefing?seat=p1` | Morning briefing of one AI seat: overnight results, shipping by item, mail, weather, farm |
| `/api/stats` | Daily stats: the days that have a `stats_NNN.json`; `?n=3` returns day 3 of every AI seat |
| `POST /api/release_freeze` | Stop an idle seat from freezing the world (`{"seat": "p1"}`, or `"all"`; `"on": false` takes it back) so game time runs until the farmer passes out. Ends with the farm day |
| `POST /api/meta` | Key/value pairs shown by the overlay and replay (`model`, `modelId`, `session`, `runnerStatus`...); an `event` key also writes a system line to the log |
| `POST /api/debug/*` | Test hooks (warp, set_time, add_item...), only with `EnableDebugApi` |

## Run control

| Want | Do | What happens |
|---|---|---|
| Pause | Press `F8` in the game window (`PauseKey`), or `POST /api/control {"action": "pause"}` | The running action finishes, then the whole world freezes and new AI calls wait. `F8` again (or `resume`) continues |
| Stop after today | `POST /api/control {"action": "stop_after_day"}` | Sets `stopAfterDay` in `/api/state`, so the script driving the AI stops once the day ends in bed. `clear_stop` undoes it |
| Stop now | `POST /api/control {"action": "stop_now"}` | The running action finishes, new calls are refused and the game stays frozen until `resume` |

The SMAPI console command `mcp_freeze [on|off]` turns time freezing on or off while the game runs, and `mcp_brain`
shows the built-in AIs (`mcp_brain stop` / `mcp_brain start` pauses and resumes them).

During a pause, a call that waits longer than 9 minutes is answered with an error (MCP clients time out at 10 minutes),
so the AI simply calls again and no stale call runs after `resume`. A script that watches for hangs should check
`paused` in `/api/state` before it treats a clock that isn't moving as a hang.

If an unknown menu stays open for more than 10 seconds while `go_to_bed` waits for the new day (the shipping screen,
level-ups, the save screen and the ready check are expected), the mod logs its type and closes it instead of letting
the call run into its 5 minute timeout.

## Tool results

Every tool reply keeps its plain text and adds a machine-readable part (MCP `structuredContent`). `isError` is `true`
only when the tool failed.

```json
{ "content": [{ "type": "text", "text": "[partial · low_energy] Stopped: energy too low (2). ..." }],
  "isError": false,
  "structuredContent": { "ok": true, "reason": "low_energy", "partial": true, "data": { "done": 12, "remaining": 28, "skipped": 0 } } }
```

- The text starts with a short tag when the tool did only part of its work (`[partial · reason]`) or failed for a
  known reason (`[no_path]`). Plain successes have no tag.
- `reason` is one of: `ok`, `partial`, `low_energy`, `no_path`, `not_here`, `no_item`, `inventory_full`, `menu_open`,
  `too_late`, `shop_closed`, `wrong_season`, `needs_upgrade`, `interrupted`, `timeout`, plus `no_money`,
  `invalid_args`, `not_found`, `not_ready`, `error` and `other` (a failure that has no specific code yet). `too_late`
  and `wrong_season` are reserved: the game gives no reliable signal, so no tool sets them yet.
- `use_tool`, `plant` and `harvest` are batch tools: they always report `data` (`done`, `remaining` = tiles not
  reached, `skipped` = tiles tried and failed) and set `partial` when `remaining` or `skipped` is above 0. `reason`
  then says why the batch stopped (or, failing that, why tiles were skipped).
- The common failures (`walk_to`, `travel_to`, `use_tool`, `plant`, `harvest`, `interact`, `talk_to`, `buy`, `ship`,
  `eat`, `go_to_bed`, `shop_list`, `think`) have codes; other tools still return text only (`reason` is `other` on failure).
- Logs: result lines of `day_NNN.jsonl` carry `reason` and `partial` (the text there stays untagged). Next to each
  day's log the mod writes `day_NNN_summary.json` with the failure reasons per tool and per player. The overlay marks
  partial results `◐` and shows the reason, the replay shows a reason badge.

## Files the mod writes

In the log folder (`LogDir`, default `logs/` in the mod folder). P1 (and a single-player run) uses the plain name,
the other seats add `_p2`, `_p3`:

| File | What |
|---|---|
| `day_NNN.jsonl.gz` | Every tool call, result, thought, chat line and event of the day, losslessly compressed as concatenated gzip members. Legacy `day_NNN.jsonl` remains readable and is migrated on the next write only after decompressed bytes match. `/api/day` and `/api/days` handle both formats; their response format is unchanged. Standard gzip tools can extract the original JSONL. Memory Markdown and saves retain their existing format. |

Player state snapshots add `transitioning` and optional `nightEvent` (native overnight event class). The built-in brain waits until overnight events/fades/pass-out finish, even when `day` already advanced. Transition diagnostics refresh on unvalidated SMAPI ticks during saving without running gameplay coroutines.
| `day_NNN_summary.json` | Failure reasons per tool and per player |
| `diary_NNN.md` | The AI's diary for the day |
| `lessons.md`, `plan.md`, `notes.md` | What `journal` keeps across days. With the built-in AI, once lessons.md passes 25 lines it is condensed after bed to the 20 most useful lessons |
| `lessons_archive.md` | The lessons as they were before each condensing |
| `Mods/StardewMCP/memory/lasting_lessons.md` | Not in the log folder: lessons that hold on any farm, shared by the seats and kept across saves; starts from `assets/lasting_lessons_seed.md` and is updated when lessons are condensed. Read in every built-in AI system prompt |
| `self.md`, `people.md` | The AI's note on who it is, and its line on each other farmer |
| `stats_NNN.json` | Daily stats, written at the day rollover. `progress` (added, same version) holds `backpackSlots`, `communityCenter` (`bundlesComplete`, `bundlesTotal`, `next`: the story's next step) and `events`: counts of `session_break` (built-in AI restarted its conversation), `rest`, `backpack_upgrade`, `quest_reward_claimed`, `festival_wait_gave_up` and the `quest_accepted` / `quest_completed` / `quest_failed` journal events |
| `chests.jsonl` | Every change to a shared chest, one JSON line per item: `day`, `time`, `seat`, `farmer`, `action` (`put` / `took`), `item`, `count`, `location`, `x`, `y`. The same changes are in the day log as kind `chest` |
| `progress_pN.json`, `progress_NNN_pN.json` | The achievement goal of each AI seat (always with the seat id, `p1` too). `progress_pN.json` is the latest reading (rewritten at every rollover and every `goals` call); `progress_NNN_pN.json` is the state at the end of farm day NNN. Fields: `achievements` (`name`, `have`, `need`, `done`, `earned`), `shipping` (`shipped` / `notShipped` item names), `cooking` (`cooked`, `knownNotCooked`, `notLearned` with `how`), `crafting` (the same), `fish` (`caught` / `notCaught`), `museum` (`onDisplay`, `total`, `missing`), `friends` (hearts and max per villager), `monsters` (Marlon's goals: `kills` / `need`), `mine.deepestFloor`, `house`, `children`, `spouse` |

## Building on top of the mod

Scripts that play many days unattended (one AI session per day, memory between days, save backups, a crash watchdog,
OBS recording) are not part of this repository. Everything such a script needs is above: drive the farmers through
`/mcp/pN`, read `/api/state`, `/api/briefing`, `/api/stats`, `/api/memory` and `/api/relationships`, control the run
with `/api/control` and `/api/release_freeze`, and test with `/api/debug/*`. [recording.md](recording.md) and
[howto-record-day.md](howto-record-day.md) describe one recording setup as an example.

The API only grows: endpoints and tool names are added, never renamed or removed.

### Tools from other SMAPI mods

Another SMAPI mod can give the AI farmers its own tool through the mod API. Copy this interface into the mod, get it in
its `GameLaunched` handler with `Helper.ModRegistry.GetApi<IStardewMcpApi>("Duckxy.StardewMCP")`, and call `AddTool`:

```csharp
public interface IStardewMcpApi
{
    void AddTool(string name, string description, string inputSchemaJson, Func<string, string> run, bool instant, bool core);
    void AddMorningNote(Func<string> note);
    void AddItemHolder(Func<string, int> count, Func<string, string> howToUse);
    void SetStoragePays(bool pays);
    void LogChestChange(bool put, string item, int count, string location, int x, int y);
}
```

The last three came later: a mod that wants to work with an older Stardew MCP too asks for them in a second interface of
their own (SMAPI refuses an interface when one of its methods is missing).

- A tool added under a built-in tool's name (`chest`, say) takes that tool's place in `tools/list` and for the AIs'
  calls. The mod's own tools that use the built-in one inside (craft fetching ingredients, for example) keep using it.

- `name` is snake_case, and `inputSchemaJson` is the tool's JSON Schema: an object whose properties have types. A tool
  that acts (`instant: false`) needs a string `reason` property.
- `run` gets the arguments as JSON and returns the result text. It runs on the game thread with `Game1.player` set to
  the calling farmer, and must answer in that one call. To report a failure, throw: the exception's message is the result.
- `instant` tools only observe and keep the world frozen. `core` tools are sent whole in the compact tool profile
  (`BrainTools: compact`); the others are cut to one sentence.
- The tool is listed in `tools/list` on every seat, the built-in AI gets it too, and the tool contract is checked when it
  is added (problems go to the SMAPI log).
- `AddMorningNote` adds a line to every farmer's morning briefing (`/api/briefing` and the built-in AI's morning
  message). The function runs on the game thread with `Game1.player` set to the farmer being briefed; it returns "" for
  nothing that day, and one that throws is left out. A line of the note that starts with `DO THIS FIRST TODAY` goes to
  the top of the farm part of the briefing instead of its end.
- A `chest` tool added in place of the built-in one is taken to reach its storage from anywhere: the mod's own steps
  (craft fetching ingredients, the mine's stash and meals, community_center's bundle items, a missing main tool, the full-pack
  hint) call it where the farmer stands instead of walking to a chest, and the chests of every map count as the farmer's.
- `AddItemHolder` tells the advice about items the farmer keeps outside the pack and the chests (machines in a slot of your
  mod): `count` gets a qualified item id and returns how many `Game1.player` has there, `howToUse` the line that says where and
  how to use them. The farm plan then stops asking for another Furnace and says your line instead.
- `SetStoragePays(true)`: your mod pays building, house and tool upgrade materials out of the chests, so the farm plan stops
  asking the farmer to carry them.
- `LogChestChange` writes a change your mod made to the chests into the chest log of the farmer on this screen (`chests.jsonl`
  and the day log, kind `chest`), like the built-in chest tool's changes.

### POST /api/order (local customization)

Body: `{"target":"all","text":"Water the crops"}`. Target may also be a configured AI name or seat id. Requires a running built-in brain; it works with any `Screens`, with or without the dashboard panel. Returns `{"queued":N,"target":"all"}`; HTTP 400 if no matching brain accepts it. Empty text and text over 1000 characters are refused. This queues a user instruction before the next model request; it does not interrupt a request or action already in progress. Console equivalent: `mcp_order all Water the crops`.
