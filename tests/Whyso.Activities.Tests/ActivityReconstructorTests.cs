using System.Text.Json.Nodes;
using Whyso.Activities;
using Whyso.Events;

namespace Whyso.Activities.Tests;

public class ActivityReconstructorTests
{
    private const string SessionId = "sess_test-session";
    private static ulong _seq;

    private static AgentEvent Ev(
        EventType type,
        JsonNode payload,
        string? eventId = null,
        DateTimeOffset? ts = null)
    {
        _seq++;
        return new AgentEvent
        {
            Version = Protocol.Version,
            EventId = eventId ?? Protocol.NewEventId(),
            SessionId = SessionId,
            Sequence = _seq,
            Timestamp = ts ?? DateTimeOffset.Parse("2026-08-11T10:00:00Z").AddSeconds((double)_seq),
            Agent = "cursor",
            EventType = type,
            Payload = payload,
        };
    }

    private static JsonObject Ctx(string? generationId = null) => new()
    {
        ["model"] = "test-model",
        ["generation_id"] = generationId,
    };

    [Fact]
    public void Pairs_tool_start_complete_by_tool_use_id()
    {
        _seq = 0;
        var events = new List<AgentEvent>
        {
            Ev(EventType.PromptSubmitted, Merge(Ctx("g1"), new JsonObject { ["prompt"] = "edit auth" })),
            Ev(EventType.ToolStarted, Merge(Ctx("g1"), new JsonObject
            {
                ["tool_name"] = "Read",
                ["tool_use_id"] = "tu_1",
                ["tool_input"] = new JsonObject { ["path"] = "a.cs" },
            })),
            Ev(EventType.ToolCompleted, Merge(Ctx("g1"), new JsonObject
            {
                ["tool_name"] = "Read",
                ["tool_use_id"] = "tu_1",
                ["tool_output"] = "ok",
                ["duration"] = 12,
            })),
            Ev(EventType.AgentResponse, Merge(Ctx("g1"), new JsonObject { ["text"] = "Done reading." })),
        };

        var timeline = ActivityReconstructor.Reconstruct(events);
        Assert.Single(timeline.Turns);
        var turn = timeline.Turns[0];
        var tool = Assert.Single(turn.Actions.OfType<ActivityItem.Tool>());
        Assert.Equal(ToolPhase.Completed, tool.Phase);
        Assert.Equal("tu_1", tool.ToolUseId);
        Assert.Equal("Read", tool.Name);
        Assert.NotNull(tool.StartMeta);
        Assert.NotNull(tool.EndMeta);
        Assert.Equal("Done reading.", turn.PlanText);
    }

