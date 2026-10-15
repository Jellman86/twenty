using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twenty.Core.Data.Upstream;

// Wire DTOs retain upstream names and optionality; they are never accepted as domain observations.
public sealed record Trading212InstrumentDto(string? Ticker, string? Isin, string? CurrencyCode,
    string? Type, string? AddedOn, long? WorkingScheduleId, decimal? MaxOpenQuantity);
public sealed record EodPriceDto(string? Date, decimal? Open, decimal? High, decimal? Low,
    decimal? Close, [property: JsonPropertyName("adjusted_close")] decimal? AdjustedClose, decimal? Volume);
public sealed record SecRecentFilingsDto(ImmutableArray<string?> AccessionNumber,
    ImmutableArray<string?> AcceptanceDateTime, ImmutableArray<string?> FilingDate,
    ImmutableArray<string?> ReportDate, ImmutableArray<string?> Form, ImmutableArray<string?> PrimaryDocument)
{
    public DataValidation ValidateShape()
    {
        int[] lengths = [AccessionNumber.IsDefault ? -1 : AccessionNumber.Length,
            AcceptanceDateTime.IsDefault ? -1 : AcceptanceDateTime.Length,
            FilingDate.IsDefault ? -1 : FilingDate.Length, ReportDate.IsDefault ? -1 : ReportDate.Length,
            Form.IsDefault ? -1 : Form.Length, PrimaryDocument.IsDefault ? -1 : PrimaryDocument.Length];
        if (lengths.Any(x => x < 0) || lengths.Distinct().Count() != 1)
            return new([new("upstream-shape", "filings.recent", "Required SEC arrays are missing or have unequal lengths; quarantine the payload.")]);
        if (AccessionNumber.Any(string.IsNullOrWhiteSpace) || AcceptanceDateTime.Any(string.IsNullOrWhiteSpace) ||
            Form.Any(string.IsNullOrWhiteSpace) || PrimaryDocument.Any(string.IsNullOrWhiteSpace))
            return new([new("upstream-shape", "filings.recent", "Required filing identity/timing/document values are missing; quarantine the payload.")]);
        return new([]);
    }
}
public static class UpstreamJson
{
    // Unknown upstream fields are retained as raw private originals by a future collector.
    // Unlike our versioned dataset file, upstream additions must not silently become domain defaults.
    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    });
}
