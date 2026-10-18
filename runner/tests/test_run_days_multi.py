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
                           meeting_turns=5, skip_meeting=False)
    cfg = multi_runner.parse_multi_config(args)
    assert cfg.seats == ["p1", "p2", "p3"]
    assert cfg.seat_configs["p1"].backend_name == "claude"
    assert cfg.seat_configs["p2"].backend_name == "agy"
    assert cfg.seat_configs["p3"].backend_name == "opencode"
    assert cfg.seat_configs["p1"].persona.name == "Claude"
    assert cfg.seat_configs["p2"].persona.name == "Gemini"
    assert cfg.seat_configs["p3"].persona.name == "Luna"


def test_parse_multi_config_custom_seats_and_backends():
    args = SimpleNamespace(
        multi=False,
        seats="p1,p3",
        backends="claude,opencode",
        models="claude-haiku-5-5,opencode-go/gpt-6-luna",
        p3_model="custom-luna-model",
        meeting_turns=3,
        skip_meeting=True,
    )
    cfg = multi_runner.parse_multi_config(args)
    assert cfg.seats == ["p1", "p3"]
    assert cfg.seat_configs["p1"].backend_name == "claude"
    assert cfg.seat_configs["p3"].backend_name == "opencode"
    assert cfg.seat_configs["p3"].model == "custom-luna-model"
    assert cfg.skip_meeting is True
    assert cfg.meeting_turns == 3


def test_persona_customization():
    all_personas = personas.load_all_personas(["p1", "p2", "p3"])
    template = "You are **{MODEL_NAME}**, playing Stardew Valley by yourself in a 100-day challenge.\n\nRules: ..."
    custom = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2", "p3"],
        personas=all_personas,
        model_display="Gemini 3.8 Flash",
        language="Thai",
    )
    assert "Gemini (Seat p2)" in custom
    assert "Curious, enthusiastic, adventurous" in custom
    assert "Discover the rarest items" in custom
    assert "Claude (p1), Luna (p3)" in custom
    assert "say(text)" in custom
    assert "whisper(to, text)" in custom
    assert "think(text)" in custom
    assert "give(to, item, count)" in custom
    assert "plan_read(history)" in custom
    assert "plan_edit(section, text)" in custom


def test_emergent_persona_customization():
    """Issue #48: emergent personas customize prompt with self and people notes."""
    all_personas = personas.load_all_personas(["p1", "p2"])
    template = "You are **{MODEL_NAME}**, playing Stardew Valley by yourself in a 100-day challenge.\n\nRules: ..."
    custom_day1 = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2"],
        personas=all_personas,
        model_display="Luna (OpenCode)",
        language="Thai",
        emergent=True,
        self_note="",
        people_note="",
    )
    assert "Gemini (Seat p2)" in custom_day1
    assert "Luna (OpenCode)" in custom_day1
    assert "Emerging Identity" in custom_day1
    assert "Your Relationships With Other Farmers" in custom_day1

    custom_day2 = personas.customize_system_prompt(
        template,
        seat="p2",
        active_seats=["p1", "p2"],
        personas=all_personas,
        model_display="Luna (OpenCode)",
        language="Thai",
        emergent=True,
        self_note="I am becoming a pragmatic trader after selling 15 parsnips.",
        people_note="p1 (Claude): dependable partner who waters crops early.",
    )
    assert "I am becoming a pragmatic trader" in custom_day2
    assert "p1 (Claude): dependable partner" in custom_day2


def test_multi_day_morning_meeting(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)

    meeting_calls = []

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=0, attempt=1, seat="p1"):
        meeting_calls.append((seat, part, args.max_turns))
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8,
                           meeting_turns=5, skip_meeting=False, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    sys_prompts = {"p1": "SYS_P1", "p2": "SYS_P2"}

    runner = multi_runner.MultiDayRunner(
        args, out_dir, log_dir, cfg,
        api_get_fn=lambda p: {"ready": True, "day": 1},
        api_post_fn=lambda p, b: {},
        wait_ready_fn=lambda: {"ready": True, "day": 1},
    )
    runner.run_meeting_phase(1, sys_prompts)

    assert len(meeting_calls) == 2
    seats_called = {c[0] for c in meeting_calls}
    assert seats_called == {"p1", "p2"}
    for call in meeting_calls:
        assert call[1] == 0  # meeting part is 0
        assert call[2] == 5  # meeting_turns


def test_multi_day_skip_meeting(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    log_dir = out_dir / "logs"
    log_dir.mkdir(parents=True)

    meeting_calls = []
    monkeypatch.setattr(run_days, "run_session", lambda *a, **kw: meeting_calls.append(kw))

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8,
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    runner = multi_runner.MultiDayRunner(
        args, out_dir, log_dir, cfg,
        api_get_fn=lambda p: {"ready": True, "day": 1},
        api_post_fn=lambda p, b: {},
        wait_ready_fn=lambda: {"ready": True, "day": 1},
    )
    runner.run_meeting_phase(1, {"p1": "SYS1", "p2": "SYS2"})
    assert len(meeting_calls) == 0


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

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8,
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
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

    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8,
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
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
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
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
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
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
                           meeting_turns=5, skip_meeting=True, pass_out_timeout=60)
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


def test_meeting_caps_tool_calls_and_releases_each_seat_when_done(tmp_path, monkeypatch):
    seen, posted = [], []

    def fake_run_session(args, out_d, day, prompt, sys_prompt, part=0, attempt=1, seat="p1"):
        seen.append((seat, args.cap_tool_calls))
        return sessions.PartResult(2, False)

    monkeypatch.setattr(run_days, "run_session", fake_run_session)
    args = SimpleNamespace(multi=True, seats="p1,p2", max_parts=4, max_breaks=8,
                           meeting_turns=5, skip_meeting=False, pass_out_timeout=60)
    cfg = multi_runner.parse_multi_config(args)
    runner = multi_runner.MultiDayRunner(args, tmp_path, tmp_path, cfg, api_get_fn=lambda p: {"ready": True, "day": 1},
                                         api_post_fn=lambda p, b: posted.append((p, b)) or {},
                                         wait_ready_fn=lambda: {"ready": True, "day": 1})
    monkeypatch.setattr(multi_runner.briefing, "build", lambda *a, **k: {})
    monkeypatch.setattr(multi_runner.briefing, "morning_prompt", lambda *a, **k: "")
    runner.run_meeting_phase(1, {"p1": "S1", "p2": "S2"})
    assert sorted(seen) == [("p1", True), ("p2", True)]  # claude too: its --max-turns counts turns, not calls
    assert sorted(b["seat"] for p, b in posted if p == "/api/release_freeze") == ["p1", "p2"]
