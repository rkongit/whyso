using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whyso.Events;

namespace Whyso.Storage;

public sealed class ProjectRecord
{
    public required string Id { get; init; }
    public required string RootPath { get; init; }
    public string? GitHead { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class SessionRecord
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Agent { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? GitHead { get; init; }
    public string? GitHeadAfter { get; init; }

    public SessionStatus Status => EndedAt is null ? SessionStatus.Active : SessionStatus.Completed;
}

public enum SessionStatus
{
    Active,
    Completed,
}

public static class SessionStatusExtensions
{
    public static string AsString(this SessionStatus status) => status switch
    {
        SessionStatus.Active => "active",
        SessionStatus.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

public sealed class SessionCommitRecord
{
    public required string Sha { get; init; }
    public required string SessionId { get; init; }
    public string? PromptEventId { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}

public abstract class AppendOutcome
{
    public sealed class Inserted(AgentEvent event_) : AppendOutcome
    {
        public AgentEvent Event { get; } = event_;
    }

    public sealed class Duplicate(AgentEvent event_) : AppendOutcome
    {
        public AgentEvent Event { get; } = event_;
    }
}

public sealed partial class Store : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Store(string root, SqliteConnection connection)
    {
        _root = root;
        _connection = connection;
    }

    public string Root => _root;

    public static async Task<(string Root, Config Config)> InitProjectAsync(string cwd)
    {
        var root = Git.DetectGitRoot(cwd);
        WorktreePaths.TryMigrateLegacyDir(root);
        Directory.CreateDirectory(WorktreePaths.WorktreeDir(root));
        Directory.CreateDirectory(WorktreePaths.EventsDir(root));

        await using var store = await OpenAsync(root);

        var existing = await store.FindProjectByRootAsync(root);
        if (existing is not null)
        {
            Config config;
            try
            {
                config = Config.Load(root);
            }
            catch
            {
                config = Config.New(existing.Id);
            }

            if (!File.Exists(WorktreePaths.ConfigPath(root)))
            {
                Config.Save(root, config);
            }
        }
        else
        {
            var projectId = $"proj_{Guid.NewGuid():D}";
            var gitHead = Git.DetectGitHead(root);
            await store.InsertProjectAsync(projectId, root, gitHead);
            Config.Save(root, Config.New(projectId));
        }

        if (!File.Exists(WorktreePaths.ConfigPath(root)))
        {
            var projectId = (await store.FindProjectByRootAsync(root))?.Id
                ?? $"proj_{Guid.NewGuid():D}";
            Config.Save(root, Config.New(projectId));
        }

        return (root, Config.Load(root));
    }

    public static async Task<Store> OpenAsync(string root)
    {
        WorktreePaths.TryMigrateLegacyDir(root);
        if (!Directory.Exists(WorktreePaths.WorktreeDir(root)))
        {
            throw StorageException.NotInitialized();
        }

        Directory.CreateDirectory(WorktreePaths.EventsDir(root));

        var dbPath = WorktreePaths.WorktreeDbPath(root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString());

        await connection.OpenAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA secure_delete = ON;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        Migrator.Migrate(connection);
        return new Store(root, connection);
    }

    public async Task InsertProjectAsync(string id, string root, string? gitHead)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO projects (id, root_path, git_head, created_at)
                VALUES ($id, $root, $head, $created)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$root", root);
            cmd.Parameters.AddWithValue("$head", (object?)gitHead ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectRecord?> FindProjectByRootAsync(string root)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, root_path, git_head, created_at
                FROM projects
                WHERE root_path = $root
                """;
            cmd.Parameters.AddWithValue("$root", root);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return ReadProject(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionRecord?> GetSessionAsync(string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            return await GetSessionUnlockedAsync(sessionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, project_id, agent, started_at, ended_at, git_head, git_head_after
                FROM sessions
                ORDER BY started_at DESC
                """;
            var list = new List<SessionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(ReadSession(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentEvent?> GetEventAsync(string eventId)
    {
        await _gate.WaitAsync();
        try
        {
            return await GetEventUnlockedAsync(eventId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AgentEvent>> ListEventsAsync(string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, session_id, sequence, event_type, timestamp, agent, payload,
                       actor, org_id, envelope_project_id, visibility
                FROM events
                WHERE session_id = $sid
                ORDER BY sequence ASC
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            var list = new List<AgentEvent>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(ReadEvent(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountSessionsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sessions";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountEventsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountPromptsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE event_type = $type";
            cmd.Parameters.AddWithValue("$type", EventType.PromptSubmitted.AsString());
            return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionCommitRecord>> ListSessionCommitsAsync(string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sha, session_id, prompt_event_id, captured_at
                FROM session_commits
                WHERE session_id = $sid
                ORDER BY captured_at ASC, sha ASC
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            return await ReadCommitRowsAsync(cmd);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Commits matching an exact SHA or a unique prefix (case-insensitive).
    /// </summary>
    public async Task<IReadOnlyList<SessionCommitRecord>> ListCommitsByShaAsync(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT sha, session_id, prompt_event_id, captured_at
                FROM session_commits
                WHERE lower(sha) = lower($sha)
                   OR (length($sha) >= 7 AND lower(sha) LIKE lower($sha) || '%')
                ORDER BY captured_at ASC, sha ASC, session_id ASC
                """;
            cmd.Parameters.AddWithValue("$sha", sha.Trim());
            return await ReadCommitRowsAsync(cmd);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IReadOnlyList<SessionCommitRecord>> ReadCommitRowsAsync(SqliteCommand cmd)
    {
        var list = new List<SessionCommitRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SessionCommitRecord
            {
                Sha = reader.GetString(0),
                SessionId = reader.GetString(1),
                PromptEventId = reader.IsDBNull(2) ? null : reader.GetString(2),
                CapturedAt = ParseTime(reader.GetString(3)),
            });
        }

        return list;
    }

    public async Task AttributeCommitsAtHeadAsync()
    {
        var head = Git.DetectGitHead(_root);
        if (head is null)
        {
            return;
        }

        var sessions = await ListSessionsAsync();
        foreach (var session in sessions)
        {
            IReadOnlyList<string> shas;
            if (string.IsNullOrEmpty(session.GitHead))
            {
                // Session started before the first commit (or HEAD detection failed).
                // Attribute commits dated at/after session start instead of skipping.
                shas = Git.CommitsSinceTime(_root, session.StartedAt);
            }
            else if (session.GitHead == head)
            {
                continue;
            }
            else
            {
                bool ok;
                try
                {
                    ok = Git.IsAncestor(_root, session.GitHead, head);
                }
                catch
                {
                    ok = false;
                }

                if (!ok)
                {
                    continue;
                }

                shas = Git.CommitsSince(_root, session.GitHead);
            }

            if (shas.Count == 0)
            {
                continue;
            }

            await RecordSessionCommitShasAsync(session.Id, shas);
        }
    }

    public async Task<IReadOnlyList<string>> RecordSessionCommitsSinceAsync(string sessionId, string? baseSha)
    {
        var shas = Git.CommitsSince(_root, baseSha);
        return await RecordSessionCommitShasAsync(sessionId, shas);
    }

    /// <summary>
    /// When true, newly attributed commits get a note under <c>refs/notes/whyso</c> (opt-in via
    /// <c>git_notes</c> in config). Notes carry ids only, never prompt text, because notes can
    /// be pushed.
    /// </summary>
    public bool WriteGitNotes { get; set; }

    public async Task<IReadOnlyList<string>> RecordSessionCommitShasAsync(
        string sessionId,
        IReadOnlyList<string> shas)
    {
        var inserted = await RecordSessionCommitShasCoreAsync(sessionId, shas);
        if (WriteGitNotes)
        {
            foreach (var sha in inserted)
            {
                try
                {
                    await WriteProvenanceNoteAsync(sha);
                }
                catch
                {
                    // Best effort: a failed note never fails capture.
                }
            }
        }

        return inserted;
    }

    /// <summary>Writes the provenance note for one commit, listing every linked session.</summary>
    public async Task<bool> WriteProvenanceNoteAsync(string sha)
    {
        var links = await ListCommitsByShaAsync(sha);
        if (links.Count == 0)
        {
            return false;
        }

        var sessions = new JsonArray();
        foreach (var link in links)
        {
            var session = await GetSessionAsync(link.SessionId);
            sessions.Add(new JsonObject
            {
                ["session_id"] = link.SessionId,
                ["prompt_event_id"] = link.PromptEventId,
                ["agent"] = session?.Agent,
                ["started_at"] = session?.StartedAt.ToUniversalTime().ToString("O"),
            });
        }

        var note = new JsonObject { ["whyso"] = 1, ["sessions"] = sessions };
        return Git.WriteNote(_root, links[0].Sha, note.ToJsonString());
    }

    private async Task<IReadOnlyList<string>> RecordSessionCommitShasCoreAsync(
        string sessionId,
        IReadOnlyList<string> shas)
    {
        if (shas.Count == 0)
        {
            return [];
        }

        await _gate.WaitAsync();
        try
        {
            var promptEventId = await LatestPromptEventIdUnlockedAsync(sessionId);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var inserted = new List<string>();
            foreach (var sha in shas)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT OR IGNORE INTO session_commits (sha, session_id, prompt_event_id, captured_at)
                    VALUES ($sha, $sid, $prompt, $now)
                    """;
                cmd.Parameters.AddWithValue("$sha", sha);
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$prompt", (object?)promptEventId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                var affected = await cmd.ExecuteNonQueryAsync();
                if (affected > 0)
                {
                    inserted.Add(sha);
                }
            }

            var head = Git.DetectGitHead(_root);
            if (head is not null)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE sessions
                    SET git_head_after = $head
                    WHERE id = $sid
                    """;
                cmd.Parameters.AddWithValue("$head", head);
                cmd.Parameters.AddWithValue("$sid", sessionId);
                await cmd.ExecuteNonQueryAsync();
            }

            return inserted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppendOutcome> AppendEventAsync(AgentEvent event_, string projectId)
    {
        try
        {
            event_.Validate();
        }
        catch (ValidationError ex)
        {
            throw StorageException.Validation(ex.Message);
        }

        var existing = await GetEventAsync(event_.EventId);
        if (existing is not null)
        {
            return new AppendOutcome.Duplicate(existing);
        }

        var sessionMissing = await GetSessionAsync(event_.SessionId) is null;
        var needGit = event_.EventType == EventType.SessionEnded || sessionMissing;
        var gitHead = needGit ? Git.DetectGitHead(_root) : null;

        // Redact secrets, then encrypt with the session key. Raw values never reach JSONL or SQLite.
        var sealedPayload = PayloadCrypto.Encrypt(
            PayloadCrypto.GetOrCreateKey(_root, event_.SessionId),
            event_.EventId,
            event_.SessionId,
            Redactor.RedactNode(event_.Payload) ?? new JsonObject());

        await _gate.WaitAsync();
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync();

            await EnsureSessionUnlockedAsync(
                tx,
                event_.SessionId,
                projectId,
                event_.Agent,
                event_.Timestamp,
                gitHead);

            // Same conversation_id can end and later continue; revive the row.
            if (event_.EventType != EventType.SessionEnded)
            {
                await ReopenSessionIfCompletedUnlockedAsync(tx, event_.SessionId);
            }

            long nextSeq;
            await using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    SELECT COALESCE(MAX(sequence), 0) + 1
                    FROM events
                    WHERE session_id = $sid
                    """;
                cmd.Parameters.AddWithValue("$sid", event_.SessionId);
                nextSeq = (long)(await cmd.ExecuteScalarAsync() ?? 1L);
            }

            event_.Sequence = (ulong)nextSeq;

            var jsonlPath = Jsonl.PathFor(_root, event_.SessionId, event_.Timestamp);
            try
            {
                Jsonl.AppendLine(jsonlPath, WithPayload(event_, sealedPayload));
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                throw new StorageException($"io error: {ex.Message}", ex);
            }

            try
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO events (id, session_id, sequence, event_type, timestamp, agent, payload,
                        actor, org_id, envelope_project_id, visibility)
                    VALUES ($id, $sid, $seq, $type, $ts, $agent, $payload,
                        $actor, $org, $eproj, $vis)
                    """;
                cmd.Parameters.AddWithValue("$id", event_.EventId);
                cmd.Parameters.AddWithValue("$sid", event_.SessionId);
                cmd.Parameters.AddWithValue("$seq", nextSeq);
                cmd.Parameters.AddWithValue("$type", event_.EventType.AsString());
                cmd.Parameters.AddWithValue("$ts", event_.Timestamp.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$agent", event_.Agent);
                cmd.Parameters.AddWithValue("$payload", sealedPayload.ToJsonString());
                cmd.Parameters.AddWithValue(
                    "$actor",
                    event_.Actor is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(event_.Actor));
                cmd.Parameters.AddWithValue("$org", (object?)event_.OrgId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$eproj", (object?)event_.ProjectId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vis", (object?)event_.Visibility ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                await tx.RollbackAsync();
                var dup = await GetEventUnlockedAsync(event_.EventId);
                if (dup is not null)
                {
                    return new AppendOutcome.Duplicate(dup);
                }

                throw new StorageException($"database error: {ex.Message}", ex);
            }

            if (event_.EventType == EventType.SessionEnded)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    UPDATE sessions
                    SET ended_at = $ended, git_head_after = $head
                    WHERE id = $sid AND ended_at IS NULL
                    """;
                cmd.Parameters.AddWithValue("$ended", event_.Timestamp.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$head", (object?)gitHead ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sid", event_.SessionId);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        finally
        {
            _gate.Release();
        }

        var command = event_.Payload?["command"]?.GetValue<string>() ?? "";
        var gitCommitDone = event_.EventType == EventType.CommandCompleted
            && Git.IsGitCommitCommand(command);

        if (sessionMissing || gitCommitDone || event_.EventType == EventType.SessionEnded)
        {
            try
            {
                await AttributeCommitsAtHeadAsync();
            }
            catch
            {
                // Best-effort provenance; never fail the append.
            }
        }

        return new AppendOutcome.Inserted(event_);
    }

    private async Task EnsureSessionUnlockedAsync(
        SqliteTransaction tx,
        string sessionId,
        string projectId,
        string agent,
        DateTimeOffset startedAt,
        string? gitHead)
    {
        await using (var check = _connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT id FROM sessions WHERE id = $id";
            check.Parameters.AddWithValue("$id", sessionId);
            var existing = await check.ExecuteScalarAsync();
            if (existing is not null)
            {
                return;
            }
        }

        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO sessions (id, project_id, agent, started_at, ended_at, git_head, git_head_after)
            VALUES ($id, $pid, $agent, $started, NULL, $head, NULL)
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$pid", projectId);
        cmd.Parameters.AddWithValue("$agent", agent);
        cmd.Parameters.AddWithValue("$started", startedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$head", (object?)gitHead ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReopenSessionIfCompletedUnlockedAsync(SqliteTransaction tx, string sessionId)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            UPDATE sessions
            SET ended_at = NULL, git_head_after = NULL
            WHERE id = $sid AND ended_at IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<SessionRecord?> GetSessionUnlockedAsync(string sessionId)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, agent, started_at, ended_at, git_head, git_head_after
            FROM sessions
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return ReadSession(reader);
    }

    private async Task<AgentEvent?> GetEventUnlockedAsync(string eventId)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, session_id, sequence, event_type, timestamp, agent, payload,
                       actor, org_id, envelope_project_id, visibility
            FROM events
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", eventId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return ReadEvent(reader);
    }

    private async Task<string?> LatestPromptEventIdUnlockedAsync(string sessionId)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM events
            WHERE session_id = $sid AND event_type = 'prompt.submitted'
            ORDER BY sequence DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RootPath = reader.GetString(1),
        GitHead = reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedAt = ParseTime(reader.GetString(3)),
    };

    private static SessionRecord ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProjectId = reader.GetString(1),
        Agent = reader.GetString(2),
        StartedAt = ParseTime(reader.GetString(3)),
        EndedAt = reader.IsDBNull(4) ? null : ParseTime(reader.GetString(4)),
        GitHead = reader.IsDBNull(5) ? null : reader.GetString(5),
        GitHeadAfter = reader.IsDBNull(6) ? null : reader.GetString(6),
    };

    /// <summary>
    /// Returns the event with its payload decrypted. If the session key was deleted (forgotten)
    /// or the ciphertext is damaged, readers get a placeholder instead of an error.
    /// </summary>
    private JsonNode OpenPayload(string eventId, string sessionId, JsonNode stored)
    {
        if (!PayloadCrypto.IsEncrypted(stored))
        {
            return stored;
        }

        var key = PayloadCrypto.TryGetKey(_root, sessionId);
        if (key is null)
        {
            return PayloadCrypto.Placeholder("session key deleted");
        }

        try
        {
            return PayloadCrypto.Decrypt(key, eventId, sessionId, stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return PayloadCrypto.Placeholder("payload failed authentication");
        }
    }

    private static AgentEvent WithPayload(AgentEvent source, JsonNode payload) => new()
    {
        Version = source.Version,
        EventId = source.EventId,
        SessionId = source.SessionId,
        Sequence = source.Sequence,
        Timestamp = source.Timestamp,
        Agent = source.Agent,
        EventType = source.EventType,
        Payload = payload,
        Actor = source.Actor,
        OrgId = source.OrgId,
        ProjectId = source.ProjectId,
        Visibility = source.Visibility,
    };

    /// <summary>Deletes the session's encryption key (crypto-shredding). Returns false if none existed.</summary>
    public bool ForgetSessionKey(string sessionId) => PayloadCrypto.DeleteKey(_root, sessionId);

    /// <summary>
    /// Flushes the WAL into the main database and truncates it, so text removed by a rebuild
    /// (with secure_delete on) does not linger in the WAL file after a forget.
    /// </summary>
    public async Task CheckpointAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private AgentEvent ReadEvent(SqliteDataReader reader)
    {
        var typeStr = reader.GetString(3);
        var eventType = EventTypeExtensions.Parse(typeStr)
            ?? throw new StorageException($"unknown event_type in database: {typeStr}");
        var eventId = reader.GetString(0);
        var sessionId = reader.GetString(1);
        var payload = OpenPayload(
            eventId,
            sessionId,
            JsonNode.Parse(reader.GetString(6)) ?? new JsonObject());
        return new AgentEvent
        {
            Version = Protocol.Version,
            EventId = reader.GetString(0),
            SessionId = reader.GetString(1),
            Sequence = (ulong)reader.GetInt64(2),
            Timestamp = ParseTime(reader.GetString(4)),
            Agent = reader.GetString(5),
            EventType = eventType,
            Payload = payload,
            Actor = reader.IsDBNull(7)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<EventActor>(reader.GetString(7)),
            OrgId = reader.IsDBNull(8) ? null : reader.GetString(8),
            ProjectId = reader.IsDBNull(9) ? null : reader.GetString(9),
            // Events from v1.0 clients have no visibility; treat them as private.
            Visibility = reader.IsDBNull(10) ? Protocol.VisibilityPrivate : reader.GetString(10),
        };
    }

    private static DateTimeOffset ParseTime(string value)
    {
        if (DateTimeOffset.TryParse(value, out var dto))
        {
            return dto.ToUniversalTime();
        }

        throw new StorageException($"invalid timestamp '{value}'");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
