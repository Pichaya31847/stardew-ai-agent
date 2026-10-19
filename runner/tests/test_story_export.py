"""Tests for runner/story_export.py: log time -> video time through video_map.jsonl, markers.csv, story tables."""

from __future__ import annotations

import csv
import json
import re
from pathlib import Path

import pytest

import story_export as se

IMPORTER = Path(r"C:\Users\admin\Documents\GitHub\duckxy-ai-life-sim\tools\common\resolve_import_markers.py")


def ts(sec: float) -> str:
    """A log timestamp `sec` seconds after 00:00:00 in the mod's format."""
    whole = int(sec)
    return f"2026-10-19T00:{whole // 60:02}:{whole % 60:02}.{int(round((sec - whole) * 1000)):03}+07:00"


# take 1: record 0-10 s, paused 10-30 s, record 30-40 s; gap; take 2: 100-110 s
MAP = [
    {"ts": ts(0), "ev": "start", "video_ms": 0},
    {"ts": ts(10), "ev": "pause", "video_ms": 10000},
    {"ts": ts(30), "ev": "resume", "video_ms": 10000},
    {"ts": ts(40), "ev": "stop", "video_ms": 20000, "path": "C:/rec/take one.mkv"},
    {"ts": ts(100), "ev": "start", "video_ms": 0},
    {"ts": ts(110), "ev": "stop", "video_ms": 10000, "path": "C:/rec/take two.mkv"},
]


def at(sec):
    takes = se.load_takes(MAP)
    hit = se.video_ms_at(takes, se.parse_ts(ts(sec)))
    return None if hit is None else (takes.index(hit[0]), hit[1])


def test_recording_is_anchor_plus_delta():
    assert at(4.5) == (0, 4500)
    assert at(35) == (0, 15000)
    assert at(105) == (1, 5000)


def test_pause_lands_on_the_resume_frame():
    assert at(12) == (0, 10000)
    assert at(29.9) == (0, 10000)
    assert at(30) == (0, 10000)


def test_elapsed_is_capped_at_the_next_anchor():
    # a pause anchor logged late: wall time ran 12 s past start but the video only holds 10 s
    takes = se.load_takes([MAP[0], {"ts": ts(12), "ev": "pause", "video_ms": 10000}])
    assert se.video_ms_at(takes, se.parse_ts(ts(11.5)))[1] == 10000


def test_between_takes_and_before_first_has_no_media():
    assert at(60) is None
    assert at(120) is None


def test_timecode_at_60fps():
    assert se.timecode(0, 60) == "00:00:00:00"
    assert se.timecode(1500, 60) == "00:00:01:30"
    assert se.timecode(3_723_999, 60) == "01:02:03:59"


def write_run(tmp_path: Path, entries: list[dict]) -> Path:
    (tmp_path / "logs").mkdir()
    (tmp_path / "logs" / "day_001.jsonl").write_text(
        "\n".join(json.dumps(e, ensure_ascii=False) for e in entries) + "\n{broken", encoding="utf-8")
    (tmp_path / "video_map.jsonl").write_text("\n".join(json.dumps(m) for m in MAP), encoding="utf-8")
    return tmp_path


def entry(sec, kind, player="p1", text="x", **extra):
    return {"ts": ts(sec), "day": 1, "gameTime": "06:10", "player": player, "kind": kind, "text": text, **extra}


def test_export_writes_markers_and_story(tmp_path, capsys):
    media = tmp_path / "media"
    media.mkdir()
    (media / "take one.mp4").write_bytes(b"")  # take two has no file: blank media + warning
    run = write_run(tmp_path, [
        entry(5, "say", text="สวัสดี @p2 | hi"),
        entry(12, "say", text="psst", args={"heard": ["p3"]}),  # say names who could hear it
        entry(13, "action", tool="walk_to"),                       # not a story line
        entry(14, "journal", text="lessons", args={"self": "ฉันขี้เกียจ"}),
        entry(15, "journal", text="lessons only"),                 # no self: skipped
        entry(16, "system", text="Save loaded: x"),                # skipped
        entry(36, "system", text="Sleeping..."),
        entry(60, "think", player="p2", text="between takes"),
        entry(105, "theft", player="p3", text="took", args={"victim": "p1"}),
        entry(106, "event", tool="gift", text="Abigail loved it"),
    ])
    rows = se.export(run, media_dir=media, fps=60)

    with open(run / "edit" / "markers.csv", encoding="utf-8-sig", newline="") as fh:
        reader = csv.DictReader(fh)
        assert reader.fieldnames == se.COLUMNS
        got = list(reader)
    assert [r["type"] for r in got] == ["say", "say", "self", "system", "think", "theft", "event:gift"]
    say, near, self_, sleep, gap, theft, gift = got
    assert say["media_timecode"] == "00:00:05:00" and say["media_file"].endswith("take one.mp4")
    assert say["target"] == "" and near["target"] == "p3" and near["media_timecode"] == "00:00:10:00"
    assert self_["text"] == "ฉันขี้เกียจ"
    assert sleep["media_timecode"] == "00:00:16:00"
    assert gap["media_file"] == "" and gap["media_timecode"] == ""
    assert theft["target"] == "p1" and theft["media_timecode"] == "00:00:05:00" and theft["media_file"] == ""
    assert "take two" in capsys.readouterr().err
    assert len({r["event_id"] for r in got}) == len(got) == len(rows)

    md = (run / "edit" / "story_day_001.md").read_text(encoding="utf-8")
    assert "| video (take @ TC) | game time | P1 | P2 | P3 | world |" in md
    assert "| take one @ 00:00:05:00 | 06:10 | **say** สวัสดี @p2 \\| hi |" in md
    assert "|  | 06:10 |  | **think** between takes |" in md
    assert "p1: **event:gift** Abigail loved it |" in md


def test_days_filter_and_mkv_fallback(tmp_path):
    media = tmp_path / "media"
    media.mkdir()
    (media / "take one.mkv").write_bytes(b"")
    run = write_run(tmp_path, [entry(5, "say")])
    (run / "logs" / "day_002.jsonl").write_text(json.dumps(entry(6, "say") | {"day": 2}), encoding="utf-8")
    rows = se.export(run, days=se.parse_days("2"), media_dir=media)
    assert [r["game_day"] for r in rows] == ["2"]
    assert rows[0]["media_file"].endswith("take one.mkv")


def test_csv_header_matches_importer():
    assert se.COLUMNS == ["event_id", "game_day", "game_time", "actor", "target", "type", "text", "source",
                          "team", "media_file", "media_timecode"]
    if not IMPORTER.is_file():
        pytest.skip("duckxy-ai-life-sim importer not checked out here")
    src = IMPORTER.read_text(encoding="utf-8")
    used = set(re.findall(r'row\["(\w+)"\]', src))
    assert used <= set(se.COLUMNS)
    # its timecode parser takes ours
    ns: dict = {}
    exec(src.split("def import_markers")[0], ns)
    assert ns["frame_of"](se.timecode(3_723_999, 60), 60) == 3_723_999 * 60 // 1000
