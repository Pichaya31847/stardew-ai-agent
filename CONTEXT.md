# Stardew MCP

A Stardew Valley mod in which AIs play farmers by themselves, alone or several on one split-screen farm, while
viewers watch what they think and do. These are the words the code, the docs and the issues use.

## Players and seats

**Seat**:
One of the four split-screen positions, P1 to P4, together with the farmer on it and how it is driven.
_Avoid_: slot, player number

**Host**:
P1, the save's own farmer; the other seats join its game.
_Avoid_: main player, server

**Farmhand**:
A farmer on P2–P4, joined into the host's game by the mod.

**Farmer**:
The in-game character on a seat. The AIs know each other only as farmers with names, never as seats.
_Avoid_: agent, bot

**Player**:
Whoever controls a farmer: an AI or a human.

**Role**:
What a seat is for: `ai`, `human`, `camera` or `off`.

**Camera seat**:
A seat whose farmer is parked out of the way and only watches. The AIs neither see it nor are seen by it, and it
never holds the clock.
_Avoid_: spectator; "P4" as a synonym (P4 is only its usual place)

**Brain**:
What drives an `ai` seat: the built-in AI with one provider, or `none` when an MCP client drives it.
_Avoid_: backend

**Built-in AI**:
The mod's own loop that plays a seat with the user's API key.
_Avoid_: viewer edition, internal agent

**MCP client**:
A program outside the game that drives a seat through that seat's MCP endpoint.

## Time

**Day**:
A farm day, counted from 1 (spring 1, year 1). Logs, diaries and stats are numbered by it.
_Avoid_: round, turn, episode

**Thinking**:
The time between an AI's last tool result and its next action.

**Freeze**:
The whole world (clock, farmers, villagers) standing still while any AI is thinking.
_Avoid_: pause

**Think timeout**:
How long a silent AI may hold the world frozen before the world goes on without it.

**Pause**:
A stop of the whole game asked for by a person or a script, independent of thinking.
_Avoid_: freeze

**Run control**:
The requests that steer a run from outside: pause, resume, stop after the day, stop now.

**Release**:
Letting the clock run for an idle seat until its farmer passes out, so the day can end.

**Pass out**:
A day that ends without the farmer going to bed: 2:00 AM, or health at 0.

**Session break**:
A notice asking a long-running MCP client to carry on in a fresh session.

## Tools and results

**Tool**:
One thing an AI can call on its MCP endpoint.
_Avoid_: command, function, skill

**Action**:
A tool that runs in game time. A farmer runs one action at a time, and every action carries a reason.

**Instant tool**:
A tool that only reads the game or talks, and costs no game time.
_Avoid_: free tool, query

**Macro**:
A tool that batches simple steps or plays a timing task for the AI. Nothing may be reachable only through a macro.

**Reason**:
The farmer's own words on why it takes an action, shown to viewers.

**Reason code**:
The machine-readable outcome of a result, such as `no_path` or `low_energy`.
_Avoid_: reason (that is the farmer's why)

**Partial**:
A result that did only part of what was asked.

**Loop guard**:
The rules that hint, and finally refuse, when an AI keeps repeating a call that fails or changes nothing.

**Game entry point**:
The game's own handler for an input. Tools call it and never re-implement it.

**Coverage**:
How far the tools reach each part of the game: works, reachable but untested, or missing.

## Talk and perception

**Say**:
A chat line heard only by the farmers whose screen shows the speaker.
_Avoid_: whisper, broadcast (neither exists)

**Thought**:
A private line an AI writes with `think`. Viewers see it; no farmer does.

**Mention**:
An `@name` in a say, which interrupts the farmer it names.

**Perception**:
What a farmer sees of the others in view: what each is actually doing, next to what it last heard them say.

**Interrupt**:
Something that happens to a farmer (a mention, a gift, someone entering its house, a theft, rain, crows) and stops its long action.
A farmer merely coming into view is an event too, but it waits for the next result instead of stopping the action.
It reaches the farmer as an event in its next result.

**Ownership**:
Who planted a crop or placed a chest or machine.

**Theft**:
A farmer taking a crop or a chest item that another farmer owns.

**Wallet**:
A farmer's own money, when the save keeps separate wallets.

## Memory

**Journal**:
An AI's written memory across days: diary, lessons, plan, notes, self note and people notes.

**Diary**:
The day told in the AI's own words, for viewers.

**Lessons**:
What the AI learned, one line per lesson, kept and added to across days.

**Plan**:
The AI's plan for the season, the week and today, rewritten as it changes.

**Notes**:
Facts the AI found out about the world.

**Self note**:
Who the AI thinks it is and what it lives for, rewritten every evening from what happened.
_Avoid_: persona (a persona is written by a person beforehand)

**People note**:
One line per other farmer: the AI's current stance towards them and why.

**Briefing**:
The morning summary of a seat: overnight results, shipping, mail, weather, the farm.

## Viewers

**HUD**:
The in-game panel on each AI's screen: its name, model, current action or thinking time, last thought and say.

**Overlay**:
The live page of thoughts and actions, made to sit on top of a recording or stream.

**Replay**:
The page that plays the logs back day by day.

**Feed**:
The live stream of log entries that the overlay reads.

**Clean frames**:
Frozen frames that look like live play, so a recording shows no sign of the freeze.

## Around the mod

**Mod**:
This repository: gameplay, the seats' runtime, the HTTP API and the pages it serves. It knows nothing about video.

**Studio**:
The separate, private production that films the series. It drives seats only through the mod's HTTP API.

**Runner**:
The studio's program that runs the AI sessions through each day.

**Smoke test**:
An in-game check without any AI, run through the debug hooks on a copy of a save.

Operator dashboard: the optional local control panel drawn over a camera seat, showing all AI seats and queuing human instructions before the next model request.
