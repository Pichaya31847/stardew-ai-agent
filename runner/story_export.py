"""Story export for editing: the day logs as Resolve markers and a VO-writing table, in video time.

OBS recording pauses while the world is frozen, so a log line's wall-clock `ts` is not its place in the video.
`runner/obs_recorder.py` writes `<out>/video_map.jsonl` with one line per recorder event:

    {"ts": "...", "ev": "start|pause|resume|stop", "video_ms": 12345, "path": "<recording file, stop only>"}

One take per recording file, ending at its stop. A log time inside a take maps through the last anchor at or
before it: a pause (or the stop) holds its `video_ms`; a start or resume adds the time elapsed since, capped at
the next anchor's `video_ms`. Lines outside every take get no media and are skipped by the importer.

Outputs, next to the logs:

  * `<out>/edit/markers.csv` for duckxy-ai-life-sim's `tools/common/resolve_import_markers.py`,
  * `<out>/edit/story_day_NNN.md`: one row per line, a column per player, for writing the Thai VO script.

    py runner/story_export.py --out runs --days 1-10 --media-dir D:/recordings --fps 60
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from bisect import bisect_right
from datetime import datetime
from pathlib import Path

COLUMNS = ["event_id", "game_day", "game_time", "actor", "target", "type", "text", "source", "team",
           "media_file", "media_timecode"]
KINDS = {"think", "say", "give", "emote", "theft", "event", "story", "diary", "journal",
         "pass_out", "interrupt", "notice", "system"}
PLAYERS = ("p1", "p2", "p3")
_DAY_FILE = re.compile(r"^day_(\d+)\.jsonl$")


def parse_ts(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def read_jsonl(path: Path) -> list[dict]:
    rows = []
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return rows
    for line in lines:
        try:
            row = json.loads(line)
        except ValueError:
            continue  # a half-written last line after a crash
        if isinstance(row, dict):
            rows.append(row)
    return rows


# ---------------------------------------------------------------------- video map


def load_takes(map_rows: list[dict]) -> list[dict]:
    """Group anchors into takes: {anchors: [(datetime, ev, video_ms)], path, end}. An unfinished take has no path."""
    takes, cur = [], None
    for row in map_rows:
        try:
            anchor = (parse_ts(row["ts"]), row["ev"], int(row["video_ms"]))
        except (KeyError, TypeError, ValueError):
            continue
        if anchor[1] == "start":
            cur = {"anchors": [anchor], "path": "", "end": None}
            takes.append(cur)
        elif cur is not None and cur["end"] is None:
            cur["anchors"].append(anchor)
            if anchor[1] == "stop":
                cur["end"], cur["path"] = anchor[0], row.get("path") or ""
    return takes


def video_ms_at(takes: list[dict], when: datetime) -> tuple[dict, int] | None:
    for take in takes:
        anchors = take["anchors"]
        if when < anchors[0][0] or (take["end"] is not None and when > take["end"]):
            continue
        i = bisect_right([a[0] for a in anchors], when) - 1
        ts, ev, ms = anchors[i]
        if ev in ("pause", "stop"):
            return take, ms
        ms += int((when - ts).total_seconds() * 1000)
        if i + 1 < len(anchors):
            ms = min(ms, anchors[i + 1][2])
        return take, ms
    return None


def timecode(ms: int, fps: int) -> str:
    frames = ms * fps // 1000
    secs, ff = divmod(frames, fps)
    return f"{secs // 3600:02}:{secs // 60 % 60:02}:{secs % 60:02}:{ff:02}"


def media_for(path: str, media_dir: Path | None, warned: set) -> str:
    """The recording as Resolve should import it: prefer the .mp4 remux, else the .mkv OBS wrote."""
    if not path:
        return ""
    src = Path(path)
    folder = media_dir or src.parent
    for ext in (".mp4", ".mkv"):
        candidate = folder / (src.stem + ext)
        if candidate.is_file():
            return str(candidate.resolve())
    if path not in warned:
        warned.add(path)
        print(f"warning: no {src.stem}.mp4/.mkv in {folder}; its markers get no media", file=sys.stderr)
    return ""


# ---------------------------------------------------------------------- log lines


def story_row(entry: dict) -> dict | None:
    """The marker fields for a log line, or None for lines that are not part of the story."""
    kind, text, args = entry.get("kind"), entry.get("text") or "", entry.get("args") or {}
    if kind not in KINDS or not isinstance(args, dict):
        return None
    actor, target, kind_type = entry.get("player") or "", "", kind
    if kind == "system":
        if not (text.startswith("New day") or text.startswith("Sleeping...")):
            return None
    elif kind == "journal":
        if not args.get("self"):
            return None
        kind_type, text = "self", args["self"]
    elif kind == "say":
        target = ",".join(args.get("heard") or [])  # say reaches only the players who could see the speaker
    elif kind == "give":
        target = args.get("to") or ""
    elif kind == "theft":
        target = args.get("victim") or ""
    elif kind == "notice":
        target = args.get("who") or ""
        kind_type = f"notice:{args['kind']}" if args.get("kind") else kind
    elif kind in ("event", "interrupt") and entry.get("tool"):
        kind_type = f"{kind}:{entry['tool']}"
    return {"actor": actor, "target": target, "type": kind_type, "text": text, "source": actor, "team": ""}


def day_files(log_dir: Path, days: tuple[int, int] | None) -> list[tuple[int, Path]]:
    found = []
    for p in log_dir.glob("day_*.jsonl"):
        m = _DAY_FILE.match(p.name)
        if m and (days is None or days[0] <= int(m.group(1)) <= days[1]):
            found.append((int(m.group(1)), p))
    return sorted(found)


def export(out: Path, days: tuple[int, int] | None = None, media_dir: Path | None = None, fps: int = 60) -> list[dict]:
    takes = load_takes(read_jsonl(out / "video_map.jsonl"))
    edit = out / "edit"
    edit.mkdir(parents=True, exist_ok=True)
    warned: set = set()
    rows = []
    for day, path in day_files(out / "logs", days):
        day_rows = []
        for n, entry in enumerate(read_jsonl(path), 1):
            row = story_row(entry)
            if row is None:
                continue
            row.update(event_id=f"d{day:03}-{n:05}", game_day=str(entry.get("day", day)),
                       game_time=entry.get("gameTime") or "", media_file="", media_timecode="", take="")
            try:
                hit = video_ms_at(takes, parse_ts(entry["ts"]))
            except (KeyError, TypeError, ValueError):
                hit = None
            if hit:
                take, ms = hit
                row["media_timecode"] = timecode(ms, fps)
                row["media_file"] = media_for(take["path"], media_dir, warned)
                row["take"] = Path(take["path"]).stem if take["path"] else f"take{takes.index(take) + 1}"
            day_rows.append(row)
        write_story(edit / f"story_day_{day:03}.md", day, day_rows)
        rows += day_rows
    with open(edit / "markers.csv", "w", encoding="utf-8-sig", newline="") as fh:
        writer = csv.DictWriter(fh, COLUMNS, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)
    return rows


def _cell(row: dict) -> str:
    label = row["type"] + (f" → {row['target']}" if row["target"] else "")
    text = row["text"].replace("|", "\\|").replace("\r", "").replace("\n", "<br>")
    return f"**{label}** {text}"


def write_story(path: Path, day: int, rows: list[dict]) -> None:
    lines = [f"# Day {day}", "", "| video (take @ TC) | game time | P1 | P2 | P3 | world |", "|---|---|---|---|---|---|"]
    for row in rows:
        video = f"{row['take']} @ {row['media_timecode']}" if row["media_timecode"] else ""
        cells = {p: "" for p in (*PLAYERS, "world")}
        world = row["type"] == "system" or row["type"].startswith(("event", "story")) or row["actor"] not in PLAYERS
        cells["world" if world else row["actor"]] = (f"{row['actor']}: " if world and row["actor"] else "") + _cell(row)
        lines.append(f"| {video} | {row['game_time']} | {cells['p1']} | {cells['p2']} | {cells['p3']} | {cells['world']} |")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def parse_days(text: str | None) -> tuple[int, int] | None:
    if not text:
        return None
    a, _, b = text.partition("-")
    return int(a), int(b or a)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Export day logs as Resolve markers and VO story tables.")
    ap.add_argument("--out", required=True, help="run folder holding logs/ and video_map.jsonl")
    ap.add_argument("--days", help="day range like 1-10, or one day")
    ap.add_argument("--media-dir", help="folder with the recordings (default: the folder in video_map.jsonl)")
    ap.add_argument("--fps", type=int, default=60)
    a = ap.parse_args(argv)
    out = Path(a.out)
    if not (out / "video_map.jsonl").is_file():
        print(f"warning: {out / 'video_map.jsonl'} missing; no line gets media", file=sys.stderr)
    rows = export(out, parse_days(a.days), Path(a.media_dir) if a.media_dir else None, a.fps)
    with_media = sum(1 for r in rows if r["media_file"])
    print(f"{len(rows)} markers ({with_media} with media) -> {out / 'edit' / 'markers.csv'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
