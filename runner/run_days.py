"""Run Claude Haiku through Stardew Valley, one Claude Code session per in-game day.

Usage (from a normal terminal where `claude` is logged in, with the game running and a save loaded):

    py runner/run_days.py --days 1
    py runner/run_days.py --until-day 100 --goal "..."

Each day:
  * builds the system prompt (rules + goal + the AI's lessons, plan and notes + recent diaries + relationships.md written from game data; see memory.py and relationships.py),
  * runs `claude -p` with only the in-game MCP server (http://localhost:8770/mcp),
  * saves Claude's full stream-json transcript next to the game logs (proof of a real run),
  * continues the same day in a new session if Haiku stopped before sleeping,
  * splits a long day into parts on purpose (planned session breaks from the mod, see sessions.py; they do
    not count toward --max-parts) and logs the tokens of every part to <out>/usage.csv (see usage_log.py),
  * on a subscription usage limit waits for the reset and retries the same part (see usage_limit.py),
  * when the sessions of a day are used up, releases the world freeze (POST /api/release_freeze) so the clock runs
    to 2:00 AM and the farmer passes out; the next morning's first prompt says what happened and asks for the
    missed day's journal first (pass_out.py, #22),
  * backs up the save folder after the day ends,
  * watches the game (runner/game_watchdog.py): if it crashes or hangs, relaunches it, auto-loads the save
    and replays the day from the morning (see --game-cmd; --no-watchdog turns this off).

After the last day of the batch, one short text-only session shortens lessons.md and notes.md (the old lists stay
as lessons_before_batchN.md) and writes a digest for each finished week. Use --skip-consolidation to skip it.
The mod's LogDir must be the same folder as <out>/logs, where this script reads diaries, lessons and plans.

Run control (pause, stop after the day, stop now; Ctrl+C is graceful) is in runner/run_control.py.
The save backup (the loaded save only, last N kept) is in runner/save_backup.py and
runner/restore_day.py rolls a day back.

Before the first day it runs runner/smoke.py (LLM-free tool checks on a copy of the save) and stops if
any case fails. That needs "EnableDebugApi": true in the mod's config.json; pass --skip-smoke to skip.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from datetime import datetime
from pathlib import Path

# multi_runner does `import run_days`: run as a script, that would load a second copy whose GUARD stays the null
# watchdog, so a multi-seat hang was detected but never recovered (day 7, 2026-10-20). Share this module instead.
if __name__ == "__main__":
    sys.modules.setdefault("run_days", sys.modules[__name__])

import agy_tool_guard
import briefing
import game_watchdog
import language
import memory
import multi_runner
import pass_out
import personas
import relationships
import sessions
import stats as run_stats
import usage_limit
import usage_log
from tool_results import format_day_summary, load_day_summary, tag_for_entry

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import save_backup  # noqa: E402
from run_control import RunControl  # noqa: E402

API = "http://localhost:8770"
CLAUDE_CMD = ["claude"]  # tests swap in a fake
AGY_CMD = ["agy"]  # tests swap in a fake
# agy gives up on an MCP call after 3 minutes (a travel_to or a wait in bed held the world that long and Gemini, left with an
# error, went looking at its MCP files with run_command, 2026-10-20): the mod answers its calls before that
AGY_REPLY_WITHIN = 150
# calls outside the game tools that agy's guard refuses (the model is told why) before the session is ended as misbehaving
AGY_DENIED_LIMIT = 5
# agy reports the guard's refusal right after the call (about 0.1 s): a disallowed call not refused by then has run
AGY_DENY_PROOF_SECONDS = 5.0


def find_opencode_cmd() -> list[str]:
    # 1. Desktop app CLI location (contains active OpenCode Go credentials)
    appdata = os.environ.get("APPDATA")
    if appdata:
        desktop_cli = Path(appdata) / "ai.opencode.desktop" / "cli" / "2.0.28-x64" / "opencode-cli.exe"
        if desktop_cli.is_file():
            return [str(desktop_cli)]
        desktop_base = Path(appdata) / "ai.opencode.desktop" / "cli"
        if desktop_base.is_dir():
            matches = list(desktop_base.glob("*/opencode-cli.exe"))
            if matches:
                return [str(matches[-1])]
    # 2. PATH lookup
    for name in ("opencode-cli", "opencode"):
        found = shutil.which(name)
        if found:
            return [found]
    return ["opencode"]


OPENCODE_CMD = find_opencode_cmd()  # tests swap in a fake


def opencode_run_cmd() -> list[str]:
    """`opencode run`; the 2.x CLI (opencode-cli) needs --standalone, or it runs inside the desktop app's background
    service and ignores the workdir's opencode.json (no stardew MCP, no tool denials: it could read the repo)."""
    standalone = Path(OPENCODE_CMD[0]).stem.lower() == "opencode-cli"
    return [*OPENCODE_CMD, "run", *(["--standalone"] if standalone else [])]


def git_init(folder: Path) -> None:
    subprocess.run(["git", "init", "-q", str(folder)], check=True)


def opencode_env(workdir: Path) -> dict[str, str]:
    """The environment for opencode in `workdir`. opencode 2.x takes its project folder from PWD, not from the process's
    working directory: started from a shell in the repo root it skips the workdir's opencode.json (no stardew server)."""
    env = dict(os.environ)
    env["PWD"] = str(Path(workdir).resolve())
    return env
DEFAULT_OUT = HERE.parent / "runs"
GUARD = game_watchdog.NullWatchdog()  # replaced in main() unless --no-watchdog
SESSIONS: dict[str, subprocess.Popen] = {}  # seat -> its running AI CLI; multi-seat kills them all on a game fault or outage


class BackendError(SystemExit):
    """The AI CLI failed before its first tool call (provider down, auth, crash). Single seat: stops the run as before;
    multi seat: holds the whole world and waits for the provider (multi_runner.hold_for_outage)."""
DEFAULT_GOAL = (
    "Nobody tells you what to live for here, and nobody keeps score. You arrived with a little money, an old farm "
    "and no plan. What makes this life worth living is yours to find: just getting by from day to day, someone or "
    "something you come to love, helping other people, winning someone's heart, or simply being happy. It may "
    "change as you go. When you know what you live for, write it in your `self` note."
)


