"""Record the run with OBS, cutting out the time the game sits frozen waiting for the AI.

The mod freezes game time while the model thinks (`frozen` in /api/state). This script watches that flag
and pauses the OBS recording while the game is frozen, so the video only contains gameplay. When the AI
posts a `think` (or the diary), recording resumes for a few seconds so the thought typed on the overlay
is in the video.

Usage (OBS open with obs-websocket enabled, game running with the mod):

    py runner/obs_recorder.py                      # starts recording if needed, Ctrl+C to quit
    py runner/obs_recorder.py --hold 3 --stop-on-exit

Only the standard library is used (a tiny WebSocket client for obs-websocket v5).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import struct
import time
import urllib.request
from datetime import datetime

API = "http://localhost:8770"
TYPE_SECONDS_PER_CHAR = 0.022  # overlay.html types thoughts at 22 ms per character
OVERLAY_POLL_SECONDS = 0.5     # overlay.html polls /api/feed every 400 ms


def log(msg: str):
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


def api_get(path: str):
    with urllib.request.urlopen(API + path, timeout=3) as r:
        return json.loads(r.read().decode("utf-8"))


# ---------------------------------------------------------------- minimal obs-websocket v5 client

class Obs:
    def __init__(self, host: str, port: int, password: str = ""):
        self.sock = socket.create_connection((host, port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((f"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                           f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
                           "Sec-WebSocket-Protocol: obswebsocket.json\r\n\r\n").encode())
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
        identify = {"rpcVersion": 1, "eventSubscriptions": 0}
        auth = hello.get("d", {}).get("authentication")
        if auth:
            if not password:
                raise ConnectionError("OBS asks for a password: pass --password")
            secret = base64.b64encode(hashlib.sha256((password + auth["salt"]).encode()).digest()).decode()
            identify["authentication"] = base64.b64encode(
                hashlib.sha256((secret + auth["challenge"]).encode()).digest()).decode()
        self._send_json({"op": 1, "d": identify})
        msg = self._recv_json()
        if msg.get("op") != 2:
            raise ConnectionError(f"OBS identify failed: {msg}")

    def request(self, request_type: str, data: dict | None = None) -> dict:
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

    def close(self):
        try:
            self._send_frame(0x8, struct.pack(">H", 1000))
            self.sock.close()
        except OSError:
            pass

    # -- framing

    def _send_json(self, obj: dict):
        self._send_frame(0x1, json.dumps(obj).encode("utf-8"))

    def _send_frame(self, opcode: int, payload: bytes):
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

    def _recv_json(self) -> dict:
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


# ---------------------------------------------------------------- recorder loop

def hold_seconds(entry: dict, args) -> float:
    text = entry.get("text") or ""
    base = args.diary_hold if entry.get("kind") == "diary" else args.hold
    # Keep the clip long enough for the overlay to finish typing the text, then read it.
    return OVERLAY_POLL_SECONDS + max(base, len(text) * TYPE_SECONDS_PER_CHAR + args.read_time)


def refresh_overlays(obs: Obs):
    """Reload browser sources that show the mod's pages. If OBS opened before the game, they hold an error page."""
    for inp in obs.request("GetInputList", {"inputKind": "browser_source"}).get("inputs", []):
        name = inp["inputName"]
        url = obs.request("GetInputSettings", {"inputName": name}).get("inputSettings", {}).get("url", "")
        if url.startswith(API) or ":8770/" in url:
            obs.request("PressInputPropertiesButton", {"inputName": name, "propertyName": "refreshnocache"})
            log(f"refreshed overlay source \"{name}\"")


