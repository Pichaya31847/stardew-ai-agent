"""Pure helpers of runner/obs_recorder.py: per-seat holds, day detection, video_map lines (no OBS, no game)."""

import json
import re
from datetime import datetime
from types import SimpleNamespace


import obs_recorder as rec

ARGS = SimpleNamespace(diary_hold=6.0, read_time=1.5)


def think(player, text="hm"):
    return {"kind": "think", "player": player, "text": text}


def diary(player, text="day"):
    return {"kind": "diary", "player": player, "text": text}


def test_think_never_holds_a_frozen_frame():
    until = {}
    assert not rec.add_hold(until, think("p1", "x" * 400), 100.0, ARGS)
    assert until == {}


def test_say_never_holds_a_frozen_frame():
    until = {}
    assert not rec.add_hold(until, {"kind": "say", "player": "p2", "text": "hi"}, 100.0, ARGS)  # the mod shows it once the world runs
    assert until == {}


def test_diaries_overlap_per_player():
    until = {}
    rec.add_hold(until, diary("p1"), 100.0, ARGS)
    one = rec.hold_seconds(diary("p1"), ARGS)
    assert one == rec.OVERLAY_POLL_SECONDS + ARGS.diary_hold
    rec.add_hold(until, diary("p1"), 100.0, ARGS)
    assert until["p1"] == 100.0 + one           # shown at once, not queued after the first
    rec.add_hold(until, diary("p2"), 100.0, ARGS)
    assert until["p2"] == 100.0 + one           # other seat has its own hold


def menu_state(m2, m4=None, thinking=()):
    return {"thinking": list(thinking),
            "players": [{"id": "p1", "role": "ai", "state": {"menu": None}},
                        {"id": "p2", "role": "ai", "state": {"menu": m2}},
                        {"id": "p4", "role": "camera", "state": {"menu": m4}}]}


def test_menus_opened_only_on_the_poll_a_menu_appears():
    since = {}
    assert rec.menus_opened(since, menu_state(None), 0.0) == []
    assert rec.menus_opened(since, menu_state("DialogueBox", "GameMenu"), 1.0) == ["p2"]   # camera seat ignored
    assert rec.menus_opened(since, menu_state("DialogueBox"), 2.0) == []                   # still open: same menu
    assert since == {"p2": 1.0}
    assert rec.menus_opened(since, menu_state(None), 3.0) == []
    assert since == {}
    assert rec.menus_opened(since, {"players": [{"id": "p2", "role": "ai"}]}, 4.0) == []  # screen not ready


def test_menu_holds_only_a_box_its_own_seat_is_thinking_over():
    since = {}
    rec.menus_opened(since, menu_state("ItemGrabMenu"), 10.0)
    assert rec.menu_holds(since, menu_state("ItemGrabMenu", thinking=["p2"]), 11.0, 2.5)      # p2's action ended on it
    assert not rec.menu_holds(since, menu_state("ItemGrabMenu", thinking=["p2"]), 12.5, 2.5)  # held long enough
    assert not rec.menu_holds(since, menu_state("TV", thinking=["p1"]), 11.0, 2.5)            # p2 mid-action, p1 froze it
    rec.menus_opened(since, menu_state(None), 11.5)
    assert not rec.menu_holds(since, menu_state(None, thinking=["p2"]), 11.6, 2.5)            # closed: nothing to show


def test_hold_restarts_from_now_after_it_ran_out():
    until = {"p1": 50.0}
    rec.add_hold(until, diary("p1"), 100.0, ARGS)
    assert until["p1"] == 100.0 + rec.hold_seconds(diary("p1"), ARGS)


def test_journal_self_holds_with_diary_time_and_self_text():
    e = {"kind": "action", "tool": "journal", "player": "p2", "args": {"self": "x" * 400}}
    until = {}
    assert rec.add_hold(until, e, 0.0, ARGS)
    assert until["p2"] == rec.OVERLAY_POLL_SECONDS + 400 * rec.READ_SECONDS_PER_CHAR + ARGS.read_time
    short = {"kind": "action", "tool": "journal", "player": "p2", "args": {"self": "me"}}
    assert rec.hold_seconds(short, ARGS) == rec.OVERLAY_POLL_SECONDS + ARGS.diary_hold


