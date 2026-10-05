from datetime import date, datetime

from api.kpis import outcome_kpis
from feed.outcomes import DayOutcome, Planned, Snapshot

DAY = date(2026, 10, 6)
MORNING = Snapshot(1, datetime(2026, 10, 6, 6, 0))


def item(ref, outcomes, kind="job", tech="a", region="North", reached="not_started", prereqs=None):
    return Planned(
        kind,
        ref,
        tech,
        tech.upper(),
        datetime(2026, 10, 6, 9),
        outcomes,
        reached,
        prereqs,
        region,
    )


def test_rates_use_the_planned_count_and_the_latest_checkpoint():
    day = DayOutcome(
        DAY,
        "ok",
        MORNING,
        [
            item("1", {"d0": "completed", "d1": "completed", "d2": "completed"}),
            item("2", {"d0": "open", "d1": "completed"}),  # provisional: no d2 yet
            item("3", {"d0": "canceled", "d1": "canceled"}, reached="in_progress", prereqs=True),
            item("4", {"d0": "rescheduled"}, tech="b", region="South"),
            item("T1", {"d0": "completed"}, kind="ticket"),
        ],
    )
    result = outcome_kpis([(day, True)])
    job = result["totals"]["job"]
    assert job["planned"] == 4
    assert job["completed"] == {"d0": 1, "d1": 2, "d2": 1}
    assert job["completion_rate"]["d0"] == 0.25
    assert job["outcome"]["completed"] == 2
    assert job["outcome"]["canceled"] == 1
    assert job["outcome_rate"]["rescheduled"] == 0.25
    assert job["pulled_d0"]["total"] == 2
    assert job["pulled_d0"]["in_progress"] == 1
    assert job["pulled_d0"]["prereqs_open"] == 1
    assert job["pulled_d0_rate"] == 0.5
    assert result["totals"]["ticket"]["completion_rate"]["d0"] == 1
    assert result["days"][0]["provisional"] is True
    assert [r["region"] for r in result["by_region"]] == ["North", "South"]
    assert [t["tech_id"] for t in result["by_tech"]] == ["a", "b"]


def test_no_morning_days_are_listed_but_not_counted():
    empty = DayOutcome(date(2026, 10, 5), "no_morning")
    result = outcome_kpis([(empty, False)])
    assert result["days"] == [
        {"date": date(2026, 10, 5), "status": "no_morning", "provisional": False}
    ]
    assert result["totals"]["job"]["planned"] == 0
    assert result["totals"]["job"]["completion_rate"]["d0"] is None


def test_filters_narrow_to_one_region_or_tech():
    day = DayOutcome(
        DAY,
        "ok",
        MORNING,
        [item("1", {"d0": "completed"}), item("2", {"d0": "open"}, tech="b", region="South")],
    )
    assert outcome_kpis([(day, False)], region="South")["totals"]["job"]["planned"] == 1
    assert outcome_kpis([(day, False)], tech="a")["totals"]["job"]["outcome"]["completed"] == 1
