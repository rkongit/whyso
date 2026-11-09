-- Envelope v1.1: optional team-readiness fields. NULL for events from older clients.
ALTER TABLE events ADD COLUMN actor TEXT;
ALTER TABLE events ADD COLUMN org_id TEXT;
ALTER TABLE events ADD COLUMN envelope_project_id TEXT;
ALTER TABLE events ADD COLUMN visibility TEXT;
