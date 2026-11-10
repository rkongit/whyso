using System.Text;
using System.Text.RegularExpressions;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Activities;

public sealed class ContextBriefOptions
{
    /// <summary>How many recent sessions to scan.</summary>
    public int MaxSessions { get; init; } = 10;

    /// <summary>Hard cap on the brief's length in characters.</summary>
    public int MaxChars { get; init; } = 4000;

    /// <summary>Session to leave out (usually the one that is starting).</summary>
    public string? ExcludeSessionId { get; init; }
}

public sealed class ContextBriefSession
{
    public required string SessionId { get; init; }
    public required string Agent { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? FirstPrompt { get; init; }
    public IReadOnlyList<string> Decisions { get; init; } = [];
    public IReadOnlyList<string> Rejections { get; init; } = [];
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> Commits { get; init; } = [];
}

/// <summary>
/// Builds a short brief of past decisions and rejected approaches for a new agent session.
/// Heuristic and local only (no LLM): sentences from prompts and agent responses that contain
/// decision or rejection phrases, plus each session's files and commits.
/// </summary>
public static class ContextBrief
{
    private static readonly Regex Rejection = new(
        @"\b(don't|do not|never|instead of|rather than|revert(ed|ing)?|reject(ed)?|avoid|stop using|undo|roll ?back|doesn't work|didn't work|not use)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Decision = new(
        @"\b(decided|decision|we('ll| will) use|let's use|going with|go with|chose|choose|switch(ed)? to|prefer|settled on|use \S+ instead)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+|\r?\n+", RegexOptions.Compiled);

    private const int MaxPerKind = 3;
    private const int MaxSentenceChars = 220;

    public static async Task<IReadOnlyList<ContextBriefSession>> CollectAsync(Store store, ContextBriefOptions? options = null)
    {
        options ??= new ContextBriefOptions();
        var sessions = (await store.ListSessionsAsync())
            .Where(s => s.Id != options.ExcludeSessionId)
            .OrderByDescending(s => s.StartedAt)
            .Take(options.MaxSessions)
            .ToList();

        var result = new List<ContextBriefSession>();
        foreach (var session in sessions)
        {
            var events = await store.ListEventsAsync(session.Id);
            var prompts = events
                .Where(e => e.EventType == EventType.PromptSubmitted)
                .Select(e => e.Payload["prompt"]?.GetValue<string>())
                .OfType<string>()
                .ToList();
            var responses = events
                .Where(e => e.EventType == EventType.AgentResponse)
                .Select(e => e.Payload["text"]?.GetValue<string>())
                .OfType<string>()
                .ToList();

            var decisions = new List<string>();
            var rejections = new List<string>();
            foreach (var sentence in prompts.Concat(responses).SelectMany(SplitSentences))
            {
                if (Rejection.IsMatch(sentence))
                {
                    AddDistinct(rejections, sentence);
                }
                else if (Decision.IsMatch(sentence))
                {
                    AddDistinct(decisions, sentence);
                }
            }

            var files = (await store.ListWorkNodeFilesAsync(workNodeId: session.Id))
                .Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var commits = (await store.ListSessionCommitsAsync(session.Id))
                .Select(c => c.Sha.Length > 7 ? c.Sha[..7] : c.Sha)
                .Distinct()
                .ToList();

            if (prompts.Count == 0 && decisions.Count == 0 && rejections.Count == 0)
            {
                continue; // nothing readable (e.g. forgotten session)
            }

            result.Add(new ContextBriefSession
            {
                SessionId = session.Id,
                Agent = session.Agent,
                StartedAt = session.StartedAt,
                Labels = (await store.ListWorkNodeLabelsAsync(session.Id)).Select(l => l.Label).ToList(),
                FirstPrompt = prompts.Count > 0 ? Clip(prompts[0]) : null,
                Decisions = decisions,
                Rejections = rejections,
                Files = files,
                Commits = commits,
            });
        }

        return result;
    }

    public static async Task<string> BuildMarkdownAsync(Store store, ContextBriefOptions? options = null)
    {
        options ??= new ContextBriefOptions();
        var sessions = await CollectAsync(store, options);
        return ToMarkdown(sessions, options.MaxChars);
    }

    public static string ToMarkdown(IReadOnlyList<ContextBriefSession> sessions, int maxChars)
    {
        if (sessions.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Past AI sessions in this repo (from Whyso)");
        sb.AppendLine("Decisions and rejected approaches from earlier sessions, newest first. Extracted heuristically from local history: verify before relying on it, and don't retry approaches listed as rejected without a reason.");
        foreach (var s in sessions)
        {
            var block = new StringBuilder();
            block.AppendLine();
            var labels = s.Labels.Count > 0 ? $" · {string.Join(", ", s.Labels)}" : "";
            block.AppendLine($"## {s.StartedAt:yyyy-MM-dd} · {s.Agent} · {s.SessionId}{labels}");
            if (s.FirstPrompt is not null)
            {
                block.AppendLine($"- Asked: {s.FirstPrompt}");
            }

            foreach (var d in s.Decisions)
            {
                block.AppendLine($"- Decided: {d}");
            }

            foreach (var r in s.Rejections)
            {
                block.AppendLine($"- Rejected / avoid: {r}");
            }

            if (s.Files.Count > 0)
            {
                block.AppendLine($"- Files: {string.Join(", ", s.Files)}");
            }

            if (s.Commits.Count > 0)
            {
                block.AppendLine($"- Commits: {string.Join(", ", s.Commits)}");
            }

            if (sb.Length + block.Length > maxChars)
            {
                break;
            }

            sb.Append(block);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        SentenceSplit.Split(text)
            .Select(s => s.Trim().TrimStart('-', '*', '#', ' '))
            .Where(s => s.Length >= 12)
            .Select(Clip);

    private static void AddDistinct(List<string> list, string sentence)
    {
        if (list.Count < MaxPerKind && !list.Contains(sentence, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(sentence);
        }
    }

    private static string Clip(string s)
    {
        var flat = Regex.Replace(s, @"\s+", " ").Trim();
        return flat.Length <= MaxSentenceChars ? flat : flat[..(MaxSentenceChars - 1)] + "…";
    }
}
