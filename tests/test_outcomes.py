from datetime import date, datetime

from feed.outcomes import (
    Snapshot,
    classify,
    day_outcome,
    find_checkpoint,
    find_morning,
    prereqs_open,
    reached,
    summarise,
)
from feed.parse import Block

DAY = date(2026, 10, 6)


def snap(id: int, when: str) -> Snapshot:
    return Snapshot(id, datetime.fromisoformat(when))


def work(
    ref: str,
    status: str = "A",
    start: str = "2026-10-06 09:00",
    kind: str = "job",
    tech: str = "t1",
    department: str = "FIELD",
    traced: bool = False,
    enroute: str | None = None,
    inprogress: str | None = None,
    prebury: str = "",
) -> Block:
    starts_at = datetime.fromisoformat(start)
    return Block(
        kind=kind,
        work_date=starts_at.date(),
        tech_id=tech,
        tech_name=tech.upper(),
        starts_at=starts_at,
        ends_at=starts_at,
        ref_id=ref,
        status=status,
        department=department,
        region="",
        skills="",
        modified_at=starts_at if traced else None,
        enroute_at=datetime.fromisoformat(enroute) if enroute else None,
        inprogress_at=datetime.fromisoformat(inprogress) if inprogress else None,
        prereqs_status=prebury,
    )


def test_morning_is_the_first_snapshot_between_six_and_seven():
    snaps = [
        snap(1, "2026-10-06 05:45"),
        snap(2, "2026-10-06 06:15"),
        snap(3, "2026-10-06 06:30"),
        snap(4, "2026-10-06 07:00"),
    ]
    assert find_morning(snaps, DAY).id == 2


def test_no_snapshot_in_the_morning_window_is_not_guessed():
    snaps = [snap(1, "2026-10-06 05:45"), snap(2, "2026-10-06 07:15")]
    assert find_morning(snaps, DAY) is None
    assert day_outcome(DAY, None, [], {}).status == "no_morning"


def test_checkpoints_are_the_last_snapshot_before_each_midnight():
    morning = snap(1, "2026-10-06 06:00")
    snaps = [
        morning,
        snap(2, "2026-10-06 23:45"),
        snap(3, "2026-10-07 00:00"),
        snap(4, "2026-10-07 23:45"),
    ]
    assert find_checkpoint(snaps, morning, DAY, 0).id == 2
    assert find_checkpoint(snaps, morning, DAY, 1).id == 4
    # d2 hasn't ended yet: the latest snapshot is the outcome so far.
    assert find_checkpoint(snaps, morning, DAY, 2).id == 4


def test_classification_order():
    assert classify("job", DAY, None) == "missing"
    assert classify("job", DAY, work("1", "C")) == "completed"
    assert classify("ticket", DAY, work("1", "R", kind="ticket")) == "completed"
    assert classify("job", DAY, work("1", "X")) == "canceled"
    assert classify("ticket", DAY, work("1", "D", kind="ticket")) == "canceled"
    assert classify("job", DAY, work("1", department="FLDSVCCON")) == "handed_off"
    moved = work("1", start="9999-12-31 00:00", kind="job_moved", tech="")
    assert classify("job", DAY, moved) == "unscheduled"
    assert classify("job", DAY, work("1", start="2026-11-15 09:00")) == "rescheduled"
    assert classify("job", DAY, work("1", tech="")) == "unassigned"
    assert classify("job", DAY, work("1", "I")) == "open"


def test_a_completed_job_beats_a_moved_date():
    assert classify("job", DAY, work("1", "C", start="2026-10-07 09:00")) == "completed"


def test_first_release_rows_without_department_are_not_handed_off():
    assert classify("job", DAY, work("1", department="")) == "open"


def test_day_outcome_counts_each_job_once_and_tracks_checkpoints():
    morning = [
        work("1", tech="a"),
        work("1", tech="b"),  # two techs on one job
        work("2"),
        work("3", "X"),  # already canceled at 06:00: not planned
        work("4", start="2026-10-07 09:00"),  # tomorrow's job
        work("T1", kind="ticket"),
    ]
    d0 = [
        work("1", "C", tech="a"),
        work("1", "C", tech="b"),
        work("2", "I"),
        work("5"),  # booked after the morning
        work("T1", "C", kind="ticket"),
    ]
    d1 = [work("1", "C"), work("2", "C"), work("T1", "C", kind="ticket")]
    d2 = d1
    outcome = day_outcome(DAY, snap(1, "2026-10-06 06:00"), morning, {"d0": d0, "d1": d1, "d2": d2})
    summary = summarise(outcome)
    assert summary["job"]["planned"] == 2
    assert summary["job"]["completed_d0"] == 1
    assert summary["job"]["completed_d1"] == 2
    assert summary["job"]["completed_d2"] == 2
    assert summary["job"]["added_after_morning"] == 1
    assert summary["ticket"]["planned"] == 1
    assert summary["ticket"]["completed_d0"] == 1