def test_journal_without_self_and_other_kinds_hold_nothing():
    until = {}
    assert not rec.add_hold(until, {"kind": "action", "tool": "journal", "player": "p1", "args": {"diary": "d"}}, 0.0, ARGS)
    assert not rec.add_hold(until, {"kind": "action", "tool": "journal", "player": "p1", "args": {"self": "  "}}, 0.0, ARGS)
    assert not rec.add_hold(until, {"kind": "action", "tool": "walk", "player": "p1"}, 0.0, ARGS)
    assert until == {}


def test_feed_day_prefers_new_day_line():
    assert rec.feed_day([], {"day": 3}) == 3
    assert rec.feed_day([], {"ready": False}) is None
    assert rec.feed_day([{"kind": "system", "text": "New day: farm day 4"}], {"day": 3}) == 4
    assert rec.feed_day([{"kind": "think", "text": "New day: farm day 9"}], {"day": 3}) == 3


def test_map_line_format():
    ts = datetime(2026, 10, 19, 8, 5, 3, 120000)
    line = json.loads(rec.map_line("pause", 61234.0, ts=ts))
    assert list(line) == ["ts", "ev", "video_ms"]
    assert line["ev"] == "pause" and line["video_ms"] == 61234
    # same shape as the mod's log ts: yyyy-MM-ddTHH:mm:ss.fffzzz
    assert re.fullmatch(r"2026-10-19T08:05:03\.120[+-]\d\d:\d\d", line["ts"])
    stop = json.loads(rec.map_line("stop", 5, path="D:/rec/day1.mkv", ts=ts))
    assert stop["path"] == "D:/rec/day1.mkv"


def item(name, kind, index, enabled=True):
    return {"sourceName": name, "inputKind": kind, "sceneItemIndex": index, "sceneItemEnabled": enabled}


def test_scene_window_capture_above_game_capture_is_refused():
    items = [item("game", "game_capture", 0), item("win", "window_capture", 1), item("think P1", "browser_source", 2)]
    assert rec.scene_problems(items) == ['"win" (window_capture) is enabled above the Game Capture and covers it: disable it']


def test_scene_console_window_captures_over_p4_are_fine_but_the_game_window_is_not():
    items = [item("game", "game_capture", 0), item("runner", "window_capture", 1), item("old", "window_capture", 2)]
    windows = {"runner": "Stardew Runner:ConsoleWindowClass:conhost.exe",
               "old": "Stardew Valley 1.6.15 - running SMAPI 4.5.2 with 4 mods:SDL_app:StardewModdingAPI.exe"}
    assert rec.scene_problems(items, windows) == ['"old" (window_capture) is enabled above the Game Capture and covers it: disable it']


def test_smapi_console_is_the_host_console_not_the_game_window_or_a_farmhand():
    windows = ["Stardew Valley 1.6.15 - running SMAPI 4.5.2 with 4 mods:SDL_app:StardewModdingAPI.exe",
               "SMAPI 4.5.2 - running Stardew Valley 1.6.15 with 5 mods [farmhand]:CASCADIA_HOSTING_WINDOW_CLASS:WindowsTerminal.exe",
               "Stardew Runner:CASCADIA_HOSTING_WINDOW_CLASS:WindowsTerminal.exe",
               "SMAPI 4.5.2 - running Stardew Valley 1.6.15 with 4 mods:CASCADIA_HOSTING_WINDOW_CLASS:WindowsTerminal.exe"]
    assert rec.smapi_console(windows) == windows[3]
    assert rec.smapi_console(windows[:3]) is None


def test_scene_disabled_or_lower_covering_source_is_fine():
    assert rec.scene_problems([item("game", "game_capture", 1), item("win", "window_capture", 2, enabled=False),
                               item("desk", "monitor_capture", 0)]) == []


def test_scene_without_game_capture_is_refused():
    assert rec.scene_problems([item("win", "window_capture", 0)]) == ["no enabled Game Capture source in the scene"]


def bmp(width, height, rgb, bpp=24):
    import struct
    step = bpp // 8
    row = (width * step + 3) & ~3
    px = bytes(rgb[::-1]) + (b"\xff" if step == 4 else b"")
    body = (px * width + b"\0" * (row - width * step)) * height
    header = b"BM" + struct.pack("<IHHI", 54 + len(body), 0, 0, 54)
    info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, bpp, 0, len(body), 0, 0, 0, 0)
    return header + info + body


