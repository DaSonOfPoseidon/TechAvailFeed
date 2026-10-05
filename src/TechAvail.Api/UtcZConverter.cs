using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechAvail.Api;

// FastAPI (pydantic) writes a UTC timestamp with "Z", System.Text.Json with "+00:00". Same instant;
// written like the Python API so clients see identical values.
public sealed class UtcZConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var format = value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff";
        var offset = value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture);
        writer.WriteStringValue(value.ToString(format, CultureInfo.InvariantCulture) + offset);
    }
}