def test_job_that_leaves_the_feed_is_missing_and_moved_rows_are_found():
    morning = [work("1"), work("2")]
    d0 = [work("2", start="9999-12-31 00:00", kind="job_moved", tech="")]
    outcome = day_outcome(DAY, snap(1, "2026-10-06 06:00"), morning, {"d0": d0, "d1": None})
    by_ref = {i.ref_id: i.outcomes for i in outcome.items}
    assert by_ref["1"] == {"d0": "missing"}
    assert by_ref["2"] == {"d0": "unscheduled"}


def test_only_install_types_are_planned_but_untyped_rows_count():
    install = work("1")
    install.task_type = "3"
    other = work("2")
    other.task_type = "14"  # Drop/Bury: blocks time, not part of the history
    untyped = work("3")  # from a feed version without task_type
    outcome = day_outcome(DAY, snap(1, "2026-10-06 06:00"), [install, other, untyped], {})
    assert sorted(i.ref_id for i in outcome.items) == ["1", "3"]


def test_reached_is_the_furthest_stage_on_the_plan_day():
    assert reached(DAY, None) == "unknown"
    assert reached(DAY, work("1")) == "unknown"  # snapshot from before the trace columns
    assert reached(DAY, work("1", traced=True)) == "not_started"
    assert reached(DAY, work("1", traced=True, enroute="2026-10-06 08:10")) == "en_route"
    both = work("1", traced=True, enroute="2026-10-06 08:10", inprogress="2026-10-06 09:02")
    assert reached(DAY, both) == "in_progress"
    # In Progress on an earlier attempt says nothing about this day.
    assert reached(DAY, work("1", traced=True, inprogress="2026-10-05 09:00")) == "not_started"


def test_prereqs_are_open_until_completed_or_dropped():
    assert prereqs_open(work("1")) is None
    assert prereqs_open(work("1", traced=True)) is False
    assert prereqs_open(work("1", traced=True, prebury="PreBury - Connectorized: A")) is True
    assert prereqs_open(work("1", traced=True, prebury="PreBury - Legacy: W")) is True
    assert prereqs_open(work("1", traced=True, prebury="PreBury - Legacy: C")) is False
    assert prereqs_open(work("1", traced=True, prebury="PreBury - Legacy: X")) is False
    # Legacy orders: a finished pre-drop doesn't hide an open pre-bury.
    both = "PreDrop - Legacy: C, PreBury - Legacy: A"
    assert prereqs_open(work("1", traced=True, prebury=both)) is True


def test_pulled_jobs_are_split_by_how_far_the_tech_got():
    morning = [work("1"), work("2"), work("3"), work("4"), work("5")]
    sentinel = "9999-12-31 00:00"
    d0 = [
        work("1", "C", traced=True, inprogress="2026-10-06 09:00"),
        work("2", "U", traced=True, inprogress="2026-10-06 09:30"),
        work("3", start=sentinel, kind="job_moved", traced=True, enroute="2026-10-06 08:00"),
        work("4", start=sentinel, kind="job_moved", traced=True, prebury="PreDrop - Legacy: A"),
        work("5", start="2026-10-09 09:00"),
    ]
    outcome = day_outcome(DAY, snap(1, "2026-10-06 06:00"), morning, {"d0": d0})
    assert summarise(outcome)["job"]["pulled_d0"] == {
        "total": 4,
        "in_progress": 1,
        "en_route": 1,
        "not_started": 1,
        "unknown": 1,
        "prereqs_open": 1,
    }


def test_planned_job_that_loses_its_tech_is_unassigned():
    morning = [work("1")]
    d0 = [work("1", kind="job_unassigned", tech="")]
    outcome = day_outcome(DAY, snap(1, "2026-10-06 06:00"), morning, {"d0": d0})
    assert outcome.items[0].outcomes == {"d0": "unassigned"}


def test_planned_work_takes_the_techs_shift_region_and_its_task_type():
    job = work("1", tech="a")
    job.task_type = "3"
    unassigned_region = work("T1", kind="ticket", tech="b")
    unassigned_region.region = "South"  # a row that carries its own region keeps it
    outcome = day_outcome(
        DAY,
        snap(1, "2026-10-06 06:00"),
        [job, unassigned_region],
        {},
        regions={"a": "North", "b": "North"},
    )
    by_ref = {i.ref_id: (i.region, i.task_type) for i in outcome.items}
    assert by_ref == {"1": ("North", "3"), "T1": ("South", "")}
