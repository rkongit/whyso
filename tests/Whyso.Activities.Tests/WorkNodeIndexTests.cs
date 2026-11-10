using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Activities.Tests;

public class WorkNodeIndexTests
{
    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("init");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return dir;
    }

    private static AgentEvent Prompt(string session, string agent, string text) =>
        AgentEvent.New(session, agent, EventType.PromptSubmitted, new JsonObject { ["prompt"] = text });

    [Fact]
    public async Task RefreshStale_projects_running_sessions_that_never_sent_session_end()
    {
        var dir = NewRepo();
        try
        {
            var (root, config) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);

            // An older session that already has a projection.
            await store.AppendEventAsync(Prompt("sess_claude", "claude-code", "modernize the UI"), config.ProjectId);
            Assert.Equal(1, await WorkNodeIndex.RefreshStaleAsync(store, root));

            // A Cursor session starts afterwards and is still running: no session-end event.
            await store.AppendEventAsync(Prompt("sess_cursor", "cursor", "hi"), config.ProjectId);
            Assert.Equal(1, await WorkNodeIndex.RefreshStaleAsync(store, root));

            var nodes = await store.ListWorkNodesAsync();
            Assert.Equal(2, nodes.Count);
            Assert.Contains(nodes, n => n.Id == "sess_cursor" && n.Agent == "cursor" && n.TurnCount == 1);

            // Nothing new: nothing to do.
            Assert.Equal(0, await WorkNodeIndex.RefreshStaleAsync(store, root));

            // More events on the running session make only that session stale again.
            await store.AppendEventAsync(Prompt("sess_cursor", "cursor", "track the recent changes"), config.ProjectId);
            Assert.Equal(["sess_cursor"], await store.ListStaleWorkNodeSessionIdsAsync());
            Assert.Equal(1, await WorkNodeIndex.RefreshStaleAsync(store, root));
            Assert.Equal(2, (await store.GetWorkNodeAsync("sess_cursor"))!.TurnCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
