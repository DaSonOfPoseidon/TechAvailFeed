-- Baseline: the schema feed/store.py creates, verbatim. Every statement is idempotent, so it
-- runs cleanly against a database the Python ingest already set up.
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
