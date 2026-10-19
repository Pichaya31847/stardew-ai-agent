"""Pure helpers of runner/obs_recorder.py: per-seat holds, day detection, video_map lines (no OBS, no game)."""

import json
import re
from datetime import datetime
from types import SimpleNamespace

import obs_recorder as rec

ARGS = SimpleNamespace(hold=3.0, diary_hold=6.0, read_time=1.5)


def think(player, text="hm"):
    return {"kind": "think", "player": player, "text": text}


def test_strip_texts_overlap_per_player():
    until = {}
    rec.add_hold(until, think("p1"), 100.0, ARGS)
    one = rec.hold_seconds(think("p1"), ARGS)
    rec.add_hold(until, {"kind": "say", "player": "p1", "text": "hm"}, 100.0, ARGS)
    assert until["p1"] == 100.0 + one           # shown at once next to the first, not queued after it
    rec.add_hold(until, think("p2"), 100.0, ARGS)
    assert until["p2"] == 100.0 + one           # other seat has its own hold


def test_hold_restarts_from_now_after_it_ran_out():
    until = {"p1": 50.0}
    rec.add_hold(until, think("p1"), 100.0, ARGS)
    assert until["p1"] == 100.0 + rec.hold_seconds(think("p1"), ARGS)


def test_say_uses_plain_max():
    until = {}
    say = {"kind": "say", "player": "p1", "text": "hi"}
    rec.add_hold(until, say, 100.0, ARGS)
    rec.add_hold(until, say, 100.0, ARGS)
    assert until["p1"] == 100.0 + rec.hold_seconds(say, ARGS)


def test_thought_hold_is_a_flat_beat():
    assert rec.hold_seconds(think("p1", "x" * 400), ARGS) == rec.hold_seconds(think("p1"), ARGS) == rec.OVERLAY_POLL_SECONDS + ARGS.hold


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
