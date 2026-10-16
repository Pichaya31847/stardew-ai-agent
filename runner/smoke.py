"""LLM-free smoke tests for the Stardew MCP tools. Run before every unattended batch.

Usage (game running with `EnableDebugApi: true` in the mod's config.json):

    py runner/smoke.py                 # run every case on a copy of the current save
    py runner/smoke.py --only shop_buy # run one case
    py runner/smoke.py --list

What it does:
  1. copies the run's save folder to a `smoke_*` slot and loads that copy in the game
     (the real save is only read; its files are hashed before and after to prove it),
  2. runs fixed cases that call the MCP tools directly and set up their own state through
     /api/debug/*,
  3. fails a case on a bad tool result shape or on new ERROR lines in the SMAPI log,
  4. loads the original save again and deletes the smoke copy.

Exit code is 0 only if every case passed, so run_days.py can use it as a gate.
Standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

API = "http://localhost:8770"
SMOKE_PREFIX = "smoke_"


# ---------------------------------------------------------------------- paths


def game_data_dir() -> Path:
    if os.name == "nt":
        return Path(os.environ["APPDATA"]) / "StardewValley"
    return Path.home() / ".config" / "StardewValley"


def saves_dir() -> Path:
    return game_data_dir() / "Saves"


def smapi_log_path() -> Path:
    return game_data_dir() / "ErrorLogs" / "SMAPI-latest.txt"


# ---------------------------------------------------------------------- http


class Smoke(Exception):
    """A failed assertion inside a case."""


class SkipCase(Exception):
    """The case cannot run in the current game state (not a failure)."""


def _request(path: str, body: dict | None = None, timeout: float = 600):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(API + path, data=data, headers={"Content-Type": "application/json"},
                                 method="POST" if body is not None else "GET")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def state() -> dict:
    try:
        return _request("/api/state", timeout=10)
    except Exception:
        return {"ready": False}


_rpc_id = 0


def call(tool: str, /, **args) -> tuple[bool, str]:
    """Call an MCP tool directly. Returns (ok, text). Also checks the result shape."""
    global _rpc_id
    _rpc_id += 1
    msg = {"jsonrpc": "2.0", "id": _rpc_id, "method": "tools/call", "params": {"name": tool, "arguments": args}}
    reply = _request("/mcp", msg)
    result = reply.get("result")
    if not isinstance(result, dict):
        raise Smoke(f"{tool}: no result object in reply: {str(reply)[:200]}")
    content = result.get("content")
    if not (isinstance(content, list) and content and content[0].get("type") == "text"
            and isinstance(content[0].get("text"), str)):
        raise Smoke(f"{tool}: bad result shape (content): {str(result)[:200]}")
    if not isinstance(result.get("isError"), bool):
        raise Smoke(f"{tool}: bad result shape (isError): {str(result)[:200]}")
    text = content[0]["text"]
    if not text.strip():
        raise Smoke(f"{tool}: empty result text")
    return (not result["isError"]), text


def ok(tool: str, /, **args) -> str:
    good, text = call(tool, **args)
    if not good:
        raise Smoke(f"{tool}({args}) failed: {text.splitlines()[0][:200]}")
    return text


def debug(op: str, **args) -> str:
    try:
        reply = _request(f"/api/debug/{op}", args)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise SystemExit("Debug API is off. Set \"EnableDebugApi\": true in Mods/StardewMCP/config.json and restart the game.")
        raise
    if not reply.get("ok"):
        raise Smoke(f"debug {op}({args}) failed: {reply.get('text')}")
    return reply.get("text", "")


# ---------------------------------------------------------------------- save handling


def hash_dir(path: Path) -> str:
    h = hashlib.sha256()
    for f in sorted(p for p in path.rglob("*") if p.is_file()):
        h.update(str(f.relative_to(path)).encode("utf-8"))
        h.update(f.read_bytes())
    return h.hexdigest()


def copy_save(src: Path, dst: Path) -> None:
    """Copy a save folder under a new name. The game loads Saves/<name>/<name>, so rename the inner files too."""
    if dst.exists():
        raise SystemExit(f"{dst} already exists")
    dst.mkdir(parents=True)
    for f in src.iterdir():
        if not f.is_file():
            continue
        name = f.name
        if name == src.name:
            name = dst.name
        elif name == src.name + "_old":
            name = dst.name + "_old"
        shutil.copy2(f, dst / name)


def remove_smoke_saves() -> None:
    """Delete every smoke_* save folder. Sleeping in the copy makes the game write its own smoke_<id> folder too."""
    for p in saves_dir().iterdir():
        if p.is_dir() and p.name.startswith(SMOKE_PREFIX):
            shutil.rmtree(p, ignore_errors=True)


def pick_source_save(explicit: str | None) -> Path:
    saves = saves_dir()
    if explicit:
        p = saves / explicit
        if not p.is_dir():
            raise SystemExit(f"No save folder {p}")
        return p
    s = state()
    if s.get("ready") and s.get("saveFolder") and not str(s["saveFolder"]).startswith(SMOKE_PREFIX):
        return saves / s["saveFolder"]
    cands = [p for p in saves.iterdir() if p.is_dir() and not p.name.startswith(SMOKE_PREFIX)]
    if not cands:
        raise SystemExit("No saves found.")
    return max(cands, key=lambda p: p.stat().st_mtime)


def load_and_wait(name: str, timeout: int = 300) -> dict:
    debug("load_save", name=name)
    t0 = time.time()
    time.sleep(3)
    while time.time() - t0 < timeout:
        s = state()
        if s.get("ready") and s.get("saveFolder") == name:
            time.sleep(2)  # let the first day-start events settle
            return s
        time.sleep(2)
    raise SystemExit(f"Timed out waiting for save {name} to load.")


# ---------------------------------------------------------------------- SMAPI log


class SmapiLog:
    ERROR = re.compile(r"^\[\d\d:\d\d:\d\d ERROR ")

    def __init__(self, path: Path):
        self.path = path
        self.pos = path.stat().st_size if path.exists() else 0

    def new_errors(self) -> list[str]:
        if not self.path.exists():
            return []
        size = self.path.stat().st_size
        if size < self.pos:  # log rotated (game restarted)
            self.pos = 0
        with self.path.open("rb") as f:
            f.seek(self.pos)
            text = f.read().decode("utf-8", errors="replace")
        self.pos = size
        return [ln for ln in text.splitlines() if self.ERROR.match(ln)]


# ---------------------------------------------------------------------- helpers for cases


def reset(location: str = "Farm") -> None:
    """Known-good player state before each case: full health/energy, morning, enough money, in `location`."""
    debug("set_time", time=900)
    debug("set_stamina", n=270)
    debug("set_health", n=100)
    debug("set_money", n=2000)
    if fresh_state().get("location") != location:
        ok("travel_to", location=location, reason="smoke test setup")


def parse_tillable_runs(look_text: str, length: int) -> list[list[list[int]]]:
    """Find horizontal runs of `length` tillable tiles (',') in a `look` map. Returns [[x,y], ...] lists."""
    m = re.search(r"Showing x (\d+)-(\d+), y (\d+)-(\d+)", look_text)
    if not m:
        return []
    x0 = int(m.group(1))
    runs = []
    for line in look_text.splitlines():
        row = re.match(r"y\s*(\d+) \| (.*)$", line)
        if not row:
            continue
        y, cells = int(row.group(1)), row.group(2)
        i = 0
        while i <= len(cells) - length:
            if all(ch == "," for ch in cells[i:i + length]):
                runs.append([[x0 + i + k, y] for k in range(length)])
                i += length
            else:
                i += 1
    return runs


def parse_things(look_text: str, symbol: str) -> list[tuple[str, list[tuple[int, int]]]]:
    """'Things in view' lines for one map symbol, e.g. 'A': [(label, [(x,y), ...]), ...] nearest first."""
    out = []
    for line in look_text.splitlines():
        m = re.match(rf"\s+{re.escape(symbol)} (.+?) x\d+: (.*)$", line)
        if m:
            out.append((m.group(1), [(int(a), int(b)) for a, b in re.findall(r"\((\d+),(\d+)\)", m.group(2))]))
    return out


def inventory_count(name: str) -> int:
    """Total stack size of items called exactly `name` in the `inventory` tool output."""
    total = 0
    for line in ok("inventory").splitlines():
        m = re.match(r"\s*slot \d+: (.+?) x(\d+) \[", line)
        if m and m.group(1).lower() == name.lower():
            total += int(m.group(2))
    return total


def open_shop(location: str, hhmm: int) -> str:
    """Set the time, travel to `location` and interact with its action tiles until a shop opens. Returns shop_list."""
    debug("set_time", time=hhmm)
    ok("travel_to", location=location, reason="smoke test")
    look = ok("look", radius=12)
    counters = [t for _, tiles in parse_things(look, "A") for t in tiles]
    for x, y in counters:
        ok("interact", x=x, y=y, reason="smoke test: find the shop counter")
        good, text = call("shop_list")
        if good:
            return text
    raise Smoke(f"no action tile in {location} opened a shop (tried {counters[:6]})")


def fresh_state() -> dict:
    """/api/state is a snapshot refreshed about 4 times a second; wait for it to catch up after a tool call."""
    time.sleep(0.6)
    return state()


# ---------------------------------------------------------------------- cases


@dataclass
class Case:
    name: str
    fn: Callable[[], None]
    skip: str = ""


CASES: list[Case] = []


def case(name: str, skip: str = ""):
    def deco(fn):
        CASES.append(Case(name, fn, skip))
        return fn
    return deco


@case("observe_tools")
def observe_tools():
    reset("Farm")
    for tool in ("status", "look", "inventory"):
        ok(tool)
    ok("think", text="smoke test")


@case("travel")
def travel():
    reset("Farm")
    text = ok("travel_to", location="FarmHouse", reason="smoke test")
    if "Arrived in FarmHouse" not in text:
        raise Smoke(f"expected arrival in FarmHouse, got: {text.splitlines()[0]}")
    text = ok("travel_to", location="Farm", reason="smoke test")
    if "Arrived in Farm" not in text:
        raise Smoke(f"expected arrival on Farm, got: {text.splitlines()[0]}")


@case("shop_buy")
def shop_buy():
    if fresh_state().get("dayOfMonth", 0) % 7 == 3:
        raise SkipCase("Pierre's is closed on Wednesdays; run the gate on another day to cover the shop")
    reset("Farm")
    open_shop("SeedShop", 1000)  # Pierre's is open 9:00-17:00 (closed Wednesdays)

    before = inventory_count("Parsnip Seeds")
    money = fresh_state().get("money")
    text = ok("buy", item="Parsnip Seeds", quantity=2, reason="smoke test")
    m = re.search(r"for (\d+)g", text)
    if not m:
        raise Smoke(f"buy result has no price: {text.splitlines()[0]}")
    after = inventory_count("Parsnip Seeds")
    if after != before + 2:
        raise Smoke(f"expected {before + 2} Parsnip Seeds after buying 2, have {after}")
    if fresh_state().get("money") != money - int(m.group(1)):
        raise Smoke("money did not drop by the reported price")
    ok("close_menu")


@case("farm_loop")
def farm_loop():
    reset("Farm")
    debug("add_item", id="(O)472", qty=5)  # Parsnip Seeds
    look = ok("look", radius=10)
    runs = parse_tillable_runs(look, 3)
    if not runs:
        raise Smoke("no run of 3 tillable tiles near the farm entrance")
    tiles = runs[0]

    ok("use_tool", tool="Hoe", tiles=tiles, reason="smoke test")
    text = ok("plant", item="Parsnip Seeds", tiles=tiles, reason="smoke test")
    m = re.search(r"Planted (\d+)/(\d+)", text)
    if not m or m.group(1) != m.group(2):
        raise Smoke(f"expected every seed planted, got: {text.splitlines()[0]}")
    ok("use_tool", tool="Watering Can", tiles=tiles, reason="smoke test")

    day = fresh_state().get("day")
    ok("go_to_bed", diary="Smoke test diary: tilled, planted and watered three parsnips.", reason="smoke test")
    now = fresh_state().get("day")
    if now != day + 1:
        raise Smoke(f"day did not advance (was {day}, now {now})")


@case("ship_and_sleep")
def ship_and_sleep():
    reset("Farm")
    debug("add_item", id="(O)24", qty=3)  # Parsnip
    ok("ship", item="Parsnip", quantity=3, reason="smoke test")
    day = fresh_state().get("day")
    text = ok("go_to_bed", diary="Smoke test diary: shipped three parsnips and went to sleep.", reason="smoke test")
    m = re.search(r"shipping income (-?\d+)g", text)
    if not m or int(m.group(1)) <= 0:
        raise Smoke(f"expected positive shipping income, got: {text.splitlines()[0]}")
    if fresh_state().get("day") != day + 1:
        raise Smoke("day did not advance")


@case("buy_recipe")
def buy_recipe():
    """#7: a bought recipe is learned, not handed over as the dish."""
    reset("Farm")
    debug("set_time", time=1300)
    wednesday = fresh_state().get("dayOfMonth", 0) % 7 == 3
    for shop_id in ("Saloon", "SeedShop"):  # Gus sells cooking recipes, Pierre some crafting ones
        if shop_id == "SeedShop" and wednesday:
            continue  # Pierre's door is locked on Wednesdays
        ok("travel_to", location=shop_id, reason="smoke test")
        debug("open_shop", id=shop_id)  # set_time doesn't move the owner to the counter
        listing = ok("shop_list")
        recipes = [m.group(1) for m in re.finditer(r"^\s+(.+?): (\d+)g.*\[recipe", listing, re.M)]
        if recipes:
            break
        ok("close_menu")
    else:
        raise SkipCase("neither the Saloon nor Pierre's sells a recipe today")

    listed = recipes[0]
    dish = re.sub(r"\s*\(?Recipe\)?$", "", listed)  # shops list "Hashbrowns (Recipe)"
    before = inventory_count(dish)
    money = fresh_state().get("money")
    text = ok("buy", item=listed, quantity=1, reason="smoke test")
    if not text.startswith("Learned recipe:"):
        raise Smoke(f"expected 'Learned recipe', got: {text.splitlines()[0]}")
    if inventory_count(dish) != before:
        raise Smoke(f"buying {listed} put the dish in the inventory")
    if fresh_state().get("money", money) >= money:
        raise Smoke("money did not drop after buying a recipe")
    ok("close_menu")


