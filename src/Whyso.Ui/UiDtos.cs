using System.Text.Json.Nodes;

namespace Whyso.Ui;

public sealed class ProjectDto
{
    public required string ProjectId { get; init; }
    public required string Root { get; init; }
    public required long SessionCount { get; init; }
}

public sealed class NodeListItemDto
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Agent { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required string Status { get; init; }
    public required string PromptExcerpt { get; init; }
    public required int TurnCount { get; init; }
    public required int FileCount { get; init; }
    public required int CommandCount { get; init; }
    public required int TestCount { get; init; }
    public required int CommitCount { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
}

public sealed class LineRangeDto
{
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
}

public sealed class FileArtifactDto
{
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required IReadOnlyList<LineRangeDto> Ranges { get; init; }
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
}

public sealed class TestDto
{
    public string? Name { get; init; }
    public string? Suite { get; init; }
    public string? Status { get; init; }
    public long? Passed { get; init; }
    public long? Failed { get; init; }
    public required string Phase { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

public sealed class EdgeDto
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public double? Score { get; init; }
    public required bool Manual { get; init; }
}

public sealed class ActivityDto
{
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Name { get; init; }
    public string? Path { get; init; }
    public string? Action { get; init; }
    public string? Command { get; init; }
    public string? Phase { get; init; }
    public string? Status { get; init; }
    public long? Passed { get; init; }
    public long? Failed { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonNode? Edits { get; init; }
    public JsonNode? Output { get; init; }
}

public sealed class TurnDto
{
    public required string Prompt { get; init; }
    public string? Plan { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string? GenerationId { get; init; }
    public required IReadOnlyList<ActivityDto> Actions { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> Commits { get; init; }
}

public sealed class AfterwardsNodeDto
{
    public required string Id { get; init; }
    public required string PromptExcerpt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Status { get; init; }
    public string? Via { get; init; }
}

public sealed class NodeDetailDto
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }
    public required string Agent { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double? DurationSeconds { get; init; }
    public required string Status { get; init; }
    public string? GitHead { get; init; }
    public string? GitHeadAfter { get; init; }
    public required IReadOnlyList<string> Prompts { get; init; }
    public required IReadOnlyList<string> Responses { get; init; }
    public required IReadOnlyList<FileArtifactDto> Files { get; init; }
    public required IReadOnlyList<string> Commands { get; init; }
    public required IReadOnlyList<TestDto> Tests { get; init; }
    public required IReadOnlyList<string> Commits { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required IReadOnlyList<EdgeDto> Edges { get; init; }
    public required IReadOnlyList<TurnDto> Turns { get; init; }
    public required IReadOnlyList<AfterwardsNodeDto> Afterwards { get; init; }
    public required int ActivityCount { get; init; }
}

public sealed class TreeResponseDto
{
    public required IReadOnlyList<NodeListItemDto> Nodes { get; init; }
    public required IReadOnlyList<EdgeDto> Edges { get; init; }
}

public sealed class HistoryEntryDto
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required string Status { get; init; }
    public required string PromptExcerpt { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required IReadOnlyList<string> TopFiles { get; init; }
    public required IReadOnlyList<string> CommitShas { get; init; }
    public required int IncomingEdges { get; init; }
    public required int OutgoingEdges { get; init; }
}

public sealed class SearchHitDto
{
    public required string WorkNodeId { get; init; }
    public required string Snippet { get; init; }
    public required string Field { get; init; }
}

public sealed class FileDetailDto
{
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required IReadOnlyList<LineRangeDto> Ranges { get; init; }
    public JsonNode? Edits { get; init; }
    public string? Snippet { get; init; }
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
}

public sealed class GitFileStatDto
{
    public required string Path { get; init; }
    public required int Additions { get; init; }
    public required int Deletions { get; init; }
}

public sealed class StatusDto
{
    public required string ProjectId { get; init; }
    public required string Root { get; init; }
    public required string Bind { get; init; }
    public required string DaemonStatus { get; init; }
    public required bool DaemonRunning { get; init; }
    public required long SessionCount { get; init; }
    public required long PromptCount { get; init; }
}

public sealed class SessionDto
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Agent { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required string Status { get; init; }
    public string? GitHead { get; init; }
    public string? GitHeadAfter { get; init; }
    public required string PromptExcerpt { get; init; }
    public required int TurnCount { get; init; }
    public required int FileCount { get; init; }
    public required int CommandCount { get; init; }
    public required int TestCount { get; init; }
    public required int CommitCount { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required IReadOnlyList<string> TopFiles { get; init; }
    public required IReadOnlyList<string> CommitShas { get; init; }
    public required int IncomingEdges { get; init; }
    public required int OutgoingEdges { get; init; }
}

public sealed class FileTouchDto
{
    public required string SessionId { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? PromptExcerpt { get; init; }
    public required IReadOnlyList<string> CommitShas { get; init; }
}

public sealed class ActivityTimelineDto
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<TurnDto> Turns { get; init; }
}

public sealed class ProvenanceHitDto
{
    public required string SessionId { get; init; }
    public required string WorkNodeId { get; init; }
    public required string MatchKind { get; init; }
    public required string Prompt { get; init; }
    public string? Plan { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string? GenerationId { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> Commits { get; init; }
}

public sealed class ProvenanceResultDto
{
    public required string SubjectKind { get; init; }
    public string? Sha { get; init; }
    public string? Path { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public required IReadOnlyList<ProvenanceHitDto> Hits { get; init; }
}

public sealed class LinkEdgeRequest
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string? Label { get; init; }
}

public sealed class LabelRequest
{
    public required string Label { get; init; }
}

public sealed class RebuildResultDto
{
    public required int NodeCount { get; init; }
}
