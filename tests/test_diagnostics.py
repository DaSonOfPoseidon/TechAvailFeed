from datetime import date, datetime

from feed.diagnostics import GROUPS, diagnose
from feed.parse import Block

TODAY = date(2026, 10, 6)
TOMORROW = date(2026, 10, 7)
YESTERDAY = date(2026, 10, 5)


def block(
    kind: str,
    day: date = TODAY,
    start: str = "08:00",
    end: str = "17:00",
    tech: str = "a",
    ref: str = "",
    status: str = "A",
    region: str = "",
    skills: str = "",
    address_issue: str | None = None,
) -> Block:
    if kind == "shift":
        region = region or "North"
        skills = skills or "INS"
    return Block(
        kind=kind,
        work_date=day,
        tech_id=tech,
        tech_name=tech.upper() if tech else "",
        starts_at=datetime.fromisoformat(f"{day} {start}"),
        ends_at=datetime.fromisoformat(f"{day} {end}"),
        ref_id=ref,
        status=status,
        department="FIELD",
        region=region,
        skills=skills,
        address_issue=address_issue,
    )


def checks(blocks: list[Block]) -> dict:
    return {c.id: c for c in diagnose(blocks, TODAY)}


def refs(check) -> list:
    return [r["ref_id"] for r in check.rows]


def test_every_check_has_a_known_group_and_a_clean_feed_has_no_rows():
    found = diagnose([block("shift"), block("job", start="09:00", end="10:00", ref="1")], TODAY)
    assert {c.group for c in found} <= set(GROUPS)
    assert [c.id for c in found if c.rows] == []


def test_tech_without_region_or_skills():
    shift = block("shift", tech="b")
    shift.region = ""
    shift.skills = ""
    found = checks([block("shift"), shift])
    assert [r["tech_id"] for r in found["tech_no_region"].rows] == ["b"]
    assert found["tech_no_region"].rows[0]["days"] == 1
    assert [r["tech_id"] for r in found["tech_no_skills"].rows] == ["b"]


def test_tech_region_varies_is_info():
    found = checks([block("shift"), block("shift", TOMORROW, region="South")])
    check = found["tech_region_varies"]
    assert check.severity == "info"
    assert check.rows[0]["regions"] == "North (1), South (1)"


def test_work_without_a_shift_that_day():
    found = checks([block("shift"), block("job", TOMORROW, "09:00", "10:00", ref="1")])
    assert refs(found["work_without_shift"]) == ["1"]


def test_dead_and_past_work_is_not_a_scheduling_conflict():
    found = checks(
        [
            block("job", TOMORROW, "09:00", "10:00", ref="1", status="X"),
            block("ticket", TOMORROW, "09:00", "10:00", ref="2", status="C"),
            block("job", YESTERDAY, "09:00", "10:00", ref="3", status="C"),
        ]
    )
    assert found["work_without_shift"].rows == []


def test_work_running_past_the_shift():
    found = checks([block("shift", end="16:00"), block("job", start="15:00", end="17:00", ref="1")])
    [row] = found["work_outside_shift"].rows
    assert (row["ref_id"], row["minutes_outside"]) == ("1", 60)
    assert row["region"] == "North"  # the tech's shift region


def test_work_during_time_off_is_not_also_reported_as_without_shift():
    found = checks(
        [
            block("time_off", start="00:00", end="23:59"),
            block("job", start="09:00", end="10:00", ref="1"),
        ]
    )
    assert refs(found["work_during_time_off"]) == ["1"]
    assert found["work_without_shift"].rows == []


def test_double_booking_needs_two_different_jobs():
    found = checks(
        [
            block("shift"),
            block("job", start="09:00", end="11:00", ref="1"),
            block("ticket", start="10:00", end="12:00", ref="2"),
            block("job", start="13:00", end="14:00", ref="3"),
            block("job", start="13:00", end="14:00", ref="3"),  # same job listed twice
            block("job", start="09:00", end="11:00", ref="1", tech="b"),  # second tech, same job
            block("shift", tech="b"),
        ]
    )
    [row] = found["double_booked"].rows
    assert (row["ref_id"], row["other_ref_id"], row["overlap_minutes"]) == ("1", "2", 60)


def test_past_work_still_open():
    found = checks(
        [
            block("job", YESTERDAY, ref="1", status="A"),
            block("job", YESTERDAY, ref="2", status="C"),
            block("ticket", YESTERDAY, ref="3", status="R"),
            block("job_unassigned", YESTERDAY, tech="", ref="4", region="North"),
            block("job", TODAY, ref="5"),
        ]
    )
    assert refs(found["past_open_work"]) == ["1", "4"]


