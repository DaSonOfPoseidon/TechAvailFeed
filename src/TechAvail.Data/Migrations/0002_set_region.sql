-- The region set on the job or ticket itself in MBS (its address's region is `region`). NULL in
-- rows from exports without the column.
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS set_region TEXT;

-- SELECT * views keep the columns they were created with.
CREATE OR REPLACE VIEW current_blocks AS
SELECT * FROM blocks WHERE closed_snapshot_id IS NULL;
