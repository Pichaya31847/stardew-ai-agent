"""runner/check_recording.py: recorded spans, pause/resume lags, overlap (no OBS, no game)."""

import pytest

import check_recording as cr

TAKE = [(0.0, "start", 0), (1.0, "frozen", 0), (2.0, "pause", 0), (5.0, "live", 0), (5.4, "resume", 0),
        (6.0, "frozen", 0), (6.5, "live", 0), (9.0, "stop", 0)]


def test_recorded_spans_follow_pause_and_resume():
    assert cr.recorded_spans(TAKE) == [(0.0, 2.0), (5.4, 9.0)]


def test_lags_pair_each_flip_with_the_next_action_before_another_flip():
    assert cr.lags(TAKE, "frozen", "pause") == [1.0]      # the 6.0 freeze never paused (live again first)
    assert [round(x, 2) for x in cr.lags(TAKE, "live", "resume")] == [0.4]


def test_overlap_sums_over_spans():
    assert cr.overlap([(0.0, 2.0), (5.4, 9.0)], 1.0, 6.0) == pytest.approx(1.6)
