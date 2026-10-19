"""Long-term memory for the AI farmer (#21): read the journal back into the prompts, consolidate it between batches.

The mod's `journal` tool writes these files into the log folder (AgentLog.MemoryFile; p1 keeps the plain names,
other seats get a `_p2` suffix, like `diary_003_p2.md`):

    diary_NNN.md   the day's diary (for the viewers; the last few are shown to the AI)
    lessons.md     "- Day N: ..." lines, appended, in the AI's own words
    plan.md        "## Season" / "## Week" / "## Today" sections, replaced section by section
    notes.md       "- [topic, day N] fact" lines, appended

This module (standard library only, no game, no network, no `claude` CLI):

  * fills the {GOAL} {LESSONS} {PLAN} {NOTES} {MEMORY} placeholders of runner/system_prompt.md  -> inject()
  * adds the same-day continuation context and the journal reminder to the day prompts          -> continuation_context(), with_reminder()
  * runs the batch-end consolidation: shorter lessons and notes, a digest per finished week     -> consolidate_batch()

Every model call goes through a `session(system_prompt, prompt) -> reply text` callable, so tests pass a fake and
run_days.py passes a `claude -p` one. Anything that goes wrong keeps the AI's files as they were.

Per-seat character notes (#48) will sit next to these files; every function takes a `player` ("p1".."p3") so the
same code serves each seat.
"""

from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

Session = Callable[[str, str], str]

LESSONS_CAP = 40        # lines kept by the batch-end consolidation
NOTES_CAP = 60
DIGEST_MAX_CHARS = 1500  # about 150 words
INJECT_MAX_LINES = 150   # newest lines of a long file that go into the system prompt
SEASONS = ("spring", "summer", "fall", "winter")

NO_LESSONS = "(No lessons yet. You write them with `journal` before bed.)"
NO_PLAN = "(No plan yet. Write one with `journal` before bed.)"
NO_NOTES = "(No facts noted yet.)"

PLACEHOLDERS = ("GOAL", "LESSONS", "PLAN", "NOTES", "MEMORY")
_PLACEHOLDER = re.compile(r"\{(" + "|".join(PLACEHOLDERS) + r")\}")


# ---------------------------------------------------------------------- files


def seat_file(log_dir: Path, stem: str, player: str = "p1", ext: str = "md") -> Path:
    """Same naming as AgentLog.MemoryFile: p1 keeps the plain name, other seats get a `_p2` suffix."""
    name = f"{stem}.{ext}" if player in ("", "p1") else f"{stem}_{player}.{ext}"
    return Path(log_dir) / name


def lessons_path(log_dir: Path, player: str = "p1") -> Path:
    return seat_file(log_dir, "lessons", player)


def plan_path(log_dir: Path, player: str = "p1") -> Path:
    return seat_file(log_dir, "plan", player)


def notes_path(log_dir: Path, player: str = "p1") -> Path:
    return seat_file(log_dir, "notes", player)


def self_path(log_dir: Path, player: str = "p1") -> Path:
    """Character self-identity file (#48); overwritten each evening by the AI."""
    return seat_file(log_dir, "self", player)


def people_path(log_dir: Path, player: str = "p1") -> Path:
    """Relationship notes per other farmer (#48); updated when interactions happen."""
    return seat_file(log_dir, "people", player)


def diary_path(log_dir: Path, day: int, player: str = "p1") -> Path:
    return seat_file(log_dir, f"diary_{day:03}", player)


def digest_path(log_dir: Path, week: int, player: str = "p1") -> Path:
    return seat_file(log_dir, f"digest_week_{week:03}", player)


def read_text(path: Path) -> str:
    """Stripped text of a file, or "" when it does not exist."""
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace").strip()
    except FileNotFoundError:
        return ""


def read_self(log_dir: Path, player: str = "p1") -> str:
    return read_text(self_path(log_dir, player))


def read_people(log_dir: Path, player: str = "p1") -> str:
    return read_text(people_path(log_dir, player))


def bedtime_persona_card(log_dir: Path, player: str = "p1") -> dict[str, str]:
    return {
        "self": read_self(log_dir, player),
        "people": read_people(log_dir, player),
    }


