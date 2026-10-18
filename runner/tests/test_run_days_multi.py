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
        models="claude-3-5-haiku,opencode-go/gpt-6-luna",
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
