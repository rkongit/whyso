using Microsoft.Data.Sqlite;

namespace Whyso.Storage;

public sealed partial class Store
{
    public async Task UpsertWorkNodeProjectionAsync(WorkNodeProjection projection)
    {
        await _gate.WaitAsync();
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync();
            await UpsertWorkNodeProjectionUnlockedAsync(tx, projection);
            await tx.CommitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAutoWorkNodeEdgesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM work_node_edges WHERE manual = 0";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAutoWorkNodeEdgesAsync(IReadOnlyList<WorkNodeEdgeRecord> edges)
    {
        await _gate.WaitAsync();
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync();
            await using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM work_node_edges WHERE manual = 0";
                await del.ExecuteNonQueryAsync();
            }

            foreach (var edge in edges.Where(e => !e.Manual))
            {
                await InsertEdgeUnlockedAsync(tx, edge);
            }

            await tx.CommitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertManualEdgeAsync(WorkNodeEdgeRecord edge)
    {
        await _gate.WaitAsync();
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync();
            await InsertEdgeUnlockedAsync(tx, new WorkNodeEdgeRecord
            {
                FromId = edge.FromId,
                ToId = edge.ToId,
                Kind = edge.Kind,
                Label = edge.Label,
                Score = edge.Score,
                Manual = true,
                CreatedAt = edge.CreatedAt,
            });
            await tx.CommitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteEdgeAsync(string fromId, string toId, string kind)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                DELETE FROM work_node_edges
                WHERE from_id = $from AND to_id = $to AND kind = $kind
                """;
            cmd.Parameters.AddWithValue("$from", fromId);
            cmd.Parameters.AddWithValue("$to", toId);
            cmd.Parameters.AddWithValue("$kind", kind);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddWorkNodeLabelAsync(string workNodeId, string label)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO work_node_labels (work_node_id, label, created_at)
                VALUES ($id, $label, $ts)
                """;
            cmd.Parameters.AddWithValue("$id", workNodeId);
            cmd.Parameters.AddWithValue("$label", label.Trim());
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveWorkNodeLabelAsync(string workNodeId, string label)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                DELETE FROM work_node_labels
                WHERE work_node_id = $id AND label = $label
                """;
            cmd.Parameters.AddWithValue("$id", workNodeId);
            cmd.Parameters.AddWithValue("$label", label.Trim());
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkNodeRecord?> GetWorkNodeAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, session_id, project_id, agent, started_at, ended_at,
                       git_head, git_head_after, status, prompt_excerpt,
                       turn_count, file_count, command_count, test_count, commit_count, updated_at
                FROM work_nodes
                WHERE id = $id OR session_id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return ReadWorkNode(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeRecord>> ListWorkNodesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, session_id, project_id, agent, started_at, ended_at,
                       git_head, git_head_after, status, prompt_excerpt,
                       turn_count, file_count, command_count, test_count, commit_count, updated_at
                FROM work_nodes
                ORDER BY started_at DESC
                """;
            var list = new List<WorkNodeRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(ReadWorkNode(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeFileRecord>> ListWorkNodeFilesAsync(
        string? workNodeId = null,
        string? path = null,
        string? pathPrefix = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            var sql =
                """
                SELECT work_node_id, path, action, first_seen, last_seen
                FROM work_node_files
                WHERE 1 = 1
                """;
            if (workNodeId is not null)
            {
                sql += " AND work_node_id = $nid";
            }

            if (path is not null)
            {
                sql += " AND (lower(path) = lower($path) OR lower(path) LIKE '%' || lower($path))";
            }

            if (pathPrefix is not null)
            {
                sql += " AND lower(replace(path, '\\', '/')) LIKE lower($prefix) || '%'";
            }

            sql += " ORDER BY last_seen ASC, path ASC";
            cmd.CommandText = sql;
            if (workNodeId is not null)
            {
                cmd.Parameters.AddWithValue("$nid", workNodeId);
            }

            if (path is not null)
            {
                cmd.Parameters.AddWithValue("$path", path.Replace('\\', '/').TrimStart('/'));
            }

            if (pathPrefix is not null)
            {
                var prefix = pathPrefix.Replace('\\', '/').TrimStart('/');
                if (!prefix.EndsWith('/'))
                {
                    prefix += "/";
                }

                cmd.Parameters.AddWithValue("$prefix", prefix);
            }

            var list = new List<WorkNodeFileRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new WorkNodeFileRecord
                {
                    WorkNodeId = reader.GetString(0),
                    Path = reader.GetString(1),
                    Action = reader.GetString(2),
                    FirstSeen = ParseTime(reader.GetString(3)),
                    LastSeen = ParseTime(reader.GetString(4)),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListWorkNodeIdsForFileAsync(string path)
    {
        var files = await ListWorkNodeFilesAsync(path: path);
        return files
            .Select(f => f.WorkNodeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkNodeCommandRecord>> ListWorkNodeCommandsAsync(string workNodeId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT work_node_id, command_text, seen_at
                FROM work_node_commands
                WHERE work_node_id = $id
                ORDER BY seen_at ASC
                """;
            cmd.Parameters.AddWithValue("$id", workNodeId);
            var list = new List<WorkNodeCommandRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new WorkNodeCommandRecord
                {
                    WorkNodeId = reader.GetString(0),
                    CommandText = reader.GetString(1),
                    SeenAt = ParseTime(reader.GetString(2)),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeTestRecord>> ListWorkNodeTestsAsync(string workNodeId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT work_node_id, name, suite, status, seen_at
                FROM work_node_tests
                WHERE work_node_id = $id
                ORDER BY seen_at ASC
                """;
            cmd.Parameters.AddWithValue("$id", workNodeId);
            var list = new List<WorkNodeTestRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new WorkNodeTestRecord
                {
                    WorkNodeId = reader.GetString(0),
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Suite = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Status = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SeenAt = ParseTime(reader.GetString(4)),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeEdgeRecord>> ListWorkNodeEdgesAsync(string? nodeId = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            if (nodeId is null)
            {
                cmd.CommandText =
                    """
                    SELECT from_id, to_id, kind, label, score, manual, created_at
                    FROM work_node_edges
                    ORDER BY created_at ASC
                    """;
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT from_id, to_id, kind, label, score, manual, created_at
                    FROM work_node_edges
                    WHERE from_id = $id OR to_id = $id
                    ORDER BY created_at ASC
                    """;
                cmd.Parameters.AddWithValue("$id", nodeId);
            }

            var list = new List<WorkNodeEdgeRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new WorkNodeEdgeRecord
                {
                    FromId = reader.GetString(0),
                    ToId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    Label = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Score = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    Manual = reader.GetInt64(5) != 0,
                    CreatedAt = ParseTime(reader.GetString(6)),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeLabelRecord>> ListWorkNodeLabelsAsync(string? workNodeId = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            if (workNodeId is null)
            {
                cmd.CommandText =
                    """
                    SELECT work_node_id, label, created_at
                    FROM work_node_labels
                    ORDER BY created_at ASC
                    """;
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT work_node_id, label, created_at
                    FROM work_node_labels
                    WHERE work_node_id = $id
                    ORDER BY created_at ASC
                    """;
                cmd.Parameters.AddWithValue("$id", workNodeId);
            }

            var list = new List<WorkNodeLabelRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new WorkNodeLabelRecord
                {
                    WorkNodeId = reader.GetString(0),
                    Label = reader.GetString(1),
                    CreatedAt = ParseTime(reader.GetString(2)),
                });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkNodeSearchHit>> SearchWorkNodesAsync(string query, int limit = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT work_node_id,
                       snippet(work_node_fts, 1, '', '', '…', 12),
                       'match'
                FROM work_node_fts
                WHERE work_node_fts MATCH $q
                LIMIT $limit
                """;
            // FTS5: quote tokens simply; escape double quotes.
            var ftsQuery = string.Join(" ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<WorkNodeSearchHit>();
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new WorkNodeSearchHit
                    {
                        WorkNodeId = reader.GetString(0),
                        Snippet = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Field = reader.IsDBNull(2) ? "match" : reader.GetString(2),
                    });
                }
            }
            catch (SqliteException)
            {
                return [];
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountWorkNodesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM work_nodes";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpsertWorkNodeProjectionUnlockedAsync(
        SqliteTransaction tx,
        WorkNodeProjection projection)
    {
        var n = projection.Node;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO work_nodes (
                    id, session_id, project_id, agent, started_at, ended_at,
                    git_head, git_head_after, status, prompt_excerpt,
                    turn_count, file_count, command_count, test_count, commit_count, updated_at)
                VALUES (
                    $id, $sid, $pid, $agent, $started, $ended,
                    $gh, $gha, $status, $excerpt,
                    $turns, $files, $cmds, $tests, $commits, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    session_id = excluded.session_id,
                    project_id = excluded.project_id,
                    agent = excluded.agent,
                    started_at = excluded.started_at,
                    ended_at = excluded.ended_at,
                    git_head = excluded.git_head,
                    git_head_after = excluded.git_head_after,
                    status = excluded.status,
                    prompt_excerpt = excluded.prompt_excerpt,
                    turn_count = excluded.turn_count,
                    file_count = excluded.file_count,
                    command_count = excluded.command_count,
                    test_count = excluded.test_count,
                    commit_count = excluded.commit_count,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", n.Id);
            cmd.Parameters.AddWithValue("$sid", n.SessionId);
            cmd.Parameters.AddWithValue("$pid", n.ProjectId);
            cmd.Parameters.AddWithValue("$agent", n.Agent);
            cmd.Parameters.AddWithValue("$started", n.StartedAt.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$ended", (object?)n.EndedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gh", (object?)n.GitHead ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gha", (object?)n.GitHeadAfter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", n.Status);
            cmd.Parameters.AddWithValue("$excerpt", n.PromptExcerpt);
            cmd.Parameters.AddWithValue("$turns", n.TurnCount);
            cmd.Parameters.AddWithValue("$files", n.FileCount);
            cmd.Parameters.AddWithValue("$cmds", n.CommandCount);
            cmd.Parameters.AddWithValue("$tests", n.TestCount);
            cmd.Parameters.AddWithValue("$commits", n.CommitCount);
            cmd.Parameters.AddWithValue("$updated", n.UpdatedAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM work_node_files WHERE work_node_id = $id";
            del.Parameters.AddWithValue("$id", n.Id);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var file in projection.Files)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO work_node_files (work_node_id, path, action, first_seen, last_seen)
                VALUES ($id, $path, $action, $first, $last)
                """;
            cmd.Parameters.AddWithValue("$id", n.Id);
            cmd.Parameters.AddWithValue("$path", file.Path);
            cmd.Parameters.AddWithValue("$action", file.Action);
            cmd.Parameters.AddWithValue("$first", file.FirstSeen.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$last", file.LastSeen.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM work_node_commands WHERE work_node_id = $id";
            del.Parameters.AddWithValue("$id", n.Id);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var command in projection.Commands)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO work_node_commands (work_node_id, command_text, seen_at)
                VALUES ($id, $text, $ts)
                """;
            cmd.Parameters.AddWithValue("$id", n.Id);
            cmd.Parameters.AddWithValue("$text", command.CommandText);
            cmd.Parameters.AddWithValue("$ts", command.SeenAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM work_node_tests WHERE work_node_id = $id";
            del.Parameters.AddWithValue("$id", n.Id);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var test in projection.Tests)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO work_node_tests (work_node_id, name, suite, status, seen_at)
                VALUES ($id, $name, $suite, $status, $ts)
                """;
            cmd.Parameters.AddWithValue("$id", n.Id);
            cmd.Parameters.AddWithValue("$name", (object?)test.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$suite", (object?)test.Suite ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)test.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", test.SeenAt.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM work_node_fts WHERE work_node_id = $id";
            del.Parameters.AddWithValue("$id", n.Id);
            await del.ExecuteNonQueryAsync();
        }

        var fts = projection.Fts;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO work_node_fts (work_node_id, prompt, plan, paths, commands, tests, commits)
                VALUES ($id, $prompt, $plan, $paths, $cmds, $tests, $commits)
                """;
            cmd.Parameters.AddWithValue("$id", fts.WorkNodeId);
            cmd.Parameters.AddWithValue("$prompt", fts.Prompt);
            cmd.Parameters.AddWithValue("$plan", fts.Plan);
            cmd.Parameters.AddWithValue("$paths", fts.Paths);
            cmd.Parameters.AddWithValue("$cmds", fts.Commands);
            cmd.Parameters.AddWithValue("$tests", fts.Tests);
            cmd.Parameters.AddWithValue("$commits", fts.Commits);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertEdgeUnlockedAsync(SqliteTransaction tx, WorkNodeEdgeRecord edge)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO work_node_edges (from_id, to_id, kind, label, score, manual, created_at)
            VALUES ($from, $to, $kind, $label, $score, $manual, $ts)
            ON CONFLICT(from_id, to_id, kind) DO UPDATE SET
                label = excluded.label,
                score = excluded.score,
                manual = excluded.manual,
                created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("$from", edge.FromId);
        cmd.Parameters.AddWithValue("$to", edge.ToId);
        cmd.Parameters.AddWithValue("$kind", edge.Kind);
        cmd.Parameters.AddWithValue("$label", (object?)edge.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score", (object?)edge.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$manual", edge.Manual ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", edge.CreatedAt.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static WorkNodeRecord ReadWorkNode(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SessionId = reader.GetString(1),
        ProjectId = reader.GetString(2),
        Agent = reader.GetString(3),
        StartedAt = ParseTime(reader.GetString(4)),
        EndedAt = reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
        GitHead = reader.IsDBNull(6) ? null : reader.GetString(6),
        GitHeadAfter = reader.IsDBNull(7) ? null : reader.GetString(7),
        Status = reader.GetString(8),
        PromptExcerpt = reader.GetString(9),
        TurnCount = reader.GetInt32(10),
        FileCount = reader.GetInt32(11),
        CommandCount = reader.GetInt32(12),
        TestCount = reader.GetInt32(13),
        CommitCount = reader.GetInt32(14),
        UpdatedAt = ParseTime(reader.GetString(15)),
    };
}
