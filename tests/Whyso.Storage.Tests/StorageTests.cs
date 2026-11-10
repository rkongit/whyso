using System.Diagnostics;
using System.Text.Json.Nodes;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Storage.Tests;

public class StorageTests
{
    private static string InitGitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Run(path, "git", "init");
        Run(path, "git", "config", "user.email", "test@example.com");
        Run(path, "git", "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, "README.md"), "test");
        Run(path, "git", "add", "README.md");
        Run(path, "git", "commit", "-m", "init");
        return path;
    }

    private static void Run(string cwd, string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
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
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static AgentEvent SampleEvent(string sessionId, EventType eventType) => new()
    {
        Version = Protocol.Version,
        EventId = Protocol.NewEventId(),
        SessionId = sessionId,
        Sequence = null,
        Timestamp = DateTimeOffset.UtcNow,
        Agent = "cursor",
        EventType = eventType,
        Payload = JsonNode.Parse("""{"path":"src/main.rs"}""")!,
    };

    [Fact]
    public async Task Init_project_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root1, cfg1) = await Store.InitProjectAsync(dir);
            var (root2, cfg2) = await Store.InitProjectAsync(dir);
            Assert.Equal(root1, root2);
            Assert.Equal(cfg1.ProjectId, cfg2.ProjectId);
            Assert.True(File.Exists(Path.Combine(root1, ".whyso", "whyso.db")));
            Assert.True(Directory.Exists(Path.Combine(root1, ".whyso", "events")));
            Assert.True(File.Exists(Path.Combine(root1, ".whyso", "config.toml")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Append_event_assigns_sequence_and_orders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            const string session = "sess_order_test";
            var ids = new List<string>();
            foreach (var eventType in new[]
                     {
                         EventType.SessionStarted, EventType.PromptSubmitted, EventType.FileModified,
                     })
            {
                var event_ = SampleEvent(session, eventType);
                ids.Add(event_.EventId);
                var outcome = await store.AppendEventAsync(event_, cfg.ProjectId);
                Assert.IsType<AppendOutcome.Inserted>(outcome);
            }

            var events = await store.ListEventsAsync(session);
            Assert.Equal(3, events.Count);
            Assert.Equal(1UL, events[0].Sequence);
            Assert.Equal(2UL, events[1].Sequence);
            Assert.Equal(3UL, events[2].Sequence);
            Assert.Equal(ids[0], events[0].EventId);
            Assert.Equal(EventType.FileModified, events[2].EventType);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Count_prompts_counts_only_prompt_submitted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            Assert.Equal(0, await store.CountPromptsAsync());

            await store.AppendEventAsync(SampleEvent("sess_p", EventType.SessionStarted), cfg.ProjectId);
            var p1 = SampleEvent("sess_p", EventType.PromptSubmitted);
            p1.Payload = JsonNode.Parse("""{"prompt":"one"}""")!;
            await store.AppendEventAsync(p1, cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_p", EventType.FileModified), cfg.ProjectId);
            var p2 = SampleEvent("sess_p", EventType.PromptSubmitted);
            p2.Payload = JsonNode.Parse("""{"prompt":"two"}""")!;
            await store.AppendEventAsync(p2, cfg.ProjectId);

            Assert.Equal(2, await store.CountPromptsAsync());
            Assert.Equal(4, await store.CountEventsAsync());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Duplicate_event_id_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            var event_ = SampleEvent("sess_dup", EventType.PromptSubmitted);
            var first = await store.AppendEventAsync(event_, cfg.ProjectId);
            var second = await store.AppendEventAsync(event_, cfg.ProjectId);
            var a = Assert.IsType<AppendOutcome.Inserted>(first);
            var b = Assert.IsType<AppendOutcome.Duplicate>(second);
            Assert.Equal(a.Event.Sequence, b.Event.Sequence);
            Assert.Equal(a.Event.EventId, b.Event.EventId);
            Assert.Single(await store.ListEventsAsync("sess_dup"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Session_ended_marks_session_completed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(SampleEvent("sess_end", EventType.SessionStarted), cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_end", EventType.SessionEnded), cfg.ProjectId);
            var session = await store.GetSessionAsync("sess_end");
            Assert.NotNull(session);
            Assert.NotNull(session!.EndedAt);
            Assert.Equal("completed", session.Status.AsString());
            Assert.NotNull(session.GitHead);
            Assert.Equal(session.GitHead, session.GitHeadAfter);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Completed_session_reopens_on_prompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(SampleEvent("sess_reopen", EventType.SessionStarted), cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_reopen", EventType.SessionEnded), cfg.ProjectId);

            var ended = await store.GetSessionAsync("sess_reopen");
            Assert.NotNull(ended!.EndedAt);
            Assert.Equal(SessionStatus.Completed, ended.Status);

            var prompt = SampleEvent("sess_reopen", EventType.PromptSubmitted);
            prompt.Payload = JsonNode.Parse("""{"prompt":"continue"}""")!;
            await store.AppendEventAsync(prompt, cfg.ProjectId);

            var reopened = await store.GetSessionAsync("sess_reopen");
            Assert.NotNull(reopened);
            Assert.Null(reopened!.EndedAt);
            Assert.Null(reopened.GitHeadAfter);
            Assert.Equal(SessionStatus.Active, reopened.Status);
            Assert.NotNull(reopened.GitHead);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Completed_session_reopens_on_session_started()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(SampleEvent("sess_restart", EventType.SessionStarted), cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_restart", EventType.SessionEnded), cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_restart", EventType.SessionStarted), cfg.ProjectId);

            var session = await store.GetSessionAsync("sess_restart");
            Assert.NotNull(session);
            Assert.Null(session!.EndedAt);
            Assert.Null(session.GitHeadAfter);
            Assert.Equal(SessionStatus.Active, session.Status);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Reopened_session_can_complete_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(SampleEvent("sess_cycle", EventType.SessionStarted), cfg.ProjectId);

            var firstEnd = SampleEvent("sess_cycle", EventType.SessionEnded);
            firstEnd.Timestamp = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
            await store.AppendEventAsync(firstEnd, cfg.ProjectId);

            var prompt = SampleEvent("sess_cycle", EventType.PromptSubmitted);
            prompt.Payload = JsonNode.Parse("""{"prompt":"again"}""")!;
            await store.AppendEventAsync(prompt, cfg.ProjectId);

            var secondEnd = SampleEvent("sess_cycle", EventType.SessionEnded);
            secondEnd.Timestamp = DateTimeOffset.Parse("2026-08-11T11:00:00Z");
            await store.AppendEventAsync(secondEnd, cfg.ProjectId);

            var session = await store.GetSessionAsync("sess_cycle");
            Assert.NotNull(session);
            Assert.Equal(SessionStatus.Completed, session!.Status);
            Assert.Equal(secondEnd.Timestamp, session.EndedAt);
            Assert.NotNull(session.GitHeadAfter);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Redundant_session_ended_keeps_first_ended_at()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(SampleEvent("sess_dup_end", EventType.SessionStarted), cfg.ProjectId);

            var firstEnd = SampleEvent("sess_dup_end", EventType.SessionEnded);
            firstEnd.Timestamp = DateTimeOffset.Parse("2026-08-11T10:00:00Z");
            await store.AppendEventAsync(firstEnd, cfg.ProjectId);

            var secondEnd = SampleEvent("sess_dup_end", EventType.SessionEnded);
            secondEnd.Timestamp = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
            await store.AppendEventAsync(secondEnd, cfg.ProjectId);

            var session = await store.GetSessionAsync("sess_dup_end");
            Assert.NotNull(session);
            Assert.Equal(firstEnd.Timestamp, session!.EndedAt);
            Assert.Equal(SessionStatus.Completed, session.Status);

            var events = await store.ListEventsAsync("sess_dup_end");
            Assert.Equal(3, events.Count);
            Assert.Equal(2, events.Count(e => e.EventType == EventType.SessionEnded));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Git_commit_is_linked_to_session_and_prompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);

            var prompt = SampleEvent("sess_prov", EventType.PromptSubmitted);
            prompt.Payload = JsonNode.Parse("""{"prompt":"add feature"}""")!;
            var promptId = prompt.EventId;
            await store.AppendEventAsync(prompt, cfg.ProjectId);

            File.WriteAllText(Path.Combine(dir, "feature.txt"), "x");
            Run(dir, "git", "add", "feature.txt");
            Run(dir, "git", "commit", "-m", "feat: from session");
            var head = Git.DetectGitHead(dir)!;

            var commitEvt = SampleEvent("sess_prov", EventType.CommandCompleted);
            commitEvt.Payload = new JsonObject
            {
                ["command"] = "git commit -m feat",
                ["cwd"] = dir,
            };
            await store.AppendEventAsync(commitEvt, cfg.ProjectId);

            var commits = await store.ListSessionCommitsAsync("sess_prov");
            Assert.Single(commits);
            Assert.Equal(head, commits[0].Sha);
            Assert.Equal(promptId, commits[0].PromptEventId);

            var bySha = await store.ListCommitsByShaAsync(head[..7]);
            Assert.Single(bySha);
            Assert.Equal(head, bySha[0].Sha);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task One_commit_links_to_multiple_sessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);

            var p1 = SampleEvent("sess_a", EventType.PromptSubmitted);
            p1.Payload = JsonNode.Parse("""{"prompt":"first change"}""")!;
            await store.AppendEventAsync(p1, cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_a", EventType.SessionEnded), cfg.ProjectId);

            var p2 = SampleEvent("sess_b", EventType.PromptSubmitted);
            p2.Payload = JsonNode.Parse("""{"prompt":"second change"}""")!;
            await store.AppendEventAsync(p2, cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent("sess_b", EventType.SessionEnded), cfg.ProjectId);

            File.WriteAllText(Path.Combine(dir, "both.txt"), "ab");
            Run(dir, "git", "add", "both.txt");
            Run(dir, "git", "commit", "-m", "both sessions");
            var head = Git.DetectGitHead(dir)!;

            await store.AppendEventAsync(SampleEvent("sess_c", EventType.SessionStarted), cfg.ProjectId);

            var a = await store.ListSessionCommitsAsync("sess_a");
            var b = await store.ListSessionCommitsAsync("sess_b");
            Assert.Single(a);
            Assert.Single(b);
            Assert.Equal(head, a[0].Sha);
            Assert.Equal(head, b[0].Sha);
            Assert.NotNull(a[0].PromptEventId);
            Assert.NotNull(b[0].PromptEventId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Commits_attributed_when_session_started_on_empty_repo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run(dir, "git", "init");
        Run(dir, "git", "config", "user.email", "test@example.com");
        Run(dir, "git", "config", "user.name", "Test");
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);

            // First event creates session while HEAD does not exist yet.
            Assert.Null(Git.DetectGitHead(dir));
            var prompt = SampleEvent("sess_empty", EventType.PromptSubmitted);
            prompt.Payload = JsonNode.Parse("""{"prompt":"bootstrap"}""")!;
            await store.AppendEventAsync(prompt, cfg.ProjectId);

            var session = await store.GetSessionAsync("sess_empty");
            Assert.NotNull(session);
            Assert.Null(session!.GitHead);

            File.WriteAllText(Path.Combine(dir, "app.txt"), "hello");
            Run(dir, "git", "add", "app.txt");
            Run(dir, "git", "commit", "-m", "initial from session");
            var head = Git.DetectGitHead(dir)!;

            await store.AppendEventAsync(SampleEvent("sess_empty", EventType.SessionEnded), cfg.ProjectId);

            var commits = await store.ListSessionCommitsAsync("sess_empty");
            Assert.Single(commits);
            Assert.Equal(head, commits[0].Sha);

            session = await store.GetSessionAsync("sess_empty");
            Assert.Equal(head, session!.GitHeadAfter);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Jsonl_append_and_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, _) = await Store.InitProjectAsync(dir);
            var event_ = SampleEvent("sess_jsonl", EventType.FileModified);
            var path = Jsonl.PathFor(root, event_.SessionId, event_.Timestamp);
            Jsonl.AppendLine(path, event_);
            var events = Jsonl.ReadEvents(path);
            Assert.Single(events);
            Assert.Equal(event_.EventId, events[0].EventId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Jsonl_rejects_malformed_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bad.jsonl");
            File.WriteAllText(path, "{\"ok\":true}\nnot-json\n");
            var ex = Assert.Throws<StorageException>(() => Jsonl.ReadEvents(path));
            Assert.Contains("malformed JSONL", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Append_writes_jsonl_source_of_truth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            var event_ = SampleEvent("sess_both", EventType.CommandStarted);
            var outcome = await store.AppendEventAsync(event_, cfg.ProjectId);
            var stored = Assert.IsType<AppendOutcome.Inserted>(outcome).Event;
            var path = Jsonl.PathFor(root, "sess_both", stored.Timestamp);
            var fromJsonl = Jsonl.ReadEvents(path);
            Assert.Single(fromJsonl);
            Assert.Equal(1UL, fromJsonl[0].Sequence);
            Assert.Equal(stored.EventId, fromJsonl[0].EventId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Detects_git_commit_commands()
    {
        Assert.True(Git.IsGitCommitCommand("git commit -m msg"));
        Assert.True(Git.IsGitCommitCommand("C:/tools/git.exe commit -am x"));
        Assert.False(Git.IsGitCommitCommand("git status"));
        Assert.False(Git.IsGitCommitCommand("cargo test"));
    }

    [Fact]
    public async Task Work_node_projection_and_fts_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
        InitGitRepo(dir);
        try
        {
            var (root, cfg) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            const string session = "sess_wn_proj";
            await store.AppendEventAsync(SampleEvent(session, EventType.SessionStarted), cfg.ProjectId);
            await store.AppendEventAsync(SampleEvent(session, EventType.PromptSubmitted), cfg.ProjectId);

            var projection = new WorkNodeProjection
            {
                Node = new WorkNodeRecord
                {
                    Id = session,
                    SessionId = session,
                    ProjectId = cfg.ProjectId,
                    Agent = "cursor",
                    StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    EndedAt = DateTimeOffset.UtcNow,
                    Status = "completed",
                    PromptExcerpt = "Implement JWT authentication",
                    TurnCount = 1,
                    FileCount = 1,
                    CommandCount = 0,
                    TestCount = 0,
                    CommitCount = 0,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                Files =
                [
                    new WorkNodeFileRecord
                    {
                        WorkNodeId = session,
                        Path = "src/auth/AuthService.java",
                        Action = "modified",
                        FirstSeen = DateTimeOffset.UtcNow.AddMinutes(-30),
                        LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10),
                    },
                ],
                Commands = [],
                Tests = [],
                Fts = new WorkNodeFtsDocument
                {
                    WorkNodeId = session,
                    Prompt = "Implement JWT authentication",
                    Plan = "Add AuthService",
                    Paths = "src/auth/AuthService.java",
                    Commands = "",
                    Tests = "",
                    Commits = "",
                },
            };

            await store.UpsertWorkNodeProjectionAsync(projection);
            var loaded = await store.GetWorkNodeAsync(session);
            Assert.NotNull(loaded);
            Assert.Equal("Implement JWT authentication", loaded!.PromptExcerpt);

            var files = await store.ListWorkNodeFilesAsync(path: "AuthService.java");
            Assert.Single(files);

            await store.AddWorkNodeLabelAsync(session, "feature");
            await store.UpsertManualEdgeAsync(new WorkNodeEdgeRecord
            {
                FromId = session,
                ToId = session,
                Kind = "manual",
                Label = "self-check",
                Manual = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            // Self-edge is odd but validates insert; use second node for a real edge.
            const string session2 = "sess_wn_proj_2";
            await store.AppendEventAsync(SampleEvent(session2, EventType.SessionStarted), cfg.ProjectId);
            var projection2 = new WorkNodeProjection
            {
                Node = new WorkNodeRecord
                {
                    Id = session2,
                    SessionId = session2,
                    ProjectId = cfg.ProjectId,
                    Agent = "cursor",
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = "active",
                    PromptExcerpt = "follow up",
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                Files = [],
                Commands = [],
                Tests = [],
                Fts = new WorkNodeFtsDocument
                {
                    WorkNodeId = session2,
                    Prompt = "follow up JWT",
                },
            };
            await store.UpsertWorkNodeProjectionAsync(projection2);
            await store.DeleteEdgeAsync(session, session, "manual");
            await store.UpsertManualEdgeAsync(new WorkNodeEdgeRecord
            {
                FromId = session,
                ToId = session2,
                Kind = "manual",
                Label = "feature",
                Manual = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var edges = await store.ListWorkNodeEdgesAsync(session);
            Assert.Contains(edges, e => e.Manual && e.ToId == session2);

            var hits = await store.SearchWorkNodesAsync("JWT");
            Assert.Contains(hits, h => h.WorkNodeId == session);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
