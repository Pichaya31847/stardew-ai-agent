package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/mark3labs/mcp-go/mcp"
	"github.com/mark3labs/mcp-go/server"
)

// GameClient manages the WebSocket connection to the Stardew Valley mod
type GameClient struct {
	conn        *websocket.Conn
	mu          sync.RWMutex
	state       *GameState
	responses   map[string]chan *WebSocketResponse
	responsesMu sync.Mutex
	connected   bool
	url         string
}

// GameState represents the current state of the game
type GameState struct {
	Player       PlayerState       `json:"player"`
	Time         TimeState         `json:"time"`
	World        WorldState        `json:"world"`
	Surroundings SurroundingsState `json:"surroundings"`
	Map          MapInfo           `json:"map"`
}

type PlayerState struct {
	Name                string          `json:"name"`
	X                   int             `json:"x"`
	Y                   int             `json:"y"`
	Location            string          `json:"location"`
	Energy              float64         `json:"energy"`
	MaxEnergy           int             `json:"maxEnergy"`
	Health              int             `json:"health"`
	MaxHealth           int             `json:"maxHealth"`
	Money               int             `json:"money"`
	CurrentTool         string          `json:"currentTool"`
	CurrentToolIndex    int             `json:"currentToolIndex"`
	FacingDirection     int             `json:"facingDirection"`
	FacingDirectionName string          `json:"facingDirectionName"`
	IsMoving            bool            `json:"isMoving"`
	CanMove             bool            `json:"canMove"`
	Inventory           []InventoryItem `json:"inventory"`
}

type TimeState struct {
	TimeOfDay           int    `json:"timeOfDay"`
	TimeString          string `json:"timeString"`
	Day                 int    `json:"day"`
	Season              string `json:"season"`
	Year                int    `json:"year"`
	DayOfWeek           string `json:"dayOfWeek"`
	IsNight             bool   `json:"isNight"`
	MinutesUntilMorning int    `json:"minutesUntilMorning"`
}

type WorldState struct {
	Weather             string `json:"weather"`
	IsOutdoors          bool   `json:"isOutdoors"`
	IsFarm              bool   `json:"isFarm"`
	IsGreenhouse        bool   `json:"isGreenhouse"`
	IsBuildableLocation bool   `json:"isBuildableLocation"`
	LocationType        string `json:"locationType"`
}

type SurroundingsState struct {
	NearbyTiles           []TileInfo           `json:"nearbyTiles"`
	NearbyObjects         []NearbyObject       `json:"nearbyObjects"`
	NearbyTerrainFeatures []NearbyTerrain      `json:"nearbyTerrainFeatures"`
	NearbyNPCs            []NearbyNPC          `json:"nearbyNPCs"`
	NearbyMonsters        []NearbyMonster      `json:"nearbyMonsters"`
	NearbyResourceClumps  []NearbyResourceClump `json:"nearbyResourceClumps"`
	NearbyDebris          []NearbyDebris       `json:"nearbyDebris"`
	NearbyBuildings       []NearbyBuilding     `json:"nearbyBuildings"`
	NearbyAnimals         []NearbyAnimal       `json:"nearbyAnimals"`
	WarpPoints            []WarpPoint          `json:"warpPoints"`
	TileInFront           TileInFront          `json:"tileInFront"`
}

type MapInfo struct {
	Name        string `json:"name"`
	DisplayName string `json:"displayName"`
	Width       int    `json:"width"`
	Height      int    `json:"height"`
	IsMineLevel bool   `json:"isMineLevel"`
	MineLevel   int    `json:"mineLevel"`
	UniqueId    string `json:"uniqueId"`
}

type TileInfo struct {
	X                 int    `json:"x"`
	Y                 int    `json:"y"`
	IsPassable        bool   `json:"isPassable"`
	IsWater           bool   `json:"isWater"`
	IsTillable        bool   `json:"isTillable"`
	HasObject         bool   `json:"hasObject"`
	HasTerrainFeature bool   `json:"hasTerrainFeature"`
	TileType          string `json:"tileType"`
}

type NearbyObject struct {
	X                int    `json:"x"`
	Y                int    `json:"y"`
	Name             string `json:"name"`
	DisplayName      string `json:"displayName"`
	Type             string `json:"type"`
	IsPassable       bool   `json:"isPassable"`
	CanBePickedUp    bool   `json:"canBePickedUp"`
	IsReadyForHarvest bool  `json:"isReadyForHarvest"`
	MinutesUntilReady int   `json:"minutesUntilReady"`
	HeldItemName     string `json:"heldItemName,omitempty"`
}

