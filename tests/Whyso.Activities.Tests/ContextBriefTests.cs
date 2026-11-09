using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Activities.Tests;

public class ContextBriefTests
{
    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("init");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return dir;
    }

    private static AgentEvent Event(string session, EventType type, JsonObject payload) =>
        AgentEvent.New(session, "claude-code", type, payload);

    [Fact]
    public async Task Brief_lists_decisions_and_rejections_and_skips_current_session()
    {
        var dir = NewRepo();
        try
        {
            var (root, config) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(Event("sess_old", EventType.PromptSubmitted,
                new JsonObject { ["prompt"] = "Add caching to the API. Don't use Redis, we have no ops for it." }), config.ProjectId);
            await store.AppendEventAsync(Event("sess_old", EventType.AgentResponse,
                new JsonObject { ["text"] = "I went with an in-memory LRU. We decided to cap it at 10k entries." }), config.ProjectId);
            await store.AppendEventAsync(Event("sess_now", EventType.PromptSubmitted,
                new JsonObject { ["prompt"] = "current session prompt we decided nothing" }), config.ProjectId);

            var brief = await ContextBrief.BuildMarkdownAsync(store, new ContextBriefOptions { ExcludeSessionId = "sess_now" });

            Assert.Contains("sess_old", brief);
            Assert.DoesNotContain("sess_now", brief);
            Assert.Contains("Rejected / avoid: Don't use Redis, we have no ops for it.", brief);
            Assert.Contains("Decided: We decided to cap it at 10k entries.", brief);
            Assert.Contains("Asked: Add caching to the API.", brief);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Markdown_respects_max_chars_and_empty_input()
    {
        Assert.Equal("", ContextBrief.ToMarkdown([], 4000));
        var sessions = Enumerable.Range(0, 50).Select(i => new ContextBriefSession
        {
            SessionId = $"sess_{i}",
            Agent = "cursor",
            StartedAt = DateTimeOffset.UtcNow,
            FirstPrompt = new string('x', 200),
        }).ToList();
        Assert.True(ContextBrief.ToMarkdown(sessions, 1500).Length <= 1500 + Environment.NewLine.Length);
    }
}
