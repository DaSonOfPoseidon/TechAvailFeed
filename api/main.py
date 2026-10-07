import os
import secrets
from collections.abc import Callable
from datetime import UTC, date, datetime, timedelta, tzinfo
from typing import Annotated
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, FastAPI, HTTPException, Query, Security
from fastapi.middleware.cors import CORSMiddleware
from fastapi.security import APIKeyHeader

from api.capacity import calendar_entries, filter_days, region_totals, unassigned_work
from api.kpis import outcome_kpis
from feed.availability import CALENDARS, NOT_BUSY, TechDay, tech_days
from feed.coords import public_coords
from feed.diagnostics import GROUPS, Check, calendar_of, diagnose
from feed.history import outcomes
from feed.parse import Block
from feed.store import Store

# Dashboard API: JSON for the calendar and KPI charts. Read-only; the
# ingest service writes everything. Any web page or server calls it over HTTP.

# MBS sends every 15 minutes, so three missed runs means the numbers are going stale.
STALE_MINUTES = 45
MAX_DAYS = 62
MAX_HISTORY_DAYS = 366
MAP_KINDS = ("job", "ticket", "job_unassigned", "ticket_unassigned")

Region = Annotated[str | None, Query(description="Exact region name; omit for all")]
Skill = Annotated[str | None, Query(description="One skill code, e.g. INS; omit for all")]
Calendar = Annotated[
    str,
    Query(
        pattern="^(install|tc)$",
        description="install (FIELD schedules, incl. dual FIELD+TC) or tc (TC-only schedules)",
    ),
]


def snapshot_info(meta: dict, now: datetime) -> dict:
    generated = meta["generated_at"]
    age = round((now - generated).total_seconds() / 60, 1) if generated else None
    return {
        "id": meta["id"],
        "generated_at": generated,
        "age_min": age,
        "stale": age is None or age > STALE_MINUTES,
    }


def work_row(block: Block) -> dict:
    return {
        "kind": block.kind,
        "ref_id": block.ref_id,
        "status": block.status,
        "task_type": block.task_type,
        "region": block.region,
        "starts_at": block.starts_at,
        "ends_at": block.ends_at,
        "address_issue": block.address_issue,
    }


def tech_detail(day: TechDay) -> dict:
    return {
        "tech_id": day.tech_id,
        "tech_name": day.tech_name,
        "region": day.region,
        "skills": day.skills,
        "shift_h": day.shift_hours,
        "lunch_h": day.lunch_hours,
        "time_off_h": day.time_off_hours,
        "available_h": day.available_hours,
        "booked_h": day.booked_hours,
        "free_h": day.free_hours,
        "jobs": day.jobs,
        "tickets": day.tickets,
        "on_time_off": day.on_time_off,
        "shifts": [{"start": s, "end": e} for s, e in day.shifts],
        "time_off": [{"start": s, "end": e} for s, e in day.time_off],
        "work": [work_row(b) for b in day.work],
        "free": [
            {"open_from": s.open_from, "open_until": s.open_until, "open_minutes": s.open_minutes}
            for s in day.free
        ],
    }


def check_summary(check: Check) -> dict:
    return {
        "id": check.id,
        "group": check.group,
        "title": check.title,
        "severity": check.severity,
        "description": check.description,
        "available": check.available,
        "count": len(check.rows),
    }


def map_points(
    blocks: list[Block], start: date, end: date, region: str | None, kind: str | None, exact: bool
) -> tuple[list[dict], list[dict]]:
    # Live jobs and tickets in [start, end], one point per job (a two-tech job lists both techs).
    # Work takes its address's region; without one, the tech's shift region that day.
    regions = {(b.tech_id, b.work_date): b.region for b in blocks if b.kind == "shift_tc"}
    regions |= {(b.tech_id, b.work_date): b.region for b in blocks if b.kind == "shift"}
    found: dict[tuple[str, str], dict] = {}
    for b in sorted(blocks, key=lambda b: (b.starts_at, b.tech_name)):
        base = b.kind.removesuffix("_unassigned")
        if b.kind not in MAP_KINDS or not start <= b.work_date <= end:
            continue
        if b.status in NOT_BUSY.get(base, ()) or (kind is not None and base != kind):
            continue
        place = b.region or regions.get((b.tech_id, b.work_date), "")
        if region is not None and place != region:
            continue
        key = (base, b.ref_id)
        if key in found:
            if b.tech_id:
                found[key]["techs"].append({"tech_id": b.tech_id, "tech_name": b.tech_name})
            continue
        lat, lon = public_coords(b.latitude, b.longitude, exact)
        found[key] = {
            "ref_id": b.ref_id,
            "kind": base,
            "assigned": b.kind in ("job", "ticket"),
            "status": b.status,
            "work_date": b.work_date,
            "starts_at": b.starts_at,
            "ends_at": b.ends_at,
            "region": place,
            "techs": [{"tech_id": b.tech_id, "tech_name": b.tech_name}] if b.tech_id else [],
            "lat": lat,
            "lon": lon,
            "gps_precision": b.gps_precision,
            "address_issue": b.address_issue,
        }
    points = [p for p in found.values() if p["lat"] is not None]
    unmapped = [p for p in found.values() if p["lat"] is None]
    for p in unmapped:
        del p["lat"], p["lon"]
    return points, unmapped


