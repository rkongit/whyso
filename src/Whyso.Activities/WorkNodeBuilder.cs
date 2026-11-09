using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Activities;

public static class WorkNodeBuilder
{
    public static WorkNode Build(
        SessionRecord session,
        SessionTimeline timeline,
        IReadOnlyList<string>? labels = null,
        IReadOnlyList<WorkNodeEdgeRecord>? edges = null)
    {
        var prompts = new List<string>();
        var responses = new List<string>();
        var files = new List<FileArtifact>();
        var commands = new List<string>();
        var tests = new List<ActivityItem.Test>();
        var commits = new List<string>();

        foreach (var turn in timeline.Turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.Prompt.Text))
            {
                prompts.Add(turn.Prompt.Text!);
            }

            if (!string.IsNullOrWhiteSpace(turn.PlanText))
            {
                responses.Add(turn.PlanText!);
            }

            foreach (var sha in turn.Artifacts.CommitShas)
            {
                if (!commits.Contains(sha, StringComparer.OrdinalIgnoreCase))
                {
                    commits.Add(sha);
                }
            }

            foreach (var file in turn.Artifacts.Files)
            {
                if (file.Action is FileAction.Created or FileAction.Modified or FileAction.Deleted)
                {
                    files.Add(file);
                }
            }

            foreach (var action in turn.Actions)
            {
                switch (action)
                {
                    case ActivityItem.Command c when !string.IsNullOrWhiteSpace(c.CommandText):
                        commands.Add(c.CommandText!);
                        break;
                    case ActivityItem.Git g when !string.IsNullOrWhiteSpace(g.CommandText):
                        commands.Add(g.CommandText!);
                        break;
                    case ActivityItem.Test t:
                        tests.Add(t);
                        break;
                }
            }
        }

        foreach (var link in timeline.Commits)
        {
            if (!commits.Contains(link.Sha, StringComparer.OrdinalIgnoreCase))
            {
                commits.Add(link.Sha);
            }
        }

        return new WorkNode
        {
            Id = session.Id,
            SessionId = session.Id,
            ProjectId = session.ProjectId,
            Agent = session.Agent,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            GitHead = session.GitHead,
            GitHeadAfter = session.GitHeadAfter ?? timeline.Commits.LastOrDefault()?.Sha,
            Status = session.Status.AsString(),
            Timeline = timeline,
            Prompts = prompts,
            Responses = responses,
            FilesChanged = files,
            Commands = commands,
            Tests = tests,
            CommitShas = commits,
            Labels = labels ?? [],
            Edges = edges ?? [],
        };
    }

    public static WorkNodeProjection ToProjection(WorkNode node)
    {
        var excerpt = node.Prompts.FirstOrDefault() ?? "";
        if (excerpt.Length > 160)
        {
            excerpt = excerpt[..160] + "…";
        }

        var fileMap = new Dictionary<(string Path, string Action), WorkNodeFileRecord>();
        foreach (var turn in node.Timeline.Turns)
        {
            foreach (var file in turn.Artifacts.Files)
            {
                if (file.Action is not (FileAction.Created or FileAction.Modified or FileAction.Deleted))
                {
                    continue;
                }

                var path = PathNormalize.Normalize(file.Path);
                if (path.Length == 0)
                {
                    continue;
                }

                var action = FileActionLabel(file.Action);
                var key = (path, action);
                var ts = turn.StartedAt;
                if (fileMap.TryGetValue(key, out var existing))
                {
                    fileMap[key] = new WorkNodeFileRecord
                    {
                        WorkNodeId = node.Id,
                        Path = path,
                        Action = action,
                        FirstSeen = existing.FirstSeen < ts ? existing.FirstSeen : ts,
                        LastSeen = existing.LastSeen > ts ? existing.LastSeen : ts,
                    };
                }
                else
                {
                    fileMap[key] = new WorkNodeFileRecord
                    {
                        WorkNodeId = node.Id,
                        Path = path,
                        Action = action,
                        FirstSeen = ts,
                        LastSeen = ts,
                    };
                }
            }
        }

        var commandRows = new List<WorkNodeCommandRecord>();
        var testRows = new List<WorkNodeTestRecord>();
        foreach (var turn in node.Timeline.Turns)
        {
            foreach (var action in turn.Actions)
            {
                switch (action)
                {
                    case ActivityItem.Command c when !string.IsNullOrWhiteSpace(c.CommandText):
                        commandRows.Add(new WorkNodeCommandRecord
                        {
                            WorkNodeId = node.Id,
                            CommandText = c.CommandText!,
                            SeenAt = c.EndMeta?.Timestamp ?? c.StartMeta?.Timestamp ?? turn.StartedAt,
                        });
                        break;
                    case ActivityItem.Git g when !string.IsNullOrWhiteSpace(g.CommandText):
                        commandRows.Add(new WorkNodeCommandRecord
                        {
                            WorkNodeId = node.Id,
                            CommandText = g.CommandText!,
                            SeenAt = g.EndMeta?.Timestamp ?? g.StartMeta?.Timestamp ?? turn.StartedAt,
                        });
                        break;
                    case ActivityItem.Test t:
                        testRows.Add(new WorkNodeTestRecord
                        {
                            WorkNodeId = node.Id,
                            Name = t.Name,
                            Suite = t.Suite,
                            Status = t.Status,
                            SeenAt = t.EndMeta?.Timestamp ?? t.StartMeta?.Timestamp ?? turn.StartedAt,
                        });
                        break;
                }
            }
        }

        var pathsText = string.Join(" ", fileMap.Keys.Select(k => k.Path).Distinct(StringComparer.OrdinalIgnoreCase));
        var commandsText = string.Join("\n", commandRows.Select(c => c.CommandText));
        var testsText = string.Join(
            "\n",
            testRows.Select(t => string.Join(
                " ",
                new[] { t.Name, t.Suite, t.Status }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        var commitsText = string.Join(" ", node.CommitShas);
        var promptText = string.Join("\n\n", node.Prompts);
        var planText = string.Join("\n\n", node.Responses);

        return new WorkNodeProjection
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
                Status = node.Status,
                PromptExcerpt = excerpt,
                TurnCount = node.Timeline.Turns.Count,
                FileCount = fileMap.Count,
                CommandCount = commandRows.Count,
                TestCount = testRows.Count,
                CommitCount = node.CommitShas.Count,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            Files = fileMap.Values.ToList(),
            Commands = commandRows,
            Tests = testRows,
            Fts = new WorkNodeFtsDocument
            {
                WorkNodeId = node.Id,
                Prompt = promptText,
                Plan = planText,
                Paths = pathsText,
                Commands = commandsText,
                Tests = testsText,
                Commits = commitsText,
            },
        };
    }

    public static string FileActionLabel(FileAction action) => action switch
    {
        FileAction.Created => "created",
        FileAction.Modified => "modified",
        FileAction.Deleted => "deleted",
        FileAction.Read => "read",
        _ => "file",
    };
}
