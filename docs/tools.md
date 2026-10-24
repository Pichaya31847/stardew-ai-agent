# Tools

**English** · [ภาษาไทย](tools.th.md) · [← README](../README.md)

Every tool the AI can call, grouped by job. Each farmer's MCP endpoint (`/mcp`, `/mcp/p1` … `/mcp/p3`) offers all of them.

- ⚡ = costs no game time (it only reads the game, or it is talk). Every other tool is an **action**: it runs in game
  time, one at a time per farmer, and takes a `reason` string that viewers see on the overlay (left out of the tables below).
- Arguments in *italics* are optional.
- The definitions live in the top-level files of [`mod/StardewMCP/Agent/`](../mod/StardewMCP/Agent/); what each reply
  looks like is in [HTTP API → Tool results](api.md#tool-results).

## Look and think

| Tool | Arguments | What it does |
|---|---|---|
| `status` ⚡ | | Day, time, weather (and tomorrow's), luck, place, energy, money, held item, what is worn, pending choices and any open dialogue or menu |
| `look` ⚡ | *`radius`*, *`kinds`*, *`diff`* | The surroundings as a tile map (17×17 by default, radius 8) with a legend and a list of things in view, other players included. `kinds` (e.g. `["crops","debris"]`) limits the list; `diff=true` lists only the tiles that changed since the last look |
| `inventory` ⚡ | | Items in the pack, what is worn and what is on the fishing rod |
| `farm_summary` ⚡ | | Whole-map farm report: empty tilled soil, crops by type with days to harvest, unwatered, ready and dead crops, crops that won't mature this season, forage, debris, watering can and nearest water |
| `think` ⚡ | `text` | A private thought, shown on the overlay. Nobody in the game sees it |

## Move

| Tool | Arguments | What it does |
|---|---|---|
| `walk_to` | `x`, `y` | Pathfind to a tile on the current map |
| `travel_to` | `location` | Go to another map. A shop owner's or villager's name also works; a closed door says when it opens |
| `mount`, `dismount` | | Ride the horse (needs a Stable) and get off |

## Farm

| Tool | Arguments | What it does |
|---|---|---|
| `use_tool` | `tool`, `tiles`, *`max_hits`* | Use a tool on one or more tiles (hoe, watering can, axe, pickaxe, scythe...) |
| `plant` | `item`, *`tiles`* or *`area`* | Plant seeds or fertilizer on tilled soil: listed tiles, an `[x, y, w, h]` area, or no tiles at all = every empty tilled tile on the map |
| `harvest` | `tiles` | Harvest crops or forage on the given tiles |
| `water_all` | *`whose`* | Water every dry crop on the map (`whose: "mine"` = only your own); refills the can at the nearest water and says whose crops it watered |
| `harvest_all` | | Collect every ready crop and forage item on the map |
| `clear_area` | `x`, `y`, `w`, `h` | Clear a rectangle (up to 100 tiles) with the right tool; large stumps and boulders are skipped and reported |
| `till_area` | `x`, `y`, `w`, `h` | Till a rectangle with the hoe |
| `place` | `item`, `tiles` | Put items, machines, chests, sprinklers, fences or furniture on the ground |

The batch tools (`water_all`, `harvest_all`, `clear_area`, `till_area`, `plant` area mode) stop with a reason (low
energy, a menu, a late hour, a full pack) and say what is left. `use_tool`, `plant` and `harvest` always report
`done`, `remaining` and `skipped` tiles.

## Game knowledge ⚡

Read from the game's own data, costs no game time.

| Tool | Arguments | What it does |
|---|---|---|
| `crops` ⚡ | *`season`* | Season planner: growth and regrow days, last day to plant, harvests that fit, seed and sell prices, gold per tile per day; marks the seeds you carry |
| `item_info` ⚡ | `name` | An object's sell prices by quality, what it restores, shops that sell it and recipes that use it |
| `calendar` ⚡ | *`days`* | Today and the next days: festivals, birthdays, the next festival, the main shops' hours and closed days |
| `villagers` ⚡ | *`name`* | Everyone: where they are, hearts, talked and gifted today and this week, birthdays. With a name: the gift tastes found out so far and what a date or a proposal still needs |
| `quests` ⚡ | | Quest journal with story, giver, progress and reward; today's Help Wanted request and who took it; Special Orders |

## Items, crafting and cooking

| Tool | Arguments | What it does |
|---|---|---|
| `craft` | *`recipe`*, *`quantity`* | Craft from the recipes you know; `craft()` lists them |
| `cook` | *`recipe`*, *`quantity`* | Cook the dishes you know in the farmhouse kitchen, from the pack and the fridge; `cook()` lists them |
| `chest` | *`x`*, *`y`*, *`action`*, *`item`*, *`items`*, *`quality`*, *`quantity`* | Store or take items in a chest or the fridge in one call: by name and quality, several items, or "everything" |
| `equip` | `item`, *`slot`*, *`count`* | Put on a ring, boots or a hat (the old one goes back), or attach bait and tackle to the fishing rod |
| `wear` | `item` | Put on a hat, shirt or pants from the pack |
| `wardrobe` ⚡ | | What you wear and the clothes you carry |
| `eat` | `item` | Eat or drink something |
| `trash` | `item`, *`quantity`*, *`quality`*, *`confirm`* | Throw items away (never tools; valuables need `confirm`) |
| `crack_geode` | *`item`*, *`quantity`* | Clint cracks geodes for 25g each |

## Shops and money

| Tool | Arguments | What it does |
|---|---|---|
| `shop_list` ⚡ | | What the open shop sells, with prices |
| `buy` | `item`, `quantity` | Buy from the open shop (also tool upgrades that trade items) |
| `sell` | `item`, *`quantity`* | Sell to the open shop for gold at once |
| `ship` | `item`, *`quantity`* | Put items in the shipping bin; paid overnight |
| `pay` ⚡ | `to`, `amount` | Send money to another player at any distance: a loan, a debt, a payment for work |

## Animals and buildings

| Tool | Arguments | What it does |
|---|---|---|
| `buy_animal` | *`type`*, *`building`*, *`name`* | Marnie's animals; `type=list` shows them with prices |
| `animals` | *`action`*, *`name`* | Your animals; `pet`, `collect` (eggs, milk, wool) and `feed` (hay troughs) |
| `build` | `building`, *`x`*, *`y`* | Order a Coop, Barn, Silo, Well or Shed from Robin; `building=list` shows the costs |
| `upgrade_house` | | Buy the next house level from Robin |
| `name` ⚡ | `text` | Name the new pet, farm animal, horse or child the open naming box asks for |

Buildings and animals are paid from the caller's own money and items.

## Villagers, romance and festivals

| Tool | Arguments | What it does |
|---|---|---|
| `talk_to` | `name`, *`gift_item`*, *`go`* | Find a villager (travels across maps unless `go=false`), talk, optionally give a gift; reports friendship points before and after and their reaction |
| `bouquet` | `name`, *`go`* | Ask a villager to date you (8 hearts) |
| `mermaid_pendant` | `name`, *`go`* | Propose to a villager (10 hearts, upgraded house) |
| `festival` | `action`, *`to`* | On a festival day: `start` talks to Lewis and begins the main event (waits for the other players), `leave` walks out |

## Quests and the Community Center

| Tool | Arguments | What it does |
|---|---|---|
| `accept_quest` | | Take today's Help Wanted request from the board |
| `special_orders` | *`action`*, *`index`* | The Special Orders board in Town: `list`, `accept` one by index, `active` with progress |
| `bundles` ⚡ | *`room`* | Community Center bundles room by room: what each needs, what is donated and by whom, the reward, which of your items fit |
| `donate_bundle` | `item`, *`bundle`* | Donate an item (or gold, for the Vault) to the first bundle that needs it, through the game's own bundle page |
| `joja` | `action`, *`project`*, *`confirm`* | The Joja route: `projects` lists the five projects, `membership` buys the 5,000g membership only with `confirm=true` (without it: a warning of what every player loses and gains), `buy_project` pays one |
| `museum` | *`action`*, *`item`* | `list` what the Museum still accepts, `donate` one item or all, collect `rewards` from Gunther |

## Adventure

| Tool | Arguments | What it does |
|---|---|---|
| `fish` | *`tries`* | Walk to the best shore, cast, play the BobberBar minigame and pick up treasure; one line per cast. Needs a rod |
| `fight` | `seconds`, *`eat_below`* | Fight nearby monsters: equips the best weapon, walks to the nearest one and swings; stops when all clear, at 30% health or when a menu opens. `eat_below` (HP %) eats healing food on the way |
| `leave_mine` | | Get out of the Mines from any floor: climbs the up ladders, or warps out when health is low, it is 11 PM or the ladder can't be reached |
| `search_trash` | *`x`*, *`y`* | Dig through a trash can; a villager who sees it likes you less |
| `watch_tv` | *`channel`* | Watch a channel: weather, fortune, Queen of Sauce (learns the recipe), tips |

In the Mines the footer shows HP, and any long action except `fight`, `eat` and `leave_mine` is cut short with
`Interrupted:` when a monster within 3 tiles hurts you.

## Menus and dialogue

| Tool | Arguments | What it does |
|---|---|---|
| `interact` | *`x`*, *`y`*, *`holding`* | The action button on a tile: doors, objects, machines, NPCs. `holding` holds an item first (load a machine, use a totem, a gift) |
| `respond` | *`option`* | Answer the open dialogue question |
| `close_menu` | | Close the open menu; accepts a letter's quest and takes treasure or gift items that fit |
| `menu` | *`action`*, *`item`*, *`count`*, *`quality`*, *`option`*, *`value`*, *`text`* | Read any open menu, then use it: take or put (chests), choose (mine elevator), amount (send money), type (names), ok or cancel, or click a listed button |
| `choose_profession` | `option`, *`skill`* | Make a pending profession choice (skill level 5 and 10). Permanent |

## Time, sleep and memory

| Tool | Arguments | What it does |
|---|---|---|
| `wait` | `minutes` | Let time pass |
| `rest` | *`minutes`* | Lie down on your own bed to get energy and health back while time passes; ends when energy is full, after the minutes, or when someone calls you |
| `journal` ⚡ | `diary`, `lessons`, `plan`, *`notes`*, *`self`*, *`people`*, *`day`* | The AI's memory across days: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections, optional facts, a `self` note (who it is, overwritten each evening) and one line per other farmer in `people`. `day` catches up a missed day |
| `go_to_bed` | | End the day. Refuses until today's `journal` is written; night events come back as an `Overnight:` line |

## Together (split-screen)

| Tool | Arguments | What it does |
|---|---|---|
| `say` ⚡ | `text` | Chat: the line shows in the chat box and above your head, heard only by the players whose screen shows you. `@name` gets someone's attention |
| `emote` ⚡ | `name` | Show an emote bubble above your head |
| `give` ⚡ | `to`, `item`, *`count`* | Hand an item to a nearby player |
| `pay` ⚡ | `to`, `amount` | Send money (see *Shops and money*) |
| `propose` | `to` | Propose marriage to another player |
| `answer_proposal` | `accept` | Answer another player's proposal |

What the farmers see of each other: `look` and `status` list the other players in view with the tool each is
*actually* running, its reason and how long, next to the last thing you heard them say. An @mention, a gift, a
player coming into view or into your house, a theft from your chest or plot, rain starting or crows on the farm
come back as `Events:` in the next tool result and stop a running `wait`, `walk_to`, `travel_to` or farm action.

## For scripts

| Tool | Arguments | What it does |
|---|---|---|
| `relationships_report` ⚡ | | This farmer's friendship data as JSON, for scripts (the same as [`/api/relationships`](api.md#endpoints) for one seat) |
