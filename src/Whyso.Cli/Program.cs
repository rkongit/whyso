using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Whyso.Activities;
using Whyso.Adapter.ClaudeCode;
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
        root.AddCommand(BuildForget());
        root.AddCommand(BuildSearch());
        root.AddCommand(BuildRebuild());
        root.AddCommand(BuildWhy());
        root.AddCommand(BuildContext());
        root.AddCommand(BuildConfig());
        root.AddCommand(BuildNotes());
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
        var cmd = new Command("init", "Initialize Whyso in the current Git project and hook up your coding agents");
        cmd.SetHandler(async () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var gitRoot = RequireGitRoot(cwd);
            var (root, config) = await Store.InitProjectAsync(gitRoot);
            Console.WriteLine($"Initialized Whyso in {root}");
            Console.WriteLine("  database : .whyso/whyso.db");
            Console.WriteLine("  events   : .whyso/events/");
            Console.WriteLine("  config   : .whyso/config.toml");
            Console.WriteLine($"  project  : {config.ProjectId}");
            Console.WriteLine($"  daemon   : {config.Bind} (starts itself on the first agent event)");

            var hooked = false;
            if (AgentDetected(root, ".claude"))
            {
                var settingsPath = IntegrateClaudeCode(root).SettingsPath;
                Console.WriteLine($"  hooks    : Claude Code ({Path.GetRelativePath(root, settingsPath)})");
                hooked = true;
            }

            if (AgentDetected(root, ".cursor"))
            {
                var hooksPath = IntegrateCursor(root).HooksPath;
                Console.WriteLine($"  hooks    : Cursor ({Path.GetRelativePath(root, hooksPath)})");
                hooked = true;
            }

            Console.WriteLine();
            if (hooked)
            {
                Console.WriteLine("Start a new agent session here and work as usual, then check: whyso sessions");
            }
            else
            {
                Console.WriteLine("No Claude Code or Cursor setup found. Hook up your agent with one of:");
                Console.WriteLine("  whyso integrate claude-code");
                Console.WriteLine("  whyso integrate cursor");
            }
        });
        return cmd;
    }

    /// <summary>True when the project or the user's home directory has this agent's config folder.</summary>
    private static bool AgentDetected(string root, string dirName)
    {
        if (Directory.Exists(Path.Combine(root, dirName)))
        {
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, dirName));
    }

    private static (string HooksPath, string Command, string Bin) IntegrateCursor(string root)
    {
        var cursorDir = Path.Combine(root, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var hooksPath = Path.Combine(cursorDir, "hooks.json");

        JsonNode? existing = null;
        if (File.Exists(hooksPath))
        {
            existing = JsonNode.Parse(File.ReadAllText(hooksPath));
        }

        var bin = ResolveWhysoBin();
        InstallWrapper(root, bin);
        var command = CursorAdapter.CursorHookCommand(bin);
        var merged = CursorAdapter.MergeCursorHooks(existing, command);
        var pretty = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(hooksPath, pretty + "\n");
        return (hooksPath, command, bin);
    }

    private static (string SettingsPath, string Command) IntegrateClaudeCode(string root)
    {
        var claudeDir = Path.Combine(root, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        JsonNode? existing = null;
        if (File.Exists(settingsPath))
        {
            existing = JsonNode.Parse(File.ReadAllText(settingsPath));
        }

        var command = ClaudeCodeAdapter.HookCommand(ResolveWhysoBin());
        var merged = ClaudeCodeAdapter.MergeSettings(existing, command);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return (settingsPath, command);
    }

    private static Command BuildDaemon(ILoggerFactory loggerFactory)
    {
        var backgroundOpt = new Option<bool>("--background", "Run without console output (used when a hook starts the daemon)")
        {
            IsHidden = true,
        };
        var cmd = new Command("daemon", "Start the local event collector daemon (hooks start it for you)")
        {
            backgroundOpt,
        };
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            _ = loggerFactory;
            string? pidPath = null;
            try
            {
                if (ctx.ParseResult.GetValueForOption(backgroundOpt))
                {
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                }

                var (root, config, store) = await OpenStoreAsync();
                store.WriteGitNotes = config.GitNotes;
                var state = new AppState { Store = store, Config = config };
                Console.WriteLine($"Whyso daemon listening on http://{config.Bind}");
                if (config.GitNotes)
                {
                    Console.WriteLine($"Git notes: on (refs/notes/{Git.NotesRef})");
                }
                Console.WriteLine("POST /v1/events  GET /v1/health");
                Console.WriteLine("Press Ctrl+C to stop, or run `whyso daemon stop`.");
                pidPath = DaemonPidPath(root);
                await DaemonHost.ServeAsync(
                    state,
                    config.Bind,
                    onStarted: () => File.WriteAllText(pidPath, Environment.ProcessId.ToString()));
            }
            catch (Exception ex)
            {
                WriteCliError(ex);
                ctx.ExitCode = 1;
            }
            finally
            {
                RemoveOwnPidFile(pidPath);
            }
        });

        var stop = new Command("stop", "Stop the daemon running for this project");
        stop.SetHandler((InvocationContext ctx) =>
        {
            try
            {
                var root = WhysoPaths.FindWhysoRoot(Directory.GetCurrentDirectory());
                var pidPath = DaemonPidPath(root);
                if (!File.Exists(pidPath)
                    || !int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid))
                {
                    Console.WriteLine("Daemon is not running.");
                    return;
                }

                try
                {
                    using var process = Process.GetProcessById(pid);
                    // A recycled pid could belong to anything; only stop our own binary.
                    if (!IsWhysoProcess(process))
                    {
                        Console.WriteLine("Daemon is not running (stale pid file removed).");
                    }
                    else
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        Console.WriteLine($"Stopped Whyso daemon (pid {pid}).");
                    }
                }
                catch (ArgumentException)
                {
                    Console.WriteLine("Daemon is not running (stale pid file removed).");
                }

                DeleteIfExists(pidPath);
            }
            catch (Exception ex)
            {
                WriteCliError(ex);
                ctx.ExitCode = 1;
            }
        });
        cmd.AddCommand(stop);
        return cmd;
    }

    private static string DaemonPidPath(string root) =>
        Path.Combine(WhysoPaths.WhysoDir(root), "daemon.pid");

    private static void RemoveOwnPidFile(string? pidPath)
    {
        try
        {
            if (pidPath is not null
                && File.Exists(pidPath)
                && File.ReadAllText(pidPath).Trim() == Environment.ProcessId.ToString())
            {
                File.Delete(pidPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsWhysoProcess(Process process)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return string.Equals(process.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Called by hooks: if nothing is listening on the project's daemon address, start
    /// <c>whyso daemon --background</c> detached and wait briefly for it. Never throws.
    /// Set <c>WHYSO_NO_AUTOSTART=1</c> to turn this off.
    /// </summary>
    private static void EnsureDaemonRunning(string? root, string? logPath)
    {
        if (root is null || Environment.GetEnvironmentVariable("WHYSO_NO_AUTOSTART") is "1" or "true")
        {
            return;
        }

        try
        {
            var config = Config.Load(root);
            if (!IPEndPoint.TryParse(config.Bind, out var endpoint) || CanConnect(endpoint, 250))
            {
                return;
            }

            StartDaemonDetached(root);
            var waited = Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 5000)
            {
                if (CanConnect(endpoint, 250))
                {
                    HookSupport.AppendLog(logPath, $"started daemon on {config.Bind}");
                    return;
                }

                Thread.Sleep(100);
            }

            HookSupport.AppendLog(logPath, $"daemon did not come up on {config.Bind} within 5s");
        }
        catch (Exception ex)
        {
            HookSupport.AppendLog(logPath, $"daemon autostart failed: {ex.Message}");
        }
    }

    private static bool CanConnect(IPEndPoint endpoint, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient(endpoint.AddressFamily);
            return client.ConnectAsync(endpoint.Address, endpoint.Port).Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the daemon so it outlives this hook process and holds none of its stdio pipes
    /// (an agent waiting for the hook's stdout to close would otherwise hang).
    /// </summary>
    private static void StartDaemonDetached(string root)
    {
        var bin = ResolveWhysoBin();
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute does not pass our handles to the child.
            psi = new ProcessStartInfo
            {
                FileName = bin,
                Arguments = "daemon --background",
                WorkingDirectory = root,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = root,
                UseShellExecute = false,
                ArgumentList =
                {
                    "-c",
                    "nohup \"$0\" daemon --background >/dev/null 2>&1 </dev/null &",
                    bin,
                },
            };
        }

        using var process = Process.Start(psi);
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
            Console.WriteLine("Database : .whyso/whyso.db");
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
            var root = RequireGitRoot(Directory.GetCurrentDirectory());
            var (hooksPath, command, bin) = IntegrateCursor(root);
            Console.WriteLine($"Configured Cursor integration at {Path.GetRelativePath(root, hooksPath)}");
            Console.WriteLine($"Hook command: {command}");
            Console.WriteLine($"Whyso binary: {bin}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("  1. Reload Cursor (or reopen the folder) so hooks are picked up");
            Console.WriteLine("  2. Run an Agent chat prompt (the daemon starts itself)");
            Console.WriteLine("  3. Check: whyso sessions");
            Console.WriteLine("     Debug:  .whyso/hooks.log  (and Cursor Output → Hooks)");
        });
        integrate.AddCommand(cursor);

        var claudeCode = new Command(
            "claude-code",
            "Install Claude Code project hooks (.claude/settings.json) that forward events to Whyso");
        claudeCode.SetHandler(() =>
        {
            var root = RequireGitRoot(Directory.GetCurrentDirectory());
            var (settingsPath, command) = IntegrateClaudeCode(root);
            Console.WriteLine($"Configured Claude Code integration at {Path.GetRelativePath(root, settingsPath)}");
            Console.WriteLine($"Hook command: {command}");
            Console.WriteLine($"Hooks: {string.Join(", ", ClaudeCodeAdapter.HookNames)}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("  1. Start a new Claude Code session here (SessionStart injects `whyso context`; the daemon starts itself)");
            Console.WriteLine("  2. Check: whyso sessions");
            Console.WriteLine("     Debug:  .whyso/hooks.log");
        });
        integrate.AddCommand(claudeCode);
        return integrate;
    }

    private static Command BuildConfig()
    {
        var config = new Command("config", "Show or change project settings in .whyso/config.toml");

        var get = new Command("get", "Show current settings");
        get.SetHandler(() =>
        {
            var root = WhysoPaths.FindWhysoRoot(Directory.GetCurrentDirectory());
            var cfg = Config.Load(root);
            Console.WriteLine($"bind                  = {cfg.Bind}");
            Console.WriteLine($"context.session_start = {cfg.ContextOnSessionStart.ToString().ToLowerInvariant()}");
            Console.WriteLine($"git.notes             = {cfg.GitNotes.ToString().ToLowerInvariant()}");
        });

        var keyArg = new Argument<string>("key", $"Setting: {string.Join(" | ", Config.BoolKeys.Keys)}");
        var valueArg = new Argument<bool>("value", "true or false");
        var set = new Command("set", "Change a setting (restart the daemon to apply)") { keyArg, valueArg };
        set.SetHandler((InvocationContext ctx) =>
        {
            var key = ctx.ParseResult.GetValueForArgument(keyArg);
            var value = ctx.ParseResult.GetValueForArgument(valueArg);
            var root = WhysoPaths.FindWhysoRoot(Directory.GetCurrentDirectory());
            var cfg = Config.Load(root);
            if (!cfg.TrySet(key, value))
            {
                Console.Error.WriteLine($"unknown setting '{key}' (expected {string.Join(", ", Config.BoolKeys.Keys)})");
                ctx.ExitCode = 1;
                return;
            }

            Config.Save(root, cfg);
            Console.WriteLine($"{key} = {value.ToString().ToLowerInvariant()} (restart `whyso daemon` to apply)");
        });

        config.AddCommand(get);
        config.AddCommand(set);
        return config;
    }

    private static Command BuildNotes()
    {
        var notes = new Command("notes", $"Git notes provenance (refs/notes/{Git.NotesRef})");
        var write = new Command("write", "Write notes for every commit already linked to a session");
        write.SetHandler(async () =>
        {
            var (_, _, store) = await OpenStoreAsync();
            var shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in await store.ListSessionsAsync())
            {
                foreach (var commit in await store.ListSessionCommitsAsync(session.Id))
                {
                    shas.Add(commit.Sha);
                }
            }

            var written = 0;
            foreach (var sha in shas)
            {
                if (await store.WriteProvenanceNoteAsync(sha))
                {
                    written++;
                }
            }

            Console.WriteLine($"Wrote {written} of {shas.Count} notes to refs/notes/{Git.NotesRef}");
            Console.WriteLine($"Share them with: git push origin refs/notes/{Git.NotesRef}");
        });
        notes.AddCommand(write);
        return notes;
    }

    private static Command BuildContext()
    {
        var formatOpt = new Option<string>("--format", () => "md", "Output format: md or json");
        var sessionsOpt = new Option<int>("--sessions", () => 10, "How many recent sessions to scan");
        var maxCharsOpt = new Option<int>("--max-chars", () => 4000, "Maximum length of the markdown brief");
        var cmd = new Command(
            "context",
            "Print a short brief of past decisions and rejected approaches for a new agent session")
        {
            formatOpt,
            sessionsOpt,
            maxCharsOpt,
        };
        cmd.SetHandler(async (string format, int sessions, int maxChars) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            var options = new ContextBriefOptions { MaxSessions = sessions, MaxChars = maxChars };
            if (format == "json")
            {
                var items = await ContextBrief.CollectAsync(store, options);
                Console.WriteLine(JsonSerializer.Serialize(items, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }));
                return;
            }

            var markdown = await ContextBrief.BuildMarkdownAsync(store, options);
            Console.Write(string.IsNullOrEmpty(markdown) ? "No past sessions with readable prompts yet.\n" : markdown);
        }, formatOpt, sessionsOpt, maxCharsOpt);
        return cmd;
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

    private static Command BuildForget()
    {
        var idArg = new Argument<string>("session-id", "Session id (sess_...)");
        var yesOpt = new Option<bool>("--yes", "Skip the confirmation prompt");
        var cmd = new Command(
            "forget",
            "Crypto-shred a session: delete its encryption key so stored prompts and payloads become unreadable")
        {
            idArg,
            yesOpt,
        };
        cmd.SetHandler(async (string id, bool yes) =>
        {
            var (_, _, store) = await OpenStoreAsync();
            if (await store.GetSessionAsync(id) is null)
            {
                throw new InvalidOperationException($"session not found: {id}");
            }

            if (!yes)
            {
                Console.Write(
                    $"Delete the encryption key for {id}? Its prompts and tool payloads become unreadable and this cannot be undone. Type 'yes' to continue: ");
                if (Console.ReadLine()?.Trim() != "yes")
                {
                    Console.WriteLine("Aborted.");
                    return;
                }
            }

            var deleted = store.ForgetSessionKey(id);
            // Rebuild the session's projection so search and summaries drop prompt text.
            await WorkNodeIndex.ProjectSessionByIdAsync(store, id);
            await store.CheckpointAsync();
            Console.WriteLine(deleted
                ? $"Forgot {id}: encryption key deleted; projections rebuilt."
                : $"{id} had no encryption key (nothing to forget).");
        }, idArg, yesOpt);
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
            var (root, _, store) = await OpenStoreAsync();
            var links = await store.ListCommitsByShaAsync(sha);
            if (links.Count == 0)
            {
                // Fallback for clones that fetched refs/notes/whyso but have no local capture.
                var full = Git.ResolveCommit(root, sha);
                var note = full is null ? null : Git.ReadNote(root, full);
                if (note is not null)
                {
                    Console.WriteLine($"No local capture for {sha}; provenance from refs/notes/{Git.NotesRef}:");
                    Console.WriteLine(note);
                    return;
                }

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
            var (url, logPath, projectRoot) = ResolveHookTarget(text);
            EnsureDaemonRunning(projectRoot, logPath);
            var response = CursorAdapter.HandleHookStdin(text, url, logPath);
            Console.WriteLine(response.ToJsonString());
        });
        hook.AddCommand(cursor);

        var claudeCode = new Command("claude-code", "Read a Claude Code hook payload from stdin and forward it to the daemon");
        claudeCode.SetHandler(() =>
        {
            // Fail open: whatever happens, exit 0 so Claude Code is never blocked.
            try
            {
                var text = HookSupport.NormalizeStdin(ReadStdinUtf8());
                var (url, logPath, projectRoot) = ResolveHookTarget(text);
                EnsureDaemonRunning(projectRoot, logPath);
                string? sessionId = null;
                try
                {
                    var raw = JsonNode.Parse(text)?["session_id"]?.GetValue<string>();
                    sessionId = raw is null ? null : Protocol.SessionIdFromExternal(raw);
                }
                catch
                {
                    // handled by the adapter's own parse
                }

                var output = ClaudeCodeAdapter.HandleHookStdin(
                    text,
                    url,
                    logPath,
                    projectRoot is null ? null : () => BuildContextForHook(projectRoot, sessionId));
                if (output.Length > 0)
                {
                    Console.WriteLine(output);
                }
            }
            catch
            {
                // never block the agent
            }
        });
        hook.AddCommand(claudeCode);
        return hook;
    }

    private static string ReadStdinUtf8()
    {
        using var stdin = Console.OpenStandardInput();
        using var ms = new MemoryStream();
        stdin.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? BuildContextForHook(string projectRoot, string? currentSessionId)
    {
        var config = Config.Load(projectRoot);
        if (!config.ContextOnSessionStart)
        {
            return null;
        }

        return Task.Run(async () =>
        {
            await using var store = await Store.OpenAsync(projectRoot);
            return await ContextBrief.BuildMarkdownAsync(
                store,
                new ContextBriefOptions { ExcludeSessionId = currentSessionId });
        }).GetAwaiter().GetResult();
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
            root = WhysoPaths.FindWhysoRoot(cwd);
        }
        catch (StorageException)
        {
            throw new InvalidOperationException(
                "whyso is not initialized here (run `whyso init` inside your Git project)");
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

    private static string ResolveWhysoBin()
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

    private static void InstallWrapper(string root, string whysoBin)
    {
        var hooksDir = Path.Combine(root, ".cursor", "hooks");
        Directory.CreateDirectory(hooksDir);
        if (OperatingSystem.IsWindows())
        {
            var wrapper = Path.Combine(hooksDir, "whyso-cursor.cmd");
            File.WriteAllText(wrapper, $"@echo off\r\n\"{whysoBin}\" hook cursor\r\n");
        }
        else
        {
            var wrapper = Path.Combine(hooksDir, "whyso-cursor.sh");
            File.WriteAllText(wrapper, $"#!/usr/bin/env bash\nexec \"{whysoBin}\" hook cursor\n");
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

    /// <summary>
    /// Finds the Whyso project a hook belongs to: Cursor's <c>workspace_roots</c>, then the
    /// payload <c>cwd</c> (Claude Code), then the process directory. Each candidate is walked
    /// up to the nearest initialized project.
    /// </summary>
    private static (string Url, string? LogPath, string? Root) ResolveHookTarget(string stdin)
    {
        var candidates = new List<string>();
        try
        {
            var raw = JsonNode.Parse(stdin);
            if (raw?["workspace_roots"] is JsonArray roots)
            {
                foreach (var rootNode in roots)
                {
                    if (rootNode?.GetValue<string>() is string rootStr)
                    {
                        candidates.Add(NormalizeWorkspaceRoot(rootStr));
                    }
                }
            }

            if (raw?["cwd"]?.GetValue<string>() is string payloadCwd)
            {
                candidates.Add(payloadCwd);
            }
        }
        catch
        {
            // fall through to the process directory
        }

        try
        {
            candidates.Add(Directory.GetCurrentDirectory());
        }
        catch
        {
            // ignore
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var root = WhysoPaths.FindWhysoRoot(candidate);
                var config = Config.Load(root);
                return (
                    $"http://{config.Bind}/v1/events",
                    Path.Combine(WhysoPaths.WhysoDir(root), "hooks.log"),
                    root);
            }
            catch
            {
                // try the next candidate
            }
        }

        // Not initialized anywhere: post to the default port, and don't create a log dir.
        return (HookSupport.DefaultEventsUrl, null, null);
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
