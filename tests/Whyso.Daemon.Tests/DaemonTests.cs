using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Daemon.Tests;

public class DaemonTests
{
    private static async Task<(HttpClient Client, string Root, Config Config, IAsyncDisposable Host)> SetupAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run(dir, "git", "init");
        Run(dir, "git", "config", "user.email", "test@example.com");
        Run(dir, "git", "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run(dir, "git", "add", "README.md");
        Run(dir, "git", "commit", "-m", "init");

        var (root, config) = await Store.InitProjectAsync(dir);
        var store = await Store.OpenAsync(root);
        var state = new AppState { Store = store, Config = config };

        var port = GetFreePort();
        var bind = $"127.0.0.1:{port}";
        var cts = new CancellationTokenSource();
        var app = DaemonHost.BuildApp(state);
        app.Urls.Clear();
        app.Urls.Add($"http://{bind}");
        await app.StartAsync(cts.Token);
        await WaitHealthyAsync($"http://{bind}/v1/health");

        var client = new HttpClient { BaseAddress = new Uri($"http://{bind}") };
        return (client, root, config, new HostLifetime(cts, app, store, dir));
    }

    private sealed class HostLifetime(
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

        throw new TimeoutException("daemon did not become healthy");
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

    private static string EventJsonBody()
    {
        var node = new JsonObject
        {
            ["version"] = Protocol.Version,
            ["event_id"] = Protocol.NewEventId(),
            ["session_id"] = "sess_daemon_test",
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["agent"] = "cursor",
            ["event_type"] = "file.modified",
            ["payload"] = new JsonObject { ["path"] = "src/main.rs" },
        };
        return node.ToJsonString();
    }

    [Fact]
    public async Task Post_event_persists_and_assigns_sequence()
    {
        var (client, _, _, host) = await SetupAsync();
        await using var _ = host;
        var response = await client.PostAsync(
            "/v1/events",
            new StringContent(EventJsonBody(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(1, body["sequence"]!.GetValue<int>());
        Assert.Equal("file.modified", body["event_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_payload_returns_400()
    {
        var (client, _, _, host) = await SetupAsync();
        await using var _ = host;
        var bad = JsonNode.Parse(EventJsonBody())!.AsObject();
        bad["event_id"] = "bad-id";
        var response = await client.PostAsync(
            "/v1/events",
            new StringContent(bad.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event_id", body);
    }

    [Fact]
    public async Task Duplicate_event_returns_200()
    {
        var (client, _, _, host) = await SetupAsync();
        await using var _ = host;
        var payload = EventJsonBody();
        var first = await client.PostAsync(
            "/v1/events",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsync(
            "/v1/events",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
        Assert.Equal(1, body["sequence"]!.GetValue<int>());
    }

    [Fact]
    public async Task Health_ok()
    {
        var (client, _, config, host) = await SetupAsync();
        await using var _ = host;
        var response = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.Equal(config.ProjectId, body["project_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Persistence_failure_returns_500()
    {
        var (client, root, _, host) = await SetupAsync();
        await using var _ = host;
        var events = Path.Combine(root, ".whyso", "events");
        Directory.Delete(events, true);
        File.WriteAllText(events, "not-a-directory");

        var response = await client.PostAsync(
            "/v1/events",
            new StringContent(EventJsonBody(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
