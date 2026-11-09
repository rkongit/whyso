using System.Net;
using System.Text;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Daemon;

/// <summary>
/// Destination for validated events. The daemon writes to <see cref="LocalStoreSink"/> today;
/// <see cref="HttpRemoteSink"/> is the seam for a future self-hosted team collector.
/// </summary>
public interface IEventSink
{
    Task<AppendOutcome> AppendAsync(AgentEvent event_, CancellationToken cancellationToken = default);
}

/// <summary>Appends to the local store (redaction, encryption, JSONL + SQLite).</summary>
public sealed class LocalStoreSink(Store store, string projectId) : IEventSink
{
    public Task<AppendOutcome> AppendAsync(AgentEvent event_, CancellationToken cancellationToken = default) =>
        store.AppendEventAsync(event_, projectId);
}

/// <summary>
/// Forwards events to a remote collector's <c>POST /v1/events</c>. Payloads are redacted
/// before they leave the machine. Not wired into the daemon in this release.
/// </summary>
public sealed class HttpRemoteSink(Uri endpoint, HttpClient? client = null) : IEventSink
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<AppendOutcome> AppendAsync(AgentEvent event_, CancellationToken cancellationToken = default)
    {
        var outbound = AgentEvent.FromJson(event_.ToJson());
        outbound.Payload = Redactor.RedactNode(outbound.Payload) ?? new System.Text.Json.Nodes.JsonObject();

        using var content = new StringContent(outbound.ToJson(), Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"remote sink returned {(int)response.StatusCode}: {body}");
        }

        var stored = AgentEvent.FromJson(body);
        return response.StatusCode == HttpStatusCode.OK
            ? new AppendOutcome.Duplicate(stored)
            : new AppendOutcome.Inserted(stored);
    }
}
