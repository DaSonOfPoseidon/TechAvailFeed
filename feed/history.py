from datetime import date, datetime, time, timedelta, tzinfo

from feed.outcomes import (
    CHECKPOINTS,
    DayOutcome,
    Snapshot,
    day_outcome,
    find_checkpoint,
    find_morning,
    summarise,
)
from feed.store import Store

# A day is final once D+2 has ended: its d2 checkpoint can no longer change.
FINAL_AFTER = timedelta(days=3)
HISTORY_DAYS = 60


def snapshots(store: Store, tz: tzinfo) -> list[Snapshot]:
    return [
        Snapshot(row["id"], row["generated_at"].astimezone(tz).replace(tzinfo=None))
        for row in store.blocks_snapshots()
    ]


def compute_day(store: Store, snaps: list[Snapshot], day, cache: dict) -> DayOutcome:
    def blocks(snapshot: Snapshot):
        if snapshot.id not in cache:
            cache[snapshot.id] = store.work_blocks(snapshot.id)
        return cache[snapshot.id]

    morning = find_morning(snaps, day)
    if morning is None:
        return day_outcome(day, None, [], {})
    checkpoints = {}
    for offset, name in enumerate(CHECKPOINTS):
        checkpoint = find_checkpoint(snaps, morning, day, offset)
        checkpoints[name] = blocks(checkpoint) if checkpoint else None
    regions = store.shift_regions(morning.id, day)
    return day_outcome(day, morning, blocks(morning), checkpoints, regions)


def is_final(day, latest: datetime) -> bool:
    return datetime.combine(day, time()) + FINAL_AFTER <= latest


def finalize(store: Store, tz: tzinfo) -> int:
    # Persist every day whose d2 has passed, so the history survives snapshot pruning.
    snaps = snapshots(store, tz)
    if not snaps:
        return 0
    done = store.finalized_days()
    latest = snaps[-1].at
    day = snaps[0].at.date()
    cache: dict = {}
    written = 0
    while is_final(day, latest):
        if day not in done:
            store.save_day(compute_day(store, snaps, day, cache))
            written += 1
        day += timedelta(days=1)
    return written


def outcomes(store: Store, tz: tzinfo, start: date, end: date) -> list[tuple[DayOutcome, bool]]:
    # Each day in [start, end], newest first, with whether it is still provisional (computed live
    # because D+2 hasn't ended). Days before the first blocks snapshot are left out.
    snaps = snapshots(store, tz)
    if not snaps:
        return []
    first = snaps[0].at.date()
    cache: dict = {}
    result = []
    day = end
    while day >= max(start, first):
        outcome = store.load_day(day)
        provisional = outcome is None
        if outcome is None:
            outcome = compute_day(store, snaps, day, cache)
        result.append((outcome, provisional))
        day -= timedelta(days=1)
    return result


def history(store: Store, tz: tzinfo, days: int = HISTORY_DAYS) -> dict:
    snaps = snapshots(store, tz)
    if not snaps:
        return {"days": []}
    today = datetime.now(tz).date()
    result = [
        {
            "date": outcome.day,
            "status": outcome.status,
            "provisional": provisional,
            "morning_at": outcome.morning.at if outcome.morning else None,
            "by_kind": summarise(outcome) if outcome.status == "ok" else None,
        }
        for outcome, provisional in outcomes(store, tz, today - timedelta(days=days - 1), today)
    ]
    return {"latest_snapshot_at": snaps[-1].at, "days": result}
