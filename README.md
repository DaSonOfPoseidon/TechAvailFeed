# TechAvailFeed

Near-real-time field technician availability, built on a scheduling system whose only export is a
scheduled report emailed as CSV.

An ingest service polls a mailbox over IMAP, parses each CSV into Postgres and tracks how fresh the data is.
A REST API turns the snapshots into a 30-day capacity calendar, KPI series, an outcome history (what happened
to the work planned for each day), data-quality diagnostics, a map feed and Excel workbooks for each.

All sample data in this repository is fictional.

## Architecture

```
scheduling system ──scheduled report (CSV by email)──▶ mailbox ──IMAP poll──▶ ingest ──▶ Postgres ◀── api (REST)
                                                                                │
                                                                                └─▶ /health, /runs.json (latency)
```

| Service | Path | Port | Role |
|---|---|---|---|
| `ingest` | `src/TechAvail.Ingest/` | 8095 | Polls IMAP, parses and stores snapshots, records delivery latency |
| `api` | `src/TechAvail.Api/` | 8097 | Read-only REST API: calendar, KPIs, outcomes, diagnostics, map, Excel exports |
| `postgres` | | (internal) | Storage |

The domain rules (free time, outcomes, diagnostics, arrivals, jeopardy) live in `src/TechAvail.Core`, with no
I/O, not in SQL or in the API layer. That keeps them unit-testable without a database.

## Design highlights

- **Replayable history with diff-only writes.** Each distinct row is stored once, with the snapshot where it
  first appeared and the one where it disappeared. A run writes only what changed, and any past snapshot can be
  rebuilt exactly. That makes it possible to measure feed latency and reliability after the fact.
- **Safe ingestion.** Duplicate deliveries are skipped by `Message-ID`. A file that fails to parse is archived
  with the reason. An empty or failed run never replaces the last good snapshot in anything user-facing.
- **Mailbox hygiene.** Processed mail is labelled, then deleted the next day. Failed mail is kept for inspection.
  With `ARCHIVE_DIR` set, a raw copy of each processed mail is kept locally (`corpus/mail`, never committed).
- **Freshness is part of every response.** Each API response includes the snapshot it was computed from and
  its age, and is flagged `stale` when deliveries stop.
- **Cached reads, invalidated by the ingest.** A snapshot's rows never change once committed, so the API caches
  them in memory by snapshot id (`CachedFeedReads`). The ingest sends `NOTIFY feed_changed` when a snapshot or
  finalized day commits, and the API's listener (`FeedChanges`) switches to it. Warm requests make no database
  queries. Responses themselves aren't cached, because several depend on the current time. `/health` always
  asks Postgres. While the listener is down, reads go straight to Postgres until it reconnects.
- **Location privacy.** Job coordinates are stored exactly but served rounded to about 110 m. The Excel exports
  never include them, and the feed carries no address text.
- **Format evolution.** New columns are optional, so older exports still parse. The parser recognises the
  format from the header row. See [`docs/feed-format.md`](docs/feed-format.md).

## API

