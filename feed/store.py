from collections import Counter
from dataclasses import astuple, fields
from datetime import datetime

import psycopg
from psycopg.rows import dict_row

from feed.outcomes import DayOutcome, Planned, Snapshot
from feed.parse import Block, ParsedFeed

SCHEMA = """
CREATE TABLE IF NOT EXISTS snapshots (
	id BIGSERIAL PRIMARY KEY
,	message_id TEXT NOT NULL UNIQUE
,	channel TEXT NOT NULL
,	subject TEXT
,	filename TEXT
,	generated_at TIMESTAMPTZ
,	email_date TIMESTAMPTZ
,	mailbox_received_at TIMESTAMPTZ
,	ingested_at TIMESTAMPTZ NOT NULL DEFAULT now()
,	row_count INTEGER NOT NULL DEFAULT 0
,	sha256 TEXT
,	status TEXT NOT NULL CHECK (status IN ('ok', 'empty', 'error'))
,	error TEXT
);

CREATE TABLE IF NOT EXISTS slots (
	snapshot_id BIGINT NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE
,	work_date DATE NOT NULL
,	tech_id TEXT NOT NULL
,	tech_name TEXT NOT NULL
,	open_from TIMESTAMP NOT NULL
,	open_until TIMESTAMP NOT NULL
,	open_minutes INTEGER NOT NULL
,	region TEXT
,	skills TEXT
);

CREATE INDEX IF NOT EXISTS slots_snapshot_idx ON slots (snapshot_id);

ALTER TABLE snapshots ADD COLUMN IF NOT EXISTS format TEXT NOT NULL DEFAULT 'slots';

-- blocks stores each distinct row once, present from first_snapshot_id until closed_snapshot_id
-- (the next blocks snapshot it was missing from; NULL while current), so every run is still
-- exactly reconstructable. Until 10-05 it held a full copy per snapshot (snapshot_id); that table
-- is renamed blocks_legacy and backfilled below. Drop it by hand once the backfill is verified.
DO $$
BEGIN
	IF EXISTS (
		SELECT 1 FROM information_schema.columns
		WHERE table_name = 'blocks' AND column_name = 'snapshot_id'
	) THEN
		ALTER TABLE blocks RENAME TO blocks_legacy;
		ALTER INDEX blocks_snapshot_idx RENAME TO blocks_legacy_snapshot_idx;
	END IF;
END $$;

CREATE TABLE IF NOT EXISTS blocks (
	id BIGSERIAL PRIMARY KEY
,	first_snapshot_id BIGINT NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE
,	closed_snapshot_id BIGINT REFERENCES snapshots (id)
,	kind TEXT NOT NULL CHECK (
		kind IN (
			'shift', 'shift_tc', 'job', 'ticket', 'time_off', 'job_moved', 'ticket_moved',
			'job_unassigned', 'ticket_unassigned'
		)
	)
,	work_date DATE NOT NULL
,	tech_id TEXT NOT NULL
,	tech_name TEXT NOT NULL
,	starts_at TIMESTAMP NOT NULL
,	ends_at TIMESTAMP NOT NULL
,	ref_id TEXT
,	status TEXT
,	department TEXT
,	region TEXT
,	skills TEXT
,	task_type TEXT
,	modified_at TIMESTAMP
,	modified_by TEXT
,	enroute_at TIMESTAMP
,	inprogress_at TIMESTAMP
,	prereqs_status TEXT
-- The service address as a flag and exact coordinates. Never served unrounded by default.
,	address_issue TEXT
,	latitude DOUBLE PRECISION
,	longitude DOUBLE PRECISION
,	gps_precision TEXT
);

CREATE INDEX IF NOT EXISTS blocks_open_idx ON blocks (first_snapshot_id)
	WHERE closed_snapshot_id IS NULL;
CREATE INDEX IF NOT EXISTS blocks_range_idx ON blocks (first_snapshot_id, closed_snapshot_id);

-- The rows of the newest blocks snapshot, for reading in psql.
CREATE OR REPLACE VIEW current_blocks AS
SELECT * FROM blocks WHERE closed_snapshot_id IS NULL;

-- One-time backfill: each run of consecutive blocks snapshots holding the same row (gaps and
-- islands, per copy of the row within a snapshot) becomes one blocks row.
DO $$
BEGIN
	IF to_regclass('blocks_legacy') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM blocks) THEN
		WITH snaps AS (
			SELECT
				id
			,	row_number() OVER (ORDER BY id) AS seq
			,	lead(id) OVER (ORDER BY id) AS next_id
			FROM snapshots
			WHERE status = 'ok' AND format = 'blocks'
		)
		, keyed AS (
			SELECT
				b.*
			,	s.seq
			,	md5(ROW(
					b.kind, b.work_date, b.tech_id, b.tech_name, b.starts_at, b.ends_at, b.ref_id,
					b.status, b.department, b.region, b.skills, b.task_type, b.modified_at,
					b.modified_by, b.enroute_at, b.inprogress_at, b.prereqs_status, b.address_issue,
					b.latitude, b.longitude, b.gps_precision
				)::text) AS h
			FROM blocks_legacy b
			JOIN snaps s ON s.id = b.snapshot_id
		)
		, copies AS (
			SELECT k.*, row_number() OVER (PARTITION BY snapshot_id, h) AS copy
			FROM keyed k
		)
		, islands AS (
			SELECT c.*, seq - row_number() OVER (PARTITION BY h, copy ORDER BY seq) AS island
			FROM copies c
		)
		, spans AS (
			SELECT h, copy, island, MIN(snapshot_id) AS first_id, MAX(snapshot_id) AS last_id
			FROM islands
			GROUP BY h, copy, island
		)
		INSERT INTO blocks (
			first_snapshot_id, closed_snapshot_id, kind, work_date, tech_id, tech_name,
			starts_at, ends_at, ref_id, status, department, region, skills, task_type,
			modified_at, modified_by, enroute_at, inprogress_at, prereqs_status,
			address_issue, latitude, longitude, gps_precision
		)
		SELECT
			sp.first_id, s.next_id, i.kind, i.work_date, i.tech_id, i.tech_name, i.starts_at,
			i.ends_at, i.ref_id, i.status, i.department, i.region, i.skills, i.task_type,
			i.modified_at, i.modified_by, i.enroute_at, i.inprogress_at, i.prereqs_status,
			i.address_issue, i.latitude, i.longitude, i.gps_precision
		FROM spans sp
		JOIN islands i ON i.h = sp.h AND i.copy = sp.copy AND i.snapshot_id = sp.first_id
		JOIN snaps s ON s.id = sp.last_id
		ORDER BY sp.first_id, i.work_date, i.tech_id, i.starts_at;
	END IF;
END $$;

-- One row per plan day once D+2 has ended; job_outcomes holds its planned items.
CREATE TABLE IF NOT EXISTS outcome_days (
	plan_date DATE PRIMARY KEY
,	status TEXT NOT NULL CHECK (status IN ('ok', 'no_morning'))
,	morning_snapshot_id BIGINT
,	morning_at TIMESTAMP
,	added_job INTEGER NOT NULL DEFAULT 0
,	added_ticket INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS job_outcomes (
	plan_date DATE NOT NULL REFERENCES outcome_days (plan_date) ON DELETE CASCADE
,	kind TEXT NOT NULL
,	ref_id TEXT NOT NULL
,	tech_id TEXT
,	tech_name TEXT
,	planned_start TIMESTAMP
,	morning_snapshot_id BIGINT
,	d0 TEXT NOT NULL
,	d1 TEXT NOT NULL
,	d2 TEXT NOT NULL
,	PRIMARY KEY (plan_date, kind, ref_id)
);

-- How far the tech got on the plan day and whether a pre-drop/pre-bury was still open, both at d0.
ALTER TABLE job_outcomes ADD COLUMN IF NOT EXISTS reached TEXT;
ALTER TABLE job_outcomes ADD COLUMN IF NOT EXISTS prereqs_open BOOLEAN;

-- The job's region (its own, or the tech's shift region before 10-02) and task type (10-02).
ALTER TABLE job_outcomes ADD COLUMN IF NOT EXISTS region TEXT;
ALTER TABLE job_outcomes ADD COLUMN IF NOT EXISTS task_type TEXT;
"""

