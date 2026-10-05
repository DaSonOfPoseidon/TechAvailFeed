from collections import Counter, defaultdict
from dataclasses import dataclass, field, replace
from datetime import date

from feed.availability import CALENDARS, NOT_BUSY, Interval, is_busy, merge, subtract
from feed.outcomes import CANCELED, COMPLETED, base_kind
from feed.parse import Block

# Data-quality checks over one snapshot's blocks, for the dashboard's diagnostics view. Rows name
# the job or tech to fix in MBS; they never carry coordinates.

GROUPS = ("tech_setup", "scheduling", "stale", "address")
ASSIGNED = ("job", "ticket")
UNASSIGNED = ("job_unassigned", "ticket_unassigned")
SHIFT_KINDS = tuple(CALENDARS.values())


def calendar_of(shift_kinds: set[str]) -> str:
    # "install", "tc", or "both" for a dual tech whose schedules are on both calendars.
    names = [name for name, kind in CALENDARS.items() if kind in shift_kinds]
    return names[0] if len(names) == 1 else "both"


@dataclass
class Check:
    id: str
    group: str
    title: str
    severity: str  # "warning" or "info"
    description: str
    available: bool = True
    rows: list[dict] = field(default_factory=list)


def minutes(intervals: list[Interval]) -> int:
    return round(sum((end - start).total_seconds() for start, end in intervals) / 60)


def work_row(block: Block, **extra) -> dict:
    return {
        "ref_id": block.ref_id,
        "kind": block.kind,
        "status": block.status,
        "work_date": block.work_date,
        "starts_at": block.starts_at,
        "ends_at": block.ends_at,
        "tech_id": block.tech_id,
        "tech_name": block.tech_name,
        "region": block.region,
        **extra,
    }


def by_time(block: Block) -> tuple:
    return (block.work_date, block.starts_at, block.ref_id, block.tech_id)


def is_live(block: Block) -> bool:
    # Not canceled/closed: the same statuses free_slots ignores.
    return block.status not in NOT_BUSY.get(base_kind(block.kind), ())


def is_done(block: Block) -> bool:
    kind = base_kind(block.kind)
    return block.status in COMPLETED[kind] | CANCELED[kind]


def tech_setup(shifts: list[Block]) -> list[Check]:
    names = {b.tech_id: b.tech_name for b in shifts}
    kinds: dict[str, set] = defaultdict(set)
    for b in shifts:
        kinds[b.tech_id].add(b.kind)
    calendars = {t: calendar_of(k) for t, k in kinds.items()}
    no_region: dict[str, list[date]] = defaultdict(list)
    regions: dict[str, Counter] = defaultdict(Counter)
    no_skills: dict[str, str] = {}
    seen: set[tuple[str, date]] = set()
    for b in sorted(shifts, key=lambda b: (b.tech_id, b.work_date, b.starts_at)):
        if (b.tech_id, b.work_date) in seen:
            continue
        seen.add((b.tech_id, b.work_date))
        if b.region:
            regions[b.tech_id][b.region] += 1
        else:
            no_region[b.tech_id].append(b.work_date)
        if not b.skills.strip():
            no_skills.setdefault(b.tech_id, b.region)

    def tech(tech_id: str, **extra) -> dict:
        return {
            "tech_id": tech_id,
            "tech_name": names[tech_id],
            "calendar": calendars[tech_id],
            **extra,
        }

    def by_name(ids) -> list[str]:
        return sorted(ids, key=lambda t: (names[t], t))

    return [
        Check(
            "tech_no_region",
            "tech_setup",
            "Techs with shifts but no primary region",
            "warning",
            "Shift days whose schedule has no primary department/region row, so their capacity "
            "lands under no region.",
            rows=[
                tech(
                    t,
                    days=len(no_region[t]),
                    first_date=no_region[t][0],
                    last_date=no_region[t][-1],
                )
                for t in by_name(no_region)
            ],
        ),
        Check(
            "tech_no_skills",
            "tech_setup",
            "Techs with no skills",
            "warning",
            "Rostered techs with no proficient skill, so no skill filter ever includes them.",
            rows=[tech(t, region=no_skills[t]) for t in by_name(no_skills)],
        ),
        Check(
            "tech_region_varies",
            "tech_setup",
            "Techs whose primary region changes between days",
            "info",
            "Can be a deliberate split schedule; worth a look when it isn't.",
            rows=[
                tech(
                    t,
                    regions=", ".join(f"{name} ({n})" for name, n in sorted(regions[t].items())),
                )
                for t in by_name(t for t in regions if len(regions[t]) > 1)
            ],
        ),
    ]