def write_text(path: Path, text: str) -> None:
    """Write through a temp file, so a crash never leaves half a memory file."""
    path = Path(path)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text.rstrip() + "\n", encoding="utf-8")
    tmp.replace(path)


def tail_lines(text: str, limit: int = INJECT_MAX_LINES) -> str:
    """The newest `limit` lines of a long file, with a note about the older ones."""
    lines = text.splitlines()
    if len(lines) <= limit:
        return text
    return f"(older lines left out: {len(lines) - limit})\n" + "\n".join(lines[-limit:])


def plan_section(plan_text: str, scope: str = "Today") -> str:
    """One section of plan.md ("Season", "Week" or "Today"; "" when it has none).

    The mod writes plan.md with exactly these `## ` headings; any other heading stays inside the section it is in.
    """
    sections: dict[str, list[str]] = {}
    current = None
    for line in plan_text.splitlines():
        m = re.match(r"^##\s+(Season|Week|Today)\s*$", line, re.IGNORECASE)
        if m:
            current = m.group(1).lower()
            sections.setdefault(current, [])
        elif current is not None:
            sections[current].append(line)
    return "\n".join(sections.get(scope.lower(), [])).strip()


# ---------------------------------------------------------------------- weekly digests


def week_days(week: int) -> range:
    """Farm days of a 1-based week: week 1 is days 1-7."""
    return range(7 * week - 6, 7 * week + 1)


def digests(log_dir: Path, player: str = "p1") -> dict[int, str]:
    """Weekly digests by week number."""
    out = {}
    suffix = "" if player in ("", "p1") else f"_{player}"
    for f in Path(log_dir).glob("digest_week_*.md"):
        m = re.fullmatch(rf"digest_week_(\d+){re.escape(suffix)}\.md", f.name)
        if m and (text := read_text(f)):
            out[int(m.group(1))] = text
    return dict(sorted(out.items()))


def render_memory(log_dir: Path, diaries: str, player: str = "p1") -> str:
    """{MEMORY}: the weekly digests of earlier weeks, then the recent diaries."""
    found = digests(log_dir, player)
    if not found:
        return diaries
    lines = [f"Week {w} (days {week_days(w).start}-{week_days(w).stop - 1}): {text}" for w, text in found.items()]
    return "Earlier weeks, in short:\n" + "\n\n".join(lines) + "\n\nRecent diaries:\n" + diaries


# ---------------------------------------------------------------------- prompts


def inject(template: str, log_dir: Path, goal: str, diaries: str, player: str = "p1") -> str:
    """Fill the system prompt's placeholders in one pass (text inside a file is never read as a placeholder)."""
    values = {
        "GOAL": goal,
        "LESSONS": tail_lines(read_text(lessons_path(log_dir, player))) or NO_LESSONS,
        "PLAN": read_text(plan_path(log_dir, player)) or NO_PLAN,
        "NOTES": tail_lines(read_text(notes_path(log_dir, player))) or NO_NOTES,
        "MEMORY": render_memory(log_dir, diaries, player),
    }
    return _PLACEHOLDER.sub(lambda m: values[m.group(1)], template)


def last_think(log_dir: Path, day: int, player: str = "p1", limit: int = 600) -> str:
    """The AI's latest `think` of the day, from the day's JSONL log."""
    f = Path(log_dir) / f"day_{day:03}.jsonl"
    try:
        lines = f.read_text(encoding="utf-8", errors="replace").splitlines()
    except FileNotFoundError:
        return ""
    for line in reversed(lines):
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if e.get("kind") == "think" and e.get("player", "p1") == player and e.get("text"):
            return str(e["text"]).strip()[:limit]
    return ""


def continuation_context(log_dir: Path, day: int, player: str = "p1") -> str:
    """For a same-day part 2+: today's plan and the last thought, which the last few actions alone do not carry."""
    parts = []
    today = plan_section(read_text(plan_path(log_dir, player)), "Today")
    if today:
        parts.append("Your plan for today (from your plan):\n" + today)
    think = last_think(log_dir, day, player)
    if think:
        parts.append("Your last thought:\n" + think)
    return "\n\n".join(parts)


