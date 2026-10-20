"""Record the run with OBS, cutting out the time the game sits frozen waiting for the AI.

The mod freezes game time while the model thinks (`frozen` in /api/state). This script watches that flag
and pauses the OBS recording while the game is frozen, so the video only contains gameplay. A
`say` never holds a frozen frame either: the mod shows its bubble when the world runs again, each line in turn. A `think` stays on the
overlay into the live footage after it and never holds a frozen frame. The evening diary and `self` note (no live
footage follows them) keep recording long enough to read, and so does a box an action leaves open as it ends (item box, letter). Each farm day gets its
own OBS file.

With `--out DIR`, every start / pause / resume / stop is appended to `DIR/video_map.jsonl` as
`{ts, ev, video_ms, path}` (ts = local time like the mod's log, video_ms = OBS's own outputDuration,
path only on stop), so log timestamps can be mapped to positions in the video files.

Usage (OBS open with obs-websocket enabled, game running with the mod):

    py runner/obs_recorder.py                      # starts recording if needed, Ctrl+C to quit
    py runner/obs_recorder.py --diary-hold 8 --stop-on-exit
    py runner/obs_recorder.py --out runs/run1      # also writes runs/run1/video_map.jsonl
    py runner/obs_recorder.py --no-pause --out runs/run1   # no pauses; cut the still time afterwards
    py runner/obs_recorder.py --out runs/run1 --stop-on-exit --stop-file runs/run1/recorder.stop   # ends with the batch

Only the standard library is used (a tiny WebSocket client for obs-websocket v5).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import socket
import struct
import sys
import time
import urllib.request
from datetime import datetime

API = "http://localhost:8770"
READ_SECONDS_PER_CHAR = 0.022  # overlay.html shows a thought whole at once; this is time to read it
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
                    if status.get("code") == 207:  # OBS is still starting up: retried like a refused connection
                        raise ConnectionError(f"{request_type}: OBS is not ready yet")
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

def is_self_note(entry: dict) -> bool:
    return entry.get("tool") == "journal" and bool(((entry.get("args") or {}).get("self") or "").strip())


def hold_text(entry: dict) -> str:
    return entry["args"]["self"] if is_self_note(entry) else (entry.get("text") or "")


def hold_seconds(entry: dict, args) -> float:
    return OVERLAY_POLL_SECONDS + max(args.diary_hold, len(hold_text(entry)) * READ_SECONDS_PER_CHAR + args.read_time)


def add_hold(until: dict, entry: dict, now: float, args) -> bool:
    """Extend the player's hold for a diary or self note. Each shows at once, so holds overlap."""
    p = entry.get("player") or ""
    if entry.get("kind") != "diary" and not is_self_note(entry):
        return False
    until[p] = max(until.get(p, 0.0), now + hold_seconds(entry, args))
    return True


def menus_opened(since: dict, state: dict, now: float) -> list[str]:
    """AI seats whose screen opened a menu (item box, dialogue, letter) since the last poll; `since` keeps when each
    seat's open menu appeared."""
    opened = []
    for p in state.get("players") or []:
        if p.get("role") != "ai":
            continue
        if (p.get("state") or {}).get("menu"):
            if p["id"] not in since:
                since[p["id"]] = now
                opened.append(p["id"])
        else:
            since.pop(p["id"], None)
    return opened


def menu_holds(since: dict, state: dict, now: float, hold: float) -> bool:
    """A box an action left open as it ended (seeds received, a letter) keeps recording for its first `hold` seconds: its
    seat is the one thinking. A menu opened mid-action (the TV's channels) waits for the world like the rest of the
    picture, and a closed one holds nothing."""
    thinking = set(state.get("thinking") or [])
    return any(p in thinking and now - t < hold for p, t in since.items())


def feed_day(entries: list, state: dict):
    """Farm day the feed shows: the latest "New day: farm day N" line, else state.day (None while loading)."""
    day = state.get("day")
    for e in entries:
        m = re.match(r"New day: farm day (\d+)", e.get("text") or "") if e.get("kind") == "system" else None
        if m:
            day = max(day or 0, int(m[1]))
    return day


