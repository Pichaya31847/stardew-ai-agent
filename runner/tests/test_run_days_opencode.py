"""Tests for the opencode (OpenCode Go Luna) backend in run_days.py (Issue #41)."""

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


class OpenCodeSession:
    def __init__(self, out_dir: Path):
        self.out_dir = out_dir
        self.cmd: list[str] = []
        self.popen_kwargs: dict = {}
        self.proc: FakeProc | None = None
        self.posts: list[tuple[str, dict]] = []
        self.stream = ""
        self.returned = None

    def transcript(self, day: int = 2, part: int = 1, seat: str = "p1") -> Path:
        seat_suffix = f"_{seat}" if seat != "p1" else ""
        return self.out_dir / "logs" / f"opencode_day_{day:03}{seat_suffix}_part{part}.jsonl"


@pytest.fixture
def play_opencode(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    session = OpenCodeSession(out_dir)

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

    def run(sample: str = "opencode_stream_day.jsonl", *, day: int = 2, part: int = 1, seat: str = "p1",
            model: str = "opencode-go/gpt-6-luna", max_turns: int = 600,
            prompt: str = "play the day", system_prompt: str = "You are Luna.") -> OpenCodeSession:
        if (SAMPLES / sample).exists():
            session.stream = (SAMPLES / sample).read_text(encoding="utf-8")
        else:
            session.stream = sample
        args = SimpleNamespace(backend="opencode", model=model, max_turns=max_turns)
        session.returned = run_days.run_session(args, out_dir, day, prompt, system_prompt, part, seat=seat)
        return session

    return run


def test_opencode_launch_command(play_opencode):
    s = play_opencode("opencode_stream_day.jsonl", prompt="PROMPT", system_prompt="RULES", model="opencode-go/gpt-6-luna")
    cmd = s.cmd
    assert cmd[1] == "run"
    assert cmd[cmd.index("--model") + 1] == "opencode-go/gpt-6-luna"
    assert cmd[cmd.index("--format") + 1] == "json"
    assert "--auto" in cmd
    assert cmd[-1] == "RULES\n\n---\n\nPROMPT"
    assert s.transcript().exists()

    # Check workdir opencode.json tool lockdown
    cfg_file = s.out_dir / "workdir" / "opencode.json"
    assert cfg_file.exists()
    cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
    assert cfg["tools"]["bash"] is False
    assert cfg["tools"]["edit"] is False
    assert cfg["permission"]["stardew_*"] == "allow"
    assert cfg["permission"]["*"] == "deny"


def test_opencode_stream_parsing_and_tool_counts(play_opencode, capsys):
    s = play_opencode("opencode_stream_day.jsonl")
    assert s.returned == 5
    out = capsys.readouterr().out
    for number, name in enumerate(["status", "say", "look", "journal", "go_to_bed"], start=1):
        assert f"  #{number} {name}" in out
    assert "  #2 say อรุณสวัสดิ์ทุกคน วันนี้ฉันจะไปเคลียร์ฟาร์มแถวหีบสมบัตินะ" in out
    assert "  result: success turns=1 cost=$0.003" in out


def test_opencode_meta_reporting(play_opencode):
    s = play_opencode("opencode_stream_day.jsonl", day=2, part=1, seat="p3")
    assert len(s.posts) == 1
    path, body = s.posts[0]
    assert path == "/api/meta"
    assert body["model"] == "Luna (OpenCode)"
    assert body["modelId"] == "opencode-go/gpt-6-luna"
    assert body["session"] == "ses_opencode_sample_day"
    assert body["seat"] == "p3"
    assert body["run"] == "day 2 · part 1"


def test_opencode_disallowed_tool_aborts(play_opencode):
    raw_ev = json.dumps({
        "type": "tool_use",
        "sessionID": "ses_test",
        "part": {
            "type": "tool",
            "tool": "bash",
            "state": {"status": "completed", "input": {"command": "dir"}, "output": ""}
        }
    })
    with pytest.raises(SystemExit) as exc:
        play_opencode(raw_ev)
    assert "Disallowed tool call aborted: bash" in str(exc.value)


def test_opencode_max_turns_stops_session(play_opencode):
    s = play_opencode("opencode_stream_day.jsonl", max_turns=3)
    assert s.returned == 3
    assert s.proc.killed is True


def test_opencode_seat_routing(play_opencode):
    s = play_opencode("opencode_stream_day.jsonl", day=1, part=1, seat="p3")
    assert s.transcript(day=1, part=1, seat="p3").exists()
    cfg_file = s.out_dir / "workdir_p3" / "opencode.json"
    assert cfg_file.exists()
    cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
    assert "/mcp/p3" in cfg["mcp"]["servers"]["stardew"]["url"]
    assert (s.out_dir / "usage_p3.csv").exists()


def test_opencode_text_session(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    called_cmd = []

    text_stream = (
        '{"type":"step_start","sessionID":"ses_txt"}\n'
        '{"type":"text","part":{"type":"text","text":"Consolidated lessons summary"}}\n'
        '{"type":"step_finish","part":{"reason":"stop"}}\n'
    )

    def popen(cmd, **kwargs):
        called_cmd.extend(cmd)
        return FakeProc(text_stream)

    monkeypatch.setattr(run_days.subprocess, "Popen", popen)
    backend = run_days.get_backend("opencode")
    session = backend.text_session(SimpleNamespace(model="opencode-go/gpt-6-luna"), out_dir, "test_label")
    reply = session("System Prompt", "User Prompt")
    assert reply == "Consolidated lessons summary"
    assert "run" in called_cmd
