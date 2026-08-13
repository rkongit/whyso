using System.Text.Json.Nodes;

namespace Worktree.Events;

public static class Normalize
{
    public static SessionNorm NormalizeSessionRecord(
        string sessionId,
        string agent,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt,
        string? commitIdBefore,
        string? commitIdAfter) =>
        new()
        {
            SessionId = sessionId,
            Agent = agent,
            StartedAt = startedAt,
            EndedAt = endedAt,
            CommitIdBefore = commitIdBefore,
            CommitIdAfter = commitIdAfter,
            Phase = null,
            IsBackgroundAgent = null,
            Meta = null,
        };

    public static NormalizedEvent? NormalizeEvent(AgentEvent event_)
    {
        var meta = BuildEventMeta(event_);
        var payload = event_.Payload;

        switch (event_.EventType)
        {
            case EventType.SessionStarted:
            case EventType.SessionEnded:
                return new NormalizedEvent.Session(new SessionNorm
                {
                    SessionId = event_.SessionId,
                    Agent = event_.Agent,
                    StartedAt = null,
                    EndedAt = null,
                    CommitIdBefore = null,
                    CommitIdAfter = null,
                    Phase = event_.EventType == EventType.SessionStarted
                        ? SessionPhase.Started
                        : SessionPhase.Ended,
                    IsBackgroundAgent = payload?["is_background_agent"]?.GetValue<bool>(),
                    Meta = meta,
                });

            case EventType.PromptSubmitted:
                return new NormalizedEvent.Prompt(new PromptNorm
                {
                    Meta = meta,
                    Text = StringField(payload, "prompt"),
                    Attachments = payload?["attachments"]?.DeepClone(),
                });

            case EventType.ToolStarted:
            case EventType.ToolCompleted:
                return new NormalizedEvent.ToolCall(new ToolCallNorm
                {
                    Meta = meta,
                    Phase = event_.EventType == EventType.ToolStarted
                        ? ToolPhase.Started
                        : ToolPhase.Completed,
                    Name = StringField(payload, "tool_name"),
                    ToolUseId = StringField(payload, "tool_use_id"),
                    Input = payload?["tool_input"]?.DeepClone(),
                    Output = payload?["tool_output"]?.DeepClone(),
                    DurationMs = NumberAsF64(payload?["duration"]),
                    ErrorMessage = StringField(payload, "error_message"),
                });

            case EventType.Error:
                if (payload?["tool_name"] is null)
                {
                    return null;
                }

                return new NormalizedEvent.ToolCall(new ToolCallNorm
                {
                    Meta = meta,
                    Phase = ToolPhase.Failed,
                    Name = StringField(payload, "tool_name"),
                    ToolUseId = StringField(payload, "tool_use_id"),
                    Input = payload?["tool_input"]?.DeepClone(),
                    Output = payload?["tool_output"]?.DeepClone(),
                    DurationMs = NumberAsF64(payload?["duration"]),
                    ErrorMessage = StringField(payload, "error_message")
                        ?? StringField(payload, "agent_message"),
                });

            case EventType.FileRead:
            case EventType.FileCreated:
            case EventType.FileModified:
            case EventType.FileDeleted:
            {
                var action = event_.EventType switch
                {
                    EventType.FileRead => FileAction.Read,
                    EventType.FileCreated => FileAction.Created,
                    EventType.FileModified => FileAction.Modified,
                    _ => FileAction.Deleted,
                };
                return new NormalizedEvent.FileChange(new FileChangeNorm
                {
                    Meta = meta,
                    Action = action,
                    Path = StringField(payload, "path"),
                    Edits = payload?["edits"]?.DeepClone(),
                    ContentOmitted = payload?["content_omitted"]?.GetValue<bool>(),
                });
            }

            case EventType.CommandStarted:
            case EventType.CommandCompleted:
            {
                var phase = event_.EventType == EventType.CommandStarted
                    ? CommandPhase.Started
                    : CommandPhase.Completed;
                var command = StringField(payload, "command");
                if (TryParseGitCommand(command, out var subcommand, out var args))
                {
                    return new NormalizedEvent.GitOperation(new GitOperationNorm
                    {
                        Meta = meta,
                        Phase = phase,
                        Command = command,
                        Subcommand = subcommand,
                        Args = args,
                        Cwd = StringField(payload, "cwd"),
                        Output = payload?["output"]?.DeepClone(),
                        Duration = payload?["duration"]?.DeepClone(),
                    });
                }

                return new NormalizedEvent.CommandExecution(new CommandExecutionNorm
                {
                    Meta = meta,
                    Phase = phase,
                    Command = command,
                    Cwd = StringField(payload, "cwd"),
                    Output = payload?["output"]?.DeepClone(),
                    Duration = payload?["duration"]?.DeepClone(),
                    Sandbox = payload?["sandbox"]?.DeepClone(),
                });
            }

            case EventType.TestStarted:
            case EventType.TestCompleted:
            {
                var phase = event_.EventType == EventType.TestStarted
                    ? TestPhase.Started
                    : TestPhase.Completed;
                var name = StringField(payload, "name");
                var suite = StringField(payload, "suite");
                var status = StringField(payload, "status");
                var passed = IntField(payload, "passed");
                var failed = IntField(payload, "failed");
                if (name is null && suite is null && status is null && passed is null && failed is null)
                {
                    return null;
                }

                return new NormalizedEvent.TestResult(new TestResultNorm
                {
                    Meta = meta,
                    Phase = phase,
                    Name = name,
                    Suite = suite,
                    Status = status,
                    Passed = passed,
                    Failed = failed,
                });
            }

            case EventType.AgentResponse:
                return new NormalizedEvent.AgentResponse(new AgentResponseNorm
                {
                    Meta = meta,
                    Text = StringField(payload, "text"),
                    Status = StringField(payload, "status"),
                    LoopCount = IntField(payload, "loop_count"),
                });

            default:
                return null;
        }
    }

    private static EventMeta BuildEventMeta(AgentEvent event_) => new()
    {
        EventId = event_.EventId,
        SessionId = event_.SessionId,
        Sequence = event_.Sequence,
        Timestamp = event_.Timestamp,
        Agent = event_.Agent,
        EventType = event_.EventType,
        Context = ExtractAgentContext(event_.Payload),
    };

    private static AgentContext ExtractAgentContext(JsonNode? payload) => new()
    {
        Model = StringField(payload, "model"),
        ModelId = StringField(payload, "model_id"),
        CursorVersion = StringField(payload, "cursor_version"),
        GenerationId = StringField(payload, "generation_id"),
        UserEmail = StringField(payload, "user_email"),
    };

    private static string? StringField(JsonNode? payload, string key)
    {
        var node = payload?[key];
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }

    private static long? IntField(JsonNode? payload, string key)
    {
        var node = payload?[key];
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return (long)d;
        }

        return null;
    }

    private static double? NumberAsF64(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        return null;
    }

    private static bool TryParseGitCommand(string? command, out string? subcommand, out List<string> args)
    {
        subcommand = null;
        args = [];
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var tokens = command.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var baseName = Path.GetFileName(tokens[0]).ToLowerInvariant();
        if (baseName is not ("git" or "git.exe"))
        {
            return false;
        }

        var rest = tokens.Skip(1).ToList();
        subcommand = rest.Count > 0 ? rest[0] : null;
        args = rest.Count > 1 ? rest.Skip(1).ToList() : [];
        return true;
    }
}
