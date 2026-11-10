using Whyso.Activities;
using Whyso.Storage;

namespace Whyso.Ui;

internal static class UiQueries
{
    public static async Task<SessionTimeline> LoadTimelineAsync(Store store, string sessionId)
    {
        var events = await store.ListEventsAsync(sessionId);
        var commits = await store.ListSessionCommitsAsync(sessionId);
        var links = commits.Select(c => new CommitLink
        {
            Sha = c.Sha,
            SessionId = c.SessionId,
            PromptEventId = c.PromptEventId,
            CapturedAt = c.CapturedAt,
        }).ToList();
        return ActivityReconstructor.Reconstruct(events, links);
    }

    public static async Task EnsureNodeAsync(Store store, string id)
    {
        if (await store.GetWorkNodeAsync(id) is not null)
        {
            return;
        }

        var session = await store.GetSessionAsync(id);
        if (session is null)
        {
            return;
        }

        await WorkNodeIndex.ProjectSessionAsync(store, session);
    }

    public static async Task EnsureNodesAsync(Store store)
    {
        // Covers sessions that are still running (no session-end projection yet) as well as a
        // database with no projections at all.
        await WorkNodeIndex.RefreshStaleAsync(store, store.Root);
    }

    public static bool TryParseLineRange(string text, out int start, out int end)
    {
        start = 0;
        end = 0;
        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out start) && int.TryParse(parts[1], out end);
    }

    public static async Task<string> ProbeDaemonAsync(string bind)
    {
        var healthUrl = $"http://{bind}/v1/health";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            var resp = await client.GetAsync(healthUrl);
            return resp.IsSuccessStatusCode
                ? "running"
                : $"unreachable (HTTP {(int)resp.StatusCode})";
        }
        catch
        {
            return "not running";
        }
    }

    public static ProvenanceResultDto ToProvenanceDto(ProvenanceResult result)
    {
        string subjectKind;
        string? sha = null;
        string? path = null;
        int? startLine = null;
        int? endLine = null;

        switch (result.Subject)
        {
            case ProvenanceSubject.Commit c:
                subjectKind = "commit";
                sha = c.Sha;
                break;
            case ProvenanceSubject.FilePath f:
                subjectKind = "file";
                path = f.Path;
                break;
            case ProvenanceSubject.CodeRange r:
                subjectKind = "code_range";
                path = r.Path;
                startLine = r.StartLine;
                endLine = r.EndLine;
                break;
            default:
                subjectKind = "unknown";
                break;
        }

        return new ProvenanceResultDto
        {
            SubjectKind = subjectKind,
            Sha = sha,
            Path = path,
            StartLine = startLine,
            EndLine = endLine,
            Hits = result.Hits.Select(h => new ProvenanceHitDto
            {
                SessionId = h.SessionId,
                WorkNodeId = h.WorkNodeId,
                MatchKind = h.MatchKind.ToString(),
                Prompt = h.Turn.Prompt.Text ?? "",
                Plan = h.Turn.PlanText,
                StartedAt = h.Turn.StartedAt,
                GenerationId = h.Turn.GenerationId,
                Files = h.Turn.Artifacts.Files.Select(f => f.Path).ToList(),
                Commits = h.Turn.Artifacts.CommitShas,
            }).ToList(),
        };
    }
}
