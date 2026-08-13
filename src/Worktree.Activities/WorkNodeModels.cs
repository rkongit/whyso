using Worktree.Events;
using Worktree.Storage;

namespace Worktree.Activities;

public static class WorkNodeEdgeKinds
{
    public const string GitAncestry = "git_ancestry";
    public const string FileOverlap = "file_overlap";
    public const string Temporal = "temporal";
    public const string Manual = "manual";
}

/// <summary>Rich inspect view: reconstructed session activities + indexed metadata.</summary>
public sealed class WorkNode
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }
    public required string Agent { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? GitHead { get; init; }
    public string? GitHeadAfter { get; init; }
    public required string Status { get; init; }
    public required SessionTimeline Timeline { get; init; }
    public IReadOnlyList<string> Prompts { get; init; } = [];
    public IReadOnlyList<string> Responses { get; init; } = [];
    public IReadOnlyList<FileArtifact> FilesChanged { get; init; } = [];
    public IReadOnlyList<string> Commands { get; init; } = [];
    public IReadOnlyList<ActivityItem.Test> Tests { get; init; } = [];
    public IReadOnlyList<string> CommitShas { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<WorkNodeEdgeRecord> Edges { get; init; } = [];
}

public sealed class WorkNodeListItem
{
    public required WorkNodeRecord Node { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
}

public sealed class FileHistoryEntry
{
    public required string WorkNodeId { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public DateTimeOffset At { get; init; }
    public IReadOnlyList<string> CommitShas { get; init; } = [];
    public string? PromptExcerpt { get; init; }
}

public sealed class ProjectHistoryEntry
{
    public required WorkNodeRecord Node { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> TopFiles { get; init; } = [];
    public IReadOnlyList<string> CommitShas { get; init; } = [];
    public int IncomingEdges { get; init; }
    public int OutgoingEdges { get; init; }
}
