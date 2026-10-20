using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Tiingo;

/// <summary>Bind history to the recorded request ticker, including legacy buckets that
/// collapsed dots into hyphens. Original captures remain immutable.</summary>
public static class TiingoHistoryIdentity
{
    public static string SourceId(string symbol)
    {
        var value = Normalize(symbol);
        return value.Contains('.') || value.Contains('-')
            ? "tiingo-history-v2-" + value.ToLowerInvariant().Replace("-", "-h").Replace(".", "-d")
            : "tiingo-history-" + value.ToLowerInvariant();
    }

    private static string LegacyId(string symbol) => "tiingo-history-" + symbol.ToLowerInvariant().Replace('.', '-');

    public static IReadOnlyList<CaptureObservation> Observations(CaptureStore store, string symbol,
        string invalidCode = "tiingo-history-provenance")
    {
        var target = Normalize(symbol);
        var result = new List<CaptureObservation>();
        foreach (var bucket in new[] { SourceId(target), LegacyId(target) }.Distinct(StringComparer.Ordinal))
            foreach (var capture in store.Observations(bucket))
            {
                if (capture.SourceId != bucket || RequestSymbol(capture) is not { } actual)
                    throw new ResearchException(invalidCode);
                // A valid capture for another ticker in a shared legacy bucket is not
                // evidence for the target. Invalid metadata never gets this exemption.
                if (actual == target) result.Add(capture);
            }
        return result;
    }

    public static bool Matches(CaptureObservation capture, string symbol) => RequestSymbol(capture) == Normalize(symbol);

    private static string? RequestSymbol(CaptureObservation capture)
    {
        if (!Uri.TryCreate(capture.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "api.tiingo.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 5 || parts[0] != "" || parts[1] != "tiingo" || parts[2] != "daily" || parts[4] != "prices") return null;
        var value = parts[3].ToUpperInvariant();
        if (!Valid(value) || capture.SourceId != SourceId(value) && capture.SourceId != LegacyId(value)) return null;
        return value;
    }

    private static string Normalize(string symbol)
    {
        var value = symbol?.ToUpperInvariant() ?? "";
        return Valid(value) ? value : throw new ResearchException("tiingo-history-provenance");
    }

    private static bool Valid(string value) => value.Length is >= 1 and <= 20 && char.IsAsciiLetterUpper(value[0]) &&
        value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '.' or '-');
}
