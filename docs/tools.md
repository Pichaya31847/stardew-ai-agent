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
| `think` ⚡ | `text` | A private thought, shown on the overlay. Nobody in the game sees it. Text with Chinese characters is sent back to be written in the configured language |

## Move

| Tool | Arguments | What it does |
|---|---|---|
| `walk_to` | `x`, `y` | Pathfind to a tile on the current map; a tile that can't be reached ends at the closest reachable tile (partial). On the Farm and in the Backwoods a way walled in by up to 15 tiles of weeds, stones and twigs (never young trees) is cleared on the way, and the result says how many tiles and how much energy it took; bumping into a locked door stops the walk |
| `travel_to` | `location` | Go to another map. A shop owner's, villager's or fellow farmer's name also works; a closed door says when it opens. Inside a festival it leaves the festival first. In co-op the game lets farmers into a festival only together: it waits up to a minute for the others (they are told), then gives up with `not_ready` instead of leaving you stuck in the box |
| `mount`, `dismount` | | Ride the horse (needs a Stable) and get off |

## Farm

| Tool | Arguments | What it does |
|---|---|---|
| `use_tool` | `tool`, `tiles`, *`max_hits`* | Use a tool on one or more tiles (hoe, watering can, axe, pickaxe, scythe...); an empty watering can is refilled at the nearest reachable water first |
| `plant` | `item`, *`tiles`* or *`area`* | Plant seeds or fertilizer on tilled soil: listed tiles, an `[x, y, w, h]` area, or no tiles at all = every empty tilled tile of yours (or nobody's) on the map; soil another farmer tilled or planted is left alone |
| `harvest` | `tiles` | Harvest crops or forage on the given tiles |
| `water_all` | *`whose`* | Water every dry crop on the map (`whose: "mine"` = only your own); refills the can at the nearest reachable water and says whose crops it watered; on day 28 it skips crops that die when the season changes |
| `harvest_all` | *`whose`* | Collect your ready crops and every forage item on the map; crops another farmer planted are left for them unless `whose=all` |
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
| `fish_guide` ⚡ | *`name`*, *`season`* | Where every fish bites, from the game data: map, season, hours, weather, Fishing level, never-caught marks. No name: today's fish map by map plus the legendary fish with their exact spot; with a name: that fish's places and whether it bites today |
| `calendar` ⚡ | *`days`* | Today and the next days: festivals, birthdays, the next festival, the main shops' hours and closed days |
| `villagers` ⚡ | *`name`* | Everyone: where they are, hearts, talked and gifted today and this week, birthdays. With a name: the gift tastes found out so far and what a date or a proposal still needs |
| `quests` | `action?`, `quest?`, `reason?` | Omit action or use `list` for an instant journal/board/Special Orders report, including unclaimed money. `claim_reward` opens the journal and clicks a completed quest's reward; `quest` selects its exact title (omit for the first available). Actions require a reason and no other menu open. Each quest has a "How:" line (what to bring to whom, and what you already carry), and the Community Center line says its next story step |

## Items, crafting and cooking

| Tool | Arguments | What it does |
|---|---|---|
| `craft` | *`recipe`*, *`quantity`* | Craft from the recipes you know; `craft()` lists them with what the useful ones are for (Scarecrow, Sprinkler, Fertilizer, machines) and the useful recipes still to learn with how to unlock them. What is short but sits in your own chests is taken out first (walking there); otherwise the craft says how many of each you have and where to get the rest, and the same craft from an unchanged pack is refused at once |
| `cook` | *`recipe`*, *`quantity`* | Cook the dishes you know in the farmhouse kitchen, from the pack and the fridge; `cook()` lists them |
| `machine` | *`action`*, *`item`*, *`quantity`* | The machines where you stand (Furnace, Keg, Preserves Jar, Cheese Press, Mayonnaise Machine, Loom, Oil Maker...): `list` shows what each makes and when it is ready (and ready machines elsewhere on the farm), `load` walks to every idle machine that takes the item and puts it in (fuel such as Coal comes from the pack), `collect` takes every ready product |
| `goals` ⚡ | *`area`* | The run's goal: every achievement except Joja, with this farmer's progress read from the game. `area`: summary (default), shipping (never shipped), cooking (not cooked yet, how to learn each recipe), crafting, fish, museum, friends, monsters (Marlon's goals), upgrades, family |
| `upgrade_tool` | *`tool`* | Walks to Clint and orders a tool upgrade (Axe, Pickaxe, Hoe, Watering Can, Trash Can) through his upgrade shop; empty lists his offers or picks up a finished upgrade |
| `chest` | *`x`*, *`y`*, *`action`*, *`item`*, *`items`*, *`quality`*, *`quantity`* | Store or take items in a chest or the fridge in one call: by name and quality, several items, or "everything". Without `x`/`y` it uses the open chest menu, or else your own nearest chest on the map; an `x`/`y` with no chest here but one at that tile in your house (or on the Farm) goes there first. Sale produce (crops, flowers, forage, fish, animal and artisan goods) is only stored as far as a bundle or quest still needs it; the rest stays in the pack to ship |
| `equip` | `item`, *`slot`*, *`count`* | Put on a ring, boots or a hat (the old one goes back), or attach bait and tackle to the fishing rod |
| `wear` | `item` | Put on a hat, shirt or pants from the pack |
| `wardrobe` ⚡ | | What you wear and the clothes you carry |
| `eat` | `item` | Eat or drink something |
| `trash` | `item`, *`quantity`*, *`quality`*, *`confirm`* | Throw items away (never tools; valuables need `confirm`) |
| `crack_geode` | *`item`*, *`quantity`* | Walks to Clint and cracks geodes (any kind the geode menu takes, Mystery Boxes too) for 25g each; quantity defaults to the whole stack; names the finds the Museum still needs. No item: a guide to each kind (where it comes from, what it holds) |

## Shops and money

| Tool | Arguments | What it does |
|---|---|---|
| `shop_list` | | What the shop sells, with prices; inside a shop building it opens the counter first |
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
| `talk_to` | `name`, *`gift_item`*, *`go`* | Find a villager (travels across maps unless `go=false`, and re-aims while they walk), talk, optionally give a gift (also how quest deliveries are handed over; their money is claimed right after); reports friendship points before and after and their reaction. With a fellow farmer's name it walks up to them and tells you to use `say` (farmers don't talk_to each other). A villager behind a bedroom door you may not open yet fails at once instead of walking into the door. |
| `bouquet` | `name`, *`go`* | Ask a villager to date you (8 hearts) |
| `mermaid_pendant` | `name`, *`go`* | Propose to a villager (10 hearts, upgraded house) |
| `festival` | `action`, *`to`*, *`max`* | On a festival day: `start` talks to Lewis and begins the main event, `leave` leaves the festival (alone it confirms; in co-op every farmer must leave together). Both wait up to a minute for the other farmers, tell them, and then cancel the wait with `not_ready`. `eggs` picks up eggs during the Egg Festival's egg hunt (nearest first, until the hunt ends or `max` are picked; `look` shows the eggs as `E`) |

## Quests and the Community Center

| Tool | Arguments | What it does |
|---|---|---|
| `accept_quest` | | Take today's Help Wanted request from the board |
| `special_orders` | *`action`*, *`index`* | The Special Orders board in Town: `list`, `accept` one by index, `active` with progress |
| `bundles` ⚡ | *`room`*, *`all`* | Bundles that take donations now: the rooms whose Junimo note is up (with the note's tile), each unfinished bundle's missing items, the reward, and what you can donate now. `all=true` lists every room and finished bundles |
| `community_center` | | The next Community Center step: reads the first Junimo note (walks to it), visits the Wizard once his letter came, then donates everything an open bundle needs, walking to each room's note; items for the bundles waiting in your own chests are taken out first |
| `donate_bundle` | `item`, *`bundle`* | Walk into the Community Center to the room's note and donate an item (`all` for every fitting item, or gold for the Vault) to the first open bundle that needs it, through the game's own bundle page |
| `joja` | `action`, *`project`*, *`confirm`* | The Joja route: `projects` lists the five projects, `membership` buys the 5,000g membership only with `confirm=true` (without it: a warning of what every player loses and gains), `buy_project` pays one |
| `museum` | *`action`*, *`item`* | `list` what the Museum still needs, `donate` walks to the Museum and gives one item or all (default) and collects any rewards, `rewards` collects from Gunther |

## Adventure

| Tool | Arguments | What it does |
|---|---|---|
| `fish` | *`tries`* | Walk to the best shore, cast, play the BobberBar minigame and pick up treasure; one line per cast. Aims into a legendary fish's spot when one it can catch today is in reach. Needs a rod |
| `fight` | `seconds`, *`eat_below`* | Fight nearby monsters: equips the best weapon, walks to the nearest one and swings; stops when all clear, at 30% health or when a menu opens. `eat_below` (HP %) eats healing food on the way |
| `mine` | *`floors`* | Go down the Mines floor by floor: walks to the Mine, takes the elevator to the deepest floor reached, breaks rocks until the ladder shows, takes ladders and shafts, fights monsters that come close (eating below 40% health) and opens treasure chests. Stops at 22:00, low energy or health, a full pack or floor 120 |
| `leave_mine` | | Get out of the Mines from any floor: climbs the up ladders, or warps out when health is low, it is 11 PM or the ladder can't be reached |
| `search_trash` | *`x`*, *`y`* | Dig through a trash can; a villager who sees it likes you less |
| `watch_tv` | *`channel`* | Watch a channel: weather, fortune, Queen of Sauce (learns the recipe), tips |

In the Mines the footer shows HP, and any long action except `fight`, `eat` and `leave_mine` is cut short with
`Interrupted:` when a monster within 3 tiles hurts you.

## Menus and dialogue

| Tool | Arguments | What it does |
|---|---|---|
| `interact` | *`x`*, *`y`*, *`holding`* | The action button on a tile: doors, objects, machines, NPCs. `holding` holds an item first (load a machine, use a totem, a gift). With no tile and nothing held inside a shop building it opens the shop |
| `respond` | *`option`* | Answer the open dialogue question |
| `close_menu` | | Close the open menu; accepts a letter's quest and takes treasure or gift items that fit |
| `menu` | *`action`*, *`item`*, *`count`*, *`quality`*, *`option`*, *`value`*, *`text`* | Read any open menu, then use it: take or put (chests), choose (mine elevator), amount (send money), type (names), ok or cancel, or click a listed button |
| `choose_profession` | `option`, *`skill`* | Make a pending profession choice (skill level 5 and 10). Permanent |

## Time, sleep and memory

| Tool | Arguments | What it does |
|---|---|---|
| `wait` | `minutes` | Let time pass |
| `rest` | *`minutes`*, *`energy`* | Lie down on your own bed to get energy and health back while time passes (co-op only, about +14 energy every 10 minutes); ends when energy reaches `energy` (default full), after the minutes, or when someone calls you, then gets up and walks to the door to carry on |
| `journal` ⚡ | `diary`, `lessons`, `plan`, *`notes`*, *`self`*, *`people`*, *`day`* | The AI's memory across days: a diary for the viewers, lessons (appended), a plan with Season / Week / Today sections, optional facts, a `self` note (who it is, overwritten each evening) and one line per other farmer in `people`. `day` catches up a missed day. A diary with Chinese characters is sent back to be written in the configured language |
| `go_to_bed` | | End the day. Refuses until today's `journal` is written; night events come back as an `Overnight:` line. Too early (before 17:00 with energy above a quarter) it names one thing to do now as a ready call and when bed is allowed, in one line the second time that day; tired before 18:00 in co-op it suggests `rest` once (a second call sleeps) |
| `morning_chores` | | Start-of-day chores in one call: open the gift boxes in your house, read every letter in the mailbox (attachments and quests claimed) and claim finished quests' money, plant this season's crop seeds you carry on your own empty tilled soil (seeds that can't ripen before their seasons end are skipped and named, and nothing is planted on day 28), report sale produce left in your chests, crops without a Scarecrow and useful things you can craft now; returns the letters' text |

## Together (split-screen)

| Tool | Arguments | What it does |
|---|---|---|
| `say` ⚡ | `text` | Chat: the line shows in the chat box and above your head, heard only by the players whose screen shows you. `@name` gets someone's attention. A line with Chinese characters is sent back to be said in the configured language |
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

Local shop guidance: travel_to still stops at the building entrance. Nearby shop action coordinates (within radius 20, at most eight tiles) are included in arrival and missing-target/shop errors. interact requires both x and y for counters; it walks adjacent and presses the actual tile. Inside a shop building shop_list, buy and sell open the counter themselves (walking to it, or through the game's shop opening when the shopkeeper is there); they never transact beyond what was asked.
