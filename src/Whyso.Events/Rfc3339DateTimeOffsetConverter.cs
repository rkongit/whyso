using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whyso.Events;

/// <summary>Serialize DateTimeOffset as RFC3339 with a trailing Z (matching Rust chrono).</summary>
public sealed class Rfc3339DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString()
            ?? throw new JsonException("timestamp must be a string");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var utc = value.ToUniversalTime();
        // Omit fractional seconds when zero to match chrono's typical output.
        var formatted = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture).Replace("+00:00", "Z");
        writer.WriteStringValue(formatted);
    }
}
