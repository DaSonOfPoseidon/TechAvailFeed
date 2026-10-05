from datetime import date, datetime

from feed.availability import free_slots, tech_days, unassigned_demand
from feed.parse import Block

TUESDAY = date(2026, 10, 6)
SATURDAY = date(2026, 10, 10)
EARLY = datetime(2026, 10, 1, 7, 0)


def at(day: date, hhmm: str) -> datetime:
    hour, minute = map(int, hhmm.split(":"))
    return datetime(day.year, day.month, day.day, hour, minute)


def block(kind: str, day: date, start: str, end: str, tech: str = "t1", status: str = "A") -> Block:
    return Block(
        kind=kind,
        work_date=day,
        tech_id=tech,
        tech_name=tech.upper(),
        starts_at=at(day, start),
        ends_at=at(day, end),
        ref_id="",
        status=status,
        department="",
        region="North" if kind == "shift" else "",
        skills="INS" if kind == "shift" else "",
    )


def windows(slots) -> list[tuple[str, str]]:
    return [(s.open_from.strftime("%H:%M"), s.open_until.strftime("%H:%M")) for s in slots]


def test_open_day_is_split_only_by_lunch():
    slots = free_slots([block("shift", TUESDAY, "08:00", "17:00")], EARLY)
    assert windows(slots) == [("08:00", "12:00"), ("13:00", "17:00")]
    assert slots[0].region == "North"
    assert slots[0].open_minutes == 240


def test_saturday_lunch_is_an_hour_later():
    slots = free_slots([block("shift", SATURDAY, "08:00", "17:00")], EARLY)
    assert windows(slots) == [("08:00", "13:00"), ("14:00", "17:00")]


def test_overlapping_busy_blocks_merge():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00"),
        block("job", TUESDAY, "08:30", "10:00"),
        block("ticket", TUESDAY, "09:30", "10:30"),
    ]
    assert windows(free_slots(blocks, EARLY)) == [("10:30", "12:00")]


def test_split_shift_gap_is_not_free():
    blocks = [
        block("shift", TUESDAY, "08:00", "11:00"),
        block("shift", TUESDAY, "14:00", "17:00"),
    ]
    assert windows(free_slots(blocks, EARLY)) == [("08:00", "11:00"), ("14:00", "17:00")]


def test_short_gaps_are_dropped():
    blocks = [
        block("shift", TUESDAY, "08:00", "11:00"),
        block("job", TUESDAY, "08:45", "11:00"),
    ]
    assert free_slots(blocks, EARLY) == []
    assert windows(free_slots(blocks, EARLY, min_minutes=30)) == [("08:00", "08:45")]


def test_todays_leading_gap_is_clipped_not_dropped():
    # The feed query discarded this gap because it starts before now + 30 min.
    now = at(TUESDAY, "13:10")
    slots = free_slots([block("shift", TUESDAY, "08:00", "17:00")], now)
    assert windows(slots) == [("13:40", "17:00")]


def test_multi_day_time_off_blocks_every_day_it_covers():
    wednesday = date(2026, 10, 7)
    time_off = Block(
        kind="time_off",
        work_date=TUESDAY,
        tech_id="t1",
        tech_name="T1",
        starts_at=at(TUESDAY, "00:00"),
        ends_at=at(date(2026, 10, 8), "00:00"),
        ref_id="",
        status="",
        department="",
        region="",
        skills="",
    )
    blocks = [
        block("shift", TUESDAY, "08:00", "17:00"),
        block("shift", wednesday, "08:00", "17:00"),
        time_off,
    ]
    assert free_slots(blocks, EARLY) == []


def test_busy_time_only_affects_its_own_tech():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00", tech="a"),
        block("shift", TUESDAY, "08:00", "12:00", tech="b"),
        block("job", TUESDAY, "08:00", "12:00", tech="a"),
    ]
    assert [s.tech_id for s in free_slots(blocks, EARLY)] == ["b"]


def test_dead_jobs_closed_tickets_and_moved_rows_are_not_busy():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00"),
        block("job", TUESDAY, "08:00", "09:00", status="X"),
        block("ticket", TUESDAY, "09:00", "10:00", status="C"),
        block("job_moved", TUESDAY, "10:00", "11:00"),
    ]
    assert windows(free_slots(blocks, EARLY)) == [("08:00", "12:00")]