def api_get(path: str):
    with urllib.request.urlopen(API + path, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


def api_post(path: str, body: dict):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


CTL = RunControl(api_get, api_post)


def game_state() -> dict:
    try:
        return api_get("/api/state")
    except Exception:
        return {"ready": False}


def wait_ready(timeout: int = 600) -> dict:
    t0 = time.time()
    while time.time() - t0 < timeout:
        s = game_state()
        if s.get("ready"):
            return s
        GUARD.check()
        time.sleep(3)
    if GUARD.armed:
        raise game_watchdog.GameFault("not_ready", f"the world was not ready for {timeout}s")
    raise SystemExit("Game is not ready (start Stardew via SMAPI and load the save).")


def recent_diaries(log_dir: Path, day: int, n: int = 3) -> str:
    parts = []
    for d in range(max(1, day - n), day):
        f = log_dir / f"diary_{d:03}.md"
        if f.exists():
            parts.append(f.read_text(encoding="utf-8").strip())
    return "\n\n".join(parts) if parts else "(This is your first day. No diaries yet.)"


def last_actions(log_dir: Path, day: int, n: int = 25, player: str = "p1") -> str:
    seat_file = log_dir / f"day_{day:03}_{player}.jsonl"
    f = seat_file if player != "p1" and seat_file.exists() else log_dir / f"day_{day:03}.jsonl"
    if not f.exists():
        return ""
    lines = f.read_text(encoding="utf-8").splitlines()[-n * 3:]
    out = []
    for line in lines:
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if e.get("kind") in ("think", "action", "result"):
            text = (e.get("text") or "").replace("\n", " ")[:200]
            tag = tag_for_entry(e) if e.get("kind") == "result" else ""  # [partial · low_energy] etc., as the model saw it
            if tag:
                text = f"{tag} {text}"
            out.append(f"{e.get('gameTime')} {e['kind']} {e.get('tool') or ''}: {text}")
    return "\n".join(out[-n:])


def continue_prompt(day: int, log_dir: Path, planned: bool = False, player: str = "p1") -> str:
    sessions.add_context_provider(memory.continuation_context)
    recent = last_actions(log_dir, day, 8 if planned else 25, player=player)
    prompt = sessions.continuation_prompt(day, log_dir, planned, recent=recent)
    return prompt + pass_out.pending_reminder(game_state(), player=player)


def report_failures(log_dir: Path, day: int) -> None:
    """Print the day's failure reasons per tool (day_XXX_summary.json, written by the mod when the day ends)."""
    summary = load_day_summary(log_dir, day)
    if summary:
        print("  " + format_day_summary(summary).replace("\n", "\n  "))


def save_folder() -> Path | None:
    """The folder of the save the game has loaded (/api/state saveFolder). Never a smoke_* slot, never a guess."""
    try:
        return save_backup.pick_save_folder(game_state(), save_backup.default_saves_dir())
    except save_backup.BackupError as e:
        print(f"  WARNING: save NOT backed up: {e}")
        return None


@dataclass
class NormalizedEvent:
    kind: str  # "init", "tool_call", "tool_denied" (agy: its guard hook refused a call), "result"
    step: int = -1  # agy's step_index: pairs a tool_call with its tool_denied
    session_id: str = ""
    model: str = ""
    tool_name: str = ""
    tool_args: dict = field(default_factory=dict)
    is_disallowed: bool = False
    subtype: str = ""
    is_error: bool = False
    num_turns: int = 0
    cost_usd: float = 0.0
    error_message: str = ""


KNOWN_STARDEW_TOOLS = {
    "think", "status", "look", "walk_to", "use_tool", "go_to_bed", "journal", "wait",
    "interact", "respond", "close_menu", "menu", "say", "emote", "give",
    "farm_summary", "harvest_all", "water_all", "clear_area", "till_area", "plant",
    "craft", "cook", "eat", "quests", "accept_quest", "crack_geode", "watch_tv",
    "ship", "shop_list", "buy", "sell", "calendar", "festival", "crops", "item_info",
    "equip", "special_orders", "fight", "leave_mine", "fish", "build", "upgrade_house",
    "buy_animal", "animals", "choose_profession", "trash", "chest"
}


def normalize_agy_tool(raw_name: str, arg: dict, server: str = "") -> tuple[str, bool]:
    """`server`: this seat's agy MCP server; call_mcp_tool to any other stardew_pN would drive another farmer."""
    for prefix in ("mcp_stardew_", "stardew_"):
        if raw_name.startswith(prefix):
            name = raw_name[len(prefix):]
            if any(name.startswith(f"{s}_") for s in ("p1", "p2", "p3", "p4")):
                name = name.split("_", 1)[1]
            return name, True
    if raw_name == "call_mcp_tool":
        srv = (arg.get("ServerName") or arg.get("server_name") or arg.get("server") or "").lower()
        if srv == server if server else srv.startswith("stardew"):
            tool_name = arg.get("ToolName") or arg.get("tool_name") or arg.get("name") or "tool"
            return tool_name, True
        return raw_name, False
    if raw_name in KNOWN_STARDEW_TOOLS:
        return raw_name, True
    return raw_name, False


def check_wallets(state: dict, is_multi: bool) -> None:
    """Several AIs on one shared wallet spend each other's money unseen (2026-10-19 rehearsal: 500g -> 10g at Pierre's).
    An older mod build without separateWallets in /api/state is let through."""
    if is_multi and isinstance(state, dict) and state.get("separateWallets") is False:
        raise SystemExit(
            "This farm still has one shared wallet. Switch to separate wallets before day 1: "
            "POST /api/debug/p1/separate_wallets (EnableDebugApi on), or the ledger in Lewis's house and one night.")


def check_log_dir(state: dict, log_dir: Path) -> None:
    """The mod writes diaries, notes and day logs to its LogDir; this script reads them from <out>/logs.

    A mismatch silently starves the AIs of yesterday's diary and leaves story_export with nothing (2026-10-19 rehearsal).
    The mod reads LogDir once at startup, so it can only be fixed in its config.json plus a game restart.
    An older mod build without logDir in /api/state is let through.
    """
    mod_dir = state.get("logDir") if isinstance(state, dict) else None
    if not mod_dir:
        return
    same = os.path.normcase(os.path.realpath(mod_dir)) == os.path.normcase(os.path.realpath(log_dir))
    if not same:
        raise SystemExit(
            f"The mod logs to {mod_dir}, but this run reads {log_dir}.\n"
            f'Set "LogDir": "{log_dir.as_posix()}" in Mods/StardewMCP/config.json and restart the game, '
            "or pass the --out whose logs folder the mod already uses.")


agy_own_mcp_file = agy_tool_guard.readable  # its server's instructions or its own saved tool output


def write_agy_guard(workdir: Path, server: str) -> None:
    """agy has no allowed-tools flag: a workspace PreToolUse hook denies every tool but this seat's server, before it runs.

    agy runs hook commands with `cmd /c` from <workdir>/.agents and escapes quotes in them, so the command is a bare
    relative .cmd and the paths live inside it. An empty server denies everything (text sessions).
    shortcut: Windows only (.cmd); a POSIX host needs a .sh twin.
    """
    agents = workdir / ".agents"
    agents.mkdir(parents=True, exist_ok=True)
    (agents / "stardew_guard.cmd").write_text(
        f'@"{sys.executable}" "{Path(agy_tool_guard.__file__).resolve()}" {server}\r\n', encoding="utf-8", newline="")
    (agents / "hooks.json").write_text(json.dumps({"stardew-guard": {"PreToolUse": [
        {"matcher": "*", "hooks": [{"type": "command", "command": ".\\stardew_guard.cmd", "timeout": 10}]}]}}, indent=2),
        encoding="utf-8")


def normalize_opencode_tool(raw_name: str, arg: dict) -> tuple[str, bool]:
    for prefix in ("mcp__stardew__", "stardew__", "stardew_", "stardew."):
        if raw_name.startswith(prefix):
            name = raw_name[len(prefix):]
            if any(name.startswith(f"{s}_") for s in ("p1", "p2", "p3", "p4")):
                name = name.split("_", 1)[1]
            if name in KNOWN_STARDEW_TOOLS:
                return name, True
            return name, False
    if raw_name in KNOWN_STARDEW_TOOLS:
        return raw_name, True
    return raw_name, False


_EXEC_NAMESPACE = re.compile(r"""tools\s*(?:\.\s*([A-Za-z_][\w-]*)|\[\s*["']([^"']+)["']\s*\])""")
_EXEC_STARDEW_CALL = re.compile(r"""tools\s*(?:\.\s*stardew|\[\s*["']stardew["']\s*\])\s*(?:\.\s*(\w+)|\[\s*["'](\w+)["']\s*\])""")
def opencode_execute_tools(code: str) -> tuple[list[str], list[str]]:
    """opencode 2.x reaches MCP tools only through its `execute` code tool (`tools["stardew"].status({})`).

    Returns (stardew tool names called, other MCP namespaces the code touches). `tools.opencode.*` is opencode's own
    discovery helper and counts as neither.
    shortcut: regex, so `const t = tools; t.blender` slips past; the wall is the workdir config (verified 2026-10-19:
    under --standalone only `stardew` has tools, and execute has no eval/globalThis/require).
    """
    namespaces = {a or b for a, b in _EXEC_NAMESPACE.findall(code or "")}
    others = sorted(namespaces - {"stardew", "opencode"})
    names = [a or b for a, b in _EXEC_STARDEW_CALL.findall(code or "")]
    return names, others


def ensure_agy_mcp(mcp_url: str, server_name: str = "stardew") -> None:
    """Ensure the 'stardew' MCP server is registered in agy CLI and points to the running game."""
    try:
        res = subprocess.run([*AGY_CMD, "mcp", "list"], capture_output=True, text=True, check=False)
        output = res.stdout or ""
        found_stardew = False
        stardew_enabled = False
        same_url = False
        for line in output.splitlines():
            parts = line.split()
            if parts and parts[0] == server_name:
                found_stardew = True
                if len(parts) >= 3 and parts[2].lower() == "enabled":
                    stardew_enabled = True
                same_url = len(parts) >= 4 and parts[3] == mcp_url
                break
        if not found_stardew or not same_url:  # `mcp add` also updates a server's URL
            subprocess.run([*AGY_CMD, "mcp", "add", server_name, mcp_url], capture_output=True, text=True, check=False)
        if found_stardew and not stardew_enabled:
            subprocess.run([*AGY_CMD, "mcp", "enable", server_name], capture_output=True, text=True, check=False)
    except Exception:
        pass


class Backend:
    name: str = "base"
    default_model: str = ""

    @property
    def display_name(self) -> str:
        return "AI"

    def prepare(self, args, out_dir: Path, seat: str = "p1") -> None:
        pass

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1, seat: str = "p1") -> tuple[list[str], dict[str, str], Path, Path]:
        raise NotImplementedError

    def parse_event(self, ev: dict) -> list[NormalizedEvent]:
        raise NotImplementedError

    def text_session(self, args, out_dir: Path, label: str) -> memory.Session:
        raise NotImplementedError


