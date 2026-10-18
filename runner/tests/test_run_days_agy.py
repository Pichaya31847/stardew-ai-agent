"""Tests for the agy (Antigravity Gemini 3.8 Flash) backend in run_days.py (Issue #12)."""

from __future__ import annotations

import io
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

import pytest

import run_days

SAMPLES = Path(__file__).resolve().parents[1] / "samples"


class FakeProc:
    def __init__(self, text: str):
        self.stdout = io.StringIO(text)
        self.waited = False
        self.killed = False

    def wait(self):
        self.waited = True
        return 0

    def kill(self):
        self.killed = True


class AgySession:
    def __init__(self, out_dir: Path):
        self.out_dir = out_dir
        self.cmd: list[str] = []
        self.popen_kwargs: dict = {}
        self.proc: FakeProc | None = None
        self.posts: list[tuple[str, dict]] = []
        self.stream = ""
        self.returned = None

    def transcript(self, day: int = 2, part: int = 1) -> Path:
        return self.out_dir / "logs" / f"agy_day_{day:03}_part{part}.jsonl"


@pytest.fixture
def play_agy(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    session = AgySession(out_dir)

    def popen(cmd, **kwargs):
        session.cmd = list(cmd)
        session.popen_kwargs = kwargs
        session.proc = FakeProc(session.stream)
        return session.proc

    def api_post(path, body):
        session.posts.append((path, body))
        return {}

    monkeypatch.setattr(run_days.subprocess, "Popen", popen)
    monkeypatch.setattr(run_days, "api_post", api_post)

    def run(sample: str = "agy_stream_day.jsonl", *, day: int = 2, part: int = 1,
            model: str = "gemini-3.8-flash-medium", max_turns: int = 600,
            prompt: str = "play the day", system_prompt: str = "You are Gemini.") -> AgySession:
        session.stream = (SAMPLES / sample).read_text(encoding="utf-8")
        args = SimpleNamespace(backend="agy", model=model, max_turns=max_turns)
        session.returned = run_days.run_session(args, out_dir, day, prompt, system_prompt, part)
        return session

    return run


def test_agy_launch_command(play_agy):
    s = play_agy("agy_stream_day.jsonl", prompt="PROMPT", system_prompt="RULES", model="gemini-3.8-flash-medium")
    cmd = s.cmd
    assert cmd[0] == "agy"
    assert cmd[cmd.index("-p") + 1] == "RULES\n\n---\n\nPROMPT"
    assert cmd[cmd.index("--model") + 1] == "gemini-3.8-flash-medium"
    assert cmd[cmd.index("--output-format") + 1] == "stream-json"
    assert "--dangerously-skip-permissions" in cmd
    assert s.transcript().exists()


def test_agy_stream_parsing_and_tool_counts(play_agy, capsys):
    s = play_agy("agy_stream_day.jsonl")
    assert s.returned == 6
    out = capsys.readouterr().out
    for number, name in enumerate(["think", "status", "look", "walk_to", "use_tool", "go_to_bed"], start=1):
        assert f"  #{number} {name}" in out
    assert "  #1 think วันนี้จะถางหญ้าแล้วปลูกพาร์สนิป" in out
    assert "  #4 walk_to ไปที่แปลงผัก" in out
    assert "  result: success turns=7" in out


def test_agy_meta_reporting(play_agy):
    s = play_agy("agy_stream_day.jsonl", day=2, part=1)
    assert len(s.posts) == 1
    path, body = s.posts[0]
    assert path == "/api/meta"
    assert body["model"] == "Gemini 3.8 Flash"
    assert body["modelId"] == "gemini-3.8-flash-medium"
    assert body["session"] == "agy-sample-session-0001"
    assert "Gemini 3.8 Flash session agy-sample-session-0001" in body["event"]


def test_agy_disallowed_tool_aborts(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    fake_stream = "\n".join([
        json.dumps({"event": "init", "conversation_id": "c1", "init": {"model": "gemini-3.8-flash-medium"}}),
        json.dumps({"event": "step_update", "step_update": {"step_type": "tool", "state": "ACTIVE", "tool_name": "run_command", "tool_info": {"parameters": {"CommandLine": "dir"}}}}),
    ]) + "\n"

    proc = FakeProc(fake_stream)
    monkeypatch.setattr(run_days.subprocess, "Popen", lambda *a, **kw: proc)
    args = SimpleNamespace(backend="agy", model="gemini-3.8-flash-medium", max_turns=600)
    with pytest.raises(SystemExit) as exc:
        run_days.run_session(args, out_dir, 1, "play", "system", 1)
    assert "Disallowed tool call aborted: run_command" in str(exc.value)
    assert proc.killed


def test_agy_turn_limit_enforced(tmp_path, monkeypatch, capsys):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    stream_lines = [json.dumps({"event": "init", "conversation_id": "c1", "init": {"model": "m1"}})]
    for i in range(1, 5):
        stream_lines.append(json.dumps({"event": "step_update", "step_update": {"step_type": "tool", "state": "ACTIVE", "tool_name": f"mcp_stardew_think", "tool_info": {"parameters": {"text": f"t{i}"}}}}))
    fake_stream = "\n".join(stream_lines) + "\n"
    proc = FakeProc(fake_stream)
    monkeypatch.setattr(run_days.subprocess, "Popen", lambda *a, **kw: proc)
    args = SimpleNamespace(backend="agy", model="gemini-3.8-flash-medium", max_turns=2)
    res = run_days.run_session(args, out_dir, 1, "play", "system", 1)
    assert res == 2
    assert proc.killed
    assert "result: error_max_turns turns=2" in capsys.readouterr().out


def test_agy_backend_defaults():
    backend = run_days.get_backend("agy")
    assert backend.name == "agy"
    assert backend.default_model == "gemini-3.8-flash-medium"
    assert backend.display_name == "Gemini 3.8 Flash"

    claude = run_days.get_backend("claude")
    assert claude.name == "claude"
    assert claude.default_model == "claude-haiku-5-5"
    assert claude.display_name == "Claude Haiku 5.5"


def test_agy_text_session(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    fake_stream = "\n".join([
        json.dumps({"event": "init", "conversation_id": "c1"}),
        json.dumps({"event": "result", "result": {"status": "SUCCESS", "response": "Consolidated summary text"}}),
    ]) + "\n"
    proc = FakeProc(fake_stream)
    monkeypatch.setattr(run_days.subprocess, "Popen", lambda *a, **kw: proc)
    args = SimpleNamespace(backend="agy", model="gemini-3.8-flash-medium")
    session = run_days.agy_text_session(args, out_dir, "test_label")
    reply = session("System prompt", "User prompt")
    assert reply == "Consolidated summary text"
