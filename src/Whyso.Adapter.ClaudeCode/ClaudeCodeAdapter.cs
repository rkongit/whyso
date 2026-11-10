using System.Text.Json.Nodes;
using Whyso.Events;

namespace Whyso.Adapter.ClaudeCode;

/// <summary>
/// Maps Claude Code hook payloads (stdin JSON) onto normalized <see cref="AgentEvent"/>s.
/// Reference: https://code.claude.com/docs/en/hooks. One hook can yield several events, for
/// example a Bash PostToolUse becomes tool.completed plus command.completed, and an Edit
/// becomes tool.completed plus file.modified, so activity reconstruction and commit
/// attribution work the same way they do for Cursor.
/// </summary>
public static class ClaudeCodeAdapter
{
    public const string AgentName = "claude-code";

    /// <summary>Hook events installed by <c>whyso integrate claude-code</c>.</summary>
    public static readonly string[] HookNames =
    [
        "SessionStart",
        "SessionEnd",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PostToolUseFailure",
        "Stop",
    ];

    /// <summary>Events whose matcher filters by tool name; "*" matches every tool.</summary>
    private static readonly HashSet<string> ToolMatcherEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure"];

    private static readonly HashSet<string> FileEditTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];

    public static IReadOnlyList<AgentEvent> Adapt(JsonNode raw)
    {
        if (raw is not JsonObject obj)
        {
            throw new HookException("expected a JSON object");
        }

        var hookName = obj["hook_event_name"]?.GetValue<string>()
            ?? throw new HookException("missing hook_event_name in Claude Code payload");
        var sessionId = Protocol.SessionIdFromExternal(obj["session_id"]?.GetValue<string>() ?? "unknown");

        var common = new JsonObject { ["claude_hook"] = hookName };
        HookSupport.CopyFields(obj, common, "cwd", "transcript_path", "permission_mode", "prompt_id");

        var events = new List<AgentEvent>();
        void Emit(EventType type, JsonObject fields)
        {
            var payload = (JsonObject)common.DeepClone();
            foreach (var (key, value) in fields)
            {
                payload[key] = value?.DeepClone();
            }

            HookSupport.TruncateStrings(payload);
            events.Add(AgentEvent.New(sessionId, AgentName, type, payload));
        }

        var toolName = obj["tool_name"]?.GetValue<string>();
        var toolInput = obj["tool_input"] as JsonObject;

        switch (hookName)
        {
            case "SessionStart":
                Emit(EventType.SessionStarted, Pick(obj, "source", "model", "agent_type"));
                break;

            case "SessionEnd":
                Emit(EventType.SessionEnded, Pick(obj, "reason"));
                break;

            case "UserPromptSubmit":
                Emit(EventType.PromptSubmitted, Pick(obj, "prompt"));
                break;

            case "PreToolUse":
                Emit(EventType.ToolStarted, Pick(obj, "tool_name", "tool_input", "tool_use_id"));
                if (toolName == "Bash" && toolInput?["command"] is not null)
                {
                    Emit(EventType.CommandStarted, new JsonObject
                    {
                        ["command"] = toolInput["command"]!.DeepClone(),
                        ["tool_use_id"] = obj["tool_use_id"]?.DeepClone(),
                    });
                }

                break;

            case "PostToolUse":
            {
                var completed = Pick(obj, "tool_name", "tool_input", "tool_use_id");
                completed["tool_output"] = obj["tool_response"]?.DeepClone();
                completed["duration"] = obj["duration_ms"]?.DeepClone();
                Emit(EventType.ToolCompleted, completed);

                if (toolName == "Bash" && toolInput?["command"] is not null)
                {
                    Emit(EventType.CommandCompleted, new JsonObject
                    {
                        ["command"] = toolInput["command"]!.DeepClone(),
                        ["output"] = obj["tool_response"]?.DeepClone(),
                        ["duration"] = obj["duration_ms"]?.DeepClone(),
                        ["tool_use_id"] = obj["tool_use_id"]?.DeepClone(),
                    });
                }

                var path = toolInput?["file_path"] ?? toolInput?["notebook_path"];
                if (toolName is not null && FileEditTools.Contains(toolName) && path is not null)
                {
                    Emit(EventType.FileModified, new JsonObject
                    {
                        ["path"] = path.DeepClone(),
                        ["tool_name"] = toolName,
                        ["tool_use_id"] = obj["tool_use_id"]?.DeepClone(),
                    });
                }

                break;
            }

            case "PostToolUseFailure":
            {
                var failed = Pick(obj, "tool_name", "tool_input", "tool_use_id", "is_interrupt");
                failed["error_message"] = obj["error"]?.DeepClone();
                failed["duration"] = obj["duration_ms"]?.DeepClone();
                Emit(EventType.Error, failed);
                break;
            }

            case "Stop":
            {
                var text = obj["last_assistant_message"];
                if (text is not null)
                {
                    Emit(EventType.AgentResponse, new JsonObject { ["text"] = text.DeepClone() });
                }

                break;
            }

            default:
                throw new HookException($"unsupported Claude Code hook: {hookName}");
        }

        return events;
    }

    /// <summary>
    /// Handles one hook invocation and returns what to print on stdout. Never throws: errors go
    /// to the hook log so the agent is never blocked. For SessionStart, the output is the
    /// optional context brief in Claude Code's <c>additionalContext</c> shape.
    /// </summary>
    public static string HandleHookStdin(
        string stdin,
        string eventsUrl,
        string? logPath = null,
        Func<string?>? sessionStartContext = null)
    {
        stdin = HookSupport.NormalizeStdin(stdin);
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(stdin);
        }
        catch (Exception ex)
        {
            HookSupport.AppendLog(logPath, $"claude-code: invalid stdin JSON: {ex.Message} (bytes={stdin.Length})");
            return "";
        }

        IReadOnlyList<AgentEvent> events;
        try
        {
            events = Adapt(raw!);
        }
        catch (Exception ex)
        {
            HookSupport.AppendLog(logPath, $"claude-code: adapt failed: {ex.Message}");
            return "";
        }

        foreach (var event_ in events)
        {
            try
            {
                var stored = HookSupport.PostEvent(event_, eventsUrl);
                HookSupport.AppendLog(
                    logPath,
                    $"claude-code: ok type={stored.EventType.AsString()} session={stored.SessionId} seq={stored.Sequence}");
            }
            catch (Exception ex)
            {
                HookSupport.AppendLog(logPath, $"claude-code: POST {eventsUrl} failed: {ex.Message}");
                break;
            }
        }

        if (raw?["hook_event_name"]?.GetValue<string>() == "SessionStart" && sessionStartContext is not null)
        {
            try
            {
                var context = sessionStartContext();
                if (!string.IsNullOrWhiteSpace(context))
                {
                    return SessionStartOutput(context);
                }
            }
            catch (Exception ex)
            {
                HookSupport.AppendLog(logPath, $"claude-code: context brief failed: {ex.Message}");
            }
        }

        return "";
    }

    /// <summary>Claude Code caps injected context at 10,000 characters.</summary>
    public const int MaxContextChars = 9_500;

    public static string SessionStartOutput(string context)
    {
        if (context.Length > MaxContextChars)
        {
            context = context[..MaxContextChars];
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "SessionStart",
                ["additionalContext"] = context,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// Command string for settings.json. Forward slashes keep a Windows path valid both in the
    /// Git Bash shell Claude Code uses for hooks and in cmd.
    /// </summary>
    public static string HookCommand(string whysoBin) => $"\"{whysoBin.Replace('\\', '/')}\" hook claude-code";

    /// <summary>
    /// Merges Whyso hooks into a Claude Code settings object (usually <c>.claude/settings.json</c>).
    /// Idempotent: earlier Whyso entries are replaced, other hooks and settings are kept.
    /// </summary>
    public static JsonObject MergeSettings(JsonNode? existing, string command)
    {
        var root = existing as JsonObject ?? new JsonObject();
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var name in HookNames)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = [];
                hooks[name] = groups;
            }

            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                for (var j = handlers.Count - 1; j >= 0; j--)
                {
                    var cmd = handlers[j]?["command"]?.GetValue<string>();
                    if (cmd is not null && HookSupport.IsOwnHookCommand(cmd, AgentName))
                    {
                        handlers.RemoveAt(j);
                    }
                }

                if (handlers.Count == 0)
                {
                    groups.RemoveAt(i);
                }
            }

            var group = new JsonObject();
            if (ToolMatcherEvents.Contains(name))
            {
                group["matcher"] = "*";
            }

            group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command });
            groups.Add(group);
        }

        return root;
    }

    private static JsonObject Pick(JsonObject src, params string[] keys)
    {
        var dst = new JsonObject();
        HookSupport.CopyFields(src, dst, keys);
        return dst;
    }
}