@case("choose_profession")
def choose_profession():
    """#8: level-5 profession choices wait for the AI; an unmade choice gets the default at bedtime."""
    reset("Farm")
    if "Professions:" in ok("status"):
        raise SkipCase("this save already has professions; run the gate on an earlier save")
    debug("set_skill", skill="farming", level=5)
    debug("set_skill", skill="fishing", level=5)
    ok("go_to_bed", diary="Smoke test diary: reached farming and fishing level 5 today.", reason="smoke test")

    status = ok("status")
    for skill in ("Farming 5", "Fishing 5"):
        if f"Pending profession choice (permanent): {skill}" not in status:
            raise Smoke(f"no pending {skill} choice in status:\n{status}")
    text = ok("choose_profession", option=1, skill="Farming", reason="smoke test")
    if "Tiller" not in text:
        raise Smoke(f"expected Tiller for Farming option 1, got: {text.splitlines()[0]}")
    good, _ = call("choose_profession", option=1, skill="Mining", reason="smoke test")
    if good:
        raise Smoke("choose_profession accepted a skill with no pending choice")

    ok("go_to_bed", diary="Smoke test diary: chose Tiller and left the fishing choice to the default.", reason="smoke test")
    status = ok("status")
    m = re.search(r"^Professions: (.+)$", status, re.M)
    if not m or "Tiller" not in m.group(1) or "Fisher" not in m.group(1):
        raise Smoke(f"expected Tiller (chosen) and Fisher (default) in status, got: {m.group(0) if m else status}")
    if "Pending profession choice" in status:
        raise Smoke("a profession choice is still pending after bedtime")


