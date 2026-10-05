from dataclasses import replace
from datetime import date, datetime

from feed.parse import Block
from feed.store import diff_blocks


def block(ref: str, status: str = "A") -> Block:
    return Block(
        kind="job",
        work_date=date(2026, 10, 6),
        tech_id="t1",
        tech_name="T1",
        starts_at=datetime(2026, 10, 6, 9),
        ends_at=datetime(2026, 10, 6, 11),
        ref_id=ref,
        status=status,
        department="FIELD",
        region="North",
        skills="",
    )


def test_unchanged_snapshot_writes_nothing():
    assert diff_blocks([(1, block("a")), (2, block("b"))], [block("b"), block("a")]) == ([], [])


def test_changed_field_closes_old_row_and_inserts_new():
    done = block("a", status="C")
    assert diff_blocks([(1, block("a")), (2, block("b"))], [done, block("b")]) == ([done], [1])


def test_removed_and_added_rows():
    assert diff_blocks([(1, block("a"))], [block("b")]) == ([block("b")], [1])


def test_first_snapshot_inserts_everything():
    assert diff_blocks([], [block("a"), block("b")]) == ([block("a"), block("b")], [])


def test_duplicate_rows_count_as_copies():
    a = block("a")
    assert diff_blocks([(1, a)], [a, a]) == ([a], [])
    assert diff_blocks([(1, a), (2, a)], [a]) == ([], [2])


def test_none_and_default_differ():
    # A row read back raw must match exactly, so a NULL never silently equals "".
    stored = replace(block("a"), department=None)
    assert diff_blocks([(1, stored)], [block("a")]) == ([block("a")], [1])
