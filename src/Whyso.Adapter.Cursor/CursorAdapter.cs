using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whyso.Events;

namespace Whyso.Adapter.Cursor;

public sealed class AdapterException : Exception
{
    public AdapterException(string message) : base(message) { }
    public AdapterException(string message, Exception inner) : base(message, inner) { }
}

public static class CursorAdapter
{
    public const int MaxStringBytes = HookSupport.MaxStringBytes;
    public const string DefaultEventsUrl = HookSupport.DefaultEventsUrl;

    public static readonly string[] CursorHookNames =
    [
        "sessionStart",
        "sessionEnd",
        "beforeSubmitPrompt",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure",
        "beforeReadFile",
        "afterFileEdit",
        "beforeShellExecution",
        "afterShellExecution",
        "afterAgentResponse",
        "stop",
    ];

    public static AgentEvent AdaptCursorHook(JsonNode raw)
    {
        if (raw is not JsonObject obj)
        {
            throw new AdapterException("expected a JSON object");
        }

        var hookName = obj["hook_event_name"]?.GetValue<string>()
            ?? throw new AdapterException("missing hook_event_name in Cursor payload");

        var eventType = MapHookToEventType(hookName)
            ?? throw new AdapterException($"unsupported Cursor hook: {hookName}");

        var conversationId = obj["conversation_id"]?.GetValue<string>()
            ?? obj["session_id"]?.GetValue<string>()
            ?? "unknown";

        var payload = BuildPayload(hookName, obj);
        TruncateStrings(payload, MaxStringBytes);

        return new AgentEvent
        {
            Version = Protocol.Version,
            EventId = Protocol.NewEventId(),
            SessionId = Protocol.SessionIdFromExternal(conversationId),
            Sequence = null,
            Timestamp = DateTimeOffset.UtcNow,
            Agent = "cursor",
            EventType = eventType,
            Payload = payload,
        };
    }

    public static string NormalizeHookStdin(string stdin)
    {
        var trimmed = stdin.TrimStart('\uFEFF', '\uFFFE', '\0');
        return trimmed.TrimStart();
    }

    public static JsonObject HandleHookStdin(string stdin, string eventsUrl, string? logPath = null)
    {
        stdin = NormalizeHookStdin(stdin);
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(stdin);
        }
        catch (Exception ex)
        {
            // No payload preview: stdin may contain prompts or secrets, and the log is plaintext.
            AppendHookLog(logPath, $"invalid stdin JSON: {ex.Message} (bytes={stdin.Length})");
            return new JsonObject();
        }

        AgentEvent event_;
        try
        {
            event_ = AdaptCursorHook(raw!);
        }
        catch (Exception ex)
        {
            AppendHookLog(logPath, $"adapt failed: {ex.Message}");
            return new JsonObject();
        }

        try
        {
            var stored = PostEvent(event_, eventsUrl);
            AppendHookLog(
                logPath,
                $"ok type={stored.EventType.AsString()} session={stored.SessionId} seq={stored.Sequence}");
        }
        catch (Exception ex)
        {
            AppendHookLog(logPath, $"POST {eventsUrl} failed: {ex.Message}");
        }

        return new JsonObject();
    }

    public static AgentEvent PostEvent(AgentEvent event_, string url) => HookSupport.PostEvent(event_, url);

    public static string CursorHookCommand(string whysoBin) =>
        $"\"{whysoBin}\" hook cursor";

    public static JsonObject MergeCursorHooks(JsonNode? existing, string command)
    {
        var root = existing as JsonObject
            ?? new JsonObject { ["version"] = 1, ["hooks"] = new JsonObject() };

        if (root["version"] is null)
        {
            root["version"] = 1;
        }

        if (root["hooks"] is not JsonObject hooksObj)
        {
            hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
        }

        foreach (var name in CursorHookNames)
        {
            JsonArray arr;
            if (hooksObj[name] is JsonArray existingArr)
            {
                arr = existingArr;
            }
            else
            {
                arr = [];
                hooksObj[name] = arr;
            }

            for (var i = arr.Count - 1; i >= 0; i--)
            {
                var cmd = arr[i]?["command"]?.GetValue<string>();
                if (cmd is not null && IsWhysoHookCommand(cmd))
                {
                    arr.RemoveAt(i);
                }
            }

            arr.Add(new JsonObject { ["command"] = command });
        }

        return root;
    }

    private static bool IsWhysoHookCommand(string command) => HookSupport.IsOwnHookCommand(command, "cursor");

    private static EventType? MapHookToEventType(string hook) => hook switch
    {
        "sessionStart" => EventType.SessionStarted,
        "sessionEnd" => EventType.SessionEnded,
        "beforeSubmitPrompt" => EventType.PromptSubmitted,
        "preToolUse" => EventType.ToolStarted,
        "postToolUse" => EventType.ToolCompleted,
        "postToolUseFailure" => EventType.Error,
        "beforeReadFile" => EventType.FileRead,
        "afterFileEdit" => EventType.FileModified,
        "beforeShellExecution" => EventType.CommandStarted,
        "afterShellExecution" => EventType.CommandCompleted,
        "afterAgentResponse" => EventType.AgentResponse,
        "stop" => EventType.AgentResponse,
        _ => null,
    };

    private static JsonObject BuildPayload(string hookName, JsonObject obj)
    {
        var payload = new JsonObject { ["cursor_hook"] = hookName };
        foreach (var key in new[]
                 {
                     "generation_id", "model", "model_id", "cursor_version", "workspace_roots",
                     "transcript_path", "user_email",
                 })
        {
            if (obj[key] is not null)
            {
                payload[key] = obj[key]!.DeepClone();
            }
        }

        switch (hookName)
        {
            case "beforeSubmitPrompt":
                CopyFields(obj, payload, "prompt", "attachments");
                break;
            case "preToolUse":
            case "postToolUse":
            case "postToolUseFailure":
                CopyFields(obj, payload,
                    "tool_name", "tool_input", "tool_use_id", "tool_output", "cwd", "duration",
                    "error_message", "failure_type", "is_interrupt", "agent_message");
                break;
            case "beforeReadFile":
                if (obj["file_path"] is not null)
                {
                    payload["path"] = obj["file_path"]!.DeepClone();
                }

                CopyFields(obj, payload, "attachments");
                if (obj["content"] is not null)
                {
                    payload["content_omitted"] = true;
                }

                break;
            case "afterFileEdit":
                if (obj["file_path"] is not null)
                {
                    payload["path"] = obj["file_path"]!.DeepClone();
                }

                CopyFields(obj, payload, "edits");
                break;
            case "beforeShellExecution":
            case "afterShellExecution":
                CopyFields(obj, payload, "command", "cwd", "output", "duration", "sandbox");
                break;
            case "afterAgentResponse":
                CopyFields(obj, payload, "text");
                break;
            case "stop":
                CopyFields(obj, payload, "status", "loop_count");
                break;
            case "sessionStart":
            case "sessionEnd":
                CopyFields(obj, payload, "session_id", "is_background_agent");
                break;
        }

        return payload;
    }

    private static void CopyFields(JsonObject src, JsonObject dst, params string[] keys) =>
        HookSupport.CopyFields(src, dst, keys);

    private static void TruncateStrings(JsonNode? value, int maxBytes) => HookSupport.TruncateStrings(value, maxBytes);

    private static void AppendHookLog(string? logPath, string message) => HookSupport.AppendLog(logPath, message);
}
