"""Tests for Multi-AI co-op coordinator and day phase manager (Issue #41)."""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

import pytest

import multi_runner
import personas
import run_days
import sessions


def test_parse_multi_config_defaults():
    args = SimpleNamespace(multi=True, seats=None, backends=None, models=None,
)
    cfg = multi_runner.parse_multi_config(args)
    assert cfg.seats == ["p1", "p2", "p3"]
    assert cfg.seat_configs["p1"].backend_name == "claude"
    assert cfg.seat_configs["p2"].backend_name == "agy"
    assert cfg.seat_configs["p3"].backend_name == "opencode"
    assert cfg.seat_configs["p1"].persona.name == "Claude"
    assert cfg.seat_configs["p2"].persona.name == "Gemini"
    assert cfg.seat_configs["p3"].persona.name == "GPT"


def test_parse_multi_config_custom_seats_and_backends():
    args = SimpleNamespace(
        multi=False,
        seats="p1,p3",
        backends="claude,opencode",
        models="claude-haiku-5-5,opencode-go/gpt-6-luna",
        p3_model="custom-luna-model",
    )
    cfg = multi_runner.parse_multi_config(args)
    assert cfg.seats == ["p1", "p3"]
    assert cfg.seat_configs["p1"].backend_name == "claude"
    assert cfg.seat_configs["p3"].backend_name == "opencode"
    assert cfg.seat_configs["p3"].model == "custom-luna-model"


def test_persona_customization():
    all_personas = personas.load_all_personas(["p1", "p2", "p3"])
    template = "You are **{MODEL_NAME}**, a newcomer who has just moved to a run-down farm in Stardew Valley.\n\nRules: ..."
    custom = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2", "p3"],
        personas=all_personas,
        language="Thai",
    )
    assert "Gemini (Seat p2)" in custom
    assert "Curious, enthusiastic, adventurous" in custom
    assert "Discover the rarest items" in custom
    assert "Claude (p1), GPT (p3)" in custom
    assert "- Claude, who goes by พี่ส้ม\n- Gemini, who goes by พี่ฟ้า (that is you)" in custom
    assert "{MODEL_NAME}" not in custom and "viewer" not in custom.lower()
    assert "say(text)" in custom and "Only people who can see you" in custom
    assert "think(text)" in custom
    assert "give(to, item, count)" in custom
    assert "whisper" not in custom and "plan_" not in custom


def test_emergent_persona_customization():
    """Issue #48: emergent personas customize prompt with self and people notes."""
    all_personas = personas.load_all_personas(["p1", "p2"])
    template = "You are **{MODEL_NAME}**, a newcomer who has just moved to a run-down farm in Stardew Valley.\n\nRules: ..."
    custom_day1 = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2"],
        personas=all_personas,
        language="Thai",
        emergent=True,
        self_note="",
        people_note="",
    )
    assert "Gemini (Seat p2)" in custom_day1
    assert "Who you are:" in custom_day1
    assert "What You Think of the Others" in custom_day1

    custom_day2 = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2"],
        personas=all_personas,
        language="Thai",
        emergent=True,
        self_note="I am becoming a pragmatic trader after selling 15 parsnips.",
        people_note="p1 (Claude): dependable partner who waters crops early.",
    )
    assert "I am becoming a pragmatic trader" in custom_day2
    assert "p1 (Claude): dependable partner" in custom_day2


@pytest.fixture(autouse=True)
def _no_briefing(monkeypatch):
    """The day prompt starts with the morning briefing; these fakes have no game to build it from."""
    monkeypatch.setattr(multi_runner.briefing, "build", lambda *a, **k: {})
    monkeypatch.setattr(multi_runner.briefing, "morning_prompt", lambda *a, **k: "BRIEFING")