# Trouble tickets may share a slot two at a time, as long as both are in the same region (user,
# 10-02-2026). Any overlap involving a job is a conflict.
TICKETS_PER_SLOT = 2


def overlap_reason(first: Block, second: Block, tickets: list[Block]) -> str | None:
    # first starts no later than second, and they overlap. None means the overlap is allowed.
    if first.kind != "ticket" or second.kind != "ticket":
        return "job_overlap"
    if not first.region or not second.region:
        return "ticket_region_unknown"
    if first.region != second.region:
        return "ticket_regions_differ"
    at = second.starts_at
    in_slot = {t.ref_id for t in tickets if t.starts_at <= at < t.ends_at}
    return "more_than_2_tickets" if len(in_slot) > TICKETS_PER_SLOT else None


def scheduling(blocks: list[Block], today: date) -> list[Check]:
    shifts: dict[tuple[str, date], list[Interval]] = defaultdict(list)
    leave: dict[str, list[Interval]] = defaultdict(list)
    work: dict[str, list[Block]] = defaultdict(list)
    for b in blocks:
        # Either calendar's shift covers the tech's work: TC tickets on a TC shift are fine.
        if b.kind in SHIFT_KINDS:
            shifts[(b.tech_id, b.work_date)].append((b.starts_at, b.ends_at))
        elif b.kind == "time_off":
            leave[b.tech_id].append((b.starts_at, b.ends_at))
        elif b.kind in ASSIGNED and b.work_date >= today and is_busy(b):
            work[b.tech_id].append(b)

    # Who is on each job, so a co-assigned person without a schedule (a trainee riding along,
    # user 10-02-2026) isn't reported as work nobody is scheduled for.
    crew: dict[tuple[str, str, date], list[Block]] = defaultdict(list)
    for items in work.values():
        for b in items:
            crew[(b.kind, b.ref_id, b.work_date)].append(b)

    def scheduled_partners(b: Block) -> list[Block]:
        return [
            p
            for p in crew[(b.kind, b.ref_id, b.work_date)]
            if p.tech_id != b.tech_id and (p.tech_id, b.work_date) in shifts
        ]

    without, ride_along, outside, during, double = [], [], [], [], []
    for tech_id, items in work.items():
        off = merge(leave[tech_id])
        unique = {(b.ref_id, b.starts_at, b.ends_at): b for b in items}
        items = sorted(unique.values(), key=by_time)
        for b in items:
            span = [(b.starts_at, b.ends_at)]
            on_leave = minutes(subtract(span, subtract(span, off)))
            working = merge(shifts.get((tech_id, b.work_date), []))
            if on_leave:
                during.append(work_row(b, minutes_on_time_off=on_leave))
            elif not working:
                if partners := scheduled_partners(b):
                    names = ", ".join(sorted({p.tech_name for p in partners}))
                    ride_along.append(work_row(b, **{"with": names}))
                else:
                    without.append(work_row(b))
            elif outside_minutes := minutes(subtract(span, working)):
                outside.append(work_row(b, minutes_outside=outside_minutes))
        tickets = [b for b in items if b.kind == "ticket"]
        for i, first in enumerate(items):
            for second in items[i + 1 :]:
                if second.starts_at >= first.ends_at:
                    break
                if second.ref_id == first.ref_id:
                    continue
                reason = overlap_reason(first, second, tickets)
                if reason is None:
                    continue
                overlap = min(first.ends_at, second.ends_at) - second.starts_at
                double.append(
                    work_row(
                        first,
                        other_ref_id=second.ref_id,
                        other_kind=second.kind,
                        other_starts_at=second.starts_at,
                        other_ends_at=second.ends_at,
                        other_region=second.region,
                        overlap_minutes=round(overlap.total_seconds() / 60),
                        reason=reason,
                    )
                )

    def ordered(rows: list[dict]) -> list[dict]:
        return sorted(rows, key=lambda r: (r["work_date"], r["starts_at"], r["ref_id"]))

    return [
        Check(
            "work_without_shift",
            "scheduling",
            "Work on a day the tech has no shift",
            "warning",
            "Live jobs and tickets, today on, booked to a tech with no shift that day (and no "
            "time off over it), with no scheduled tech on the same job. They take no capacity, so "
            "the calendar overstates free time.",
            rows=ordered(without),
        ),
        Check(
            "ride_along",
            "scheduling",
            "Riding along without a schedule",
            "info",
            "Work for a tech with no shift that day, on a job a scheduled tech is also assigned to "
            "(e.g. a trainee). Expected; listed so it stays visible.",
            rows=ordered(ride_along),
        ),
        Check(
            "work_outside_shift",
            "scheduling",
            "Work running outside the tech's shift",
            "warning",
            "Live work, today on, that starts before or ends after the tech's shift.",
            rows=ordered(outside),
        ),
        Check(
            "work_during_time_off",
            "scheduling",
            "Work during the tech's time off",
            "warning",
            "Live work, today on, overlapping the tech's time off.",
            rows=ordered(during),
        ),
        Check(
            "double_booked",
            "scheduling",
            "Double-booked techs",
            "warning",
            "Two different live jobs or tickets for one tech whose times overlap. Two tickets may "
            "share a slot if both are in the same region; a third, or any job, is a conflict.",
            rows=ordered(double),
        ),
    ]


