# Configuration

[← README](../README.md) · [Tools](tools.md) · [HTTP API](api.md)

All settings live in `Mods/StardewMCP/config.json`, created on the first start. With
[Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) you can also change them in the game
(title screen → the cog → *Stardew MCP*).

## Seats and roles

`Seats` lists who plays each screen, P1 to P4. P1 is the save's own farmer; P2–P4 are farmhands and only exist with
`SplitScreen` on.

| Role | Meaning |
|---|---|
| `ai` | An AI plays this farmer: the built-in AI (`Brain` + `ApiKey`), or an MCP client on `/mcp/pN` (`Brain: none`) |
| `human` | You, with keyboard and mouse (P1 only). Never frozen by the clock |
| `camera` | A parked farmer whose screen just watches, e.g. for a video. It neither sees nor is seen by the AIs |
| `off` | No screen |

An `ai` seat takes these fields:

| Field | Meaning |
|---|---|
| `Brain` | `anthropic`, `openai`, `gemini`, or `none` (an MCP client drives the seat) |
| `Model` | Model id; empty = the provider's default (`claude-haiku-5-5`, `gpt-5-mini`, `gemini-3.8-flash`) |
| `ApiKey` | Your key for that provider (stored as plain text) |
| `BaseUrl` | `openai` only: any OpenAI-compatible server, e.g. OpenRouter `https://openrouter.ai/api/v1`, Ollama `http://localhost:11434/v1` |
| `Name` | The farmer's name (wins over `FarmhandNames`) |

### Time modes

`FreezeTimeWhileThinking` decides what happens while an AI is between actions:

- **on** (default): the whole world (clock, farmers, villagers) stops while any AI thinks, so slow models cost no game time.
- **off**: the clock keeps running and the AIs keep up as well as they can. The natural choice when you play P1 yourself.

## All settings

| Setting | Default | Meaning |
|---|---|---|
| `Seats` | P1–P3 `ai` (no `Brain`), P4 `camera` | Who plays each screen (see above) |
| `ShowHud` | `true` | Show each AI's thoughts, chat and current action on its own screen |
| `MaxOutputTokens` | `4096` | Built-in AI: longest answer the model may write per turn |
| `DailyTokenBudget` | `0` | Built-in AI: tokens (input + output) per seat per day before it stops until the next morning (0 = no limit) |
| `HttpPort` | `8770` | Port for MCP, overlay, replay and the API |
| `FreezeTimeWhileThinking` | `true` | Stop the clock while no action is running |
| `LogDir` | `""` | Log folder; empty = `logs/` in the mod folder |
| `AutoLoadSave` | `""` | Save folder to load automatically from the title screen |
| `ThoughtLanguage` | `""` | Language of the AI's on-screen text (`think`, action reasons, diary), e.g. `"English"`; empty = the game's language. The overlay and replay follow it (see below) |
| `FarmBatchMinEnergy` | `10` | `water_all`, `harvest_all`, `clear_area`, `till_area` and `plant` area mode stop when energy drops below this (0 = only stop at 0) |
| `FishingAssist` | `"perfect"` | How `fish` plays the minigame: `"perfect"` keeps the fish inside the bar; `"natural"` presses the button like a player, so it can lose fish |
| `DefaultProfession` | `0` | Profession picked at bedtime when the AI hasn't chosen one: 0 = left option, 1 = right |
| `PlayHeartEvents` | `false` | Let heart events (story cutscenes) play and stop at their questions for `respond`, instead of skipping them. Experimental, see [coverage.md](coverage.md) |
| `RevealGiftTastes` | `false` | `villagers(name)` shows the game's real gift tastes. Off: a farmer only knows the tastes it found out by gifting |
| `RepeatHintAfter` | `3` | Loop guard: add a "try something different" hint when the identical call has failed this many times in a row (0 = off) |
| `RepeatBlockAfter` | `5` | Loop guard: refuse the Nth identical call in a row without running it when the N-1 before it failed (0 = off) |
| `NoProgressAfter` | `25` | Loop guard: add a "pick an action" hint after this many calls in a row with no game time, inventory or position change (0 = off) |
| `PauseKey` | `"F8"` | Key (an XNA `Keys` name) that toggles pause / resume while the game window has focus; empty = off |
| `EnableDebugApi` | `false` | Serve `/api/debug/*` test hooks for automated tests; leave off for normal play |
| `ChatSeconds` | `5` | Longest time an AI's `say` stays up, as the bubble above its head and its line in the chat box |

### Split-screen

| Setting | Default | Meaning |
|---|---|---|
| `SplitScreen` | `false` | Up to four screens share the farm in split-screen co-op |
| `AutoBuildCabins` | `true` | Build the cabins the farmhands need when the farm has too few (changes the save) |
| `FarmhandNames` | `["Gemini","GPT","Camera"]` | Farmhand names for P2, P3, P4 (a seat's `Name` wins); an existing farmhand with that name is reused |
| `ThinkTimeoutSeconds` | `300` | An AI silent for longer stops holding the world frozen (0 = never) |
| `BedtimeWarning` | `2200` | From this time an AI not in bed gets one warning that the others wait for it (0 = off) |
| `LockHostInput` | `true` | Ignore the real keyboard/mouse on P1 once a save is loaded, when P1 is an AI |
| `CleanFrames` | `true` | Frozen frames look live (no Paused banner, HUD kept), no cursors, gamepad hint or join messages |
| `AutoZoom` | `true` | Zoom each screen to about 20 tiles across |

The first time the game starts its co-op server, Windows Firewall may ask for permission once; allow it. For
separate wallets, create the co-op farm with "Separate wallets" on (see [economy.md](economy.md)).

### Long runs

For scripts that keep one MCP session open for a long time:

| Setting | Default | Meaning |
|---|---|---|
| `SessionBreakTimes` | `[1200,1800]` | From these game times a tool result carries `Session break:`, asking the AI to continue in a fresh session so its context stays small |
| `SessionBreakToolCalls` | `150` | Session break after this many tool calls in one session (0 = off) |
| `SessionBreakResultChars` | `120000` | Session break after this many characters of tool results in one session, about 30k tokens (0 = off) |
| `SessionBreaksForAll` | `false` | Session breaks for every MCP client; by default only clients that connect to `/mcp?breaks=1` |

## Language of the overlay and replay

`ThoughtLanguage` sets one language for everything viewers read. The tools ask the AI to write in it, and the overlay
and replay pick their labels from it (`?lang=English` on either page overrides it). `"Thai"` and `"English"` have full
UI tables; any other language (e.g. `"Japanese"`) gets thoughts in that language with an English UI. To add a table,
copy the `en` block in the `i18n` section of `mod/StardewMCP/assets/overlay.html` and `replay.html`.

If the game's font can't draw a script, its characters show as `·` on the in-game HUD unless a font mod adds them.

## The AI's behaviour

The built-in AI's system prompt is `Mods/StardewMCP/assets/system_prompt.md`. Edit it to change how the AIs play.