class ClaudeBackend(Backend):
    name = "claude"
    default_model = "claude-haiku-5-5"

    @property
    def display_name(self) -> str:
        return "Claude Haiku 5.5"

    def prepare(self, args, out_dir: Path, seat: str = "p1") -> None:
        pass

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1, seat: str = "p1"):
        mcp_cfg = out_dir / (f"mcp_{seat}.json" if seat != "p1" else "mcp.json")
        mcp_url = sessions.mcp_url(API, breaks=not getattr(args, "no_session_breaks", False), seat=seat)
        mcp_cfg.write_text(json.dumps({"mcpServers": {"stardew": {"type": "http", "url": mcp_url}}}), encoding="utf-8")
        sys_file = out_dir / (f"system_day_{day:03}_{seat}.md" if seat != "p1" else f"system_day_{day:03}.md")
        sys_file.write_text(system_prompt, encoding="utf-8")

        seat_suffix = f"_{seat}" if seat != "p1" else ""
        retry_suffix = f"_retry{attempt - 1}" if attempt > 1 else ""
        transcript = out_dir / "logs" / (f"claude_day_{day:03}{seat_suffix}_part{part}" + retry_suffix + ".jsonl")
        workdir = out_dir / (f"workdir_{seat}" if seat != "p1" else "workdir")
        workdir.mkdir(exist_ok=True)

        cmd = [
            *CLAUDE_CMD, "-p", prompt,
            "--model", args.model,
            "--system-prompt-file", str(sys_file),
            "--mcp-config", str(mcp_cfg), "--strict-mcp-config",
            "--tools", "",
            "--allowedTools", "mcp__stardew__*",
            "--setting-sources", "project",
            "--output-format", "stream-json", "--verbose",
            "--max-turns", str(args.max_turns),
        ]
        env = opencode_env(workdir)
        env["MCP_TOOL_TIMEOUT"] = "600000"
        env["MCP_TIMEOUT"] = "60000"
        return cmd, env, transcript, workdir

    def parse_event(self, ev: dict) -> list[NormalizedEvent]:
        kind = ev.get("type")
        if kind == "system" and ev.get("subtype") == "init":
            return [NormalizedEvent(kind="init", session_id=ev.get("session_id", ""), model=ev.get("model", ""))]
        elif kind == "assistant":
            items = []
            for c in ev.get("message", {}).get("content", []):
                if c.get("type") == "tool_use":
                    name = c["name"].replace("mcp__stardew__", "")
                    items.append(NormalizedEvent(kind="tool_call", tool_name=name, tool_args=c.get("input", {})))
            return items
        elif kind == "result":
            return [NormalizedEvent(
                kind="result",
                subtype=ev.get("subtype", ""),
                is_error=bool(ev.get("is_error")),
                num_turns=ev.get("num_turns", 0),
                cost_usd=float(ev.get("total_cost_usd", 0.0) or 0.0),
                error_message=str(ev.get("result", "")),
            )]
        return []

    def text_session(self, args, out_dir: Path, label: str) -> memory.Session:
        return claude_text_session(args, out_dir, label)


