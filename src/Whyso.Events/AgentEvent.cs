using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Whyso.Events;

/// <summary>
/// Normalized agent event envelope.
/// <c>Sequence</c> is assigned by the Whyso daemon and may be absent on ingest.
/// </summary>
public sealed class AgentEvent
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = Protocol.Version;

    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Sequence { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "";

    [JsonPropertyName("event_type")]
    public EventType EventType { get; set; }

    [JsonPropertyName("payload")]
    public JsonNode Payload { get; set; } = new JsonObject();

    // v1.1 optional fields (team readiness). All omitted from JSON when null.

    /// <summary>Who produced the event: OS user and a stable, hashed machine id.</summary>
    [JsonPropertyName("actor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventActor? Actor { get; set; }

    [JsonPropertyName("org_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrgId { get; set; }

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; set; }

    /// <summary><c>private</c> (default), <c>team</c> or <c>audit</c>; see <see cref="Protocol.Visibilities"/>.</summary>
    [JsonPropertyName("visibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Visibility { get; set; }

    public static AgentEvent New(string sessionId, string agent, EventType eventType, JsonNode payload) =>
        new()
        {
            Version = Protocol.Version,
            EventId = Protocol.NewEventId(),
            SessionId = sessionId,
            Sequence = null,
            Timestamp = DateTimeOffset.UtcNow,
            Agent = agent,
            EventType = eventType,
            Payload = payload,
        };

    public void Validate() => Protocol.ValidateEnvelope(this);

    public string ToJson() => JsonSerializer.Serialize(this, EventJson.Options);

    public static AgentEvent FromJson(string json) =>
        JsonSerializer.Deserialize<AgentEvent>(json, EventJson.Options)
        ?? throw new JsonException("failed to deserialize AgentEvent");
}

public sealed class EventActor
{
    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("machine_id")]
    public string MachineId { get; set; } = "";

    /// <summary>The current OS user and a SHA-256-derived id of this machine (the hostname is not stored).</summary>
    public static EventActor Local()
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.MachineName.ToLowerInvariant()));
        return new EventActor
        {
            User = Environment.UserName,
            MachineId = "mach_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant(),
        };
    }
}

public static class EventJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new EventTypeJsonConverter());
        options.Converters.Add(new Rfc3339DateTimeOffsetConverter());
        return options;
    }
}
