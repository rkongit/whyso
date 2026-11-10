using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Whyso.Activities;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Ui.Tests;

public class UiApiTests
{
    private static async Task<(HttpClient Client, string Root, Config Config, Store Store, IAsyncDisposable Host)> SetupAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run(dir, "git", "init");
        Run(dir, "git", "config", "user.email", "test@example.com");
        Run(dir, "git", "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run(dir, "git", "add", "README.md");
        Run(dir, "git", "commit", "-m", "init");

        var (root, config) = await Store.InitProjectAsync(dir);
        var store = await Store.OpenAsync(root);
        var state = new UiState { Store = store, Config = config, Root = root };

        var port = GetFreePort();
        var bind = $"127.0.0.1:{port}";
        var cts = new CancellationTokenSource();
        var app = UiHost.BuildApp(state);
        app.Urls.Clear();
        app.Urls.Add($"http://{bind}");
        await app.StartAsync(cts.Token);
        await WaitHealthyAsync($"http://{bind}/api/v1/health");

        var client = new HttpClient { BaseAddress = new Uri($"http://{bind}") };
        return (client, root, config, store, new HostDisposable(cts, app, store, dir));
    }

    private sealed class HostDisposable(
        CancellationTokenSource cts,
        WebApplication app,
        Store store,
        string dir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await app.StopAsync(); } catch { /* ignore */ }
            try { await app.DisposeAsync(); } catch { /* ignore */ }
            store.Dispose();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitHealthyAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var resp = await client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // retry
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("ui host did not become healthy");
    }

    private static void Run(string cwd, string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
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

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static async Task SeedSessionAsync(Store store, string projectId, string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        async Task Append(EventType type, JsonObject payload)
        {
            var evt = new AgentEvent
            {
                Version = Protocol.Version,
                EventId = Protocol.NewEventId(),
                SessionId = sessionId,
                Timestamp = now,
                Agent = "cursor",
                EventType = type,
                Payload = payload,
            };
            await store.AppendEventAsync(evt, projectId);
            now = now.AddSeconds(1);
        }

        await Append(EventType.SessionStarted, new JsonObject());
        await Append(EventType.PromptSubmitted, new JsonObject { ["prompt"] = "add payment endpoint" });
        await Append(EventType.FileModified, new JsonObject { ["path"] = "src/Payments.cs" });
        await Append(EventType.AgentResponse, new JsonObject { ["text"] = "Added Payments.cs" });
        await Append(EventType.SessionEnded, new JsonObject());
        await WorkNodeIndex.ProjectSessionByIdAsync(store, sessionId);
    }

    [Fact]
    public async Task Health_ok()
    {
        var (client, _, config, _, host) = await SetupAsync();
        await using var _ = host;
        var response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.Equal(config.ProjectId, body["project_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sessions_lists_seeded_session()
    {
        var (client, _, config, store, host) = await SetupAsync();
        await using var _ = host;
        await SeedSessionAsync(store, config.ProjectId, "sess_ui_test");

        var response = await client.GetAsync("/api/v1/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.NotEmpty(body);
        Assert.Contains(body, n => n!["id"]!.GetValue<string>() == "sess_ui_test");

        var detail = await client.GetAsync("/api/v1/sessions/sess_ui_test");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var node = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!;
        Assert.Equal("sess_ui_test", node["id"]!.GetValue<string>());
        Assert.True(node["prompts"]!.AsArray().Count > 0);
        Assert.True(node["files"]!.AsArray().Count > 0);
        Assert.True(node["turns"]!.AsArray().Count > 0);
    }

    [Fact]
    public async Task Project_endpoint_returns_counts()
    {
        var (client, _, config, store, host) = await SetupAsync();
        await using var _ = host;
        await SeedSessionAsync(store, config.ProjectId, "sess_ui_proj");

        var response = await client.GetAsync("/api/v1/project");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(config.ProjectId, body["projectId"]!.GetValue<string>());
        Assert.True(body["sessionCount"]!.GetValue<int>() >= 1);
        Assert.Null(body["nodeCount"]);
    }

    [Fact]
    public async Task Status_returns_sessions_and_prompts()
    {
        var (client, _, config, store, host) = await SetupAsync();
        await using var _ = host;
        await SeedSessionAsync(store, config.ProjectId, "sess_ui_status");

        var response = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(config.ProjectId, body["projectId"]!.GetValue<string>());
        Assert.True(body["sessionCount"]!.GetValue<int>() >= 1);
        Assert.True(body["promptCount"]!.GetValue<int>() >= 1);
        Assert.Null(body["eventCount"]);
        Assert.Null(body["nodeCount"]);
    }
}
