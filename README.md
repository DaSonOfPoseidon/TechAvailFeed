# TechAvailFeed

Near-real-time field technician availability, built on a scheduling system whose only export is a
scheduled report emailed as CSV.

An ingest service polls a mailbox over IMAP, parses each CSV into Postgres and tracks how fresh the data is.
A REST API turns the snapshots into a 30-day capacity calendar, KPI series, an outcome history (what happened
to the work planned for each day), data-quality diagnostics, a map feed and an Excel export.

All sample data in this repository is fictional.

## Architecture

```
scheduling system ──scheduled report (CSV by email)──▶ mailbox ──IMAP poll──▶ ingest ──▶ Postgres ◀── api (REST)
                                                                                │
                                                                                └─▶ /health, /runs.json (latency)
```

| Service | Path | Port | Role |
|---|---|---|---|
| `ingest` | `feed/` | 8095 | Polls IMAP, parses and stores snapshots, records delivery latency |
| `api` | `api/` | 8097 | Read-only REST API: calendar, KPIs, outcomes, diagnostics, map, Excel export |
| `postgres` | | (internal) | Storage |

The domain rules (free time, outcomes, diagnostics) live in plain Python modules under `feed/`, not in SQL
or in the API layer. That keeps them unit-testable without a database.

## Design highlights

- **Replayable history with diff-only writes.** Each distinct row is stored once, with the snapshot where it
  first appeared and the one where it disappeared. A run writes only what changed, and any past snapshot can be
  rebuilt exactly. That makes it possible to measure feed latency and reliability after the fact.
- **Safe ingestion.** Duplicate deliveries are skipped by `Message-ID`. A file that fails to parse is archived
  with the reason. An empty or failed run never replaces the last good snapshot in anything user-facing.
- **Mailbox hygiene.** Processed mail is labelled, then deleted the next day. Failed mail is kept for inspection.
  With `ARCHIVE_DIR` set, a raw copy of each processed mail is kept locally for replay and parity tests.
- **Freshness is part of every response.** Each API response includes the snapshot it was computed from and
  its age, and is flagged `stale` when deliveries stop.
- **Location privacy.** Job coordinates are stored exactly but served rounded to about 110 m. The Excel export
  never includes them, and the feed carries no address text.
- **Format evolution.** New columns are optional, so older exports still parse. The parser recognises the
  format from the header row. See [`docs/feed-format.md`](docs/feed-format.md).

## API

Interactive docs are served at `/docs` and the OpenAPI schema at `/openapi.json`. When `API_KEY` is set,
`/api/v1/*` requires an `X-API-Key` header.

| Endpoint | Returns |
|---|---|
| `GET /health` | Service status and snapshot age (no auth) |
| `GET /api/v1/filters` | Regions, skills and technicians for filter dropdowns |
| `GET /api/v1/calendar` | Capacity per day and region: available, booked, free and unassigned hours, utilisation |
| `GET /api/v1/calendar/{date}` | One day per technician: shifts, time off, booked work, free slots |
| `GET /api/v1/kpis/capacity` | Daily capacity series plus totals per region |
| `GET /api/v1/kpis/outcomes` | Completion, cancellation and reschedule rates per day, region and technician |
| `GET /api/v1/diagnostics` | Data-quality checks: double bookings, work outside shifts, stale open work, setup gaps |
| `GET /api/v1/map` | Jobs as map points with rounded coordinates |
| `GET /api/v1/export.xlsx` | All of the above as a multi-sheet workbook |

Most endpoints take `start`, `days`, `region` and `skill` query parameters.

The ingest service also exposes `/latest.json` (the newest snapshot with computed free slots), `/history.json`
and `/runs.json`. `/runs.json` gives per-run latency split into source → mailbox → ingest.

## Setup

```
cp .env.example .env    # set POSTGRES_PASSWORD, IMAP_USER, IMAP_PASSWORD, MAIL_SUBJECT
docker compose up -d --build
curl localhost:8095/health   # mail_configured: true, no last_poll_error
curl localhost:8097/health
```