def test_day_prompt_has_the_briefing_and_no_meeting(tmp_path, monkeypatch):
    """No morning meeting: each seat starts the day with its briefing; day 1 says where the strangers live."""
    prompts = {}

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        prompts[(seat, part)] = prompt
        return sessions.PartResult(4, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    state = {"ready": True, "day": 1, "players": [{"id": s, "state": {"inBed": True}} for s in ("p1", "p2")]}

    def api_get(path):
        state["day"] = 2 if prompts else 1
        return state

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    multi_runner.run_multi_day(args, tmp_path, tmp_path, 1, {"p1": "S1", "p2": "S2"}, cfg,
                               api_get_fn=api_get, api_post_fn=lambda p, b: {}, wait_ready_fn=lambda: state)
    assert set(prompts) == {("p1", 1), ("p2", 1)}  # one part each, no part 0 meeting
    for p in prompts.values():
        assert p.startswith("BRIEFING") and "cabins near yours" in p and "can see you" in p
    assert "cabins" not in multi_runner.free_play_prompt(2, "p1", cfg.seat_configs["p1"].persona, "B")


def test_multi_day_free_play_and_all_in_bed_rollover(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)

    state = {
        "ready": True,
        "day": 1,
        "players": [
            {"id": "p1", "state": {"inBed": True}},
            {"id": "p2", "state": {"inBed": True}},
        ]
    }

    def api_get(path):
        if path == "/api/state":
            # Simulate rollover to day 2
            state["day"] = 2
            return state
        return {"ready": True}

    posted = []
    def api_post(path, body):
        posted.append((path, body))
        return {}

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        return sessions.PartResult(4, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    sys_prompts = {"p1": "SYS1", "p2": "SYS2"}

    res = multi_runner.run_multi_day(
        args, out_dir, log_dir, 1, sys_prompts, cfg,
        api_get_fn=api_get, api_post_fn=api_post, wait_ready_fn=lambda: state
    )
    assert res.get("day") == 2


def test_multi_day_independent_continuation(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)

    calls = []
    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        calls.append((seat, part))
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)

    step = 0
    state = {
        "ready": True,
        "day": 1,
        "players": [
            {"id": "p1", "state": {"inBed": False}},
            {"id": "p2", "state": {"inBed": True}},
        ]
    }

    def api_get(path):
        nonlocal step
        if path == "/api/state":
            step += 1
            if step >= 3:
                state["players"][0]["state"]["inBed"] = True
                state["day"] = 2
            return state
        return {"ready": True}

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    sys_prompts = {"p1": "SYS1", "p2": "SYS2"}

    res = multi_runner.run_multi_day(
        args, out_dir, log_dir, 1, sys_prompts, cfg,
        api_get_fn=api_get, api_post_fn=lambda p, b: {}, wait_ready_fn=lambda: state
    )
    assert res.get("day") == 2
    assert ("p1", 2) in calls


def test_multi_day_freeze_release_on_exhausted_sessions(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)

    posted = []
    def api_post(path, body):
        posted.append((path, body))
        return {}

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)

    step = 0
    state = {
        "ready": True,
        "day": 1,
        "players": [
            {"id": "p1", "state": {"inBed": False}},
            {"id": "p2", "state": {"inBed": True}},
        ]
    }

    def api_get(path):
        nonlocal step
        if path == "/api/state":
            step += 1
            if step >= 4:
                state["day"] = 2
            return state
        return {"ready": True}

    # max_parts = 1 so after part 1, p1 exhausts sessions and releases freeze
    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=1, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    sys_prompts = {"p1": "SYS1", "p2": "SYS2"}

    res = multi_runner.run_multi_day(
        args, out_dir, log_dir, 1, sys_prompts, cfg,
        api_get_fn=api_get, api_post_fn=api_post, wait_ready_fn=lambda: state
    )
    assert any(path == "/api/release_freeze" and body.get("seat") == "p1" for path, body in posted)


