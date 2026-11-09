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
    public const int MaxStringBytes = 64 * 1024;
    public const string DefaultEventsUrl = "http://127.0.0.1:9477/v1/events";

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
            var preview = stdin.Length <= 80 ? stdin : stdin[..80];
            AppendHookLog(logPath, $"invalid stdin JSON: {ex.Message} (bytes={stdin.Length}, preview={preview})");
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

    public static AgentEvent PostEvent(AgentEvent event_, string url)
    {
        // Match SQLite busy_timeout (5s) so a locked DB does not outlive the HTTP client.
        const int maxAttempts = 3;
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new StringContent(event_.ToJson(), Encoding.UTF8, "application/json");
                using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new AdapterException($"HTTP error: {(int)response.StatusCode} {body}");
                }

                return AgentEvent.FromJson(body);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                last = ex;
                Thread.Sleep(50 * attempt);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last is AdapterException ae
            ? ae
            : new AdapterException($"HTTP error: {last?.Message ?? "unknown"}", last!);
    }

    public static string CursorHookCommand(string worktreeBin) =>
        $"\"{worktreeBin}\" hook cursor";

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
                if (cmd is not null && IsWorktreeHookCommand(cmd))
                {
                    arr.RemoveAt(i);
                }
            }

            arr.Add(new JsonObject { ["command"] = command });
        }

        return root;
    }

    private static bool IsWorktreeHookCommand(string command)
    {
        var lower = command.ToLowerInvariant();
        // Matches the current "whyso" binary and the pre-rename "worktree" one, so integrate replaces both.
        return (lower.Contains("whyso") || lower.Contains("worktree")) && lower.Contains("hook") && lower.Contains("cursor");
    }

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

    private static void CopyFields(JsonObject src, JsonObject dst, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (src[key] is not null)
            {
                dst[key] = src[key]!.DeepClone();
            }
        }
    }

    private static void TruncateStrings(JsonNode? value, int maxBytes)
    {
        switch (value)
        {
            case JsonValue jv when jv.TryGetValue<string>(out var s):
                // Leaf string handled by parent object/array mutation.
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > maxBytes)
                    {
                        arr[i] = Truncate(s, maxBytes);
                    }
                    else
                    {
                        TruncateStrings(arr[i], maxBytes);
                    }
                }

                break;
            case JsonObject map:
            {
                var truncated = false;
                foreach (var key in map.Select(kv => kv.Key).ToList())
                {
                    if (key == "_truncated")
                    {
                        continue;
                    }

                    if (map[key] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > maxBytes)
                    {
                        map[key] = Truncate(s, maxBytes);
                        truncated = true;
                    }
                    else
                    {
                        TruncateStrings(map[key], maxBytes);
                    }
                }

                if (truncated)
                {
                    map["_truncated"] = true;
                }

                break;
            }
        }
    }

    private static string Truncate(string s, int maxBytes)
    {
        if (s.Length <= maxBytes)
        {
            return s;
        }

        var cut = s[..maxBytes];
        while (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return cut;
    }

    private static void AppendHookLog(string? logPath, string message)
    {
        if (logPath is null)
        {
            return;
        }

        try
        {
            var parent = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
