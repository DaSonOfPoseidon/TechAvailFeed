# Records the Python API's responses for a set of requests, at a fixed clock, against a copy of
# the production database, so the .NET API (TechAvail.Parity api) can be checked against them.
# Output stays in the gitignored corpus/: the responses are real feed data.
#
#   uv run python -m tools.api_golden <database_url> <out.json>
import json
import os
import sys
from datetime import timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from fastapi.testclient import TestClient

from api.main import create_app
from feed.store import Store


def paths(filters: dict, today: str, history_start: str) -> list[str]:
    region = filters["regions"][0] if filters["regions"] else "x"
    skill = filters["skills"][0] if filters["skills"] else "x"
    tech = filters["techs"][0]["tech_id"] if filters["techs"] else "x"
    return [
        "/health",
        "/api/v1/filters",
        "/api/v1/calendar",
        f"/api/v1/calendar?start={today}&days=14&calendar=tc",
        f"/api/v1/calendar?days=7&region={region}&skill={skill}",
        f"/api/v1/calendar/{today}",
        f"/api/v1/calendar/{today}?calendar=tc&region={region}",
        "/api/v1/kpis/capacity",
        f"/api/v1/kpis/capacity?days=10&region={region}",
        f"/api/v1/kpis/outcomes?start={history_start}&end={today}",
        f"/api/v1/kpis/outcomes?start={history_start}&end={today}&region={region}",
        f"/api/v1/kpis/outcomes?start={history_start}&end={today}&tech={tech}",
        "/api/v1/diagnostics",
        "/api/v1/diagnostics?group=scheduling",
        "/api/v1/diagnostics?check=double_booked",
        "/api/v1/diagnostics?group=nope",
        "/api/v1/diagnostics?check=nope",
        "/api/v1/map",
        f"/api/v1/map?start={today}&days=7&kind=job",
        f"/api/v1/map?days=3&region={region}",
        "/api/v1/calendar?days=0",
        "/api/v1/calendar?days=63",
        "/api/v1/calendar?calendar=both",
        f"/api/v1/kpis/outcomes?start={today}&end={history_start}",
        "/api/v1/export.xlsx",
        f"/api/v1/export.xlsx?days=7&history_days=10&region={region}&calendar=tc",
    ]


def main(url: str, out: str) -> None:
    store = Store(url)
    # Five minutes after the newest snapshot, so it is fresh and "today" is its day.
    now = store.snapshot_meta()["generated_at"] + timedelta(minutes=5)
    tz = ZoneInfo(os.environ.get("MAIL_TZ", "America/Chicago"))
    client = TestClient(create_app(store=store, tz=tz, api_key="", clock=lambda: now))
    today = now.astimezone(tz).date()
    filters = client.get("/api/v1/filters").json()
    responses = {}
    for path in paths(filters, today.isoformat(), (today - timedelta(days=10)).isoformat()):
        response = client.get(path)
        if path.startswith("/api/v1/export.xlsx"):
            # Saved next to the JSON; tools/compare_xlsx.py compares the two exports.
            name = f"{Path(out).stem}_export{len(responses)}.xlsx"
            (Path(out).parent / name).write_bytes(response.content)
            disposition = response.headers.get("content-disposition")
            responses[path] = {"status": response.status_code, "body": disposition}
            continue
        body = response.json()
        # Only the status of validation errors: FastAPI's detail lists are framework-specific.
        if response.status_code == 422 and not isinstance(body.get("detail"), str):
            body = None
        responses[path] = {"status": response.status_code, "body": body}
    Path(out).write_text(json.dumps({"now": now.isoformat(), "responses": responses}, indent=1))
    print(f"{len(responses)} responses at {now.isoformat()}")


if __name__ == "__main__":
    main(*sys.argv[1:])