CASES.append(Case("clint_pickup", lambda: None, skip="needs the tool upgrade flow (#2)"))


# ---------------------------------------------------------------------- runner


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(errors="replace")  # game text may hold characters the console codepage lacks
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", action="append", help="Run only this case (repeatable)")
    ap.add_argument("--list", action="store_true", help="List cases and exit")
    ap.add_argument("--save", help="Save folder to copy (default: the loaded save, else the newest)")
    ap.add_argument("--keep", action="store_true", help="Keep the smoke_* copy afterwards")
    ap.add_argument("--no-restore", action="store_true", help="Leave the smoke copy loaded instead of reloading the original save")
    ap.add_argument("--smapi-log", default=str(smapi_log_path()))
    args = ap.parse_args()

    if args.list:
        for c in CASES:
            print(f"{c.name}{'  (skipped: ' + c.skip + ')' if c.skip else ''}")
        return 0

    chosen = [c for c in CASES if not args.only or c.name in args.only]
    if args.only and len(chosen) != len(set(args.only)):
        raise SystemExit(f"Unknown case in {args.only}; see --list")

    try:
        _request("/api/state", timeout=10)
    except Exception as e:
        raise SystemExit(f"Cannot reach the game at {API} ({e}). Start Stardew via SMAPI first.")

    original = state().get("saveFolder") if state().get("ready") else None
    src = pick_source_save(args.save)
    smoke_name = f"{SMOKE_PREFIX}{src.name}_{time.strftime('%H%M%S')}"
    dst = saves_dir() / smoke_name
    if not args.keep:
        remove_smoke_saves()  # leftovers from an earlier aborted run
    before_hash = hash_dir(src)

    print(f"Copying save {src.name} -> {smoke_name}")
    copy_save(src, dst)
    log = SmapiLog(Path(args.smapi_log))

    failures = 0
    try:
        load_and_wait(smoke_name)
        log.new_errors()  # ignore load-time noise from before the first case
        for c in chosen:
            if c.skip:
                print(f"  SKIP {c.name}: {c.skip}")
                continue
            t0 = time.time()
            try:
                c.fn()
                errs = log.new_errors()
                if errs:
                    raise Smoke("SMAPI log has new errors:\n    " + "\n    ".join(errs[:5]))
                print(f"  PASS {c.name} ({time.time() - t0:.0f}s)")
            except SkipCase as e:
                print(f"  SKIP {c.name}: {e}")
            except Smoke as e:
                failures += 1
                log.new_errors()
                print(f"  FAIL {c.name}: {e}")
            except Exception as e:  # network / unexpected
                failures += 1
                print(f"  FAIL {c.name}: {type(e).__name__}: {e}")
    finally:
        if not args.no_restore:
            restore = original or src.name
            print(f"Reloading {restore} (the last saved state, normally the start of the day)")
            try:
                load_and_wait(restore)
            except SystemExit as e:
                print(f"  could not reload the original save: {e}")
                failures += 1
        if not args.keep and not args.no_restore:
            remove_smoke_saves()

    if hash_dir(src) != before_hash:
        failures += 1
        print(f"FAIL the real save {src.name} changed during the smoke run!")
    else:
        print(f"Real save {src.name} unchanged.")

    print("SMOKE PASSED" if failures == 0 else f"SMOKE FAILED ({failures} problem(s))")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
