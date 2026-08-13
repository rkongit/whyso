using System.Text.Json;
using System.Text.Json.Serialization;
using Worktree.Storage;

namespace Worktree.Activities;

public static class WorkNodeFormat
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteNodes(TextWriter writer, IReadOnlyList<WorkNodeListItem> items, bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(items.Select(i => new
            {
                id = i.Node.Id,
                session_id = i.Node.SessionId,
                agent = i.Node.Agent,
                started_at = i.Node.StartedAt,
                ended_at = i.Node.EndedAt,
                status = i.Node.Status,
                prompt_excerpt = i.Node.PromptExcerpt,
                turn_count = i.Node.TurnCount,
                file_count = i.Node.FileCount,
                command_count = i.Node.CommandCount,
                test_count = i.Node.TestCount,
                commit_count = i.Node.CommitCount,
                labels = i.Labels,
            }), JsonOptions));
            return;
        }

        if (items.Count == 0)
        {
            writer.WriteLine("No sessions yet. Capture activity (or `worktree rebuild`).");
            return;
        }

        writer.WriteLine(
            $"{"ID",-36} {"AGENT",-10} {"STARTED",-16} {"STATUS",-10} {"FILES",5} {"CMITS",5} LABELS / PROMPT");
        foreach (var item in items)
        {
            var n = item.Node;
            var started = n.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm");
            var labels = item.Labels.Count == 0 ? "-" : string.Join(",", item.Labels);
            var excerpt = Truncate(n.PromptExcerpt, 48);
            writer.WriteLine(
                $"{n.Id,-36} {n.Agent,-10} {started,-16} {n.Status,-10} {n.FileCount,5} {n.CommitCount,5} [{labels}] {excerpt}");
        }
    }

    public static void WriteNode(TextWriter writer, WorkNode node, bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                id = node.Id,
                session_id = node.SessionId,
                project_id = node.ProjectId,
                agent = node.Agent,
                started_at = node.StartedAt,
                ended_at = node.EndedAt,
                status = node.Status,
                git_head = node.GitHead,
                git_head_after = node.GitHeadAfter,
                prompts = node.Prompts,
                responses = node.Responses,
                files = node.FilesChanged.Select(f => new { path = f.Path, action = f.Action.ToString(), ranges = f.Ranges }),
                commands = node.Commands,
                tests = node.Tests.Select(t => new { name = t.Name, suite = t.Suite, status = t.Status, phase = t.Phase.ToString() }),
                commits = node.CommitShas,
                labels = node.Labels,
                edges = node.Edges.Select(e => new
                {
                    from = e.FromId,
                    to = e.ToId,
                    kind = e.Kind,
                    label = e.Label,
                    score = e.Score,
                    manual = e.Manual,
                }),
                turns = node.Timeline.Turns.Select(t => new
                {
                    prompt = t.Prompt.Text,
                    plan = t.PlanText,
                    actions = t.Actions.Select(a => a.SummaryLine()),
                    commits = t.Artifacts.CommitShas,
                    files = t.Artifacts.Files.Select(f => f.Path),
                }),
            }, JsonOptions));
            return;
        }

        writer.WriteLine($"Session {node.Id}");
        writer.WriteLine($"  session : {node.SessionId}");
        writer.WriteLine($"  agent   : {node.Agent}");
        writer.WriteLine($"  status  : {node.Status}");
        writer.WriteLine($"  started : {node.StartedAt.ToUniversalTime():O}");
        if (node.EndedAt is not null)
        {
            writer.WriteLine($"  ended   : {node.EndedAt.Value.ToUniversalTime():O}");
        }

        writer.WriteLine($"  git     : {Short(node.GitHead)} → {Short(node.GitHeadAfter)}");
        if (node.Labels.Count > 0)
        {
            writer.WriteLine($"  labels  : {string.Join(", ", node.Labels)}");
        }

        writer.WriteLine();
        writer.WriteLine("Prompts:");
        if (node.Prompts.Count == 0)
        {
            writer.WriteLine("  (none)");
        }
        else
        {
            foreach (var p in node.Prompts)
            {
                writer.WriteLine($"  - {Truncate(p, 400)}");
            }
        }

        if (node.Responses.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Agent responses / plan:");
            foreach (var r in node.Responses)
            {
                writer.WriteLine($"  - {Truncate(r, 800)}");
            }
        }

        writer.WriteLine();
        ActivityFormat.WriteTimeline(writer, node.Timeline);

        if (node.CommitShas.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Commits: " + string.Join(", ", node.CommitShas.Select(Short)));
        }

        if (node.Edges.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Edges:");
            foreach (var e in node.Edges)
            {
                var label = string.IsNullOrWhiteSpace(e.Label) ? "" : $" label={e.Label}";
                var manual = e.Manual ? " manual" : "";
                writer.WriteLine($"  {e.FromId} -[{e.Kind}{label}{manual}]-> {e.ToId}");
            }
        }
    }

    public static void WriteTree(
        TextWriter writer,
        IReadOnlyList<WorkNodeRecord> nodes,
        IReadOnlyList<WorkNodeEdgeRecord> edges,
        string? fromId,
        bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                nodes = nodes.Select(n => new { id = n.Id, excerpt = n.PromptExcerpt, status = n.Status }),
                edges = edges.Select(e => new
                {
                    from = e.FromId,
                    to = e.ToId,
                    kind = e.Kind,
                    label = e.Label,
                    manual = e.Manual,
                    score = e.Score,
                }),
                from = fromId,
            }, JsonOptions));
            return;
        }

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var filtered = fromId is null
            ? edges
            : edges.Where(e => e.FromId == fromId || e.ToId == fromId || Reachable(fromId, e, edges)).ToList();

        writer.WriteLine("Work Tree");
        if (filtered.Count == 0)
        {
            writer.WriteLine("  (no edges — run `worktree rebuild` or `worktree link`)");
            foreach (var n in nodes.OrderBy(n => n.StartedAt))
            {
                writer.WriteLine($"  • {n.Id}  {Truncate(n.PromptExcerpt, 60)}");
            }

            return;
        }

        foreach (var e in filtered.OrderBy(e => e.CreatedAt))
        {
            var fromEx = byId.TryGetValue(e.FromId, out var f) ? Truncate(f.PromptExcerpt, 40) : "";
            var toEx = byId.TryGetValue(e.ToId, out var t) ? Truncate(t.PromptExcerpt, 40) : "";
            var label = string.IsNullOrWhiteSpace(e.Label) ? "" : $" ({e.Label})";
            var manual = e.Manual ? "*" : "";
            writer.WriteLine($"  {e.FromId}  {fromEx}");
            writer.WriteLine($"    └─[{e.Kind}{label}]{manual}→ {e.ToId}  {toEx}");
        }
    }

    public static void WriteHistory(
        TextWriter writer,
        IReadOnlyList<ProjectHistoryEntry> entries,
        bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(entries.Select(e => new
            {
                id = e.Node.Id,
                started_at = e.Node.StartedAt,
                status = e.Node.Status,
                prompt_excerpt = e.Node.PromptExcerpt,
                labels = e.Labels,
                top_files = e.TopFiles,
                commits = e.CommitShas,
                incoming_edges = e.IncomingEdges,
                outgoing_edges = e.OutgoingEdges,
            }), JsonOptions));
            return;
        }

        if (entries.Count == 0)
        {
            writer.WriteLine("No sessions yet.");
            return;
        }

        writer.WriteLine("Sessions");
        foreach (var e in entries)
        {
            var n = e.Node;
            writer.WriteLine();
            writer.WriteLine($"{n.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm}  {n.Id}  ({n.Status})  {n.Agent}");
            writer.WriteLine($"  prompt : {Truncate(n.PromptExcerpt, 100)}");
            if (e.Labels.Count > 0)
            {
                writer.WriteLine($"  labels : {string.Join(", ", e.Labels)}");
            }

            if (e.CommitShas.Count > 0)
            {
                writer.WriteLine($"  commits: {string.Join(", ", e.CommitShas.Select(Short))}");
            }

            if (e.TopFiles.Count > 0)
            {
                writer.WriteLine($"  files  : {string.Join(", ", e.TopFiles.Take(8))}");
            }

            writer.WriteLine($"  edges  : in={e.IncomingEdges} out={e.OutgoingEdges}");
            writer.WriteLine($"  inspect: worktree session {n.Id}");
        }
    }

    public static void WriteFileHistory(TextWriter writer, IReadOnlyList<FileHistoryEntry> entries, bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(entries, JsonOptions));
            return;
        }

        if (entries.Count == 0)
        {
            writer.WriteLine("No file history in sessions.");
            return;
        }

        writer.WriteLine("File history");
        foreach (var e in entries)
        {
            writer.WriteLine(
                $"{e.At.ToUniversalTime():yyyy-MM-dd HH:mm}  {e.Action,-9}  session={e.WorkNodeId}  {e.Path}");
            if (!string.IsNullOrWhiteSpace(e.PromptExcerpt))
            {
                writer.WriteLine($"    prompt: {Truncate(e.PromptExcerpt!, 100)}");
            }

            if (e.CommitShas.Count > 0)
            {
                writer.WriteLine($"    commits: {string.Join(", ", e.CommitShas.Select(Short))}");
            }
        }
    }

    public static void WriteSearch(
        TextWriter writer,
        IReadOnlyList<WorkNodeSearchHit> hits,
        IReadOnlyDictionary<string, WorkNodeRecord> nodes,
        bool json)
    {
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(hits.Select(h => new
            {
                work_node_id = h.WorkNodeId,
                field = h.Field,
                snippet = h.Snippet,
                prompt_excerpt = nodes.TryGetValue(h.WorkNodeId, out var n) ? n.PromptExcerpt : null,
            }), JsonOptions));
            return;
        }

        if (hits.Count == 0)
        {
            writer.WriteLine("No matches.");
            return;
        }

        writer.WriteLine($"Search results ({hits.Count})");
        foreach (var h in hits)
        {
            var excerpt = nodes.TryGetValue(h.WorkNodeId, out var n) ? Truncate(n.PromptExcerpt, 60) : "";
            writer.WriteLine($"  {h.WorkNodeId}  [{h.Field}]  {excerpt}");
            if (!string.IsNullOrWhiteSpace(h.Snippet))
            {
                writer.WriteLine($"    {h.Snippet}");
            }

            writer.WriteLine($"    → worktree session {h.WorkNodeId}");
        }
    }

    private static bool Reachable(string fromId, WorkNodeEdgeRecord edge, IReadOnlyList<WorkNodeEdgeRecord> all)
    {
        // Lightweight: only direct neighbors already filtered; keep helper for future BFS.
        _ = all;
        return edge.FromId == fromId || edge.ToId == fromId;
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "-" : sha.Length <= 7 ? sha : sha[..7];

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}
