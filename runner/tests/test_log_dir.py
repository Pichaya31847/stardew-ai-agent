"""run_days refuses to start when the mod logs somewhere other than <out>/logs."""

from __future__ import annotations

import pytest

import run_days


def test_matching_log_dir_passes(tmp_path):
    logs = tmp_path / "runs-ep01" / "logs"
    logs.mkdir(parents=True)
    run_days.check_log_dir({"ready": True, "logDir": str(logs).replace("\\", "/").upper()}, logs)  # case and slashes differ on Windows


def test_other_log_dir_stops_with_the_config_line(tmp_path):
    logs = tmp_path / "runs-rehearsal" / "logs"
    with pytest.raises(SystemExit) as exc:
        run_days.check_log_dir({"ready": True, "logDir": str(tmp_path / "runs-warmup3" / "logs")}, logs)
    msg = str(exc.value)
    assert "runs-warmup3" in msg and f'"LogDir": "{logs.as_posix()}"' in msg


@pytest.mark.parametrize("state", [{"ready": True}, {"ready": False}, {}, None])
def test_older_mod_or_no_game_is_let_through(tmp_path, state):
    run_days.check_log_dir(state, tmp_path / "logs")


def test_main_stops_before_any_session(monkeypatch, tmp_path):
    out = tmp_path / "out"
    monkeypatch.setattr(run_days, "game_state", lambda: {"ready": True, "day": 1, "logDir": str(tmp_path / "elsewhere")})
    monkeypatch.setattr(run_days, "run_session", lambda *a, **k: pytest.fail("a session started"))
    monkeypatch.setattr(run_days.sys, "argv", ["run_days.py", "--out", str(out), "--skip-smoke", "--no-watchdog", "--days", "1"])
    with pytest.raises(SystemExit, match="The mod logs to"):
        run_days.main()


def test_multi_seat_refuses_a_shared_wallet():
    with pytest.raises(SystemExit, match="separate_wallets"):
        run_days.check_wallets({"ready": True, "separateWallets": False}, is_multi=True)
    run_days.check_wallets({"ready": True, "separateWallets": False}, is_multi=False)  # one AI: nothing to share
    run_days.check_wallets({"ready": True, "separateWallets": True}, is_multi=True)
    run_days.check_wallets({"ready": True}, is_multi=True)  # older mod build