type NearbyTerrain struct {
	X                 int    `json:"x"`
	Y                 int    `json:"y"`
	Type              string `json:"type"`
	IsPassable        bool   `json:"isPassable"`
	GrowthStage       int    `json:"growthStage"`
	IsFullyGrown      bool   `json:"isFullyGrown"`
	HasSeed           bool   `json:"hasSeed"`
	CanBeChopped      bool   `json:"canBeChopped"`
	FruitCount        int    `json:"fruitCount"`
	IsWatered         bool   `json:"isWatered"`
	HasCrop           bool   `json:"hasCrop"`
	CropName          string `json:"cropName,omitempty"`
	CropPhase         int    `json:"cropPhase"`
	DaysUntilHarvest  int    `json:"daysUntilHarvest"`
	IsReadyForHarvest bool   `json:"isReadyForHarvest"`
	IsDead            bool   `json:"isDead"`
	GrassType         int    `json:"grassType"`
}

type NearbyNPC struct {
	X               int    `json:"x"`
	Y               int    `json:"y"`
	Name            string `json:"name"`
	DisplayName     string `json:"displayName"`
	IsFacingPlayer  bool   `json:"isFacingPlayer"`
	CanTalk         bool   `json:"canTalk"`
	FriendshipLevel int    `json:"friendshipLevel"`
	IsMoving        bool   `json:"isMoving"`
}

type NearbyMonster struct {
	X              int    `json:"x"`
	Y              int    `json:"y"`
	Name           string `json:"name"`
	Health         int    `json:"health"`
	MaxHealth      int    `json:"maxHealth"`
	DamageToFarmer int    `json:"damageToFarmer"`
	IsGlider       bool   `json:"isGlider"`
	Distance       int    `json:"distance"`
}

type NearbyResourceClump struct {
	X      int     `json:"x"`
	Y      int     `json:"y"`
	Width  int     `json:"width"`
	Height int     `json:"height"`
	Type   string  `json:"type"`
	Health float64 `json:"health"`
}

type NearbyDebris struct {
	X             int    `json:"x"`
	Y             int    `json:"y"`
	Name          string `json:"name"`
	Type          string `json:"type"`
	CanBePickedUp bool   `json:"canBePickedUp"`
}

type NearbyBuilding struct {
	X          int    `json:"x"`
	Y          int    `json:"y"`
	Width      int    `json:"width"`
	Height     int    `json:"height"`
	Type       string `json:"type"`
	DoorX      int    `json:"doorX"`
	DoorY      int    `json:"doorY"`
	HasAnimals bool   `json:"hasAnimals"`
}

type NearbyAnimal struct {
	X          int    `json:"x"`
	Y          int    `json:"y"`
	Name       string `json:"name"`
	Type       string `json:"type"`
	Age        int    `json:"age"`
	Happiness  int    `json:"happiness"`
	CanBePet   bool   `json:"canBePet"`
	HasProduce bool   `json:"hasProduce"`
}

type WarpPoint struct {
	X              int    `json:"x"`
	Y              int    `json:"y"`
	TargetLocation string `json:"targetLocation"`
	TargetX        int    `json:"targetX"`
	TargetY        int    `json:"targetY"`
	IsDoor         bool   `json:"isDoor"`
}

type TileInFront struct {
	X           int    `json:"x"`
	Y           int    `json:"y"`
	IsPassable  bool   `json:"isPassable"`
	IsWater     bool   `json:"isWater"`
	IsTillable  bool   `json:"isTillable"`
	ObjectName  string `json:"objectName,omitempty"`
	ObjectType  string `json:"objectType,omitempty"`
	TerrainType string `json:"terrainType,omitempty"`
	NPCName     string `json:"npcName,omitempty"`
	CanInteract bool   `json:"canInteract"`
}

type InventoryItem struct {
	Slot        int    `json:"slot"`
	Name        string `json:"name"`
	DisplayName string `json:"displayName"`
	Stack       int    `json:"stack"`
	Category    string `json:"category"`
	IsTool      bool   `json:"isTool"`
	IsWeapon    bool   `json:"isWeapon"`
}