def map_line(ev: str, video_ms, path: str | None = None, ts: datetime | None = None) -> str:
    """One video_map.jsonl line; ts matches the mod's log ts (yyyy-MM-ddTHH:mm:ss.fffzzz)."""
    line = {"ts": (ts or datetime.now()).astimezone().isoformat(timespec="milliseconds"),
            "ev": ev, "video_ms": int(video_ms or 0)}
    if path is not None:
        line["path"] = path
    return json.dumps(line, ensure_ascii=False)


COVERING_KINDS = ("window_capture", "monitor_capture")


def game_window(window: str | None) -> bool:
    """A Window Capture's "window" setting ("title:class:exe", ':' in a title is escaped) names the game's own window.
    Unknown counts as the game; consoles (runner, recorder, SMAPI) shown over P4's quarter don't."""
    return window is None or window.split(":")[1:2] == ["SDL_app"]


def file_name(day) -> str:
    """OBS's FilenameFormatting for a file that starts on farm day `day`: "DAY02 2026-10-20 21-15-20.mkv"."""
    stamp = "%CCYY-%MM-%DD %hh-%mm-%ss"
    return f"DAY{int(day):02d} {stamp}" if day else stamp


def scene_problems(items: list[dict], windows: dict[str, str] | None = None) -> list[str]:
    """What in the scene would keep the game out of the recording. items: GetSceneItemList's sceneItems; windows:
    each Window Capture's "window" setting by source name.

    A window or display capture of the game in exclusive fullscreen only gets stale frames; stacked above the
    Game Capture it recorded a frozen picture (2026-10-19)."""
    windows = windows or {}
    games = [i for i in items if i["inputKind"] == "game_capture" and i["sceneItemEnabled"]]
    if not games:
        return ["no enabled Game Capture source in the scene"]
    bottom = min(i["sceneItemIndex"] for i in games)
    return [f"\"{i['sourceName']}\" ({i['inputKind']}) is enabled above the Game Capture and covers it: disable it"
            for i in items
            if i["inputKind"] in COVERING_KINDS and i["sceneItemEnabled"] and i["sceneItemIndex"] > bottom
            and (i["inputKind"] != "window_capture" or game_window(windows.get(i["sourceName"])))]


def bmp_mean_brightness(data: bytes) -> float:
    """Mean of all colour channels (0..255) of an uncompressed 24/32-bit BMP, as OBS's GetSourceScreenshot returns."""
    offset, = struct.unpack_from("<I", data, 10)
    width, height, _, bpp = struct.unpack_from("<iiHH", data, 18)
    step = bpp // 8
    row = (abs(width) * step + 3) & ~3
    total = 0
    for y in range(abs(height)):
        px = data[offset + y * row: offset + y * row + abs(width) * step]
        total += sum(px) - (sum(px[3::4]) if step == 4 else 0)  # drop alpha
    return total / (abs(width) * abs(height) * 3)


def check_scene(obs: Obs, wait_s: float = 15.0):
    """Refuse to record a scene that shows no game: a covering capture, or a Game Capture still black (not hooked,
    e.g. the game was started on a hidden desktop or a second monitor)."""
    scene = obs.request("GetCurrentProgramScene")["currentProgramSceneName"]
    items = obs.request("GetSceneItemList", {"sceneName": scene})["sceneItems"]
    windows = {i["sourceName"]: obs.request("GetInputSettings", {"inputName": i["sourceName"]})
               .get("inputSettings", {}).get("window") for i in items if i["inputKind"] == "window_capture"}
    problems = scene_problems(items, windows)
    if problems:
        raise SystemExit(f"OBS scene \"{scene}\": " + "; ".join(problems))
    game = min((i for i in items if i["inputKind"] == "game_capture" and i["sceneItemEnabled"]),
               key=lambda i: i["sceneItemIndex"])["sourceName"]
    deadline = time.monotonic() + wait_s
    while True:
        shot = obs.request("GetSourceScreenshot", {"sourceName": game, "imageFormat": "bmp",
                                                    "imageWidth": 64, "imageHeight": 36})["imageData"]
        brightness = bmp_mean_brightness(base64.b64decode(shot.split(",", 1)[1]))
        if brightness > 2:
            log(f"OBS scene \"{scene}\": \"{game}\" shows the game (brightness {brightness:.0f})")
            return
        if time.monotonic() > deadline:
            raise SystemExit(f"OBS \"{game}\" is black: it has not hooked the game. Start the game on the main "
                             "monitor from your own desktop session, check the source's window, then try again.")
        time.sleep(1)


