-- Provenance: commits produced (or attributed) to a session, linked to a prompt when known.
CREATE TABLE IF NOT EXISTS session_commits (
    sha TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(id),
    prompt_event_id TEXT,
    captured_at TEXT NOT NULL,
    PRIMARY KEY (sha, session_id)
);

CREATE INDEX IF NOT EXISTS idx_session_commits_session
    ON session_commits (session_id);