type WebSocketMessage struct {
	ID     string                 `json:"id,omitempty"`
	Type   string                 `json:"type"`
	Action string                 `json:"action,omitempty"`
	Params map[string]interface{} `json:"params,omitempty"`
}

type WebSocketResponse struct {
	ID      string      `json:"id,omitempty"`
	Type    string      `json:"type"`
	Success bool        `json:"success"`
	Message string      `json:"message,omitempty"`
	Data    interface{} `json:"data,omitempty"`
}

var gameClient *GameClient

func NewGameClient() *GameClient {
	return &GameClient{
		responses: make(map[string]chan *WebSocketResponse),
	}
}

func (c *GameClient) Connect(url string) error {
	c.url = url
	conn, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		return fmt.Errorf("failed to connect to game: %w", err)
	}

	c.mu.Lock()
	c.conn = conn
	c.connected = true
	c.mu.Unlock()

	go c.listen()
	go c.keepAlive()

	return nil
}

func (c *GameClient) keepAlive() {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()

	for range ticker.C {
		c.mu.RLock()
		conn := c.conn
		connected := c.connected
		c.mu.RUnlock()

		if !connected || conn == nil {
			return
		}

		ping := WebSocketMessage{
			ID:   fmt.Sprintf("%d", time.Now().UnixNano()),
			Type: "ping",
		}
		data, _ := json.Marshal(ping)

		c.mu.Lock()
		err := conn.WriteMessage(websocket.TextMessage, data)
		c.mu.Unlock()

		if err != nil {
			log.Printf("Ping failed: %v", err)
			return
		}
	}
}

func (c *GameClient) reconnect() {
	c.mu.Lock()
	c.connected = false
	if c.conn != nil {
		c.conn.Close()
	}
	c.mu.Unlock()

	for {
		log.Printf("Attempting to reconnect...")
		time.Sleep(5 * time.Second)

		conn, _, err := websocket.DefaultDialer.Dial(c.url, nil)
		if err != nil {
			log.Printf("Reconnect failed: %v", err)
			continue
		}

		c.mu.Lock()
		c.conn = conn
		c.connected = true
		c.mu.Unlock()

		log.Println("Reconnected to Stardew Valley!")

		go c.listen()
		go c.keepAlive()
		return
	}
}

func (c *GameClient) listen() {
	for {
		c.mu.RLock()
		conn := c.conn
		c.mu.RUnlock()

		if conn == nil {
			return
		}

		_, message, err := conn.ReadMessage()
		if err != nil {
			log.Printf("WebSocket read error: %v", err)
			go c.reconnect()
			return
		}

		var response WebSocketResponse
		if err := json.Unmarshal(message, &response); err != nil {
			log.Printf("Failed to parse response: %v", err)
			continue
		}

		switch response.Type {
		case "state":
			c.handleStateUpdate(&response)
		case "response":
			c.handleCommandResponse(&response)
		case "pong":
			// Heartbeat response, ignore
		case "error":
			log.Printf("Error from game: %s", response.Message)
		}
	}
}

func (c *GameClient) handleStateUpdate(response *WebSocketResponse) {
	data, err := json.Marshal(response.Data)
	if err != nil {
		log.Printf("Failed to marshal state data: %v", err)
		return
	}

	var state GameState
	if err := json.Unmarshal(data, &state); err != nil {
		log.Printf("Failed to parse state: %v", err)
		return
	}

	c.mu.Lock()
	c.state = &state
	c.mu.Unlock()
}

func (c *GameClient) handleCommandResponse(response *WebSocketResponse) {
	if response.ID == "" {
		return
	}

	c.responsesMu.Lock()
	ch, ok := c.responses[response.ID]
	if ok {
		delete(c.responses, response.ID)
	}
	c.responsesMu.Unlock()

	if ok {
		ch <- response
	}
}

func (c *GameClient) GetState() *GameState {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.state
}

func (c *GameClient) IsConnected() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.connected
}

