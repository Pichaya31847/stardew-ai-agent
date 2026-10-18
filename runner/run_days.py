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
import shutil
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

import briefing
import game_watchdog
import language
import memory
import pass_out
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
DEFAULT_OUT = HERE.parent / "runs"
GUARD = game_watchdog.NullWatchdog()  # replaced in main() unless --no-watchdog
DEFAULT_GOAL = (
    "Build the best farm you can in 100 days. You choose the strategy: what to grow, how to earn money, "
    "which villagers to befriend and what to upgrade."
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


def last_actions(log_dir: Path, day: int, n: int = 25) -> str:
    f = log_dir / f"day_{day:03}.jsonl"
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


def continue_prompt(day: int, log_dir: Path, planned: bool = False) -> str:
    sessions.add_context_provider(memory.continuation_context)
    recent = last_actions(log_dir, day, 8 if planned else 25)
    prompt = sessions.continuation_prompt(day, log_dir, planned, recent=recent)
    return prompt + pass_out.pending_reminder(game_state())


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
    kind: str  # "init", "tool_call", "result"
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
    "interact", "respond", "close_menu", "menu", "say", "whisper", "emote", "give",
    "farm_summary", "harvest_all", "water_all", "clear_area", "till_area", "plant",
    "craft", "cook", "eat", "quests", "accept_quest", "crack_geode", "watch_tv",
    "ship", "shop_list", "buy", "sell", "calendar", "festival", "crops", "item_info",
    "equip", "special_orders", "fight", "leave_mine", "fish", "build", "upgrade_house",
    "buy_animal", "animals", "choose_profession", "trash", "chest", "plan_read", "plan_edit"
}


def normalize_agy_tool(raw_name: str, arg: dict) -> tuple[str, bool]:
    if raw_name.startswith("mcp_stardew_"):
        return raw_name[len("mcp_stardew_"):], True
    if raw_name.startswith("stardew_"):
        return raw_name[len("stardew_"):], True
    if raw_name == "call_mcp_tool":
        srv = (arg.get("server_name") or arg.get("server") or "").lower()
        if srv == "stardew":
            tool_name = arg.get("tool_name") or arg.get("name") or "tool"
            return tool_name, True
        return raw_name, False
    if raw_name in KNOWN_STARDEW_TOOLS:
        return raw_name, True
    return raw_name, False


def ensure_agy_mcp(mcp_url: str) -> None:
    """Ensure the 'stardew' MCP server is registered in agy CLI and points to the running game."""
    try:
        res = subprocess.run([*AGY_CMD, "mcp", "list"], capture_output=True, text=True, check=False)
        output = res.stdout or ""
        found_stardew = False
        stardew_enabled = False
        for line in output.splitlines():
            parts = line.split()
            if parts and parts[0] == "stardew":
                found_stardew = True
                if len(parts) >= 3 and parts[2].lower() == "enabled":
                    stardew_enabled = True
                break
        if not found_stardew:
            subprocess.run([*AGY_CMD, "mcp", "add", "stardew", mcp_url], capture_output=True, text=True, check=False)
        elif not stardew_enabled:
            subprocess.run([*AGY_CMD, "mcp", "enable", "stardew"], capture_output=True, text=True, check=False)
    except Exception:
        pass


class Backend:
    name: str = "base"
    default_model: str = ""

    @property
    def display_name(self) -> str:
        return "AI"

    def prepare(self, args, out_dir: Path) -> None:
        pass

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1) -> tuple[list[str], dict[str, str], Path, Path]:
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

    def prepare(self, args, out_dir: Path) -> None:
        pass

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1):
        mcp_cfg = out_dir / "mcp.json"
        mcp_url = sessions.mcp_url(API, breaks=not getattr(args, "no_session_breaks", False))
        mcp_cfg.write_text(json.dumps({"mcpServers": {"stardew": {"type": "http", "url": mcp_url}}}), encoding="utf-8")
        sys_file = out_dir / f"system_day_{day:03}.md"
        sys_file.write_text(system_prompt, encoding="utf-8")

        transcript = out_dir / "logs" / (f"claude_day_{day:03}_part{part}" + (f"_retry{attempt - 1}" if attempt > 1 else "") + ".jsonl")
        workdir = out_dir / "workdir"
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
        env = dict(os.environ)
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

    def prepare(self, args, out_dir: Path) -> None:
        mcp_url = sessions.mcp_url(API, breaks=not getattr(args, "no_session_breaks", False))
        ensure_agy_mcp(mcp_url)

    def build_cmd(self, args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1):
        # agy has no --system-prompt-file: prepend system prompt to -p prompt
        full_prompt = f"{system_prompt}\n\n---\n\n{prompt}"
        transcript = out_dir / "logs" / (f"agy_day_{day:03}_part{part}" + (f"_retry{attempt - 1}" if attempt > 1 else "") + ".jsonl")
        workdir = out_dir / "workdir"
        workdir.mkdir(exist_ok=True)

        cmd = [
            *AGY_CMD, "-p", full_prompt,
            "--model", args.model,
            "--output-format", "stream-json",
            "--dangerously-skip-permissions",
        ]
        if getattr(args, "print_timeout", 0):
            cmd.extend(["--print-timeout", f"{args.print_timeout}s"])
        env = dict(os.environ)
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
            if su.get("step_type") == "tool" and su.get("state") == "ACTIVE":
                raw_name = su.get("tool_name", "")
                tinfo = su.get("tool_info", {})
                arg = tinfo.get("parameters", {})
                name, is_stardew = normalize_agy_tool(raw_name, arg)
                if not is_stardew:
                    return [NormalizedEvent(kind="tool_call", tool_name=raw_name, tool_args=arg, is_disallowed=True)]
                return [NormalizedEvent(kind="tool_call", tool_name=name, tool_args=arg)]
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


