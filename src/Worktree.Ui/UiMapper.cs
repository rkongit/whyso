using System.Text.Json.Nodes;
using Worktree.Activities;
using Worktree.Events;
using Worktree.Storage;

namespace Worktree.Ui;

public static class UiMapper
{
    public static NodeListItemDto ToListItem(WorkNodeRecord n, IReadOnlyList<string> labels) => new()
    {
        Id = n.Id,
        SessionId = n.SessionId,
        Agent = n.Agent,
        StartedAt = n.StartedAt,
        EndedAt = n.EndedAt,
        Status = n.Status,
        PromptExcerpt = n.PromptExcerpt,
        TurnCount = n.TurnCount,
        FileCount = n.FileCount,
        CommandCount = n.CommandCount,
        TestCount = n.TestCount,
        CommitCount = n.CommitCount,
        Labels = labels,
    };

    public static async Task<NodeDetailDto> ToDetailAsync(Store store, WorkNode node)
    {
        var stats = GitDiffStats.Numstat(store.Root, node.GitHead, node.GitHeadAfter);
        var files = node.FilesChanged
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.Last();
                stats.TryGetValue(last.Path, out var st);
                return new FileArtifactDto
                {
                    Path = last.Path,
                    Action = last.Action.ToString(),
                    Ranges = last.Ranges.Select(r => new LineRangeDto
                    {
                        StartLine = r.StartLine,
                        EndLine = r.EndLine,
                    }).ToList(),
                    Additions = st?.Additions,
                    Deletions = st?.Deletions,
                };
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allNodes = await store.ListWorkNodesAsync();
        var afterwards = BuildAfterwards(node, allNodes);

        var activityCount = node.Timeline.Turns.Sum(t =>
            1 + t.Actions.Count + t.Responses.Count);

        double? durationSeconds = null;
        if (node.EndedAt is not null)
        {
            durationSeconds = (node.EndedAt.Value - node.StartedAt).TotalSeconds;
        }

        return new NodeDetailDto
        {
            Id = node.Id,
            SessionId = node.SessionId,
            ProjectId = node.ProjectId,
            Agent = node.Agent,
            StartedAt = node.StartedAt,
            EndedAt = node.EndedAt,
            DurationSeconds = durationSeconds,
            Status = node.Status,
            GitHead = node.GitHead,
            GitHeadAfter = node.GitHeadAfter,
            Prompts = node.Prompts,
            Responses = node.Responses,
            Files = files,
            Commands = node.Commands,
            Tests = node.Tests.Select(ToTest).ToList(),
            Commits = node.CommitShas,
            Labels = node.Labels,
            Edges = node.Edges.Select(ToEdge).ToList(),
            Turns = node.Timeline.Turns.Select(ToTurn).ToList(),
            Afterwards = afterwards,
            ActivityCount = activityCount,
        };
    }

