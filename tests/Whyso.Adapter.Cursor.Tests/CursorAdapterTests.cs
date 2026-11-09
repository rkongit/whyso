using System.Text.Json.Nodes;
using Whyso.Adapter.Cursor;
using Whyso.Events;

namespace Whyso.Adapter.Cursor.Tests;

public class CursorAdapterTests
{
    [Fact]
    public void Maps_before_submit_prompt()
    {
        var raw = JsonNode.Parse("""
            {
              "hook_event_name": "beforeSubmitPrompt",
              "conversation_id": "abc-123",
              "generation_id": "gen-1",
              "prompt": "Implement JWT auth",
              "workspace_roots": ["/tmp/proj"]
            }
            """)!;
        var event_ = CursorAdapter.AdaptCursorHook(raw);
        Assert.Equal(EventType.PromptSubmitted, event_.EventType);
        Assert.Equal("sess_abc-123", event_.SessionId);
        Assert.Equal("cursor", event_.Agent);
        Assert.Equal("Implement JWT auth", event_.Payload["prompt"]!.GetValue<string>());
        Assert.Equal("beforeSubmitPrompt", event_.Payload["cursor_hook"]!.GetValue<string>());
    }

    [Fact]
    public void Maps_after_file_edit()
    {
        var raw = JsonNode.Parse("""
            {
              "hook_event_name": "afterFileEdit",
              "conversation_id": "c1",
              "file_path": "src/auth.rs",
              "edits": [{ "old_string": "a", "new_string": "b" }]
            }
            """)!;
        var event_ = CursorAdapter.AdaptCursorHook(raw);
        Assert.Equal(EventType.FileModified, event_.EventType);
        Assert.Equal("src/auth.rs", event_.Payload["path"]!.GetValue<string>());
    }

    [Fact]
    public void Omits_file_content_on_read()
    {
        var raw = JsonNode.Parse("""
            {
              "hook_event_name": "beforeReadFile",
              "conversation_id": "c1",
              "file_path": "secret.env",
              "content": "SECRET=1"
            }
            """)!;
        var event_ = CursorAdapter.AdaptCursorHook(raw);
        Assert.Equal(EventType.FileRead, event_.EventType);
        Assert.Null(event_.Payload["content"]);
        Assert.True(event_.Payload["content_omitted"]!.GetValue<bool>());
    }

    [Fact]
    public void Truncates_large_strings()
    {
        var big = new string('x', CursorAdapter.MaxStringBytes + 100);
        var raw = new JsonObject
        {
            ["hook_event_name"] = "afterShellExecution",
            ["conversation_id"] = "c1",
            ["command"] = "echo hi",
            ["output"] = big,
        };
        var event_ = CursorAdapter.AdaptCursorHook(raw);
        Assert.True(event_.Payload["output"]!.GetValue<string>().Length <= CursorAdapter.MaxStringBytes);
        Assert.True(event_.Payload["_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Merge_hooks_is_idempotent()
    {
        var cmd = CursorAdapter.CursorHookCommand("whyso");
        var first = CursorAdapter.MergeCursorHooks(null, cmd);
        var second = CursorAdapter.MergeCursorHooks(first.DeepClone(), cmd);
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.Single(first["hooks"]!["afterFileEdit"]!.AsArray());
    }

    [Fact]
    public void Merge_preserves_existing_hooks()
    {
        var existing = JsonNode.Parse("""
            {
              "version": 1,
              "hooks": {
                "afterFileEdit": [{ "command": "./hooks/format.sh" }]
              }
            }
            """)!;
        var merged = CursorAdapter.MergeCursorHooks(existing, "whyso hook cursor");
        Assert.Equal(2, merged["hooks"]!["afterFileEdit"]!.AsArray().Count);
    }

    [Fact]
    public void Merge_replaces_old_worktree_command()
    {
        var existing = JsonNode.Parse("""
            {
              "version": 1,
              "hooks": {
                "afterFileEdit": [{ "command": "whyso hook cursor" }]
              }
            }
            """)!;
        var merged = CursorAdapter.MergeCursorHooks(
            existing,
            "\"C:\\bin\\whyso.exe\" hook cursor");
        var cmds = merged["hooks"]!["afterFileEdit"]!.AsArray();
        Assert.Single(cmds);
        Assert.Contains(@"C:\bin\whyso.exe", cmds[0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Hook_command_does_not_use_cmd()
    {
        var cmd = CursorAdapter.CursorHookCommand(@"C:\bin\whyso.exe");
        Assert.DoesNotContain("cmd /c", cmd.ToLowerInvariant());
        Assert.Contains("hook cursor", cmd);
    }

    [Fact]
    public void Strips_utf8_bom_from_cursor_stdin()
    {
        var raw = "\uFEFF{\"hook_event_name\":\"beforeSubmitPrompt\",\"conversation_id\":\"abc\",\"prompt\":\"hi\"}";
        var event_ = CursorAdapter.AdaptCursorHook(
            JsonNode.Parse(CursorAdapter.NormalizeHookStdin(raw))!);
        Assert.Equal(EventType.PromptSubmitted, event_.EventType);
        Assert.Equal("sess_abc", event_.SessionId);
    }
}
