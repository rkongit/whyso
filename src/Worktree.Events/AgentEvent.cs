using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Worktree.Events;

/// <summary>
/// Normalized agent event envelope.
/// <c>Sequence</c> is assigned by the Worktree daemon and may be absent on ingest.
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