    public static FileDetailDto? FindFileDetail(WorkNode node, string path)
    {
        var normalized = path.Replace('\\', '/');
        ActivityItem.File? best = null;
        FileArtifact? artifact = null;

        foreach (var turn in node.Timeline.Turns)
        {
            foreach (var item in turn.Actions)
            {
                if (item is ActivityItem.File f
                    && f.Path is not null
                    && string.Equals(f.Path.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    best = f;
                }
            }

            foreach (var file in turn.Artifacts.Files)
            {
                if (string.Equals(file.Path.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    artifact = file;
                }
            }
        }

        if (best is null && artifact is null)
        {
            var listed = node.FilesChanged.LastOrDefault(f =>
                string.Equals(f.Path.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
            if (listed is null)
            {
                return null;
            }

            artifact = listed;
        }

        var action = best?.Action.ToString() ?? artifact?.Action.ToString() ?? "Modified";
        var ranges = new List<LineRangeDto>();
        if (artifact is not null && artifact.Ranges.Count > 0)
        {
            ranges.AddRange(artifact.Ranges.Select(r => new LineRangeDto
            {
                StartLine = r.StartLine,
                EndLine = r.EndLine,
            }));
        }
        else if (best is not null)
        {
            ranges.AddRange(EditRangeExtractorPublic(best.Edits).Select(r => new LineRangeDto
            {
                StartLine = r.StartLine,
                EndLine = r.EndLine,
            }));
        }

        var edits = best?.Edits;
        var snippet = ExtractSnippet(edits);

        return new FileDetailDto
        {
            Path = artifact?.Path ?? best?.Path ?? path,
            Action = action,
            Ranges = ranges,
            Edits = edits,
            Snippet = snippet,
            Additions = null,
            Deletions = null,
        };
    }

    public static FileDetailDto EnrichFileDetail(FileDetailDto detail, string root, string? gitHead, string? gitHeadAfter)
    {
        var stats = GitDiffStats.Numstat(root, gitHead, gitHeadAfter);
        if (stats.TryGetValue(detail.Path, out var st))
        {
            return new FileDetailDto
            {
                Path = detail.Path,
                Action = detail.Action,
                Ranges = detail.Ranges,
                Edits = detail.Edits,
                Snippet = detail.Snippet,
                Additions = st.Additions,
                Deletions = st.Deletions,
            };
        }

        return detail;
    }

    private static IReadOnlyList<AfterwardsNodeDto> BuildAfterwards(
        WorkNode node,
        IReadOnlyList<WorkNodeRecord> allNodes)
    {
        var result = new List<AfterwardsNodeDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };

        foreach (var edge in node.Edges.Where(e => e.FromId == node.Id))
        {
            var target = allNodes.FirstOrDefault(n => n.Id == edge.ToId);
            if (target is null || !seen.Add(target.Id))
            {
                continue;
            }

            result.Add(new AfterwardsNodeDto
            {
                Id = target.Id,
                PromptExcerpt = target.PromptExcerpt,
                StartedAt = target.StartedAt,
                Status = target.Status,
                Via = edge.Label ?? edge.Kind,
            });
        }

        foreach (var next in allNodes
                     .Where(n => n.StartedAt > node.StartedAt && n.Id != node.Id)
                     .OrderBy(n => n.StartedAt)
                     .Take(3))
        {
            if (!seen.Add(next.Id))
            {
                continue;
            }

            result.Add(new AfterwardsNodeDto
            {
                Id = next.Id,
                PromptExcerpt = next.PromptExcerpt,
                StartedAt = next.StartedAt,
                Status = next.Status,
                Via = "temporal",
            });
        }

        return result.Take(5).ToList();
    }

    public static TurnDto ToTurnPublic(ActivityTurn turn) => ToTurn(turn);

    private static TurnDto ToTurn(ActivityTurn turn) => new()
    {
        Prompt = turn.Prompt.Text ?? "",
        Plan = turn.PlanText,
        StartedAt = turn.StartedAt,
        GenerationId = turn.GenerationId,
        Actions = turn.Actions.Select(ToActivity).ToList(),
        Files = turn.Artifacts.Files.Select(f => f.Path).ToList(),
        Commits = turn.Artifacts.CommitShas,
    };

    private static ActivityDto ToActivity(ActivityItem item) => item switch
    {
        ActivityItem.Tool t => new ActivityDto
        {
            Kind = "tool",
            Summary = t.SummaryLine(),
            Timestamp = t.EndMeta?.Timestamp ?? t.StartMeta?.Timestamp,
            Name = t.Name,
            Phase = t.Phase.ToString(),
            DurationMs = t.DurationMs,
            ErrorMessage = t.ErrorMessage,
            Output = t.Output,
        },
        ActivityItem.File f => new ActivityDto
        {
            Kind = "file",
            Summary = f.SummaryLine(),
            Timestamp = f.Meta.Timestamp,
            Path = f.Path,
            Action = f.Action.ToString(),
            Edits = f.Edits,
        },
        ActivityItem.Command c => new ActivityDto
        {
            Kind = "command",
            Summary = c.SummaryLine(),
            Timestamp = c.EndMeta?.Timestamp ?? c.StartMeta?.Timestamp,
            Command = c.CommandText,
            Phase = c.Phase.ToString(),
            Output = c.Output,
            DurationMs = TryDurationMs(c.Duration),
        },
        ActivityItem.Git g => new ActivityDto
        {
            Kind = "git",
            Summary = g.SummaryLine(),
            Timestamp = g.EndMeta?.Timestamp ?? g.StartMeta?.Timestamp,
            Command = g.CommandText,
            Name = g.Subcommand,
            Phase = g.Phase.ToString(),
            Output = g.Output,
            DurationMs = TryDurationMs(g.Duration),
        },
        ActivityItem.Test t => new ActivityDto
        {
            Kind = "test",
            Summary = t.SummaryLine(),
            Timestamp = t.EndMeta?.Timestamp ?? t.StartMeta?.Timestamp,
            Name = t.Name ?? t.Suite,
            Status = t.Status,
            Phase = t.Phase.ToString(),
            Passed = t.Passed,
            Failed = t.Failed,
        },
        _ => new ActivityDto
        {
            Kind = "unknown",
            Summary = item.SummaryLine(),
        },
    };

    private static TestDto ToTest(ActivityItem.Test t) => new()
    {
        Name = t.Name,
        Suite = t.Suite,
        Status = t.Status,
        Passed = t.Passed,
        Failed = t.Failed,
        Phase = t.Phase.ToString(),
        StartedAt = t.StartMeta?.Timestamp,
        EndedAt = t.EndMeta?.Timestamp,
    };

    public static EdgeDto ToEdge(WorkNodeEdgeRecord e) => new()
    {
        From = e.FromId,
        To = e.ToId,
        Kind = e.Kind,
        Label = e.Label,
        Score = e.Score,
        Manual = e.Manual,
    };

    private static double? TryDurationMs(JsonNode? duration)
    {
        if (duration is null)
        {
            return null;
        }

        try
        {
            if (duration is JsonValue)
            {
                return duration.GetValue<double>();
            }

            if (duration is JsonObject obj)
            {
                foreach (var key in new[] { "ms", "duration_ms", "durationMs", "elapsed_ms" })
                {
                    if (obj[key] is JsonValue v)
                    {
                        return v.GetValue<double>();
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? ExtractSnippet(JsonNode? edits)
    {
        if (edits is null)
        {
            return null;
        }

        var parts = new List<string>();
        void Walk(JsonNode? node)
        {
            if (node is null)
            {
                return;
            }

            if (node is JsonValue val)
            {
                try
                {
                    var s = val.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(s) && (s.Contains('\n') || s.Length > 40))
                    {
                        parts.Add(s);
                    }
                }
                catch
                {
                    // ignore
                }

                return;
            }

            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "new_string", "newString", "new_text", "newText", "content", "text", "after" })
                {
                    if (obj[key] is JsonValue v)
                    {
                        try
                        {
                            var s = v.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                parts.Add(s);
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                foreach (var prop in obj)
                {
                    if (prop.Key is "new_string" or "newString" or "new_text" or "newText" or "content" or "text" or "after")
                    {
                        continue;
                    }

                    Walk(prop.Value);
                }

                return;
            }

            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    Walk(item);
                }
            }
        }

        Walk(edits);
        if (parts.Count == 0)
        {
            return null;
        }

        var joined = string.Join("\n\n", parts.Distinct());
        return joined.Length > 4000 ? joined[..4000] + "\n…" : joined;
    }

    // EditRangeExtractor is internal — mirror via public path using WorkNodeBuilder artifacts.
    private static IReadOnlyList<LineRange> EditRangeExtractorPublic(JsonNode? edits)
    {
        // Re-use ranges already projected onto FileArtifact when possible; for raw edits
        // duplicate minimal extraction locally.
        if (edits is null)
        {
            return [];
        }

        var ranges = new List<LineRange>();
        void TryOne(JsonNode? node)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            int? start = null;
            int? end = null;
            foreach (var name in new[] { "start_line", "startLine", "old_start", "oldStart", "new_start", "newStart" })
            {
                if (obj[name] is JsonValue v && TryInt(v, out var n))
                {
                    start = n;
                    break;
                }
            }

            foreach (var name in new[] { "end_line", "endLine", "old_end", "oldEnd", "new_end", "newEnd" })
            {
                if (obj[name] is JsonValue v && TryInt(v, out var n))
                {
                    end = n;
                    break;
                }
            }

            if (start is not null && end is not null)
            {
                var s = start.Value;
                var e = end.Value;
                if (e < s)
                {
                    (s, e) = (e, s);
                }

                ranges.Add(new LineRange { StartLine = s, EndLine = e });
            }
        }

        if (edits is JsonArray arr)
        {
            foreach (var item in arr)
            {
                TryOne(item);
            }
        }
        else
        {
            TryOne(edits);
        }

        return ranges;
    }

    private static bool TryInt(JsonValue v, out int value)
    {
        try
        {
            value = v.GetValue<int>();
            return true;
        }
        catch
        {
            return int.TryParse(v.ToString(), out value);
        }
    }
}