# The blocks content columns, in Block field order.
BLOCK_COLUMNS = [f.name for f in fields(Block)]

# The blocks rows of one snapshot, its id passed as %(snapshot)s.
AT_SNAPSHOT = """
first_snapshot_id <= %(snapshot)s
AND (closed_snapshot_id IS NULL OR closed_snapshot_id > %(snapshot)s)
"""

# Every Block field, with NULLs from older snapshots read as the parser's defaults.
BLOCK_FIELDS = """
kind, work_date, tech_id, tech_name, starts_at, ends_at, ref_id, status,
COALESCE(department, '') AS department, COALESCE(region, '') AS region,
COALESCE(skills, '') AS skills, COALESCE(task_type, '') AS task_type, modified_at,
COALESCE(modified_by, '') AS modified_by, enroute_at, inprogress_at,
COALESCE(prereqs_status, '') AS prereqs_status, address_issue, latitude, longitude,
COALESCE(gps_precision, '') AS gps_precision
"""

# Delivery latency per snapshot, split at the mailbox: MBS -> Gmail, then Gmail -> this poller.
RUNS_SQL = """
SELECT
	id
,	status
,	format
,	row_count
,	generated_at
,	mailbox_received_at
,	ingested_at
,	EXTRACT(EPOCH FROM (mailbox_received_at - generated_at)) / 60 AS mbs_to_mailbox_min
,	EXTRACT(EPOCH FROM (ingested_at - mailbox_received_at)) / 60 AS mailbox_to_ingest_min
,	EXTRACT(EPOCH FROM (ingested_at - generated_at)) / 60 AS total_min
,	error
FROM snapshots
ORDER BY id DESC
LIMIT %s
"""