def test_unassigned_work_without_region():
    found = checks(
        [
            block("job_unassigned", tech="", ref="1"),
            block("ticket_unassigned", tech="", ref="2", region="South"),
            block("job_unassigned", tech="", ref="3", status="X"),
        ]
    )
    assert refs(found["unassigned_no_region"]) == ["1"]


def test_address_issue_is_unavailable_until_the_feed_sends_it():
    found = checks([block("shift"), block("job", ref="1")])
    assert found["work_address_issue"].available is False


def test_address_issues_on_live_work():
    found = checks(
        [
            block("shift", address_issue=""),
            block("job", start="09:00", end="10:00", ref="1", address_issue="no_gps"),
            block("job", start="10:00", end="11:00", ref="2", address_issue=""),
            block("job_unassigned", tech="", ref="3", region="N", address_issue="no_address"),
            block("job", YESTERDAY, ref="4", status="C", address_issue="no_gps"),
            block("job", ref="5", status="X", address_issue="no_city"),
        ]
    )
    check = found["work_address_issue"]
    assert check.available is True
    assert [(r["ref_id"], r["issue"]) for r in check.rows] == [("3", "no_address"), ("1", "no_gps")]


def test_tc_shift_covers_tc_work_and_tech_rows_name_their_calendar():
    tc_shift = block("shift_tc", tech="t")
    tc_shift.region = ""
    found = checks([tc_shift, block("ticket", start="09:00", end="10:00", tech="t", ref="1")])
    assert found["work_without_shift"].rows == []
    [row] = found["tech_no_region"].rows
    assert (row["tech_id"], row["calendar"]) == ("t", "tc")


def ticket(ref: str, start: str, end: str, region: str, tech: str = "a") -> Block:
    return block("ticket", start=start, end=end, tech=tech, ref=ref, region=region)


def double(blocks: list[Block]) -> list[tuple]:
    rows = checks([block("shift"), *blocks])["double_booked"].rows
    return [(r["ref_id"], r["other_ref_id"], r["reason"]) for r in rows]


def test_two_tickets_in_one_slot_and_region_are_allowed():
    assert (
        double([ticket("1", "10:00", "12:00", "North"), ticket("2", "10:00", "12:00", "North")])
        == []
    )


def test_two_tickets_in_one_slot_but_different_regions_conflict():
    found = double([ticket("1", "10:00", "12:00", "North"), ticket("2", "10:00", "12:00", "South")])
    assert found == [("1", "2", "ticket_regions_differ")]


def test_a_third_ticket_in_the_slot_conflicts():
    found = double(
        [
            ticket("1", "10:00", "12:00", "North"),
            ticket("2", "10:00", "12:00", "North"),
            ticket("3", "11:00", "12:00", "North"),
        ]
    )
    assert {r[2] for r in found} == {"more_than_2_tickets"}


def test_ticket_without_its_own_region_is_not_assumed_to_match():
    # A snapshot from before the feed query sent the ticket's own region.
    found = double([ticket("1", "10:00", "12:00", ""), ticket("2", "10:00", "12:00", "")])
    assert found == [("1", "2", "ticket_region_unknown")]


def test_a_job_overlapping_anything_is_still_a_conflict():
    job = block("job", start="10:00", end="12:00", ref="9")
    assert double([job, ticket("1", "10:00", "12:00", "North")]) == [("1", "9", "job_overlap")]


def test_conflict_rows_show_each_tickets_own_region():
    rows = checks(
        [
            block("shift"),
            ticket("1", "10:00", "12:00", "North"),
            ticket("2", "10:00", "12:00", "South"),
        ]
    )["double_booked"].rows
    assert [(r["region"], r["other_region"]) for r in rows] == [("North", "South")]


def test_a_job_keeps_its_own_region_and_borrows_the_shifts_only_without_one():
    own = block("job", start="15:00", end="18:00", ref="1", region="South")
    bare = block("job", start="16:00", end="18:00", ref="2", tech="b")
    rows = checks([block("shift", end="16:00"), block("shift", tech="b", end="16:00"), own, bare])
    regions = {r["ref_id"]: r["region"] for r in rows["work_outside_shift"].rows}
    assert regions == {"1": "South", "2": "North"}


def test_a_ride_along_without_a_schedule_is_info_not_a_conflict():
    found = checks(
        [
            block("shift", tech="lead"),
            block("job", start="09:00", end="10:00", tech="lead", ref="1"),
            block("job", start="09:00", end="10:00", tech="trainee", ref="1"),
            block("job", start="11:00", end="12:00", tech="solo", ref="2"),
        ]
    )
    assert refs(found["work_without_shift"]) == ["2"]
    [row] = found["ride_along"].rows
    assert (row["tech_id"], row["ref_id"], row["with"]) == ("trainee", "1", "LEAD")
    assert found["ride_along"].severity == "info"
