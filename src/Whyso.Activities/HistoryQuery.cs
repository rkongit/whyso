using Whyso.Storage;

namespace Whyso.Activities;

public static class HistoryQuery
{
    public static async Task<IReadOnlyList<ProjectHistoryEntry>> ProjectHistoryAsync(
        Store store,
        string? filePath = null,
        string? pathPrefix = null)
    {
        var nodes = await store.ListWorkNodesAsync();
        var labels = await store.ListWorkNodeLabelsAsync();
        var edges = await store.ListWorkNodeEdgesAsync();
        var labelsByNode = labels
            .GroupBy(l => l.WorkNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Label).ToList(), StringComparer.Ordinal);

        HashSet<string>? filterIds = null;
        if (!string.IsNullOrWhiteSpace(filePath) || !string.IsNullOrWhiteSpace(pathPrefix))
        {
            var files = await store.ListWorkNodeFilesAsync(path: filePath, pathPrefix: pathPrefix);
            filterIds = files.Select(f => f.WorkNodeId).ToHashSet(StringComparer.Ordinal);
            nodes = nodes.Where(n => filterIds.Contains(n.Id)).ToList();
        }

        var entries = new List<ProjectHistoryEntry>();
        var sessionsById = (await store.ListSessionsAsync())
            .ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(n => n.StartedAt))
        {
            var files = await store.ListWorkNodeFilesAsync(workNodeId: node.Id);
            var commits = await store.ListSessionCommitsAsync(node.SessionId);
            var liveStatus = sessionsById.TryGetValue(node.SessionId, out var sess)
                ? sess.Status.AsString()
                : node.Status;
            entries.Add(new ProjectHistoryEntry
            {
                Node = new WorkNodeRecord
                {
                    Id = node.Id,
                    SessionId = node.SessionId,
                    ProjectId = node.ProjectId,
                    Agent = node.Agent,
                    StartedAt = node.StartedAt,
                    EndedAt = node.EndedAt,
                    GitHead = node.GitHead,
                    GitHeadAfter = node.GitHeadAfter,
                    Status = liveStatus,
                    PromptExcerpt = node.PromptExcerpt,
                    TurnCount = node.TurnCount,
                    FileCount = node.FileCount,
                    CommandCount = node.CommandCount,
                    TestCount = node.TestCount,
                    CommitCount = node.CommitCount,
                    UpdatedAt = node.UpdatedAt,
                },
                Labels = labelsByNode.TryGetValue(node.Id, out var lbs) ? lbs : [],
                TopFiles = files
                    .Select(f => f.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList(),
                CommitShas = commits.Select(c => c.Sha).ToList(),
                IncomingEdges = edges.Count(e => e.ToId == node.Id),
                OutgoingEdges = edges.Count(e => e.FromId == node.Id),
            });
        }

        return entries;
    }

    public static async Task<IReadOnlyList<FileHistoryEntry>> FileHistoryAsync(Store store, string path)
    {
        var files = await store.ListWorkNodeFilesAsync(path: path);
        var nodes = (await store.ListWorkNodesAsync()).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var entries = new List<FileHistoryEntry>();
        foreach (var file in files.OrderBy(f => f.LastSeen))
        {
            nodes.TryGetValue(file.WorkNodeId, out var node);
            var commits = await store.ListSessionCommitsAsync(file.WorkNodeId);
            entries.Add(new FileHistoryEntry
            {
                WorkNodeId = file.WorkNodeId,
                Path = file.Path,
                Action = file.Action,
                At = file.LastSeen,
                CommitShas = commits.Select(c => c.Sha).ToList(),
                PromptExcerpt = node?.PromptExcerpt,
            });
        }

        return entries;
    }

    public static async Task<IReadOnlyList<WorkNodeSearchHit>> SearchAsync(
        Store store,
        string query,
        string? type,
        string? fromKind,
        string? fromRef)
    {
        var hits = (await store.SearchWorkNodesAsync(query)).ToList();

        if (!string.IsNullOrWhiteSpace(fromKind) && !string.IsNullOrWhiteSpace(fromRef))
        {
            var seedIds = await ResolveSeedNodeIdsAsync(store, fromKind, fromRef);
            if (seedIds.Count > 0)
            {
                var edges = await store.ListWorkNodeEdgesAsync();
                var neighborhood = new HashSet<string>(seedIds, StringComparer.Ordinal);
                foreach (var e in edges)
                {
                    if (seedIds.Contains(e.FromId) || seedIds.Contains(e.ToId))
                    {
                        neighborhood.Add(e.FromId);
                        neighborhood.Add(e.ToId);
                    }
                }

                hits = hits.Where(h => neighborhood.Contains(h.WorkNodeId)).ToList();
            }
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            hits = await FilterByTypeAsync(store, hits, type);
        }

        return hits;
    }

    private static async Task<HashSet<string>> ResolveSeedNodeIdsAsync(Store store, string kind, string reference)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        switch (kind.Trim().ToLowerInvariant())
        {
            case "prompt":
            case "node":
            {
                var node = await store.GetWorkNodeAsync(reference);
                if (node is not null)
                {
                    ids.Add(node.Id);
                }
                else
                {
                    var hits = await store.SearchWorkNodesAsync(reference, limit: 20);
                    foreach (var h in hits)
                    {
                        ids.Add(h.WorkNodeId);
                    }
                }

                break;
            }
            case "file":
            {
                foreach (var id in await store.ListWorkNodeIdsForFileAsync(reference))
                {
                    ids.Add(id);
                }

                break;
            }
            case "commit":
            {
                var commits = await store.ListCommitsByShaAsync(reference);
                foreach (var c in commits)
                {
                    ids.Add(c.SessionId);
                }

                break;
            }
        }

        return ids;
    }

    private static async Task<List<WorkNodeSearchHit>> FilterByTypeAsync(
        Store store,
        List<WorkNodeSearchHit> hits,
        string type)
    {
        var t = type.Trim().ToLowerInvariant();
        var result = new List<WorkNodeSearchHit>();
        foreach (var hit in hits)
        {
            switch (t)
            {
                case "prompt":
                case "node":
                    result.Add(hit with { Field = t });
                    break;
                case "file":
                {
                    var files = await store.ListWorkNodeFilesAsync(workNodeId: hit.WorkNodeId);
                    if (files.Count > 0)
                    {
                        result.Add(new WorkNodeSearchHit
                        {
                            WorkNodeId = hit.WorkNodeId,
                            Snippet = string.Join(", ", files.Select(f => f.Path).Take(5)),
                            Field = "file",
                        });
                    }

                    break;
                }
                case "commit":
                {
                    var commits = await store.ListSessionCommitsAsync(hit.WorkNodeId);
                    if (commits.Count > 0)
                    {
                        result.Add(new WorkNodeSearchHit
                        {
                            WorkNodeId = hit.WorkNodeId,
                            Snippet = string.Join(", ", commits.Select(c => c.Sha[..Math.Min(7, c.Sha.Length)])),
                            Field = "commit",
                        });
                    }

                    break;
                }
                case "test":
                {
                    var tests = await store.ListWorkNodeTestsAsync(hit.WorkNodeId);
                    if (tests.Count > 0)
                    {
                        result.Add(new WorkNodeSearchHit
                        {
                            WorkNodeId = hit.WorkNodeId,
                            Snippet = string.Join(", ", tests.Select(x => x.Name ?? x.Suite ?? "?").Take(5)),
                            Field = "test",
                        });
                    }

                    break;
                }
                case "command":
                {
                    var cmds = await store.ListWorkNodeCommandsAsync(hit.WorkNodeId);
                    if (cmds.Count > 0)
                    {
                        result.Add(new WorkNodeSearchHit
                        {
                            WorkNodeId = hit.WorkNodeId,
                            Snippet = string.Join("; ", cmds.Select(c => c.CommandText).Take(3)),
                            Field = "command",
                        });
                    }

                    break;
                }
                default:
                    result.Add(hit);
                    break;
            }
        }

        return result;
    }
}
