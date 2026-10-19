"""pytest setup for the runner tests: no game, no network, no real `claude`."""

import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

RUNNER = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(RUNNER))          # `import usage_limit`, `import run_days`


class FakeGame:
    """Stands in for the mod's HTTP API: /api/state, POST /api/meta, and POST /test/next_day (the AI slept)."""

    def __init__(self):
        self.day = 1
        self.metas: list[dict] = []
        self.lock = threading.Lock()
        game = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _send(self, obj):
                data = json.dumps(obj).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                if self.path == "/api/state":
                    with game.lock:
                        self._send({"ready": True, "day": game.day, "saveFolder": "Farm_1"})
                else:
                    self.send_error(404)

            def do_POST(self):
                body = self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}"
                with game.lock:
                    if self.path == "/api/meta":
                        game.metas.append(json.loads(body))
                    elif self.path == "/test/next_day":
                        game.day += 1
                    else:
                        self.send_error(404)
                        return
                self._send({"ok": True})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.url = f"http://127.0.0.1:{self.port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def statuses(self) -> list[str]:
        return [m["runnerStatus"] for m in self.metas if "runnerStatus" in m]

    def events(self) -> list[str]:
        return [m["event"] for m in self.metas if "event" in m]

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def game():
    g = FakeGame()
    yield g
    g.close()


@pytest.fixture(autouse=True)
def _mock_watchdog_for_runner_tests(monkeypatch, request):
    if "test_game_watchdog" not in request.node.nodeid:
        import game_watchdog
        old_from_args = game_watchdog.from_args

        def fake_from_args(args, log_dir, api_get, api=game_watchdog.DEFAULT_API):
            if not getattr(args, "game_cmd", "") and getattr(args, "game_process", "") == game_watchdog.default_process_name():
                return game_watchdog.NullWatchdog()
            return old_from_args(args, log_dir, api_get, api)

        monkeypatch.setattr(game_watchdog, "from_args", fake_from_args)

    if "test_pass_out" not in request.node.nodeid:
        import pass_out
        old_let_time = pass_out.let_time_run_out

        def fake_let_time(day, get_state, post, sleep=time.sleep, *, timeout=1800.0, **kwargs):
            return old_let_time(day, get_state, post, sleep, timeout=min(timeout, 0.0), **kwargs)

        monkeypatch.setattr(pass_out, "let_time_run_out", fake_let_time)


@pytest.fixture(autouse=True)
def _fresh_run_control(monkeypatch):
    """run_days.main() ends by holding the world (stop_now), which sticks in its RunControl: one per test."""
    import run_days
    from run_control import RunControl
    monkeypatch.setattr(run_days, "CTL", RunControl(run_days.api_get, run_days.api_post))


@pytest.fixture(autouse=True)
def _no_real_briefing_fetch(monkeypatch):
    """briefing.build with run_days' real api_get would hit localhost:8770 and retry 15x with 1 s sleeps."""
    import briefing
    import run_days
    old_build = briefing.build

    def build(api_get, *args, **kwargs):
        return "" if api_get is run_days.api_get else old_build(api_get, *args, **kwargs)

    monkeypatch.setattr(briefing, "build", build)


@pytest.fixture(autouse=True)
def _real_game_port_refuses_at_once(monkeypatch):
    """A closed :8770 takes ~2 s to refuse on Windows; the tests that reach it by accident only need the error."""
    import urllib.request
    old_urlopen = urllib.request.urlopen

    def urlopen(req, *args, **kwargs):
        url = req if isinstance(req, str) else req.full_url
        if ":8770/" in url or url.endswith(":8770"):
            raise ConnectionRefusedError("no game in tests")
        return old_urlopen(req, *args, **kwargs)

    monkeypatch.setattr(urllib.request, "urlopen", urlopen)


@pytest.fixture(autouse=True)
def _no_stats_wait(monkeypatch, request):
    """wait_for_stats polls up to 6 s for a file the fake game never writes."""
    if "test_stats" not in request.node.nodeid:
        import stats
        old_wait = stats.wait_for_stats

        def wait(log_dir, day, player="p1", timeout=6.0, poll=0.5, sleep=None):
            return old_wait(log_dir, day, player, 0.0, poll, sleep)

        monkeypatch.setattr(stats, "wait_for_stats", wait)

