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


def _agy_call(server: str, tool: str) -> dict:
    # shape seen from agy 2026-10-19: PascalCase keys
    return {"event": "step_update", "step_update": {"step_type": "tool", "state": "ACTIVE", "tool_name": "call_mcp_tool",
            "tool_info": {"parameters": {"Arguments": {}, "ServerName": server, "ToolName": tool}}}}


def test_agy_call_mcp_tool_to_own_seat_server_counts_as_game_tool():
    b = run_days.AgyBackend()
    b.server = "stardew_p2"
    assert [(e.tool_name, e.is_disallowed) for e in b.parse_event(_agy_call("stardew_p2", "look"))] == [("look", False)]


def test_agy_call_mcp_tool_to_another_seat_or_server_is_disallowed():
    b = run_days.AgyBackend()
    b.server = "stardew_p2"
    assert b.parse_event(_agy_call("stardew_p1", "travel_to"))[0].is_disallowed  # would drive Claude's farmer
    assert b.parse_event(_agy_call("Roblox_Studio", "run_code"))[0].is_disallowed


def _agy_view(path: str) -> dict:
    return {"event": "step_update", "step_update": {"step_type": "tool", "state": "ACTIVE", "tool_name": "view_file",
            "tool_info": {"parameters": {"AbsolutePath": path}}}}


def test_agy_view_file_of_its_own_stardew_instructions_is_ignored():
    b = run_days.AgyBackend()
    b.server = "stardew_p2"
    own = Path.home() / ".gemini" / "antigravity-cli" / "mcp" / "stardew_p2" / "instructions.md"
    assert b.parse_event(_agy_view(str(own))) == []


def test_agy_view_file_elsewhere_is_disallowed():
    b = run_days.AgyBackend()
    b.server = "stardew_p2"
    mcp = Path.home() / ".gemini" / "antigravity-cli" / "mcp"
    for path in (mcp / "stardew_p1" / "instructions.md", mcp / "stardew_p2" / ".." / "blender" / "x.md", Path("CLAUDE.md").resolve()):
        assert b.parse_event(_agy_view(str(path)))[0].is_disallowed, path


def test_agy_launch_writes_the_tool_guard_before_starting(play_agy):
    seen = {}
    orig = run_days.subprocess.Popen

    def popen(cmd, **kwargs):
        hooks = Path(kwargs["cwd"]) / ".agents" / "hooks.json"
        seen["hooks"] = json.loads(hooks.read_text(encoding="utf-8")) if hooks.exists() else None
        seen["cmd"] = (hooks.parent / "stardew_guard.cmd").read_text(encoding="utf-8") if hooks.exists() else ""
        return orig(cmd, **kwargs)

    run_days.subprocess.Popen = popen
    try:
        play_agy("agy_stream_day.jsonl")
    finally:
        run_days.subprocess.Popen = orig
    hook = seen["hooks"]["stardew-guard"]["PreToolUse"][0]
    assert hook["matcher"] == "*" and hook["hooks"][0]["command"] == r".\stardew_guard.cmd"
    assert "agy_tool_guard.py" in seen["cmd"] and seen["cmd"].rstrip().endswith(" stardew")


def test_agy_text_session_guard_allows_no_tool(tmp_path, monkeypatch):
    out_dir = tmp_path / "out"
    (out_dir / "logs").mkdir(parents=True)
    stream = json.dumps({"event": "result", "result": {"status": "SUCCESS", "response": "OK"}}) + "\n"
    cwds = []
    monkeypatch.setattr(run_days.subprocess, "Popen", lambda *a, **kw: cwds.append(kw["cwd"]) or FakeProc(stream))
    run_days.agy_text_session(SimpleNamespace(model="m"), out_dir, "probe")("s", "p")
    guard = (Path(cwds[0]) / ".agents" / "stardew_guard.cmd").read_text(encoding="utf-8")
    assert Path(cwds[0]).name == "workdir_agy_text" and guard.rstrip().endswith('agy_tool_guard.py"')


def _guard(call: dict, server: str = "stardew_p2") -> dict:
    # the real stdin/stdout contract agy uses (verified with agy 2026-10-19)
    script = Path(run_days.agy_tool_guard.__file__)
    res = subprocess.run([run_days.sys.executable, str(script), server], input=json.dumps({"toolCall": call}),
                         capture_output=True, text=True, check=True)
    return json.loads(res.stdout)


def test_agy_guard_allows_only_the_seats_own_server():
    own = Path.home() / ".gemini" / "antigravity-cli" / "mcp" / "stardew_p2" / "look.json"
    assert _guard({"name": "call_mcp_tool", "args": {"ServerName": "stardew_p2", "ToolName": "look"}}) == {"decision": "allow"}
    assert _guard({"name": "view_file", "args": {"AbsolutePath": str(own)}}) == {"decision": "allow"}
    for call in ({"name": "run_command", "args": {"CommandLine": "dir"}},
                 {"name": "write_to_file", "args": {"TargetFile": "x.txt"}},
                 {"name": "call_mcp_tool", "args": {"ServerName": "stardew_p1", "ToolName": "travel_to"}},
                 {"name": "view_file", "args": {"AbsolutePath": str(own.parent.parent / "stardew_p1" / "look.json")}}):
        out = _guard(call)
        assert out["decision"] == "deny" and "Stardew" in out["reason"], call


def test_agy_may_read_back_its_own_saved_tool_output():
    brain = Path.home() / ".gemini" / "antigravity-cli" / "brain"
    out = brain / "c1702e34" / ".system_generated" / "steps" / "36" / "output.txt"  # seen in the 2026-10-19 rehearsal
    assert _guard({"name": "view_file", "args": {"AbsolutePath": str(out)}}) == {"decision": "allow"}
    b = run_days.AgyBackend()
    b.server = "stardew_p2"
    assert b.parse_event(_agy_view(str(out))) == []
    for path in (brain / "c1702e34" / "notes.md", brain / "c1702e34" / ".system_generated" / ".." / ".." / ".." / "settings.json"):
        assert _guard({"name": "view_file", "args": {"AbsolutePath": str(path)}})["decision"] == "deny", path


def test_agy_guard_with_no_server_or_bad_input_denies():
    assert _guard({"name": "call_mcp_tool", "args": {"ServerName": "stardew"}}, server="")["decision"] == "deny"
    script = Path(run_days.agy_tool_guard.__file__)
    res = subprocess.run([run_days.sys.executable, str(script), "stardew"], input="not json", capture_output=True, text=True)
    assert json.loads(res.stdout)["decision"] == "deny"
