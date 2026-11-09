using Whyso.Storage;

namespace Whyso.Activities;

public static class WorkTreeBuilder
{
    /// <summary>
    /// Compute automatic edges between Work Nodes. Manual edges are not returned.
    /// </summary>
    public static IReadOnlyList<WorkNodeEdgeRecord> BuildAutoEdges(
        string gitRoot,
        IReadOnlyList<WorkNodeRecord> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filesByNode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> commitsByNode)
    {
        var ordered = nodes
            .OrderBy(n => n.StartedAt)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count < 2)
        {
            return [];
        }

        var edges = new List<WorkNodeEdgeRecord>();
        var now = DateTimeOffset.UtcNow;
        var linked = new HashSet<(string From, string To)>();

        // git_ancestry: prior node head/commits ancestor of later node head/commits
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var older = ordered[i];
                var newer = ordered[j];
                if (HasGitAncestry(gitRoot, older, newer, commitsByNode))
                {
                    Add(edges, linked, older.Id, newer.Id, WorkNodeEdgeKinds.GitAncestry, score: 1.0, now);
                }
            }
        }

        // file_overlap: link each node to the most recent prior node with shared write paths
        for (var i = 1; i < ordered.Count; i++)
        {
            var newer = ordered[i];
            filesByNode.TryGetValue(newer.Id, out var newerFiles);
            newerFiles ??= [];
            if (newerFiles.Count == 0)
            {
                continue;
            }

            var newerSet = new HashSet<string>(newerFiles, StringComparer.OrdinalIgnoreCase);
            for (var j = i - 1; j >= 0; j--)
            {
                var older = ordered[j];
                filesByNode.TryGetValue(older.Id, out var olderFiles);
                olderFiles ??= [];
                if (olderFiles.Count == 0)
                {
                    continue;
                }

                var overlap = olderFiles.Count(p => newerSet.Contains(p));
                if (overlap == 0)
                {
                    continue;
                }

                var union = olderFiles.Concat(newerFiles).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var score = union == 0 ? 0 : (double)overlap / union;
                Add(edges, linked, older.Id, newer.Id, WorkNodeEdgeKinds.FileOverlap, score, now);
                break;
            }
        }

        // temporal fallback: previous completed node when no stronger edge into this node
        for (var i = 1; i < ordered.Count; i++)
        {
            var newer = ordered[i];
            if (linked.Any(e => e.To == newer.Id))
            {
                continue;
            }

            var older = ordered[i - 1];
            Add(edges, linked, older.Id, newer.Id, WorkNodeEdgeKinds.Temporal, score: 0.1, now);
        }

        return edges;
    }

    private static bool HasGitAncestry(
        string gitRoot,
        WorkNodeRecord older,
        WorkNodeRecord newer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> commitsByNode)
    {
        var olderTips = new List<string>();
        if (!string.IsNullOrWhiteSpace(older.GitHeadAfter))
        {
            olderTips.Add(older.GitHeadAfter!);
        }

        if (commitsByNode.TryGetValue(older.Id, out var olderCommits))
        {
            olderTips.AddRange(olderCommits);
        }

        var newerTips = new List<string>();
        if (!string.IsNullOrWhiteSpace(newer.GitHead))
        {
            newerTips.Add(newer.GitHead!);
        }

        if (!string.IsNullOrWhiteSpace(newer.GitHeadAfter))
        {
            newerTips.Add(newer.GitHeadAfter!);
        }

        if (commitsByNode.TryGetValue(newer.Id, out var newerCommits))
        {
            newerTips.AddRange(newerCommits);
        }

        olderTips = olderTips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        newerTips = newerTips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (olderTips.Count == 0 || newerTips.Count == 0)
        {
            return false;
        }

        foreach (var a in olderTips)
        {
            foreach (var b in newerTips)
            {
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (Git.IsAncestor(gitRoot, a, b))
                    {
                        return true;
                    }
                }
                catch
                {
                    // ignore git errors
                }
            }
        }

        return false;
    }

    private static void Add(
        List<WorkNodeEdgeRecord> edges,
        HashSet<(string From, string To)> linked,
        string from,
        string to,
        string kind,
        double score,
        DateTimeOffset createdAt)
    {
        if (from == to)
        {
            return;
        }

        linked.Add((from, to));
        edges.Add(new WorkNodeEdgeRecord
        {
            FromId = from,
            ToId = to,
            Kind = kind,
            Label = null,
            Score = score,
            Manual = false,
            CreatedAt = createdAt,
        });
    }
}