def test_held_task_blocks_but_held_ticket_does_not():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00"),
        block("job", TUESDAY, "08:00", "10:00", status="H"),
        block("ticket", TUESDAY, "10:00", "12:00", status="H"),
    ]
    assert windows(free_slots(blocks, EARLY)) == [("10:00", "12:00")]


def test_clip_rounds_up_to_the_minute():
    now = datetime(2026, 10, 6, 13, 10, 51, 5000)
    slots = free_slots([block("shift", TUESDAY, "08:00", "17:00")], now)
    assert slots[0].open_from == at(TUESDAY, "13:41")


def unassigned(kind: str, day: date, start: str, end: str, region: str, status: str = "A") -> Block:
    row = block(kind, day, start, end, tech="", status=status)
    row.region = region
    return row


def test_unassigned_work_takes_no_tech_time():
    blocks = [
        block("shift", TUESDAY, "08:00", "17:00"),
        unassigned("job_unassigned", TUESDAY, "08:00", "17:00", "North"),
    ]
    assert windows(free_slots(blocks, EARLY)) == [("08:00", "12:00"), ("13:00", "17:00")]


def test_unassigned_demand_is_summed_per_day_and_region():
    blocks = [
        unassigned("job_unassigned", TUESDAY, "08:00", "10:00", "North"),
        unassigned("job_unassigned", TUESDAY, "13:00", "14:30", "North"),
        unassigned("ticket_unassigned", TUESDAY, "10:00", "11:00", "North"),
        unassigned("job_unassigned", TUESDAY, "08:00", "10:00", "South"),
        unassigned("job_unassigned", TUESDAY, "08:00", "10:00", "North", status="X"),  # dead
        unassigned("ticket_unassigned", TUESDAY, "08:00", "10:00", "North", status="H"),  # held
        unassigned("job_unassigned", date(2026, 10, 5), "08:00", "10:00", "North"),  # past
        block("job", TUESDAY, "08:00", "10:00"),  # assigned: not demand
    ]
    demand = unassigned_demand(blocks, TUESDAY)
    assert [(d.region, d.jobs, d.tickets, d.hours) for d in demand] == [
        ("North", 2, 1, 4.5),
        ("South", 1, 0, 2.0),
    ]


def test_tech_day_splits_the_shift_into_lunch_time_off_booked_and_free():
    blocks = [
        block("shift", TUESDAY, "08:00", "17:00"),
        block("job", TUESDAY, "08:00", "10:00"),
        block("ticket", TUESDAY, "11:30", "12:30"),  # overlaps lunch: counted once
        block("time_off", TUESDAY, "15:00", "23:59"),
    ]
    [day] = tech_days(blocks, EARLY)
    assert (day.shift_hours, day.lunch_hours, day.time_off_hours) == (9, 1, 2)
    assert day.available_hours == 6
    assert day.booked_hours == 2.5
    # 10:00-11:30 and 13:00-15:00; the free time between bookings, at least an hour each.
    assert day.free_hours == 3.5
    assert (day.jobs, day.tickets) == (1, 1)
    assert day.on_time_off
    assert day.region == "North"
    assert [s.open_from.strftime("%H:%M") for s in day.free] == ["10:00", "13:00"]


def test_tech_day_ignores_dead_work_and_counts_jobs_once():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00"),
        block("job", TUESDAY, "08:00", "09:00", status="X"),
        block("ticket", TUESDAY, "09:00", "10:00", status="C"),
        block("job_moved", TUESDAY, "10:00", "11:00"),
    ]
    [day] = tech_days(blocks, EARLY)
    assert (day.booked_hours, day.jobs, day.tickets) == (0, 0, 0)


def test_leave_without_a_shift_is_a_day_off_with_no_capacity():
    leave = block("time_off", TUESDAY, "00:00", "23:59")
    leave.ends_at = at(date(2026, 10, 7), "23:59")
    days = tech_days([leave], EARLY, start=TUESDAY, end=date(2026, 10, 7))
    assert [(d.work_date, d.on_time_off, d.shift_hours) for d in days] == [
        (TUESDAY, True, 0),
        (date(2026, 10, 7), True, 0),
    ]


