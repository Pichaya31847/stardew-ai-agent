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