class AgyBackend(Backend):
    name = "agy"
    default_model = "gemini-3.8-flash-medium"

    @property
    def display_name(self) -> str:
        return "Gemini 3.8 Flash"

    def prepare(self, args, out_dir: Path, seat: str = "p1") -> None:
        mcp_url = sessions.mcp_url(API, breaks=not getattr(args, "no_session_breaks", False), seat=seat,
                                   reply_within=AGY_REPLY_WITHIN)
        srv_name = f"stardew_{seat}" if seat != "p1" else "stardew"
        self.server = srv_name
        ensure_agy_mcp(mcp_url, server_name=srv_name)

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1, seat: str = "p1"):
        # agy has no --system-prompt-file: prepend system prompt to -p prompt
        full_prompt = f"{system_prompt}\n\n---\n\n{prompt}"
        seat_suffix = f"_{seat}" if seat != "p1" else ""
        retry_suffix = f"_retry{attempt - 1}" if attempt > 1 else ""
        transcript = out_dir / "logs" / (f"agy_day_{day:03}{seat_suffix}_part{part}" + retry_suffix + ".jsonl")
        workdir = out_dir / (f"workdir_{seat}" if seat != "p1" else "workdir")
        workdir.mkdir(exist_ok=True)
        write_agy_guard(workdir, getattr(self, "server", "stardew" if seat == "p1" else f"stardew_{seat}"))

        cmd = [
            *AGY_CMD, "-p", full_prompt,
            "--model", args.model,
            "--output-format", "stream-json",
            "--dangerously-skip-permissions",
        ]
        if getattr(args, "print_timeout", 0):
            cmd.extend(["--print-timeout", f"{args.print_timeout}s"])
        env = opencode_env(workdir)
        env["MCP_TOOL_TIMEOUT"] = "600000"
        env["MCP_TIMEOUT"] = "60000"
        return cmd, env, transcript, workdir

    def parse_event(self, ev: dict) -> list[NormalizedEvent]:
        event = ev.get("event")
        if event == "init":
            init_obj = ev.get("init", {})
            return [NormalizedEvent(kind="init", session_id=ev.get("conversation_id", ""), model=init_obj.get("model", ""))]
        elif event == "step_update":
            su = ev.get("step_update", {})
            step = su.get("step_index", -1)
            if su.get("step_type") == "tool" and su.get("state") == "ACTIVE":
                raw_name = su.get("tool_name", "")
                tinfo = su.get("tool_info", {})
                arg = tinfo.get("parameters", {})
                path = agy_tool_guard.read_path(raw_name, arg)
                if path is not None and agy_own_mcp_file(path, getattr(self, "server", "")):
                    return []  # its own copy of the stardew server's instructions: not a game action, not a leak
                name, is_stardew = normalize_agy_tool(raw_name, arg, getattr(self, "server", ""))
                if not is_stardew:
                    return [NormalizedEvent(kind="tool_call", step=step, tool_name=raw_name, tool_args=arg, is_disallowed=True)]
                return [NormalizedEvent(kind="tool_call", step=step, tool_name=name, tool_args=arg)]
            if su.get("step_type") == "tool" and su.get("state") == "ERROR":
                # the guard's refusal, right after the ACTIVE step: "tool call denied by pre-tool hook: <DENY_REASON>"
                message = str(((su.get("tool_info") or {}).get("error") or {}).get("message") or "")
                if "denied by pre-tool hook" in message:
                    return [NormalizedEvent(kind="tool_denied", step=step, tool_name=su.get("tool_name", ""))]
            return []
        elif event == "result":
            res = ev.get("result", {})
            status = res.get("status", "")
            is_err = status != "SUCCESS"
            return [NormalizedEvent(
                kind="result",
                subtype=status.lower(),
                is_error=is_err,
                num_turns=res.get("num_turns", 0),
                error_message=str(res.get("response", "")),
            )]
        return []

    def text_session(self, args, out_dir: Path, label: str) -> memory.Session:
        return agy_text_session(args, out_dir, label)


class OpenCodeBackend(Backend):
    name = "opencode"
    default_model = "opencode-go/gpt-6-luna"

    @property
    def display_name(self) -> str:
        return "Luna (OpenCode)"

    def prepare(self, args, out_dir: Path, seat: str = "p1") -> None:
        workdir = out_dir / (f"workdir_{seat}" if seat != "p1" else "workdir")
        workdir.mkdir(parents=True, exist_ok=True)
        mcp_url = sessions.mcp_url(API, breaks=not getattr(args, "no_session_breaks", False), seat=seat)
        opencode_cfg = workdir / "opencode.json"
        cfg_data = {
            "$schema": "https://opencode.ai/config.json",
            "mcp": {
                "stardew": {
                    "type": "remote",
                    "url": mcp_url,
                }
            },
            "tools": {
                "bash": False,
                "edit": False,
                "write": False,
                "view": False,
                "read": False,
                "glob": False,
                "grep": False,
                # opencode 2.x calls MCP tools only through `execute`; parse_event kills code that reaches these
                "blender_*": False,
                "godot-mcp-pro_*": False,
            },
            # opencode 2.x turns this into an ordered rule list where the last match wins: deny-all must come first
            "permission": {
                "*": "deny",
                "execute": "allow",
                "stardew_*": "allow",
            },
        }
        opencode_cfg.write_text(json.dumps(cfg_data, indent=2), encoding="utf-8")
        # opencode 2.x lists a remote MCP server's tools only inside a git repo: an --out outside the repo left GPT
        # with `stardew` but no tools (2026-10-20)
        if not (workdir / ".git").exists():
            git_init(workdir)

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1, seat: str = "p1"):
        full_prompt = f"{system_prompt}\n\n---\n\n{prompt}"
        seat_suffix = f"_{seat}" if seat != "p1" else ""
        retry_suffix = f"_retry{attempt - 1}" if attempt > 1 else ""
        transcript = out_dir / "logs" / (f"opencode_day_{day:03}{seat_suffix}_part{part}" + retry_suffix + ".jsonl")
        workdir = out_dir / (f"workdir_{seat}" if seat != "p1" else "workdir")
        workdir.mkdir(parents=True, exist_ok=True)

        cmd = [
            *opencode_run_cmd(),
            "--model", args.model,
            "--format", "json",
            "--auto",
            full_prompt,
        ]
        env = opencode_env(workdir)
        env["MCP_TOOL_TIMEOUT"] = "600000"
        env["MCP_TIMEOUT"] = "60000"
        return cmd, env, transcript, workdir

    def parse_event(self, ev: dict) -> list[NormalizedEvent]:
        ev_type = ev.get("type")
        if ev_type == "step_start":
            return [NormalizedEvent(kind="init", session_id=ev.get("sessionID", ""))]
        elif ev_type == "tool_use" and "part" in ev:
            part = ev.get("part") or {}
            raw_name = part.get("tool") or ""
            state = part.get("state") or {}
            arg = state.get("input") or {}
            if raw_name == "execute":
                names, others = opencode_execute_tools(str(arg.get("code") or ""))
                if others:  # the code reaches another MCP server from the user's global config
                    return [NormalizedEvent(kind="tool_call", tool_name=f"execute:{','.join(others)}", tool_args=arg,
                                            is_disallowed=True)]
                return [NormalizedEvent(kind="tool_call", tool_name=n, tool_args=arg) for n in names] or \
                       [NormalizedEvent(kind="tool_call", tool_name="execute", tool_args=arg)]
            name, is_stardew = normalize_opencode_tool(raw_name, arg)
            if not is_stardew:
                return [NormalizedEvent(kind="tool_call", tool_name=raw_name, tool_args=arg, is_disallowed=True)]
            return [NormalizedEvent(kind="tool_call", tool_name=name, tool_args=arg)]
        elif ev_type == "step_finish" and "part" in ev:
            part = ev.get("part") or {}
            reason = part.get("reason", "stop")
            is_err = reason in ("error", "failed")
            cost = float(part.get("cost") or 0.0)
            return [NormalizedEvent(
                kind="result",
                subtype="success" if reason == "stop" else reason,
                is_error=is_err,
                num_turns=1,
                cost_usd=cost,
                error_message=str(part.get("output", "")),
            )]
        elif ev_type == "error":
            return [NormalizedEvent(
                kind="result",
                subtype="error",
                is_error=True,
                error_message=str(ev.get("error", "opencode error")),
            )]
        return []

    def text_session(self, args, out_dir: Path, label: str) -> memory.Session:
        return opencode_text_session(args, out_dir, label)


