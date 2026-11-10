namespace Whyso.Activities;

public abstract record ProvenanceSubject
{
    public sealed record Commit(string Sha) : ProvenanceSubject;

    public sealed record FilePath(string Path) : ProvenanceSubject;

    public sealed record CodeRange(string Path, int StartLine, int EndLine) : ProvenanceSubject;
}

public enum ProvenanceMatchKind
{
    ExactCommit,
    FileTouch,
    EditOverlap,
    DegradedToFile,
}

public sealed class ProvenanceHit
{
    public required ActivityTurn Turn { get; init; }
    public required ProvenanceMatchKind MatchKind { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Work Node id (v1: same as session id).</summary>
    public string WorkNodeId => SessionId;
}

public sealed class ProvenanceResult
{
    public required ProvenanceSubject Subject { get; init; }
    public IReadOnlyList<ProvenanceHit> Hits { get; init; } = [];
}
