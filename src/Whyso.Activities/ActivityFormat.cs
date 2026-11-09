using Whyso.Events;

namespace Whyso.Activities;

/// <summary>Human-readable formatting for reconstructed activities and provenance hits.</summary>
public static class ActivityFormat
{
    public static void WriteTimeline(TextWriter writer, SessionTimeline timeline)
    {
        writer.WriteLine($"Session {timeline.SessionId} ({timeline.Agent})");
        if (timeline.Turns.Count == 0)
        {
            writer.WriteLine("  (no prompt turns)");
            return;
        }

        for (var i = 0; i < timeline.Turns.Count; i++)
        {
            writer.WriteLine();
            WriteTurn(writer, timeline.Turns[i], heading: $"Turn {i + 1}");
        }
    }

    public static void WriteProvenance(TextWriter writer, ProvenanceResult result)
    {
        writer.WriteLine(SubjectLabel(result.Subject));
        if (result.Hits.Count == 0)
        {
            writer.WriteLine("No matching prompt turns found.");
            return;
        }

        for (var i = 0; i < result.Hits.Count; i++)
        {
            var hit = result.Hits[i];
            writer.WriteLine();
            writer.WriteLine(
                $"--- match {i + 1}/{result.Hits.Count} ({hit.MatchKind}) session={hit.SessionId}");
            WriteTurn(writer, hit.Turn, heading: null);
        }
    }

    public static void WriteTurn(TextWriter writer, ActivityTurn turn, string? heading)
    {
        if (heading is not null)
        {
            writer.WriteLine(heading);
        }

        writer.WriteLine($"Prompt: {Truncate(turn.Prompt.Text ?? "", 400)}");
        if (!string.IsNullOrWhiteSpace(turn.PlanText))
        {
            writer.WriteLine($"Plan: {Truncate(turn.PlanText!, 2000)}");
        }

        if (turn.Actions.Count > 0)
        {
            writer.WriteLine("Actions:");
            foreach (var action in turn.Actions)
            {
                writer.WriteLine($"  - {action.SummaryLine()}");
            }
        }
        else
        {
            writer.WriteLine("Actions: (none)");
        }

        if (turn.Artifacts.CommitShas.Count > 0)
        {
            writer.WriteLine(
                "Commits: " + string.Join(", ", turn.Artifacts.CommitShas.Select(ShortSha)));
        }

        if (turn.Artifacts.Files.Count > 0)
        {
            var paths = turn.Artifacts.Files
                .Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            writer.WriteLine("Files: " + string.Join(", ", paths));
        }
    }

    private static string SubjectLabel(ProvenanceSubject subject) => subject switch
    {
        ProvenanceSubject.Commit c => $"Why commit {c.Sha}",
        ProvenanceSubject.FilePath f => $"Why file {f.Path}",
        ProvenanceSubject.CodeRange r => $"Why {r.Path}:{r.StartLine}-{r.EndLine}",
        _ => "Why",
    };

    private static string ShortSha(string sha) =>
        sha.Length <= 7 ? sha : sha[..7];

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}
