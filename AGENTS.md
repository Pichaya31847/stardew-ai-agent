# Agent instructions

For coding agents working on this repository without Stardew Valley installed (Jules, Codex and the
like). `CLAUDE.md` has the full layout and design, `CONTRIBUTING.md` the PR rules.

## You cannot build the mod

`mod/StardewMCP` references the game and SMAPI assemblies through Pathoschild.Stardew.ModBuildConfig,
which needs the game installed. Don't run `dotnet build` or `dotnet restore` there, and don't add game
assemblies, stubs or reference packages to make it build. A change to a mod file the tests don't compile
can't be checked here: keep it small and mechanical, and say in the PR description that it needs a local
build.

## Checks

```
dotnet test mod/StardewMCP.Tests                       # .NET 6 SDK or newer
pip install pytest && python -m pytest -q mod/tests    # Python 3.11; test_i18n.py runs only with node
```

Both must pass before you open a PR. CI runs the same two jobs.

## What you can test

`mod/StardewMCP.Tests` does not reference the mod project. It compiles the mod's game-free files (no
`Game1`, `GameLocation`, `Farmer`, no `StardewValley` or `StardewModdingAPI` usings) through the
`<Compile Include Link>` list in its `.csproj`. To test more logic, move it into a game-free file of its
own, leave a one-line forwarder where it was, link the file, add a test and add the file to the
game-free list in `CLAUDE.md`.

## Don't break

- The HTTP API (`/api/*`, `/api/events`) and tool names: add to them, don't rename or remove.
- Tool schemas: snake_case names, a `reason` on every tool that isn't `Instant`, `required` lists only
  declared properties (`ToolContract`, `mod/tests/test_tool_sources.py`).
- Another repository parses this source by path: keep `Reasons` (with `Reasons.All`) in
  `mod/StardewMCP/Agent/ToolResult.cs` above `CallContext`, and keep tool definitions in the top-level
  files of `mod/StardewMCP/Agent/`.
- No game files, saves, logs or personal paths; no code copied from other mods unless their license
  allows it.

## Leave for a human with the game

New tools, gameplay changes and the 🟡 / ❌ rows of `docs/coverage.md` have to be tried in game. Don't
take them on unless the task says so.
