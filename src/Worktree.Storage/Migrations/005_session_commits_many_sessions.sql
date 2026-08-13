-- One commit may link to many sessions (e.g. commit after several short chats).
CREATE TABLE IF NOT EXISTS session_commits_multi (
    sha TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(id),
    prompt_event_id TEXT,
    captured_at TEXT NOT NULL,
    PRIMARY KEY (sha, session_id)
);

INSERT OR IGNORE INTO session_commits_multi (sha, session_id, prompt_event_id, captured_at)
SELECT sha, session_id, prompt_event_id, captured_at
FROM session_commits;

DROP TABLE IF EXISTS session_commits;
ALTER TABLE session_commits_multi RENAME TO session_commits;

CREATE INDEX IF NOT EXISTS idx_session_commits_session
    ON session_commits (session_id);

CREATE INDEX IF NOT EXISTS idx_session_commits_sha
    ON session_commits (sha);