def stale(blocks: list[Block], today: date) -> list[Check]:
    past = [
        b
        for b in blocks
        if b.kind in ASSIGNED + UNASSIGNED and b.work_date < today and not is_done(b)
    ]
    no_region = [b for b in blocks if b.kind in UNASSIGNED and is_live(b) and not b.region]
    return [
        Check(
            "past_open_work",
            "stale",
            "Past work still open",
            "warning",
            "Jobs and tickets dated before today that were neither completed nor canceled.",
            rows=[work_row(b) for b in sorted(past, key=by_time)],
        ),
        Check(
            "unassigned_no_region",
            "stale",
            "Unassigned work with no region",
            "warning",
            "Live work with no tech and no region, so it counts against no region's capacity.",
            rows=[work_row(b) for b in sorted(no_region, key=by_time)],
        ),
    ]


def address(blocks: list[Block], today: date) -> Check:
    work = [b for b in blocks if b.kind in ASSIGNED + UNASSIGNED]
    flagged = [b for b in work if b.work_date >= today and is_live(b) and b.address_issue]
    return Check(
        "work_address_issue",
        "address",
        "Work with an address problem",
        "warning",
        "Live work, today on, whose service address is missing, not found, has no city or GPS, "
        "or has a geocode no operating region covers.",
        available=any(b.address_issue is not None for b in work),
        rows=[
            work_row(b, issue=b.address_issue)
            for b in sorted(flagged, key=lambda b: (b.address_issue, *by_time(b)))
        ],
    )


def diagnose(blocks: list[Block], today: date) -> list[Check]:
    # today is MBS local. Scheduling and address checks look from today on; stale ones before it.
    shifts = [b for b in blocks if b.kind in SHIFT_KINDS]
    # The install shift's region wins when a tech has both.
    regions = {(b.tech_id, b.work_date): b.region for b in shifts if b.kind == "shift_tc"}
    regions |= {(b.tech_id, b.work_date): b.region for b in shifts if b.kind == "shift"}
    # The query gives each job and ticket the region its address's geocode maps to in its department
    # (10-02). A job without one (unmapped, or an older snapshot) falls back to the tech's shift
    # region that day; a ticket doesn't, so the slot rule never trusts a borrowed region.
    blocks = [
        (
            replace(b, region=regions.get((b.tech_id, b.work_date), ""))
            if b.kind == "job" and not b.region
            else b
        )
        for b in blocks
    ]
    return [
        *tech_setup(shifts),
        *scheduling(blocks, today),
        *stale(blocks, today),
        address(blocks, today),
    ]
