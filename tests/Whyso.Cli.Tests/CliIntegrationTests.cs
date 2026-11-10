using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Whyso.Cli.Tests;

public class CliIntegrationTests
{
    private static string WhysoBin()
    {
        var local = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "whyso.exe" : "whyso");
        if (File.Exists(local))
        {
            return local;
        }

        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Whyso.Cli", "bin", "Debug", "net8.0",
            OperatingSystem.IsWindows() ? "whyso.exe" : "whyso"));
        Assert.True(File.Exists(path), $"whyso binary not found at {local} or {path}");
        return path;
    }

    private static string InitGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run(dir, "git", "init");
        Run(dir, "git", "config", "user.email", "test@example.com");
        Run(dir, "git", "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "test");
        Run(dir, "git", "add", "README.md");
        Run(dir, "git", "commit", "-m", "init");
        return dir;
    }

    private static void Run(string cwd, string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunWhyso(
        string cwd, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WhysoBin(),
            WorkingDirectory = cwd,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        // Read stdout/stderr concurrently to avoid pipe-buffer deadlocks on Windows.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        Task.WaitAll(stdoutTask, stderrTask);
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    [Fact]
    public void Init_and_status()
    {
        var dir = InitGitRepo();
        try
        {
            var (code, stdout, _) = RunWhyso(dir, null, "init");
            Assert.Equal(0, code);
            Assert.Contains("Initialized Whyso", stdout);
            Assert.True(File.Exists(Path.Combine(dir, ".whyso", "whyso.db")));
            Assert.True(File.Exists(Path.Combine(dir, ".whyso", "config.toml")));

            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);

            var status = RunWhyso(dir, null, "status");
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("Sessions : 0", status.StdOut);
            Assert.Contains("Prompts : 0", status.StdOut);
            Assert.DoesNotContain("Events :", status.StdOut);
            Assert.DoesNotContain("Projections :", status.StdOut);
            Assert.Contains("Project ID", status.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sessions_empty_message()
    {
        var dir = InitGitRepo();
        try
        {
            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);
            var result = RunWhyso(dir, null, "sessions");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No sessions yet.", result.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Sessions_rebuild_filters_and_search()
    {
        var dir = InitGitRepo();
        Process? daemon = null;
        try
        {
            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);
            var port = GetFreePort();
            var bind = $"127.0.0.1:{port}";
            var configPath = Path.Combine(dir, ".whyso", "config.toml");
            var config = File.ReadAllText(configPath);
            var updated = string.Join("\n", config.Split('\n').Select(line =>
                line.TrimStart().StartsWith("bind") ? $"bind = \"{bind}\"" : line.TrimEnd('\r')));
            File.WriteAllText(configPath, updated);

            daemon = Process.Start(new ProcessStartInfo
            {
                FileName = WhysoBin(),
                WorkingDirectory = dir,
                ArgumentList = { "daemon" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(daemon);
            Assert.True(await WaitForHealthAsync($"http://{bind}/v1/health", TimeSpan.FromSeconds(15)));

            const string sessionId = "sess_nodes_cli";
            var rootJson = JsonValue.Create(dir)!;
            foreach (var hook in new[]
                     {
                         new JsonObject
                         {
                             ["hook_event_name"] = "sessionStart",
                             ["conversation_id"] = "nodes_cli",
                             ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                         },
                         new JsonObject
                         {
                             ["hook_event_name"] = "beforeSubmitPrompt",
                             ["conversation_id"] = "nodes_cli",
                             ["prompt"] = "Refactor storage layer",
                             ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                         },
                         new JsonObject
                         {
                             ["hook_event_name"] = "afterFileEdit",
                             ["conversation_id"] = "nodes_cli",
                             ["file_path"] = "src/Whyso.Storage/Store.cs",
                             ["edits"] = new JsonArray(new JsonObject
                             {
                                 ["old_string"] = "a",
                                 ["new_string"] = "b",
                             }),
                         },
                         new JsonObject
                         {
                             ["hook_event_name"] = "afterAgentResponse",
                             ["conversation_id"] = "nodes_cli",
                             ["text"] = "Updated Store.",
                             ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                         },
                         new JsonObject
                         {
                             ["hook_event_name"] = "sessionEnd",
                             ["conversation_id"] = "nodes_cli",
                             ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                         },
                     })
            {
                var hookResult = RunWhyso(dir, hook.ToJsonString(), "hook", "cursor");
                Assert.Equal(0, hookResult.ExitCode);
            }

            // Allow projection after sessionEnd.
            await Task.Delay(300);

            var rebuild = RunWhyso(dir, null, "rebuild");
            Assert.Equal(0, rebuild.ExitCode);
            Assert.Contains("Rebuilt", rebuild.StdOut);

            var sessions = RunWhyso(dir, null, "sessions");
            Assert.Equal(0, sessions.ExitCode);
            Assert.Contains(sessionId, sessions.StdOut);

            var session = RunWhyso(dir, null, "session", sessionId);
            Assert.Equal(0, session.ExitCode);
            Assert.Contains("Refactor storage layer", session.StdOut);

            Assert.Equal(0, RunWhyso(dir, null, "label", sessionId, "refactor").ExitCode);

            var filtered = RunWhyso(dir, null, "sessions", "--prefix", "src/");
            Assert.Equal(0, filtered.ExitCode);
            Assert.Contains(sessionId, filtered.StdOut);

            var search = RunWhyso(dir, null, "search", "storage");
            Assert.Equal(0, search.ExitCode);
            Assert.Contains(sessionId, search.StdOut);

            var why = RunWhyso(dir, null, "why", "file", "Store.cs");
            Assert.Equal(0, why.ExitCode);
            Assert.Contains("session=", why.StdOut);
        }
        finally
        {
            if (daemon is not null)
            {
                try { daemon.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { daemon.WaitForExit(5000); } catch { /* ignore */ }
                daemon.Dispose();
            }

            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Integrate_cursor_writes_hooks()
    {
        var dir = InitGitRepo();
        try
        {
            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);
            var result = RunWhyso(dir, null, "integrate", "cursor");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Configured Cursor integration", result.StdOut);
            var hooks = File.ReadAllText(Path.Combine(dir, ".cursor", "hooks.json"));
            Assert.Contains("afterFileEdit", hooks);
            Assert.True(
                hooks.Contains("whyso-cursor") || hooks.Contains("hook cursor"),
                hooks);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(File.Exists(Path.Combine(dir, ".cursor", "hooks", "whyso-cursor.cmd")));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task End_to_end_daemon_capture()
    {
        var dir = InitGitRepo();
        Process? daemon = null;
        try
        {
            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);

            var port = GetFreePort();
            var bind = $"127.0.0.1:{port}";
            var configPath = Path.Combine(dir, ".whyso", "config.toml");
            var config = File.ReadAllText(configPath);
            var updated = string.Join("\n", config.Split('\n').Select(line =>
                line.TrimStart().StartsWith("bind") ? $"bind = \"{bind}\"" : line.TrimEnd('\r')));
            File.WriteAllText(configPath, updated);

            daemon = Process.Start(new ProcessStartInfo
            {
                FileName = WhysoBin(),
                WorkingDirectory = dir,
                ArgumentList = { "daemon" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(daemon);

            Assert.True(await WaitForHealthAsync($"http://{bind}/v1/health", TimeSpan.FromSeconds(15)));

            const string sessionId = "sess_e2e_cursor";
            var rootJson = JsonValue.Create(dir)!;
            var hooks = new[]
            {
                new JsonObject
                {
                    ["hook_event_name"] = "sessionStart",
                    ["conversation_id"] = "e2e_cursor",
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "beforeSubmitPrompt",
                    ["conversation_id"] = "e2e_cursor",
                    ["prompt"] = "Implement JWT authentication",
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "afterFileEdit",
                    ["conversation_id"] = "e2e_cursor",
                    ["file_path"] = "src/auth/AuthService.java",
                    ["edits"] = new JsonArray(new JsonObject
                    {
                        ["old_string"] = "",
                        ["new_string"] = "class Auth {}",
                    }),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "beforeShellExecution",
                    ["conversation_id"] = "e2e_cursor",
                    ["command"] = "cargo test",
                    ["cwd"] = dir,
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "afterShellExecution",
                    ["conversation_id"] = "e2e_cursor",
                    ["command"] = "cargo test",
                    ["output"] = "ok",
                    ["duration"] = 12,
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "afterAgentResponse",
                    ["conversation_id"] = "e2e_cursor",
                    ["text"] = "Done.",
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
                new JsonObject
                {
                    ["hook_event_name"] = "sessionEnd",
                    ["conversation_id"] = "e2e_cursor",
                    ["workspace_roots"] = new JsonArray(rootJson.DeepClone()),
                },
            };

            foreach (var hook in hooks)
            {
                var result = RunWhyso(dir, hook.ToJsonString(), "hook", "cursor");
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("{}", result.StdOut);
            }

            var direct = new JsonObject
            {
                ["version"] = "1.0",
                ["event_id"] = "evt_" + Guid.NewGuid().ToString("D"),
                ["session_id"] = sessionId,
                ["timestamp"] = "2026-08-10T15:30:00Z",
                ["agent"] = "cursor",
                ["event_type"] = "test.completed",
                ["payload"] = new JsonObject { ["name"] = "auth_test" },
            };
            using (var client = new HttpClient())
            {
                var resp = await client.PostAsync(
                    $"http://{bind}/v1/events",
                    new StringContent(direct.ToJsonString(), Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
            }

            var sessions = RunWhyso(dir, null, "sessions");
            Assert.Equal(0, sessions.ExitCode);
            Assert.Contains(sessionId, sessions.StdOut);
            Assert.Contains("cursor", sessions.StdOut);
            // Post-end test.completed reopens the completed conversation.
            Assert.Contains("active", sessions.StdOut);

            var events = RunWhyso(dir, null, "events", sessionId);
            Assert.Equal(0, events.ExitCode);
            void Require(string needle) =>
                Assert.True(
                    events.StdOut.Contains(needle, StringComparison.Ordinal),
                    $"Expected events output to contain '{needle}'. Output:{Environment.NewLine}{events.StdOut}");

            Require("SESSION STARTED");
            Require("PROMPT:");
            Require("FILE MODIFIED:");
            Require("CMD START:");
            Require("CMD DONE:");
            Require("RESPONSE:");
            Require("SESSION ENDED");
            Require("TEST DONE:");

            var jsonlFiles = FindJsonlFiles(Path.Combine(dir, ".whyso", "events"), sessionId);
            Assert.NotEmpty(jsonlFiles);
            var contents = string.Join("\n", jsonlFiles.Select(File.ReadAllText));
            var lines = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(lines.Length >= 7, $"expected JSONL lines, got {lines.Length}");
            Assert.Contains("file.modified", contents);
            Assert.Contains("prompt.submitted", contents);

            var status = RunWhyso(dir, null, "status");
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("Sessions : 1", status.StdOut);
            Assert.Contains("Prompts :", status.StdOut);
            Assert.DoesNotContain("Events :", status.StdOut);
            Assert.DoesNotContain("Projections :", status.StdOut);
            Assert.Contains("running", status.StdOut);
        }
        finally
        {
            if (daemon is not null)
            {
                try { daemon.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { daemon.WaitForExit(5000); } catch { /* ignore */ }
                daemon.Dispose();
            }

            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Init_installs_hooks_for_detected_agent()
    {
        var dir = InitGitRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".claude"));
            var (code, stdout, _) = RunWhyso(dir, null, "init");
            Assert.Equal(0, code);
            Assert.Contains("Claude Code", stdout);
            var settings = File.ReadAllText(Path.Combine(dir, ".claude", "settings.json"));
            Assert.Contains("hook claude-code", settings);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Hook_starts_daemon_and_stop_ends_it()
    {
        var dir = InitGitRepo();
        try
        {
            Assert.Equal(0, RunWhyso(dir, null, "init").ExitCode);
            var bind = $"127.0.0.1:{GetFreePort()}";
            var configPath = Path.Combine(dir, ".whyso", "config.toml");
            File.WriteAllText(configPath, string.Join("\n", File.ReadAllText(configPath).Split('\n').Select(line =>
                line.TrimStart().StartsWith("bind") ? $"bind = \"{bind}\"" : line.TrimEnd('\r'))));

            Assert.Contains("not running", RunWhyso(dir, null, "status").StdOut);

            // No daemon yet: the hook has to start one, deliver the event, and still return.
            var hook = new JsonObject
            {
                ["hook_event_name"] = "sessionStart",
                ["conversation_id"] = "autostart",
                ["workspace_roots"] = new JsonArray(JsonValue.Create(dir)),
            };
            var result = RunWhyso(dir, hook.ToJsonString(), "hook", "cursor");
            Assert.Equal(0, result.ExitCode);

            var status = RunWhyso(dir, null, "status").StdOut;
            Assert.Contains("Daemon : running", status);
            Assert.Contains("Sessions : 1", status);
            Assert.True(File.Exists(Path.Combine(dir, ".whyso", "daemon.pid")));

            var stop = RunWhyso(dir, null, "daemon", "stop");
            Assert.Equal(0, stop.ExitCode);
            Assert.Contains("Stopped Whyso daemon", stop.StdOut);
            Assert.Contains("not running", RunWhyso(dir, null, "status").StdOut);
            Assert.False(File.Exists(Path.Combine(dir, ".whyso", "daemon.pid")));
        }
        finally
        {
            try { RunWhyso(dir, null, "daemon", "stop"); } catch { /* ignore */ }
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForHealthAsync(string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                var resp = await client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // retry
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static List<string> FindJsonlFiles(string root, string sessionId)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetFiles(root, $"{sessionId}.jsonl", SearchOption.AllDirectories).ToList();
    }
}