def journal_reminder(state: dict, plan_text: str = "") -> str:
    """Which plan sections tonight's journal should rewrite, from the game state ({"season", "dayOfMonth"})."""
    notes = []
    if not plan_text.strip():
        notes.append("You have no plan yet: tonight's journal should write ## Season, ## Week and ## Today.")
    dom = state.get("dayOfMonth")
    if isinstance(dom, int) and 1 <= dom <= 28:
        tomorrow = dom % 28 + 1  # day 1 of a month is a Monday
        if tomorrow == 1:
            season = state.get("season")
            nxt = SEASONS[(SEASONS.index(season) + 1) % 4] if season in SEASONS else "the next season"
            notes.append(f"Tomorrow is the first day of {nxt}: tonight's journal should rewrite ## Season (and ## Week).")
        elif tomorrow % 7 == 1:
            notes.append("Tomorrow is a Monday: tonight's journal should rewrite ## Week.")
    return " ".join(notes)


def with_reminder(prompt: str, state: dict, log_dir: Path, player: str = "p1") -> str:
    """The first prompt of a day, plus the journal reminder when there is one."""
    reminder = journal_reminder(state, read_text(plan_path(log_dir, player)))
    return f"{prompt}\n\n{reminder}" if reminder else prompt


# ---------------------------------------------------------------------- batch-end consolidation

ROLE = (
    "You are an AI playing a farmer in Stardew Valley. Between batches of game days you tidy your own memory. "
    "You have no game tools in this session and nothing to play: reply with plain text only."
)

LESSONS_TASK = """\
Below are the lessons you wrote for yourself so far, oldest first ({count} lines).

{text}

Rewrite them as one shorter list for your future self:
- merge duplicates and near-duplicates into one line;
- drop lessons that are outdated or turned out wrong (for example a fix for something that no longer happens);
- keep the concrete, useful ones: what to do, what not to do, and why. Keep the day number when it matters;
- keep your own words, first person, in the same language as the lines above;
- at most {cap} lines, one lesson per line, every line starting with "- ".
Reply with only the list."""

NOTES_TASK = """\
Below are the facts you noted so far, oldest first ({count} lines), each tagged like [shops, day 3].

{text}

Rewrite them as one shorter list of facts for your future self:
- merge duplicates into one line; where two lines disagree keep the newer one;
- drop facts that are no longer true or no longer matter;
- keep the tag of each line, in the same language as the lines above;
- at most {cap} lines, one fact per line, every line starting with "- ".
Reply with only the list."""

DIGEST_TASK = """\
Below are your diaries from week {week} (days {first}-{last}).

{text}

Write a digest of that week for your future self, at most 150 words, first person, in the same language as the diaries: \
what you built and earned, what went wrong, and what is still in progress.
Reply with only the digest text."""

DO_IT = "Do the task described in your instructions. Reply with only the result."


@dataclass
class Result:
    """What one consolidation job did. `status`: consolidated | kept | empty | error."""

    name: str
    status: str
    before: int = 0
    after: int = 0
    backup: str = ""
    detail: str = ""

    def describe(self) -> str:
        if self.status == "consolidated" and self.backup:
            return f"{self.name}: {self.before} -> {self.after} lines, old list kept as {self.backup}"
        if self.status == "consolidated":
            return f"{self.name}: written ({self.detail})"
        return f"{self.name}: {self.status}{' (' + self.detail + ')' if self.detail else ''}"


def parse_list(reply: str) -> list[str]:
    """The list lines of a model reply as "- text". Preface, code fences and blank lines are ignored."""
    out = []
    for line in reply.splitlines():
        m = re.match(r"^\s*(?:[-*•]|\d+[.)])\s+(\S.*?)\s*$", line)
        if m:
            out.append("- " + m.group(1))
    return out


def clean_prose(reply: str) -> str:
    """A model reply without code fences around it."""
    text = reply.strip()
    text = re.sub(r"^```[a-zA-Z]*\s*\n", "", text)
    text = re.sub(r"\n```\s*$", "", text)
    return text.strip()


def next_batch(log_dir: Path, stem: str, player: str = "p1") -> int:
    """Number for the next `<stem>_before_batchN.md` backup: one more than the highest that exists."""
    suffix = "" if player in ("", "p1") else f"_{player}"
    top = 0
    for f in Path(log_dir).glob(f"{stem}_before_batch*.md"):
        m = re.fullmatch(rf"{re.escape(stem)}_before_batch(\d+){re.escape(suffix)}\.md", f.name)
        if m:
            top = max(top, int(m.group(1)))
    return top + 1


