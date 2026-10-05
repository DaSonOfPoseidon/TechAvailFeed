from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta

from feed.parse import Block

# Ported from the feed query: a slot must be at least this long, and can't start sooner than
# this after "now". Unlike the query, a gap starting too soon is clipped, not
# dropped, so "free from now until 15:00" still shows today.
MIN_MINUTES = 60
LEAD_MINUTES = 30

# The feed query's assumed lunch hour: 12:00-13:00, Saturdays 13:00-14:00.
LUNCH_START = time(12)
SATURDAY_LUNCH_START = time(13)
LUNCH_LENGTH = timedelta(hours=1)
SATURDAY = 5

# The feed query sends a schedule as "shift" if it is on the install calendar and "shift_tc" if
# it is on the TC (trouble call) calendar; a dual tech's schedule, tagged FIELD and TC at once,
# comes as both.
# Capacity is computed for one calendar at a time and never added across the two.
CALENDARS = {"install": "shift", "tc": "shift_tc"}
# Both calendars cover the same hours. A trouble call blocks a dual tech's install time, but an
# install doesn't block trouble calls (user, 10-02-2026).
BUSY_ON = {"install": ("job", "ticket", "time_off"), "tc": ("ticket", "time_off")}

# Statuses that don't occupy a tech's time, as the query filtered them before the feed carried every
# status: Unnecessary/Canceled tasks; Closed/Deleted/Hold/Cleared tickets. A held task stays busy
# while a held ticket is free, a documented asymmetry.
NOT_BUSY = {"job": {"U", "X"}, "ticket": {"C", "D", "H", "R"}}

Interval = tuple[datetime, datetime]


@dataclass
class FreeSlot:
    work_date: date
    tech_id: str
    tech_name: str
    open_from: datetime
    open_until: datetime
    open_minutes: int
    region: str
    skills: str


def merge(intervals: list[Interval]) -> list[Interval]:
    merged: list[Interval] = []
    for start, end in sorted(intervals):
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    return merged


def subtract(free: list[Interval], busy: list[Interval]) -> list[Interval]:
    # Both inputs must be merged and sorted.
    result: list[Interval] = []
    for start, end in free:
        cursor = start
        for busy_start, busy_end in busy:
            if busy_end <= cursor or busy_start >= end:
                continue
            if busy_start > cursor:
                result.append((cursor, busy_start))
            cursor = max(cursor, busy_end)
        if cursor < end:
            result.append((cursor, end))
    return result


def lunch(work_date: date) -> Interval:
    start_time = SATURDAY_LUNCH_START if work_date.weekday() == SATURDAY else LUNCH_START
    start = datetime.combine(work_date, start_time)
    return start, start + LUNCH_LENGTH


def intersect(a: list[Interval], b: list[Interval]) -> list[Interval]:
    # Both inputs must be merged and sorted.
    return subtract(a, subtract(a, b))


def hours(intervals: list[Interval]) -> float:
    return sum((end - start).total_seconds() for start, end in intervals) / 3600


def is_busy(block: Block, calendar: str = "install") -> bool:
    return block.kind in BUSY_ON[calendar] and block.status not in NOT_BUSY.get(block.kind, ())


TechBlocks = dict[str, list[Block]]


def group(
    blocks: list[Block], calendar: str = "install"
) -> tuple[dict[tuple[str, date], list[Block]], TechBlocks]:
    # One calendar's shift segments per tech and day, and every busy block per tech (time off can
    # span days).
    shift_kind = CALENDARS[calendar]
    shifts: dict[tuple[str, date], list[Block]] = defaultdict(list)
    busy: dict[str, list[Block]] = defaultdict(list)
    for block in blocks:
        if block.kind == shift_kind:
            shifts[(block.tech_id, block.work_date)].append(block)
        elif is_busy(block, calendar):
            busy[block.tech_id].append(block)
    return shifts, busy


def free_slots(
    blocks: list[Block],
    now: datetime,
    *,
    min_minutes: int = MIN_MINUTES,
    lead_minutes: int = LEAD_MINUTES,
    with_lunch: bool = True,
    calendar: str = "install",
) -> list[FreeSlot]:
    # now is naive MBS local time, like the block timestamps.
    shifts, busy = group(blocks, calendar)

    # Rounded up to the whole minute, so clipped slots don't start at 17:00:51.
    earliest = now.replace(second=0, microsecond=0) + timedelta(minutes=lead_minutes)
    if now.second or now.microsecond:
        earliest += timedelta(minutes=1)
    minimum = timedelta(minutes=min_minutes)
    slots = []
    for (tech_id, work_date), segments in shifts.items():
        working = merge([(s.starts_at, s.ends_at) for s in segments])
        taken = [(b.starts_at, b.ends_at) for b in busy[tech_id]]
        taken += [lunch(work_date)] if with_lunch else []
        for start, end in subtract(working, merge(taken)):
            start = max(start, earliest)
            if end - start < minimum:
                continue
            first = segments[0]
            slots.append(
                FreeSlot(
                    work_date=work_date,
                    tech_id=tech_id,
                    tech_name=first.tech_name,
                    open_from=start,
                    open_until=end,
                    open_minutes=round((end - start).total_seconds() / 60),
                    region=first.region,
                    skills=first.skills,
                )
            )
    slots.sort(key=lambda s: (s.work_date, s.tech_name, s.open_from))
    return slots


