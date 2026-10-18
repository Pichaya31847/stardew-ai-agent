"""Persona loader and prompt customizer for multi-AI co-op (Issue #41).

Each seat (p1, p2, p3) has its own character identity (name, traits, optional secret goal)
stored in runner/personas/{seat}.md, plus shared co-op rules for communication, morning meetings,
and day phases. Standard library only.
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
        "traits": "Methodical, cooperative, attentive to detail. Takes charge of core farm logistics and keeping the farm organized.",
        "secret_goal": "Ensure the Community Center is restored and protect the farm's shared harmony.",
    },
    "p2": {
        "name": "Gemini",
        "traits": "Curious, enthusiastic, adventurous. Loves exploring the mines, foraging wild plants, and discovering new things.",
        "secret_goal": "Discover the rarest items and catch legendary fish to amaze the viewers.",
    },
    "p3": {
        "name": "Luna",
        "traits": "Pragmatic, sharp, independent. Focuses on profits, bargains, and accumulating personal wealth.",
        "secret_goal": "Save an emergency fund of 5,000g in your own personal chest.",
    },
}


@dataclass
class Persona:
    name: str
    traits: str
    secret_goal: str = ""

    def summary(self) -> str:
        s = f"{self.name}: {self.traits}"
        if self.secret_goal:
            s += f" (Secret: {self.secret_goal})"
        return s


def parse_persona_md(text: str, fallback_name: str = "") -> Persona:
    """Parse Markdown with # Name, # Traits, # Secret Goal sections."""
    sections: dict[str, list[str]] = {}
    current = None
    for line in text.splitlines():
        m = re.match(r"^#{1,3}\s+(.+?)\s*$", line)
        if m:
            current = m.group(1).lower().replace(" ", "_")
            sections.setdefault(current, [])
        elif current is not None:
            sections[current].append(line)

    name = "\n".join(sections.get("name", [])).strip() or fallback_name
    traits = "\n".join(sections.get("traits", [])).strip()
    secret_goal = "\n".join(sections.get("secret_goal", [])).strip()
    return Persona(name=name, traits=traits, secret_goal=secret_goal)


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
            )
        except Exception:
            pass

    return Persona(
        name=default_info["name"],
        traits=default_info["traits"],
        secret_goal=default_info.get("secret_goal", ""),
    )


def load_all_personas(seats: list[str], personas_dir: Path | None = None) -> dict[str, Persona]:
    return {s: load_persona(s, personas_dir) for s in seats}


COOP_RULES_BLOCK = """\
# Multi-Player Co-op & Day Phases
You share this farm with other players in split-screen co-op. Each player has their own wallet, inventory, cabin, and personal chest.

## Co-op Communication Tools
- `say(text)`: Speak aloud in the in-game chat box and above your head. All players and viewers hear it. Write `@name` or `@p2` to call a player and interrupt their running task so they read it immediately.
- `whisper(to, text)`: Privately whisper to one player. Other players do not know what was said; viewers see it. Use this for secrets, alliances, or private deals.
- `think(text)`: Internal monologue visible ONLY to viewers on screen. Other players NEVER see your `think` thoughts. This is where your true feelings, lies, and secret plans belong!
- `give(to, item, count)`: Hand items to another player standing within 4 tiles of you.
- `plan_read(history)`: Read the shared plan board that all players can see, along with who edited what.
- `plan_edit(section, text)`: Write or update a section on the shared plan board (e.g. "Today's Tasks" or "Crops").

## Daily Phases
1. **Morning Meeting (6:00 AM)**:
   - Time is frozen while you talk. Hold a quick morning standup with your fellow farmers.
   - Use `say` to discuss plans and `plan_edit` to claim tasks. Conclude your meeting when tasks are agreed.
2. **Free Play**:
   - Head out into the valley to farm, forage, mine, fish, or visit shops.
   - Coordinate when necessary via `say` or `whisper`. Respect personal plots and chests unless negotiated.
3. **Evening Journal & Bedtime**:
   - When the day's work is done (or it is late), write today's journal with `journal`.
   - Update your identity (`self`) and notes on other players (`people`) in `journal` to record who you are becoming and your thoughts on other players.
   - Call `go_to_bed`. All active farmers must be in bed for the day to roll over into the next morning!
"""


def customize_system_prompt(
    template: str,
    seat: str,
    active_seats: list[str],
    personas: dict[str, Persona],
    model_display: str,
    language: str = "Thai",
    emergent: bool = False,
    self_note: str = "",
    people_note: str = "",
) -> str:
    """Customize system_prompt.md for a specific seat in a multi-AI co-op game."""
    my_persona = personas.get(seat) or load_persona(seat)
    other_seats = [s for s in active_seats if s != seat]
    peers_str = ", ".join(f"{personas[s].name} ({s})" for s in other_seats) if other_seats else "none"

    if emergent:
        if self_note.strip():
            identity_body = f"- **Name:** {my_persona.name} (Seat {seat})\n- **Emerging Identity (from your self notes):**\n{self_note.strip()}"
        else:
            identity_body = (
                f"- **Name:** {my_persona.name} (Seat {seat})\n"
                "- **Emerging Identity:** You start with no scripted traits. Your personality, goals, and communication style emerge from what you experience in the game. In your evening `journal`, write your identity statement (`self` parameter, ~150 words)."
            )

        if people_note.strip():
            people_block = f"\n# Your Relationships With Other Farmers (private thoughts)\n{people_note.strip()}\n"
        else:
            people_block = f"\n# Your Relationships With Other Farmers (private thoughts)\nYou are getting to know your companions ({peers_str}). Record one-line notes with your stance and evidence in your evening `journal` (`people` parameter).\n"

        persona_header = f"""\
You are **{my_persona.name}** ({model_display}), playing Stardew Valley in split-screen co-op on seat **{seat}** with other AI farmers ({peers_str}). {language} YouTube viewers watch your farm and read your thoughts on screen.

# Your Character Identity
{identity_body}
{people_block}
{COOP_RULES_BLOCK}
"""
    else:
        secret_block = f"- **Secret Goal (keep private!):** {my_persona.secret_goal}\n" if my_persona.secret_goal else ""
        persona_header = f"""\
You are **{my_persona.name}** ({model_display}), playing Stardew Valley in split-screen co-op on seat **{seat}** with other AI farmers ({peers_str}). {language} YouTube viewers watch your farm and read your thoughts on screen.

# Your Character Identity
- **Name:** {my_persona.name} (Seat {seat})
- **Traits:** {my_persona.traits}
{secret_block}
{COOP_RULES_BLOCK}
"""
    # Replace the solo opening line: "You are **{MODEL_NAME}**, playing Stardew Valley by yourself in a 100-day challenge..."
    prompt = re.sub(
        r"^You are \*\*.*?\*\*, playing Stardew Valley by yourself in a 100-day challenge\..*?\n+",
        persona_header + "\n",
        template,
        count=1,
    )
    if persona_header not in prompt:
        prompt = persona_header + "\n\n" + prompt
    return prompt