def get_backend(name: str) -> Backend:
    name = (name or "claude").lower()
    if name == "agy":
        return AgyBackend()
    elif name == "claude":
        return ClaudeBackend()
    elif name == "opencode":
        return OpenCodeBackend()
    raise ValueError(f"Unknown backend: {name}")


def copy_args_with_seat(args, backend_name: str, model: str):
    d = vars(args).copy() if hasattr(args, "__dict__") else {}
    new_args = argparse.Namespace(**d)
    new_args.backend = backend_name
    new_args.model = model
    return new_args


def _end_unguarded(proc, name: str, unguarded: list[str]) -> None:
    """Timer thread: a disallowed agy call its guard did not refuse in time has run, so the session ends now."""
    unguarded.append(name)
    print(f"  NOT REFUSED BY THE GUARD: {name}: ending the session", flush=True)
    if hasattr(proc, "kill"):
        proc.kill()


def run_session(args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1, seat: str = "p1") -> sessions.PartResult:
    backend_name = getattr(args, "backend", "claude")
    backend = get_backend(backend_name)
    try:
        backend.prepare(args, out_dir, seat=seat)
    except TypeError:
        backend.prepare(args, out_dir)

    try:
        cmd, env, transcript, workdir = backend.build_cmd(args, out_dir, day, prompt, system_prompt, part, attempt, seat=seat)
    except TypeError:
        cmd, env, transcript, workdir = backend.build_cmd(args, out_dir, day, prompt, system_prompt, part, attempt)

    seat_tag = f" [{seat}]" if seat != "p1" else ""
    print(f"[{datetime.now():%H:%M:%S}]{seat_tag} day {day} part {part}: starting {args.model}")
    session_id = ""
    tool_calls = 0
    denied = 0  # agy calls its guard refused in this session
    unproven: dict[int, tuple[threading.Timer, str]] = {}  # step -> (kill timer, tool): agy calls the guard is yet to refuse
    unguarded: list[str] = []  # calls the guard never refused: they ran
    limit = usage_limit.LimitWatcher()
    tracker = usage_log.UsageTracker()
    game_start, t_start = game_state().get("time", ""), time.time()
    with transcript.open("w", encoding="utf-8") as tf:
        try:
            proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False, **CTL.popen_kwargs())
        except TypeError:  # test doubles without creationflags/start_new_session or without stdin
            try:
                proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        text=True, encoding="utf-8", errors="replace", shell=False)
            except TypeError:
                proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        text=True, encoding="utf-8", errors="replace", shell=False)
        if hasattr(proc, "poll"):
            CTL.watch(proc)  # stop_now / Ctrl+C: ends the session once the running tool call has finished
        assert proc.stdout
        GUARD.attach(proc)
        game_watchdog.die_with_runner(proc)
        SESSIONS[seat] = proc
        for line in proc.stdout:
            tf.write(line)
            tf.flush()
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                print("  " + line.rstrip())
                limit.feed_line(line)
                continue
            limit.feed(ev)
            tracker.feed(ev)

            norm_events = backend.parse_event(ev)
            stopped_by_turn_limit = False
            for norm_ev in norm_events:
                if norm_ev.kind == "init":
                    session_id = norm_ev.session_id or session_id
                    model = norm_ev.model or args.model
                    print(f"  session {session_id} model {model}")
                    try:
                        api_post("/api/meta", {"model": backend.display_name, "modelId": model, "session": session_id,
                                               "seat": seat,
                                               "run": f"day {day} · part {part}",
                                               "event": f"{backend.display_name} session {session_id} started ({model}), day {day} part {part}"})
                    except Exception:
                        pass
                elif norm_ev.kind == "tool_denied":
                    pending = unproven.pop(norm_ev.step, None)
                    if pending:
                        pending[0].cancel()
                    print(f"  DENIED BY GUARD: {norm_ev.tool_name} ({denied}/{AGY_DENIED_LIMIT})")
                elif norm_ev.kind == "tool_call":
                    if norm_ev.is_disallowed and backend.name == "agy" and denied < AGY_DENIED_LIMIT:
                        # agy's guard hook refuses it before it runs and tells the model so: the session goes on
                        # (2026-10-20: one search_web stopped Gemini for the day before). agy reports that refusal as an
                        # ERROR step at once; a call not refused within AGY_DENY_PROOF_SECONDS ran, and ends the session.
                        denied += 1
                        timer = threading.Timer(AGY_DENY_PROOF_SECONDS, _end_unguarded, (proc, norm_ev.tool_name, unguarded))
                        timer.daemon = True
                        unproven[norm_ev.step] = (timer, norm_ev.tool_name)
                        timer.start()
                        continue
                    if norm_ev.is_disallowed:
                        print(f"  DISALLOWED TOOL: {norm_ev.tool_name}")
                        for timer, _ in unproven.values():
                            timer.cancel()
                        if hasattr(proc, "kill"):
                            proc.kill()
                        if tool_calls == 0 and backend.name == "opencode":
                            # opencode sometimes starts without the workdir's opencode.json (no stardew server, its own
                            # shell instead): a failed start, retried like a provider error, not a misbehaving model
                            raise BackendError(f"{backend.display_name} started without the stardew tools "
                                               f"(first call: {norm_ev.tool_name})")
                        raise SystemExit(f"Disallowed tool call aborted: {norm_ev.tool_name}")
                    tool_calls += 1
                    name = norm_ev.tool_name
                    arg = norm_ev.tool_args
                    short = arg.get("text") or arg.get("reason") or ""
                    print(f"  #{tool_calls} {name} {short[:90]}")
                    if getattr(args, "max_turns", None) and tool_calls >= args.max_turns and backend.name in ("agy", "opencode"):
                        print(f"  result: error_max_turns turns={tool_calls}")
                        if hasattr(proc, "kill"):
                            proc.kill()
                        stopped_by_turn_limit = True
                        break
                elif norm_ev.kind == "result":
                    cost_str = f" cost=${norm_ev.cost_usd:.3f}" if (norm_ev.cost_usd or backend.name in ("claude", "opencode")) else ""
                    print(f"  result: {norm_ev.subtype} turns={norm_ev.num_turns}{cost_str}")
                    if norm_ev.is_error and tool_calls == 0 and not limit.info:
                        GUARD.explain_failure()  # a dead game makes Claude/agy fail at once: recover instead of stopping
                        err_label = backend.display_name
                        raise BackendError(f"{err_label} error, stopping: {norm_ev.error_message}")
            if stopped_by_turn_limit:
                break
        proc.wait()
        GUARD.detach()
        SESSIONS.pop(seat, None)
        for timer, name in unproven.values():  # the session ended before the guard's refusal showed: it ran
            timer.cancel()
            if name not in unguarded:
                unguarded.append(name)
    if unguarded:  # not "Disallowed tool ...": a broken guard gets no second session (multi_runner)
        raise SystemExit(f"Guard failed: agy ran {', '.join(unguarded)} without its guard refusing it; check the hook in "
                         f"{workdir / '.agents'}")
    if limit.info:  # subscription usage limit: the caller waits for the reset and retries this part
        raise usage_limit.UsageLimitHit(limit.info, tool_calls)
    row = tracker.row(day, part, game_start, game_state().get("time", ""), time.time() - t_start)
    csv_file = out_dir / (f"usage_{seat}.csv" if seat != "p1" else "usage.csv")
    usage_log.append_row(csv_file, row)
    print(f"  usage: {row['total_tokens']:,} tokens (in {row['input_tokens']:,} · out {row['output_tokens']:,} · "
          f"cache read {row['cache_read_input_tokens']:,}), peak context {row['peak_context_tokens']:,}"
          f"{' · planned session break' if tracker.break_seen else ''}")
    return sessions.PartResult(tool_calls, tracker.break_seen)


