# Tools

[ภาษาไทย](tools.th.md) · **English** · [← README](../README.en.md)

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
| `travel_to` | `location` | Go to another map. A shop owner's, villager's or fellow farmer's name also works; a closed door says when it opens. Inside a festival it leaves the festival first. In co-op the game lets farmers into a festival only together: it waits up to a minute for the others (they are told), then gives up with `not_ready` instead of leaving you stuck in the box |
| `mount`, `dismount` | | Ride the horse (needs a Stable) and get off |

## Farm

| Tool | Arguments | What it does |
|---|---|---|
| `use_tool` | `tool`, `tiles`, *`max_hits`* | Use a tool on one or more tiles (hoe, watering can, axe, pickaxe, scythe...) |
| `plant` | `item`, *`tiles`* or *`area`* | Plant seeds or fertilizer on tilled soil: listed tiles, an `[x, y, w, h]` area, or no tiles at all = every empty tilled tile on the map |
| `harvest` | `tiles` | Harvest crops or forage on the given tiles |
| `water_all` | *`whose`* | Water every dry crop on the map (`whose: "mine"` = only your own); refills the can at the nearest water and says whose crops it watered |
| `harvest_all` | | Collect every ready crop and forage item on the map |
| `forage` | *`places`*, *`max_places`*, *`list`* | Travel to the maps around the farm that have forage today, nearest first, and pick it all up; `list=true` only counts it |
| `clear_area` | `x`, `y`, `w`, `h` | Clear a rectangle with the right tool, row by row from the end nearest you; the Scythe also cuts dead crops. Large stumps and boulders are skipped and reported. Over 100 tiles it does the first rows and says the call for the rest |
| `till_area` | `x`, `y`, `w`, `h` | Clear, then till a rectangle row by row, but only as many tiles as you have this season's seeds for (minus the empty tilled soil already there); with no seeds it refuses |
| `place` | `item`, `tiles` | Put items, machines, chests, sprinklers, fences or furniture on the ground |

The batch tools (`water_all`, `harvest_all`, `clear_area`, `till_area`, `plant` area mode) stop with a reason (low
energy, a menu, a late hour, a full pack) and say what is left. `use_tool`, `plant` and `harvest` always report
`done`, `remaining` and `skipped` tiles; a tile that needs another tool says which ("it is twig: use the Axe"). Tile lists also accept the shapes small models send: `[{"x":1,"y":2}]`, `["1,2"]`,
one string `"[(1, 2), (3, 4)]"`, flat numbers `[1, 2, 3, 4]`, or one tile as `tile` or `x`/`y`.

## Game knowledge ⚡

Read from the game's own data, costs no game time.

| Tool | Arguments | What it does |
|---|---|---|
| `crops` ⚡ | *`season`* | Season planner: growth and regrow days, last day to plant, harvests that fit, seed and sell prices, gold per tile per day; marks the seeds you carry |
| `item_info` ⚡ | `name` | An object's sell prices by quality, what it restores, shops that sell it and recipes that use it |
| `calendar` ⚡ | *`days`* | Today and the next days: festivals, birthdays, the next festival, the main shops' hours and closed days |
| `villagers` ⚡ | *`name`* | Everyone: where they are, hearts, talked and gifted today and this week, birthdays. With a name: the gift tastes found out so far and what a date or a proposal still needs |
| `quests` | `action?`, `quest?`, `reason?` | Omit action or use `list` for an instant journal/board/Special Orders report, including unclaimed money. `claim_reward` opens the journal and clicks a completed quest's reward; `quest` selects its exact title (omit for the first available). Actions require a reason and no other menu open. Each quest has a "How:" line (what to bring to whom, and what you already carry), and the Community Center line says its next story step |

## Items, crafting and cooking

