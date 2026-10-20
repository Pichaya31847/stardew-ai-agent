"""Multi-AI concurrent session coordinator and day phase manager (Issue #41).

Manages split-screen co-op sessions across seats (p1, p2, p3) with day phases:
  Day: Free Play from 6:00 (concurrent sessions, independent continuation if one stops early). There is no
       meeting: the farmers find each other, and `say` reaches only those who can see the speaker.
  Evening: Private Diary & Bedtime (journal before bed, all in bed advances the day)

Standard library only.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import briefing
import game_watchdog
import memory
import pass_out
import personas
import run_days
import sessions
import usage_limit

PROBE_EVERY = 600     # s between health probes of a seat whose CLI is alive but stopped holding the world (timed out)
HOLD_AFTER_KILL = 600  # s the world stays paused after ending sessions: the mod drops their held calls at 540 s
HELD_TIMEOUT = 420    # s to wait for running actions to finish before the pause really freezes (actions cap at 6 min)
OUTAGE_RETRY = 300    # s between retries when the provider is down without a usage-limit reset time


@dataclass
class SeatConfig:
    seat: str
    backend_name: str
    model: str
    persona: personas.Persona


@dataclass
class MultiConfig:
    seats: list[str] = field(default_factory=lambda: ["p1", "p2", "p3"])
    seat_configs: dict[str, SeatConfig] = field(default_factory=dict)


def is_in_bed(state: dict, seat: str) -> bool:
    for p in state.get("players", []):
        if p.get("id") == seat:
            s_state = p.get("state")
            if isinstance(s_state, dict):
                return bool(s_state.get("inBed"))
    return False


def went_to_bed(state: dict, seat: str, log_dir: Path, day: int) -> bool:
    """In bed for the night: go_to_bed needs today's diary and is still waiting in the sleep check. Every farmer wakes up
    on its bed at 6:00 (2026-10-19: Gemini stopped at 6:00 held the world 5 minutes), and a farmer woken from bed still
    stands on it (2026-10-20: GPT, woken at 13:00, ended its session there and was never relaunched)."""
    p = next((p for p in state.get("players", []) if p.get("id") == seat), {})
    asleep = p.get("action") == "go_to_bed" or (p.get("state") or {}).get("menu") == "ReadyCheckDialog"
    return is_in_bed(state, seat) and asleep and memory.diary_path(log_dir, day, seat).exists()


def all_in_bed(state: dict, seats: list[str]) -> bool:
    return all(is_in_bed(state, s) for s in seats)


def any_released(state: dict, seats: list[str]) -> bool:
    for p in state.get("players", []):
        if p.get("id") in seats and p.get("released"):
            return True
    return False


def parse_multi_config(args, personas_dir: Path | None = None) -> MultiConfig:
    """Build MultiConfig from CLI arguments."""
    seats_arg = getattr(args, "seats", None)
    if seats_arg:
        seats = [s.strip().lower() for s in seats_arg.split(",") if s.strip()]
    elif getattr(args, "multi", False):
        seats = ["p1", "p2", "p3"]
    else:
        seats = ["p1"]

    all_personas = personas.load_all_personas(seats, personas_dir)
    seat_configs = {}
    default_backends = {"p1": "claude", "p2": "agy", "p3": "opencode"}

    backends_arg = getattr(args, "backends", None)
    backends_list = [b.strip() for b in backends_arg.split(",")] if backends_arg else []

    models_arg = getattr(args, "models", None)
    models_list = [m.strip() for m in models_arg.split(",")] if models_arg else []

    for i, seat in enumerate(seats):
        backend_name = (
            getattr(args, f"{seat}_backend", None)
            or (backends_list[i] if i < len(backends_list) else None)
            or getattr(args, "backend", None)
            or default_backends.get(seat, "claude")
        )
        backend = run_days.get_backend(backend_name)
        model = (
            getattr(args, f"{seat}_model", None)
            or (models_list[i] if i < len(models_list) else None)
            or getattr(args, "model", None)
            or backend.default_model
        )
        seat_configs[seat] = SeatConfig(
            seat=seat,
            backend_name=backend_name,
            model=model,
            persona=all_personas[seat],
        )

    return MultiConfig(
        seats=seats,
        seat_configs=seat_configs,
    )


def free_play_prompt(day: int, seat: str, persona: personas.Persona, briefing_text: str) -> str:
    if day <= 1:
        first = ("This is your first morning here. The other newcomers are strangers to you: you only know their names "
                 "from Mayor Lewis's letter. They live in the cabins near yours on this farm.\n")
    else:
        first = ""
    return (
        f"{briefing_text}\n\n"
        f"[Day {day}]\n"
        f"{first}"
        f"Spend the day as you choose. `say` is heard only by people who can see you, so to talk with someone, go where they are.\n"
        f"When your work is done or evening approaches, write your private journal with `journal` "
        f"(including your evolving `self` identity and notes on other `people`), then call `go_to_bed`."
    )


class MultiDayRunner:
    """Coordinates concurrent sessions for all active seats across a single day."""

    def __init__(self, args, out_dir: Path, log_dir: Path, multi_cfg: MultiConfig, api_get_fn, api_post_fn, wait_ready_fn):
        self.args = args
        self.out_dir = out_dir
        self.log_dir = log_dir
        self.cfg = multi_cfg
        self.api_get = api_get_fn
        self.api_post = api_post_fn
        self.wait_ready = wait_ready_fn

    # ------------------------------------------------------------------ outages

    def probe(self, seat: str) -> str | None:
        """Ask the seat's provider for a one-word reply: None when it answers, else the error text."""
        sc = self.cfg.seat_configs[seat]
        seat_args = run_days.copy_args_with_seat(self.args, sc.backend_name, sc.model)
        try:
            run_days.get_backend(sc.backend_name).text_session(seat_args, self.out_dir, f"probe_{seat}")(
                "You are a health check.", "Reply with the single word OK.")
            return None
        except Exception as e:
            return str(e) or type(e).__name__

    def hold_for_outage(self, day: int, seat: str, error, alive: dict[str, threading.Thread]) -> list[str] | None:
        """The seat's provider is down or over its usage limit: freeze the whole world (run-control pause, so the
        recorder cuts the time too), end the other sessions cleanly and wait until the provider answers.

        Returns the seats whose sessions were ended, to relaunch with the "continue" prompt, or None when the
        provider answers at once (not an outage: the part counts as usual, so a broken CLI can't loop). Raises SystemExit
        (world still held) when the outage lasts longer than --outage-cap-hours.
        """
        info = error.info if isinstance(error, usage_limit.UsageLimitHit) else None
        if info is None:
            err = self.probe(seat)
            if err is None:
                return None
            info = _limit_info(str(error) if error else err)
        cap = float(getattr(self.args, "outage_cap_hours", 6)) * 3600
        deadline = time.time() + cap
        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} provider down ({info.message[:120]}): holding the world")
        self._control("pause")
        t0 = time.time()
        while time.time() - t0 < HELD_TIMEOUT and not (self._get_quiet("/api/control") or {}).get("held"):
            time.sleep(2)
        state = self._get_quiet("/api/state") or {}
        killed = [s for s, t in alive.items() if t.is_alive() and not is_in_bed(state, s)]
        for s in killed:
            game_watchdog.kill_tree(run_days.SESSIONS.get(s))
        for s in killed:
            alive[s].join(timeout=30)
        killed_at = time.time()

        unknown_reset = info.reset_at is None and not usage_limit.looks_like_limit(info.message)
        retry = OUTAGE_RETRY if unknown_reset else 60 * float(getattr(self.args, "limit_retry_minutes", usage_limit.DEFAULT_RETRY_MINUTES))
        margin = float(getattr(self.args, "limit_margin_seconds", usage_limit.DEFAULT_MARGIN_SECONDS))
        waiter = usage_limit.LimitWaiter(self.api_post, retry_seconds=retry, margin_seconds=margin, max_waits=10 ** 9)
        attempt = 1
        while True:
            plan = usage_limit.plan_wait(info, usage_limit._now(), retry, margin)
            if plan.until.timestamp() > deadline:
                raise SystemExit(
                    f"{seat}: provider still down after the {cap / 3600:g} h outage cap; the world stays held. Day {day} "
                    f"was not finished: close the game without saving, run `py runner/restore_day.py --day {day - 1} "
                    f"--out {self.out_dir}`, then run the same command again.")
            waiter.wait(usage_limit.UsageLimitHit(info), day, 0, attempt)
            err = self.probe(seat)
            if err is None:
                break
            info, attempt = _limit_info(err), attempt + 1
        if killed:
            time.sleep(max(0.0, HOLD_AFTER_KILL - (time.time() - killed_at)))
        self._control("resume")
        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} provider is back: resuming day {day}")
        return killed

    def _control(self, action: str) -> None:
        try:
            self.api_post("/api/control", {"action": action})
        except Exception:
            pass

    def _get_quiet(self, path: str) -> dict | None:
        try:
            return self.api_get(path)
        except Exception:
            return None

    def run_free_play_phase(self, day: int, system_prompts: dict[str, str]) -> dict:
        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] Day {day}: Starting Free Play for {', '.join(self.cfg.seats)}")

        parts = {s: sessions.PartCounter(self.args.max_parts, self.args.max_breaks) for s in self.cfg.seats}
        cur_part = {s: 1 for s in self.cfg.seats}
        done_seats = set()
        active_threads: dict[str, threading.Thread] = {}
        seat_results: dict[str, sessions.PartResult] = {}
        stopped: set[str] = set()  # seats whose session raised SystemExit: not relaunched
        slipped: set[str] = set()  # seats that already used their one disallowed-tool restart today
        trouble: dict[str, object] = {}  # seat -> UsageLimitHit / BackendError of its last part: hold the world
        probed: dict[str, float] = {}
        prompts: dict[str, str] = {}  # seat -> prompt of its last part, repeated when that part never got going

        def launch_seat_session(seat: str, prompt: str, part: int):
            prompts[seat] = prompt
            sc = self.cfg.seat_configs[seat]
            seat_args = run_days.copy_args_with_seat(self.args, sc.backend_name, sc.model)

            def worker():
                seat_results.pop(seat, None)
                try:
                    seat_results[seat] = run_days.run_session(seat_args, self.out_dir, day, prompt, system_prompts[seat], part=part, seat=seat)
                except (usage_limit.UsageLimitHit, run_days.BackendError) as e:  # provider down: the loop holds the world
                    trouble[seat] = e
                except game_watchdog.GameFault as e:  # the loop's GUARD.check() recovers
                    print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} ended by a game fault: {e}")
                except SystemExit as e:  # disallowed tool; a thread would swallow it and get relaunched
                    if str(e).startswith("Disallowed tool") and seat not in slipped:
                        # one slip (Gemini reaching for its shell, denied by its guard anyway) costs a part, not the day
                        slipped.add(seat)
                        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} session ended: {e} (it gets one more)")
                        return
                    print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} stopped: {e}")
                    stopped.add(seat)

            t = threading.Thread(target=worker, name=f"play_{seat}_p{part}")
            active_threads[seat] = t
            t.start()

        # Initial launch for all seats
        for seat in self.cfg.seats:
            brief_text = briefing.morning_prompt(day, briefing.build(self.api_get, day, seat=seat, out_dir=self.out_dir))
            prompt = free_play_prompt(day, seat, self.cfg.seat_configs[seat].persona, brief_text)
            launch_seat_session(seat, prompt, cur_part[seat])

        def relaunch(seats_to_go: list[str]):
            for s in seats_to_go:
                if s in done_seats or s in stopped:
                    continue
                trouble.pop(s, None)  # the ended session's own error is part of the outage
                cur_part[s] += 1  # planned: an outage never counts toward --max-parts
                launch_seat_session(s, run_days.continue_prompt(day, self.log_dir, planned=True, player=s), cur_part[s])

        while True:
            time.sleep(1.0)
            run_days.GUARD.check()
            try:
                state = self.api_get("/api/state")
            except OSError:  # the game stopped answering: wait for it (raises GameFault for the watchdog)
                state = self.wait_ready()
            if not state.get("ready"):
                continue

            # Day rollover detected!
            if state.get("day", day) > day:
                print(f"[Multi-AI] Day rollover detected! Day {state.get('day')} started.")
                for s, t in active_threads.items():  # a CLI still running must not drive tomorrow's farmer
                    if t.is_alive():
                        game_watchdog.kill_tree(run_days.SESSIONS.get(s))
                return state

            # stop_now: each session ends after its running tool call; launch nothing new, keep the world frozen
            if run_days.CTL.stop_now_requested():
                if not any(t.is_alive() for t in active_threads.values()):
                    return {"ready": True, "day": state.get("day", day), "stop_now": True}
                continue

            # Check completed threads
            for seat in self.cfg.seats:
                if seat in done_seats:
                    continue

                thread = active_threads.get(seat)
                if thread and thread.is_alive():
                    # alive but no longer holding the world: its CLI may hang on a dead provider
                    timed_out = any(p.get("id") == seat and p.get("timedOut") for p in state.get("players", []))
                    if timed_out and time.time() - probed.get(seat, 0) > PROBE_EVERY:
                        probed[seat] = time.time()
                        err = self.probe(seat)
                        if err is not None:
                            relaunch(self.hold_for_outage(day, seat, run_days.BackendError(err), active_threads) or [])
                    continue
                if thread and not thread.is_alive():
                    # Thread finished
                    thread.join()
                    in_bed = went_to_bed(state, seat, self.log_dir, day)
                    error, res = trouble.pop(seat, None), seat_results.get(seat)
                    empty = isinstance(res, sessions.PartResult) and res.tool_calls == 0
                    if not in_bed and seat not in stopped and (error is not None or empty):
                        killed = self.hold_for_outage(day, seat, error, active_threads)
                        if killed is not None:
                            relaunch([seat] + [s for s in killed if s != seat])
                            continue
                    if in_bed:
                        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} ({self.cfg.seat_configs[seat].persona.name}) is in bed.")
                        card = memory.bedtime_persona_card(self.log_dir, seat)
                        if card.get("self"):
                            print(f"[Bedtime Card] {seat} Self Identity:\n{card['self']}")
                        if card.get("people"):
                            print(f"[Bedtime Card] {seat} People Notes:\n{card['people']}")
                        done_seats.add(seat)
                        continue

                    # Not in bed: check if we should continue part
                    res = seat_results.get(seat)
                    planned = res.planned_break if isinstance(res, sessions.PartResult) else False
                    has_more = parts[seat].after_part(planned)
                    if has_more and seat not in stopped:
                        cur_part[seat] += 1
                        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} continuing with part {cur_part[seat]}{' (planned break)' if planned else ''}")
                        cont_p = (prompts[seat] if error is not None or empty  # it never got going: same prompt again
                                  else run_days.continue_prompt(day, self.log_dir, planned=planned, player=seat))
                        launch_seat_session(seat, cont_p, cur_part[seat])
                    else:
                        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} {'was stopped' if seat in stopped else f'used all {self.args.max_parts} sessions'}: releasing freeze.")
                        try:
                            self.api_post("/api/release_freeze", {"seat": seat})
                        except Exception:
                            pass
                        done_seats.add(seat)

            # If all seats are in bed or done
            if len(done_seats) >= len(self.cfg.seats):
                # Wait for game to roll over to next day
                if all_in_bed(state, self.cfg.seats):
                    print("[Multi-AI] All active players are in bed. Waiting for morning...")
                else:
                    print("[Multi-AI] All player sessions finished. Letting clock run out until 2:00 AM pass-out...")
                s = pass_out.let_time_run_out(day, lambda: self.api_get("/api/state"), self.api_post, timeout=self.args.pass_out_timeout)
                return s


def _limit_info(text: str) -> usage_limit.LimitInfo:
    """An outage error as a LimitInfo: the reset time only when the text reads like a usage limit."""
    reset, source = usage_limit.parse_reset(text) if usage_limit.looks_like_limit(text) else (None, "none")
    return usage_limit.LimitInfo(text or "no reply", reset, source)


def run_multi_day(
    args,
    out_dir: Path,
    log_dir: Path,
    day: int,
    system_prompts: dict[str, str],
    multi_cfg: MultiConfig,
    api_get_fn,
    api_post_fn,
    wait_ready_fn,
) -> dict:
    runner = MultiDayRunner(args, out_dir, log_dir, multi_cfg, api_get_fn, api_post_fn, wait_ready_fn)
    try:
        return runner.run_free_play_phase(day, system_prompts)
    except game_watchdog.GameFault:
        # the watchdog keeps one session slot: end every seat's CLI before it relaunches the game and replays the day
        for proc in list(run_days.SESSIONS.values()):
            game_watchdog.kill_tree(proc)
        raise
