namespace Worktree.Activities;

public static class ProvenanceQuery
{
    public static ProvenanceResult Trace(
        ProvenanceSubject subject,
        IReadOnlyList<SessionTimeline> timelines)
    {
        var hits = subject switch
        {
            ProvenanceSubject.Commit commit => TraceCommit(commit.Sha, timelines),
            ProvenanceSubject.FilePath file => TraceFile(file.Path, timelines, lineRange: null),
            ProvenanceSubject.CodeRange code => TraceFile(code.Path, timelines, (code.StartLine, code.EndLine)),
            _ => [],
        };

        return new ProvenanceResult
        {
            Subject = subject,
            Hits = hits,
        };
    }

    private static List<ProvenanceHit> TraceCommit(string sha, IReadOnlyList<SessionTimeline> timelines)
    {
        var hits = new List<ProvenanceHit>();
        foreach (var timeline in timelines)
        {
            var matchingLinks = timeline.Commits
                .Where(c => ShaEquals(c.Sha, sha))
                .ToList();

            if (matchingLinks.Count == 0)
            {
                // Also check artifact index in case commits were attributed on turns only.
                foreach (var turn in timeline.Turns)
                {
                    if (turn.Artifacts.CommitShas.Any(s => ShaEquals(s, sha)))
                    {
                        hits.Add(new ProvenanceHit
                        {
                            Turn = turn,
                            MatchKind = ProvenanceMatchKind.ExactCommit,
                            SessionId = timeline.SessionId,
                        });
                    }
                }

                continue;
            }

            foreach (var link in matchingLinks)
            {
                ActivityTurn? turn = null;
                if (link.PromptEventId is not null)
                {
                    turn = timeline.Turns.FirstOrDefault(t =>
                        t.Prompt.Meta.EventId == link.PromptEventId);
                }

                turn ??= timeline.Turns
                    .Where(t => t.StartedAt <= link.CapturedAt)
                    .OrderByDescending(t => t.StartedAt)
                    .FirstOrDefault();

                turn ??= timeline.Turns.LastOrDefault();
                if (turn is null)
                {
                    continue;
                }

                hits.Add(new ProvenanceHit
                {
                    Turn = turn,
                    MatchKind = ProvenanceMatchKind.ExactCommit,
                    SessionId = timeline.SessionId,
                });
            }
        }

        return Deduplicate(hits);
    }

    private static List<ProvenanceHit> TraceFile(
        string path,
        IReadOnlyList<SessionTimeline> timelines,
        (int Start, int End)? lineRange)
    {
        var hits = new List<ProvenanceHit>();
        foreach (var timeline in timelines)
        {
            foreach (var turn in timeline.Turns)
            {
                var files = turn.Artifacts.Files
                    .Where(f => PathNormalize.EqualsPath(f.Path, path) || PathNormalize.EndsWithPath(f.Path, path))
                    .ToList();

                if (files.Count == 0)
                {
                    continue;
                }

                if (lineRange is null)
                {
                    hits.Add(new ProvenanceHit
                    {
                        Turn = turn,
                        MatchKind = ProvenanceMatchKind.FileTouch,
                        SessionId = timeline.SessionId,
                    });
                    continue;
                }

                var (start, end) = lineRange.Value;
                if (end < start)
                {
                    (start, end) = (end, start);
                }

                var anyRanges = files.Any(f => f.Ranges.Count > 0);
                if (!anyRanges)
                {
                    hits.Add(new ProvenanceHit
                    {
                        Turn = turn,
                        MatchKind = ProvenanceMatchKind.DegradedToFile,
                        SessionId = timeline.SessionId,
                    });
                    continue;
                }

                var overlaps = files.Any(f => f.Ranges.Any(r => r.Overlaps(start, end)));
                if (overlaps)
                {
                    hits.Add(new ProvenanceHit
                    {
                        Turn = turn,
                        MatchKind = ProvenanceMatchKind.EditOverlap,
                        SessionId = timeline.SessionId,
                    });
                }
            }
        }

        return Deduplicate(hits);
    }

    private static List<ProvenanceHit> Deduplicate(List<ProvenanceHit> hits)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProvenanceHit>();
        foreach (var hit in hits.OrderBy(h => h.Turn.StartedAt))
        {
            var key = $"{hit.SessionId}:{hit.Turn.Prompt.Meta.EventId}:{hit.MatchKind}";
            if (seen.Add(key))
            {
                result.Add(hit);
            }
        }

        return result;
    }

    private static bool ShaEquals(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow short SHA prefix match (7+ chars).
        if (a.Length >= 7 && b.Length >= 7)
        {
            var shorter = a.Length <= b.Length ? a : b;
            var longer = a.Length <= b.Length ? b : a;
            return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
