using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Storage.Tests;

public class GitNotesTests
{
    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return stdout.Trim();
    }

    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Git(dir, "init");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        Git(dir, "add", "a.txt");
        Git(dir, "commit", "-m", "init");
        return dir;
    }

    private static AgentEvent Event(EventType type, JsonObject payload) =>
        AgentEvent.New("sess_notes", "claude-code", type, payload);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_during_session_gets_note_only_when_enabled(bool enabled)
    {
        var dir = NewRepo();
        try
        {
            var (root, config) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            store.WriteGitNotes = enabled;

            await store.AppendEventAsync(Event(EventType.SessionStarted, new JsonObject()), config.ProjectId);
            await store.AppendEventAsync(Event(EventType.PromptSubmitted, new JsonObject { ["prompt"] = "private prompt text" }), config.ProjectId);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "2");
            Git(dir, "commit", "-am", "change");
            var sha = Git(dir, "rev-parse", "HEAD");
            await store.AppendEventAsync(Event(EventType.CommandCompleted, new JsonObject { ["command"] = "git commit -am change" }), config.ProjectId);

            var note = Whyso.Storage.Git.ReadNote(root, sha);
            if (!enabled)
            {
                Assert.Null(note);
                return;
            }

            Assert.NotNull(note);
            var json = JsonNode.Parse(note!)!;
            Assert.Equal("sess_notes", json["sessions"]![0]!["session_id"]!.GetValue<string>());
            Assert.Equal("claude-code", json["sessions"]![0]!["agent"]!.GetValue<string>());
            Assert.DoesNotContain("private prompt text", note);
            Assert.Equal(sha, Whyso.Storage.Git.ResolveCommit(root, sha[..8]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