Then schedule the source report to email its CSV to the mailbox, with a subject that matches `MAIL_SUBJECT`.
To use an existing Postgres, set `DATABASE_URL` and drop the `postgres` service; the schema is created on
start. See `.env.example` for all the settings.

## Development

```
uv sync
uv run pytest && uv run ruff check . && uv run black --check .
```

## .NET rewrite (`dotnet` branch)

The Python implementation is tagged `v0-python`. This branch rewrites it in C#/.NET: an ASP.NET Core REST
API and a worker service, using Dapper and SQL-first migrations on the same Postgres schema, with an
Angular/TypeScript dashboard. Python keeps running in production until each part is proven identical. The port
is strangler-style: the same database is shared, so no data migration is needed.

```
TechAvailFeed.slnx
src/TechAvail.Core/          parsing, sender check and domain rules (no I/O)
src/TechAvail.Data/          Postgres: DbUp migrations, the store, outcome history
tests/TechAvail.*.Tests/     xUnit (data tests need scripts/test-db.sh up)
tools/TechAvail.Parity/      compares .NET output with the Python golden files
contract/golden/fixtures/    Python's output for the fake fixtures in tests/fixtures/
scripts/dotnet.sh            runs the .NET SDK in Docker, so the host needs no SDK
```

### Status

- [x] Solution scaffold, Python-compatible text helpers and strict CSV reader
- [x] Feed parser (`feed/parse.py`), identical to Python on every mail in the real feed corpus
- [x] Mail reading and sender check (`feed/mail.py`)
- [x] Parity tool and CI
- [x] Data layer (DbUp baseline, diff-only writes, reads), identical to Python when the corpus is replayed
- [x] Availability and outcome history, identical to Python on the corpus and a production copy
- [x] Diagnostics, capacity and KPIs
- [x] REST API (every endpoint and the Excel export identical to Python on a production copy)
- [x] Ingest worker (MailKit), checked read-only against the live mailbox
- [ ] Dockerfiles, compose services and the cutover from the Python ingest
- [ ] Angular dashboard
- [ ] Retire the Python services

### Parity testing

The Python code is the reference. `tools/golden.py` records what it makes of each input, and the .NET code
has to produce identical output:

- **Fake fixtures** (`tests/fixtures/*.csv`, including edge and error cases): their golden files are committed
  and checked by `dotnet test`. To regenerate them, run `uv run python -m tools.golden --fixtures`.
- **Real feed mail**: the ingest keeps a copy of each processed mail when `ARCHIVE_DIR` is set, and
  `tools/export_corpus.py` copies whatever is still in the mailbox. That corpus and its golden files live in the
  gitignored `corpus/` and never leave the machine, because they hold real schedules and locations.
  `tools/golden.py` also cross-checks every mail against the snapshot production stored for it.

```
scripts/dotnet.sh build
scripts/dotnet.sh test
scripts/dotnet.sh format --verify-no-changes
```

To check the real corpus, regenerate the golden files with the Python code, then run the parity tool with the
feed's sender address:

```
docker compose run --rm --no-deps -v $PWD/tools:/app/tools -v $PWD/corpus:/app/corpus ingest python -m tools.golden
MAIL_FROM=<sender> scripts/dotnet.sh run --project tools/TechAvail.Parity -- corpus
```

The tool prints counts and the JSON paths that differ, never values, and exits non-zero on any difference.

Two more checks run against a throwaway Postgres (`scripts/test-db.sh up`):

- `scripts/replay-parity.sh` replays the corpus through both ingests into empty databases and compares
  `snapshots`, `blocks` and `slots` row by row, ids included.
- `scripts/history-parity.sh` copies the production database (read-only) twice, finalizes the outcome history
  with each implementation and compares `outcome_days`, `job_outcomes` and every day's computed outcome.

## License

[MIT](LICENSE)
