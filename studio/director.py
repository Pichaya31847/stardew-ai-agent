"""OBS scene director driven by the mod's /api/events Server-Sent Events (SSE) stream.

Connects to http://localhost:8770/api/events and switches OBS scenes via obs-websocket v5:
  - "all-three": 2x2 grid during plain farm work / idle
  - "focus-P1", "focus-P2", "focus-P3": cropped focus on the active farmer

Focus rules:
  - Focus the speaker on `say` and `whisper`
  - Focus the subject on `theft`, `gift` (or `give`), and `caught-slacking`
  - Return to `all-three` (grid) otherwise
  - Hold each focus scene for at least 3 seconds (>= 3s)

Standard library only (Python 3).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import select
import socket
import struct
import time
import urllib.parse
from datetime import datetime
from typing import Any, Iterable, Iterator

EVENTS_URL = "http://localhost:8770/api/events"
GRID_SCENE = "all-three"
FOCUS_SCENES: dict[str, str] = {
    "p1": "focus-P1",
    "p2": "focus-P2",
    "p3": "focus-P3",
}
MIN_HOLD_SECONDS = 3.0

FOCUS_EVENT_TYPES = frozenset({
    "say",
    "whisper",
    "theft",
    "gift",
    "caught-slacking",
})

EVENT_ALIASES: dict[str, str] = {
    "give": "gift",
    "gave": "gift",
    "steal": "theft",
    "stole": "theft",
    "chest-theft": "theft",
    "plot-theft": "theft",
    "harvest-theft": "theft",
    "caught_slacking": "caught-slacking",
    "slacking": "caught-slacking",
}

PLAYER_KEYS = (
    "speaker",
    "player",
    "from",
    "actor",
    "thief",
    "slacker",
    "who",
    "seat",
)


def log(msg: str) -> None:
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


# ---------------------------------------------------------------- scene picking


def normalize_event_type(event: dict[str, Any] | None) -> str:
    """Return canonical event type name (e.g. 'say', 'whisper', 'theft', 'gift', 'caught-slacking')."""
    if not isinstance(event, dict):
        return ""
    raw = (
        event.get("type")
        or event.get("event")
        or event.get("kind")
        or ""
    )
    raw = str(raw).strip().lower().replace("_", "-")
    if raw in ("action", "interrupt", "") and event.get("tool"):
        raw = str(event["tool"]).strip().lower().replace("_", "-")
    return EVENT_ALIASES.get(raw, raw)


def normalize_player(value: Any) -> str | None:
    """Normalize a player identifier ('p1', 'P2', 3, 'player1') into 'p1'..'p3', or None."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, int):
        key = f"p{value}"
    else:
        s = str(value).strip().lower()
        if s.startswith("player") and s[6:].isdigit():
            key = f"p{s[6:]}"
        elif s.isdigit():
            key = f"p{s}"
        else:
            key = s
    return key if key in FOCUS_SCENES else None


def extract_player(event: dict[str, Any] | None) -> str | None:
    """Extract the speaking or acting player ('p1'..'p3') from an event dict."""
    if not isinstance(event, dict):
        return None
    for k in PLAYER_KEYS:
        p = normalize_player(event.get(k))
        if p:
            return p
    args = event.get("args")
    if isinstance(args, dict):
        for k in PLAYER_KEYS:
            p = normalize_player(args.get(k))
            if p:
                return p
    return None


def scene_for_event(event: dict[str, Any] | None) -> str:
    """Map a single event to its immediate target scene without hold-time state."""
    if not isinstance(event, dict) or event.get("ok") is False:
        return GRID_SCENE
    etype = normalize_event_type(event)
    if etype in FOCUS_EVENT_TYPES:
        player = extract_player(event)
        if player and player in FOCUS_SCENES:
            return FOCUS_SCENES[player]
    return GRID_SCENE


class SceneDirector:
    """Stateful scene picker that holds each focus scene for at least `hold_seconds` (>= 3s)."""

    def __init__(self, hold_seconds: float = MIN_HOLD_SECONDS) -> None:
        self.hold_seconds = max(MIN_HOLD_SECONDS, float(hold_seconds))
        self.current_scene: str = GRID_SCENE
        self.focus_until: float = 0.0

    def pick_scene(self, event: dict[str, Any] | None, now: float | None = None) -> str:
        ts = time.monotonic() if now is None else float(now)
        desired = scene_for_event(event)

        if self.current_scene != GRID_SCENE and ts < self.focus_until:
            if desired == self.current_scene:
                self.focus_until = max(self.focus_until, ts + self.hold_seconds)
            return self.current_scene

        self.current_scene = desired
        if desired != GRID_SCENE:
            self.focus_until = ts + self.hold_seconds
        else:
            self.focus_until = 0.0
        return self.current_scene


