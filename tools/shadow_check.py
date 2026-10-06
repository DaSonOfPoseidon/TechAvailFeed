# Daily check while the .NET API shadows the Python one: the live services must answer every
# request in tools/api_golden.py identically, and the .NET ingest must be polling and storing
# every delivery. Appends one entry per run; prints only paths and counts, never feed values.
#
#   uv run python -m tools.shadow_check >> corpus/shadow-check.log
import json
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from datetime import UTC, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from tools.api_golden import paths
from tools.compare_xlsx import describe
from tools.replay import diff

PYTHON_API = "http://localhost:8097"
DOTNET_API = "http://localhost:8098"
INGEST = "http://localhost:8095"
CONTAINERS = ["techavailfeed-ingest-1", "techavailfeed-api-1", "techavailfeed-api-net-1"]
TZ = ZoneInfo("America/Chicago")
# The feed comes every 15 minutes, around the clock.
EXPECTED_PER_DAY = 96


def api_key() -> str:
    env = Path(__file__).resolve().parent.parent / ".env"
    for line in env.read_text().splitlines() if env.exists() else []:
        if line.startswith("API_KEY="):
            return line.split("=", 1)[1].strip()
    return ""


def get(base: str, path: str, key: str) -> tuple[int, bytes]:
    request = urllib.request.Request(base + path, headers={"X-API-Key": key} if key else {})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()


def comparable(path: str, status: int, body: bytes):
    if path.startswith("/api/v1/export.xlsx"):
        with tempfile.NamedTemporaryFile(suffix=".xlsx") as file:
            file.write(body)
            file.flush()
            # The export time is the wall clock.
            return status, describe(file.name, {"About!B1"}) if status == 200 else None
    return status, without_age(json.loads(body))


def framework_validation(a, b) -> bool:
    # Only the status of validation errors: FastAPI's detail lists are framework-specific, and
    # .NET answers the same 422 with a one-line detail.
    return a[0] == b[0] == 422 and any(
        isinstance(x[1], dict) and isinstance(x[1].get("detail"), list) for x in (a, b)
    )


def without_age(value):
    # The snapshot's age depends on the moment each side was asked.
    if isinstance(value, dict):
        return {k: without_age(v) for k, v in value.items() if k != "age_min"}
    if isinstance(value, list):
        return [without_age(v) for v in value]
    return value


def compare_apis(key: str) -> tuple[int, list[str]]:
    today = datetime.now(TZ).date()
    filters = json.loads(get(PYTHON_API, "/api/v1/filters", key)[1])
    requests = paths(filters, today.isoformat(), (today - timedelta(days=10)).isoformat())
    problems = []
    for path in requests:
        # Asked twice on a difference: a new snapshot can land between the two requests.
        for _ in range(2):
            a = comparable(path, *get(PYTHON_API, path, key))
            b = comparable(path, *get(DOTNET_API, path, key))
            if a == b or framework_validation(a, b):
                break
        else:
            found = ["status"] if a[0] != b[0] else diff(a[1], b[1])
            problems.append(f"{path}: {len(found)} differ, first {', '.join(found[:5])}")
    return len(requests), problems


def check_ingest(key: str, now: datetime) -> tuple[str, list[str]]:
    problems = []
    health = json.loads(get(INGEST, "/health", key)[1])
    if health.get("last_poll_error"):
        problems.append("ingest: last poll failed")
    polled = health.get("last_poll_at")
    if not polled or now - datetime.fromisoformat(polled) > timedelta(minutes=5):
        problems.append(f"ingest: last poll at {polled}")
    runs = json.loads(get(INGEST, "/runs.json", key)[1])["runs"]
    day = [r for r in runs if now - datetime.fromisoformat(r["ingested_at"]) < timedelta(days=1)]
    bad = [r for r in day if r["status"] != "ok"]
    if bad:
        problems.append(f"ingest: {len(bad)} non-ok runs, ids {[r['id'] for r in bad[:10]]}")
    if len(day) < EXPECTED_PER_DAY - 4:
        problems.append(f"ingest: only {len(day)} runs in 24 h (expected ~{EXPECTED_PER_DAY})")
    return f"{len(day)} runs in 24 h, {len(bad)} non-ok", problems


def check_containers() -> list[str]:
    problems = []
    for name in CONTAINERS:
        found = subprocess.run(
            ["docker", "inspect", "-f", "{{.State.Health.Status}} {{.RestartCount}}", name],
            capture_output=True,
            text=True,
        )
        health, _, restarts = found.stdout.strip().partition(" ")
        if found.returncode or health != "healthy" or restarts != "0":
            problems.append(f"{name}: {health or 'missing'}, {restarts or '?'} restarts")
    return problems


def main() -> int:
    now = datetime.now(UTC)
    key = api_key()
    problems = check_containers()
    try:
        runs, ingest_problems = check_ingest(key, now)
        problems += ingest_problems
        requests, api_problems = compare_apis(key)
        problems += api_problems
        summary = f"{requests} requests compared, {len(api_problems)} differ; {runs}"
    except Exception as exc:
        summary = "check failed"
        problems.append(f"{type(exc).__name__}: {exc}")
    print(f"{now.astimezone(TZ):%Y-%m-%d %H:%M} {'PROBLEMS' if problems else 'OK'}: {summary}")
    for problem in problems:
        print(f"  {problem}")
    sys.stdout.flush()
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