| Tool | Arguments | What it does |
|---|---|---|
| `craft` | *`recipe`*, *`quantity`* | Craft from the recipes you know; `craft()` lists them |
| `cook` | *`recipe`*, *`quantity`* | Cook the dishes you know in the farmhouse kitchen, from the pack and the fridge; `cook()` lists them |
| `chest` | *`x`*, *`y`*, *`action`*, *`item`*, *`items`*, *`quality`*, *`quantity`* | Store or take items in a chest or the fridge in one call: by name and quality, several items, or "everything". Without `x`/`y` it uses the open chest menu, or else your own nearest chest on the map |
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
| `buy` | `item`, `quantity` | Buy from the shop (also tool upgrades that trade items). Inside a shop with no menu open it walks to the counter and opens it first |
| `sell` | `item`, *`quantity`*, *`allow_reserved`* | Sell to the shop for gold at once; inside a shop it opens the counter first, elsewhere it names the shops that buy the item. Seeds, fertilizer and minimum crafting supplies are protected; reports safe surplus. `allow_reserved=true` permits a deliberate sale with an explanation in reason. |
| `upgrade_backpack` | | Buy the next backpack size at Pierre's (24 slots for 2,000g, then 36 for 10,000g): walks to the shop and its backpack display |
| `ship` | `item`, *`quantity`*, *`allow_reserved`* | Put surplus items in the shipping bin; paid overnight. Same reserve protection and intentional override as `sell`. The bin is not storage. |
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

`use_tool` with a Scythe validates live weeds, non-empty grass or a ripe crop before walking and again immediately before swinging. Empty/stale/building tiles are skipped, unchanged targets are not reported as successful cuts, and a batch completing zero targets fails with guidance to refresh `look`/`farm_summary`. `clear_area` stops after two ineffective scythe hits on the same target.

`interact` and `talk_to` finish ordinary dialogue and return its pages as text. Opened letters are read, their attachments/quests claimed through the normal close path, and closed. `respond(option)` answers a question and finishes ordinary follow-up dialogue; explicit further choices stay open. Shops/chests stay open while being used, and common movement/work actions close completed menus first. Cutscenes, night events and sleep checks are not automatically dismissed by this cleanup. `accept_quest` checks adjacent calendar/Help Wanted tiles separately; the built-in brain reads `quests` every morning and is instructed to accept feasible board requests and verify them.

During overnight farm events, fades or pass-out transitions, new gameplay/social calls wait or report `not_ready`. `say` cannot interrupt sleep, and `respond` answers game dialogue rather than another AI's chat. A new date alone does not mean the farmer is awake. `go_to_bed` no longer permits repeating the command to bypass its before-17:00 guard when energy is above one quarter and health above one third. Inventory/tool results warn at two free slots or fewer and explain chest crafting/storage and Pierre's separate backpack display.

| Tool | Arguments | What it does |
|---|---|---|
| `talk_to` | `name`, *`gift_item`*, *`go`* | Find a villager (travels across maps unless `go=false`, and re-aims while they walk), talk, optionally give a gift (also how quest deliveries are handed over; their money is claimed right after); reports friendship points before and after and their reaction. With a fellow farmer's name it walks up to them and tells you to use `say` (farmers don't talk_to each other). |
| `bouquet` | `name`, *`go`* | Ask a villager to date you (8 hearts) |
| `mermaid_pendant` | `name`, *`go`* | Propose to a villager (10 hearts, upgraded house) |
| `festival` | `action`, *`to`*, *`max`* | On a festival day: `start` talks to Lewis and begins the main event, `leave` leaves the festival (alone it confirms; in co-op every farmer must leave together). Both wait up to a minute for the other farmers, tell them, and then cancel the wait with `not_ready`. `eggs` picks up eggs during the Egg Festival's egg hunt (nearest first, until the hunt ends or `max` are picked; `look` shows the eggs as `E`) |

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
| `rest` | *`minutes`*, *`energy`* | Lie down on your own bed to get energy and health back while time passes (co-op only, about +14 energy every 10 minutes); ends when energy reaches `energy` (default full), after the minutes, or when someone calls you, then gets up and walks to the door to carry on |
| `journal` ⚡ | `diary`, `lessons`, `plan`, *`notes`*, *`self`*, *`people`*, *`day`* | The AI's memory across days: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections, optional facts, a `self` note (who it is, overwritten each evening) and one line per other farmer in `people`. `day` catches up a missed day |
| `go_to_bed` | | End the day. Refuses until today's `journal` is written; night events come back as an `Overnight:` line. Too early it names the farm work still waiting; tired before 18:00 in co-op it suggests `rest` once (a second call sleeps) |
| `morning_chores` | | Start-of-day chores in one call: open the gift boxes in your house, read every letter in the mailbox (attachments and quests claimed) and claim finished quests' money; returns the letters' text |

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

Local shop guidance: travel_to still stops at the building entrance. Nearby shop action coordinates (within radius 20, at most eight tiles) are included in arrival and missing-target/shop errors. interact requires both x and y for counters; it walks adjacent and presses the actual tile. shop_list, buy and sell still require an already-open ShopMenu and never open a shop or transact implicitly.
