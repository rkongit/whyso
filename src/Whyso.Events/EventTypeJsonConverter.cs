using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whyso.Events;

public sealed class EventTypeJsonConverter : JsonConverter<EventType>
{
    public override EventType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString()
            ?? throw new JsonException("event_type must be a string");
        return EventTypeExtensions.Parse(value)
            ?? throw new JsonException($"unknown event_type: {value}");
    }

    public override void Write(Utf8JsonWriter writer, EventType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.AsString());
    }
}
