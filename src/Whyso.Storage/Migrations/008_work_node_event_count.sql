-- How many events the projection was built from. A session whose stored event count differs
-- (or that has no projection yet) is stale and gets re-projected on the next read.
ALTER TABLE work_nodes ADD COLUMN event_count INTEGER NOT NULL DEFAULT 0;