def test_leave_is_only_expanded_inside_the_requested_range():
    leave = block("time_off", TUESDAY, "00:00", "23:59")
    leave.starts_at = at(date(2026, 9, 1), "00:00")
    leave.ends_at = at(date(2027, 10, 1), "23:59")
    assert [d.work_date for d in tech_days([leave], EARLY, start=TUESDAY, end=TUESDAY)] == [TUESDAY]


def test_lead_time_only_clips_free_hours():
    now = at(TUESDAY, "13:10")
    [day] = tech_days([block("shift", TUESDAY, "08:00", "17:00")], now)
    assert day.available_hours == 8
    assert day.free_hours == round(200 / 60, 2)


def test_tc_shifts_are_not_install_capacity():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00", tech="install"),
        block("shift_tc", TUESDAY, "08:00", "12:00", tech="trouble"),
        block("ticket", TUESDAY, "08:00", "09:00", tech="trouble"),
    ]
    assert [s.tech_id for s in free_slots(blocks, EARLY)] == ["install"]
    assert [d.tech_id for d in tech_days(blocks, EARLY)] == ["install"]
    [tc] = tech_days(blocks, EARLY, calendar="tc")
    assert (tc.tech_id, tc.booked_hours, tc.free_hours) == ("trouble", 1, 3)
    assert windows(free_slots(blocks, EARLY, calendar="tc")) == [("09:00", "12:00")]


def test_a_day_off_counts_on_the_techs_own_calendar():
    blocks = [
        block("shift", TUESDAY, "08:00", "17:00", tech="install"),
        block("shift_tc", TUESDAY, "08:00", "17:00", tech="trouble"),
        block("time_off", date(2026, 10, 7), "00:00", "23:59", tech="install"),
        block("time_off", date(2026, 10, 7), "00:00", "23:59", tech="trouble"),
        block("time_off", date(2026, 10, 7), "00:00", "23:59", tech="leave"),  # no shifts at all
    ]
    wednesday = date(2026, 10, 7)
    install = tech_days(blocks, EARLY, start=wednesday, end=wednesday)
    tc = tech_days(blocks, EARLY, start=wednesday, end=wednesday, calendar="tc")
    # A tech with no shift of either kind stays on the install calendar, as before shift_tc.
    assert [d.tech_id for d in install] == ["install", "leave"]
    assert [d.tech_id for d in tc] == ["trouble"]


def test_dual_tech_tickets_block_installs_but_jobs_dont_block_trouble_calls():
    blocks = [
        block("shift", TUESDAY, "08:00", "12:00", tech="dual"),
        block("shift_tc", TUESDAY, "08:00", "12:00", tech="dual"),
        block("job", TUESDAY, "08:00", "10:00", tech="dual"),
        block("ticket", TUESDAY, "10:00", "11:00", tech="dual"),
    ]
    [install] = tech_days(blocks, EARLY)
    [tc] = tech_days(blocks, EARLY, calendar="tc")
    assert (install.booked_hours, install.free_hours, install.jobs, install.tickets) == (3, 1, 1, 1)
    assert (tc.booked_hours, tc.free_hours, tc.jobs, tc.tickets) == (1, 3, 0, 1)
    assert windows(free_slots(blocks, EARLY, calendar="tc")) == [
        ("08:00", "10:00"),
        ("11:00", "12:00"),
    ]


def test_a_dual_techs_day_off_counts_on_both_calendars():
    wednesday = date(2026, 10, 7)
    blocks = [
        block("shift", TUESDAY, "08:00", "17:00", tech="dual"),
        block("shift_tc", TUESDAY, "08:00", "17:00", tech="dual"),
        block("time_off", wednesday, "00:00", "23:59", tech="dual"),
    ]
    for calendar in ("install", "tc"):
        days = tech_days(blocks, EARLY, start=wednesday, end=wednesday, calendar=calendar)
        assert [(d.tech_id, d.on_time_off) for d in days] == [("dual", True)]
