from datetime import date, datetime

from api.capacity import calendar_entries, filter_days, region_totals, unassigned_work
from feed.availability import tech_days
from feed.parse import Block

MON = date(2026, 10, 5)
TUE = date(2026, 10, 6)
EARLY = datetime(2026, 10, 1, 7, 0)


def block(
    kind: str,
    day: date,
    start: str,
    end: str,
    tech: str = "a",
    region: str = "",
    skills: str = "",
    status: str = "A",
    ref: str = "",
) -> Block:
    return Block(
        kind=kind,
        work_date=day,
        tech_id=tech,
        tech_name=tech.upper(),
        starts_at=datetime.fromisoformat(f"{day} {start}"),
        ends_at=datetime.fromisoformat(f"{day} {end}"),
        ref_id=ref,
        status=status,
        department="FIELD",
        region=region,
        skills=skills,
    )


BLOCKS = [
    block("shift", MON, "08:00", "17:00", "a", "North", "INS, RECO"),
    block("shift", MON, "08:00", "17:00", "b", "South", "INS"),
    block("job", MON, "08:00", "10:00", "a", ref="j1"),
    block("ticket", MON, "13:00", "14:00", "b", ref="t1"),
    block("time_off", TUE, "00:00", "23:59", "a"),
    block("job_unassigned", MON, "09:00", "11:00", "", "North", ref="u1"),
    block("ticket_unassigned", MON, "09:00", "10:00", "", "South", ref="u2"),
    block("job_unassigned", MON, "09:00", "11:00", "", "North", status="X", ref="u3"),
]


def build(region=None, skill=None):
    days = filter_days(tech_days(BLOCKS, EARLY, start=MON, end=TUE), region, skill)
    demand = unassigned_work(BLOCKS, MON, TUE, region)
    return calendar_entries(days, demand, MON, TUE)


def test_calendar_totals_and_regions():
    monday, tuesday = build()
    totals = monday["totals"]
    assert (totals["techs_on"], totals["shift_h"], totals["available_h"]) == (2, 18, 16)
    assert (totals["booked_h"], totals["jobs"], totals["tickets"]) == (3, 1, 1)
    assert totals["free_h"] == 13
    assert (totals["unassigned_jobs"], totals["unassigned_tickets"]) == (1, 1)
    assert totals["unassigned_h"] == 3
    assert totals["net_h"] == 10
    assert totals["utilization"] == round(3 / 16, 3)
    assert [r["region"] for r in monday["by_region"]] == ["North", "South"]
    assert monday["by_region"][0]["net_h"] == 6 - 2  # 10-12 and 13-17, minus u1
    # Tech a is off all of Tuesday; their usual region still gets the head count.
    assert tuesday["totals"]["techs_off"] == 1
    assert tuesday["totals"]["utilization"] is None
    assert tuesday["by_region"][0]["region"] == "North"


def test_every_date_in_range_gets_an_entry():
    entries = calendar_entries([], [], MON, date(2026, 10, 9))
    assert len(entries) == 5
    assert entries[-1]["totals"]["techs_on"] == 0


def test_region_filter_applies_to_techs_and_demand():
    monday, _ = build(region="South")
    assert monday["totals"]["techs_on"] == 1
    assert monday["totals"]["unassigned_jobs"] == 0
    assert monday["totals"]["unassigned_tickets"] == 1


def test_skill_filter_matches_whole_codes():
    monday, _ = build(skill="reco")
    assert monday["totals"]["techs_on"] == 1
    assert build(skill="REC")[0]["totals"]["techs_on"] == 0


def test_region_totals_sum_the_range():
    north = region_totals(build())[0]
    assert north["region"] == "North"
    assert north["tech_days_off"] == 1
    assert "techs_on" not in north
    assert north["unassigned_h"] == 2


def test_unassigned_tc_work_is_tc_demand():
    tc_ticket = block("ticket_unassigned", MON, "09:00", "10:00", "", "North", ref="u9")
    tc_ticket.department = "TC"
    blocks = BLOCKS + [tc_ticket]
    assert "u9" not in [b.ref_id for b in unassigned_work(blocks, MON, TUE)]
    assert [b.ref_id for b in unassigned_work(blocks, MON, TUE, calendar="tc")] == ["u9"]
