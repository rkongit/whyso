using System.Text.Json;
using System.Text.Json.Nodes;
using Whyso.Events;

namespace Whyso.Events.Tests;

public class AgentEventTests
{
    private static AgentEvent SampleEvent() => new()
    {
        Version = Protocol.Version,
        EventId = "evt_123",
        SessionId = "sess_123",
        Sequence = 17,
        Timestamp = DateTimeOffset.Parse("2026-08-10T15:30:00Z"),
        Agent = "cursor",
        EventType = EventType.FileModified,
        Payload = JsonNode.Parse("""{"path":"src/auth/AuthService.java"}""")!,
    };

    [Fact]
    public void Serializes_to_expected_json()
    {
        var event_ = SampleEvent();
        using var doc = JsonDocument.Parse(event_.ToJson());
        var root = doc.RootElement;
        Assert.Equal(Protocol.Version, root.GetProperty("version").GetString());
        Assert.Equal("evt_123", root.GetProperty("event_id").GetString());
        Assert.Equal("sess_123", root.GetProperty("session_id").GetString());
        Assert.Equal(17UL, root.GetProperty("sequence").GetUInt64());
        Assert.Equal("2026-08-10T15:30:00Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("cursor", root.GetProperty("agent").GetString());
        Assert.Equal("file.modified", root.GetProperty("event_type").GetString());
        Assert.Equal("src/auth/AuthService.java", root.GetProperty("payload").GetProperty("path").GetString());
    }

    [Fact]
    public void Deserializes_from_json()
    {
        const string raw = """
            {
                "version": "1.0",
                "event_id": "evt_123",
                "session_id": "sess_123",
                "sequence": 17,
                "timestamp": "2026-08-10T15:30:00Z",
                "agent": "cursor",
                "event_type": "file.modified",
                "payload": { "path": "src/auth/AuthService.java" }
            }
            """;
        var event_ = AgentEvent.FromJson(raw);
        Assert.Equal(SampleEvent().EventId, event_.EventId);
        Assert.Equal(SampleEvent().EventType, event_.EventType);
        Assert.Equal(17UL, event_.Sequence);
    }

    [Fact]
    public void Omits_sequence_when_null()
    {
        var event_ = SampleEvent();
        event_.Sequence = null;
        using var doc = JsonDocument.Parse(event_.ToJson());
        Assert.False(doc.RootElement.TryGetProperty("sequence", out _));
    }

    [Fact]
    public void Validates_ok_for_well_formed_event()
    {
        SampleEvent().Validate();
    }

    [Fact]
    public void Rejects_bad_version_and_ids()
    {
        var event_ = SampleEvent();
        event_.Version = "2.0";
        Assert.Throws<ValidationError.UnsupportedVersion>(() => event_.Validate());

        event_ = SampleEvent();
        event_.EventId = "bad";
        Assert.Throws<ValidationError.InvalidEventId>(() => event_.Validate());

        event_ = SampleEvent();
        event_.SessionId = "sess_";
        Assert.Throws<ValidationError.InvalidSessionId>(() => event_.Validate());

        event_ = SampleEvent();
        event_.Agent = "  ";
        Assert.Throws<ValidationError.InvalidAgent>(() => event_.Validate());
    }

    [Fact]
    public void All_event_types_round_trip()
    {
        foreach (var eventType in EventTypeExtensions.All)
        {
            var event_ = new AgentEvent
            {
                Version = Protocol.Version,
                EventId = Protocol.NewEventId(),
                SessionId = Protocol.NewSessionId(),
                Sequence = null,
                Timestamp = DateTimeOffset.UtcNow,
                Agent = "cursor",
                EventType = eventType,
                Payload = new JsonObject(),
            };
            var encoded = event_.ToJson();
            var decoded = AgentEvent.FromJson(encoded);
            Assert.Equal(eventType, decoded.EventType);
            Assert.Equal(eventType, EventTypeExtensions.Parse(eventType.AsString()));
            Assert.False(string.IsNullOrEmpty(eventType.DisplayLabel()));
        }
    }

    [Fact]
    public void Session_id_from_external_prefixes_when_needed()
    {
        Assert.Equal("sess_abc-123", Protocol.SessionIdFromExternal("abc-123"));
        Assert.Equal("sess_abc-123", Protocol.SessionIdFromExternal("sess_abc-123"));
    }
}
