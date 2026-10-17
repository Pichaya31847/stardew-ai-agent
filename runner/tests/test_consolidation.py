"""memory.py batch-end consolidation with a fake session: file rotation, bad replies, digests."""

from __future__ import annotations

from pathlib import Path

import memory


class FakeSession:
    """Plays the model: returns the queued replies (the last one repeats) and records every call."""

    def __init__(self, *replies):
        self.replies = list(replies)
        self.calls: list[tuple[str, str]] = []

    def __call__(self, system: str, prompt: str) -> str:
        self.calls.append((system, prompt))
        reply = self.replies[min(len(self.calls), len(self.replies)) - 1]
        if isinstance(reply, Exception):
            raise reply
        return reply


def write(path: Path, text: str) -> Path:
    path.write_text(text, encoding="utf-8")
    return path


def lessons(n: int) -> str:
    return "\n".join(f"- Day {i}: lesson number {i}" for i in range(1, n + 1)) + "\n"


def bullets(n: int) -> str:
    return "\n".join(f"- merged lesson {i}" for i in range(1, n + 1))


# ---------------------------------------------------------------------- lessons


def test_lessons_are_rewritten_and_the_old_file_is_kept(tmp_path):
    original = write(tmp_path / "lessons.md", lessons(30)).read_text(encoding="utf-8")
    session = FakeSession("Here is the short list:\n```\n" + bullets(12) + "\n```\nHope it helps.")
    r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, memory.LESSONS_CAP, session)

    assert (r.status, r.before, r.after, r.backup) == ("consolidated", 30, 12, "lessons_before_batch1.md")
    assert (tmp_path / "lessons_before_batch1.md").read_text(encoding="utf-8") == original
    now = (tmp_path / "lessons.md").read_text(encoding="utf-8")
    assert now == bullets(12) + "\n"                      # only the list lines: preface, fences and sign-off are dropped
    assert len(session.calls) == 1
    system, _ = session.calls[0]
    assert "lesson number 30" in system and "at most 40 lines" in system  # the lines and the cap are in the prompt
    assert not list(tmp_path.glob("*.tmp"))


def test_each_batch_gets_the_next_backup_number(tmp_path):
    write(tmp_path / "lessons.md", lessons(5))
    for expected in (1, 2, 3):
        r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, FakeSession(bullets(3)))
        assert r.backup == f"lessons_before_batch{expected}.md"
        write(tmp_path / "lessons.md", (tmp_path / "lessons.md").read_text(encoding="utf-8") + f"- Day {expected + 10}: new\n")
    assert sorted(p.name for p in tmp_path.glob("lessons_before_batch*.md")) == [
        "lessons_before_batch1.md", "lessons_before_batch2.md", "lessons_before_batch3.md"]
    # batch 2's backup is what lessons.md held after batch 1 plus the line added since
    assert "merged lesson 1" in (tmp_path / "lessons_before_batch2.md").read_text(encoding="utf-8")
    assert "- Day 11: new" in (tmp_path / "lessons_before_batch2.md").read_text(encoding="utf-8")


def test_other_seats_keep_their_own_files_and_numbers(tmp_path):
    write(tmp_path / "lessons.md", lessons(4))
    write(tmp_path / "lessons_p2.md", lessons(6))
    memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, FakeSession("- p1 short list"))
    r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, FakeSession("- p2 short list"), player="p2")
    assert r.backup == "lessons_before_batch1_p2.md"                 # p1's batch 1 does not count for p2
    assert (tmp_path / "lessons_p2.md").read_text(encoding="utf-8") == "- p2 short list\n"
    assert (tmp_path / "lessons.md").read_text(encoding="utf-8") == "- p1 short list\n"
    assert "lesson number 6" in (tmp_path / "lessons_before_batch1_p2.md").read_text(encoding="utf-8")
    assert "lesson number 6" not in (tmp_path / "lessons_before_batch1.md").read_text(encoding="utf-8")
    assert memory.next_batch(tmp_path, "lessons") == 2
    assert memory.next_batch(tmp_path, "lessons", "p2") == 2
    assert memory.next_batch(tmp_path, "lessons", "p3") == 1


def test_a_reply_over_the_cap_is_asked_again_once(tmp_path):
    write(tmp_path / "lessons.md", lessons(50))
    session = FakeSession(bullets(45), bullets(35))
    r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, session)
    assert r.status == "consolidated" and r.after == 35 and len(session.calls) == 2
    assert "45 lines" in session.calls[1][1] and "limit is 40" in session.calls[1][1]


def test_a_bad_reply_twice_keeps_the_file_untouched(tmp_path):
    original = write(tmp_path / "lessons.md", lessons(50)).read_text(encoding="utf-8")
    for replies in ((bullets(45), bullets(44)), ("I cannot do that.", "Sorry, no.")):
        session = FakeSession(*replies)
        r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, session)
        assert r.status == "kept" and len(session.calls) == 2
        assert (tmp_path / "lessons.md").read_text(encoding="utf-8") == original
        assert not list(tmp_path.glob("lessons_before_batch*"))     # no backup for a change that did not happen


def test_a_failing_session_keeps_the_file_and_does_not_raise(tmp_path):
    original = write(tmp_path / "lessons.md", lessons(5)).read_text(encoding="utf-8")
    r = memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, FakeSession(RuntimeError("claude exited 1")))
    assert r.status == "error" and "claude exited 1" in r.detail
    assert (tmp_path / "lessons.md").read_text(encoding="utf-8") == original