def run(args):
    obs = Obs(args.host, args.port, args.password)
    log(f"connected to OBS at {args.host}:{args.port}")
    status = obs.request("GetRecordStatus")
    if not status.get("outputActive"):
        if args.no_start:
            raise SystemExit("OBS is not recording (start it, or drop --no-start).")
        obs.request("StartRecord")
        log("recording started")
        status = {"outputActive": True, "outputPaused": False}
    paused = bool(status.get("outputPaused"))

    while True:
        try:
            feed = api_get("/api/feed?after=0")
            break
        except OSError:
            log(f"waiting for the game API at {API} …")
            time.sleep(3)
    seq = max((e.get("seq", 0) for e in feed.get("entries", [])), default=0)
    if not args.no_refresh_overlay:
        refresh_overlays(obs)
    api_down = False

    hold_until = 0.0
    frozen_since: float | None = None
    t_start = time.monotonic()
    paused_total = 0.0
    paused_at = time.monotonic() if paused else None
    last_report = t_start

    def set_paused(want: bool, why: str):
        nonlocal paused, paused_at, paused_total
        if want == paused:
            return
        try:
            obs.request("PauseRecord" if want else "ResumeRecord")
        except RuntimeError:
            st = obs.request("GetRecordStatus")
            if not st.get("outputActive"):
                raise SystemExit("Recording was stopped in OBS; exiting.")
            if bool(st.get("outputPaused")) != want:
                raise
        paused = want
        now = time.monotonic()
        if want:
            paused_at = now
        elif paused_at is not None:
            paused_total += now - paused_at
            paused_at = None
        if args.verbose:
            log(("⏸ pause" if want else "⏺ resume") + f" ({why})")

    try:
        while True:
            now = time.monotonic()
            try:
                feed = api_get(f"/api/feed?after={seq}")
            except OSError:
                api_down = True
                time.sleep(1)  # game restarting / loading: leave OBS as it is
                continue
            if api_down:
                api_down = False
                if not args.no_refresh_overlay:
                    refresh_overlays(obs)
            for e in feed.get("entries", []):
                seq = max(seq, e.get("seq", 0))
                if e.get("kind") in ("think", "diary"):
                    hold_until = max(hold_until, now + hold_seconds(e, args))
                    if args.verbose:
                        log(f"{e['kind']}: {(e.get('text') or '')[:60]}")

            frozen = bool(feed.get("state", {}).get("frozen"))
            frozen_since = (frozen_since or now) if frozen else None
            waiting = frozen_since is not None and now - frozen_since >= args.min_pause

            if now < hold_until:
                set_paused(False, "showing thought")
            elif waiting:
                set_paused(True, "waiting for the AI")
            else:
                set_paused(False, "game running")

            cut = paused_total + (now - paused_at if paused_at is not None else 0)
            video = now - t_start - cut
            if now - last_report >= args.report_every:
                last_report = now
                log(f"real {(now - t_start) / 60:.1f} min · cut {cut / 60:.1f} min · video ≈ {video / 60:.1f} min")
            if args.max_minutes and video >= args.max_minutes * 60:
                log(f"video reached {args.max_minutes:g} min")
                args.stop_on_exit = True
                return
            time.sleep(args.interval)
    finally:
        try:
            if paused:
                obs.request("ResumeRecord")
            if args.stop_on_exit:
                out = obs.request("StopRecord")
                log(f"recording stopped → {out.get('outputPath', '')}")
            else:
                log("left OBS recording (resumed). Stop it in OBS when done.")
        finally:
            obs.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=4455)
    ap.add_argument("--password", default=os.environ.get("OBS_WS_PASSWORD", ""))
    ap.add_argument("--hold", type=float, default=3.0, help="Minimum seconds of video kept for each thought")
    ap.add_argument("--diary-hold", type=float, default=6.0, help="Minimum seconds kept for the end-of-day diary")
    ap.add_argument("--read-time", type=float, default=1.5,
                    help="Extra seconds after the overlay finishes typing a long thought")
    ap.add_argument("--min-pause", type=float, default=0.8,
                    help="Only pause after the game has been frozen this long (avoids tiny cuts between tool calls)")
    ap.add_argument("--interval", type=float, default=0.2, help="Polling interval in seconds")
    ap.add_argument("--report-every", type=float, default=300, help="Seconds between progress lines")
    ap.add_argument("--max-minutes", type=float, default=0,
                    help="Stop recording and exit once the (cut) video is this long. 0 = no limit")
    ap.add_argument("--no-start", action="store_true", help="Don't start recording; require it to be running already")
    ap.add_argument("--stop-on-exit", action="store_true", help="Stop the OBS recording when this script exits")
    ap.add_argument("--no-refresh-overlay", action="store_true",
                    help="Don't reload the overlay browser source in OBS when the game API comes up")
    ap.add_argument("-v", "--verbose", action="store_true", help="Log every pause/resume")
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