LATENCY_SQL = """
SELECT
	COUNT(*) AS runs
,	COUNT(*) FILTER (WHERE status = 'ok') AS ok
,	COUNT(*) FILTER (WHERE status = 'empty') AS empty
,	COUNT(*) FILTER (WHERE status = 'error') AS errors
,	percentile_cont(0.5) WITHIN GROUP (ORDER BY total_min) AS total_median_min
,	percentile_cont(0.95) WITHIN GROUP (ORDER BY total_min) AS total_p95_min
,	MAX(total_min) AS total_max_min
FROM (
	SELECT
		status
	,	EXTRACT(EPOCH FROM (ingested_at - generated_at)) / 60 AS total_min
	FROM snapshots
) s
"""


def diff_blocks(
    current: list[tuple[int, Block]], new: list[Block]
) -> tuple[list[Block], list[int]]:
    # What turns the current rows into the new snapshot, as a multiset: the blocks to insert and the
    # ids to close. A row that is unchanged stays open.
    wanted = Counter(astuple(b) for b in new)
    close = []
    for row_id, block in current:
        key = astuple(block)
        if wanted[key]:
            wanted[key] -= 1
        else:
            close.append(row_id)
    insert = []
    for block in new:
        key = astuple(block)
        if wanted[key]:
            wanted[key] -= 1
            insert.append(block)
    return insert, close


