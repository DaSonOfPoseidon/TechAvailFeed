-- Why a job wasn't completed, as the code MBS gives. NULL in rows from exports without the column.
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS incomplete_reason TEXT;

-- SELECT * views keep the columns they were created with.
CREATE OR REPLACE VIEW current_blocks AS
SELECT * FROM blocks WHERE closed_snapshot_id IS NULL;
