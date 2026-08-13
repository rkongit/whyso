using System.Text.Json.Nodes;
using Worktree.Events;

namespace Worktree.Events.Tests;

public class NormalizeTests
{
    private static AgentEvent Event(EventType eventType, JsonNode payload) => new()
    {
        Version = Protocol.Version,
        EventId = Protocol.NewEventId(),
        SessionId = "sess_test",
        Sequence = 1,
        Timestamp = DateTimeOffset.Parse("2026-08-10T15:30:00Z"),
        Agent = "cursor",
        EventType = eventType,
        Payload = payload,
    };

    [Fact]
    public void Normalizes_prompt_sample_payload_with_agent_context()
    {
        var payload = JsonNode.Parse("""
            {
              "attachments": [],
              "cursor_hook": "beforeSubmitPrompt",
              "cursor_version": "3.15.6",
              "generation_id": "2a977bf3-9101-4a17-a21f-e92d6dc287a8",
              "model": "cursor-grok-4.5-high-fast",
              "model_id": "grok-4.5",
              "prompt": "write one more minimal api",
              "user_email": "adarshrk111@gmail.com"
            }
            """)!;
        var normalized = Normalize.NormalizeEvent(Event(EventType.PromptSubmitted, payload));
        var prompt = Assert.IsType<NormalizedEvent.Prompt>(normalized);
        Assert.Equal("write one more minimal api", prompt.Value.Text);
        Assert.Equal("cursor-grok-4.5-high-fast", prompt.Value.Meta.Context.Model);
        Assert.Equal("grok-4.5", prompt.Value.Meta.Context.ModelId);
        Assert.Equal("3.15.6", prompt.Value.Meta.Context.CursorVersion);
        Assert.Equal("2a977bf3-9101-4a17-a21f-e92d6dc287a8", prompt.Value.Meta.Context.GenerationId);
        Assert.Equal("adarshrk111@gmail.com", prompt.Value.Meta.Context.UserEmail);
    }

    [Fact]
    public void Normalize_session_record_exposes_commit_ids()
    {
        var started = DateTimeOffset.Parse("2026-08-10T15:00:00Z");
        var ended = DateTimeOffset.Parse("2026-08-10T16:00:00Z");
        var session = Normalize.NormalizeSessionRecord(
            "sess_abc", "cursor", started, ended, "aaa111", "bbb222");
        Assert.Equal("aaa111", session.CommitIdBefore);
        Assert.Equal("bbb222", session.CommitIdAfter);
        Assert.Equal("sess_abc", session.SessionId);
    }

    [Fact]
    public void Git_command_becomes_git_operation_instead_of_command()
    {
        var payload = JsonNode.Parse("""
            {"command":"git status --short","cwd":"/repo","model":"cursor-grok-4.5-high-fast"}
            """)!;
        var normalized = Normalize.NormalizeEvent(Event(EventType.CommandCompleted, payload));
        var git = Assert.IsType<NormalizedEvent.GitOperation>(normalized);
        Assert.Equal("status", git.Value.Subcommand);
        Assert.Equal(["--short"], git.Value.Args);
        Assert.Equal(CommandPhase.Completed, git.Value.Phase);
        Assert.Equal("cursor-grok-4.5-high-fast", git.Value.Meta.Context.Model);
    }

    [Fact]
    public void Non_git_command_stays_command_execution()
    {
        var payload = JsonNode.Parse("""{"command":"cargo test"}""")!;
        var normalized = Normalize.NormalizeEvent(Event(EventType.CommandStarted, payload));
        var cmd = Assert.IsType<NormalizedEvent.CommandExecution>(normalized);
        Assert.Equal("cargo test", cmd.Value.Command);
        Assert.Equal(CommandPhase.Started, cmd.Value.Phase);
    }

    [Fact]
    public void Empty_test_payload_returns_null()
    {
        Assert.Null(Normalize.NormalizeEvent(Event(EventType.TestCompleted, new JsonObject())));
    }

    [Fact]
    public void Test_with_fields_normalizes()
    {
        var payload = JsonNode.Parse("""{"name":"auth","status":"passed","passed":3,"failed":0}""")!;
        var normalized = Normalize.NormalizeEvent(Event(EventType.TestCompleted, payload));
        var test = Assert.IsType<NormalizedEvent.TestResult>(normalized);
        Assert.Equal("auth", test.Value.Name);
        Assert.Equal("passed", test.Value.Status);
        Assert.Equal(3, test.Value.Passed);
        Assert.Equal(0, test.Value.Failed);
    }

    [Fact]
    public void File_modified_and_tool_and_response()
    {
        var file = Normalize.NormalizeEvent(Event(
            EventType.FileModified,
            JsonNode.Parse("""{"path":"src/main.rs","edits":[{"old":"a","new":"b"}]}""")!));
        var fileChange = Assert.IsType<NormalizedEvent.FileChange>(file);
        Assert.Equal(FileAction.Modified, fileChange.Value.Action);
        Assert.Equal("src/main.rs", fileChange.Value.Path);
        Assert.NotNull(fileChange.Value.Edits);

        var tool = Normalize.NormalizeEvent(Event(
            EventType.ToolStarted,
            JsonNode.Parse("""{"tool_name":"Shell","tool_use_id":"tu_1"}""")!));
        var toolCall = Assert.IsType<NormalizedEvent.ToolCall>(tool);
        Assert.Equal(ToolPhase.Started, toolCall.Value.Phase);
        Assert.Equal("Shell", toolCall.Value.Name);

        var response = Normalize.NormalizeEvent(Event(
            EventType.AgentResponse,
            JsonNode.Parse("""{"text":"done"}""")!));
        var agentResponse = Assert.IsType<NormalizedEvent.AgentResponse>(response);
        Assert.Equal("done", agentResponse.Value.Text);
    }

    [Fact]
    public void Plain_error_without_tool_is_null()
    {
        Assert.Null(Normalize.NormalizeEvent(Event(EventType.Error, JsonNode.Parse("""{"message":"x"}""")!)));
    }

    [Fact]
    public void Git_exe_path_is_detected()
    {
        var payload = JsonNode.Parse("""{"command":"C:/tools/git.exe commit -m msg"}""")!;
        var normalized = Normalize.NormalizeEvent(Event(EventType.CommandStarted, payload));
        var git = Assert.IsType<NormalizedEvent.GitOperation>(normalized);
        Assert.Equal("commit", git.Value.Subcommand);
        Assert.Equal(["-m", "msg"], git.Value.Args);
    }
}