@dataclass
class TechDay:
    # One tech's day. available = shift - lunch - time off; booked is the jobs and tickets inside
    # it; free is the usable slots (free_slots' rules, so today's are clipped to now + lead time).
    # booked + free <= available: gaps shorter than MIN_MINUTES are neither.
    work_date: date
    tech_id: str
    tech_name: str
    region: str
    skills: str
    shifts: list[Interval]
    time_off: list[Interval]
    work: list[Block]
    free: list[FreeSlot]
    shift_hours: float
    lunch_hours: float
    time_off_hours: float
    available_hours: float
    booked_hours: float
    free_hours: float
    jobs: int
    tickets: int
    on_time_off: bool


def day_span(work_date: date) -> Interval:
    start = datetime.combine(work_date, time())
    return start, start + timedelta(days=1)


def tech_days(
    blocks: list[Block],
    now: datetime,
    *,
    start: date | None = None,
    end: date | None = None,
    calendar: str = "install",
) -> list[TechDay]:
    # Every tech and date in [start, end] with a shift on this calendar, or time off. The range
    # defaults to the shift dates, so leave running for months is only expanded where it is asked
    # for. A day off counts on every calendar the tech has shifts on; a tech with no shifts at all
    # stays on install, as before TC shifts were sent.
    shifts, busy = group(blocks, calendar)
    scheduled = {
        name: {b.tech_id for b in blocks if b.kind == kind} for name, kind in CALENDARS.items()
    }
    on_any = set().union(*scheduled.values())

    def belongs(tech_id: str) -> bool:
        return tech_id in scheduled[calendar] or (calendar == "install" and tech_id not in on_any)

    shift_dates = [work_date for _, work_date in shifts]
    start = start or min(shift_dates, default=None)
    end = end or max(shift_dates, default=None)
    if start is None or end is None:
        return []
    free: dict[tuple[str, date], list[FreeSlot]] = defaultdict(list)
    for slot in free_slots(blocks, now, calendar=calendar):
        free[(slot.tech_id, slot.work_date)].append(slot)

    keys = {key for key in shifts if start <= key[1] <= end}
    names: dict[str, str] = {}
    for tech_id, tech_blocks in busy.items():
        for block in tech_blocks:
            names.setdefault(tech_id, block.tech_name)
            if block.kind != "time_off" or not belongs(tech_id):
                continue
            day = max(block.starts_at.date(), start)
            while day <= min(block.ends_at.date(), end):
                keys.add((tech_id, day))
                day += timedelta(days=1)
    # A day off still belongs to the region the tech usually works in.
    home: dict[str, Block] = {}
    for (tech_id, _), segments in sorted(shifts.items()):
        home.setdefault(tech_id, segments[0])

    days = []
    for tech_id, work_date in keys:
        segments = shifts.get((tech_id, work_date), [])
        working = merge([(s.starts_at, s.ends_at) for s in segments])
        tech_blocks = busy.get(tech_id, [])
        span = [day_span(work_date)]
        leave = merge([(b.starts_at, b.ends_at) for b in tech_blocks if b.kind == "time_off"])
        lunch_taken = intersect(working, [lunch(work_date)])
        off = intersect(working, leave)
        available = subtract(working, merge(lunch_taken + off))
        work = [
            b
            for b in tech_blocks
            if b.kind != "time_off" and intersect(span, [(b.starts_at, b.ends_at)])
        ]
        booked = intersect(available, merge([(b.starts_at, b.ends_at) for b in work]))
        slots = free.get((tech_id, work_date), [])
        info = segments[0] if segments else home.get(tech_id)
        days.append(
            TechDay(
                work_date=work_date,
                tech_id=tech_id,
                tech_name=info.tech_name if info else names.get(tech_id, tech_id),
                region=info.region if info else "",
                skills=info.skills if info else "",
                shifts=working,
                time_off=intersect(span, leave),
                work=sorted(work, key=lambda b: (b.starts_at, b.kind, b.ref_id)),
                free=slots,
                shift_hours=round(hours(working), 2),
                lunch_hours=round(hours(lunch_taken), 2),
                time_off_hours=round(hours(off), 2),
                available_hours=round(hours(available), 2),
                booked_hours=round(hours(booked), 2),
                free_hours=round(sum(s.open_minutes for s in slots) / 60, 2),
                jobs=len({b.ref_id for b in work if b.kind == "job"}),
                tickets=len({b.ref_id for b in work if b.kind == "ticket"}),
                on_time_off=bool(off) or (not working and bool(intersect(span, leave))),
            )
        )
    days.sort(key=lambda d: (d.work_date, d.tech_name, d.tech_id))
    return days


@dataclass
class UnassignedDemand:
    work_date: date
    region: str
    jobs: int
    tickets: int
    hours: float


def unassigned_demand(blocks: list[Block], today: date) -> list[UnassignedDemand]:
    # Scheduled work with no tech yet, per day and the work's region. Like the
    # region availability summary, it will take a rostered tech's time, so a dashboard nets it
    # against free capacity. Free slots above stay per tech and don't subtract it.
    totals: dict[tuple[date, str], list] = defaultdict(lambda: [0, 0, 0.0])
    for block in blocks:
        kind = block.kind.removesuffix("_unassigned")
        if kind == block.kind or block.work_date < today:
            continue
        if block.status in NOT_BUSY.get(kind, ()):
            continue
        entry = totals[(block.work_date, block.region)]
        entry[0 if kind == "job" else 1] += 1
        entry[2] += (block.ends_at - block.starts_at).total_seconds() / 3600
    return [
        UnassignedDemand(work_date, region, jobs, tickets, round(hours, 2))
        for (work_date, region), (jobs, tickets, hours) in sorted(totals.items())
    ]
