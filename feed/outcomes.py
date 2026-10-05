from collections import Counter
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta

from feed.parse import Block

# The morning plan is the first snapshot in [06:00, 07:00) local; without one, never guess.
MORNING = time(6)
MORNING_WINDOW = timedelta(hours=1)

# Checkpoints: the last snapshot before the midnight ending the plan day (d0), the next day (d1)
# and the day after (d2). The feed query's 2-day lookback keeps the plan day in the feed until then.
CHECKPOINTS = ("d0", "d1", "d2")

SENTINEL_YEAR = 9999
IN_HOUSE = {"FIELD", "TC"}
COMPLETED = {"job": {"C"}, "ticket": {"C", "R"}}
CANCELED = {"job": {"U", "X"}, "ticket": {"D"}}
# The history covers installs only (FTTH Install, Voice Install, Equipment Upgrade, Infli FTTH
# Install); other FIELD tasks still block calendar time. Rows from before the feed carried a
# task_type have "" and were already install-only in SQL.
INSTALL_TYPES = {"3", "23", "36", "120"}
# Dead before the day started, so never part of the morning plan.
NOT_PLANNED = {"job": {"U", "X"}, "ticket": {"D"}}

OUTCOMES = (
    "completed",
    "canceled",
    "handed_off",
    "unscheduled",
    "rescheduled",
    "unassigned",
    "open",
    "missing",
)
KINDS = ("job", "ticket")

# A job pulled from the plan day: the cases where it matters whether a tech had already engaged.
PULLED = ("canceled", "unscheduled", "rescheduled")
# How far the tech got on the plan day, from the query's status trace. A pull after In Progress
# means a tech was on site (the same failure whether it ends canceled or unscheduled); En Route is
# a gray area. "unknown" is a snapshot from before the trace columns, or a job gone from the feed.
REACHED = ("in_progress", "en_route", "not_started", "unknown")
# A pre-drop/pre-bury on the order that isn't Completed or dropped (U/X) can keep the install
# from going in. Legacy orders can have both a pre-drop and a pre-bury open.
PREREQS_DONE = {"C", "U", "X"}

Key = tuple[str, str]


@dataclass
class Snapshot:
    id: int
    at: datetime  # naive MBS local time


@dataclass
class Planned:
    kind: str
    ref_id: str
    tech_id: str
    tech_name: str
    planned_start: datetime
    outcomes: dict[str, str] = field(default_factory=dict)
    reached: str = "unknown"
    prereqs_open: bool | None = None
    region: str = ""
    task_type: str = ""


@dataclass
class DayOutcome:
    day: date
    status: str  # "ok" or "no_morning"
    morning: Snapshot | None = None
    items: list[Planned] = field(default_factory=list)
    added_after_morning: dict[str, int] = field(default_factory=dict)


def base_kind(kind: str) -> str:
    # job_moved / job_unassigned are the same job off the calendar or without a tech.
    return kind.split("_", 1)[0]


def find_morning(snapshots: list[Snapshot], day: date) -> Snapshot | None:
    start = datetime.combine(day, MORNING)
    in_window = [s for s in snapshots if start <= s.at < start + MORNING_WINDOW]
    return min(in_window, key=lambda s: s.at, default=None)


def find_checkpoint(snapshots: list[Snapshot], morning: Snapshot, day: date, offset: int):
    # Before the boundary has passed this is simply the latest snapshot: the outcome so far.
    boundary = datetime.combine(day + timedelta(days=offset + 1), time())
    candidates = [s for s in snapshots if morning.at <= s.at < boundary]
    return max(candidates, key=lambda s: s.at, default=None)


def index(blocks: list[Block]) -> dict[Key, Block]:
    found: dict[Key, Block] = {}
    for block in blocks:
        kind = base_kind(block.kind)
        if kind in KINDS and block.ref_id:
            found.setdefault((kind, block.ref_id), block)
    return found