def create_app(
    store: Store | None = None,
    tz: tzinfo | None = None,
    api_key: str | None = None,
    cors_origins: list[str] | None = None,
    exact_coords: bool | None = None,
    clock: Callable[[], datetime] = lambda: datetime.now(UTC),
) -> FastAPI:
    # uvicorn --factory api.main:create_app builds it from the environment; tests pass fakes.
    env = os.environ
    store = store or Store(env["DATABASE_URL"])
    tz = tz or ZoneInfo(env.get("MAIL_TZ", "America/Chicago"))
    api_key = env.get("API_KEY", "") if api_key is None else api_key
    if exact_coords is None:
        # Off by default: exact coordinates locate a customer's home (feed/coords.py).
        exact_coords = env.get("EXACT_COORDS", "").strip().lower() in ("1", "true", "yes")
    if cors_origins is None:
        cors_origins = [o.strip() for o in env.get("CORS_ORIGINS", "").split(",") if o.strip()]

    def local_now() -> datetime:
        # Naive MBS local time, like the block timestamps.
        return clock().astimezone(tz).replace(tzinfo=None)

    def check_key(
        key: Annotated[str | None, Security(APIKeyHeader(name="X-API-Key", auto_error=False))],
    ):
        if api_key and not (key and secrets.compare_digest(key, api_key)):
            raise HTTPException(401, "missing or wrong X-API-Key")

    def latest() -> tuple[list[Block], dict]:
        found = store.latest()
        if found is None or "blocks" not in found:
            raise HTTPException(404, "no blocks snapshot yet")
        return found["blocks"], found["snapshot"]

    def info(meta: dict | None) -> dict | None:
        return snapshot_info(meta, clock()) if meta else None

    def window(start: date | None, days: int) -> tuple[date, date]:
        start = start or local_now().date()
        return start, start + timedelta(days=days - 1)

    def capacity(blocks, start, end, region, skill, calendar):
        found = tech_days(blocks, local_now(), start=start, end=end, calendar=calendar)
        days = filter_days(found, region, skill)
        demand = unassigned_work(blocks, start, end, region, calendar)
        return days, demand, calendar_entries(days, demand, start, end)

    def history_window(start: date | None, end: date | None) -> tuple[date, date]:
        end = end or local_now().date()
        start = start or end - timedelta(days=29)
        if start > end or (end - start).days >= MAX_HISTORY_DAYS:
            raise HTTPException(422, f"need start <= end, at most {MAX_HISTORY_DAYS} days")
        return start, end

    app = FastAPI(
        title="TechAvailFeed API",
        description="30-day tech capacity calendar and KPIs from the MBS feed.",
        version="1.0",
    )
    if cors_origins:
        app.add_middleware(
            CORSMiddleware,
            allow_origins=cors_origins,
            allow_methods=["GET"],
            allow_headers=["X-API-Key"],
            expose_headers=["Content-Disposition"],
        )

    @app.get("/health")
    def health() -> dict:
        # No auth: the monitoring probe. 200 even before the first snapshot.
        meta = store.snapshot_meta()
        return {"ok": True, "snapshot": info(meta)}

    v1 = APIRouter(prefix="/api/v1", dependencies=[Depends(check_key)])

    @v1.get("/filters")
    def filters() -> dict:
        blocks, meta = latest()
        techs: dict[str, dict] = {}
        kinds: dict[str, set] = {}
        skills: set[str] = set()
        regions: set[str] = set()
        # Install shifts first, so a tech with both lists their install region.
        for b in sorted(blocks, key=lambda b: b.kind != "shift"):
            if b.kind in CALENDARS.values():
                techs.setdefault(
                    b.tech_id, {"tech_id": b.tech_id, "tech_name": b.tech_name, "region": b.region}
                )
                kinds.setdefault(b.tech_id, set()).add(b.kind)
                skills.update(s.strip() for s in b.skills.split(",") if s.strip())
            if b.region:
                regions.add(b.region)
        for tech_id, tech in techs.items():
            tech["calendar"] = calendar_of(kinds[tech_id])
        return {
            "snapshot": info(meta),
            "regions": sorted(regions),
            "skills": sorted(skills),
            "techs": sorted(techs.values(), key=lambda t: (t["tech_name"], t["tech_id"])),
        }

    @v1.get("/calendar")
    def calendar_range(
        start: date | None = None,
        days: Annotated[int, Query(ge=1, le=MAX_DAYS)] = 30,
        region: Region = None,
        skill: Skill = None,
        calendar: Calendar = "install",
    ) -> dict:
        blocks, meta = latest()
        start, end = window(start, days)
        _, _, entries = capacity(blocks, start, end, region, skill, calendar)
        return {
            "snapshot": info(meta),
            "filters": {"region": region, "skill": skill, "calendar": calendar},
            "start": start,
            "end": end,
            "days": entries,
        }

    @v1.get("/calendar/{day}")
    def calendar_day(
        day: date, region: Region = None, skill: Skill = None, calendar: Calendar = "install"
    ) -> dict:
        blocks, meta = latest()
        days, demand, [entry] = capacity(blocks, day, day, region, skill, calendar)
        return {
            "snapshot": info(meta),
            "filters": {"region": region, "skill": skill, "calendar": calendar},
            **entry,
            "techs": [tech_detail(d) for d in days],
            "unassigned": [work_row(b) for b in demand],
        }

    @v1.get("/kpis/capacity")
    def capacity_kpis(
        start: date | None = None,
        days: Annotated[int, Query(ge=1, le=MAX_DAYS)] = 30,
        region: Region = None,
        skill: Skill = None,
        calendar: Calendar = "install",
    ) -> dict:
        blocks, meta = latest()
        start, end = window(start, days)
        _, _, entries = capacity(blocks, start, end, region, skill, calendar)
        return {
            "snapshot": info(meta),
            "filters": {"region": region, "skill": skill, "calendar": calendar},
            "series": [{"date": e["date"], **e["totals"]} for e in entries],
            "by_region": region_totals(entries),
        }

    @v1.get("/kpis/outcomes")
    def outcome_series(
        start: date | None = None,
        end: date | None = None,
        region: Region = None,
        tech: Annotated[str | None, Query(description="tech_id; omit for all")] = None,
    ) -> dict:
        start, end = history_window(start, end)
        meta = store.snapshot_meta()
        return {
            "snapshot": info(meta),
            "filters": {"region": region, "tech": tech},
            "start": start,
            "end": end,
            **outcome_kpis(outcomes(store, tz, start, end), region, tech),
        }

    @v1.get("/diagnostics")
    def diagnostics(
        group: Annotated[str | None, Query(description=f"One of {', '.join(GROUPS)}")] = None,
        check: Annotated[str | None, Query(description="One check id")] = None,
    ) -> dict:
        blocks, meta = latest()
        found = diagnose(blocks, local_now().date())
        if group is not None and group not in GROUPS:
            raise HTTPException(422, f"unknown group {group!r}; use one of {', '.join(GROUPS)}")
        if check is not None and check not in {c.id for c in found}:
            raise HTTPException(422, f"unknown check {check!r}")
        found = [
            c
            for c in found
            if (group is None or c.group == group) and (check is None or c.id == check)
        ]
        return {
            "snapshot": info(meta),
            "summary": [check_summary(c) for c in found],
            "checks": [{**check_summary(c), "rows": c.rows} for c in found],
        }

    @v1.get("/map")
    def map_view(
        start: date | None = None,
        days: Annotated[int, Query(ge=1, le=MAX_DAYS)] = 1,
        region: Region = None,
        kind: Annotated[str | None, Query(pattern="^(job|ticket)$")] = None,
    ) -> dict:
        blocks, meta = latest()
        start, end = window(start, days)
        points, unmapped = map_points(blocks, start, end, region, kind, exact_coords)
        return {
            "snapshot": info(meta),
            "filters": {"region": region, "kind": kind},
            "start": start,
            "end": end,
            "exact": exact_coords,
            "points": points,
            "unmapped": unmapped,
        }

    app.include_router(v1)
    return app
