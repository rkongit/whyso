namespace Whyso.Storage;

internal static class Migrator
{
    private static readonly string[] MigrationNames =
    [
        "001_init.sql",
        "002_session_git_range.sql",
        "003_session_commits.sql",
        "004_session_commits_unique_sha.sql",
        "005_session_commits_many_sessions.sql",
        "006_work_nodes.sql",
        "007_envelope_v1_1.sql",
    ];

    public static void Migrate(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS _worktree_migrations (
                    version TEXT PRIMARY KEY NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM _worktree_migrations";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        var asm = typeof(Migrator).Assembly;
        foreach (var name in MigrationNames)
        {
            if (applied.Contains(name))
            {
                continue;
            }

            var resourceName = $"Whyso.Storage.Migrations.{name}";
            using var stream = asm.GetManifestResourceStream(resourceName)
                ?? throw new StorageException($"missing migration resource: {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO _worktree_migrations (version, applied_at) VALUES ($v, $t)";
                cmd.Parameters.AddWithValue("$v", name);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }
}
