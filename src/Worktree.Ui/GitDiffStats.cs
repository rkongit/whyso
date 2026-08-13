using System.Diagnostics;
using System.Text;

namespace Worktree.Ui;

/// <summary>Best-effort git numstat between two SHAs. Returns empty on failure.</summary>
public static class GitDiffStats
{
    public static IReadOnlyDictionary<string, GitFileStatDto> Numstat(string root, string? fromSha, string? toSha)
    {
        if (string.IsNullOrWhiteSpace(fromSha) || string.IsNullOrWhiteSpace(toSha) || fromSha == toSha)
        {
            return new Dictionary<string, GitFileStatDto>(StringComparer.OrdinalIgnoreCase);
        }

        var (exit, stdout, _) = Run(root, "diff", "--numstat", $"{fromSha}..{toSha}");
        if (exit != 0)
        {
            return new Dictionary<string, GitFileStatDto>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, GitFileStatDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(parts[0], out var add))
            {
                add = 0;
            }

            if (!int.TryParse(parts[1], out var del))
            {
                del = 0;
            }

            var path = parts[2].Trim();
            if (path.Length == 0)
            {
                continue;
            }

            map[path] = new GitFileStatDto
            {
                Path = path,
                Additions = add,
                Deletions = del,
            };
        }

        return map;
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
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (1, "", "failed to start git");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (1, "", ex.Message);
        }
    }
}
