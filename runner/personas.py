"""Persona loader and prompt customizer for multi-AI co-op (Issue #41).

Each seat (p1, p2, p3) has its own character identity (farmer name, nickname, traits, optional secret goal)
stored in runner/personas/{seat}.md, plus shared co-op rules for communication
and the shape of a day. Standard library only.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

DEFAULT_PERSONAS_DIR = Path(__file__).resolve().parent / "personas"

DEFAULT_DEFAULTS = {
    "p1": {
        "name": "Claude",
        "nickname": "พี่ส้ม",
        "traits": "Methodical, cooperative, attentive to detail. Takes charge of core farm logistics and keeping the farm organized.",
        "secret_goal": "Ensure the Community Center is restored and protect the farm's shared harmony.",
    },
    "p2": {
        "name": "Gemini",
        "nickname": "พี่ฟ้า",
        "traits": "Curious, enthusiastic, adventurous. Loves exploring the mines, foraging wild plants, and discovering new things.",
        "secret_goal": "Discover the rarest items and catch a legendary fish.",
    },
    "p3": {
        "name": "GPT",
        "nickname": "พี่เขียว",
        "traits": "Pragmatic, sharp, independent. Focuses on profits, bargains, and accumulating personal wealth.",
        "secret_goal": "Save an emergency fund of 5,000g in your own personal chest.",
    },
}


@dataclass
class Persona:
    name: str  # the farmer's in-game name: chat and @mentions use it
    traits: str
    secret_goal: str = ""
    nickname: str = ""  # what the others call them; learned in-world from the welcome note

    def known_as(self) -> str:
        return f"{self.name}, who goes by {self.nickname}" if self.nickname else self.name

    def summary(self) -> str:
        s = f"{self.name}: {self.traits}"
        if self.secret_goal:
            s += f" (Secret: {self.secret_goal})"
        return s


def parse_persona_md(text: str, fallback_name: str = "") -> Persona:
    """Parse Markdown with # Name, # Nickname, # Traits, # Secret Goal sections."""
    sections: dict[str, list[str]] = {}
    current = None
    for line in text.splitlines():
        m = re.match(r"^#{1,3}\s+(.+?)\s*$", line)
        if m:
            current = m.group(1).lower().replace(" ", "_")
            sections.setdefault(current, [])
        elif current is not None:
            sections[current].append(line)

    def section(key: str) -> str:
        return "\n".join(sections.get(key, [])).strip()

    return Persona(name=section("name") or fallback_name, traits=section("traits"),
                   secret_goal=section("secret_goal"), nickname=section("nickname"))


def load_persona(seat: str, personas_dir: Path | None = None) -> Persona:
    """Load persona from {personas_dir}/{seat}.md or fallback to defaults."""
    dir_path = Path(personas_dir) if personas_dir else DEFAULT_PERSONAS_DIR
    seat_key = seat.lower()
    default_info = DEFAULT_DEFAULTS.get(seat_key, {
        "name": f"Farmer_{seat_key.upper()}",
        "traits": "A hardworking farmer sharing the valley.",
        "secret_goal": "",
    })

    md_file = dir_path / f"{seat_key}.md"
    if md_file.exists():
        try:
            return parse_persona_md(md_file.read_text(encoding="utf-8"), fallback_name=default_info["name"])
        except Exception:
            pass

    json_file = dir_path / f"{seat_key}.json"
    if json_file.exists():
        try:
            data = json.loads(json_file.read_text(encoding="utf-8"))
            return Persona(
                name=data.get("name", default_info["name"]),
                traits=data.get("traits", default_info["traits"]),
                secret_goal=data.get("secret_goal", default_info["secret_goal"]),
                nickname=data.get("nickname", default_info.get("nickname", "")),
            )
        except Exception:
            pass

    return Persona(
        name=default_info["name"],
        traits=default_info["traits"],
        secret_goal=default_info.get("secret_goal", ""),
        nickname=default_info.get("nickname", ""),
    )


def load_all_personas(seats: list[str], personas_dir: Path | None = None) -> dict[str, Persona]:
    return {s: load_persona(s, personas_dir) for s in seats}