def pick_scene(
    event: dict[str, Any] | None,
    *,
    now: float | None = None,
    director: SceneDirector | None = None,
    hold_seconds: float = MIN_HOLD_SECONDS,
) -> str:
    """Pick an OBS scene ('all-three' or 'focus-P1'/'focus-P2'/'focus-P3') for an event.

    When `director` is provided, enforces the >= 3s focus hold across calls.
    When called statelessly with just `event`, returns the scene for that event.
    """
    if director is not None:
        return director.pick_scene(event, now=now)
    return scene_for_event(event)


# ---------------------------------------------------------------- SSE client


def parse_sse_lines(lines: Iterable[str | bytes]) -> Iterator[dict[str, Any]]:
    """Parse an iterable of SSE lines into event dicts."""
    event_name = ""
    data_lines: list[str] = []

    def flush() -> dict[str, Any] | None:
        nonlocal event_name, data_lines
        if not data_lines:
            event_name = ""
            return None
        raw = "\n".join(data_lines).strip()
        ev_type = event_name
        event_name = ""
        data_lines = []
        if not raw:
            return None
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError:
            return None
        if isinstance(parsed, dict):
            if ev_type and not (parsed.get("type") or parsed.get("event") or parsed.get("kind")):
                parsed["type"] = ev_type
            return parsed
        return None

    for raw_line in lines:
        line = raw_line.decode("utf-8", errors="replace") if isinstance(raw_line, bytes) else raw_line
        line = line.rstrip("\r\n")
        if not line:
            ev = flush()
            if ev is not None:
                yield ev
            continue
        if line.startswith(":"):
            continue
        if line.startswith("event:"):
            event_name = line[6:].strip()
        elif line.startswith("data:"):
            data_lines.append(line[5:].lstrip())
        elif line.startswith("{") and not data_lines:
            # Fallback if server streams raw JSONL lines on /api/events
            data_lines.append(line)
            ev = flush()
            if ev is not None:
                yield ev

    ev = flush()
    if ev is not None:
        yield ev


def iter_sse(url: str = EVENTS_URL, poll_interval: float = 0.25) -> Iterator[dict[str, Any] | None]:
    """Connect to an SSE endpoint and yield event dicts, or None on poll_interval ticks."""
    parsed = urllib.parse.urlsplit(url)
    host = parsed.hostname or "localhost"
    port = parsed.port or (443 if parsed.scheme == "https" else 80)
    path = parsed.path or "/api/events"
    if parsed.query:
        path = f"{path}?{parsed.query}"

    conn = http.client.HTTPConnection(host, port, timeout=10)
    try:
        conn.request("GET", path, headers={
            "Accept": "text/event-stream",
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
        })
        resp = conn.getresponse()
        if resp.status != 200:
            raise ConnectionError(f"GET {url} returned HTTP {resp.status} {resp.reason}")
        sock = conn.sock
        event_name = ""
        data_lines: list[str] = []

        while True:
            buffered = False
            if hasattr(resp.fp, "peek"):
                try:
                    buffered = bool(resp.fp.peek(1))
                except OSError:
                    buffered = False
            if not buffered and sock is not None:
                ready, _, _ = select.select([sock], [], [], poll_interval)
                if not ready:
                    yield None
                    continue

            raw_line = resp.readline()
            if not raw_line:
                raise ConnectionError("SSE stream closed by server")
            line = raw_line.decode("utf-8", errors="replace").rstrip("\r\n")
            if not line:
                if data_lines:
                    for ev in parse_sse_lines([f"event: {event_name}" if event_name else "", *[f"data: {d}" for d in data_lines], ""]):
                        yield ev
                    event_name = ""
                    data_lines = []
                else:
                    yield None
                continue
            if line.startswith(":"):
                yield None
                continue
            if line.startswith("event:"):
                event_name = line[6:].strip()
            elif line.startswith("data:"):
                data_lines.append(line[5:].lstrip())
            elif line.startswith("{") and not data_lines:
                for ev in parse_sse_lines([line]):
                    yield ev
    finally:
        conn.close()


# ---------------------------------------------------------------- minimal obs-websocket v5 client


