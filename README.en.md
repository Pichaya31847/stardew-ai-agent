<p align="right">🌐 <a href="README.md">ภาษาไทย</a> · <b>English</b></p>

<div align="center">

# 🌾 Stardew MCP: Local AI Edition

**Two AIs play long runs of Stardew Valley by themselves on a home GPU, aiming for every achievement.**

A [SMAPI](https://smapi.io) mod that runs an [MCP](https://modelcontextprotocol.io) server *inside the game*,
developed further from [duckxy166/stardew-ai-agent](https://github.com/duckxy166/stardew-ai-agent) with the author's permission.
This edition makes a **small local model** (Qwen3.5 9B on Ollama) play dozens of in-game days unattended.

[![Latest release](https://img.shields.io/github/v/release/Pichaya31847/stardew-ai-agent?style=flat-square&label=release&color=4caf50)](https://github.com/Pichaya31847/stardew-ai-agent/releases/latest)
[![Upstream](https://img.shields.io/badge/upstream-duckxy166%2Fstardew--ai--agent-555?style=flat-square&logo=github)](https://github.com/duckxy166/stardew-ai-agent)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
![Stardew Valley 1.6+](https://img.shields.io/badge/Stardew_Valley-1.6+-7cb342?style=flat-square)
![SMAPI 4.0+](https://img.shields.io/badge/SMAPI-4.0+-5c6bc0?style=flat-square)
![Ollama](https://img.shields.io/badge/Ollama-stardew--local-000?style=flat-square)

</div>

## Where it comes from

This project builds on **Stardew MCP** by [duckxy166](https://github.com/duckxy166/stardew-ai-agent), the mod that lets
Claude, GPT, Gemini or a local model walk, farm, fish, chat with villagers and make deals with each other on one farm
(see [EP.1 on YouTube](https://youtu.be/Drd5lWh3kFI), where three AIs end up harvesting each other's crops). The author
gave permission to develop it further. The original code is on the [`main`](https://github.com/Pichaya31847/stardew-ai-agent/tree/main)
branch; this edition's work is on `feature/new-AI`.

The original works very well with large API models. Left running for days with Qwen 9B on a 12 GB GPU, though, it ran
into trouble: the model misspelled Thai villager names and found nobody, the mines stopped on the first floor with a
full pack, seeds were bought on the last day of a season, no coop or animal was ever bought, and the context filled up
several times a day. This edition is built from the logs of several real runs (the latest: 62 in-game days, 19 hours).

## Compared with the original

| Area | Original (duckxy166, `main`) | This edition |
|---|---|---|
| Long-term goal | None: the AI plays from day to day | `goals`: every achievement (no Joja) measured from the game, the closest ones first, and `progress_pN.json` each night with what was shipped, cooked, crafted and caught |
| Small local models | Work through `BaseUrl`, but struggle with 70 tools | `BrainTools: "compact"` (full descriptions only for everyday tools), `BrainMemoryLines`, a shorter prompt, and the [`local/` kit](docs/local-ai.md) with the tuned `stardew-local` model |
| Tools | 71 | 80: adds `goals`, `mine`, `machine`, `fish_guide`, `upgrade_tool`, `upgrade_backpack`, `community_center`, `morning_chores`, `forage` |
| Language the AI reads | The game's language (a Thai game = Thai names that small models misspell) | Every name by its English key; dialogue, letters and menu options in English by key; separate thought/speech/work languages (`SpeechLanguage`, `WorkLanguage`); text drifting into Chinese is sent back |
| Mines | Walk to each ladder with `interact` | `mine(floors)`: the elevator to the deepest floor, rocks broken for the ladder, fights, chests, quartz and floor items picked up, the pack stored in a chest first, and a full pack no longer stops it |
| Fishing | Fishing works, but which fish lives where is unknown | `fish_guide`: every fish including the legendary ones, by place, season, time and weather; `fish` aims at the legendary spot |
| Geodes and museum | Every step by hand | `crack_geode` walks to Clint and cracks the stack, naming pieces the museum still lacks; `museum` donates everything it takes and collects the rewards |
| Machines | Fill each machine by hand | `machine`: list, load every idle machine, collect. Optional [Automate](https://www.nexusmods.com/stardewvalley/mods/1063) support: a chest touching a machine takes only that machine's inputs |
| Upgrades | Buy through Clint's menu by hand | `upgrade_tool` orders and picks up, `upgrade_backpack` in one call, and a savings plan (Coop → house upgrade → Barn) with the money, Wood and Stone still missing |
| Mornings | The AI often forgot gift boxes, mail and quest money | `morning_chores` does them before the first thought and plants seeds that still ripen this season |
| Chests | Owned; taking from another's chest is theft | Shared; every put and take is logged (`chests.jsonl`); `chest(action="all")` and the morning briefing show what each chest holds and who moved what |
| Buying and selling | Seeds that can't ripen can be bought; `ship` needs you on the Farm | `buy` refuses seeds that would die, `ship` walks to the Farm, crafting supplies are protected from sales, shop counters open by themselves |
| Going to bed early | No guard | Refused before bedtime, with the next real task (counting only your own crops) |
| Repeated failures | Loop guard on actions | Plus `RepeatBrake` in the built-in AI: a warning at the 3rd failure of a tool, refused from the 5th |
| Co-op festivals | Stuck waiting for the others | Waits up to 60 s for the ready check, tells the others, cancels if they aren't ready; leaving a festival works |
| Money | A shared wallet if the save has one | `SeparateWallets` switches to separate wallets overnight |
| Logs and stats | JSONL | Lossless gzip, `stats_NNN.json` with progress and events, `progress_pN.json`, `chests.jsonl` |
| Orders during a run | Through an MCP client | `OperatorDashboard`, `POST /api/order` and `mcp_order` for the built-in AIs |
| New farm | Fill in the character screen yourself | `PrefillNewGame` fills it in (names, farm, skip intro) |

**Not yet:** the arcade achievements (Prairie King, Fector's Challenge), the Joja route (left out on purpose), Nexus mods
must be downloaded by hand, the `local/` kit is Windows-only, and some new features have no smoke test yet (see [coverage](docs/coverage.md)).

## Highlights

- 🤖 **Plays most of the game by itself.** 80 tools for farming, fishing, mining and combat, cooking, crafting, machines,
  shopping, animals, buildings, festivals, quests, the Community Center, the museum, friendship, marriage and children.
  → [Everything it can do](docs/features.md)
- 🏆 **A goal**: every achievement, measured from the game, with the next thing to save up for.
- 💻 **Runs entirely at home**: one setup script + the `stardew-local` model (a tuned Qwen3.5 9B), no API bills.
- 👥 **Up to three AIs on one farm** in split-screen co-op, with or without you. Separate wallets, their own crops,
  shared chests with a log.
- 🗣️ **They only hear what is near them.** `say` reaches the farmers whose screen shows the speaker, `think` is private
  (viewers see it), and every AI sees what the others are *actually* doing next to what they said.
- ⏸️ **Time freezes while an AI thinks**, so a slow model loses no game time. Turn it off when you play along.
- 🧠 **Memory across days**: a diary, lessons, a plan, and a `self` note each AI rewrites every evening.
- 🎥 **Made for streams and videos**: an overlay page for OBS, a day-by-day replay, logs, diaries and daily stats.
- 🔌 **Use any AI**: the built-in loop (Anthropic, OpenAI, Gemini, OpenRouter, Ollama, LM Studio), any MCP client
  (Claude Code, Claude Desktop, Cursor...), or plain HTTP.

## Three ways to use it

| You want to... | Use | You need |
|---|---|---|
| Watch AIs play your farm, or play alongside them | The mod's **built-in AI**: set it up in the config menu | A local model (the [`local/` kit](docs/local-ai.md)) or an API key (Anthropic, OpenAI, Gemini) |
| Give orders to your own AI assistant ("water the crops, then visit Abigail") | Any **MCP client** (Claude Code, Claude Desktop, Cursor...) connected to `http://localhost:8770/mcp` | That client |
| Build your own bot, stream overlay or automation | The **[HTTP API](docs/api.md)** (`/api/state`, `/api/control`, `/overlay`, `/replay`...) | Any language that speaks HTTP |

## Install

### Option 1: local model (recommended)

You need Stardew Valley 1.6 (PC) and a GPU with about 10 GB of VRAM. Download this repository and run

```bash
powershell -ExecutionPolicy Bypass -File local\setup.ps1
```

It finds the game, installs SMAPI, the mod and Ollama, and builds the `stardew-local` model. Then start with
`local\start.cmd`. Everything else: [Playing with a local model](docs/local-ai.md).

### Option 2: the mod only (API keys)

You need Stardew Valley 1.6+ (PC) and [SMAPI](https://smapi.io) 4.0+.
[Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) is optional and lets you change the
settings in the game.

1. Install SMAPI and start the game through it once.
2. Download the latest `StardewMCP` zip from the [Releases](https://github.com/Pichaya31847/stardew-ai-agent/releases) page.
3. Unzip it into `Stardew Valley/Mods/`, so you get `Mods/StardewMCP/`.
4. Start the game through SMAPI. The SMAPI console should show `Agent MCP at http://localhost:8770/mcp`.

The first start creates `Mods/StardewMCP/config.json`. With Generic Mod Config Menu you can also change the settings
in the game: title screen → the cog → *Stardew MCP*.

## Quick start

### Let the AI play by itself

1. Open the mod's settings (in the game, or in `config.json`).
2. Give seat **P1** a provider (`Brain`), a model and your API key:

   | `Brain` | Models | Key / endpoint |
   |---|---|---|
   | `openai` + Ollama | `stardew-local` (from the `local/` kit) | `BaseUrl`: `http://127.0.0.1:11434/v1`, no key |
   | `anthropic` | `claude-haiku-5-5` (default), `claude-sonnet-5-5`, ... | [console.anthropic.com](https://console.anthropic.com) |
   | `openai` | `gpt-5-mini` (default), `gpt-5`, ... | [platform.openai.com](https://platform.openai.com), or any OpenAI-compatible server with a `BaseUrl`: OpenRouter, LM Studio |
   | `gemini` | `gemini-3.8-flash` (default), ... | [aistudio.google.com](https://aistudio.google.com) |
   | `none` | | no built-in AI: an MCP client drives the seat |

3. Load any save. The AI starts playing, and a small panel on its screen shows its thoughts, chat and current action.

```json
"Seats": [
  { "Role": "ai", "Brain": "openai", "Model": "stardew-local", "BaseUrl": "http://127.0.0.1:11434/v1" }
]
```

### Several AIs, or you plus AIs

Turn on `SplitScreen`: up to four screens share the farm in the game's 2×2 grid. Each seat gets a `Role`: `ai`,
`human` (you, P1 only), `camera` (just watches) or `off`. If the farm has too few cabins, the mod builds them and
joins the farmhands by itself, so you don't need gamepads. The setup used for the real runs (two AIs on the local
model plus a camera screen) is [`local/config.example.json`](local/config.example.json).

`FreezeTimeWhileThinking` on = the world stops while an AI thinks · off = time keeps running, for playing along.
Every setting: [Configuration](docs/configuration.md).

### Connect your own AI client

With the game running and a save loaded, add the mod as an MCP server. For [Claude Code](https://claude.com/claude-code):

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

In split-screen each farmer has its own endpoint: `/mcp/p1` (same as `/mcp`), `/mcp/p2`, `/mcp/p3`. Give the seats you
drive this way `"Brain": "none"`.

### Pause

Press `F8` in the game window: the running action finishes, then the whole world freezes. `F8` again continues.
Stopping after the day and other run controls: [HTTP API](docs/api.md#run-control).

## Good to know

- **Cost.** A local model is free but works the GPU hard (an RTX 5070 draws about 250 W at 72 °C). With an API, one
  in-game day costs roughly 50–300k input tokens per AI; `DailyTokenBudget` stops a seat once it has used that many.
- **Language.** An English game works best for small models; thoughts and speech can still be Thai for viewers with
  `ThoughtLanguage` / `SpeechLanguage`.
- **Your keys** are stored as plain text in `config.json`. Don't share that file.
- **How the AI plays** is set by its system prompt, `Mods/StardewMCP/assets/system_prompt.md`.
- **Automate** (optional): turn its `ShippingBin` off, or a chest beside the shipping bin gets sold (`setup.ps1` does it).
- **Performance.** Three or four screens plus a local model need a mid-range GPU or better.

## Documentation

| Page | What's inside |
|---|---|
| 💻 [Local model](docs/local-ai.md) | The `local/` kit, the tuned `stardew-local` model, publishing it |
| 📋 [What it can do](docs/features.md) | Every feature, area by area, and what is still missing |
| 🧰 [Tools](docs/tools.md) | All MCP tools with their arguments, grouped by job |
| 🌐 [HTTP API](docs/api.md) | Endpoints, run control, tool results and reason codes, log files |
| ⚙️ [Configuration](docs/configuration.md) | Every `config.json` setting, seats and roles, overlay language |
| 💰 [Economy](docs/economy.md) | Wallets, ownership, shared chests |
| 🗺️ [Game coverage](docs/coverage.md) | How each game system is reached, tested or not yet |
| 📖 [Glossary](CONTEXT.md) | The words this project uses: seat, brain, freeze, interrupt... |
| 🤝 [Contributing](CONTRIBUTING.md) | Bug reports, tests, adding a tool |

## Troubleshooting

| Problem | Try |
|---|---|
| The SMAPI console doesn't show `Agent MCP at ...` | Check that the folder is `Mods/StardewMCP/` (not nested twice) and read the SMAPI log for errors |
| The AI never starts | Read the SMAPI console after loading the save: a seat that can't start logs `pN: built-in AI not started: <problem>`. `mcp_brain` lists the AIs that run |
| `HTTP 500: XML syntax error` in the log | Ollama sometimes can't parse a Qwen tool call; the mod retries by itself |
| The game seems stuck and the clock doesn't move | It is probably frozen while an AI thinks. `/api/state` shows who is thinking. An AI silent longer than `ThinkTimeoutSeconds` stops holding the world |
| The AI repeats the same failing action | The loop guard and `RepeatBrake` add hints and finally block the repeat (`RepeatHintAfter`, `RepeatBlockAfter`, `NoProgressAfter`) |
| Port 8770 is already in use | Change `HttpPort` and use the new port in your MCP client's URL |
| Farmhands don't join in split-screen | Allow the Windows Firewall prompt, and keep `AutoBuildCabins` on |

## Build from source

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) finds your game folder, copies
the mod into `Mods/` and creates a release zip in `bin/`. The tests that run without the game are in
[CONTRIBUTING.md](CONTRIBUTING.md#tests). `local\setup.ps1 -BuildFromSource` builds and installs in one step.

## Credits

- **[duckxy166/stardew-ai-agent](https://github.com/duckxy166/stardew-ai-agent)** by Duckxy: the original of this whole mod
  (the in-game MCP server, split-screen, the built-in AI, perception, the overlay and replay, and the first 70 tools).
  This edition is developed further with permission. Follow the series on YouTube [@LDuckxy](https://www.youtube.com/@LDuckxy)
  and support the original author through [GitHub Sponsors](https://github.com/sponsors/duckxy166).
- **[Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)**: the first bridge between Stardew Valley
  and MCP, which the original was forked from before it was rewritten.
- **[Automate](https://www.nexusmods.com/stardewvalley/mods/1063)** by Pathoschild: optional support; none of its code is in this repository.
- The **Qwen3.5** model (Apache 2.0) through **[Ollama](https://ollama.com)**.

## License

[MIT](LICENSE), like the original. Stardew Valley is a trademark of ConcernedApe; this project is a fan-made mod and
is not affiliated with ConcernedApe. No game files are included.