class Store:
    def __init__(self, database_url: str):
        self.database_url = database_url

    def connect(self) -> psycopg.Connection:
        return psycopg.connect(self.database_url, row_factory=dict_row)

    def init_schema(self) -> None:
        with self.connect() as conn:
            conn.execute(SCHEMA)

    def seen(self, message_id: str) -> bool:
        with self.connect() as conn:
            row = conn.execute(
                "SELECT 1 FROM snapshots WHERE message_id = %s", (message_id,)
            ).fetchone()
        return row is not None

    def save(
        self,
        *,
        message_id: str,
        channel: str,
        subject: str | None,
        filename: str | None,
        email_date: datetime | None,
        mailbox_received_at: datetime | None,
        feed: ParsedFeed | None,
        sha256: str | None = None,
        error: str | None = None,
    ) -> int:
        if error is not None:
            status = "error"
        elif feed is not None and feed.row_count:
            status = "ok"
        else:
            status = "empty"
        with self.connect() as conn:
            snapshot_id = conn.execute(
                """
                INSERT INTO snapshots (
                    message_id, channel, subject, filename, generated_at, email_date,
                    mailbox_received_at, row_count, sha256, status, error, format
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                RETURNING id
                """,
                (
                    message_id,
                    channel,
                    subject,
                    filename,
                    feed.generated_at if feed else None,
                    email_date,
                    mailbox_received_at,
                    feed.row_count if feed else 0,
                    feed.sha256 if feed else sha256,
                    status,
                    error,
                    feed.format if feed else "slots",
                ),
            ).fetchone()["id"]
            if status == "ok" and feed.format == "blocks":
                self._save_blocks(conn, snapshot_id, feed.blocks)
            elif status == "ok":
                with conn.cursor() as cur:
                    cur.executemany(
                        """
                        INSERT INTO slots (
                            snapshot_id, work_date, tech_id, tech_name, open_from,
                            open_until, open_minutes, region, skills
                        )
                        VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
                        """,
                        [
                            (
                                snapshot_id,
                                s.work_date,
                                s.tech_id,
                                s.tech_name,
                                s.open_from,
                                s.open_until,
                                s.open_minutes,
                                s.region,
                                s.skills,
                            )
                            for s in feed.slots
                        ],
                    )
        return snapshot_id

    def _save_blocks(self, conn: psycopg.Connection, snapshot_id: int, blocks: list[Block]) -> None:
        # Only what changed since the previous blocks snapshot is written. The current rows are
        # read raw (no COALESCE) so a reconstructed snapshot matches what was ingested.
        columns = ", ".join(BLOCK_COLUMNS)
        current = [
            (row.pop("id"), Block(**row))
            for row in conn.execute(
                f"SELECT id, {columns} FROM blocks WHERE closed_snapshot_id IS NULL"
            ).fetchall()
        ]
        insert, close = diff_blocks(current, blocks)
        with conn.cursor() as cur:
            if close:
                cur.execute(
                    "UPDATE blocks SET closed_snapshot_id = %s WHERE id = ANY(%s)",
                    (snapshot_id, close),
                )
            if insert:
                cur.executemany(
                    f"""
                    INSERT INTO blocks (first_snapshot_id, {columns})
                    VALUES (%s{", %s" * len(BLOCK_COLUMNS)})
                    """,
                    [(snapshot_id, *astuple(b)) for b in insert],
                )

    def latest(self) -> dict | None:
        # An empty or failed run never replaces the last good snapshot. While both feeds run,
        # the newest blocks snapshot wins over a newer legacy one.
        with self.connect() as conn:
            snapshot = conn.execute("""
                SELECT * FROM snapshots WHERE status = 'ok'
                ORDER BY (format = 'blocks') DESC, id DESC LIMIT 1
                """).fetchone()
            if snapshot is None:
                return None
            if snapshot["format"] == "blocks":
                blocks = conn.execute(
                    f"""
                    SELECT {BLOCK_FIELDS}
                    FROM blocks
                    WHERE {AT_SNAPSHOT}
                    ORDER BY work_date, tech_name, starts_at, kind, ref_id, id
                    """,
                    {"snapshot": snapshot["id"]},
                ).fetchall()
                return {"snapshot": snapshot, "blocks": [Block(**b) for b in blocks]}
            slots = conn.execute(
                """
                SELECT work_date, tech_id, tech_name, open_from, open_until,
                       open_minutes, region, skills
                FROM slots
                WHERE snapshot_id = %s
                ORDER BY work_date, tech_name, open_from
                """,
                (snapshot["id"],),
            ).fetchall()
        return {"snapshot": snapshot, "slots": slots}

    def runs(self, limit: int = 200) -> list[dict]:
        with self.connect() as conn:
            return conn.execute(RUNS_SQL, (limit,)).fetchall()

    def latency(self) -> dict:
        with self.connect() as conn:
            return conn.execute(LATENCY_SQL).fetchone()

    def blocks_snapshots(self) -> list[dict]:
        with self.connect() as conn:
            return conn.execute("""
                SELECT id, generated_at FROM snapshots
                WHERE status = 'ok' AND format = 'blocks' AND generated_at IS NOT NULL
                ORDER BY generated_at
                """).fetchall()

    def work_blocks(self, snapshot_id: int) -> list[Block]:
        # Only the rows the outcome history needs: jobs and tickets, on or off the calendar.
        with self.connect() as conn:
            rows = conn.execute(
                f"""
                SELECT {BLOCK_FIELDS}
                FROM blocks
                WHERE {AT_SNAPSHOT} AND kind NOT IN ('shift', 'shift_tc', 'time_off')
                """,
                {"snapshot": snapshot_id},
            ).fetchall()
        return [Block(**row) for row in rows]

    def shift_regions(self, snapshot_id: int, day) -> dict[str, str]:
        # Each tech's shift region on one day: fallback for work from before the query sent its own.
        with self.connect() as conn:
            rows = conn.execute(
                f"""
                SELECT DISTINCT ON (tech_id) tech_id, COALESCE(region, '') AS region
                FROM blocks
                WHERE {AT_SNAPSHOT} AND kind IN ('shift', 'shift_tc') AND work_date = %(day)s
                ORDER BY tech_id, kind <> 'shift', starts_at
                """,
                {"snapshot": snapshot_id, "day": day},
            ).fetchall()
        return {row["tech_id"]: row["region"] for row in rows}

    def snapshot_meta(self) -> dict | None:
        # The snapshot latest() would serve, without loading its rows.
        with self.connect() as conn:
            return conn.execute("""
                SELECT id, format, generated_at, ingested_at FROM snapshots WHERE status = 'ok'
                ORDER BY (format = 'blocks') DESC, id DESC LIMIT 1
                """).fetchone()

    def finalized_days(self) -> set:
        with self.connect() as conn:
            rows = conn.execute("SELECT plan_date FROM outcome_days").fetchall()
        return {row["plan_date"] for row in rows}

    def save_day(self, outcome: DayOutcome) -> None:
        morning = outcome.morning
        with self.connect() as conn:
            conn.execute(
                """
                INSERT INTO outcome_days (
                    plan_date, status, morning_snapshot_id, morning_at, added_job, added_ticket
                )
                VALUES (%s, %s, %s, %s, %s, %s)
                ON CONFLICT (plan_date) DO NOTHING
                """,
                (
                    outcome.day,
                    outcome.status,
                    morning.id if morning else None,
                    morning.at if morning else None,
                    outcome.added_after_morning.get("job", 0),
                    outcome.added_after_morning.get("ticket", 0),
                ),
            )
            with conn.cursor() as cur:
                cur.executemany(
                    """
                    INSERT INTO job_outcomes (
                        plan_date, kind, ref_id, tech_id, tech_name, planned_start,
                        morning_snapshot_id, d0, d1, d2, reached, prereqs_open, region, task_type
                    )
                    VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT DO NOTHING
                    """,
                    [
                        (
                            outcome.day,
                            i.kind,
                            i.ref_id,
                            i.tech_id,
                            i.tech_name,
                            i.planned_start,
                            morning.id,
                            i.outcomes["d0"],
                            i.outcomes["d1"],
                            i.outcomes["d2"],
                            i.reached,
                            i.prereqs_open,
                            i.region,
                            i.task_type,
                        )
                        for i in outcome.items
                    ],
                )

    def load_day(self, day) -> DayOutcome | None:
        with self.connect() as conn:
            row = conn.execute("SELECT * FROM outcome_days WHERE plan_date = %s", (day,)).fetchone()
            if row is None:
                return None
            items = conn.execute(
                """
                SELECT kind, ref_id, tech_id, tech_name, planned_start, d0, d1, d2,
                       COALESCE(reached, 'unknown') AS reached, prereqs_open,
                       COALESCE(region, '') AS region, COALESCE(task_type, '') AS task_type
                FROM job_outcomes WHERE plan_date = %s
                ORDER BY kind, planned_start, ref_id
                """,
                (day,),
            ).fetchall()
        morning = None
        if row["morning_snapshot_id"] is not None:
            morning = Snapshot(row["morning_snapshot_id"], row["morning_at"])
        return DayOutcome(
            day,
            row["status"],
            morning,
            [
                Planned(
                    i["kind"],
                    i["ref_id"],
                    i["tech_id"],
                    i["tech_name"],
                    i["planned_start"],
                    {"d0": i["d0"], "d1": i["d1"], "d2": i["d2"]},
                    i["reached"],
                    i["prereqs_open"],
                    i["region"],
                    i["task_type"],
                )
                for i in items
            ],
            {"job": row["added_job"], "ticket": row["added_ticket"]},
        )
