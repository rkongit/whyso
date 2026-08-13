namespace Worktree.Activities;

internal static class PathNormalize
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path.Trim().Replace('\\', '/');
        while (trimmed.Contains("//", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);
        }

        return trimmed.TrimStart('/');
    }

    public static bool EqualsPath(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool EndsWithPath(string? full, string? suffix)
    {
        var a = Normalize(full);
        var b = Normalize(suffix);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase);
    }
}
