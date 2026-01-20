# Stardew Valley MCP Bridge

## Project Goal

Create a Model Context Protocol (MCP) server that allows an AI to play Stardew Valley by bridging the game with Claude or other MCP-compatible AI systems.

## Architecture Overview

```
┌─────────────────┐     WebSocket      ┌─────────────────┐     MCP/stdio     ┌─────────────────┐
│  Stardew Valley │ ◄────────────────► │   MCP Server    │ ◄───────────────► │   AI (Claude)   │
│   (SMAPI Mod)   │                    │      (Go)       │                    │                 │
│      C#         │                    │                 │                    │                 │
└─────────────────┘                    └─────────────────┘                    └─────────────────┘
```

### Components

1. **SMAPI Mod (C#)** - Runs inside Stardew Valley
   - Monitors game state via `GameLoop` events
   - Simulates player actions via `InputHelper`
   - Hosts a WebSocket server for external communication
   - Exposes game data as JSON

2. **MCP Server (Go)** - The middleware
   - Connects to the SMAPI mod via WebSocket
   - Exposes MCP Tools (actions) and Resources (game state) to the AI
   - Translates AI commands into game actions

3. **AI Client** - Claude or other MCP-compatible AI
   - Receives game state as structured data
   - Issues high-level commands to play the game

## MCP Interface Design

### Tools (Actions the AI can take)

| Tool | Parameters | Description |
|------|------------|-------------|
| `move_to` | `x: int, y: int` | Pathfind and move player to tile coordinates |
| `interact` | `direction: string` | Press action button (talk, harvest, check mail) |
| `use_tool` | `tool_id: string, target_x: int, target_y: int` | Select and use a tool on a specific tile |
| `manage_inventory` | `item_id: string, action: string` | Trash, move, or consume items |
| `switch_tool` | `tool_name: string` | Switch to a specific tool in inventory |

### Resources (Information the AI can read)

| Resource URI | Description |
|--------------|-------------|
| `stardew://world/map` | JSON representation of nearby tiles, crops, objects |
| `stardew://player/status` | Current energy, health, money, time of day, location |
| `stardew://player/inventory` | List of items currently held |
| `stardew://world/npcs` | Nearby NPCs and their locations |
| `stardew://world/time` | Current season, day, time, weather |

### Prompts (Pre-built instruction templates)

| Prompt | Description |
|--------|-------------|
| `daily_farm_check` | Identify and water unwatered crops |
| `forage_area` | Scan and collect all forageable items nearby |
| `social_visit` | Find and talk to NPCs to build relationships |

## SMAPI APIs to Use

- **`IMonitor`** - Logging
- **`IModHelper`** - Access game state and helpers
- **`Helper.Events.GameLoop.UpdateTicked`** - Main game loop hook (60fps)
- **`Game1.player`** - Player character state and position
- **`Game1.currentLocation`** - Current map, objects, NPCs, terrain

## Technical Challenges

1. **Latency** - LLMs are slow vs 60fps gameplay
   - Solution: AI issues high-level commands, mod executes autonomously
   - Consider "pause and plan" mode for complex decisions

2. **Pathfinding** - Stardew has no built-in A* for players
   - Solution: Implement pathfinding in the SMAPI mod
   - Consider using `Pathoschild.Stardew.Common` or similar

3. **State Serialization** - Efficiently represent game world as JSON
   - Only send relevant/nearby data to reduce payload size
   - Cache and diff to minimize data transfer

## Project Structure

```
stardew-mcp/
├── CLAUDE.md           # This file
├── mod/                # SMAPI Mod (C#)
│   ├── StardewMCP/
│   │   ├── ModEntry.cs
│   │   ├── WebSocketServer.cs
│   │   ├── GameStateSerializer.cs
│   │   └── CommandExecutor.cs
│   └── StardewMCP.sln
└── mcp-server/         # MCP Server (Go)
    ├── main.go
    ├── go.mod
    ├── tools/          # MCP tool implementations
    ├── resources/      # MCP resource handlers
    └── websocket/      # WebSocket client to SMAPI mod
```

## Development Steps

1. Set up SMAPI mod project following official guide
2. Implement WebSocket server in the mod
3. Create game state serialization (JSON export)
4. Implement command execution (movement, tools, interaction)
5. Build Go MCP server with WebSocket client
6. Define and implement MCP tools and resources
7. Test with Claude Desktop by adding to `claude_desktop_config.json`

## References

- [SMAPI Documentation](https://stardewvalleywiki.com/Modding:Index)
- [MCP Specification](https://modelcontextprotocol.io)
- [Creating SMAPI Mods](https://stardewvalleywiki.com/Modding:Modder_Guide/Get_Started)
