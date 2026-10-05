from collections import defaultdict

from feed.outcomes import CHECKPOINTS, KINDS, OUTCOMES, PULLED, REACHED, DayOutcome, Planned

# Rates over feed.outcomes' planned items. Each rate's denominator is the planned count of that
# kind; a day without a morning plan ("no_morning") is listed but adds nothing to the rates.


def final(item: Planned) -> str | None:
    # The latest checkpoint the item has: d2 once a day is final, the outcome so far before that.
    for name in reversed(CHECKPOINTS):
        if name in item.outcomes:
            return item.outcomes[name]
    return None


def rate(count: int, total: int) -> float | None:
    return round(count / total, 3) if total else None


def stats(items: list[Planned], kind: str) -> dict:
    items = [i for i in items if i.kind == kind]
    planned = len(items)
    completed = {
        name: sum(i.outcomes.get(name) == "completed" for i in items) for name in CHECKPOINTS
    }
    latest = [final(i) for i in items]
    outcome = {name: latest.count(name) for name in OUTCOMES}
    result = {
        "planned": planned,
        "completed": completed,
        "completion_rate": {name: rate(n, planned) for name, n in completed.items()},
        "outcome": outcome,
        "outcome_rate": {name: rate(n, planned) for name, n in outcome.items()},
    }
    if kind == "job":
        pulled = [i for i in items if i.outcomes.get("d0") in PULLED]
        result["pulled_d0"] = {
            "total": len(pulled),
            **{name: sum(i.reached == name for i in pulled) for name in REACHED},
            "prereqs_open": sum(bool(i.prereqs_open) for i in pulled),
        }
        result["pulled_d0_rate"] = rate(len(pulled), planned)
    return result


def by_kind(items: list[Planned]) -> dict:
    return {kind: stats(items, kind) for kind in KINDS}


def select(outcome: DayOutcome, region: str | None, tech: str | None) -> list[Planned]:
    return [
        i
        for i in outcome.items
        if (region is None or i.region == region) and (tech is None or i.tech_id == tech)
    ]


def outcome_kpis(
    days: list[tuple[DayOutcome, bool]], region: str | None = None, tech: str | None = None
) -> dict:
    series = []
    pooled: list[Planned] = []
    regions: dict[str, list[Planned]] = defaultdict(list)
    techs: dict[str, list[Planned]] = defaultdict(list)
    names: dict[str, str] = {}
    for outcome, provisional in sorted(days, key=lambda d: d[0].day):
        items = select(outcome, region, tech)
        series.append(
            {
                "date": outcome.day,
                "status": outcome.status,
                "provisional": provisional,
                **(by_kind(items) if outcome.status == "ok" else {}),
            }
        )
        pooled += items
        for item in items:
            regions[item.region].append(item)
            techs[item.tech_id].append(item)
            names.setdefault(item.tech_id, item.tech_name)
    return {
        "days": series,
        "totals": by_kind(pooled),
        "by_region": [{"region": name, **by_kind(regions[name])} for name in sorted(regions)],
        "by_tech": [
            {"tech_id": tech_id, "tech_name": names[tech_id], **by_kind(techs[tech_id])}
            for tech_id in sorted(techs, key=lambda t: (names[t], t))
        ],
    }
