"""Multi-AI concurrent session coordinator and day phase manager (Issue #41).

Manages split-screen co-op sessions across seats (p1, p2, p3) with day phases:
  Phase 1: Morning Meeting (world frozen, chat + plan board)
  Phase 2: Free Play (concurrent sessions, independent continuation if one stops early)
  Phase 3: Evening Private Diary & Bedtime (journal before bed, all in bed advances the day)

Standard library only.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import briefing
import memory
import pass_out
import personas
import run_days
import sessions


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
    meeting_turns: int = 5
    skip_meeting: bool = False


def is_in_bed(state: dict, seat: str) -> bool:
    for p in state.get("players", []):
        if p.get("id") == seat:
            s_state = p.get("state")
            if isinstance(s_state, dict):
                return bool(s_state.get("inBed"))
    return False


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
        meeting_turns=getattr(args, "meeting_turns", 5),
        skip_meeting=getattr(args, "skip_meeting", False),
    )


def morning_meeting_prompt(day: int, seat: str, persona: personas.Persona, briefing_text: str, turns: int) -> str:
    return (
        f"{briefing_text}\n\n"
        f"[Phase 1: Morning Meeting]\n"
        f"It is 6:00 AM on day {day}. The world is frozen while you talk with your fellow farmers.\n"
        f"Hold a quick morning standup:\n"
        f"- Use `say` to speak in in-game chat (or `whisper` to talk privately).\n"
        f"- Use `plan_read` to see what needs doing and `plan_edit` to claim or coordinate tasks.\n"
        f"- Agree on who waters crops, who forages, who fishes, or who goes to town.\n"
        f"- Keep it brief (under {turns} turns). When tasks are agreed, end your turn so free play can begin."
    )


def free_play_prompt(day: int, seat: str, persona: personas.Persona) -> str:
    return (
        f"[Phase 2: Free Play]\n"
        f"Morning meeting is adjourned. Head out and begin your day {day} work!\n"
        f"Execute your tasks: farm, forage, fish, mine, or trade. Coordinate via `say` or `whisper` when needed.\n"
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

    def run_meeting_phase(self, day: int, system_prompts: dict[str, str]) -> None:
        if self.cfg.skip_meeting or len(self.cfg.seats) <= 1:
            return

        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] Day {day}: Starting Morning Meeting ({', '.join(self.cfg.seats)})")
        threads = []
        for seat in self.cfg.seats:
            sc = self.cfg.seat_configs[seat]
            brief_text = briefing.morning_prompt(day, briefing.build(self.api_get, day, seat=seat, out_dir=self.out_dir))
            prompt = morning_meeting_prompt(day, seat, sc.persona, brief_text, self.cfg.meeting_turns)
            sys_prompt = system_prompts[seat]

            def run_seat_meeting(s=seat, p=prompt, sp=sys_prompt):
                meeting_args = run_days.copy_args_with_max_turns(self.args, self.cfg.meeting_turns)
                meeting_args.cap_tool_calls = True  # claude's --max-turns counts turns, not calls: Haiku ran farm work here
                setattr(meeting_args, "backend", self.cfg.seat_configs[s].backend_name)
                setattr(meeting_args, "model", self.cfg.seat_configs[s].model)
                try:
                    run_days.run_session(meeting_args, self.out_dir, day, p, sp, part=0, seat=s)
                except SystemExit as e:  # a thread would swallow it silently
                    print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {s} meeting stopped: {e}")
                # done talking: stop holding the world still, or a seat still in its meeting that started an action
                # waits forever (the seat's next MCP call takes the release back)
                try:
                    self.api_post("/api/release_freeze", {"seat": s, "on": True})
                except Exception:
                    pass

            t = threading.Thread(target=run_seat_meeting, name=f"meeting_{seat}")
            threads.append(t)
            t.start()

        for t in threads:
            t.join()
        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] Day {day}: Morning Meeting finished.")

    def run_free_play_phase(self, day: int, system_prompts: dict[str, str]) -> dict:
        print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] Day {day}: Starting Free Play for {', '.join(self.cfg.seats)}")

        parts = {s: sessions.PartCounter(self.args.max_parts, self.args.max_breaks) for s in self.cfg.seats}
        cur_part = {s: 1 for s in self.cfg.seats}
        done_seats = set()
        active_threads: dict[str, threading.Thread] = {}
        seat_results: dict[str, sessions.PartResult] = {}
        stopped: set[str] = set()  # seats whose session raised SystemExit: not relaunched

        def launch_seat_session(seat: str, prompt: str, part: int):
            sc = self.cfg.seat_configs[seat]
            seat_args = run_days.copy_args_with_seat(self.args, sc.backend_name, sc.model)

            def worker():
                try:
                    seat_results[seat] = run_days.run_session(seat_args, self.out_dir, day, prompt, system_prompts[seat], part=part, seat=seat)
                except SystemExit as e:  # disallowed tool / backend error; a thread would swallow it and get relaunched
                    print(f"[{time.strftime('%H:%M:%S')}] [Multi-AI] {seat} stopped: {e}")
                    stopped.add(seat)

            t = threading.Thread(target=worker, name=f"play_{seat}_p{part}")
            active_threads[seat] = t
            t.start()

        # Initial launch for all seats
        for seat in self.cfg.seats:
            prompt = free_play_prompt(day, seat, self.cfg.seat_configs[seat].persona)
            launch_seat_session(seat, prompt, cur_part[seat])

        while True:
            time.sleep(1.0)
            state = self.api_get("/api/state")
            if not state.get("ready"):
                continue

            # Day rollover detected!
            if state.get("day", day) > day:
                print(f"[Multi-AI] Day rollover detected! Day {state.get('day')} started.")
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
                if thread and not thread.is_alive():
                    # Thread finished
                    thread.join()
                    in_bed = is_in_bed(state, seat)
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
                        cont_p = run_days.continue_prompt(day, self.log_dir, planned=planned, player=seat)
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
    runner.run_meeting_phase(day, system_prompts)
    if run_days.CTL.stop_now_requested():
        return {"ready": True, "day": day, "stop_now": True}
    return runner.run_free_play_phase(day, system_prompts)
