namespace Whyso.Storage;

public static class WorktreePaths
{
    public const string WorktreeDirName = ".whyso";

    /// <summary>Data directory name used before the Worktree → Whyso rename. Read for one release.</summary>
    public const string LegacyDirName = ".ai-worktree";

    /// <summary>
    /// Active data directory. Falls back to the legacy directory only when it exists and
    /// the new one does not (e.g. the move failed because a daemon held a file open).
    /// </summary>
    public static string WorktreeDir(string root)
    {
        var current = Path.Combine(root, WorktreeDirName);
        if (!Directory.Exists(current))
        {
            var legacy = Path.Combine(root, LegacyDirName);
            if (Directory.Exists(legacy))
            {
                return legacy;
            }
        }

        return current;
    }

    /// <summary>
    /// Moves a legacy <c>.ai-worktree/</c> directory to <c>.whyso/</c>. Best effort: if the move
    /// fails, the legacy directory keeps being used and a warning is printed.
    /// </summary>
    public static void TryMigrateLegacyDir(string root)
    {
        var legacy = Path.Combine(root, LegacyDirName);
        var current = Path.Combine(root, WorktreeDirName);
        if (!Directory.Exists(legacy) || Directory.Exists(current))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, current);
            Console.Error.WriteLine($"whyso: migrated {LegacyDirName}/ to {WorktreeDirName}/");
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(
                $"whyso: warning: could not move {LegacyDirName}/ to {WorktreeDirName}/ ({ex.Message}); " +
                "still using the old directory. Stop the daemon and run `whyso init` to finish the move.");
        }
    }

    public static string WorktreeDbPath(string root) => Path.Combine(WorktreeDir(root), "worktree.db");

    public static string EventsDir(string root) => Path.Combine(WorktreeDir(root), "events");

    public static string ConfigPath(string root) => Path.Combine(WorktreeDir(root), "config.toml");

    public static string FindWorktreeRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (true)
        {
            if (Directory.Exists(Path.Combine(current, WorktreeDirName))
                || Directory.Exists(Path.Combine(current, LegacyDirName)))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                throw StorageException.NotInitialized();
            }

            current = parent.FullName;
        }
    }
}
