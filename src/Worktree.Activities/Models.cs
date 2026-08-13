using System.Text.Json.Nodes;
using Worktree.Events;

namespace Worktree.Activities;

/// <summary>Commit attribution link (mirrors storage session_commits without depending on Storage).</summary>
public sealed class CommitLink
{
    public required string Sha { get; init; }
    public required string SessionId { get; init; }
    public string? PromptEventId { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}

public sealed class LineRange
{
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }

    public bool Overlaps(int start, int end) =>
        StartLine <= end && EndLine >= start;
}

public sealed class FileArtifact
{
    public required string Path { get; init; }
    public FileAction Action { get; init; }
    public IReadOnlyList<LineRange> Ranges { get; init; } = [];
}

public sealed class TurnArtifacts
{
    public List<string> CommitShas { get; } = [];
    public List<FileArtifact> Files { get; } = [];
}

public abstract class ActivityItem
{
    public sealed class Tool(
        string? name,
        string? toolUseId,
        ToolPhase phase,
        JsonNode? input,
        JsonNode? output,
        double? durationMs,
        string? errorMessage,
        EventMeta? startMeta,
        EventMeta? endMeta) : ActivityItem
    {
        public string? Name { get; } = name;
        public string? ToolUseId { get; } = toolUseId;
        public ToolPhase Phase { get; } = phase;
        public JsonNode? Input { get; } = input;
        public JsonNode? Output { get; } = output;
        public double? DurationMs { get; } = durationMs;
        public string? ErrorMessage { get; } = errorMessage;
        public EventMeta? StartMeta { get; } = startMeta;
        public EventMeta? EndMeta { get; } = endMeta;
    }

    public sealed class File(
        FileAction action,
        string? path,
        JsonNode? edits,
        bool? contentOmitted,
        EventMeta meta) : ActivityItem
    {
        public FileAction Action { get; } = action;
        public string? Path { get; } = path;
        public JsonNode? Edits { get; } = edits;
        public bool? ContentOmitted { get; } = contentOmitted;
        public EventMeta Meta { get; } = meta;
    }

    public sealed class Command(
        string? command,
        string? cwd,
        CommandPhase phase,
        JsonNode? output,
        JsonNode? duration,
        EventMeta? startMeta,
        EventMeta? endMeta) : ActivityItem
    {
        public string? CommandText { get; } = command;
        public string? Cwd { get; } = cwd;
        public CommandPhase Phase { get; } = phase;
        public JsonNode? Output { get; } = output;
        public JsonNode? Duration { get; } = duration;
        public EventMeta? StartMeta { get; } = startMeta;
        public EventMeta? EndMeta { get; } = endMeta;
    }

    public sealed class Git(
        string? command,
        string? subcommand,
        IReadOnlyList<string> args,
        string? cwd,
        CommandPhase phase,
        JsonNode? output,
        JsonNode? duration,
        EventMeta? startMeta,
        EventMeta? endMeta) : ActivityItem
    {
        public string? CommandText { get; } = command;
        public string? Subcommand { get; } = subcommand;
        public IReadOnlyList<string> Args { get; } = args;
        public string? Cwd { get; } = cwd;
        public CommandPhase Phase { get; } = phase;
        public JsonNode? Output { get; } = output;
        public JsonNode? Duration { get; } = duration;
        public EventMeta? StartMeta { get; } = startMeta;
        public EventMeta? EndMeta { get; } = endMeta;
    }

    public sealed class Test(
        string? name,
        string? suite,
        string? status,
        long? passed,
        long? failed,
        TestPhase phase,
        EventMeta? startMeta,
        EventMeta? endMeta) : ActivityItem
    {
        public string? Name { get; } = name;
        public string? Suite { get; } = suite;
        public string? Status { get; } = status;
        public long? Passed { get; } = passed;
        public long? Failed { get; } = failed;
        public TestPhase Phase { get; } = phase;
        public EventMeta? StartMeta { get; } = startMeta;
        public EventMeta? EndMeta { get; } = endMeta;
    }

    public string SummaryLine() => this switch
    {
        Tool t => t.Phase switch
        {
            ToolPhase.Started => $"TOOL: {t.Name ?? "?"} (started)",
            ToolPhase.Completed => $"TOOL: {t.Name ?? "?"}",
            ToolPhase.Failed => $"TOOL FAIL: {t.Name ?? "?"}",
            _ => $"TOOL: {t.Name ?? "?"}",
        },
        File f => $"{FileLabel(f.Action)}: {f.Path ?? "?"}",
        Command c => c.Phase switch
        {
            CommandPhase.Started => $"CMD: {Truncate(c.CommandText ?? "?", 60)} (started)",
            _ => $"CMD: {Truncate(c.CommandText ?? "?", 60)}",
        },
        Git g => g.Phase switch
        {
            CommandPhase.Started => $"GIT: {g.Subcommand ?? "git"} (started)",
            _ => $"GIT: {g.Subcommand ?? "git"}",
        },
        Test t => t.Phase switch
        {
            TestPhase.Started => $"TEST: {t.Name ?? t.Suite ?? "?"} (started)",
            _ => t.Status is not null
                ? $"TEST: {t.Name ?? t.Suite ?? "?"} ({t.Status})"
                : $"TEST: {t.Name ?? t.Suite ?? "?"}",
        },
        _ => "ACTIVITY",
    };

    private static string FileLabel(FileAction action) => action switch
    {
        FileAction.Read => "FILE READ",
        FileAction.Created => "FILE CREATED",
        FileAction.Modified => "FILE MODIFIED",
        FileAction.Deleted => "FILE DELETED",
        _ => "FILE",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public sealed class ActivityTurn
{
    public required string SessionId { get; init; }
    public required PromptNorm Prompt { get; init; }
    public string? GenerationId { get; init; }
    public List<ActivityItem> Actions { get; } = [];
    public List<AgentResponseNorm> Responses { get; } = [];
    public TurnArtifacts Artifacts { get; } = new();

    public string? PlanText
    {
        get
        {
            if (Responses.Count == 0)
            {
                return null;
            }

            return string.Join("\n\n", Responses.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        }
    }

    public DateTimeOffset StartedAt => Prompt.Meta.Timestamp;
}

public sealed class SessionTimeline
{
    public required string SessionId { get; init; }
    public required string Agent { get; init; }
    public List<ActivityTurn> Turns { get; } = [];
    public List<CommitLink> Commits { get; } = [];
}
