using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twenty.Core.Data;

public static class DatasetJson
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 32,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new ExplicitInstantConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static DatasetFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new JsonException("Dataset exceeds the input size limit.");
        return JsonSerializer.Deserialize<DatasetFile>(bytes, Options)
            ?? throw new JsonException("Dataset is absent.");
    }

    private sealed class ExplicitInstantConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            // Do not let a timestamp without an offset inherit the machine's local timezone.
            var hasOffset = text is { Length: >= 20 } && (text.EndsWith('Z') ||
                (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-'));
            if (!hasOffset || !reader.TryGetDateTimeOffset(out var value))
                throw new JsonException("Timestamp requires an explicit RFC 3339 offset.");
            return value.ToUniversalTime();
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime());
    }
}
