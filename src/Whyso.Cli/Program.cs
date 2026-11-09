using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Whyso.Activities;
using Whyso.Adapter.Cursor;
using Whyso.Daemon;
using Whyso.Events;
using Whyso.Storage;
using Whyso.Ui;

namespace Whyso.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Keep agent response text (em dashes, etc.) intact on Windows consoles.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            b.SetMinimumLevel(LogLevel.Information);
        });

        var root = new RootCommand("Whyso — provenance capture for AI-assisted development");

        root.AddCommand(BuildInit());
        root.AddCommand(BuildDaemon(loggerFactory));
        root.AddCommand(BuildStatus());
        root.AddCommand(BuildSessions());
        root.AddCommand(BuildSession());
        root.AddCommand(BuildEvents());
        root.AddCommand(BuildActivity());
        root.AddCommand(BuildTree());
        root.AddCommand(BuildLink());
        root.AddCommand(BuildUnlink());
        root.AddCommand(BuildLabel());
        root.AddCommand(BuildSearch());
        root.AddCommand(BuildRebuild());
        root.AddCommand(BuildWhy());
        root.AddCommand(BuildUi());
        root.AddCommand(BuildIntegrate());
        root.AddCommand(BuildHook());

        return await root.InvokeAsync(args);
    }

    private static Command BuildUi()
    {
        var portOpt = new Option<int>("--port", () => 9478, "Port for the explorer UI (default 9478)");
        var bindOpt = new Option<string?>("--bind", "Bind address host:port (overrides --port)");
        var openOpt = new Option<bool>("--open", () => true, "Open the browser after starting");
        var cmd = new Command("ui", "Start the Whyso explorer UI on localhost")
        {
            portOpt,
            bindOpt,
            openOpt,
        };
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            try
            {
                var port = ctx.ParseResult.GetValueForOption(portOpt);
                var bind = ctx.ParseResult.GetValueForOption(bindOpt);
                var open = ctx.ParseResult.GetValueForOption(openOpt);
                var (root, config, store) = await OpenStoreAsync();
                var listen = string.IsNullOrWhiteSpace(bind) ? $"127.0.0.1:{port}" : bind!;
                var state = new UiState { Store = store, Config = config, Root = root };
                var url = $"http://{listen}";
                Console.WriteLine($"Whyso explorer listening on {url}");
                Console.WriteLine("API  /api/v1/{status,sessions,tree,search,why,…}");
                Console.WriteLine("Press Ctrl+C to stop.");
                if (open)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    }
                    catch
                    {
                        // ignore browser launch failures
                    }
                }

                await UiHost.ServeAsync(state, listen);
            }
            catch (Exception ex)
            {
                WriteCliError(ex);
                ctx.ExitCode = 1;
            }
        });
        return cmd;
    }

    private static Command BuildInit()
    {
        var cmd = new Command("init", "Initialize Whyso in the current Git project");
        cmd.SetHandler(async () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var gitRoot = RequireGitRoot(cwd);
            var (root, config) = await Store.InitProjectAsync(gitRoot);
            Console.WriteLine($"Initialized Whyso in {root}");
            Console.WriteLine("  database : .whyso/worktree.db");
            Console.WriteLine("  events   : .whyso/events/");
            Console.WriteLine("  config   : .whyso/config.toml");
            Console.WriteLine($"  project  : {config.ProjectId}");
            Console.WriteLine($"  daemon   : {config.Bind}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("  whyso integrate cursor");
            Console.WriteLine("  whyso daemon");
        });
        return cmd;
    }

    private static Command BuildDaemon(ILoggerFactory loggerFactory)
    {
        var cmd = new Command("daemon", "Start the local event collector daemon");
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            _ = loggerFactory;
            try
            {
                var (root, config, store) = await OpenStoreAsync();
                var state = new AppState { Store = store, Config = config };
                Console.WriteLine($"Whyso daemon listening on http://{config.Bind}");
                Console.WriteLine("POST /v1/events  GET /v1/health");
                Console.WriteLine("Press Ctrl+C to stop.");
                await DaemonHost.ServeAsync(state, config.Bind);
            }
            catch (Exception ex)
            {
                WriteCliError(ex);
                ctx.ExitCode = 1;
            }
        });
        return cmd;
    }

    private static Command BuildStatus()
    {
        var cmd = new Command("status", "Show project and daemon status");
        cmd.SetHandler(async () =>
        {
            var (root, config, store) = await OpenStoreAsync();
            await EnsureNodesAsync(store);
            var sessions = await store.CountWorkNodesAsync();
            var prompts = await store.CountPromptsAsync();
            var healthUrl = $"http://{config.Bind}/v1/health";
            string daemonStatus;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var resp = await client.GetAsync(healthUrl);
                daemonStatus = resp.IsSuccessStatusCode
                    ? "running"
                    : $"unreachable (HTTP {(int)resp.StatusCode})";
            }
            catch
            {
                daemonStatus = "not running";
            }

            Console.WriteLine($"Project : {root}");
            Console.WriteLine($"Project ID : {config.ProjectId}");
            Console.WriteLine("Database : .whyso/worktree.db");
            Console.WriteLine($"Bind : {config.Bind}");
            Console.WriteLine($"Daemon : {daemonStatus}");
            Console.WriteLine($"Sessions : {sessions}");
            Console.WriteLine($"Prompts : {prompts}");
            if (config.Bind != Config.DefaultBind)
            {
                Console.WriteLine("(non-default bind address)");
            }
        });
        return cmd;
    }

    private static Command BuildSessions()
    {
        var fileOpt = new Option<string?>("--file", "Filter to a file path (file-touch timeline when used alone)");
        var prefixOpt = new Option<string?>("--prefix", "Filter to a path prefix / cluster");
        var jsonOpt = new Option<bool>("--json", "Machine-readable JSON output");
        var cmd = new Command("sessions", "List sessions (projections, labels, files, commits, edges)")
        {
            fileOpt,
            prefixOpt,
            jsonOpt,
        };
        cmd.SetHandler(async (string? file, string? prefix, bool json) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            try
            {
                await store.AttributeCommitsAtHeadAsync();
            }
            catch
            {
                // Best-effort; still show whatever is stored.
            }

            await EnsureNodesAsync(store);

            if (!string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(prefix))
            {
                var fileHistory = await HistoryQuery.FileHistoryAsync(store, file!);
                WorkNodeFormat.WriteFileHistory(Console.Out, fileHistory, json);
                return;
            }

            var entries = await HistoryQuery.ProjectHistoryAsync(store, file, prefix);
            WorkNodeFormat.WriteHistory(Console.Out, entries, json);
        }, fileOpt, prefixOpt, jsonOpt);
        return cmd;
    }

    private static Command BuildSession()
    {
        var idArg = new Argument<string>("id", "Session id (sess_...)");
        var jsonOpt = new Option<bool>("--json", "Machine-readable JSON output");
        var cmd = new Command("session", "Inspect a session (prompts, activities, files, git)")
        {
            idArg,
            jsonOpt,
        };
        cmd.SetHandler(async (string id, bool json) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            var session = await store.GetSessionAsync(id);
            if (session is null)
            {
                throw new InvalidOperationException($"session not found: {id}");
            }

            var node = await WorkNodeIndex.ProjectSessionAsync(store, session);
            WorkNodeFormat.WriteNode(Console.Out, node, json);
        }, idArg, jsonOpt);
        return cmd;
    }

    private static Command BuildEvents()
    {
        var sessionIdArg = new Argument<string>("session-id", "Session id (sess_...)");
        var cmd = new Command("events", "List events for a session") { sessionIdArg };
        cmd.SetHandler(async (string sessionId) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            if (await store.GetSessionAsync(sessionId) is null)
            {
                throw new InvalidOperationException($"session not found: {sessionId}");
            }

            var events = await store.ListEventsAsync(sessionId);
            if (events.Count == 0)
            {
                Console.WriteLine($"No events for {sessionId}");
                return;
            }

            foreach (var event_ in events)
            {
                var seq = event_.Sequence ?? 0;
                var ts = event_.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
                var label = Normalize.NormalizeEvent(event_)?.SummaryLine()
                    ?? event_.EventType.DisplayLabel();
                Console.WriteLine($"{seq,4}  {ts}  {label}");
            }
        }, sessionIdArg);
        return cmd;
    }

    private static Command BuildIntegrate()
    {
        var integrate = new Command("integrate", "Configure an agent integration");
        var cursor = new Command("cursor", "Install Cursor project hooks that forward events to Whyso");
        cursor.SetHandler(() =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var root = RequireGitRoot(cwd);
            var cursorDir = Path.Combine(root, ".cursor");
            Directory.CreateDirectory(cursorDir);
            var hooksPath = Path.Combine(cursorDir, "hooks.json");

            JsonNode? existing = null;
            if (File.Exists(hooksPath))
            {
                existing = JsonNode.Parse(File.ReadAllText(hooksPath));
            }

            var worktreeBin = ResolveWorktreeBin();
            InstallWrapper(root, worktreeBin);
            var command = CursorAdapter.CursorHookCommand(worktreeBin);
            var merged = CursorAdapter.MergeCursorHooks(existing, command);
            var pretty = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(hooksPath, pretty + "\n");

            var rel = Path.GetRelativePath(root, hooksPath);
            Console.WriteLine($"Configured Cursor integration at {rel}");
            Console.WriteLine($"Hook command: {command}");
            Console.WriteLine($"Whyso binary: {worktreeBin}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("  1. Keep `whyso daemon` running in this project");
            Console.WriteLine("  2. Reload Cursor (or reopen the folder) so hooks are picked up");
            Console.WriteLine("  3. Run an Agent chat prompt");
            Console.WriteLine("  4. Check: whyso sessions");
            Console.WriteLine("     Debug:  .whyso/hooks.log  (and Cursor Output → Hooks)");
        });
        integrate.AddCommand(cursor);
        return integrate;
    }

    private static Command BuildActivity()
    {
        var sessionIdArg = new Argument<string>("session-id", "Session id (sess_...)");
        var cmd = new Command("activity", "Reconstruct prompt turns and agent actions for a session")
        {
            sessionIdArg,
        };
        cmd.SetHandler(async (string sessionId) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            if (await store.GetSessionAsync(sessionId) is null)
            {
                throw new InvalidOperationException($"session not found: {sessionId}");
            }

            var timeline = await LoadTimelineAsync(store, sessionId);
            ActivityFormat.WriteTimeline(Console.Out, timeline);
        }, sessionIdArg);
        return cmd;
    }

    private static Command BuildTree()
    {
        var fromOpt = new Option<string?>("--from", "Focus on edges around a session id");
        var jsonOpt = new Option<bool>("--json", "Machine-readable JSON output");
        var cmd = new Command("tree", "Show the Work Tree (relationships between sessions)")
        {
            fromOpt,
            jsonOpt,
        };
        cmd.SetHandler(async (string? from, bool json) =>
        {
            var (root, _, store) = await OpenStoreAsync();
            await EnsureNodesAsync(store);
            await WorkNodeIndex.RebuildAutoEdgesAsync(store, root);
            var nodes = await store.ListWorkNodesAsync();
            var edges = await store.ListWorkNodeEdgesAsync(from);
            WorkNodeFormat.WriteTree(Console.Out, nodes, edges, from, json);
        }, fromOpt, jsonOpt);
        return cmd;
    }

    private static Command BuildLink()
    {
        var fromArg = new Argument<string>("from", "Source session id");
        var toArg = new Argument<string>("to", "Target session id");
        var labelOpt = new Option<string?>("--label", "Optional label (feature|fix|refactor|custom)");
        var cmd = new Command("link", "Manually link two sessions")
        {
            fromArg,
            toArg,
            labelOpt,
        };
        cmd.SetHandler(async (string from, string to, string? label) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            await EnsureNodeAsync(store, from);
            await EnsureNodeAsync(store, to);
            if (await store.GetWorkNodeAsync(from) is null)
            {
                throw new InvalidOperationException($"session not found: {from}");
            }

            if (await store.GetWorkNodeAsync(to) is null)
            {
                throw new InvalidOperationException($"session not found: {to}");
            }

            await store.UpsertManualEdgeAsync(new WorkNodeEdgeRecord
            {
                FromId = from,
                ToId = to,
                Kind = WorkNodeEdgeKinds.Manual,
                Label = label,
                Score = 1.0,
                Manual = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            Console.WriteLine($"Linked {from} → {to}" + (label is null ? "" : $" ({label})"));
        }, fromArg, toArg, labelOpt);
        return cmd;
    }

    private static Command BuildUnlink()
    {
        var fromArg = new Argument<string>("from", "Source session id");
        var toArg = new Argument<string>("to", "Target session id");
        var kindOpt = new Option<string>("--kind", () => WorkNodeEdgeKinds.Manual, "Edge kind to remove");
        var cmd = new Command("unlink", "Remove a session edge")
        {
            fromArg,
            toArg,
            kindOpt,
        };
        cmd.SetHandler(async (string from, string to, string kind) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            await store.DeleteEdgeAsync(from, to, kind);
            Console.WriteLine($"Removed edge {from} -[{kind}]-> {to}");
        }, fromArg, toArg, kindOpt);
        return cmd;
    }

    private static Command BuildLabel()
    {
        var idArg = new Argument<string>("id", "Session id");
        var labelArg = new Argument<string>("label", "Label (feature|fix|refactor|custom)");
        var removeOpt = new Option<bool>("--remove", "Remove the label instead of adding it");
        var cmd = new Command("label", "Add or remove a label on a session")
        {
            idArg,
            labelArg,
            removeOpt,
        };
        cmd.SetHandler(async (string id, string label, bool remove) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            await EnsureNodeAsync(store, id);
            if (await store.GetWorkNodeAsync(id) is null)
            {
                throw new InvalidOperationException($"session not found: {id}");
            }

            if (remove)
            {
                await store.RemoveWorkNodeLabelAsync(id, label);
                Console.WriteLine($"Removed label '{label}' from {id}");
            }
            else
            {
                await store.AddWorkNodeLabelAsync(id, label);
                Console.WriteLine($"Labeled {id} as '{label}'");
            }
        }, idArg, labelArg, removeOpt);
        return cmd;
    }

    private static Command BuildSearch()
    {
        var queryArg = new Argument<string>("query", "Search text");
        var typeOpt = new Option<string?>("--type", "Filter: prompt|node|file|commit|test|command");
        var fromOpt = new Option<string?>("--from", "Navigate from prompt|file|commit:<ref>");
        var jsonOpt = new Option<bool>("--json", "Machine-readable JSON output");
        var cmd = new Command("search", "Search prompts, sessions, files, commits, and activities")
        {
            queryArg,
            typeOpt,
            fromOpt,
            jsonOpt,
        };
        cmd.SetHandler(async (string query, string? type, string? from, bool json) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            await EnsureNodesAsync(store);
            string? fromKind = null;
            string? fromRef = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                var parts = from.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    fromKind = parts[0];
                    fromRef = parts[1];
                }
                else
                {
                    fromKind = "node";
                    fromRef = from;
                }
            }

            var hits = await HistoryQuery.SearchAsync(store, query, type, fromKind, fromRef);
            var nodes = (await store.ListWorkNodesAsync()).ToDictionary(n => n.Id, StringComparer.Ordinal);
            WorkNodeFormat.WriteSearch(Console.Out, hits, nodes, json);
        }, queryArg, typeOpt, fromOpt, jsonOpt);
        return cmd;
    }

    private static Command BuildRebuild()
    {
        var cmd = new Command("rebuild", "Rebuild session projections and auto Work Tree edges");
        cmd.SetHandler(async () =>
        {
            var (root, _, store) = await OpenStoreAsync();
            try
            {
                await store.AttributeCommitsAtHeadAsync();
            }
            catch
            {
                // Best-effort commit repair.
            }

            var count = await WorkNodeIndex.RebuildAllAsync(store, root);
            var edges = await store.ListWorkNodeEdgesAsync();
            var auto = edges.Count(e => !e.Manual);
            var manual = edges.Count(e => e.Manual);
            Console.WriteLine($"Rebuilt {count} work node(s); edges auto={auto} manual={manual}");
        });
        return cmd;
    }

    private static Command BuildWhy()
    {
        var why = new Command("why", "Trace a commit or file back to the session / prompt(s)");

        var shaArg = new Argument<string>("sha", "Git commit SHA (full or prefix)");
        var whyCommit = new Command("commit", "Provenance for a Git commit") { shaArg };
        whyCommit.SetHandler(async (string sha) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            var links = await store.ListCommitsByShaAsync(sha);
            if (links.Count == 0)
            {
                Console.WriteLine($"No captured provenance for commit {sha}");
                return;
            }

            var sessionIds = links.Select(l => l.SessionId).Distinct(StringComparer.Ordinal).ToList();
            var timelines = new List<SessionTimeline>();
            foreach (var sessionId in sessionIds)
            {
                timelines.Add(await LoadTimelineAsync(store, sessionId));
            }

            var result = ProvenanceQuery.Trace(new ProvenanceSubject.Commit(sha), timelines);
            ActivityFormat.WriteProvenance(Console.Out, result);
        }, shaArg);

        var pathArg = new Argument<string>("path", "File path (absolute or repo-relative)");
        var linesOpt = new Option<string?>("--lines", "Optional line range, e.g. 10-42");
        var jsonOpt = new Option<bool>("--json", "Also print file action timeline as JSON section");
        var whyFile = new Command("file", "Provenance for a file (optional line range)")
        {
            pathArg,
            linesOpt,
            jsonOpt,
        };
        whyFile.SetHandler(async (string path, string? lines, bool json) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            await EnsureNodesAsync(store);

            var candidateIds = await store.ListWorkNodeIdsForFileAsync(path);
            IEnumerable<SessionRecord> sessions;
            if (candidateIds.Count > 0)
            {
                var all = await store.ListSessionsAsync();
                sessions = all.Where(s => candidateIds.Contains(s.Id));
            }
            else
            {
                sessions = await store.ListSessionsAsync();
            }

            var timelines = new List<SessionTimeline>();
            foreach (var session in sessions)
            {
                timelines.Add(await LoadTimelineAsync(store, session.Id));
            }

            ProvenanceSubject subject;
            if (!string.IsNullOrWhiteSpace(lines))
            {
                if (!TryParseLineRange(lines, out var start, out var end))
                {
                    throw new InvalidOperationException(
                        "invalid --lines value; expected start-end (e.g. 10-42)");
                }

                subject = new ProvenanceSubject.CodeRange(path, start, end);
            }
            else
            {
                subject = new ProvenanceSubject.FilePath(path);
            }

            var result = ProvenanceQuery.Trace(subject, timelines);
            ActivityFormat.WriteProvenance(Console.Out, result);

            Console.WriteLine();
            var fileHistory = await HistoryQuery.FileHistoryAsync(store, path);
            WorkNodeFormat.WriteFileHistory(Console.Out, fileHistory, json);
        }, pathArg, linesOpt, jsonOpt);

        why.AddCommand(whyCommit);
        why.AddCommand(whyFile);
        return why;
    }

    private static async Task EnsureNodesAsync(Store store)
    {
        var count = await store.CountWorkNodesAsync();
        if (count > 0)
        {
            return;
        }

        var sessions = await store.ListSessionsAsync();
        if (sessions.Count == 0)
        {
            return;
        }

        await WorkNodeIndex.RebuildAllAsync(store, store.Root);
    }

    private static async Task EnsureNodeAsync(Store store, string id)
    {
        if (await store.GetWorkNodeAsync(id) is not null)
        {
            return;
        }

        var session = await store.GetSessionAsync(id);
        if (session is null)
        {
            return;
        }

        await WorkNodeIndex.ProjectSessionAsync(store, session);
    }

    private static async Task<SessionTimeline> LoadTimelineAsync(Store store, string sessionId)
    {
        var events = await store.ListEventsAsync(sessionId);
        var commits = await store.ListSessionCommitsAsync(sessionId);
        var links = commits.Select(c => new CommitLink
        {
            Sha = c.Sha,
            SessionId = c.SessionId,
            PromptEventId = c.PromptEventId,
            CapturedAt = c.CapturedAt,
        }).ToList();
        return ActivityReconstructor.Reconstruct(events, links);
    }

    private static bool TryParseLineRange(string text, out int start, out int end)
    {
        start = 0;
        end = 0;
        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out start) && int.TryParse(parts[1], out end);
    }

    private static Command BuildHook()
    {
        var hook = new Command("hook", "Adapter entrypoints invoked by agent hooks");
        var cursor = new Command("cursor", "Read a Cursor hook payload from stdin and forward it to the daemon");
        cursor.SetHandler(() =>
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            var text = Encoding.UTF8.GetString(bytes);
            text = CursorAdapter.NormalizeHookStdin(text);
            var (url, logPath) = ResolveEventsUrlAndLog(text);
            var response = CursorAdapter.HandleHookStdin(text, url, logPath);
            Console.WriteLine(response.ToJsonString());
        });
        hook.AddCommand(cursor);
        return hook;
    }

    private static void WriteCliError(Exception ex)
    {
        var message = FormatCliError(ex);
        Console.Error.WriteLine(message);
    }

    private static string FormatCliError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                || (cur is IOException && cur.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)))
            {
                return """
                    Failed to start: address already in use (another process is likely bound to this port).
                      • Check: whyso status
                      • Stop the other process, or change bind in .whyso/config.toml
                    """;
            }
        }

        if (ex is InvalidOperationException or StorageException)
        {
            return ex.Message;
        }

        return $"error: {ex.Message}";
    }

    private static async Task<(string Root, Config Config, Store Store)> OpenStoreAsync()
    {
        var cwd = Directory.GetCurrentDirectory();
        string root;
        try
        {
            root = WorktreePaths.FindWorktreeRoot(cwd);
        }
        catch (StorageException)
        {
            throw new InvalidOperationException(
                "whyso is not initialized here (run `whyso init` inside your Git project, then `whyso daemon` from that directory)");
        }

        var config = Config.Load(root);
        var store = await Store.OpenAsync(root);
        return (root, config, store);
    }

    private static string RequireGitRoot(string cwd)
    {
        try
        {
            return Git.DetectGitRoot(cwd);
        }
        catch (StorageException ex) when (ex.Message.Contains("not a git repository"))
        {
            throw new InvalidOperationException(
                "not a git repository; run `whyso init` inside a git project");
        }
    }

    private static string ResolveWorktreeBin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                return exe;
            }
        }
        catch
        {
            // fall through
        }

        return "whyso";
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void InstallWrapper(string root, string worktreeBin)
    {
        var hooksDir = Path.Combine(root, ".cursor", "hooks");
        Directory.CreateDirectory(hooksDir);
        if (OperatingSystem.IsWindows())
        {
            var wrapper = Path.Combine(hooksDir, "whyso-cursor.cmd");
            DeleteIfExists(Path.Combine(hooksDir, "worktree-cursor.cmd"));
            File.WriteAllText(wrapper, $"@echo off\r\n\"{worktreeBin}\" hook cursor\r\n");
        }
        else
        {
            var wrapper = Path.Combine(hooksDir, "whyso-cursor.sh");
            DeleteIfExists(Path.Combine(hooksDir, "worktree-cursor.sh"));
            File.WriteAllText(wrapper, $"#!/usr/bin/env bash\nexec \"{worktreeBin}\" hook cursor\n");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "+x", wrapper },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                })?.WaitForExit();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static (string Url, string? LogPath) ResolveEventsUrlAndLog(string stdin)
    {
        try
        {
            var raw = JsonNode.Parse(stdin);
            if (raw?["workspace_roots"] is JsonArray roots)
            {
                foreach (var rootNode in roots)
                {
                    if (rootNode?.GetValue<string>() is not string rootStr)
                    {
                        continue;
                    }

                    var rootPath = NormalizeWorkspaceRoot(rootStr);
                    if (Directory.Exists(Path.Combine(rootPath, ".whyso")))
                    {
                        try
                        {
                            var config = Config.Load(rootPath);
                            return (
                                $"http://{config.Bind}/v1/events",
                                Path.Combine(rootPath, ".whyso", "hooks.log"));
                        }
                        catch
                        {
                            // continue
                        }
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var cwd = Directory.GetCurrentDirectory();
            try
            {
                var root = WorktreePaths.FindWorktreeRoot(cwd);
                var config = Config.Load(root);
                return (
                    $"http://{config.Bind}/v1/events",
                    Path.Combine(root, ".whyso", "hooks.log"));
            }
            catch
            {
                // continue
            }

            if (Directory.Exists(Path.Combine(cwd, ".whyso")))
            {
                try
                {
                    var config = Config.Load(cwd);
                    return (
                        $"http://{config.Bind}/v1/events",
                        Path.Combine(cwd, ".whyso", "hooks.log"));
                }
                catch
                {
                    // continue
                }
            }

            return (
                CursorAdapter.DefaultEventsUrl,
                Path.Combine(cwd, ".whyso", "hooks.log"));
        }
        catch
        {
            return (CursorAdapter.DefaultEventsUrl, null);
        }
    }

    private static string NormalizeWorkspaceRoot(string root)
    {
        var trimmed = root.Trim();
        if (trimmed.Length >= 3
            && trimmed[0] == '/'
            && char.IsAsciiLetter(trimmed[1])
            && trimmed[2] == ':')
        {
            return trimmed[1..];
        }

        return trimmed;
    }
}
