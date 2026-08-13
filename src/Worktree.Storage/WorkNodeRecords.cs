namespace Worktree.Storage;

public sealed class WorkNodeRecord
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
    public string PromptExcerpt { get; init; } = "";
    public int TurnCount { get; init; }
    public int FileCount { get; init; }
    public int CommandCount { get; init; }
    public int TestCount { get; init; }
    public int CommitCount { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class WorkNodeFileRecord
{
    public required string WorkNodeId { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

public sealed class WorkNodeCommandRecord
{
    public required string WorkNodeId { get; init; }
    public required string CommandText { get; init; }
    public DateTimeOffset SeenAt { get; init; }
}

public sealed class WorkNodeTestRecord
{
    public required string WorkNodeId { get; init; }
    public string? Name { get; init; }
    public string? Suite { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset SeenAt { get; init; }
}

public sealed class WorkNodeEdgeRecord
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public double? Score { get; init; }
    public bool Manual { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class WorkNodeLabelRecord
{
    public required string WorkNodeId { get; init; }
    public required string Label { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class WorkNodeFtsDocument
{
    public required string WorkNodeId { get; init; }
    public string Prompt { get; init; } = "";
    public string Plan { get; init; } = "";
    public string Paths { get; init; } = "";
    public string Commands { get; init; } = "";
    public string Tests { get; init; } = "";
    public string Commits { get; init; } = "";
}

/// <summary>Full projection payload for upserting one Work Node (auto fields only).</summary>
public sealed class WorkNodeProjection
{
    public required WorkNodeRecord Node { get; init; }
    public IReadOnlyList<WorkNodeFileRecord> Files { get; init; } = [];
    public IReadOnlyList<WorkNodeCommandRecord> Commands { get; init; } = [];
    public IReadOnlyList<WorkNodeTestRecord> Tests { get; init; } = [];
    public required WorkNodeFtsDocument Fts { get; init; }
}

public sealed record WorkNodeSearchHit
{
    public required string WorkNodeId { get; init; }
    public required string Snippet { get; init; }
    public required string Field { get; init; }
}