def get_backend(name: str) -> Backend:
    name = (name or "claude").lower()
    if name == "agy":
        return AgyBackend()
    elif name == "claude":
        return ClaudeBackend()
    raise ValueError(f"Unknown backend: {name}")


def run_session(args, out_dir: Path, day: int, prompt: str, system_prompt: str, part: int, attempt: int = 1) -> sessions.PartResult:
    backend_name = getattr(args, "backend", "claude")
    backend = get_backend(backend_name)
    backend.prepare(args, out_dir)

    cmd, env, transcript, workdir = backend.build_cmd(args, out_dir, day, prompt, system_prompt, part, attempt)

    print(f"[{datetime.now():%H:%M:%S}] day {day} part {part}: starting {args.model}")
    session_id = ""
    tool_calls = 0
    limit = usage_limit.LimitWatcher()
    tracker = usage_log.UsageTracker()
    game_start, t_start = game_state().get("time", ""), time.time()
    with transcript.open("w", encoding="utf-8") as tf:
        try:
            proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False, **CTL.popen_kwargs())
        except TypeError:  # test doubles without creationflags/start_new_session
            proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", shell=False)
        if hasattr(proc, "poll"):
            CTL.watch(proc)  # stop_now / Ctrl+C: ends the session once the running tool call has finished
        assert proc.stdout
        GUARD.attach(proc)
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
                                               "run": f"day {day} · part {part}",
                                               "event": f"{backend.display_name} session {session_id} started ({model}), day {day} part {part}"})
                    except Exception:
                        pass
                elif norm_ev.kind == "tool_call":
                    if norm_ev.is_disallowed:
                        print(f"  DISALLOWED TOOL: {norm_ev.tool_name}")
                        if hasattr(proc, "kill"):
                            proc.kill()
                        raise SystemExit(f"Disallowed tool call aborted: {norm_ev.tool_name}")
                    tool_calls += 1
                    name = norm_ev.tool_name
                    arg = norm_ev.tool_args
                    short = arg.get("text") or arg.get("reason") or ""
                    print(f"  #{tool_calls} {name} {short[:90]}")
                    if getattr(args, "max_turns", None) and tool_calls >= args.max_turns and backend.name == "agy":
                        print(f"  result: error_max_turns turns={tool_calls}")
                        if hasattr(proc, "kill"):
                            proc.kill()
                        stopped_by_turn_limit = True
                        break
                elif norm_ev.kind == "result":
                    cost_str = f" cost=${norm_ev.cost_usd:.3f}" if (norm_ev.cost_usd or backend.name == "claude") else ""
                    print(f"  result: {norm_ev.subtype} turns={norm_ev.num_turns}{cost_str}")
                    if norm_ev.is_error and tool_calls == 0 and not limit.info:
                        GUARD.explain_failure()  # a dead game makes Claude/agy fail at once: recover instead of stopping
                        err_label = "Claude" if backend.name == "claude" else "agy"
                        raise SystemExit(f"{err_label} error, stopping: {norm_ev.error_message}")
            if stopped_by_turn_limit:
                break
        proc.wait()
        GUARD.detach()
    if limit.info:  # subscription usage limit: the caller waits for the reset and retries this part
        raise usage_limit.UsageLimitHit(limit.info, tool_calls)
    row = tracker.row(day, part, game_start, game_state().get("time", ""), time.time() - t_start)
    usage_log.append_row(out_dir / "usage.csv", row)
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
        workdir = out_dir / "workdir"
        workdir.mkdir(exist_ok=True)
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