def test_bmp_brightness_black_vs_game():
    assert rec.bmp_mean_brightness(bmp(5, 3, (0, 0, 0))) == 0           # unhooked capture; 5 px rows are padded
    assert rec.bmp_mean_brightness(bmp(5, 3, (0, 0, 0), bpp=32)) == 0   # alpha 255 is not brightness
    assert rec.bmp_mean_brightness(bmp(4, 2, (30, 60, 90))) == 60


class FakeObs:
    """Just enough obs-websocket: Stop/Pause/Resume fail like OBS when nothing is recording (501 / 503)."""

    def __init__(self, *_):
        self.active, self.paused, self.calls, self.formats = False, False, [], []

    def request(self, kind, data=None):
        self.calls.append(kind)
        if kind == "GetRecordStatus":
            return {"outputActive": self.active, "outputPaused": self.paused, "outputDuration": 1000}
        if kind == "StartRecord":
            self.active = True
        elif kind in ("StopRecord", "PauseRecord", "ResumeRecord"):
            if not self.active:
                raise RuntimeError(f"{kind} failed: {501 if kind == 'StopRecord' else 503} ")
            if kind == "StopRecord":
                self.active = self.paused = False
                return {"outputPath": "C:/v/day.mkv"}
            self.paused = kind == "PauseRecord"
        elif kind == "GetInputList":
            return {"inputs": []}
        elif kind == "GetProfileParameter":
            return {"parameterValue": "%CCYY-%MM-%DD %hh-%mm-%ss"}
        elif kind == "SetProfileParameter":
            self.formats.append(data["parameterValue"])
        return {}

    def close(self):
        pass


def args_for(tmp_path, **kw):
    a = dict(host="h", port=1, password="", out=str(tmp_path), no_start=False, no_refresh_overlay=True,
             no_pause=False, min_pause=0.0, menu_hold=2.5, interval=0, report_every=1e9, max_minutes=0,
             stop_on_exit=True, verbose=False, diary_hold=6.0, read_time=1.5, stop_file=str(tmp_path / "rec.stop"))
    a.update(kw)
    return SimpleNamespace(**a)


def test_game_restart_after_obs_stopped_starts_a_new_file_instead_of_crashing(tmp_path, monkeypatch):
    obs = FakeObs()
    monkeypatch.setattr(rec, "Obs", lambda *a: obs)
    monkeypatch.setattr(rec, "check_scene", lambda o: None)
    monkeypatch.setattr(rec.time, "sleep", lambda s: None)
    frozen = {"entries": [], "state": {"day": 1, "frozen": True}}
    args = args_for(tmp_path)
    steps = iter(["ok", "ok", "down", "obs-stopped", "ok", "stop"])

    def api(path):
        step = next(steps)
        if step == "down":
            raise OSError("game closed")
        if step == "obs-stopped":
            obs.active = obs.paused = False  # stopped in OBS while the game was away
            raise OSError("still loading")
        if step == "stop":
            (tmp_path / "rec.stop").write_text("")
            raise OSError("checked on the next pass")
        return frozen

    monkeypatch.setattr(rec, "api_get", api)
    rec.run(args)  # returns via the stop file instead of raising 501 / 503
    events = [json.loads(l)["ev"] for l in (tmp_path / "video_map.jsonl").read_text(encoding="utf-8").splitlines()]
    assert obs.calls.count("StartRecord") == 2               # the first file, then a new one when the game is back
    assert events[-2:] == ["frozen", "stop"] or events[-1] == "stop"
    assert events.count("start") == 2 and events.count("stop") == 2
    assert not obs.active                                   # --stop-on-exit stopped the second file
    assert not (tmp_path / "rec.stop").exists()             # consumed, so the next batch doesn't stop at once
    # both files named after the farm day, then OBS's own format back for the user's recordings
    assert obs.formats == ["DAY01 %CCYY-%MM-%DD %hh-%mm-%ss"] * 2 + ["%CCYY-%MM-%DD %hh-%mm-%ss"]