COOP_RULES_BLOCK = """\
# Living with the others
Other newcomers share this farm with you. Each of you has your own wallet, inventory, cabin and personal chest.
Nobody asked you to work together: helping each other, trading, competing or keeping to yourself is up to you.

## Talking
- `say(text)`: Speak aloud: it shows in the chat box and above your head. Only people who can see you right now hear it, so to talk with someone, go where they are. Write `@name` (their farmer name) to call someone who can hear you and interrupt what they are doing.
- `think(text)`: Your private thought. Nobody else ever sees it. Your true feelings, doubts and secret plans belong here.
- `give(to, item, count)`: Hand items to someone standing within 4 tiles of you.

## A day
1. **The day** starts at 6:00 AM: spend it as you choose, in the valley or on the farm. If you want to agree on something with the others, find them and talk. Respect each other's plots and chests unless you agree otherwise.
2. **Evening**: when you are done (or it is late), write today's `journal`, including who you are becoming (`self`) and what you think of each of the others (`people`). Then `go_to_bed`. Night only comes when everyone is in bed.
"""


def welcome_note(seat: str, active_seats: list[str], personas: dict[str, Persona]) -> str:
    """Mayor Lewis's welcome letter: the only place the newcomers learn each other's nicknames."""
    lines = [f"- {personas[s].known_as()}" + (" (that is you)" if s == seat else "") for s in active_seats]
    return "Mayor Lewis's welcome letter named this season's new residents:\n" + "\n".join(lines)


def customize_system_prompt(
    template: str,
    seat: str,
    active_seats: list[str],
    personas: dict[str, Persona],
    language: str = "Thai",
    emergent: bool = False,
    self_note: str = "",
    people_note: str = "",
) -> str:
    """Customize system_prompt.md for a specific seat in a multi-AI co-op game."""
    my_persona = personas.get(seat) or load_persona(seat)
    other_seats = [s for s in active_seats if s != seat]
    peers_str = ", ".join(personas[s].name for s in other_seats) if other_seats else "none"
    me = f"**{my_persona.name}**" + (f" (people call you **{my_persona.nickname}**)" if my_persona.nickname else "")
    opening = (f"You are {me}, a newcomer who has just moved to a run-down farm in Stardew Valley. "
               f"Other newcomers ({peers_str}) arrived the same day; you have never met them.\n\n"
               f"{welcome_note(seat, active_seats, personas)}")

    if emergent:
        if self_note.strip():
            identity_body = f"- **Name:** {my_persona.name}\n- **Who you are becoming (from your self notes):**\n{self_note.strip()}"
        else:
            identity_body = (
                f"- **Name:** {my_persona.name}\n"
                "- **Who you are:** nobody wrote that for you. Your personality, goals and way of talking come from what you live through here. "
                "In your evening `journal`, write who you are (`self` parameter, ~150 words)."
            )

        if people_note.strip():
            people_block = f"\n# What You Think of the Others (private)\n{people_note.strip()}\n"
        else:
            people_block = (f"\n# What You Think of the Others (private)\nYou do not know {peers_str} yet. "
                            "Record one-line notes with your stance and evidence in your evening `journal` (`people` parameter).\n")

        persona_header = f"""\
{opening}

# Your Character Identity
{identity_body}
{people_block}
{COOP_RULES_BLOCK}
"""
    else:
        secret_block = f"- **Secret Goal (keep private!):** {my_persona.secret_goal}\n" if my_persona.secret_goal else ""
        persona_header = f"""\
{opening}

# Your Character Identity
- **Name:** {my_persona.name}
- **Traits:** {my_persona.traits}
{secret_block}
{COOP_RULES_BLOCK}
"""
    # Replace the solo opening line: "You are **{MODEL_NAME}**, a newcomer who has just moved to ..."
    prompt = re.sub(
        r"^You are \*\*.*?\*\*, a newcomer who has just moved to .*?\n+",
        lambda _m: persona_header + "\n",
        template,
        count=1,
    )
    if persona_header not in prompt:
        prompt = persona_header + "\n\n" + prompt
    return prompt
