using System.Text.Json.Nodes;
using Whyso.Adapter.ClaudeCode;
using Whyso.Events;

namespace Whyso.Adapter.ClaudeCode.Tests;

public class ClaudeCodeAdapterTests
{
    private static IReadOnlyList<AgentEvent> Adapt(string json) => ClaudeCodeAdapter.Adapt(JsonNode.Parse(json)!);

    [Fact]
    public void Maps_user_prompt_submit()
    {
        var events = Adapt("""
            {"session_id":"abc-123","transcript_path":"/t.jsonl","cwd":"/repo",
             "hook_event_name":"UserPromptSubmit","prompt":"Add JWT auth"}
            """);
        var e = Assert.Single(events);
        Assert.Equal(EventType.PromptSubmitted, e.EventType);
        Assert.Equal("sess_abc-123", e.SessionId);
        Assert.Equal("claude-code", e.Agent);
        Assert.Equal("Add JWT auth", e.Payload["prompt"]!.GetValue<string>());
        Assert.Equal("UserPromptSubmit", e.Payload["claude_hook"]!.GetValue<string>());
        Assert.Equal("/repo", e.Payload["cwd"]!.GetValue<string>());
    }

    [Fact]
    public void Bash_post_tool_use_emits_tool_and_command_completed()
    {
        var events = Adapt("""
            {"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Bash",
             "tool_input":{"command":"git commit -m x"},"tool_response":{"stdout":"ok"},
             "tool_use_id":"tu_1","duration_ms":42}
            """);
        Assert.Equal([EventType.ToolCompleted, EventType.CommandCompleted], events.Select(e => e.EventType));
        Assert.Equal("git commit -m x", events[1].Payload["command"]!.GetValue<string>());
        Assert.Equal(42, events[0].Payload["duration"]!.GetValue<int>());
        Assert.Equal("ok", events[0].Payload["tool_output"]!["stdout"]!.GetValue<string>());
    }

    [Fact]
    public void Edit_post_tool_use_emits_file_modified()
    {
        var events = Adapt("""
            {"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Edit",
             "tool_input":{"file_path":"/repo/src/a.cs","old_string":"x","new_string":"y"},"tool_use_id":"tu_2"}
            """);
        Assert.Equal([EventType.ToolCompleted, EventType.FileModified], events.Select(e => e.EventType));
        Assert.Equal("/repo/src/a.cs", events[1].Payload["path"]!.GetValue<string>());
    }

    [Fact]
    public void Read_tool_does_not_emit_file_modified()
    {
        var events = Adapt("""
            {"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Read",
             "tool_input":{"file_path":"/repo/src/a.cs"},"tool_use_id":"tu_3"}
            """);
        Assert.Equal(EventType.ToolCompleted, Assert.Single(events).EventType);
    }

    [Fact]
    public void Maps_failure_session_lifecycle_and_stop()
    {
        Assert.Equal(EventType.SessionStarted, Assert.Single(Adapt("""{"session_id":"s","hook_event_name":"SessionStart","source":"startup"}""")).EventType);
        Assert.Equal(EventType.SessionEnded, Assert.Single(Adapt("""{"session_id":"s","hook_event_name":"SessionEnd","reason":"other"}""")).EventType);

        var failure = Assert.Single(Adapt("""{"session_id":"s","hook_event_name":"PostToolUseFailure","tool_name":"Bash","error":"boom"}"""));
        Assert.Equal(EventType.Error, failure.EventType);
        Assert.Equal("boom", failure.Payload["error_message"]!.GetValue<string>());

        var stop = Assert.Single(Adapt("""{"session_id":"s","hook_event_name":"Stop","last_assistant_message":"Done. I went with SQLite."}"""));
        Assert.Equal(EventType.AgentResponse, stop.EventType);
        Assert.Equal("Done. I went with SQLite.", stop.Payload["text"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_hook_throws_and_handler_fails_open()
    {
        Assert.Throws<HookException>(() => Adapt("""{"session_id":"s","hook_event_name":"Notification"}"""));
        Assert.Equal("", ClaudeCodeAdapter.HandleHookStdin("not json", "http://127.0.0.1:1/v1/events"));
    }

    [Fact]
    public void Session_start_returns_context_even_when_daemon_is_down()
    {
        var output = ClaudeCodeAdapter.HandleHookStdin(
            """{"session_id":"s","hook_event_name":"SessionStart","source":"startup"}""",
            "http://127.0.0.1:1/v1/events",
            sessionStartContext: () => "past decisions");
        var json = JsonNode.Parse(output)!;
        Assert.Equal("SessionStart", json["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());
        Assert.Equal("past decisions", json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_settings_is_idempotent_and_keeps_other_hooks()
    {
        var existing = JsonNode.Parse("""
            {"model":"opus","hooks":{"PreToolUse":[
              {"matcher":"Bash","hooks":[{"type":"command","command":"./lint.sh"}]},
              {"matcher":"*","hooks":[{"type":"command","command":"\"C:/old/worktree.exe\" hook claude-code"}]}
            ]}}
            """);
        var command = ClaudeCodeAdapter.HookCommand(@"C:\bin\whyso.exe");
        Assert.Equal("\"C:/bin/whyso.exe\" hook claude-code", command);

        var once = ClaudeCodeAdapter.MergeSettings(existing, command);
        var twice = ClaudeCodeAdapter.MergeSettings(JsonNode.Parse(once.ToJsonString()), command);

        Assert.Equal(once.ToJsonString(), twice.ToJsonString());
        Assert.Equal("opus", twice["model"]!.GetValue<string>());
        var pre = twice["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("./lint.sh", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(command, pre[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("*", pre[1]!["matcher"]!.GetValue<string>());
        // Events without matcher support get no matcher.
        Assert.Null(twice["hooks"]!["UserPromptSubmit"]![0]!["matcher"]);
        foreach (var name in ClaudeCodeAdapter.HookNames)
        {
            Assert.NotNull(twice["hooks"]![name]);
        }
    }
}
