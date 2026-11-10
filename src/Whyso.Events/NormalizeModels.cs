using System.Text.Json.Nodes;

namespace Whyso.Events;

public sealed class AgentContext
{
    public string? Model { get; set; }
    public string? ModelId { get; set; }
    public string? CursorVersion { get; set; }
    public string? GenerationId { get; set; }
    public string? UserEmail { get; set; }
}

public sealed class EventMeta
{
    public required string EventId { get; set; }
    public required string SessionId { get; set; }
    public ulong? Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Agent { get; set; }
    public EventType EventType { get; set; }
    public AgentContext Context { get; set; } = new();
}

public enum SessionPhase { Started, Ended }
public enum ToolPhase { Started, Completed, Failed }
public enum FileAction { Read, Created, Modified, Deleted }
public enum CommandPhase { Started, Completed }
public enum TestPhase { Started, Completed }

public sealed class SessionNorm
{
    public required string SessionId { get; set; }
    public required string Agent { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? CommitIdBefore { get; set; }
    public string? CommitIdAfter { get; set; }
    public SessionPhase? Phase { get; set; }
    public bool? IsBackgroundAgent { get; set; }
    public EventMeta? Meta { get; set; }
}

public sealed class PromptNorm
{
    public required EventMeta Meta { get; set; }
    public string? Text { get; set; }
    public JsonNode? Attachments { get; set; }

    /// <summary>Synthetic prompt used for orphan activity before the first real prompt.</summary>
    public static PromptNorm Preamble(string sessionId, string agent, DateTimeOffset timestamp) => new()
    {
        Meta = new EventMeta
        {
            EventId = "evt_preamble",
            SessionId = sessionId,
            Sequence = 0,
            Timestamp = timestamp,
            Agent = agent,
            EventType = EventType.PromptSubmitted,
        },
        Text = "(session preamble — activity before first prompt)",
    };
}

public sealed class ToolCallNorm
{
    public required EventMeta Meta { get; set; }
    public ToolPhase Phase { get; set; }
    public string? Name { get; set; }
    public string? ToolUseId { get; set; }
    public JsonNode? Input { get; set; }
    public JsonNode? Output { get; set; }
    public double? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class FileChangeNorm
{
    public required EventMeta Meta { get; set; }
    public FileAction Action { get; set; }
    public string? Path { get; set; }
    public JsonNode? Edits { get; set; }
    public bool? ContentOmitted { get; set; }
}

public sealed class CommandExecutionNorm
{
    public required EventMeta Meta { get; set; }
    public CommandPhase Phase { get; set; }
    public string? Command { get; set; }
    public string? Cwd { get; set; }
    public JsonNode? Output { get; set; }
    public JsonNode? Duration { get; set; }
    public JsonNode? Sandbox { get; set; }
}

public sealed class TestResultNorm
{
    public required EventMeta Meta { get; set; }
    public TestPhase Phase { get; set; }
    public string? Name { get; set; }
    public string? Suite { get; set; }
    public string? Status { get; set; }
    public long? Passed { get; set; }
    public long? Failed { get; set; }
}

public sealed class AgentResponseNorm
{
    public required EventMeta Meta { get; set; }
    public string? Text { get; set; }
    public string? Status { get; set; }
    public long? LoopCount { get; set; }
}

public sealed class GitOperationNorm
{
    public required EventMeta Meta { get; set; }
    public CommandPhase Phase { get; set; }
    public string? Command { get; set; }
    public string? Subcommand { get; set; }
    public List<string> Args { get; set; } = [];
    public string? Cwd { get; set; }
    public JsonNode? Output { get; set; }
    public JsonNode? Duration { get; set; }
}

public abstract class NormalizedEvent
{
    public sealed class Session(SessionNorm value) : NormalizedEvent
    {
        public SessionNorm Value { get; } = value;
    }

    public sealed class Prompt(PromptNorm value) : NormalizedEvent
    {
        public PromptNorm Value { get; } = value;
    }

