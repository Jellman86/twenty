using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public static class RawReplayJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(RawReplayOperation))
            {
                info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "kind" };
                foreach (var (type, name) in new (Type, string)[]
                {
                    (typeof(RawReplayDeposit), "deposit"), (typeof(RawReplayAssumedFill), "assumedFill"),
                    (typeof(RawReplayModelledFill), "modelledFill"), (typeof(RawReplaySplit), "split"),
                    (typeof(RawReplayDividendCapture), "dividendCapture"), (typeof(RawReplayDividendPayment), "dividendPayment"),
                    (typeof(RawReplayTerminalCapture), "terminalCapture"), (typeof(RawReplayTerminalSettlement), "terminalSettlement"),
                    (typeof(RawReplayMark), "mark")
                }) info.PolymorphismOptions.DerivedTypes.Add(new(type, name));
            }
            if (info.Type == typeof(RawActionSchedule))
            {
                info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "kind" };
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(RawSplitSchedule), "split"));
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(RawDividendSchedule), "dividend"));
                info.PolymorphismOptions.DerivedTypes.Add(new(typeof(RawTerminalSchedule), "terminal"));
            }
            // Derived display values have no setter/constructor binding. Exclude them from
            // this wire format so a supplied value cannot be silently ignored on read.
            if (info.Type == typeof(FillPolicy) || info.Type == typeof(ExecutionFill))
                foreach (var property in info.Properties.Where(p => p.Name is "rate" or "slippageFromOpen").ToArray())
                    info.Properties.Remove(property);
        });
        var options = new JsonSerializerOptions(DatasetJson.Options) { TypeInfoResolver = resolver };
        options.Converters.Insert(0, new CalendarConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed record CalendarWire(CalendarReference Reference, string Venue, string SourceReference,
        ImmutableArray<ExecutionDay> Days, string Sha256);
    private sealed class CalendarConverter : JsonConverter<ExecutionCalendar>
    {
        public override ExecutionCalendar Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<CalendarWire>(ref reader, options) ?? throw new JsonException("Missing calendar.");
            var calendar = new ExecutionCalendar(wire.Reference, wire.Venue, wire.Days, wire.SourceReference);
            if (calendar.Sha256 != wire.Sha256) throw new ResearchException("raw-replay-calendar-hash");
            return calendar;
        }
        public override void Write(Utf8JsonWriter writer, ExecutionCalendar value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new CalendarWire(value.Reference, value.Venue, value.SourceReference, value.Days, value.Sha256), options);
    }

    internal static T Read<T>(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes) throw new ResearchException("raw-replay-size");
        var copy = bytes.ToArray();
        using var document = JsonDocument.Parse(copy, new() { MaxDepth = 32 });
        CheckDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(copy, Options) ?? throw new JsonException("Missing replay document.");
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate replay member.");
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    internal static byte[] Write<T>(T value, int maximumBytes)
    {
        using var stream = new BoundedStream(maximumBytes);
        JsonSerializer.Serialize(stream, value, Options);
        return stream.ToArray();
    }
    private sealed class BoundedStream(int maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maximumBytes) throw new ResearchException("raw-replay-size");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maximumBytes) throw new ResearchException("raw-replay-size");
            base.Write(buffer, offset, count);
        }
    }
}
