using Worktree.Events;

namespace Worktree.Activities;

public static class ActivityReconstructor
{
    public static SessionTimeline Reconstruct(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<CommitLink>? commits = null)
    {
        var ordered = events
            .OrderBy(e => e.Sequence ?? 0UL)
            .ThenBy(e => e.Timestamp)
            .ToList();

        var sessionId = ordered.FirstOrDefault()?.SessionId
            ?? commits?.FirstOrDefault()?.SessionId
            ?? "";
        var agent = ordered.FirstOrDefault()?.Agent ?? "";

        var timeline = new SessionTimeline
        {
            SessionId = sessionId,
            Agent = agent,
        };

        if (commits is not null)
        {
            timeline.Commits.AddRange(commits);
        }

        var turns = new List<TurnBuilder>();
        TurnBuilder? current = null;
        TurnBuilder? preamble = null;
        var byGeneration = new Dictionary<string, TurnBuilder>(StringComparer.Ordinal);

        foreach (var event_ in ordered)
        {
            var normalized = Normalize.NormalizeEvent(event_);
            if (normalized is null)
            {
                continue;
            }

            switch (normalized)
            {
                case NormalizedEvent.Session:
                    // Session lifecycle is not part of a turn.
                    break;

                case NormalizedEvent.Prompt prompt:
                {
                    var genId = prompt.Value.Meta.Context.GenerationId;
                    if (genId is not null
                        && byGeneration.TryGetValue(genId, out var existing)
                        && ReferenceEquals(existing, current))
                    {
                        // Same generation continuing — keep current turn (rare second prompt).
                        current.Prompts.Add(prompt.Value);
                    }
                    else
                    {
                        current = new TurnBuilder(prompt.Value);
                        turns.Add(current);
                        if (genId is not null)
                        {
                            byGeneration[genId] = current;
                        }
                    }

                    break;
                }

                default:
                {
                    var target = ResolveTurn(normalized, current, byGeneration);
                    if (target is null)
                    {
                        // Orphan activity before any prompt — keep in a synthetic preamble turn.
                        preamble ??= new TurnBuilder(PromptNorm.Preamble(sessionId, agent, event_.Timestamp));
                        if (!turns.Contains(preamble))
                        {
                            turns.Insert(0, preamble);
                        }

                        preamble.AddNormalized(normalized);
                        break;
                    }

                    target.AddNormalized(normalized);
                    break;
                }
            }
        }

        foreach (var builder in turns)
        {
            timeline.Turns.Add(builder.Build(sessionId));
        }

        AttributeCommits(timeline);
        return timeline;
    }

    private static TurnBuilder? ResolveTurn(
        NormalizedEvent normalized,
        TurnBuilder? current,
        Dictionary<string, TurnBuilder> byGeneration)
    {
        var genId = GetGenerationId(normalized);
        if (genId is not null && byGeneration.TryGetValue(genId, out var byGen))
        {
            return byGen;
        }

        return current;
    }

    private static string? GetGenerationId(NormalizedEvent normalized) => normalized switch
    {
        NormalizedEvent.Prompt p => p.Value.Meta.Context.GenerationId,
        NormalizedEvent.ToolCall t => t.Value.Meta.Context.GenerationId,
        NormalizedEvent.FileChange f => f.Value.Meta.Context.GenerationId,
        NormalizedEvent.CommandExecution c => c.Value.Meta.Context.GenerationId,
        NormalizedEvent.TestResult t => t.Value.Meta.Context.GenerationId,
        NormalizedEvent.AgentResponse a => a.Value.Meta.Context.GenerationId,
        NormalizedEvent.GitOperation g => g.Value.Meta.Context.GenerationId,
        NormalizedEvent.Session s => s.Value.Meta?.Context.GenerationId,
        _ => null,
    };

