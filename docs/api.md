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
| `lessons.md`, `plan.md`, `notes.md` | What `journal` keeps across days |
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
### POST /api/order (local customization)

Body: `{"target":"all","text":"Water the crops"}`. Target may also be a configured AI name or seat id. Requires `OperatorDashboard=true` and a running built-in brain. Returns `{"queued":N,"target":"all"}`; HTTP 400 if no matching brain accepts it. Empty text and text over 1000 characters are refused. This queues a user instruction before the next model request; it does not interrupt a request or action already in progress. Console equivalent: `mcp_order all Water the crops`.
