using System.Text.Json.Nodes;
using Whyso.Activities;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Activities.Tests;

public class WorkNodeTests
{
    private const string SessionId = "sess_worknode";
    private static ulong _seq;

    private static AgentEvent Ev(EventType type, JsonNode payload, DateTimeOffset? ts = null)
    {
        _seq++;
        return new AgentEvent
        {
            Version = Protocol.Version,
            EventId = Protocol.NewEventId(),
            SessionId = SessionId,
            Sequence = _seq,
            Timestamp = ts ?? DateTimeOffset.Parse("2026-08-11T10:00:00Z").AddSeconds((double)_seq),
            Agent = "cursor",
            EventType = type,
            Payload = payload,
        };
    }

    private static JsonObject Ctx(string? generationId = "g1") => new()
    {
        ["generation_id"] = generationId,
    };

    private static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var merged = new JsonObject();
        foreach (var (k, v) in a)
        {
            merged[k] = v?.DeepClone();
        }

        foreach (var (k, v) in b)
        {
            merged[k] = v?.DeepClone();
        }

        return merged;
    }

    [Fact]
    public void Preamble_keeps_orphan_file_activity()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.FileModified, Merge(Ctx(), new JsonObject
            {
                ["path"] = "early.cs",
                ["edits"] = new JsonArray(),
            })),
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "later" })),
        ]);

        Assert.Equal(2, timeline.Turns.Count);
        Assert.Contains("(session preamble", timeline.Turns[0].Prompt.Text);
        Assert.Equal("early.cs", Assert.Single(timeline.Turns[0].Artifacts.Files).Path);
        Assert.Equal("later", timeline.Turns[1].Prompt.Text);
    }

    [Fact]
    public void WorkNode_aggregates_files_commands_and_prompts()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "add auth" })),
            Ev(EventType.FileCreated, Merge(Ctx(), new JsonObject { ["path"] = "Auth.cs" })),
            Ev(EventType.CommandCompleted, Merge(Ctx(), new JsonObject
            {
                ["command"] = "dotnet test",
                ["output"] = "ok",
            })),
            Ev(EventType.TestCompleted, Merge(Ctx(), new JsonObject
            {
                ["name"] = "AuthTests",
                ["status"] = "passed",
            })),
            Ev(EventType.AgentResponse, Merge(Ctx(), new JsonObject { ["text"] = "Auth added." })),
        ],
        [
            new CommitLink
            {
                Sha = "abc1234567890",
                SessionId = SessionId,
                PromptEventId = null,
                CapturedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var session = new SessionRecord
        {
            Id = SessionId,
            ProjectId = "proj_1",
            Agent = "cursor",
            StartedAt = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-08-11T11:00:00Z"),
            GitHead = "base",
            GitHeadAfter = "abc1234567890",
        };

        var node = WorkNodeBuilder.Build(session, timeline, labels: ["feature"]);
        Assert.Equal(SessionId, node.Id);
        Assert.Contains("add auth", node.Prompts);
        Assert.Contains("Auth added.", node.Responses);
        Assert.Contains(node.FilesChanged, f => f.Path == "Auth.cs");
        Assert.Contains(node.Commands, c => c.Contains("dotnet test"));
        Assert.NotEmpty(node.Tests);
        Assert.Contains("abc1234567890", node.CommitShas);
        Assert.Equal(["feature"], node.Labels);

        var projection = WorkNodeBuilder.ToProjection(node);
        Assert.Equal(SessionId, projection.Node.Id);
        Assert.NotEmpty(projection.Files);
        Assert.True(projection.Node.FileCount > 0);
        Assert.Contains("Auth.cs", projection.Fts.Paths, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add auth", projection.Fts.Prompt);
    }

    [Fact]
    public void WorkTree_builds_file_overlap_and_temporal_edges()
    {
        var older = new WorkNodeRecord
        {
            Id = "sess_a",
            SessionId = "sess_a",
            ProjectId = "p",
            Agent = "cursor",
            StartedAt = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-08-11T10:30:00Z"),
            Status = "completed",
            PromptExcerpt = "first",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var newer = new WorkNodeRecord
        {
            Id = "sess_b",
            SessionId = "sess_b",
            ProjectId = "p",
            Agent = "cursor",
            StartedAt = DateTimeOffset.Parse("2026-08-11T11:00:00Z"),
            EndedAt = DateTimeOffset.Parse("2026-08-11T11:30:00Z"),
            Status = "completed",
            PromptExcerpt = "second",
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var files = new Dictionary<string, IReadOnlyList<string>>
        {
            ["sess_a"] = ["src/A.cs"],
            ["sess_b"] = ["src/A.cs", "src/B.cs"],
        };
        var commits = new Dictionary<string, IReadOnlyList<string>>();

        var edges = WorkTreeBuilder.BuildAutoEdges(
            gitRoot: Directory.GetCurrentDirectory(),
            nodes: [older, newer],
            filesByNode: files,
            commitsByNode: commits);

        Assert.Contains(edges, e =>
            e.FromId == "sess_a"
            && e.ToId == "sess_b"
            && e.Kind == WorkNodeEdgeKinds.FileOverlap);

        var noFiles = WorkTreeBuilder.BuildAutoEdges(
            Directory.GetCurrentDirectory(),
            [older, newer],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["sess_a"] = [],
                ["sess_b"] = [],
            },
            commits);

        Assert.Contains(noFiles, e => e.Kind == WorkNodeEdgeKinds.Temporal);
    }

    [Fact]
    public void Provenance_hit_exposes_work_node_id()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "x" })),
            Ev(EventType.FileModified, Merge(Ctx(), new JsonObject
            {
                ["path"] = "f.cs",
                ["edits"] = new JsonArray(),
            })),
        ]);

        var hit = Assert.Single(
            ProvenanceQuery.Trace(new ProvenanceSubject.FilePath("f.cs"), [timeline]).Hits);
        Assert.Equal(SessionId, hit.WorkNodeId);
    }
}