    private static void AttributeCommits(SessionTimeline timeline)
    {
        foreach (var commit in timeline.Commits)
        {
            ActivityTurn? turn = null;
            if (commit.PromptEventId is not null)
            {
                turn = timeline.Turns.FirstOrDefault(t =>
                    t.Prompt.Meta.EventId == commit.PromptEventId);
            }

            turn ??= timeline.Turns
                .Where(t => t.StartedAt <= commit.CapturedAt)
                .OrderByDescending(t => t.StartedAt)
                .FirstOrDefault();

            if (turn is not null && !turn.Artifacts.CommitShas.Contains(commit.Sha, StringComparer.OrdinalIgnoreCase))
            {
                turn.Artifacts.CommitShas.Add(commit.Sha);
            }
        }
    }

    private sealed class TurnBuilder
    {
        public List<PromptNorm> Prompts { get; } = [];
        private readonly List<PendingTool> _openToolsById = [];
        private readonly Queue<PendingTool> _openToolsFifo = new();
        private readonly Queue<PendingCommand> _openCommands = new();
        private readonly Queue<PendingGit> _openGits = new();
        private readonly Queue<PendingTest> _openTests = new();
        private readonly List<ActivityItem> _actions = [];
        private readonly List<AgentResponseNorm> _responses = [];
        private readonly TurnArtifacts _artifacts = new();

        public TurnBuilder(PromptNorm prompt) => Prompts.Add(prompt);

        public void AddNormalized(NormalizedEvent normalized)
        {
            switch (normalized)
            {
                case NormalizedEvent.ToolCall tool:
                    HandleTool(tool.Value);
                    break;
                case NormalizedEvent.FileChange file:
                    HandleFile(file.Value);
                    break;
                case NormalizedEvent.CommandExecution cmd:
                    HandleCommand(cmd.Value);
                    break;
                case NormalizedEvent.GitOperation git:
                    HandleGit(git.Value);
                    break;
                case NormalizedEvent.TestResult test:
                    HandleTest(test.Value);
                    break;
                case NormalizedEvent.AgentResponse response:
                    _responses.Add(response.Value);
                    break;
                case NormalizedEvent.Prompt prompt:
                    Prompts.Add(prompt.Value);
                    break;
            }
        }

        public ActivityTurn Build(string sessionId)
        {
            // Flush unpaired starts as incomplete activities.
            while (_openToolsFifo.Count > 0)
            {
                var p = _openToolsFifo.Dequeue();
                _actions.Add(p.ToActivity(ToolPhase.Started));
            }

            foreach (var p in _openToolsById)
            {
                _actions.Add(p.ToActivity(ToolPhase.Started));
            }

            while (_openCommands.Count > 0)
            {
                var p = _openCommands.Dequeue();
                _actions.Add(p.ToActivity(CommandPhase.Started));
            }

            while (_openGits.Count > 0)
            {
                var p = _openGits.Dequeue();
                _actions.Add(p.ToActivity(CommandPhase.Started));
            }

            while (_openTests.Count > 0)
            {
                var p = _openTests.Dequeue();
                _actions.Add(p.ToActivity(TestPhase.Started));
            }

            var prompt = Prompts[0];
            var turn = new ActivityTurn
            {
                SessionId = sessionId,
                Prompt = prompt,
                GenerationId = prompt.Meta.Context.GenerationId,
            };
            turn.Actions.AddRange(_actions);
            turn.Responses.AddRange(_responses);
            turn.Artifacts.CommitShas.AddRange(_artifacts.CommitShas);
            turn.Artifacts.Files.AddRange(_artifacts.Files);
            return turn;
        }

