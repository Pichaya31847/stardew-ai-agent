"""Unit tests for studio/director.py scene-picking logic with fake events."""

from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from director import (  # noqa: E402
    GRID_SCENE,
    MIN_HOLD_SECONDS,
    SceneDirector,
    parse_sse_lines,
    pick_scene,
    scene_for_event,
)


class DirectorScenePickingTests(unittest.TestCase):
    def test_say_focuses_speaker(self) -> None:
        self.assertEqual(pick_scene({"type": "say", "player": "p1", "text": "Good morning!"}), "focus-P1")
        self.assertEqual(pick_scene({"kind": "say", "speaker": "P2", "text": "psst"}), "focus-P2")
        self.assertEqual(pick_scene({"kind": "action", "tool": "say", "player": "p3", "text": "On my way"}), "focus-P3")

    def test_theft_gift_and_caught_slacking_focus_player(self) -> None:
        self.assertEqual(pick_scene({"type": "theft", "player": "p2", "item": "Parsnip"}), "focus-P2")
        self.assertEqual(pick_scene({"type": "gift", "from": "p3", "to": "p1", "item": "Wood"}), "focus-P3")
        self.assertEqual(pick_scene({"kind": "give", "player": "p1", "args": {"to": "p2"}}), "focus-P1")
        self.assertEqual(pick_scene({"type": "caught-slacking", "player": "p3"}), "focus-P3")
        self.assertEqual(pick_scene({"event": "caught_slacking", "slacker": "P1"}), "focus-P1")

    def test_plain_work_and_other_events_use_grid(self) -> None:
        self.assertEqual(pick_scene(None), GRID_SCENE)
        self.assertEqual(pick_scene({"type": "walk_to", "player": "p1"}), GRID_SCENE)
        self.assertEqual(pick_scene({"kind": "action", "tool": "use_tool", "player": "p2"}), GRID_SCENE)
        self.assertEqual(pick_scene({"kind": "think", "player": "p1", "text": "thinking..."}), GRID_SCENE)
        self.assertEqual(pick_scene({"type": "say", "player": "p4", "text": "camera screen"}), GRID_SCENE)
        self.assertEqual(pick_scene({"type": "say", "player": "p1", "ok": False}), GRID_SCENE)

    def test_hold_focus_at_least_three_seconds(self) -> None:
        d = SceneDirector(hold_seconds=3.0)
        self.assertGreaterEqual(d.hold_seconds, MIN_HOLD_SECONDS)

        # t=0.0s: P1 speaks -> focus-P1
        self.assertEqual(pick_scene({"type": "say", "player": "p1"}, now=0.0, director=d), "focus-P1")

        # t=1.0s & t=2.9s: plain work arrives, but focus must be held >= 3s
        self.assertEqual(pick_scene({"type": "walk_to", "player": "p1"}, now=1.0, director=d), "focus-P1")
        self.assertEqual(pick_scene({"type": "use_tool", "player": "p2"}, now=2.9, director=d), "focus-P1")

        # Even if another player triggers an event at t=2.5s (< 3s), current focus is held >= 3s
        self.assertEqual(pick_scene({"type": "say", "player": "p2"}, now=2.5, director=d), "focus-P1")

        # t=3.0s: 3s hold has elapsed; plain work returns to all-three grid
        self.assertEqual(pick_scene({"type": "walk_to", "player": "p1"}, now=3.0, director=d), "all-three")

    def test_same_player_event_extends_focus_hold(self) -> None:
        d = SceneDirector(hold_seconds=3.0)
        self.assertEqual(d.pick_scene({"type": "say", "player": "p2"}, now=10.0), "focus-P2")
        # Another focus event for P2 at t=12.0 extends hold until t=15.0
        self.assertEqual(d.pick_scene({"type": "gift", "player": "p2"}, now=12.0), "focus-P2")
        self.assertEqual(d.pick_scene(None, now=14.5), "focus-P2")
        self.assertEqual(d.pick_scene(None, now=15.0), "all-three")

    def test_switch_to_new_focus_after_hold_expires(self) -> None:
        d = SceneDirector(hold_seconds=3.0)
        self.assertEqual(d.pick_scene({"type": "theft", "player": "p1"}, now=0.0), "focus-P1")
        self.assertEqual(d.pick_scene({"type": "caught-slacking", "player": "p3"}, now=3.1), "focus-P3")
        self.assertEqual(d.pick_scene(None, now=5.5), "focus-P3")
        self.assertEqual(d.pick_scene(None, now=6.1), "all-three")

    def test_parse_sse_lines(self) -> None:
        raw = [
            ": keepalive\n",
            "event: say\n",
            'data: {"player": "p2", "to": "p3", "text": "secret"}\n',
            "\n",
            'data: {"type": "caught-slacking", "player": "p3"}\n',
            "\n",
        ]
        events = list(parse_sse_lines(raw))
        self.assertEqual(len(events), 2)
        self.assertEqual(scene_for_event(events[0]), "focus-P2")
        self.assertEqual(scene_for_event(events[1]), "focus-P3")


if __name__ == "__main__":
    unittest.main(verbosity=2)
