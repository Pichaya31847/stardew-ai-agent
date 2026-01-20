using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

/// <summary>Executes commands received from the WebSocket server.</summary>
public class CommandExecutor
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ConcurrentQueue<GameCommand> _commandQueue = new();
    private readonly Pathfinder _pathfinder = new();

    // Movement state - now uses a calculated path
    private List<Vector2>? _currentPath;
    private int _pathIndex;
    private Vector2? _finalTarget;
    private Action<CommandResponse>? _moveCallback;
    private string? _moveCommandId;
    private int _stuckCounter;

    // Tool use state - for repeated tool usage
    private int _toolUseCount;
    private int _toolUseRemaining;
    private int _toolUseCooldown;
    private const int ToolUseCooldownTicks = 30; // ~0.5 seconds between swings at 60fps
    private Action<CommandResponse>? _toolCallback;
    private string? _toolCommandId;
    private string? _toolName;

    // Hold tool state - for charged tool usage (watering can, hoe)
    private int _holdToolTicks;
    private int _holdToolRemaining;
    private Action<CommandResponse>? _holdToolCallback;
    private string? _holdToolCommandId;
    private string? _holdToolName;
    private bool _holdToolReleased;

    public CommandExecutor(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    // Public properties for movement state (used by GameStateSerializer)
    public bool IsMoving => _currentPath != null && _pathIndex < _currentPath.Count;
    public Vector2? MovementTarget => _finalTarget;
    public int? PathLength => _currentPath?.Count;
    public int? PathProgress => _currentPath != null ? _pathIndex : null;

    /// <summary>Extract an integer from a parameter that may be a JsonElement.</summary>
    private int GetIntParam(object obj, int defaultValue = 0)
    {
        if (obj is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : defaultValue;
        }
        return Convert.ToInt32(obj);
    }

    /// <summary>Extract a string from a parameter that may be a JsonElement.</summary>
    private string GetStringParam(object? obj, string defaultValue = "")
    {
        if (obj is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? defaultValue : defaultValue;
        }
        return obj?.ToString() ?? defaultValue;
    }

    /// <summary>Queue a command for execution on the game thread.</summary>
    public CommandResponse QueueCommand(GameCommand command)
    {
        _monitor.Log($"Queuing command: {command.Action}", LogLevel.Debug);
        _commandQueue.Enqueue(command);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Command queued"
        };
    }

    /// <summary>Process pending commands and continuous movement (called from game loop every tick).</summary>
    public void ProcessPendingCommands()
    {
        // Process new commands (only if not busy with tool use)
        if (_toolUseRemaining == 0 && _commandQueue.TryDequeue(out var command))
        {
            ExecuteCommand(command);
        }

        // Continue movement if we have a target
        ProcessMovement();

        // Continue tool use if active
        ProcessToolUse();

        // Continue held tool if active
        ProcessHoldTool();
    }

    /// <summary>Set cursor position to the tile in front of the player based on facing direction.</summary>
    private void SetCursorToFacingTile()
    {
        var player = Game1.player;
        int tileX = (int)player.Tile.X;
        int tileY = (int)player.Tile.Y;

        // Calculate tile in front based on facing direction
        // 0 = up, 1 = right, 2 = down, 3 = left
        switch (player.FacingDirection)
        {
            case 0: tileY--; break; // up
            case 1: tileX++; break; // right
            case 2: tileY++; break; // down
            case 3: tileX--; break; // left
        }

        // Convert tile to screen pixel position (center of tile)
        // Tiles are 64x64 pixels, add 32 to get center
        int pixelX = (tileX * 64) + 32 - Game1.viewport.X;
        int pixelY = (tileY * 64) + 32 - Game1.viewport.Y;

        Game1.setMousePosition(pixelX, pixelY);
    }

    /// <summary>Process repeated tool usage.</summary>
    private void ProcessToolUse()
    {
        if (_toolUseRemaining <= 0)
            return;

        // Decrement cooldown
        if (_toolUseCooldown > 0)
        {
            _toolUseCooldown--;
            return;
        }

        // Check if player can act (not in animation)
        var player = Game1.player;
        if (player.UsingTool || !player.CanMove)
        {
            return;
        }

        // Set cursor to tile in front so tool swings in correct direction
        SetCursorToFacingTile();

        // Use the tool
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);
        _toolUseRemaining--;
        _toolUseCooldown = ToolUseCooldownTicks;

        _monitor.Log($"Tool swing {_toolUseCount - _toolUseRemaining}/{_toolUseCount}", LogLevel.Debug);

        // Check if done
        if (_toolUseRemaining <= 0)
        {
            _monitor.Log($"Completed {_toolUseCount} tool uses", LogLevel.Debug);

            // player variable already in scope from line 143
            _toolCallback?.Invoke(new CommandResponse
            {
                Id = _toolCommandId ?? "",
                Success = true,
                Message = $"Used {_toolName} {_toolUseCount} times. VERIFY tile-in-front to confirm target was affected!",
                Data = new Dictionary<string, object>
                {
                    ["swings"] = _toolUseCount,
                    ["tool"] = _toolName ?? "Unknown",
                    ["energy"] = player.Stamina,
                    ["note"] = "Read tile-in-front to verify the target (tree/rock/etc) was destroyed or changed"
                }
            });

            ClearToolState();
        }
    }

    /// <summary>Process held tool charging (watering can, hoe).</summary>
    private void ProcessHoldTool()
    {
        if (_holdToolRemaining <= 0)
            return;

        var player = Game1.player;
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        // Keep pressing the button every tick to simulate holding
        if (!_holdToolReleased)
        {
            // Set cursor to tile in front so tool aims in correct direction
            SetCursorToFacingTile();

            // Press every tick to simulate holding
            _helper.Input.Press(useButton);
            _holdToolRemaining--;

            // Check if we should stop (release)
            if (_holdToolRemaining <= 0)
            {
                _holdToolReleased = true;
                // No explicit release needed - just stop pressing

                _monitor.Log($"Released {_holdToolName} after {_holdToolTicks} ticks", LogLevel.Debug);

                _holdToolCallback?.Invoke(new CommandResponse
                {
                    Id = _holdToolCommandId ?? "",
                    Success = true,
                    Message = $"Used {_holdToolName} with {_holdToolTicks} tick charge",
                    Data = new Dictionary<string, object>
                    {
                        ["ticks"] = _holdToolTicks,
                        ["tool"] = _holdToolName ?? "Unknown"
                    }
                });

                ClearHoldToolState();
            }
        }
    }

    /// <summary>Clear hold tool state.</summary>
    private void ClearHoldToolState()
    {
        _holdToolTicks = 0;
        _holdToolRemaining = 0;
        _holdToolCallback = null;
        _holdToolCommandId = null;
        _holdToolName = null;
        _holdToolReleased = false;
    }

    /// <summary>Clear tool use state.</summary>
    private void ClearToolState()
    {
        _toolUseCount = 0;
        _toolUseRemaining = 0;
        _toolUseCooldown = 0;
        _toolCallback = null;
        _toolCommandId = null;
        _toolName = null;
    }

    /// <summary>Process continuous movement along calculated path.</summary>
    private void ProcessMovement()
    {
        if (_currentPath == null || _pathIndex >= _currentPath.Count)
            return;

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);

        // Get current target tile in path
        var targetTile = _currentPath[_pathIndex];

        // Check if arrived at current waypoint
        if (currentPos == targetTile)
        {
            _pathIndex++;
            _stuckCounter = 0;

            // Check if we've completed the entire path
            if (_pathIndex >= _currentPath.Count)
            {
                _monitor.Log($"Arrived at final destination ({targetTile.X}, {targetTile.Y})", LogLevel.Debug);

                // Include full current state for verification
                var actualPos = Game1.player.Tile;
                _moveCallback?.Invoke(new CommandResponse
                {
                    Id = _moveCommandId ?? "",
                    Success = true,
                    Message = $"Arrived at ({(int)actualPos.X}, {(int)actualPos.Y}) in {Game1.currentLocation?.Name ?? "Unknown"}",
                    Data = new Dictionary<string, object>
                    {
                        ["arrived"] = true,
                        ["x"] = (int)actualPos.X,
                        ["y"] = (int)actualPos.Y,
                        ["location"] = Game1.currentLocation?.Name ?? "Unknown"
                    }
                });

                ClearMovementState();
                return;
            }

            // Move to next waypoint
            targetTile = _currentPath[_pathIndex];
        }

        // Check if stuck (same position for too long)
        _stuckCounter++;
        if (_stuckCounter > 120) // ~2 seconds at 60fps
        {
            _monitor.Log("Movement stuck, recalculating path...", LogLevel.Debug);
            RecalculatePath();
            _stuckCounter = 0;
            return;
        }

        // Calculate direction to next waypoint
        int dx = (int)(targetTile.X - currentPos.X);
        int dy = (int)(targetTile.Y - currentPos.Y);

        SButton? moveButton = null;
        if (dx > 0) moveButton = GetMoveButton("right");
        else if (dx < 0) moveButton = GetMoveButton("left");
        else if (dy > 0) moveButton = GetMoveButton("down");
        else if (dy < 0) moveButton = GetMoveButton("up");

        if (moveButton.HasValue)
        {
            _helper.Input.Press(moveButton.Value);
        }
    }

    /// <summary>Recalculate path when stuck or blocked.</summary>
    private void RecalculatePath()
    {
        if (_finalTarget == null)
        {
            ClearMovementState();
            return;
        }

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);

        var newPath = _pathfinder.FindPath(Game1.currentLocation, currentPos, _finalTarget.Value);

        if (newPath == null || newPath.Count == 0)
        {
            _monitor.Log("Cannot find path to destination", LogLevel.Warn);
            _moveCallback?.Invoke(new CommandResponse
            {
                Id = _moveCommandId ?? "",
                Success = false,
                Message = "Path blocked - cannot reach destination",
                Data = new Dictionary<string, object>
                {
                    ["arrived"] = false,
                    ["x"] = (int)currentPos.X,
                    ["y"] = (int)currentPos.Y
                }
            });
            ClearMovementState();
            return;
        }

        _currentPath = newPath;
        _pathIndex = 0;
        _monitor.Log($"Path recalculated: {newPath.Count} tiles", LogLevel.Debug);
    }

    /// <summary>Clear all movement state.</summary>
    private void ClearMovementState()
    {
        _currentPath = null;
        _pathIndex = 0;
        _finalTarget = null;
        _moveCallback = null;
        _moveCommandId = null;
        _stuckCounter = 0;
    }

    private void ExecuteCommand(GameCommand command)
    {
        _monitor.Log($"Executing command: {command.Action}", LogLevel.Debug);

        try
        {
            var result = command.Action.ToLower() switch
            {
                "move_to" => ExecuteMoveTo(command),
                "stop" => ExecuteStop(command),
                "interact" => ExecuteInteract(command),
                "use_tool" => ExecuteUseTool(command),
                "use_tool_repeat" => ExecuteUseToolRepeat(command),
                "hold_tool" => ExecuteHoldTool(command),
                "switch_tool" => ExecuteSwitchTool(command),
                "face_direction" => ExecuteFaceDirection(command),
                "place_item" => ExecutePlaceItem(command),
                "eat_item" => ExecuteEatItem(command),
                "select_item" => ExecuteSelectItem(command),
                "get_state" => ExecuteGetState(command),
                _ => new CommandResponse
                {
                    Id = command.Id,
                    Success = false,
                    Message = $"Unknown action: {command.Action}"
                }
            };

            // For use_tool_repeat, hold_tool - don't invoke callback immediately - wait for completion
            // Note: move_to is now non-blocking - returns immediately, AI polls player/status to check progress
            var asyncActions = new[] { "use_tool_repeat", "hold_tool" };
            if (!asyncActions.Contains(command.Action.ToLower()))
            {
                command.OnComplete?.Invoke(result);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"Command failed: {ex.Message}", LogLevel.Error);
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = ex.Message
            });
        }
    }

    private CommandResponse ExecuteMoveTo(GameCommand command)
    {
        if (!command.Params.TryGetValue("x", out var xObj) ||
            !command.Params.TryGetValue("y", out var yObj))
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing x or y parameter"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "Missing x or y parameter" };
        }

        int targetX = GetIntParam(xObj);
        int targetY = GetIntParam(yObj);

        var player = Game1.player;
        var currentPos = new Vector2((int)player.Tile.X, (int)player.Tile.Y);
        var targetPos = new Vector2(targetX, targetY);

        // Check if already at destination
        if (currentPos == targetPos)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = true,
                Message = "Already at destination",
                Data = new Dictionary<string, object>
                {
                    ["arrived"] = true,
                    ["x"] = targetX,
                    ["y"] = targetY
                }
            });
            return new CommandResponse { Id = command.Id, Success = true, Message = "Already at destination" };
        }

        // Calculate path using A*
        var path = _pathfinder.FindPath(Game1.currentLocation, currentPos, targetPos);

        if (path == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No path found to ({targetX}, {targetY})"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = $"No path found to ({targetX}, {targetY})" };
        }

        // Set up movement along the calculated path
        _currentPath = path;
        _pathIndex = 0;
        _finalTarget = targetPos;
        _moveCallback = null; // Non-blocking: don't store callback, AI polls status instead
        _moveCommandId = null;
        _stuckCounter = 0;

        _monitor.Log($"Path found: {path.Count} tiles from ({currentPos.X}, {currentPos.Y}) to ({targetX}, {targetY})", LogLevel.Info);

        // Return immediately - movement continues in background
        // AI should poll player/status to check isMoving and position
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Movement started to ({targetX}, {targetY}) via {path.Count} tiles. Poll player/status to check progress.",
            Data = new Dictionary<string, object>
            {
                ["started"] = true,
                ["targetX"] = targetX,
                ["targetY"] = targetY,
                ["pathLength"] = path.Count,
                ["note"] = "Movement is non-blocking. Read player/status to check isMoving and current position."
            }
        };
    }

    private CommandResponse ExecuteStop(GameCommand command)
    {
        ClearMovementState();

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "Movement stopped"
        };
    }

    private SButton? GetMoveButton(string direction)
    {
        var options = Game1.options;
        return direction.ToLower() switch
        {
            "up" => options.moveUpButton.Length > 0 ? options.moveUpButton[0].ToSButton() : SButton.W,
            "down" => options.moveDownButton.Length > 0 ? options.moveDownButton[0].ToSButton() : SButton.S,
            "left" => options.moveLeftButton.Length > 0 ? options.moveLeftButton[0].ToSButton() : SButton.A,
            "right" => options.moveRightButton.Length > 0 ? options.moveRightButton[0].ToSButton() : SButton.D,
            _ => null
        };
    }

    private CommandResponse ExecuteInteract(GameCommand command)
    {
        // Simulate action button press
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        var player = Game1.player;
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Interaction triggered at ({(int)player.Tile.X}, {(int)player.Tile.Y}) in {Game1.currentLocation?.Name ?? "Unknown"}. VERIFY the expected result occurred!",
            Data = new Dictionary<string, object>
            {
                ["x"] = (int)player.Tile.X,
                ["y"] = (int)player.Tile.Y,
                ["location"] = Game1.currentLocation?.Name ?? "Unknown",
                ["note"] = "Read game state to verify interaction had the expected effect (e.g., world/time for sleeping)"
            }
        };
    }

    private CommandResponse ExecuteUseTool(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            };
        }

        // Set cursor to tile in front so tool swings in correct direction
        SetCursorToFacingTile();

        // Simulate use tool button press
        var useButton = Game1.options.useToolButton.Length > 0
            ? Game1.options.useToolButton[0].ToSButton()
            : SButton.MouseLeft;

        _helper.Input.Press(useButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Used tool: {player.CurrentTool.Name}"
        };
    }

    private CommandResponse ExecuteUseToolRepeat(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "No tool equipped" };
        }

        // Get count parameter (default to 1)
        int count = 1;
        if (command.Params.TryGetValue("count", out var countObj))
        {
            count = GetIntParam(countObj, 1);
        }

        // Clamp count to reasonable range
        count = Math.Clamp(count, 1, 100);

        // Set up repeated tool use state
        _toolUseCount = count;
        _toolUseRemaining = count;
        _toolUseCooldown = 0; // Start immediately
        _toolCallback = command.OnComplete;
        _toolCommandId = command.Id;
        _toolName = player.CurrentTool.Name;

        _monitor.Log($"Starting {count} uses of {_toolName}", LogLevel.Info);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Using {_toolName} {count} times"
        };
    }

    private CommandResponse ExecuteSwitchTool(GameCommand command)
    {
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);

        if (slot < 0 || slot > 11)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Slot must be between 0 and 11"
            };
        }

        Game1.player.CurrentToolIndex = slot;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Switched to slot {slot}"
        };
    }

    private CommandResponse ExecuteGetState(GameCommand command)
    {
        // This is handled by the WebSocket server directly
        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "State retrieved"
        };
    }

    private CommandResponse ExecuteFaceDirection(GameCommand command)
    {
        if (!command.Params.TryGetValue("direction", out var dirObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing direction parameter"
            };
        }

        string direction = GetStringParam(dirObj).ToLower();
        var player = Game1.player;

        int facingDirection = direction switch
        {
            "up" => 0,
            "right" => 1,
            "down" => 2,
            "left" => 3,
            _ => -1
        };

        if (facingDirection == -1)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"Invalid direction: {direction}. Use up, down, left, or right."
            };
        }

        player.FacingDirection = facingDirection;
        player.FarmerSprite.StopAnimation();

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Now facing {direction}",
            Data = new Dictionary<string, object>
            {
                ["direction"] = direction,
                ["facingDirection"] = facingDirection
            }
        };
    }

    private CommandResponse ExecutePlaceItem(GameCommand command)
    {
        var player = Game1.player;

        // Optionally switch to a specific slot first
        if (command.Params.TryGetValue("slot", out var slotObj))
        {
            int slot = GetIntParam(slotObj);
            if (slot >= 0 && slot <= 11)
            {
                player.CurrentToolIndex = slot;
            }
        }

        var currentItem = player.CurrentItem;
        if (currentItem == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No item selected to place"
            };
        }

        // Get the tile in front of the player
        var tileInFront = player.GetToolLocation() / 64f;
        int tileX = (int)tileInFront.X;
        int tileY = (int)tileInFront.Y;
        var location = Game1.currentLocation;

        // Try to place the object
        // For seeds/fertilizer on tilled soil, use the action button
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Attempted to place {currentItem.Name} at ({tileX}, {tileY})",
            Data = new Dictionary<string, object>
            {
                ["item"] = currentItem.Name,
                ["x"] = tileX,
                ["y"] = tileY
            }
        };
    }

    private CommandResponse ExecuteHoldTool(GameCommand command)
    {
        var player = Game1.player;

        if (player.CurrentTool == null)
        {
            command.OnComplete?.Invoke(new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "No tool equipped"
            });
            return new CommandResponse { Id = command.Id, Success = false, Message = "No tool equipped" };
        }

        // Get duration parameter (default to 60 ticks = 1 second)
        int ticks = 60;
        if (command.Params.TryGetValue("ticks", out var ticksObj))
        {
            ticks = GetIntParam(ticksObj, 60);
        }

        // Clamp ticks to reasonable range (1 second to 3 seconds)
        ticks = Math.Clamp(ticks, 30, 180);

        // Set up hold tool state
        _holdToolTicks = ticks;
        _holdToolRemaining = ticks;
        _holdToolCallback = command.OnComplete;
        _holdToolCommandId = command.Id;
        _holdToolName = player.CurrentTool.Name;
        _holdToolReleased = false;

        _monitor.Log($"Starting {ticks} tick hold of {_holdToolName}", LogLevel.Info);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Holding {_holdToolName} for {ticks} ticks"
        };
    }

    private CommandResponse ExecuteEatItem(GameCommand command)
    {
        var player = Game1.player;

        // Get slot parameter
        if (!command.Params.TryGetValue("slot", out var slotObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing slot parameter"
            };
        }

        int slot = GetIntParam(slotObj);
        if (slot < 0 || slot > 35)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Slot must be between 0 and 35"
            };
        }

        var item = player.Items[slot];
        if (item == null)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"No item in slot {slot}"
            };
        }

        // Check if item is edible
        if (item is not StardewValley.Object obj || obj.Edibility <= -300)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"{item.Name} is not edible"
            };
        }

        // Switch to the item and eat it
        int previousSlot = player.CurrentToolIndex;
        player.CurrentToolIndex = slot;

        // Press action button to eat
        var actionButton = Game1.options.actionButton.Length > 0
            ? Game1.options.actionButton[0].ToSButton()
            : SButton.MouseRight;

        _helper.Input.Press(actionButton);

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = $"Eating {item.Name}",
            Data = new Dictionary<string, object>
            {
                ["item"] = item.Name,
                ["edibility"] = obj.Edibility,
                ["slot"] = slot
            }
        };
    }

    private CommandResponse ExecuteSelectItem(GameCommand command)
    {
        if (!command.Params.TryGetValue("name", out var nameObj))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = "Missing name parameter"
            };
        }

        string itemName = GetStringParam(nameObj).ToLower();
        var player = Game1.player;

        // Search inventory for the item
        for (int i = 0; i < player.Items.Count; i++)
        {
            var item = player.Items[i];
            if (item != null && item.Name.ToLower().Contains(itemName))
            {
                player.CurrentToolIndex = i;
                return new CommandResponse
                {
                    Id = command.Id,
                    Success = true,
                    Message = $"Selected {item.Name} in slot {i}",
                    Data = new Dictionary<string, object>
                    {
                        ["item"] = item.Name,
                        ["slot"] = i,
                        ["stack"] = item.Stack
                    }
                };
            }
        }

        return new CommandResponse
        {
            Id = command.Id,
            Success = false,
            Message = $"No item matching '{itemName}' found in inventory"
        };
    }
}

#region Command Classes

public class GameCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Action { get; set; } = "";
    public Dictionary<string, object> Params { get; set; } = new();
    public Action<CommandResponse>? OnComplete { get; set; }
}

public class CommandResponse
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, object>? Data { get; set; }
}

#endregion
