from collections import defaultdict
from dataclasses import asdict, dataclass
from datetime import date, timedelta

from feed.availability import NOT_BUSY, TechDay
from feed.parse import Block

# Rollups of feed.availability's per-tech days for the calendar and the capacity KPIs.
# No scheduling rules live here: hours come from tech_days, demand from the unassigned rows.


@dataclass
class Capacity:
    techs_on: int = 0
    techs_off: int = 0
    shift_h: float = 0
    available_h: float = 0
    booked_h: float = 0
    free_h: float = 0
    jobs: int = 0
    tickets: int = 0
    unassigned_jobs: int = 0
    unassigned_tickets: int = 0
    unassigned_h: float = 0
    # Set by finish(): booked / available, and the free hours left once unassigned work is placed.
    utilization: float | None = None
    net_h: float = 0

    def add_day(self, day: TechDay) -> None:
        self.techs_on += day.available_hours > 0
        self.techs_off += day.on_time_off
        self.shift_h += day.shift_hours
        self.available_h += day.available_hours
        self.booked_h += day.booked_hours
        self.free_h += day.free_hours
        self.jobs += day.jobs
        self.tickets += day.tickets

    def add_demand(self, block: Block) -> None:
        if block.kind == "job_unassigned":
            self.unassigned_jobs += 1
        else:
            self.unassigned_tickets += 1
        self.unassigned_h += (block.ends_at - block.starts_at).total_seconds() / 3600

    def finish(self) -> "Capacity":
        for name in ("shift_h", "available_h", "booked_h", "free_h", "unassigned_h"):
            setattr(self, name, round(getattr(self, name), 2))
        self.utilization = round(self.booked_h / self.available_h, 3) if self.available_h else None
        self.net_h = round(self.free_h - self.unassigned_h, 2)
        return self

    def as_dict(self) -> dict:
        return asdict(self)


def has_skill(skills: str, skill: str) -> bool:
    return skill.upper() in {s.strip().upper() for s in skills.split(",")}


def filter_days(days: list[TechDay], region: str | None, skill: str | None) -> list[TechDay]:
    return [
        d
        for d in days
        if (region is None or d.region == region) and (skill is None or has_skill(d.skills, skill))
    ]


def unassigned_work(
    blocks: list[Block],
    start: date,
    end: date,
    region: str | None = None,
    calendar: str = "install",
) -> list[Block]:
    # Live jobs and tickets with no tech yet, by the region their address maps to. They carry no
    # skill, so a skill filter doesn't narrow them. TC-department work is the TC calendar's
    # demand; everything else is install's.
    found = []
    for block in blocks:
        kind = block.kind.removesuffix("_unassigned")
        if kind == block.kind or not start <= block.work_date <= end:
            continue
        if (block.department == "TC") != (calendar == "tc"):
            continue
        if block.status in NOT_BUSY.get(kind, ()):
            continue
        if region is not None and block.region != region:
            continue
        found.append(block)
    return sorted(found, key=lambda b: (b.work_date, b.region, b.starts_at, b.ref_id))


def calendar_entries(
    days: list[TechDay], demand: list[Block], start: date, end: date
) -> list[dict]:
    # One entry per date in [start, end], totals plus a per-region breakdown. Pass days and
    # demand already filtered.
    totals: dict[date, Capacity] = defaultdict(Capacity)
    regions: dict[date, dict[str, Capacity]] = defaultdict(lambda: defaultdict(Capacity))
    for day in days:
        totals[day.work_date].add_day(day)
        regions[day.work_date][day.region].add_day(day)
    for block in demand:
        totals[block.work_date].add_demand(block)
        regions[block.work_date][block.region].add_demand(block)

    result = []
    current = start
    while current <= end:
        by_region = regions.get(current, {})
        result.append(
            {
                "date": current,
                "totals": totals[current].finish().as_dict(),
                "by_region": [
                    {"region": name, **by_region[name].finish().as_dict()}
                    for name in sorted(by_region)
                ],
            }
        )
        current += timedelta(days=1)
    return result


def region_totals(entries: list[dict]) -> list[dict]:
    # Sums a calendar's per-region rows over the whole range.
    totals: dict[str, Capacity] = defaultdict(Capacity)
    for entry in entries:
        for row in entry["by_region"]:
            total = totals[row["region"]]
            for name in (
                "techs_off",
                "shift_h",
                "available_h",
                "booked_h",
                "free_h",
                "jobs",
                "tickets",
                "unassigned_jobs",
                "unassigned_tickets",
                "unassigned_h",
            ):
                setattr(total, name, getattr(total, name) + row[name])
    result = []
    for name in sorted(totals):
        row = totals[name].finish().as_dict()
        # Head counts don't add up across days: summed, they are tech-days.
        del row["techs_on"]
        row["tech_days_off"] = row.pop("techs_off")
        result.append({"region": name, **row})
    return result