def batch_end_memory(args, out_dir: Path, log_dir: Path, current_day: int):
    """Shorter lessons and notes, a digest per finished week (memory.consolidate_batch), printed one line each."""
    print(f"[{datetime.now():%H:%M:%S}] batch finished: consolidating lessons, notes and weekly digests")
    backend = get_backend(getattr(args, "backend", "claude"))
    session = backend.text_session(args, out_dir, f"consolidate_day_{current_day:03}")
    for result in memory.consolidate_batch(log_dir, current_day, session):
        print("  " + result.describe())


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
    ap.add_argument("--backend", choices=["claude", "agy"], default="claude",
                    help="Player AI backend: 'claude' (Claude Code) or 'agy' (Antigravity Gemini 3.8 Flash)")
    ap.add_argument("--model", default=None,
                    help="Model identifier (default: claude-haiku-5-5 for claude, gemini-3.8-flash-medium for agy)")
    ap.add_argument("--goal", default=DEFAULT_GOAL)
    ap.add_argument("--language", default=None, help="Language of the AI's thoughts, reasons and diary (default: the mod's ThoughtLanguage, else Thai)")
    ap.add_argument("--max-turns", type=int, default=600)
    ap.add_argument("--max-parts", type=int, default=4, help="Sessions per day if the AI stops before sleeping (planned session breaks do not count)")
    ap.add_argument("--max-breaks", type=int, default=sessions.DEFAULT_MAX_BREAKS, help="Planned session breaks per day before a part counts toward --max-parts")
    ap.add_argument("--no-session-breaks", action="store_true", help="One session per day, as before (the baseline for comparing usage.csv)")
    ap.add_argument("--pass-out-timeout", type=int, default=1800,
                    help="Seconds to let game time run when a day's sessions are used up, waiting for the farmer to pass out")
    ap.add_argument("--out", default=None,
                    help="Output directory (default: runs for claude, runs-gemini for agy)")
    ap.add_argument("--skip-smoke", action="store_true", help="Do not run runner/smoke.py before the batch")
    ap.add_argument("--skip-consolidation", action="store_true", help="Do not shorten lessons.md and notes.md after the batch")
    ap.add_argument("--keep-backups", type=int, default=save_backup.DEFAULT_KEEP,
                    help="Keep the newest N save backups (default %(default)s, 0 = keep all)")
    usage_limit.add_args(ap)
    game_watchdog.add_args(ap)
    args = ap.parse_args()

    backend = get_backend(args.backend)
    if args.model is None:
        args.model = backend.default_model
    if args.out is None:
        if args.backend == "agy":
            args.out = str(DEFAULT_OUT.parent / "runs-gemini")
        else:
            args.out = str(DEFAULT_OUT)

    out_dir = Path(args.out)
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    template = (HERE / "system_prompt.md").read_text(encoding="utf-8")
    template = template.replace("{MODEL_NAME}", backend.display_name)
    global GUARD
    GUARD = game_watchdog.from_args(args, log_dir, api_get, API)

    if not args.skip_smoke:
        wait_ready()
        print("Running smoke tests before the batch (use --skip-smoke to skip)...")
        if subprocess.run([sys.executable, str(HERE / "smoke.py")]).returncode != 0:
            raise SystemExit("Smoke tests failed, not starting the batch. Fix the failing case above, or pass --skip-smoke.")

    waiter = usage_limit.LimitWaiter.from_args(args, post=api_post)
    CTL.start()  # clears stale stop requests; Ctrl+C = stop after the day / after the call / abort
    ready_state = game_state()
    lang = language.resolve(args.language, ready_state)
    print(language.describe(lang, ready_state))
    template = language.fill_prompt(template, lang)

    days_played = 0
    stopped_early = False
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
        # Placeholders filled by memory.inject: "{GOAL}", "{LESSONS}", "{PLAN}", "{NOTES}", "{MEMORY}"
        relationships.update(log_dir, lambda: api_get("/api/relationships"))
        system_prompt = memory.inject(
            template.replace("{RELATIONSHIPS}", relationships.for_prompt(log_dir)),
            log_dir, args.goal, recent_diaries(log_dir, day)
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
            # After a crash the watchdog calls this again: the day restarts from the morning with a fresh part 1.
            cur_state = wait_ready() if attempt > 1 else state
            parts, planned = sessions.PartCounter(args.max_parts, args.max_breaks), False
            part = 0
            while True:
                part += 1
                if part == 1:
                    # The morning briefing replaces "start with status and look" (falls back to it if the mod has none).
                    prompt = briefing.morning_prompt(day, briefing.build(api_get, day, "p1", out_dir=out_dir, sleep=time.sleep))
                    if morning:
                        prompt += "\n\n" + morning
                    prompt = memory.with_reminder(prompt, cur_state, log_dir)
                    # The previous day ended by passing out: say what happened and ask for that day's journal first.
                    prompt = pass_out.with_morning_notice(prompt, cur_state, lambda d: last_actions(log_dir, d))
                else:
                    prompt = continue_prompt(day, log_dir, planned=planned)
                # a usage-limit stop waits for the reset and retries this same part (it does not count toward --max-parts)
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
        if s["day"] <= day and game_state().get("ready"):
            # Every session of the day is used and the farmer is still up. The mod keeps the world frozen while no
            # action runs, so release it and let the clock reach 2:00 AM: the farmer passes out and a new day starts.
            s = pass_out.let_time_run_out(day, game_state, api_post, time.sleep, timeout=args.pass_out_timeout)
        if s.get("ready") and s["day"] > day:
            _do_backup(out_dir, day, args.keep_backups)
            report_failures(log_dir, day)
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
        batch_end_memory(args, out_dir, log_dir, wait_ready()["day"])


if __name__ == "__main__":
    main()
