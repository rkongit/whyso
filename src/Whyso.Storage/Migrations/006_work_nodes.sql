-- Work Node projection: rebuildable summaries + graph + FTS (JSONL remains source of truth).

CREATE TABLE IF NOT EXISTS work_nodes (
    id TEXT PRIMARY KEY NOT NULL,
    session_id TEXT NOT NULL UNIQUE REFERENCES sessions(id),
    project_id TEXT NOT NULL REFERENCES projects(id),
    agent TEXT NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT,
    git_head TEXT,
    git_head_after TEXT,
    status TEXT NOT NULL,
    prompt_excerpt TEXT NOT NULL DEFAULT '',
    turn_count INTEGER NOT NULL DEFAULT 0,
    file_count INTEGER NOT NULL DEFAULT 0,
    command_count INTEGER NOT NULL DEFAULT 0,
    test_count INTEGER NOT NULL DEFAULT 0,
    commit_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_work_nodes_project_started
    ON work_nodes (project_id, started_at DESC);

CREATE TABLE IF NOT EXISTS work_node_files (
    work_node_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    path TEXT NOT NULL,
    action TEXT NOT NULL,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    PRIMARY KEY (work_node_id, path, action)
);

CREATE INDEX IF NOT EXISTS idx_work_node_files_path
    ON work_node_files (path);

CREATE TABLE IF NOT EXISTS work_node_commands (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    work_node_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    command_text TEXT NOT NULL,
    seen_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_work_node_commands_node
    ON work_node_commands (work_node_id);

CREATE TABLE IF NOT EXISTS work_node_tests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    work_node_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    name TEXT,
    suite TEXT,
    status TEXT,
    seen_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_work_node_tests_node
    ON work_node_tests (work_node_id);

CREATE TABLE IF NOT EXISTS work_node_edges (
    from_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    to_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    label TEXT,
    score REAL,
    manual INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    PRIMARY KEY (from_id, to_id, kind)
);

CREATE INDEX IF NOT EXISTS idx_work_node_edges_to
    ON work_node_edges (to_id);

CREATE TABLE IF NOT EXISTS work_node_labels (
    work_node_id TEXT NOT NULL REFERENCES work_nodes(id) ON DELETE CASCADE,
    label TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (work_node_id, label)
);

CREATE VIRTUAL TABLE IF NOT EXISTS work_node_fts USING fts5(
    work_node_id UNINDEXED,
    prompt,
    plan,
    paths,
    commands,
    tests,
    commits,
    tokenize = 'porter unicode61'
);