        private void HandleTool(ToolCallNorm tool)
        {
            if (tool.Phase == ToolPhase.Started)
            {
                var pending = new PendingTool(tool);
                if (!string.IsNullOrEmpty(tool.ToolUseId))
                {
                    _openToolsById.Add(pending);
                }
                else
                {
                    _openToolsFifo.Enqueue(pending);
                }

                return;
            }

            PendingTool? match = null;
            if (!string.IsNullOrEmpty(tool.ToolUseId))
            {
                match = _openToolsById.FirstOrDefault(p =>
                    string.Equals(p.Start.ToolUseId, tool.ToolUseId, StringComparison.Ordinal));
                if (match is not null)
                {
                    _openToolsById.Remove(match);
                }
            }

            if (match is null && _openToolsFifo.Count > 0)
            {
                // FIFO by name when possible
                var queue = _openToolsFifo.ToList();
                var idx = queue.FindIndex(p =>
                    string.Equals(p.Start.Name, tool.Name, StringComparison.Ordinal));
                if (idx < 0 && string.IsNullOrEmpty(tool.ToolUseId))
                {
                    idx = 0;
                }

                if (idx >= 0)
                {
                    match = queue[idx];
                    queue.RemoveAt(idx);
                    _openToolsFifo.Clear();
                    foreach (var item in queue)
                    {
                        _openToolsFifo.Enqueue(item);
                    }
                }
            }

            if (match is null)
            {
                // Completion without start — emit as standalone.
                _actions.Add(new ActivityItem.Tool(
                    tool.Name,
                    tool.ToolUseId,
                    tool.Phase,
                    tool.Input,
                    tool.Output,
                    tool.DurationMs,
                    tool.ErrorMessage,
                    startMeta: null,
                    endMeta: tool.Meta));
                return;
            }

            _actions.Add(new ActivityItem.Tool(
                tool.Name ?? match.Start.Name,
                tool.ToolUseId ?? match.Start.ToolUseId,
                tool.Phase,
                match.Start.Input ?? tool.Input,
                tool.Output,
                tool.DurationMs ?? match.Start.DurationMs,
                tool.ErrorMessage ?? match.Start.ErrorMessage,
                match.Start.Meta,
                tool.Meta));
        }

        private void HandleFile(FileChangeNorm file)
        {
            _actions.Add(new ActivityItem.File(
                file.Action,
                file.Path,
                file.Edits,
                file.ContentOmitted,
                file.Meta));

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return;
            }

            var ranges = EditRangeExtractor.Extract(file.Edits);
            _artifacts.Files.Add(new FileArtifact
            {
                Path = file.Path,
                Action = file.Action,
                Ranges = ranges,
            });
        }

        private void HandleCommand(CommandExecutionNorm cmd)
        {
            if (cmd.Phase == CommandPhase.Started)
            {
                _openCommands.Enqueue(new PendingCommand(cmd));
                return;
            }

            PendingCommand? match = null;
            if (_openCommands.Count > 0)
            {
                var queue = _openCommands.ToList();
                var idx = queue.FindIndex(p =>
                    string.Equals(p.Start.Command, cmd.Command, StringComparison.Ordinal));
                if (idx < 0)
                {
                    idx = 0;
                }

                match = queue[idx];
                queue.RemoveAt(idx);
                _openCommands.Clear();
                foreach (var item in queue)
                {
                    _openCommands.Enqueue(item);
                }
            }

            if (match is null)
            {
                _actions.Add(new ActivityItem.Command(
                    cmd.Command, cmd.Cwd, cmd.Phase, cmd.Output, cmd.Duration, null, cmd.Meta));
                return;
            }

            _actions.Add(new ActivityItem.Command(
                cmd.Command ?? match.Start.Command,
                cmd.Cwd ?? match.Start.Cwd,
                cmd.Phase,
                cmd.Output ?? match.Start.Output,
                cmd.Duration ?? match.Start.Duration,
                match.Start.Meta,
                cmd.Meta));
        }