def claude_text_session(args, out_dir: Path, label: str) -> memory.Session:
    """A memory.Session backed by `claude -p` with no tools and no MCP servers: returns the reply text.

    The material goes into the system prompt file, so a long lessons list never hits the command-line length limit.
    """
    def run(system_prompt: str, prompt: str) -> str:
        sys_file = out_dir / f"system_{label}.md"
        sys_file.write_text(system_prompt, encoding="utf-8")
        mcp_cfg = out_dir / "mcp_none.json"
        mcp_cfg.write_text(json.dumps({"mcpServers": {}}), encoding="utf-8")
        transcript = out_dir / "logs" / f"claude_{label}.jsonl"
        workdir = out_dir / "workdir"
        workdir.mkdir(exist_ok=True)
        cmd = [
            *CLAUDE_CMD, "-p", prompt,
            "--model", args.model,
            "--system-prompt-file", str(sys_file),
            "--mcp-config", str(mcp_cfg), "--strict-mcp-config",
            "--tools", "",
            "--setting-sources", "project",
            "--output-format", "stream-json", "--verbose",
            "--max-turns", "3",
        ]
        reply, failed = "", ""
        with transcript.open("a", encoding="utf-8") as tf:
            proc = subprocess.Popen(cmd, cwd=workdir, env=dict(os.environ), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False)
            assert proc.stdout
            for line in proc.stdout:
                tf.write(line)
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("type") == "result":
                    reply = ev.get("result") or ""
                    if ev.get("is_error"):
                        failed = reply or "claude reported an error"
            proc.wait()
        if failed or not reply.strip():
            raise RuntimeError(failed or "empty reply")
        return reply
    return run


def agy_text_session(args, out_dir: Path, label: str) -> memory.Session:
    """A memory.Session backed by `agy -p` with no MCP tools: returns the reply text."""
    def run(system_prompt: str, prompt: str) -> str:
        full_prompt = f"{system_prompt}\n\n---\n\n{prompt}"
        transcript = out_dir / "logs" / f"agy_{label}.jsonl"
        workdir = out_dir / "workdir_agy_text"  # not a seat's workdir: its guard allows no tool at all
        write_agy_guard(workdir, "")
        cmd = [
            *AGY_CMD, "-p", full_prompt,
            "--model", args.model,
            "--output-format", "stream-json",
            "--dangerously-skip-permissions",
        ]
        reply, failed = "", ""
        with transcript.open("a", encoding="utf-8") as tf:
            proc = subprocess.Popen(cmd, cwd=workdir, env=dict(os.environ), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False)
            assert proc.stdout
            for line in proc.stdout:
                tf.write(line)
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("event") == "result":
                    res = ev.get("result") or {}
                    reply = res.get("response") or ""
                    if res.get("status") != "SUCCESS":
                        failed = reply or f"agy reported status {res.get('status')}"
            proc.wait()
        if failed or not reply.strip():
            raise RuntimeError(failed or "empty reply")
        return reply
    return run


def opencode_text_session(args, out_dir: Path, label: str) -> memory.Session:
    """A memory.Session backed by `opencode run --format json` with no tools: returns the reply text."""
    def run(system_prompt: str, prompt: str) -> str:
        full_prompt = f"{system_prompt}\n\n---\n\n{prompt}"
        transcript = out_dir / "logs" / f"opencode_{label}.jsonl"
        workdir = out_dir / "workdir_text"
        workdir.mkdir(exist_ok=True)
        (workdir / "opencode.json").write_text(json.dumps({
            "tools": {"bash": False, "edit": False, "write": False, "view": False, "read": False, "glob": False, "grep": False},
            "permission": {"*": "deny"},  # 2.x --standalone would otherwise offer the global MCP servers via execute
        }), encoding="utf-8")
        cmd = [
            *opencode_run_cmd(),
            "--model", args.model,
            "--format", "json",
            "--auto",
            full_prompt,
        ]
        reply, failed = "", ""
        with transcript.open("a", encoding="utf-8") as tf:
            proc = subprocess.Popen(cmd, cwd=workdir, env=opencode_env(workdir), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False)
            assert proc.stdout
            for line in proc.stdout:
                tf.write(line)
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("type") == "text" and "part" in ev:
                    part = ev.get("part") or {}
                    reply += str(part.get("text") or "")
                elif ev.get("type") == "error":
                    failed = str(ev.get("error") or "opencode reported an error")
            proc.wait()
        if failed or not reply.strip():
            raise RuntimeError(failed or "empty reply")
        return reply
    return run