func (c *GameClient) SendCommand(action string, params map[string]interface{}) (*WebSocketResponse, error) {
	if !c.IsConnected() {
		return nil, fmt.Errorf("not connected to game")
	}

	id := fmt.Sprintf("%d", time.Now().UnixNano())

	msg := WebSocketMessage{
		ID:     id,
		Type:   "command",
		Action: action,
		Params: params,
	}

	ch := make(chan *WebSocketResponse, 1)
	c.responsesMu.Lock()
	c.responses[id] = ch
	c.responsesMu.Unlock()

	data, err := json.Marshal(msg)
	if err != nil {
		return nil, err
	}

	c.mu.Lock()
	err = c.conn.WriteMessage(websocket.TextMessage, data)
	c.mu.Unlock()

	if err != nil {
		c.responsesMu.Lock()
		delete(c.responses, id)
		c.responsesMu.Unlock()
		return nil, err
	}

	// Timeout for command responses
	// move_to is non-blocking, but use_tool_repeat can still take 30+ seconds
	select {
	case response := <-ch:
		return response, nil
	case <-time.After(60 * time.Second):
		c.responsesMu.Lock()
		delete(c.responses, id)
		c.responsesMu.Unlock()
		return nil, fmt.Errorf("timeout waiting for response")
	}
}

func main() {
	gameClient = NewGameClient()

	go func() {
		for {
			if err := gameClient.Connect("ws://localhost:8765/game"); err != nil {
				log.Printf("Failed to connect to game (will retry): %v", err)
				time.Sleep(5 * time.Second)
				continue
			}
			log.Println("Connected to Stardew Valley!")
			break
		}
	}()

	s := server.NewMCPServer(
		"stardew-mcp",
		"1.0.0",
		server.WithResourceCapabilities(true, false),
	)

	registerTools(s)
	registerResources(s)

	if err := server.ServeStdio(s); err != nil {
		log.Fatalf("Server error: %v", err)
	}
}

func registerTools(s *server.MCPServer) {
	// move_to tool
	moveToTool := mcp.NewTool("move_to",
		mcp.WithDescription("Move the player to specific tile coordinates using A* pathfinding"),
		mcp.WithNumber("x", mcp.Required(), mcp.Description("Target tile X coordinate")),
		mcp.WithNumber("y", mcp.Required(), mcp.Description("Target tile Y coordinate")),
	)
	s.AddTool(moveToTool, moveToHandler)

	// interact tool
	interactTool := mcp.NewTool("interact",
		mcp.WithDescription("Press the action button to interact with the tile in front of the player (talk to NPCs, check objects, harvest crops)"),
	)
	s.AddTool(interactTool, interactHandler)

	// use_tool tool
	useToolTool := mcp.NewTool("use_tool",
		mcp.WithDescription("Use the currently equipped tool ONCE on the tile in front of the player. For repeated use (chopping trees, breaking rocks), use use_tool_repeat instead."),
	)
	s.AddTool(useToolTool, useToolHandler)

	// use_tool_repeat tool
	useToolRepeatTool := mcp.NewTool("use_tool_repeat",
		mcp.WithDescription("Use the currently equipped tool multiple times with proper timing. Use this for chopping trees, breaking rocks, or any action requiring repeated swings. Waits for completion before returning."),
		mcp.WithNumber("count", mcp.Required(), mcp.Description("Number of times to use the tool (1-100)")),
	)
	s.AddTool(useToolRepeatTool, useToolRepeatHandler)

	// switch_tool tool
	switchToolTool := mcp.NewTool("switch_tool",
		mcp.WithDescription("Switch to a specific inventory slot (0-11 for hotbar)"),
		mcp.WithNumber("slot", mcp.Required(), mcp.Description("Inventory slot (0-11)")),
	)
	s.AddTool(switchToolTool, switchToolHandler)

	// stop tool
	stopTool := mcp.NewTool("stop",
		mcp.WithDescription("Stop any current movement"),
	)
	s.AddTool(stopTool, stopHandler)

	// face_direction tool
	faceDirectionTool := mcp.NewTool("face_direction",
		mcp.WithDescription("Turn the player to face a specific direction without moving. Required before using tools on specific tiles."),
		mcp.WithString("direction", mcp.Required(), mcp.Description("Direction to face: up, down, left, or right")),
	)
	s.AddTool(faceDirectionTool, faceDirectionHandler)

	// place_item tool
	placeItemTool := mcp.NewTool("place_item",
		mcp.WithDescription("Place the currently held item on the tile in front of the player. Use for planting seeds, placing objects, applying fertilizer."),
		mcp.WithNumber("slot", mcp.Description("Optional inventory slot to select before placing (0-11)")),
	)
	s.AddTool(placeItemTool, placeItemHandler)

	// hold_tool tool
	holdToolTool := mcp.NewTool("hold_tool",
		mcp.WithDescription("Hold and charge the current tool for area effect (watering can, hoe). Longer hold = larger area with upgraded tools."),
		mcp.WithNumber("ticks", mcp.Description("How long to hold in game ticks (30-180, default 60). 60 ticks = 1 second.")),
	)
	s.AddTool(holdToolTool, holdToolHandler)

	// eat_item tool
	eatItemTool := mcp.NewTool("eat_item",
		mcp.WithDescription("Eat a food item from inventory to restore energy and health."),
		mcp.WithNumber("slot", mcp.Required(), mcp.Description("Inventory slot containing the food item (0-35)")),
	)
	s.AddTool(eatItemTool, eatItemHandler)

	// select_item tool
	selectItemTool := mcp.NewTool("select_item",
		mcp.WithDescription("Select an item from inventory by name (partial match). Useful for finding and equipping tools or seeds."),
		mcp.WithString("name", mcp.Required(), mcp.Description("Name of the item to find and select (case-insensitive, partial match)")),
	)
	s.AddTool(selectItemTool, selectItemHandler)
}

func moveToHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	x, _ := request.Params.Arguments["x"].(float64)
	y, _ := request.Params.Arguments["y"].(float64)

	response, err := gameClient.SendCommand("move_to", map[string]interface{}{
		"x": int(x),
		"y": int(y),
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func interactHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	response, err := gameClient.SendCommand("interact", nil)
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func useToolHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	response, err := gameClient.SendCommand("use_tool", nil)
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func useToolRepeatHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	count, _ := request.Params.Arguments["count"].(float64)

	response, err := gameClient.SendCommand("use_tool_repeat", map[string]interface{}{
		"count": int(count),
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func switchToolHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	slot, _ := request.Params.Arguments["slot"].(float64)

	response, err := gameClient.SendCommand("switch_tool", map[string]interface{}{
		"slot": int(slot),
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func stopHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	response, err := gameClient.SendCommand("stop", nil)
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func faceDirectionHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	direction, _ := request.Params.Arguments["direction"].(string)

	response, err := gameClient.SendCommand("face_direction", map[string]interface{}{
		"direction": direction,
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func placeItemHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	params := map[string]interface{}{}

	if slot, ok := request.Params.Arguments["slot"].(float64); ok {
		params["slot"] = int(slot)
	}

	response, err := gameClient.SendCommand("place_item", params)
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func holdToolHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	params := map[string]interface{}{}

	if ticks, ok := request.Params.Arguments["ticks"].(float64); ok {
		params["ticks"] = int(ticks)
	}

	response, err := gameClient.SendCommand("hold_tool", params)
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func eatItemHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	slot, _ := request.Params.Arguments["slot"].(float64)

	response, err := gameClient.SendCommand("eat_item", map[string]interface{}{
		"slot": int(slot),
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

func selectItemHandler(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	name, _ := request.Params.Arguments["name"].(string)

	response, err := gameClient.SendCommand("select_item", map[string]interface{}{
		"name": name,
	})
	if err != nil {
		return mcp.NewToolResultError(err.Error()), nil
	}

	return mcp.NewToolResultText(fmt.Sprintf("%s (success: %v)", response.Message, response.Success)), nil
}

// Game knowledge content - all mechanics from wiki
const gameKnowledge = `# Stardew Valley Game Knowledge

## ⚠️ CRITICAL: ALWAYS VERIFY YOUR ACTIONS

**TRUST NOTHING. VERIFY EVERYTHING.**

A "success: true" response only means the command was SENT, not that it WORKED as intended.
You MUST read game state AFTER every action to confirm the expected result occurred.

### Verification Protocol (MANDATORY)
After EVERY action, you MUST:
1. Read stardew://player/status to verify your position/location changed correctly
2. Read stardew://player/tile-in-front or stardew://world/surroundings to verify the world state changed
3. If verification fails, the action did NOT work - try again or use a different approach

### Examples of Required Verification:
- After move_to: Read player/status → Verify position matches destination
- After interact at door: Read player/status → Verify location name changed
- After use_tool_repeat on tree: Read tile-in-front → Verify tree is gone (not still there)
- After interact with bed: Read world/time → Verify day changed (you actually slept)

## CRITICAL WORKFLOW FOR ANY ACTION

Before doing ANYTHING, you MUST:
1. Read stardew://player/status to know your position, energy, and equipped tool
2. Read stardew://world/surroundings to see what's around you
3. Identify your target (tree, crop, NPC, etc.) from the surroundings data
4. Move adjacent to target using move_to
5. Face the target using face_direction
6. Verify target is in front using stardew://player/tile-in-front
7. Use the correct tool with the correct number of uses
8. **VERIFY THE RESULT** by reading game state again

## CHOPPING TREES

Trees appear in terrain features with type "tree" and growthStage >= 5 (fully grown).

### Required Tool: AXE
### Required Hits by Axe Level:
- Basic Axe: 10 hits to fell tree + 5 hits to remove stump = 15 total
- Copper Axe: 8 hits + 4 stump = 12 total
- Steel Axe: 6 hits + 3 stump = 9 total
- Gold Axe: 4 hits + 2 stump = 6 total
- Iridium Axe: 2 hits + 1 stump = 3 total

### Workflow to Chop a Tree:
1. select_item with name "axe" to equip axe
2. move_to to get adjacent to the tree (not ON the tree)
3. face_direction toward the tree
4. Read tile-in-front to confirm tree is there (terrainType: "tree")
5. use_tool_repeat with count: 10 (for basic axe)
6. Check if tree is gone, if stump remains, use_tool_repeat with count: 5 more
7. Yields: 12-16 wood + 5 sap

## FARMING

### Tilling Soil
- Tool: HOE
- Target: Tillable ground (tile with isTillable: true)
- Action: Face tile, use_tool once

### Planting Seeds
- Required: Tilled soil (HoeDirt terrain feature)
- Action: select_item for seeds, face_direction toward tilled dirt, place_item

### Watering Crops
- Tool: WATERING CAN
- Target: HoeDirt with crop (hasCorop: true, isWatered: false)
- Action: Face crop tile, use_tool once
- Charged watering: hold_tool with ticks (60=1 row, 90=2 rows, 120=3 rows for upgraded cans)

### Harvesting
- Fully grown crops have isReadyForHarvest: true
- Action: Walk into crop OR interact while facing it

## MINING

### Breaking Rocks
- Tool: PICKAXE
- Regular rocks: 1-2 hits
- Ore nodes (copper/iron/gold): 3-5 hits
- Large boulders: 10+ hits with upgraded pickaxe

### Mine Floors
- Copper ore: Any floor
- Iron ore: Floors 40-79
- Gold ore: Floors 80+
- Iridium: Skull Cavern only

## FORAGING

### Picking Up Items
- Forage items appear in "debris" with canBePickedUp: true
- Action: Walk onto the tile to auto-pickup, OR interact while facing

### Berry Bushes
- Bush terrain feature with isReadyForHarvest: true
- Action: interact while facing the bush

## ENERGY MANAGEMENT

- Each tool use costs energy (2-8 depending on tool)
- Running out of energy = collapse
- Eating restores energy
- To eat: eat_item with slot number of food in inventory
- Go to bed before 2:00 AM to avoid collapse

## SLEEPING / GOING TO BED

**CRITICAL**: The bed is inside your farmhouse building, NOT outside on the farm!

### Workflow to Sleep:
1. Read player/status to check current location
2. If on "Farm" (outside): Navigate to your farmhouse door and enter
   - From most farm layouts, the farmhouse is in the upper-left area
   - Read world/surroundings to find the farmhouse entrance
   - Walk to the door and use interact to enter
3. Read player/status to VERIFY you are now in "FarmHouse"
4. Read world/surroundings to find the bed object
5. move_to adjacent to the bed
6. face_direction toward the bed
7. interact to get into bed
8. Read world/time to VERIFY the day changed (confirms you actually slept)

### Common Sleep Mistakes:
- Thinking you're inside when you're still outside on the farm
- Not verifying location name changed to "FarmHouse"
- Claiming you slept without verifying day number increased
- Not checking world/time after interact to confirm sleep worked

## LOCATIONS (Map Names)

The game has different maps you can navigate between:

### Key Locations:
- "Farm" - Your farm's OUTDOOR area (crops, animals, buildings)
- "FarmHouse" - INSIDE your house (where your bed is)
- "Town" - Pelican Town center
- "Beach" - The beach area
- "Mountain" - Mountain area (Robin's shop, mines entrance)
- "Forest" - Cindersap Forest (Marnie's ranch)
- "Mine" - Inside the mines

### Navigation:
- Use move_to to walk around the current map
- Use world/surroundings to find exits/entrances to other areas
- Use interact at doors/exits to transition between maps
- Always verify with player/status that your location changed after transitions

## MOVEMENT

### Pathfinding (NON-BLOCKING)
- move_to uses A* pathfinding and returns IMMEDIATELY
- Movement continues in background - you can poll status to check progress
- Cannot walk through: trees, rocks, buildings, water, fences, NPCs
- If "No path found": target is blocked or unreachable
- Try moving to an adjacent walkable tile instead

### Movement Workflow:
1. Call move_to with target x, y coordinates
2. Command returns immediately with "Movement started"
3. Poll player/status to check:
   - isPathfinding: true while moving, false when done
   - x, y: current position (check if matches target)
   - pathfindingTargetX/Y: where you're heading
   - pathProgress/pathLength: progress through path
4. Use "stop" command to cancel movement if needed
5. Once position matches target, movement is complete

### Positioning for Actions
- You must be ADJACENT to target, not ON it
- Trees/rocks: stand next to them, face them
- Example: Tree at (25, 20) - move_to (24, 20) then face_direction "right"

## NPC INTERACTION

### Talking
- Move adjacent to NPC
- Face NPC (check nearbyNPCs for position)
- Use interact

### Gifting
- Give 1 gift per day, 2 per week max
- Loved gifts: +80 points, Liked: +45, Neutral: +20
- Birthday: 8x multiplier
- Action: select_item gift, face NPC, interact

## COMMON MISTAKES TO AVOID

1. **TRUSTING "success: true" WITHOUT VERIFICATION** - This is the #1 mistake! Always read game state after actions.
2. NOT reading surroundings before acting
3. Using wrong number of tool hits (tree needs 10+, not 3)
4. Trying to walk ONTO objects instead of adjacent
5. Forgetting to face_direction before tool use
6. Not checking tile-in-front to verify target
7. Ignoring energy levels until collapse
8. Using move_to on blocked tiles
9. Thinking you're inside when still on "Farm" (outside) - check location name!
10. Claiming actions worked without verifying game state changed

## TERRAIN FEATURE TYPES

From NearbyTerrainFeatures:
- "tree": Regular tree (chop with axe)
- "fruit_tree": Fruit tree (don't chop, harvest fruit)
- "hoe_dirt": Tilled soil (plant seeds, water)
- "grass": Grass (cut with scythe for hay)
- "bush": Berry bush (interact to harvest when in bloom)

## OBJECT TYPES

From NearbyObjects:
- "chest": Storage container
- "machine": Craftable processor (furnace, keg, etc.)
- "sprinkler": Auto-waters adjacent tiles
- "forage": Wild item to pick up
`

func registerResources(s *server.MCPServer) {
	// Game knowledge resource - CRITICAL: Read this first to understand game mechanics
	knowledgeResource := mcp.NewResource(
		"stardew://game/knowledge",
		"Game Knowledge & Mechanics",
		mcp.WithResourceDescription("IMPORTANT: Read this FIRST before taking any actions. Contains all game mechanics: how to chop trees, farm, mine, fish, etc."),
		mcp.WithMIMEType("text/markdown"),
	)
	s.AddResource(knowledgeResource, knowledgeHandler)

	// Player status resource
	playerResource := mcp.NewResource(
		"stardew://player/status",
		"Player Status",
		mcp.WithResourceDescription("Current player state: position, energy, health, money, equipped tool, facing direction"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(playerResource, playerStatusHandler)

	// Inventory resource
	inventoryResource := mcp.NewResource(
		"stardew://player/inventory",
		"Player Inventory",
		mcp.WithResourceDescription("All items in player's inventory with slot, name, stack count, and whether it's a tool/weapon"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(inventoryResource, inventoryHandler)

	// Time resource
	timeResource := mcp.NewResource(
		"stardew://world/time",
		"World Time",
		mcp.WithResourceDescription("Current time, day, season, year, weather, and minutes until morning"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(timeResource, timeHandler)

	// Map info resource
	mapResource := mcp.NewResource(
		"stardew://world/map",
		"Map Info",
		mcp.WithResourceDescription("Current location map info: name, dimensions, type (farm/town/mine/etc), mine level"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(mapResource, mapHandler)

	// Surroundings resource - comprehensive nearby data
	surroundingsResource := mcp.NewResource(
		"stardew://world/surroundings",
		"Surroundings",
		mcp.WithResourceDescription("Everything within 10 tiles: objects, NPCs, monsters, terrain, buildings, animals, warp points, and the tile directly in front"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(surroundingsResource, surroundingsHandler)

	// Tile in front resource - quick check of what's directly ahead
	tileInFrontResource := mcp.NewResource(
		"stardew://player/tile-in-front",
		"Tile In Front",
		mcp.WithResourceDescription("What's on the tile directly in front of the player: passability, objects, NPCs, terrain"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(tileInFrontResource, tileInFrontHandler)

	// Nearby NPCs resource
	npcsResource := mcp.NewResource(
		"stardew://world/npcs",
		"Nearby NPCs",
		mcp.WithResourceDescription("NPCs within 10 tiles: name, position, friendship level, whether they can talk"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(npcsResource, npcsHandler)

	// Nearby objects resource
	objectsResource := mcp.NewResource(
		"stardew://world/objects",
		"Nearby Objects",
		mcp.WithResourceDescription("Objects within 10 tiles: machines, chests, crops, forage - with ready/harvest status"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(objectsResource, objectsHandler)

	// Warp points resource
	warpsResource := mcp.NewResource(
		"stardew://world/warps",
		"Warp Points",
		mcp.WithResourceDescription("All exit/warp points in current location with target destinations"),
		mcp.WithMIMEType("application/json"),
	)
	s.AddResource(warpsResource, warpsHandler)
}

func playerStatusHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	data, err := json.MarshalIndent(state.Player, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func inventoryHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	data, err := json.MarshalIndent(state.Player.Inventory, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func timeHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	timeInfo := map[string]interface{}{
		"timeOfDay":           state.Time.TimeOfDay,
		"timeString":          state.Time.TimeString,
		"day":                 state.Time.Day,
		"season":              state.Time.Season,
		"year":                state.Time.Year,
		"dayOfWeek":           state.Time.DayOfWeek,
		"weather":             state.World.Weather,
		"isNight":             state.Time.IsNight,
		"minutesUntilMorning": state.Time.MinutesUntilMorning,
	}

	data, err := json.MarshalIndent(timeInfo, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func mapHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	mapInfo := map[string]interface{}{
		"name":         state.Map.Name,
		"displayName":  state.Map.DisplayName,
		"width":        state.Map.Width,
		"height":       state.Map.Height,
		"locationType": state.World.LocationType,
		"isMineLevel":  state.Map.IsMineLevel,
		"mineLevel":    state.Map.MineLevel,
		"isOutdoors":   state.World.IsOutdoors,
		"isFarm":       state.World.IsFarm,
		"isGreenhouse": state.World.IsGreenhouse,
	}

	data, err := json.MarshalIndent(mapInfo, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func surroundingsHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	data, err := json.MarshalIndent(state.Surroundings, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func tileInFrontHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	data, err := json.MarshalIndent(state.Surroundings.TileInFront, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func npcsHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	// Combine NPCs and monsters for comprehensive character info
	result := map[string]interface{}{
		"npcs":     state.Surroundings.NearbyNPCs,
		"monsters": state.Surroundings.NearbyMonsters,
		"animals":  state.Surroundings.NearbyAnimals,
	}

	data, err := json.MarshalIndent(result, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func objectsHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	// Combine objects, terrain, debris, and resource clumps
	result := map[string]interface{}{
		"objects":        state.Surroundings.NearbyObjects,
		"terrain":        state.Surroundings.NearbyTerrainFeatures,
		"debris":         state.Surroundings.NearbyDebris,
		"resourceClumps": state.Surroundings.NearbyResourceClumps,
	}

	data, err := json.MarshalIndent(result, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func warpsHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	state := gameClient.GetState()
	if state == nil {
		return nil, fmt.Errorf("game not connected")
	}

	data, err := json.MarshalIndent(state.Surroundings.WarpPoints, "", "  ")
	if err != nil {
		return nil, err
	}

	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "application/json",
			},
			Text: string(data),
		},
	}, nil
}

func knowledgeHandler(ctx context.Context, request mcp.ReadResourceRequest) ([]interface{}, error) {
	return []interface{}{
		mcp.TextResourceContents{
			ResourceContents: mcp.ResourceContents{
				URI:      request.Params.URI,
				MIMEType: "text/markdown",
			},
			Text: gameKnowledge,
		},
	}, nil
}
