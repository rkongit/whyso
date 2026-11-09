using Whyso.Storage;

namespace Whyso.Activities;

/// <summary>Orchestrates Work Node projection upserts and auto edge rebuilds.</summary>
public static class WorkNodeIndex
{
    public static async Task<WorkNode> ProjectSessionAsync(Store store, SessionRecord session)
    {
        var events = await store.ListEventsAsync(session.Id);
        var commits = await store.ListSessionCommitsAsync(session.Id);
        var links = commits.Select(c => new CommitLink
        {
            Sha = c.Sha,
            SessionId = c.SessionId,
            PromptEventId = c.PromptEventId,
            CapturedAt = c.CapturedAt,
        }).ToList();
        var timeline = ActivityReconstructor.Reconstruct(events, links);
        var labels = (await store.ListWorkNodeLabelsAsync(session.Id))
            .Select(l => l.Label)
            .ToList();
        var edges = await store.ListWorkNodeEdgesAsync(session.Id);
        var node = WorkNodeBuilder.Build(session, timeline, labels, edges);
        var projection = WorkNodeBuilder.ToProjection(node);
        await store.UpsertWorkNodeProjectionAsync(projection);
        return node;
    }

    public static async Task<int> RebuildAllAsync(Store store, string gitRoot)
    {
        var sessions = await store.ListSessionsAsync();
        var count = 0;
        foreach (var session in sessions)
        {
            await ProjectSessionAsync(store, session);
            count++;
        }

        await RebuildAutoEdgesAsync(store, gitRoot);
        return count;
    }

    public static async Task RebuildAutoEdgesAsync(Store store, string gitRoot)
    {
        var nodes = await store.ListWorkNodesAsync();
        var filesByNode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var commitsByNode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var files = await store.ListWorkNodeFilesAsync(workNodeId: node.Id);
            filesByNode[node.Id] = files
                .Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var commits = await store.ListSessionCommitsAsync(node.SessionId);
            commitsByNode[node.Id] = commits.Select(c => c.Sha).ToList();
        }

        var edges = WorkTreeBuilder.BuildAutoEdges(gitRoot, nodes, filesByNode, commitsByNode);
        await store.ReplaceAutoWorkNodeEdgesAsync(edges);
    }

    public static async Task ProjectSessionByIdAsync(Store store, string sessionId)
    {
        var session = await store.GetSessionAsync(sessionId);
        if (session is null)
        {
            return;
        }

        await ProjectSessionAsync(store, session);
        await RebuildAutoEdgesAsync(store, store.Root);
    }
}
