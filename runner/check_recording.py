"""Check a pause/resume recording against the game log: how late OBS paused and resumed, and which says, thoughts and
action results made it into the video and for how long.

    py runner/check_recording.py --out runs-test              # last take (OBS file) in runs-test/video_map.jsonl
    py runner/check_recording.py --out runs-test --video "C:/Users/me/Videos/x.mkv"   # also lists still stretches

Needs the recorder's `frozen` / `live` lines in video_map.jsonl (obs_recorder.py writes them since 2026-10-19).
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import subprocess
from datetime import datetime
from pathlib import Path

EVENT_KINDS = ("think", "emote", "result")  # a say shows once the world runs (the mod queues it)
SHOWN_SECONDS = 5.0  # how long a chat line / bubble stays up (mod ChatSeconds)


def parse_ts(ts: str) -> float:
    return datetime.fromisoformat(ts).timestamp()


def read_jsonl(path: Path) -> list[dict]:
    rows = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return rows


def takes(rows: list[dict]) -> list[list[tuple[float, str, int]]]:
    """Anchors (ts, ev, video_ms) per take, start to stop (or to the end of the file)."""
    out: list[list[tuple[float, str, int]]] = []
    for r in rows:
        if r.get("ev") == "start" or not out:
            out.append([])
        out[-1].append((parse_ts(r["ts"]), r["ev"], int(r.get("video_ms") or 0)))
    return out


def recorded_spans(take) -> list[tuple[float, float]]:
    """Wall-clock spans during which OBS was recording."""
    spans, since = [], None
    for ts, ev, _ in take:
        if ev in ("start", "resume") and since is None:
            since = ts
        elif ev in ("pause", "stop") and since is not None:
            spans.append((since, ts))
            since = None
    if since is not None:
        spans.append((since, take[-1][0]))
    return spans


def lags(take, flip: str, act: str) -> list[float]:
    """Seconds from each `flip` (frozen / live) to the recorder's next `act` (pause / resume), if it came before the next flip."""
    out, pending = [], None
    for ts, ev, _ in take:
        if ev == flip:
            pending = ts
        elif ev in ("frozen", "live"):
            pending = None
        elif ev == act and pending is not None:
            out.append(ts - pending)
            pending = None
    return out


def overlap(spans, a: float, b: float) -> float:
    return sum(max(0.0, min(b, e) - max(a, s)) for s, e in spans)


def event_rows(log_rows: list[dict], t0: float, t1: float) -> list[dict]:
    out = []
    for e in log_rows:
        if e.get("kind") in EVENT_KINDS and e.get("player") and "ts" in e:
            ts = parse_ts(e["ts"])
            if t0 <= ts <= t1:
                out.append({**e, "_ts": ts})
    return out


def stills(video: str, min_s: float) -> list[tuple[float, float]]:
    """(start, duration) of still stretches in the video, by ffmpeg freezedetect on a downscaled copy."""
    cmd = ["ffmpeg", "-hide_banner", "-nostats", "-i", video, "-vf", f"scale=960:-1,freezedetect=n=0.001:d={min_s}",
           "-map", "0:v", "-f", "null", "-"]
    err = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace").stderr
    starts = [float(x) for x in re.findall(r"freeze_start: ([\d.]+)", err)]
    durs = [float(x) for x in re.findall(r"freeze_duration: ([\d.]+)", err)]
    return list(zip(starts, durs))


def fmt(xs: list[float]) -> str:
    if not xs:
        return "none"
    xs = sorted(xs)
    return f"n={len(xs)} median {statistics.median(xs):.2f}s p90 {xs[min(len(xs) - 1, int(len(xs) * 0.9))]:.2f}s max {xs[-1]:.2f}s"


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, help="Run folder with video_map.jsonl and logs/")
    ap.add_argument("--take", type=int, default=-1, help="Which take (one OBS file each), 0 = first, -1 = last")
    ap.add_argument("--video", help="Recording file: also list still stretches (ffmpeg freezedetect)")
    ap.add_argument("--min-still", type=float, default=0.5, help="Shortest still stretch to list (seconds)")
    args = ap.parse_args(argv)

    out = Path(args.out)
    all_takes = takes(read_jsonl(out / "video_map.jsonl"))
    if not all_takes:
        print("no take in video_map.jsonl")
        return 1
    take = all_takes[args.take]
    t0, t1 = take[0][0], take[-1][0]
    spans = recorded_spans(take)
    rec = sum(e - s for s, e in spans)
    print(f"take {datetime.fromtimestamp(t0):%H:%M:%S}-{datetime.fromtimestamp(t1):%H:%M:%S}: "
          f"{(t1 - t0) / 60:.1f} min real, {rec / 60:.1f} min recorded, {sum(1 for _, ev, _ in take if ev == 'pause')} pauses")
    if not any(ev in ("frozen", "live") for _, ev, _ in take):
        print("no frozen/live lines: lags unknown (recorder older than 2026-10-19)")
    else:
        print(f"resume lag (game live -> OBS resume): {fmt(lags(take, 'live', 'resume'))}")
        print(f"pause lag  (game frozen -> OBS pause): {fmt(lags(take, 'frozen', 'pause'))}  (includes --min-pause on purpose)")

    # day_NNN*.jsonl: the watchdog renames a replayed day's log to day_NNN_attempt*
    log_rows = [r for f in sorted((out / "logs").glob("day_[0-9][0-9][0-9]*.jsonl")) for r in read_jsonl(f)]
    events = event_rows(log_rows, t0, t1)
    missed = []
    by_kind: dict[str, list[float]] = {}
    for e in events:
        seen = overlap(spans, e["_ts"], e["_ts"] + SHOWN_SECONDS)
        by_kind.setdefault(e["kind"], []).append(seen)
        if seen < 0.5 and e["kind"] in ("emote", "result"):
            missed.append((e, seen))
    for kind, seen in sorted(by_kind.items()):
        print(f"{kind:7} in video for its first {SHOWN_SECONDS:.0f}s: {sum(1 for s in seen if s >= 0.5)}/{len(seen)} visible, {fmt(seen)}")
    opened = [ts for ts, ev, _ in take if ev == "menu"]
    if opened:
        seen = [overlap(spans, ts, ts + 2.5) for ts in opened]
        print(f"menu    in video for its first 2.5s: {sum(1 for s in seen if s >= 2.0)}/{len(seen)} shown 2s+, {fmt(seen)}")
    for e, seen in missed[:15]:
        text = (e.get("text") or "").replace("\n", " ")[:70]
        print(f"  missed {datetime.fromtimestamp(e['_ts']):%H:%M:%S} {e['player']} {e['kind']} {e.get('tool') or ''}: {text} ({seen:.1f}s)")

    if args.video:
        found = stills(args.video, args.min_still)
        print(f"still stretches >= {args.min_still}s: {len(found)}, total {sum(d for _, d in found):.1f}s")
        for s, d in sorted(found, key=lambda x: -x[1])[:10]:
            print(f"  {int(s // 60)}:{s % 60:05.2f} for {d:.2f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