        private void HandleGit(GitOperationNorm git)
        {
            if (git.Phase == CommandPhase.Started)
            {
                _openGits.Enqueue(new PendingGit(git));
                return;
            }

            PendingGit? match = null;
            if (_openGits.Count > 0)
            {
                var queue = _openGits.ToList();
                var idx = queue.FindIndex(p =>
                    string.Equals(p.Start.Command, git.Command, StringComparison.Ordinal)
                    || string.Equals(p.Start.Subcommand, git.Subcommand, StringComparison.Ordinal));
                if (idx < 0)
                {
                    idx = 0;
                }

                match = queue[idx];
                queue.RemoveAt(idx);
                _openGits.Clear();
                foreach (var item in queue)
                {
                    _openGits.Enqueue(item);
                }
            }

            if (match is null)
            {
                _actions.Add(new ActivityItem.Git(
                    git.Command, git.Subcommand, git.Args, git.Cwd, git.Phase,
                    git.Output, git.Duration, null, git.Meta));
                return;
            }

            _actions.Add(new ActivityItem.Git(
                git.Command ?? match.Start.Command,
                git.Subcommand ?? match.Start.Subcommand,
                git.Args.Count > 0 ? git.Args : match.Start.Args,
                git.Cwd ?? match.Start.Cwd,
                git.Phase,
                git.Output ?? match.Start.Output,
                git.Duration ?? match.Start.Duration,
                match.Start.Meta,
                git.Meta));
        }

        private void HandleTest(TestResultNorm test)
        {
            if (test.Phase == TestPhase.Started)
            {
                _openTests.Enqueue(new PendingTest(test));
                return;
            }

            PendingTest? match = null;
            if (_openTests.Count > 0)
            {
                var queue = _openTests.ToList();
                var idx = queue.FindIndex(p =>
                    string.Equals(p.Start.Name, test.Name, StringComparison.Ordinal)
                    || string.Equals(p.Start.Suite, test.Suite, StringComparison.Ordinal));
                if (idx < 0)
                {
                    idx = 0;
                }

                match = queue[idx];
                queue.RemoveAt(idx);
                _openTests.Clear();
                foreach (var item in queue)
                {
                    _openTests.Enqueue(item);
                }
            }

            if (match is null)
            {
                _actions.Add(new ActivityItem.Test(
                    test.Name, test.Suite, test.Status, test.Passed, test.Failed,
                    test.Phase, null, test.Meta));
                return;
            }

            _actions.Add(new ActivityItem.Test(
                test.Name ?? match.Start.Name,
                test.Suite ?? match.Start.Suite,
                test.Status ?? match.Start.Status,
                test.Passed ?? match.Start.Passed,
                test.Failed ?? match.Start.Failed,
                test.Phase,
                match.Start.Meta,
                test.Meta));
        }

        private sealed class PendingTool(ToolCallNorm start)
        {
            public ToolCallNorm Start { get; } = start;

            public ActivityItem.Tool ToActivity(ToolPhase phase) => new(
                Start.Name, Start.ToolUseId, phase, Start.Input, Start.Output,
                Start.DurationMs, Start.ErrorMessage, Start.Meta, null);
        }

        private sealed class PendingCommand(CommandExecutionNorm start)
        {
            public CommandExecutionNorm Start { get; } = start;

            public ActivityItem.Command ToActivity(CommandPhase phase) => new(
                Start.Command, Start.Cwd, phase, Start.Output, Start.Duration, Start.Meta, null);
        }

        private sealed class PendingGit(GitOperationNorm start)
        {
            public GitOperationNorm Start { get; } = start;

            public ActivityItem.Git ToActivity(CommandPhase phase) => new(
                Start.Command, Start.Subcommand, Start.Args, Start.Cwd, phase,
                Start.Output, Start.Duration, Start.Meta, null);
        }

        private sealed class PendingTest(TestResultNorm start)
        {
            public TestResultNorm Start { get; } = start;

            public ActivityItem.Test ToActivity(TestPhase phase) => new(
                Start.Name, Start.Suite, Start.Status, Start.Passed, Start.Failed,
                phase, Start.Meta, null);
        }
    }
}
