using System.Text.Json.Serialization;

namespace Worktree.Events;

/// <summary>Strongly typed event categories in the v1 protocol.</summary>
[JsonConverter(typeof(EventTypeJsonConverter))]
public enum EventType
{
    SessionStarted,
    SessionEnded,
    PromptSubmitted,
    ToolStarted,
    ToolCompleted,
    FileRead,
    FileCreated,
    FileModified,
    FileDeleted,
    CommandStarted,
    CommandCompleted,
    TestStarted,
    TestCompleted,
    AgentResponse,
    Error,
}

public static class EventTypeExtensions
{
    public static string AsString(this EventType eventType) => eventType switch
    {
        EventType.SessionStarted => "session.started",
        EventType.SessionEnded => "session.ended",
        EventType.PromptSubmitted => "prompt.submitted",
        EventType.ToolStarted => "tool.started",
        EventType.ToolCompleted => "tool.completed",
        EventType.FileRead => "file.read",
        EventType.FileCreated => "file.created",
        EventType.FileModified => "file.modified",
        EventType.FileDeleted => "file.deleted",
        EventType.CommandStarted => "command.started",
        EventType.CommandCompleted => "command.completed",
        EventType.TestStarted => "test.started",
        EventType.TestCompleted => "test.completed",
        EventType.AgentResponse => "agent.response",
        EventType.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    public static EventType? Parse(string value) => value switch
    {
        "session.started" => EventType.SessionStarted,
        "session.ended" => EventType.SessionEnded,
        "prompt.submitted" => EventType.PromptSubmitted,
        "tool.started" => EventType.ToolStarted,
        "tool.completed" => EventType.ToolCompleted,
        "file.read" => EventType.FileRead,
        "file.created" => EventType.FileCreated,
        "file.modified" => EventType.FileModified,
        "file.deleted" => EventType.FileDeleted,
        "command.started" => EventType.CommandStarted,
        "command.completed" => EventType.CommandCompleted,
        "test.started" => EventType.TestStarted,
        "test.completed" => EventType.TestCompleted,
        "agent.response" => EventType.AgentResponse,
        "error" => EventType.Error,
        _ => null,
    };

    public static string DisplayLabel(this EventType eventType) => eventType switch
    {
        EventType.SessionStarted => "SESSION STARTED",
        EventType.SessionEnded => "SESSION ENDED",
        EventType.PromptSubmitted => "PROMPT SUBMITTED",
        EventType.ToolStarted => "TOOL STARTED",
        EventType.ToolCompleted => "TOOL COMPLETED",
        EventType.FileRead => "FILE READ",
        EventType.FileCreated => "FILE CREATED",
        EventType.FileModified => "FILE MODIFIED",
        EventType.FileDeleted => "FILE DELETED",
        EventType.CommandStarted => "COMMAND STARTED",
        EventType.CommandCompleted => "COMMAND COMPLETED",
        EventType.TestStarted => "TEST STARTED",
        EventType.TestCompleted => "TEST COMPLETED",
        EventType.AgentResponse => "AGENT RESPONSE",
        EventType.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    public static IReadOnlyList<EventType> All { get; } =
    [
        EventType.SessionStarted,
        EventType.SessionEnded,
        EventType.PromptSubmitted,
        EventType.ToolStarted,
        EventType.ToolCompleted,
        EventType.FileRead,
        EventType.FileCreated,
        EventType.FileModified,
        EventType.FileDeleted,
        EventType.CommandStarted,
        EventType.CommandCompleted,
        EventType.TestStarted,
        EventType.TestCompleted,
        EventType.AgentResponse,
        EventType.Error,
    ];
}