def batch_end_memory(args, out_dir: Path, log_dir: Path, current_day: int, seats: list[tuple[str, str, str]] | None = None):
    """Shorter lessons and notes, a digest per finished week (memory.consolidate_batch), printed one line each.

    seats: (seat, backend, model) per AI seat, each consolidated by its own backend; default p1 with args.backend."""
    print(f"[{datetime.now():%H:%M:%S}] batch finished: consolidating lessons, notes and weekly digests")
    for seat, backend_name, model in seats or [("p1", getattr(args, "backend", "claude"), getattr(args, "model", None))]:
        seat_args = copy_args_with_seat(args, backend_name, model)
        label = f"consolidate_day_{current_day:03}" + ("" if seat == "p1" else f"_{seat}")
        session = get_backend(backend_name).text_session(seat_args, out_dir, label)
        for result in memory.consolidate_batch(log_dir, current_day, session, player=seat):
            print(f"  {'' if seat == 'p1' else f'[{seat}] '}{result.describe()}")


def backup_save(out_dir: Path, day: int, keep: int = save_backup.DEFAULT_KEEP):
    src = save_folder()
    if not src:
        return
    try:
        dst = save_backup.backup_folder(src, out_dir, day, keep)
    except save_backup.BackupError as e:
        print(f"  WARNING: save NOT backed up: {e}")
        return
    print(f"  save backed up → {dst}")


