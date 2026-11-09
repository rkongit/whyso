using System.Diagnostics;
using System.Text;

namespace Whyso.Storage;

public static class Git
{
    public static string DetectGitRoot(string start)
    {
        var (exit, stdout, _) = Run(start, "rev-parse", "--show-toplevel");
        if (exit != 0)
        {
            throw StorageException.NotAGitRepository();
        }

        var root = stdout.Trim();
        if (string.IsNullOrEmpty(root))
        {
            throw StorageException.NotAGitRepository();
        }

        return root;
    }

    public static string? DetectGitHead(string root)
    {
        var (exit, stdout, _) = Run(root, "rev-parse", "HEAD");
        if (exit != 0)
        {
            return null;
        }

        var head = stdout.Trim();
        return string.IsNullOrEmpty(head) ? null : head;
    }

    public static IReadOnlyList<string> CommitsSince(string root, string? sinceSha)
    {
        var head = DetectGitHead(root);
        if (head is null)
        {
            return [];
        }

        if (sinceSha is null)
        {
            return [head];
        }

        if (sinceSha == head)
        {
            return [];
        }

        var range = $"{sinceSha}..{head}";
        var (exit, stdout, _) = Run(root, "log", "--reverse", "--format=%H", range);
        if (exit != 0)
        {
            return [head];
        }

        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    /// <summary>
    /// Commits with committer time at/after <paramref name="since"/> (UTC), oldest first.
    /// Used when a session has no <c>git_head</c> baseline (e.g. started on an empty repo).
    /// </summary>
    public static IReadOnlyList<string> CommitsSinceTime(string root, DateTimeOffset since)
    {
        var head = DetectGitHead(root);
        if (head is null)
        {
            return [];
        }

        // git --since is committer-date based; small skew covers clock skew / same-second commits.
        var sinceArg = since.ToUniversalTime().AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss");
        var (exit, stdout, _) = Run(
            root,
            "log",
            "--reverse",
            "--format=%H",
            $"--since={sinceArg}");
        if (exit != 0)
        {
            return [];
        }

        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    public static bool IsAncestor(string root, string ancestor, string descendant)
    {
        if (ancestor == descendant)
        {
            return true;
        }

        var (exit, _, _) = Run(root, "merge-base", "--is-ancestor", ancestor, descendant);
        return exit == 0;
    }

    public static bool IsGitCommitCommand(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var baseName = Path.GetFileName(tokens[0]).ToLowerInvariant();
        if (baseName is not ("git" or "git.exe"))
        {
            return false;
        }

        return tokens.Skip(1).Any(t => t == "commit");
    }

    /// <summary>Notes ref used for provenance: <c>refs/notes/whyso</c>.</summary>
    public const string NotesRef = "whyso";

    /// <summary>Writes (or replaces) the Whyso note on a commit. Returns false if git failed.</summary>
    public static bool WriteNote(string root, string sha, string message)
    {
        var (exit, _, _) = Run(root, "notes", $"--ref={NotesRef}", "add", "-f", "-m", message, sha);
        return exit == 0;
    }

    /// <summary>Reads the Whyso note on a commit, or null when there is none.</summary>
    public static string? ReadNote(string root, string sha)
    {
        var (exit, stdout, _) = Run(root, "notes", $"--ref={NotesRef}", "show", sha);
        return exit == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : null;
    }

    /// <summary>Resolves a full or abbreviated SHA to the full commit SHA.</summary>
    public static string? ResolveCommit(string root, string rev)
    {
        var (exit, stdout, _) = Run(root, "rev-parse", "--verify", "--quiet", rev + "^{commit}");
        return exit == 0 ? stdout.Trim() : null;
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw StorageException.Git("failed to start git");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw StorageException.Git($"failed to run git: {ex.Message}");
        }
    }
}