def test_multi_day_seat_stopped_by_system_exit_is_not_relaunched(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)
    posted, calls = [], []

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        calls.append((seat, part))
        if seat == "p1":
            raise SystemExit("Disallowed tool call aborted: read")  # inside a worker thread
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    step = 0
    state = {"ready": True, "day": 1,
             "players": [{"id": "p1", "state": {"inBed": False}}, {"id": "p2", "state": {"inBed": True}}]}

    def api_get(path):
        nonlocal step
        if path == "/api/state":
            step += 1
            if step >= 4:
                state["day"] = 2
        return state

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    multi_runner.run_multi_day(args, out_dir, log_dir, 1, {"p1": "S1", "p2": "S2"}, cfg,
                               api_get_fn=api_get, api_post_fn=lambda p, b: posted.append((p, b)) or {},
                               wait_ready_fn=lambda: state)
    assert [c for c in calls if c[0] == "p1"] == [("p1", 1)]  # not burned through all 4 parts
    assert ("/api/release_freeze", {"seat": "p1"}) in posted


def test_multi_day_stop_now_launches_no_further_parts(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)
    posted, calls = [], []
    ctl = SimpleNamespace(stop=False)
    monkeypatch.setattr(run_days, "CTL", SimpleNamespace(stop_now_requested=lambda: ctl.stop))

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        calls.append((seat, part))
        ctl.stop = True  # stop_now arrives while part 1 runs
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    state = {"ready": True, "day": 1, "players": [{"id": "p2", "state": {"inBed": False}}]}
    args = SimpleNamespace(multi=True, seats="p2", max_parts=4, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    res = multi_runner.run_multi_day(args, out_dir, log_dir, 1, {"p2": "S2"}, cfg,
                                     api_get_fn=lambda p: state, api_post_fn=lambda p, b: posted.append((p, b)) or {},
                                     wait_ready_fn=lambda: state)
    assert res == {"ready": True, "day": 1, "stop_now": True}
    assert calls == [("p2", 1)]
    assert not any(p == "/api/release_freeze" for p, _ in posted)  # the world stays frozen until resume


def test_main_multi_without_backend_flags_uses_each_seats_default(tmp_path, monkeypatch):
    import sys
    seen = {}
    real = multi_runner.parse_multi_config

    def capture(args, personas_dir=None):
        seen["cfg"] = real(args, personas_dir)
        raise KeyboardInterrupt  # stop main() right after the seats are known

    monkeypatch.setattr(multi_runner, "parse_multi_config", capture)
    monkeypatch.setattr(sys, "argv", ["run_days.py", "--multi", "--seats", "p1,p2,p3", "--skip-smoke", "--out", str(tmp_path)])
    with pytest.raises(KeyboardInterrupt):
        run_days.main()
    cfg = seen["cfg"]
    assert [cfg.seat_configs[s].backend_name for s in ("p1", "p2", "p3")] == ["claude", "agy", "opencode"]
    assert [cfg.seat_configs[s].model for s in ("p1", "p2", "p3")] == \
        ["claude-haiku-5-5", "gemini-3.8-flash-medium", "opencode-go/gpt-6-luna"]


# ---------------------------------------------------------------- outages and game faults (multi seat)

import threading as _threading
from datetime import timedelta

import game_watchdog
import usage_limit


class _Sessions:
    """Fake CLIs: a seat's session can block until the runner kills it (kill_tree)."""

    def __init__(self, monkeypatch):
        self.killed: list[str] = []
        self.release: dict[str, _threading.Event] = {}
        monkeypatch.setattr(run_days, "SESSIONS", {})
        monkeypatch.setattr(game_watchdog, "kill_tree", self.kill)

    def kill(self, proc, *a, **k):
        if proc is None:
            return
        self.killed.append(proc)
        self.release.setdefault(proc, _threading.Event()).set()

    def block(self, seat: str, result):
        run_days.SESSIONS[seat] = seat  # the "process" is the seat name
        self.release.setdefault(seat, _threading.Event()).wait(10)
        self.release.pop(seat, None)
        return result


def _fake_clock(monkeypatch):
    clock = {"now": usage_limit._now()}
    monkeypatch.setattr(usage_limit, "_now", lambda: clock["now"])
    monkeypatch.setattr(usage_limit, "_sleep", lambda s: clock.__setitem__("now", clock["now"] + timedelta(seconds=s)))
    monkeypatch.setattr(multi_runner, "HOLD_AFTER_KILL", 0)
    return clock


def _runner(tmp_path, seats, api_get, posted, max_parts=4):
    args = SimpleNamespace(multi=True, seats=seats, max_parts=max_parts, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    return multi_runner.MultiDayRunner(args, tmp_path, tmp_path, cfg, api_get_fn=api_get,
                                       api_post_fn=lambda p, b: posted.append((p, b)) or {},
                                       wait_ready_fn=lambda: api_get("/api/state"))


def test_usage_limit_on_one_seat_holds_the_world_and_resumes_everyone(tmp_path, monkeypatch):
    fake = _Sessions(monkeypatch)
    _fake_clock(monkeypatch)
    calls, posted = [], []
    reset = usage_limit._now() + timedelta(hours=1)

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        calls.append((seat, part))
        if (seat, part) == ("p1", 1):
            raise usage_limit.UsageLimitHit(usage_limit.LimitInfo("limit reached", reset, "iso"), 3)
        if (seat, part) == ("p2", 1):
            return fake.block("p2", sessions.PartResult(5, False))
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    probes = []
    monkeypatch.setattr(multi_runner.MultiDayRunner, "probe", lambda self, seat: probes.append(seat))
    state = {"ready": True, "day": 1, "players": [{"id": "p1", "state": {}}, {"id": "p2", "state": {}}]}

    def api_get(path):
        if path == "/api/control":
            return {"held": True}
        if ("p1", 2) in calls and ("p2", 2) in calls:
            state["day"] = 2
        return state

    res = _runner(tmp_path, "p1,p2", api_get, posted).run_free_play_phase(1, {"p1": "S1", "p2": "S2"})
    assert res["day"] == 2
    assert fake.killed == ["p2"]                       # the healthy seat's session was ended cleanly
    controls = [b["action"] for p, b in posted if p == "/api/control"]
    assert controls == ["pause", "resume"]
    assert probes == ["p1"]                            # a known reset: no probe before the wait, one after
    assert ("p1", 2) in calls and ("p2", 2) in calls   # both relaunched with the continue prompt
    assert not any(p == "/api/release_freeze" for p, _ in posted)


def test_outage_longer_than_the_cap_stops_with_the_world_held(tmp_path, monkeypatch):
    _Sessions(monkeypatch)
    _fake_clock(monkeypatch)
    posted = []
    far = usage_limit._now() + timedelta(hours=10)

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        raise usage_limit.UsageLimitHit(usage_limit.LimitInfo("limit reached", far, "iso"), 0)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    monkeypatch.setattr(multi_runner.MultiDayRunner, "probe", lambda self, seat: "still down")
    state = {"ready": True, "day": 1, "players": [{"id": "p1", "state": {}}]}
    runner = _runner(tmp_path, "p1", lambda p: {"held": True} if p == "/api/control" else state, posted)
    with pytest.raises(SystemExit, match="outage cap"):
        runner.run_free_play_phase(1, {"p1": "S1"})
    assert [b["action"] for p, b in posted if p == "/api/control"] == ["pause"]  # never resumed


def test_backend_error_waits_until_the_probe_answers(tmp_path, monkeypatch):
    _Sessions(monkeypatch)
    clock = _fake_clock(monkeypatch)
    start = clock["now"]
    calls, posted, answers = [], [], ["down", "down", "down", None]

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        calls.append(part)
        if part == 1:
            raise run_days.BackendError("agy error, stopping: 503")
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    monkeypatch.setattr(multi_runner.MultiDayRunner, "probe", lambda self, seat: answers.pop(0))
    state = {"ready": True, "day": 1, "players": [{"id": "p2", "state": {}}]}

    def api_get(path):
        if path == "/api/control":
            return {"held": True}
        if 2 in calls:
            state["day"] = 2
        return state

    _runner(tmp_path, "p2", api_get, posted).run_free_play_phase(1, {"p2": "S2"})
    assert calls == [1, 2]
    assert clock["now"] - start >= timedelta(seconds=2 * multi_runner.OUTAGE_RETRY)  # waited, retrying every 5 min
    assert [b["action"] for p, b in posted if p == "/api/control"] == ["pause", "resume"]


def test_empty_part_with_a_healthy_provider_counts_as_a_part(tmp_path, monkeypatch):
    _Sessions(monkeypatch)
    posted = []
    monkeypatch.setattr(run_days, "run_session", lambda *a, **k: sessions.PartResult(0, False))
    monkeypatch.setattr(multi_runner.MultiDayRunner, "probe", lambda self, seat: None)
    step = 0
    state = {"ready": True, "day": 1, "players": [{"id": "p1", "state": {}}, {"id": "p2", "state": {"inBed": True}}]}

    def api_get(path):
        nonlocal step
        step += 1
        if step >= 4:
            state["day"] = 2
        return state

    _runner(tmp_path, "p1,p2", api_get, posted, max_parts=1).run_free_play_phase(1, {"p1": "S1", "p2": "S2"})
    assert not any(p == "/api/control" for p, _ in posted)      # not an outage: no pause
    assert ("/api/release_freeze", {"seat": "p1"}) in posted    # the part counted toward --max-parts


def test_timed_out_seat_with_a_healthy_provider_is_left_alone(tmp_path, monkeypatch):
    fake = _Sessions(monkeypatch)
    posted, probes = [], []
    monkeypatch.setattr(run_days, "run_session",
                        lambda *a, seat="p1", **k: fake.block(seat, sessions.PartResult(3, False)))
    monkeypatch.setattr(multi_runner.MultiDayRunner, "probe", lambda self, seat: probes.append(seat))
    state = {"ready": True, "day": 1, "players": [{"id": "p1", "timedOut": True, "state": {}}]}

    def api_get(path):
        if probes and path == "/api/state":
            state["day"] = 2
        return state

    res = _runner(tmp_path, "p1", api_get, posted).run_free_play_phase(1, {"p1": "S1"})
    assert res["day"] == 2 and probes == ["p1"]
    assert not any(p == "/api/control" for p, _ in posted)
    assert fake.killed == ["p1"]  # only at rollover: a CLI still running must not drive tomorrow's farmer


def test_game_fault_kills_every_seat_cli_and_reaches_the_watchdog(tmp_path, monkeypatch):
    fake = _Sessions(monkeypatch)

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=1, attempt=1, seat="p1"):
        return fake.block(seat, sessions.PartResult(1, False))

    class Guard:
        def check(self):
            if len(run_days.SESSIONS) == 2:
                raise game_watchdog.GameFault("process_gone", "test")

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    monkeypatch.setattr(run_days, "GUARD", Guard())
    state = {"ready": True, "day": 1, "players": [{"id": "p1", "state": {}}, {"id": "p2", "state": {}}]}
    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    with pytest.raises(game_watchdog.GameFault):
        multi_runner.run_multi_day(args, tmp_path, tmp_path, 1, {"p1": "S1", "p2": "S2"}, cfg,
                                   api_get_fn=lambda p: state, api_post_fn=lambda p, b: {},
                                   wait_ready_fn=lambda: state)
    assert sorted(fake.killed) == ["p1", "p2"]


def test_game_not_answering_waits_for_it_instead_of_crashing(tmp_path, monkeypatch):
    _Sessions(monkeypatch)
    monkeypatch.setattr(run_days, "run_session", lambda *a, **k: sessions.PartResult(2, False))
    calls = {"n": 0}

    def api_get(path):
        calls["n"] += 1
        if calls["n"] == 1:
            raise ConnectionRefusedError("game restarting")
        return {"ready": True, "day": 1, "players": [{"id": "p1", "state": {"inBed": True}}]}

    args = SimpleNamespace(multi=True, seats="p1", max_parts=4, max_breaks=0,
                           pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    runner = multi_runner.MultiDayRunner(args, tmp_path, tmp_path, cfg, api_get_fn=api_get, api_post_fn=lambda p, b: {},
                                         wait_ready_fn=lambda: {"ready": True, "day": 2})
    assert runner.run_free_play_phase(1, {"p1": "S1"})["day"] == 2