    [Fact]
    public void Incomplete_tool_pair_remains_visible_as_started()
    {
        _seq = 0;
        var events = new List<AgentEvent>
        {
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "run tool" })),
            Ev(EventType.ToolStarted, Merge(Ctx(), new JsonObject
            {
                ["tool_name"] = "Shell",
                ["tool_use_id"] = "tu_open",
            })),
        };

        var timeline = ActivityReconstructor.Reconstruct(events);
        var tool = Assert.Single(timeline.Turns[0].Actions.OfType<ActivityItem.Tool>());
        Assert.Equal(ToolPhase.Started, tool.Phase);
        Assert.Equal("Shell", tool.Name);
    }

    [Fact]
    public void Splits_turns_on_new_prompt()
    {
        _seq = 0;
        var events = new List<AgentEvent>
        {
            Ev(EventType.PromptSubmitted, Merge(Ctx("g1"), new JsonObject { ["prompt"] = "first" })),
            Ev(EventType.FileModified, Merge(Ctx("g1"), new JsonObject
            {
                ["path"] = "src/A.cs",
                ["edits"] = new JsonArray(),
            })),
            Ev(EventType.AgentResponse, Merge(Ctx("g1"), new JsonObject { ["text"] = "first plan" })),
            Ev(EventType.PromptSubmitted, Merge(Ctx("g2"), new JsonObject { ["prompt"] = "second" })),
            Ev(EventType.FileModified, Merge(Ctx("g2"), new JsonObject
            {
                ["path"] = "src/B.cs",
                ["edits"] = new JsonArray(),
            })),
            Ev(EventType.AgentResponse, Merge(Ctx("g2"), new JsonObject { ["text"] = "second plan" })),
        };

        var timeline = ActivityReconstructor.Reconstruct(events);
        Assert.Equal(2, timeline.Turns.Count);
        Assert.Equal("first", timeline.Turns[0].Prompt.Text);
        Assert.Equal("src/A.cs", Assert.Single(timeline.Turns[0].Artifacts.Files).Path);
        Assert.Equal("second", timeline.Turns[1].Prompt.Text);
        Assert.Equal("src/B.cs", Assert.Single(timeline.Turns[1].Artifacts.Files).Path);
    }

    [Fact]
    public void Generation_id_keeps_events_with_matching_turn()
    {
        _seq = 0;
        var events = new List<AgentEvent>
        {
            Ev(EventType.PromptSubmitted, Merge(Ctx("gen-a"), new JsonObject { ["prompt"] = "a" })),
            Ev(EventType.PromptSubmitted, Merge(Ctx("gen-b"), new JsonObject { ["prompt"] = "b" })),
            // Late event for gen-a after gen-b prompt — still attaches to gen-a turn.
            Ev(EventType.FileModified, Merge(Ctx("gen-a"), new JsonObject
            {
                ["path"] = "late.cs",
                ["edits"] = new JsonArray(),
            })),
        };

        var timeline = ActivityReconstructor.Reconstruct(events);
        Assert.Equal(2, timeline.Turns.Count);
        Assert.Contains(timeline.Turns[0].Artifacts.Files, f => f.Path == "late.cs");
        Assert.DoesNotContain(timeline.Turns[1].Artifacts.Files, f => f.Path == "late.cs");
    }

    [Fact]
    public void Commit_with_prompt_event_id_attributes_to_that_turn()
    {
        _seq = 0;
        var promptId = "evt_prompt-one";
        var events = new List<AgentEvent>
        {
            Ev(EventType.PromptSubmitted, Merge(Ctx("g1"), new JsonObject { ["prompt"] = "ship it" }), promptId),
            Ev(EventType.AgentResponse, Merge(Ctx("g1"), new JsonObject { ["text"] = "Committing." })),
            Ev(EventType.PromptSubmitted, Merge(Ctx("g2"), new JsonObject { ["prompt"] = "other" })),
        };

        var commits = new[]
        {
            new CommitLink
            {
                Sha = "abcdef1234567890",
                SessionId = SessionId,
                PromptEventId = promptId,
                CapturedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z"),
            },
        };

        var timeline = ActivityReconstructor.Reconstruct(events, commits);
        Assert.Contains("abcdef1234567890", timeline.Turns[0].Artifacts.CommitShas);
        Assert.Empty(timeline.Turns[1].Artifacts.CommitShas);

        var result = ProvenanceQuery.Trace(new ProvenanceSubject.Commit("abcdef1"), [timeline]);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(ProvenanceMatchKind.ExactCommit, hit.MatchKind);
        Assert.Equal("ship it", hit.Turn.Prompt.Text);
        Assert.Equal("Committing.", hit.Turn.PlanText);
    }

    [Fact]
    public void Multi_session_sha_returns_multiple_provenance_hits()
    {
        _seq = 0;
        var t1 = ActivityReconstructor.Reconstruct(
            [
                Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "from sess1" }),
                    "evt_p1"),
            ],
            [
                new CommitLink
                {
                    Sha = "deadbeefdeadbeef",
                    SessionId = SessionId,
                    PromptEventId = "evt_p1",
                    CapturedAt = DateTimeOffset.UtcNow,
                },
            ]);

        _seq = 0;
        var events2 = new List<AgentEvent>
        {
            new()
            {
                Version = Protocol.Version,
                EventId = "evt_p2",
                SessionId = "sess_other",
                Sequence = 1,
                Timestamp = DateTimeOffset.Parse("2026-08-11T11:00:00Z"),
                Agent = "cursor",
                EventType = EventType.PromptSubmitted,
                Payload = Merge(Ctx(), new JsonObject { ["prompt"] = "from sess2" }),
            },
        };
        var t2 = ActivityReconstructor.Reconstruct(
            events2,
            [
                new CommitLink
                {
                    Sha = "deadbeefdeadbeef",
                    SessionId = "sess_other",
                    PromptEventId = "evt_p2",
                    CapturedAt = DateTimeOffset.UtcNow,
                },
            ]);

        var result = ProvenanceQuery.Trace(new ProvenanceSubject.Commit("deadbeef"), [t1, t2]);
        Assert.Equal(2, result.Hits.Count);
        Assert.Contains(result.Hits, h => h.Turn.Prompt.Text == "from sess1");
        Assert.Contains(result.Hits, h => h.Turn.Prompt.Text == "from sess2");
    }

    [Fact]
    public void File_path_subject_finds_turns_that_touched_path()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "touch file" })),
            Ev(EventType.FileModified, Merge(Ctx(), new JsonObject
            {
                ["path"] = @"src\Whyso\Foo.cs",
                ["edits"] = new JsonArray(new JsonObject
                {
                    ["old_string"] = "a",
                    ["new_string"] = "b",
                }),
            })),
            Ev(EventType.AgentResponse, Merge(Ctx(), new JsonObject { ["text"] = "Updated Foo." })),
        ]);

        var result = ProvenanceQuery.Trace(new ProvenanceSubject.FilePath("src/Whyso/Foo.cs"), [timeline]);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(ProvenanceMatchKind.FileTouch, hit.MatchKind);
        Assert.Equal("touch file", hit.Turn.Prompt.Text);
    }

    [Fact]
    public void Code_range_overlaps_when_edit_has_line_fields()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "fix lines" })),
            Ev(EventType.FileModified, Merge(Ctx(), new JsonObject
            {
                ["path"] = "src/A.cs",
                ["edits"] = new JsonArray(new JsonObject
                {
                    ["start_line"] = 10,
                    ["end_line"] = 20,
                    ["old_string"] = "old",
                    ["new_string"] = "new",
                }),
            })),
        ]);

        var overlap = ProvenanceQuery.Trace(new ProvenanceSubject.CodeRange("src/A.cs", 15, 18), [timeline]);
        Assert.Equal(ProvenanceMatchKind.EditOverlap, Assert.Single(overlap.Hits).MatchKind);

        var miss = ProvenanceQuery.Trace(new ProvenanceSubject.CodeRange("src/A.cs", 50, 60), [timeline]);
        Assert.Empty(miss.Hits);
    }

    [Fact]
    public void Code_range_degrades_to_file_when_edits_lack_line_info()
    {
        _seq = 0;
        var timeline = ActivityReconstructor.Reconstruct(
        [
            Ev(EventType.PromptSubmitted, Merge(Ctx(), new JsonObject { ["prompt"] = "no lines" })),
            Ev(EventType.FileModified, Merge(Ctx(), new JsonObject
            {
                ["path"] = "src/B.cs",
                ["edits"] = new JsonArray(new JsonObject
                {
                    ["old_string"] = "a",
                    ["new_string"] = "b",
                }),
            })),
        ]);

        var result = ProvenanceQuery.Trace(new ProvenanceSubject.CodeRange("src/B.cs", 1, 5), [timeline]);
        Assert.Equal(ProvenanceMatchKind.DegradedToFile, Assert.Single(result.Hits).MatchKind);
    }

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
}