def _do_backup(out_dir: Path, day: int, keep: int):
    try:
        backup_save(out_dir, day, keep)
    except TypeError:
        backup_save(out_dir, day)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--days", type=int, default=1, help="How many in-game days to play in this run")
    ap.add_argument("--until-day", type=int, default=0, help="Stop after this farm day number")
    ap.add_argument("--backend", choices=["claude", "agy", "opencode"], default=None,
                    help="Player AI backend: 'claude' (Claude Code), 'agy' (Antigravity Gemini 3.8 Flash), or 'opencode' (OpenCode Go Luna)")
    ap.add_argument("--model", default=None,
                    help="Model identifier (default: claude-haiku-5-5 for claude, gemini-3.8-flash-medium for agy, opencode-go/gpt-6-luna for opencode)")
    ap.add_argument("--goal", default=DEFAULT_GOAL)
    ap.add_argument("--language", default=None, help="Language of the AI's thoughts, reasons and diary (default: the mod's ThoughtLanguage, else Thai)")
    ap.add_argument("--max-turns", type=int, default=600)
    ap.add_argument("--max-parts", type=int, default=4, help="Sessions per day if the AI stops before sleeping (planned session breaks do not count)")
    ap.add_argument("--max-breaks", type=int, default=sessions.DEFAULT_MAX_BREAKS, help="Planned session breaks per day before a part counts toward --max-parts")
    ap.add_argument("--no-session-breaks", action="store_true", help="One session per day, as before (the baseline for comparing usage.csv)")
    ap.add_argument("--pass-out-timeout", type=int, default=1800,
                    help="Seconds to let game time run when a day's sessions are used up, waiting for the farmer to pass out")
    ap.add_argument("--out", default=None,
                    help="Output directory (default: runs for claude, runs-gemini for agy, runs-opencode for opencode, runs-multi for multi)")
    ap.add_argument("--skip-smoke", action="store_true", help="Do not run runner/smoke.py before the batch")
    ap.add_argument("--skip-consolidation", action="store_true", help="Do not shorten lessons.md and notes.md after the batch")
    ap.add_argument("--keep-backups", type=int, default=save_backup.DEFAULT_KEEP,
                    help="Keep the newest N save backups (default %(default)s, 0 = keep all)")

    # Multi-AI flags (Issue #41)
    ap.add_argument("--multi", action="store_true", help="Run multi-AI co-op across multiple seats (default: p1, p2, p3)")
    ap.add_argument("--seats", default=None, help="Comma-separated active seats, e.g. 'p1,p2' or 'p1,p2,p3'")
    ap.add_argument("--backends", default=None, help="Comma-separated backends per seat, e.g. 'claude,agy,opencode'")
    ap.add_argument("--models", default=None, help="Comma-separated models per seat")
    ap.add_argument("--personas-dir", default=None, help="Directory containing {seat}.md persona files")
    ap.add_argument("--emergent-personas", action=argparse.BooleanOptionalAction, default=True,
                    help="Characters emerge from self/people notes written in journal (Issue #48, default: True)")
    ap.add_argument("--outage-cap-hours", type=float, default=6,
                    help="Multi seat: how long the world stays held waiting for a seat's provider (outage or usage limit) "
                         "before the batch stops for a human (default 6)")
    for s in ("p1", "p2", "p3", "p4"):
        ap.add_argument(f"--{s}-backend", choices=["claude", "agy", "opencode"], default=None, help=f"Backend for seat {s}")
        ap.add_argument(f"--{s}-model", default=None, help=f"Model for seat {s}")

    usage_limit.add_args(ap)
    game_watchdog.add_args(ap)
    args = ap.parse_args()

    personas_dir = Path(args.personas_dir) if args.personas_dir else None
    multi_cfg = multi_runner.parse_multi_config(args, personas_dir)
    is_multi = len(multi_cfg.seats) > 1 or args.multi
    args.backend = args.backend or "claude"  # after the seats: a default here would override p2=agy, p3=opencode

    backend = get_backend(args.backend)
    if args.model is None:
        args.model = backend.default_model

    if args.out is None:
        if is_multi:
            args.out = str(DEFAULT_OUT.parent / "runs-multi")
        elif args.backend == "agy":
            args.out = str(DEFAULT_OUT.parent / "runs-gemini")
        elif args.backend == "opencode":
            args.out = str(DEFAULT_OUT.parent / "runs-opencode")
        else:
            args.out = str(DEFAULT_OUT)

    out_dir = Path(args.out).resolve()  # the CLIs run inside out_dir/workdir*, so relative paths would break
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    template = (HERE / "system_prompt.md").read_text(encoding="utf-8")
    template = template.replace("{MODEL_NAME}", backend.display_name)
    global GUARD
    GUARD = game_watchdog.from_args(args, log_dir, api_get, API)

    CTL.start()  # clears stale stop requests (the last batch ended holding the world); Ctrl+C = stop after the day / after the call / abort
    if not args.skip_smoke:
        wait_ready()
        print("Running smoke tests before the batch (use --skip-smoke to skip)...")
        if subprocess.run([sys.executable, str(HERE / "smoke.py")]).returncode != 0:
            raise SystemExit("Smoke tests failed, not starting the batch. Fix the failing case above, or pass --skip-smoke.")

    waiter = usage_limit.LimitWaiter.from_args(args, post=api_post)
    ready_state = game_state()
    check_log_dir(ready_state, log_dir)
    check_wallets(ready_state, is_multi)
    lang = language.resolve(args.language, ready_state)
    print(language.describe(lang, ready_state))
    template = language.fill_prompt(template, lang)

    days_played = 0
    stopped_early = False
    try:
        batch_days: list[int] = []
        while True:
            if CTL.stop_between_days():
                print(f"Stopped as requested after {days_played} day(s). Run again to continue.")
                break
            CTL.wait_while_paused()
            state = GUARD.ready(wait_ready)
            # After a pass-out the mod writes its record once the morning has settled; wait for that so the prompt can use it.
            if game_state().get("ready"):
                settled = pass_out.wait_settled(game_state, sleep=time.sleep)
                if settled.get("ready"):
                    state = settled
            day = state["day"]
            if args.until_day and day > args.until_day:
                break
            if not args.until_day and days_played >= args.days:
                break

            # relationships.md is written from game data (not by the AI) once per day, right before the prompt is built.
            relationships.update(log_dir, lambda: api_get("/api/relationships"))
            recent_d = recent_diaries(log_dir, day)

            if is_multi:
                system_prompts = {}
                for s in multi_cfg.seats:
                    sc = multi_cfg.seat_configs[s]
                    seat_backend = get_backend(sc.backend_name)
                    raw_template = (HERE / "system_prompt.md").read_text(encoding="utf-8")
                    raw_template = raw_template.replace("{MODEL_NAME}", seat_backend.display_name)
                    raw_template = language.fill_prompt(raw_template, lang)
                    s_template = personas.customize_system_prompt(
                        raw_template,
                        seat=s,
                        active_seats=multi_cfg.seats,
                        personas={seat: cfg.persona for seat, cfg in multi_cfg.seat_configs.items()},
                        language=lang.get("name", "Thai") if isinstance(lang, dict) else "Thai",
                        emergent=getattr(args, "emergent_personas", True),
                        self_note=memory.read_self(log_dir, s),
                        people_note=memory.read_people(log_dir, s),
                    )
                    system_prompts[s] = memory.inject(
                        s_template.replace("{RELATIONSHIPS}", relationships.for_prompt(log_dir)),
                        log_dir, args.goal, recent_d, player=s
                    )

                def play_day(attempt: int) -> dict:
                    return multi_runner.run_multi_day(
                        args, out_dir, log_dir, day, system_prompts, multi_cfg, api_get, api_post, wait_ready
                    )

                s = GUARD.run_day(day, play_day)
            else:
                system_prompt = memory.inject(
                    template.replace("{RELATIONSHIPS}", relationships.for_prompt(log_dir)),
                    log_dir, args.goal, recent_d
                )
                morning = run_stats.morning_for_prompt(log_dir, day)

                def call_session(p: str, part: int, attempt: int):
                    if attempt == 1:
                        return run_session(args, out_dir, day, p, system_prompt, part)
                    try:
                        return run_session(args, out_dir, day, p, system_prompt, part, attempt)
                    except TypeError:
                        return run_session(args, out_dir, day, p, system_prompt, part)

                def play_day(attempt: int) -> dict:
                    cur_state = wait_ready() if attempt > 1 else state
                    parts, planned = sessions.PartCounter(args.max_parts, args.max_breaks), False
                    part = 0
                    while True:
                        part += 1
                        if part == 1:
                            prompt = briefing.morning_prompt(day, briefing.build(api_get, day, "p1", out_dir=out_dir, sleep=time.sleep))
                            if morning:
                                prompt += "\n\n" + morning
                            prompt = memory.with_reminder(prompt, cur_state, log_dir)
                            prompt = pass_out.with_morning_notice(prompt, cur_state, lambda d: last_actions(log_dir, d))
                        else:
                            prompt = continue_prompt(day, log_dir, planned=planned)
                        res = usage_limit.run_part(waiter, lambda p, n: call_session(p, part, n),
                                                   prompt, lambda: continue_prompt(day, log_dir, planned=False), day, part,
                                                   day_done=lambda: wait_ready()["day"] > day)
                        if CTL.stop_now_requested():
                            return {"ready": True, "day": game_state().get("day", day), "stop_now": True}
                        planned = res.planned_break if isinstance(res, sessions.PartResult) else False
                        GUARD.check()
                        time.sleep(3)
                        s = wait_ready()
                        if s["day"] > day:
                            return s
                        print(f"  day {day} not finished after part {part}{' (planned session break)' if planned else ''}; continuing")
                        if not parts.after_part(planned):
                            return s

                s = GUARD.run_day(day, play_day)

            if s.get("stop_now"):
                if s.get("day", day) > day:  # the last call was go_to_bed and the day did finish
                    _do_backup(out_dir, day, args.keep_backups)
                    print(f"stop_now: day {day} had finished.")
                else:
                    print(f"stop_now: the session ended after its last tool call; day {day} is not finished.")
                print("The game stays frozen until you resume (F8 or run_control.py resume). Run again to continue.")
                return
            if not is_multi and s["day"] <= day and game_state().get("ready"):
                s = pass_out.let_time_run_out(day, game_state, api_post, time.sleep, timeout=args.pass_out_timeout)
            if s.get("ready") and s["day"] > day:
                _do_backup(out_dir, day, args.keep_backups)
                report_failures(log_dir, day)
                if is_multi:
                    for seat in multi_cfg.seats:
                        seat_csv = out_dir / (f"usage_{seat}.csv" if seat != "p1" else "usage.csv")
                        if seat_csv.exists():
                            line = usage_log.day_line(seat_csv, day)
                            if "no rows" not in line:
                                print(f"  [{seat}] {line}")
                else:
                    print(usage_log.day_line(out_dir / "usage.csv", day))
                days_played += 1
                batch_days.append(day)
                run_stats.finish_day(out_dir, log_dir, day, batch_days)
            else:
                print(f"Day {day} still not finished after {args.max_parts} sessions and {args.pass_out_timeout}s of released "
                      "time — stopping for a human to check.")
                stopped_early = True
                break

        if days_played and not stopped_early and not args.skip_consolidation:
            seats = [(sc.seat, sc.backend_name, sc.model) for sc in multi_cfg.seat_configs.values()] if is_multi else None
            CTL.request("stop_now")  # hold the world: seats would time out during consolidation and the clock run
            batch_end_memory(args, out_dir, log_dir, wait_ready()["day"], seats)
    finally:
        # Hold the world whenever the runner exits (done, stopped or crashed): otherwise every seat times out and the
        # game plays and saves whole days with idle farmers. The next run's CTL.start() (clear_stop) lifts it.
        CTL.request("stop_now")


if __name__ == "__main__":
    main()