def planned_items(
    blocks: list[Block], day: date, regions: dict[str, str] | None = None
) -> dict[Key, Planned]:
    # The work's region (the feed query's address geocode); otherwise the tech's shift region.
    regions = regions or {}
    planned: dict[Key, Planned] = {}
    for block in blocks:
        if block.kind not in KINDS or block.work_date != day or not block.ref_id:
            continue
        if block.status in NOT_PLANNED[block.kind]:
            continue
        if block.kind == "job" and block.task_type and block.task_type not in INSTALL_TYPES:
            continue
        planned.setdefault(
            (block.kind, block.ref_id),
            Planned(
                block.kind,
                block.ref_id,
                block.tech_id,
                block.tech_name,
                block.starts_at,
                region=block.region or regions.get(block.tech_id, ""),
                task_type=block.task_type,
            ),
        )
    return planned


def classify(kind: str, day: date, block: Block | None) -> str:
    if block is None:
        return "missing"
    if block.status in COMPLETED[kind]:
        return "completed"
    if block.status in CANCELED[kind]:
        return "canceled"
    # Rows from before the feed carried a department have "", which says nothing.
    if block.department and block.department not in IN_HOUSE:
        return "handed_off"
    if block.starts_at.year == SENTINEL_YEAR:
        return "unscheduled"
    if block.starts_at.date() != day:
        return "rescheduled"
    if not block.tech_id:
        return "unassigned"
    return "open"


def reached(day: date, block: Block | None) -> str:
    # Every task row carries modified_at once the feed query has the trace columns.
    if block is None or block.modified_at is None:
        return "unknown"
    if block.inprogress_at and block.inprogress_at.date() == day:
        return "in_progress"
    if block.enroute_at and block.enroute_at.date() == day:
        return "en_route"
    return "not_started"


def prereqs_open(block: Block | None) -> bool | None:
    if block is None or block.modified_at is None:
        return None
    statuses = [item.rsplit(": ", 1)[-1] for item in block.prereqs_status.split(", ") if item]
    return any(status not in PREREQS_DONE for status in statuses)


def day_outcome(
    day: date,
    morning: Snapshot | None,
    morning_blocks: list[Block],
    checkpoints: dict[str, list[Block] | None],
    regions: dict[str, str] | None = None,
) -> DayOutcome:
    if morning is None:
        return DayOutcome(day, "no_morning")
    planned = planned_items(morning_blocks, day, regions)
    for name, blocks in checkpoints.items():
        if blocks is None:
            continue
        found = index(blocks)
        for key, item in planned.items():
            item.outcomes[name] = classify(item.kind, day, found.get(key))
    added = Counter()
    d0 = checkpoints.get("d0")
    if d0 is not None:
        found = index(d0)
        for key, item in planned.items():
            item.reached = reached(day, found.get(key))
            item.prereqs_open = prereqs_open(found.get(key))
        for key in planned_items(d0, day):
            if key not in planned:
                added[key[0]] += 1
    return DayOutcome(
        day,
        "ok",
        morning,
        sorted(planned.values(), key=lambda p: (p.kind, p.planned_start, p.ref_id)),
        {kind: added[kind] for kind in KINDS},
    )


def summarise(outcome: DayOutcome) -> dict:
    by_kind = {}
    for kind in KINDS:
        items = [i for i in outcome.items if i.kind == kind]
        latest = [i.outcomes.get("d2") for i in items]
        by_kind[kind] = {
            "planned": len(items),
            **{
                f"completed_{name}": sum(i.outcomes.get(name) == "completed" for i in items)
                for name in CHECKPOINTS
            },
            "as_of_d2": {name: latest.count(name) for name in OUTCOMES},
            "added_after_morning": outcome.added_after_morning.get(kind, 0),
        }
    pulled = [i for i in outcome.items if i.kind == "job" and i.outcomes.get("d0") in PULLED]
    by_kind["job"]["pulled_d0"] = {
        "total": len(pulled),
        **{name: sum(i.reached == name for i in pulled) for name in REACHED},
        "prereqs_open": sum(bool(i.prereqs_open) for i in pulled),
    }
    return by_kind
