# Contributing

Thanks for helping out! Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with:

- what you asked the AI to do and what happened instead,
- your SMAPI log (upload it at <https://smapi.io/log> and paste the link),
- game, SMAPI and mod versions.

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Build with `dotnet build` in `mod/StardewMCP` and try the change in game.
3. Keep changes focused; match the style of the surrounding code.
4. Describe what you changed and how you tested it.

## Adding an agent tool

Tools live in `mod/StardewMCP/Agent/AgentTools.cs`. Each tool is a `ToolDef` with a name,
description, JSON input schema and a coroutine that runs on the game thread. Mark a tool
`Instant` if it only reads state, so game time stays frozen while it runs.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