    public sealed class ToolCall(ToolCallNorm value) : NormalizedEvent
    {
        public ToolCallNorm Value { get; } = value;
    }

    public sealed class FileChange(FileChangeNorm value) : NormalizedEvent
    {
        public FileChangeNorm Value { get; } = value;
    }

    public sealed class CommandExecution(CommandExecutionNorm value) : NormalizedEvent
    {
        public CommandExecutionNorm Value { get; } = value;
    }

    public sealed class TestResult(TestResultNorm value) : NormalizedEvent
    {
        public TestResultNorm Value { get; } = value;
    }

    public sealed class AgentResponse(AgentResponseNorm value) : NormalizedEvent
    {
        public AgentResponseNorm Value { get; } = value;
    }

    public sealed class GitOperation(GitOperationNorm value) : NormalizedEvent
    {
        public GitOperationNorm Value { get; } = value;
    }

    public string SummaryLine() => this switch
    {
        Session s => s.Value.Phase switch
        {
            SessionPhase.Started => "SESSION STARTED",
            SessionPhase.Ended => "SESSION ENDED",
            _ => "SESSION",
        },
        Prompt p => FormatPrompt(p.Value),
        ToolCall t => t.Value.Phase switch
        {
            ToolPhase.Started => $"TOOL START: {t.Value.Name ?? "?"}",
            ToolPhase.Completed => $"TOOL DONE: {t.Value.Name ?? "?"}",
            ToolPhase.Failed => $"TOOL FAIL: {t.Value.Name ?? "?"}",
            _ => "TOOL",
        },
        FileChange f => $"{FileActionLabel(f.Value.Action)}: {f.Value.Path ?? "?"}",
        CommandExecution c => c.Value.Phase switch
        {
            CommandPhase.Started => $"CMD START: {Truncate(c.Value.Command ?? "?", 50)}",
            CommandPhase.Completed => $"CMD DONE: {Truncate(c.Value.Command ?? "?", 50)}",
            _ => "CMD",
        },
        TestResult t => FormatTest(t.Value),
        AgentResponse a => FormatResponse(a.Value),
        GitOperation g => g.Value.Phase switch
        {
            CommandPhase.Started => $"GIT START: {g.Value.Subcommand ?? "git"}",
            CommandPhase.Completed => $"GIT DONE: {g.Value.Subcommand ?? "git"}",
            _ => "GIT",
        },
        _ => "EVENT",
    };

    private static string FormatPrompt(PromptNorm p)
    {
        var text = p.Text ?? "";
        const int max = 200;
        var preview = Truncate(text, max);
        if (text.Length > max)
        {
            return $"PROMPT: {preview}…";
        }

        return string.IsNullOrEmpty(preview) ? "PROMPT" : $"PROMPT: {preview}";
    }

    private static string FormatTest(TestResultNorm t)
    {
        var name = t.Name ?? t.Suite ?? "?";
        return t.Phase switch
        {
            TestPhase.Started => $"TEST START: {name}",
            TestPhase.Completed when t.Status is not null => $"TEST DONE: {name} ({t.Status})",
            TestPhase.Completed => $"TEST DONE: {name}",
            _ => "TEST",
        };
    }

    private static string FormatResponse(AgentResponseNorm a)
    {
        if (a.Text is not null)
        {
            // Show the full reply for typical agent responses; only trim very long ones.
            const int max = 2000;
            if (a.Text.Length <= max)
            {
                return $"RESPONSE: {a.Text}";
            }

            return $"RESPONSE: {Truncate(a.Text, max)}…";
        }

        return a.Status is not null ? $"RESPONSE: {a.Status}" : "RESPONSE";
    }

    private static string FileActionLabel(FileAction action) => action switch
    {
        FileAction.Read => "FILE READ",
        FileAction.Created => "FILE CREATED",
        FileAction.Modified => "FILE MODIFIED",
        FileAction.Deleted => "FILE DELETED",
        _ => "FILE",
    };

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }
}
