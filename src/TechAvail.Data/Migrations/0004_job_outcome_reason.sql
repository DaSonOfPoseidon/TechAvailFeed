-- The job's incomplete reason at d0. NULL in days finalized before this column, which are never recomputed.
ALTER TABLE job_outcomes ADD COLUMN IF NOT EXISTS incomplete_reason TEXT;