def test_nothing_to_consolidate_makes_no_session_call(tmp_path):
    session = FakeSession(bullets(1))
    assert memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, session).status == "empty"
    write(tmp_path / "lessons.md", "\n\n")
    assert memory.consolidate_list(tmp_path, "lessons", memory.LESSONS_TASK, 40, session).status == "empty"
    assert session.calls == []


def test_parse_list_accepts_bullets_and_numbers():
    reply = "Sure!\n* star\n• dot\n- dash\n1. one\n2) two\n2.5 days is not a list line\n  - indented\n-no space\n"
    assert memory.parse_list(reply) == ["- star", "- dot", "- dash", "- one", "- two", "- indented"]


# ---------------------------------------------------------------------- notes


def test_notes_are_consolidated_like_lessons_with_their_own_cap(tmp_path):
    write(tmp_path / "notes.md", "\n".join(f"- [shops, day {i}] fact {i}" for i in range(1, 11)))
    session = FakeSession("- [shops, day 1] merged fact")
    r = memory.consolidate_list(tmp_path, "notes", memory.NOTES_TASK, memory.NOTES_CAP, session)
    assert (r.status, r.backup) == ("consolidated", "notes_before_batch1.md")
    assert "at most 60 lines" in session.calls[0][0] and "fact 10" in session.calls[0][0]
    assert (tmp_path / "notes.md").read_text(encoding="utf-8") == "- [shops, day 1] merged fact\n"


# ---------------------------------------------------------------------- weekly digests


def test_digest_weeks_need_all_seven_days_over_and_a_diary(tmp_path):
    for d in (1, 2, 9):
        write(memory.diary_path(tmp_path, d), f"diary {d}")
    assert memory.missing_digest_weeks(tmp_path, 8) == [1]       # day 8 now: days 1-7 are over
    assert memory.missing_digest_weeks(tmp_path, 7) == []        # day 7 is still being played
    assert memory.missing_digest_weeks(tmp_path, 16) == [1, 2]
    assert memory.missing_digest_weeks(tmp_path, 30) == [1, 2]   # weeks 3 and 4 have no diaries
    write(memory.digest_path(tmp_path, 1), "done already")
    assert memory.missing_digest_weeks(tmp_path, 16) == [2]


def test_digest_is_written_from_the_weeks_diaries(tmp_path):
    for d in (1, 2, 3, 8):
        write(memory.diary_path(tmp_path, d), f"# Day {d}\n\nDIARY-OF-DAY-{d}")
    session = FakeSession("```\nWeek one: planted parsnips and ran out of water.\n```")
    r = memory.write_digest(tmp_path, 1, session)
    assert r.status == "consolidated"
    assert memory.digest_path(tmp_path, 1).read_text(encoding="utf-8") == "Week one: planted parsnips and ran out of water.\n"
    system = session.calls[0][0]
    assert all(f"DIARY-OF-DAY-{d}" in system for d in (1, 2, 3)) and "DIARY-OF-DAY-8" not in system
    assert "days 1-7" in system and "150 words" in system


def test_digest_too_long_is_asked_again_then_skipped(tmp_path):
    write(memory.diary_path(tmp_path, 1), "d")
    long = "word " * 400
    session = FakeSession(long, "short digest")
    assert memory.write_digest(tmp_path, 1, session).status == "consolidated" and len(session.calls) == 2
    assert memory.digest_path(tmp_path, 1).read_text(encoding="utf-8") == "short digest\n"

    write(memory.diary_path(tmp_path, 8), "d")
    r = memory.write_digest(tmp_path, 2, FakeSession(long))
    assert r.status == "kept" and not memory.digest_path(tmp_path, 2).exists()


# ---------------------------------------------------------------------- the whole batch end


def test_consolidate_batch_runs_every_job_once(tmp_path):
    write(tmp_path / "lessons.md", lessons(20))
    write(tmp_path / "notes.md", "- [places, day 1] Willy's shop is at the Beach\n- [places, day 2] Willy's shop is at the Beach (30,33)\n")
    for d in (1, 2, 3, 4, 5, 6, 7, 8):
        write(memory.diary_path(tmp_path, d), f"diary {d}")
    session = FakeSession(bullets(5), "- [places, day 2] Willy's shop is at the Beach (30,33)", "Week one digest.")
    results = memory.consolidate_batch(tmp_path, 9, session)

    assert [(r.name, r.status) for r in results] == [
        ("lessons", "consolidated"), ("notes", "consolidated"), ("digest_week_001", "consolidated")]
    assert len(session.calls) == 3
    assert (tmp_path / "lessons_before_batch1.md").exists() and (tmp_path / "notes_before_batch1.md").exists()
    assert (tmp_path / "digest_week_001.md").exists()
    assert all(r.describe() for r in results)

    # the next day's prompt uses the shorter files and the digest
    out = memory.inject("{LESSONS}\n{NOTES}\n{MEMORY}", tmp_path, "g", "RECENT")
    assert "merged lesson 5" in out and "lesson number 20" not in out
    assert "Week one digest." in out and "RECENT" in out

    # a second batch end with nothing new only re-tidies the lists: the digest is not written twice
    again = memory.consolidate_batch(tmp_path, 9, FakeSession(bullets(2), "- [places, day 2] x"))
    assert [r.name for r in again] == ["lessons", "notes"]
    assert (tmp_path / "lessons_before_batch2.md").exists()


def test_consolidate_batch_survives_a_dead_session(tmp_path):
    write(tmp_path / "lessons.md", lessons(3))
    write(memory.diary_path(tmp_path, 1), "d")
    results = memory.consolidate_batch(tmp_path, 9, FakeSession(RuntimeError("no claude")))
    assert [r.status for r in results] == ["error", "empty", "error"]
    assert (tmp_path / "lessons.md").read_text(encoding="utf-8") == lessons(3)
