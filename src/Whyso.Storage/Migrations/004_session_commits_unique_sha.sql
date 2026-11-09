-- One commit SHA → one session. Rebuild if an older composite PK existed.
CREATE TABLE IF NOT EXISTS session_commits_v2 (
    sha TEXT PRIMARY KEY NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(id),
    prompt_event_id TEXT,
    captured_at TEXT NOT NULL
);

INSERT OR IGNORE INTO session_commits_v2 (sha, session_id, prompt_event_id, captured_at)
SELECT sha, session_id, prompt_event_id, captured_at
FROM session_commits
ORDER BY
  CASE WHEN prompt_event_id IS NOT NULL THEN 0 ELSE 1 END,
  captured_at ASC;

DROP TABLE IF EXISTS session_commits;
ALTER TABLE session_commits_v2 RENAME TO session_commits;

CREATE INDEX IF NOT EXISTS idx_session_commits_session
    ON session_commits (session_id);