def smapi_console(windows: list[str]) -> str | None:
    """The host game's SMAPI console among OBS's "title:class:exe" window choices. Its title carries the mod count and
    the host/farmhand role, so a fixed title in the scene misses it."""
    return next((w for w in windows if w.startswith("SMAPI ") and "[farmhand]" not in w and ":SDL_app:" not in w), None)


def point_smapi_log(obs: Obs, source: str = "Log SMAPI"):
    """Aim the scene's SMAPI log Window Capture (shown over P4's quarter) at the console that is open now."""
    try:
        items = obs.request("GetInputPropertiesListPropertyItems", {"inputName": source, "propertyName": "window"})
    except RuntimeError:
        return  # no such source in this scene
    window = smapi_console([i.get("itemValue", "") for i in items.get("propertyItems", [])])
    if window:
        obs.request("SetInputSettings", {"inputName": source, "inputSettings": {"window": window}})
        log(f"\"{source}\" shows {window.split(':')[0]}")


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
    map_path = os.path.join(args.out, "video_map.jsonl") if args.out else None
    if map_path:
        os.makedirs(args.out, exist_ok=True)

    def write_map(ev: str, video_ms=None, path: str | None = None):
        if not map_path:
            return
        if video_ms is None:
            video_ms = obs.request("GetRecordStatus").get("outputDuration", 0)
        with open(map_path, "a", encoding="utf-8") as f:
            f.write(map_line(ev, video_ms, path) + "\n")

    def wait_active(want: bool):
        for _ in range(100):  # StartRecord / StopRecord take a moment to settle
            if bool(obs.request("GetRecordStatus").get("outputActive")) == want:
                return
            time.sleep(0.1)
        raise ConnectionError(f"OBS recording did not {'start' if want else 'stop'}")

    fmt = {"parameterCategory": "Output", "parameterName": "FilenameFormatting"}
    old_format = obs.request("GetProfileParameter", fmt).get("parameterValue")

    def start_record(day=None):
        obs.request("SetProfileParameter", {**fmt, "parameterValue": file_name(day)})
        obs.request("StartRecord")
        wait_active(True)
        write_map("start", 0)

    while True:
        try:
            feed = api_get("/api/feed?after=0")
            break
        except OSError:
            log(f"waiting for the game API at {API} …")
            time.sleep(3)
    check_scene(obs)
    point_smapi_log(obs)

    status = obs.request("GetRecordStatus")
    if not status.get("outputActive"):
        if args.no_start:
            raise SystemExit("OBS is not recording (start it, or drop --no-start).")
        start_record(feed.get("state", {}).get("day"))
        log("recording started")
        status = {"outputActive": True, "outputPaused": False}
    else:
        write_map("start", status.get("outputDuration", 0))  # attached: anchor to where the file is now
        if status.get("outputPaused"):
            write_map("pause", status.get("outputDuration", 0))
    paused = bool(status.get("outputPaused"))
    seq = max((e.get("seq", 0) for e in feed.get("entries", [])), default=0)
    file_day = feed.get("state", {}).get("day")
    if not args.no_refresh_overlay:
        refresh_overlays(obs)
    api_down = False

    hold_until: dict[str, float] = {}
    menus: dict[str, float] = {}
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
        write_map("pause" if want else "resume")
        now = time.monotonic()
        if want:
            paused_at = now
        elif paused_at is not None:
            paused_total += now - paused_at
            paused_at = None
        if args.verbose:
            log(("⏸ pause" if want else "⏺ resume") + f" ({why})")

    def stop_record() -> str:
        nonlocal paused, paused_at, paused_total
        st = obs.request("GetRecordStatus")
        video_ms = st.get("outputDuration", 0)
        # Already stopped (in OBS, or while the game was closed): StopRecord would fail with 501 and kill the recorder.
        path = obs.request("StopRecord").get("outputPath", "") if st.get("outputActive") else ""
        write_map("stop", video_ms, path or None)
        if paused_at is not None:
            paused_total += time.monotonic() - paused_at
        paused, paused_at = False, None
        return path

    def new_file(why: str, day=None):
        path = stop_record()
        wait_active(False)
        start_record(day)
        log(f"new recording file ({why}); previous → {path}")

    try:
        while True:
            now = time.monotonic()
            if args.stop_file and os.path.exists(args.stop_file):
                os.remove(args.stop_file)
                log("stop file found: the batch is over")
                args.stop_on_exit = True
                return
            try:
                feed = api_get(f"/api/feed?after={seq}")
            except OSError:
                if not api_down:
                    api_down = True
                    set_paused(True, "game API down")  # game restarting / loading
                time.sleep(1)
                continue
            entries, state = feed.get("entries", []), feed.get("state", {})
            if api_down:
                api_down = False
                new_file("game API back", state.get("day"))
                file_day = None
                if not args.no_refresh_overlay:
                    refresh_overlays(obs)
            day = feed_day(entries, state)
            if day is not None:
                if file_day is not None and day != file_day:
                    new_file(f"farm day {day}", day)
                file_day = day
            for e in entries:
                seq = max(seq, e.get("seq", 0))
                if add_hold(hold_until, e, now, args) and args.verbose:
                    log(f"{e.get('player')} {e.get('tool') or e['kind']}: {hold_text(e)[:60]}")

            for p in menus_opened(menus, state, now):
                write_map("menu")  # for check_recording.py
                if args.verbose:
                    log(f"{p} opened a menu")
            frozen = bool(state.get("frozen"))
            if frozen != (frozen_since is not None):
                write_map("frozen" if frozen else "live")  # flag flips, for check_recording.py's lag numbers
            frozen_since = (frozen_since or now) if frozen else None
            waiting = frozen_since is not None and now - frozen_since >= args.min_pause

            if args.no_pause:
                set_paused(False, "continuous")  # still stretches are cut afterwards (docs/recording.md, auto-editor)
            elif any(now < t for t in hold_until.values()) or menu_holds(menus, state, now, args.menu_hold):
                set_paused(False, "showing text")
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
            if paused and obs.request("GetRecordStatus").get("outputActive"):
                obs.request("ResumeRecord")
                write_map("resume")
            if args.stop_on_exit:
                log(f"recording stopped → {stop_record()}")
            else:
                log("left OBS recording (resumed). Stop it in OBS when done.")
        finally:
            if old_format:  # the user's own OBS recordings keep their usual names
                obs.request("SetProfileParameter", {**fmt, "parameterValue": old_format})
            obs.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=4455)
    ap.add_argument("--password", default=os.environ.get("OBS_WS_PASSWORD", ""))
    ap.add_argument("--diary-hold", type=float, default=6.0, help="Minimum seconds kept for the end-of-day diary")
    ap.add_argument("--read-time", type=float, default=1.5,
                    help="Extra seconds to read a long diary or self note")
    ap.add_argument("--menu-hold", type=float, default=2.5,
                    help="Seconds of frozen video kept for a box an AI's action left open as it ended (item box, letter)")
    ap.add_argument("--min-pause", type=float, default=0.0,
                    help="Only pause after the game has been frozen this long. 0: a cut on a frozen world is seamless, "
                         "while every frozen frame let through shows as a hitch")
    ap.add_argument("--no-pause", action="store_true",
                    help="Record continuously (one file per day) and cut the frozen time afterwards with auto-editor")
    ap.add_argument("--interval", type=float, default=0.05,
                    help="Polling interval in seconds (frozen frames reach the video for up to this long)")
    ap.add_argument("--report-every", type=float, default=300, help="Seconds between progress lines")
    ap.add_argument("--max-minutes", type=float, default=0,
                    help="Stop recording and exit once the (cut) video is this long. 0 = no limit")
    ap.add_argument("--no-start", action="store_true", help="Don't start recording; require it to be running already")
    ap.add_argument("--stop-on-exit", action="store_true", help="Stop the OBS recording when this script exits")
    ap.add_argument("--stop-file", default="",
                    help="Stop recording and exit once this file appears (record_day.ps1 creates it when the runner ends)")
    ap.add_argument("--no-refresh-overlay", action="store_true",
                    help="Don't reload the overlay browser source in OBS when the game API comes up")
    ap.add_argument("--out", default="",
                    help="Run folder for video_map.jsonl (log time to video position). Without it no map is written")
    ap.add_argument("-v", "--verbose", action="store_true", help="Log every pause/resume")
    args = ap.parse_args()
    sys.stdout.reconfigure(errors="replace")  # a redirected Windows console is cp1252: log lines carry ⏸ → and Thai

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