The OpenAPI schema is served at `/openapi.json`. When `API_KEY` is set, `/api/v1/*` requires an `X-API-Key`
header. Errors are `{"detail": "..."}`, with 422 for bad query values.

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
| `GET /api/v1/capacity.xlsx` | The calendar as a workbook (same filters as `/calendar`): capacity per day and region, tech days, free slots, the schedule behind them and unassigned work |
| `GET /api/v1/outcomes.xlsx` | The outcome history as a workbook (same filters as `/kpis/outcomes`): outcomes per day, and per planned job |
| `GET /api/v1/diagnostics.xlsx` | The data-quality checks as a workbook (same filters as `/diagnostics`), one row per finding |
| `GET /api/v1/arrivals.xlsx` | On-time arrival workbook for `date` (default today): the 8:00 jobs as of the 8:15 run, the day so far, or a past day's completed jobs, highlighted en route (yellow) / not started (red). |
| `GET /api/v1/jeopardy.xlsx` | The VP's jobs-in-jeopardy status update (sent at 10 AM, 1 PM, 3 PM and 5 PM). `at=HH:mm` reads that time's run (default: the day's latest); `date`, and `region`, which is a VP region. One sheet holds Green/Yellow/Red per VP region and the areas/techs of concern (job in jeopardy, previous job going long, a slot with more jobs than techs), with a blank *Actions Taking* column for the dispatcher. Rules: `src/TechAvail.Core/StatusUpdate.cs`. The area to VP region map follows MBSReporter's multiregion rules. **Hannibal-Bowling Green → STL West and Carrollton → West are inferred and still need the VP to confirm them.** |

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

The .NET SDK runs in Docker, so the host needs only Docker:

```
scripts/test-db.sh up                          # throwaway Postgres for the data tests
scripts/dotnet.sh build
scripts/dotnet.sh test TechAvailFeed.slnx -m:1 # one project at a time keeps the SDK container under 1 GiB
scripts/dotnet.sh format TechAvailFeed.slnx --verify-no-changes
docker compose up -d --build ingest api        # deploy a change; the source is baked into the images
```

```
TechAvailFeed.slnx
src/TechAvail.Core/          parsing, sender check and domain rules (no I/O)
src/TechAvail.Data/          Postgres: DbUp migrations, the store, outcome history
src/TechAvail.Api/           the dashboard API and its Excel exports (Xlsx.cs and AboutSheet.cs are shared)
src/TechAvail.Ingest/        the ingest worker
Dockerfile                   one image per project, chosen with the PROJECT build arg
tests/TechAvail.*.Tests/     xUnit (data tests need scripts/test-db.sh up)
tests/fixtures/              fake feed files (.csv) and what the parser makes of them (.json)
tools/TechAvail.ImapCheck/   read-only check of the mailbox code against the live mailbox
scripts/dotnet.sh            runs the .NET SDK in Docker
```

The fixture snapshots cover edge and error cases. After an intended parser change, rewrite them with
`UPDATE_SNAPSHOTS=1 scripts/dotnet.sh test tests/TechAvail.Core.Tests` and review the diff.
`scripts/imap-check.sh` fetches every processed mail still in the mailbox (EXAMINE and BODY.PEEK only) and
compares it with the archived copy in `corpus/mail`. The corpus never leaves the machine, because it holds real
schedules and locations.

## History

The first version was Python (FastAPI and a polling worker), tagged `v0-python`. It was rewritten in C#/.NET
strangler-style on the same Postgres schema, so no data was migrated. Each part ran side by side with Python
until its output was identical on the real feed corpus, a replay of every archived mail, the outcome history
and every API response on a production copy. The .NET ingest took over on 2026-10-06 and the .NET API on
2026-10-07, after the daily shadow checks (2026-10-06 and 2026-10-07) found no differences. The Python code was then removed, and the
C# that existed only to reproduce Python's exact formatting was replaced with .NET's own parsers and
serialisation.

### Status

- [x] Feed parser, mail reading and sender check
- [x] Data layer (DbUp baseline, diff-only writes, reads)
- [x] Availability, outcome history, diagnostics, capacity and KPIs
- [x] REST API and Excel exports
- [x] Ingest worker (MailKit)
- [x] Python retired
- [ ] Angular dashboard

### Performance

Before the cutover, both APIs were measured live, side by side on the same database and snapshot
(2026-10-06, 24-core host, 1 GiB container cap). Single requests are medians of 9, after a warm-up.

| | Python (FastAPI) | .NET |
|---|---|---|
| `/calendar` | 123 ms | 57 ms |
| `/kpis/outcomes` | 362 ms | 58 ms |
| `/export.xlsx` (since split into capacity/outcomes/diagnostics) | 1.76 s | 0.43 s |
| 40 `/kpis/outcomes`, 10 at a time | 3.86 s | 0.67 s |
| 12 exports, 3 at a time | 21.5 s | 2.3 s |
| Memory after that load | 183 MiB | 184 MiB |

The gap widens under concurrency because Python's GIL serialises the CPU-bound work.

The .NET services use workstation GC (`Directory.Build.props`), which costs some speed under bursts. The web
SDK's default server GC keeps a heap per core and settled at 326–357 MiB after the same load. With workstation
GC the services settle at about 165 MiB, but concurrent bursts take 1.2–2× as long: 0.71–0.76 s against
0.38–0.64 s for the 40 requests, and 2.3–2.4 s against 1.7–2.1 s for the 12 exports. Single requests showed no
difference beyond run-to-run noise. Tuning server GC (`GCConserveMemory`, a 16 or 32 MB gen0 budget, 4 heaps)
saved at most 15%. For a dashboard with a few users, memory matters more than burst throughput. If that
changes, removing `ServerGarbageCollection` restores server GC.

## License

[MIT](LICENSE)