def consolidate_list(log_dir: Path, stem: str, task: str, cap: int, session: Session, player: str = "p1",
                     attempts: int = 2) -> Result:
    """Rewrite lessons.md or notes.md as a shorter list, keeping the old file as `<stem>_before_batchN.md`.

    A reply that is empty, has no list lines or has more than `cap` lines is asked for again (once, with the
    reason). If it is still bad, or the session fails, the file is left exactly as it was and no backup is made.
    """
    path = seat_file(log_dir, stem, player)
    original = read_text(path)
    count = len([ln for ln in original.splitlines() if ln.strip()])
    if count == 0:
        return Result(stem, "empty", detail="nothing written yet")

    system = ROLE + "\n\n" + task.format(count=count, text=original, cap=cap)
    feedback = ""
    problem = "no reply"
    for _ in range(attempts):
        try:
            reply = session(system, DO_IT + feedback)
        except Exception as e:  # a failed session must not stop the rest of the batch end
            return Result(stem, "error", before=count, detail=f"{type(e).__name__}: {e}")
        items = parse_list(reply)
        if not items:
            problem = "the reply had no list lines"
            feedback = '\n\nYour last reply had no list lines. Reply with only the list, every line starting with "- ".'
        elif len(items) > cap:
            problem = f"the reply had {len(items)} lines, the limit is {cap}"
            feedback = f"\n\nYour last reply had {len(items)} lines; the limit is {cap}. Merge more and reply with at most {cap} lines."
        else:
            backup = seat_file(log_dir, f"{stem}_before_batch{next_batch(log_dir, stem, player)}", player)
            shutil.copy2(path, backup)
            write_text(path, "\n".join(items))
            return Result(stem, "consolidated", before=count, after=len(items), backup=backup.name)
    return Result(stem, "kept", before=count, detail=problem)


def missing_digest_weeks(log_dir: Path, current_day: int, player: str = "p1") -> list[int]:
    """Finished weeks (all seven days before `current_day`) that have diaries but no digest yet."""
    have = digests(log_dir, player)
    weeks = []
    for w in range(1, (current_day - 1) // 7 + 1):
        if w not in have and any(diary_path(log_dir, d, player).exists() for d in week_days(w)):
            weeks.append(w)
    return weeks


def write_digest(log_dir: Path, week: int, session: Session, player: str = "p1", attempts: int = 2) -> Result:
    name = f"digest_week_{week:03}"
    diaries = "\n\n".join(t for d in week_days(week) if (t := read_text(diary_path(log_dir, d, player))))
    if not diaries:
        return Result(name, "empty", detail="no diaries that week")
    days = week_days(week)
    system = ROLE + "\n\n" + DIGEST_TASK.format(week=week, first=days.start, last=days.stop - 1, text=diaries)
    feedback = ""
    problem = "no reply"
    for _ in range(attempts):
        try:
            text = clean_prose(session(system, DO_IT + feedback))
        except Exception as e:
            return Result(name, "error", detail=f"{type(e).__name__}: {e}")
        if not text:
            problem = "the reply was empty"
            feedback = "\n\nYour last reply was empty. Reply with the digest text."
        elif len(text) > DIGEST_MAX_CHARS:
            problem = f"the reply was {len(text)} characters, the limit is {DIGEST_MAX_CHARS}"
            feedback = f"\n\nYour last reply was too long ({len(text)} characters). Write at most 150 words."
        else:
            write_text(digest_path(log_dir, week, player), text)
            return Result(name, "consolidated", detail=f"{len(text)} characters")
    return Result(name, "kept", detail=problem)


def consolidate_batch(log_dir: Path, current_day: int, session: Session, player: str = "p1") -> list[Result]:
    """The batch-end session: shorter lessons, shorter notes, a digest for each finished week that has none.

    `current_day` is the farm day the game is on now (the day after the batch's last one).
    """
    results = [
        consolidate_list(log_dir, "lessons", LESSONS_TASK, LESSONS_CAP, session, player),
        consolidate_list(log_dir, "notes", NOTES_TASK, NOTES_CAP, session, player),
    ]
    for week in missing_digest_weeks(log_dir, current_day, player):
        results.append(write_digest(log_dir, week, session, player))
    return results
