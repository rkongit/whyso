namespace Whyso.Storage;

public static class WhysoPaths
{
    public const string WhysoDirName = ".whyso";

    public const string DbFileName = "whyso.db";

    public static string WhysoDir(string root) => Path.Combine(root, WhysoDirName);

    public static string DbPath(string root) => Path.Combine(WhysoDir(root), DbFileName);

    public static string EventsDir(string root) => Path.Combine(WhysoDir(root), "events");

    public static string ConfigPath(string root) => Path.Combine(WhysoDir(root), "config.toml");

    public static string FindWhysoRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (true)
        {
            if (Directory.Exists(Path.Combine(current, WhysoDirName)))
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
