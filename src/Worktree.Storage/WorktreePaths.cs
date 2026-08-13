namespace Worktree.Storage;

public static class WorktreePaths
{
    public const string WorktreeDirName = ".ai-worktree";

    public static string WorktreeDir(string root) => Path.Combine(root, WorktreeDirName);

    public static string WorktreeDbPath(string root) => Path.Combine(WorktreeDir(root), "worktree.db");

    public static string EventsDir(string root) => Path.Combine(WorktreeDir(root), "events");

    public static string ConfigPath(string root) => Path.Combine(WorktreeDir(root), "config.toml");

    public static string FindWorktreeRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (true)
        {
            if (Directory.Exists(WorktreeDir(current)))
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