class Obs:
    def __init__(self, host: str, port: int, password: str = "") -> None:
        self.sock = socket.create_connection((host, port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((
            f"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
            "Sec-WebSocket-Protocol: obswebsocket.json\r\n\r\n"
        ).encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.sock.recv(1024)
            if not chunk:
                raise ConnectionError("OBS closed the connection during the handshake")
            head += chunk
        if b" 101 " not in head.split(b"\r\n", 1)[0]:
            raise ConnectionError(f"OBS refused the WebSocket upgrade: {head.splitlines()[0]!r}")
        self._buf = head.split(b"\r\n\r\n", 1)[1]
        self._next_id = 0

        hello = self._recv_json()
        identify: dict[str, Any] = {"rpcVersion": 1, "eventSubscriptions": 0}
        auth = hello.get("d", {}).get("authentication")
        if auth:
            if not password:
                raise ConnectionError("OBS asks for a password: pass --password or set OBS_WS_PASSWORD")
            secret = base64.b64encode(hashlib.sha256((password + auth["salt"]).encode()).digest()).decode()
            identify["authentication"] = base64.b64encode(
                hashlib.sha256((secret + auth["challenge"]).encode()).digest()
            ).decode()
        self._send_json({"op": 1, "d": identify})
        msg = self._recv_json()
        if msg.get("op") != 2:
            raise ConnectionError(f"OBS identify failed: {msg}")

    def request(self, request_type: str, data: dict[str, Any] | None = None) -> dict[str, Any]:
        self._next_id += 1
        rid = str(self._next_id)
        self._send_json({"op": 6, "d": {"requestType": request_type, "requestId": rid, "requestData": data or {}}})
        while True:
            msg = self._recv_json()
            if msg.get("op") == 7 and msg["d"].get("requestId") == rid:
                status = msg["d"]["requestStatus"]
                if not status.get("result"):
                    raise RuntimeError(f"{request_type} failed: {status.get('code')} {status.get('comment', '')}")
                return msg["d"].get("responseData") or {}

    def set_scene(self, scene_name: str) -> None:
        self.request("SetCurrentProgramScene", {"sceneName": scene_name})

    def close(self) -> None:
        try:
            self._send_frame(0x8, struct.pack(">H", 1000))
            self.sock.close()
        except OSError:
            pass

    def _send_json(self, obj: dict[str, Any]) -> None:
        self._send_frame(0x1, json.dumps(obj).encode("utf-8"))

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        n = len(payload)
        header = bytes([0x80 | opcode])
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + struct.pack(">H", n)
        else:
            header += bytes([0x80 | 127]) + struct.pack(">Q", n)
        mask = os.urandom(4)
        self.sock.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def _read(self, n: int) -> bytes:
        while len(self._buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("OBS closed the connection")
            self._buf += chunk
        out, self._buf = self._buf[:n], self._buf[n:]
        return out

    def _recv_json(self) -> dict[str, Any]:
        message = b""
        while True:
            b0, b1 = self._read(2)
            opcode, n = b0 & 0x0F, b1 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._read(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._read(8))[0]
            mask = self._read(4) if b1 & 0x80 else b""
            payload = self._read(n)
            if mask:
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
            if opcode == 0x8:
                raise ConnectionError("OBS closed the connection")
            if opcode == 0x9:
                self._send_frame(0xA, payload)
                continue
            if opcode in (0x0, 0x1, 0x2):
                message += payload
                if b0 & 0x80:
                    return json.loads(message.decode("utf-8"))


# ---------------------------------------------------------------- director loop


def run(args: argparse.Namespace) -> None:
    obs: Obs | None = None
    if not args.dry_run:
        obs = Obs(args.host, args.port, args.password)
        log(f"connected to OBS at {args.host}:{args.port}")

    director = SceneDirector(hold_seconds=args.hold)
    active_obs_scene: str | None = None

    def apply_scene(scene: str, reason: str) -> None:
        nonlocal active_obs_scene
        if scene == active_obs_scene:
            return
        if obs is not None:
            obs.set_scene(scene)
        active_obs_scene = scene
        if args.verbose or args.dry_run:
            log(f"scene -> {scene} ({reason})")

    try:
        apply_scene(GRID_SCENE, "initial")
        while True:
            try:
                log(f"connecting to SSE stream {args.url} …")
                for event in iter_sse(args.url, poll_interval=args.poll_interval):
                    now = time.monotonic()
                    scene = director.pick_scene(event, now=now)
                    reason = normalize_event_type(event) or "hold-expired" if event is None else (
                        f"{normalize_event_type(event)}:{extract_player(event) or '-'}"
                    )
                    apply_scene(scene, reason)
            except (ConnectionError, OSError) as ex:
                log(f"SSE stream unavailable ({ex}); retrying in 2 s…")
                time.sleep(2)
    finally:
        if obs is not None:
            obs.close()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default=EVENTS_URL, help=f"SSE events endpoint (default: {EVENTS_URL})")
    ap.add_argument("--host", default="localhost", help="OBS WebSocket host (default: localhost)")
    ap.add_argument("--port", type=int, default=4455, help="OBS WebSocket port (default: 4455)")
    ap.add_argument("--password", default=os.environ.get("OBS_WS_PASSWORD", ""), help="OBS WebSocket password")
    ap.add_argument("--hold", type=float, default=MIN_HOLD_SECONDS,
                    help="Minimum seconds to hold each focus scene (>= 3.0)")
    ap.add_argument("--poll-interval", type=float, default=0.25,
                    help="Seconds between timer checks when SSE stream is idle")
    ap.add_argument("--dry-run", action="store_true", help="Log scene switches without connecting to OBS")
    ap.add_argument("-v", "--verbose", action="store_true", help="Log every scene switch")
    args = ap.parse_args()

    while True:
        try:
            run(args)
            return
        except KeyboardInterrupt:
            return
        except (ConnectionError, OSError) as ex:
            log(f"OBS connection problem: {ex}. Retrying in 5 s…")
            time.sleep(5)


if __name__ == "__main__":
    main()
