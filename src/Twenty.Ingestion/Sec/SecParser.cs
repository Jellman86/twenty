using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twenty.Core.Data.Upstream;

namespace Twenty.Ingestion.Sec;

public static partial class SecParser
{
    public const string Version = "sec-submissions-v1";
    /// <summary>Periodic-report forms whose report period must already have ended by the filing date.</summary>
    public static readonly ImmutableArray<string> PeriodicForms = ["10-K", "10-K/A", "10-Q", "10-Q/A", "20-F", "20-F/A", "40-F", "40-F/A"];
    /// <summary>Listing-end evidence: 8-K Items 2.01/3.01, issuer/exchange Form 25 removals, merger proxies and
    /// deregistrations. These carry no report period; they are terminal-outcome review sources, not periodic filings.</summary>
    public static readonly ImmutableArray<string> TerminalForms = ["8-K", "8-K/A", "25", "25/A", "25-NSE", "25-NSE/A",
        "DEFM14A", "DEFM14C", "15-12B", "15-12G", "15-15D"];
    public static ImmutableArray<string> Forms(SecFormSet set) => set switch
    {
        SecFormSet.Periodic => PeriodicForms,
        SecFormSet.Terminal => TerminalForms,
        _ => throw new SecCollectionException("invalid-form-set")
    };
    [GeneratedRegex("^[A-Z0-9][A-Z0-9 /-]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex FormPattern();
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ZonedTime();
    public static SecSubmissionPage Parse(byte[] bytes, string cik, bool history, DateTimeOffset observedAt)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            Object(root);
            var expectedCik = SecIdentity.Cik(cik);
            if (!history)
            {
                var actual = Required(root, "cik");
                var value = actual.ValueKind == JsonValueKind.Number ? actual.GetRawText() : actual.GetString();
                if (value is null || SecIdentity.Cik(value) != expectedCik) throw new SecCollectionException("cik-mismatch");
            }
            var filings = history ? root : Required(root, "filings");
            Object(filings);
            var recent = history ? root : Required(filings, "recent");
            Object(recent);
            // Reuse the issue #3 parallel-array contract; never zip/truncate malformed arrays.
            var dto = UpstreamJson.Read<SecRecentFilingsDto>(recent.GetRawText());
            if (dto is null || !dto.ValidateShape().IsValid) throw new SecCollectionException("sec-array-shape");
            if (dto.AccessionNumber.Length > 50000) throw new SecCollectionException("sec-row-limit");
            var rows = ImmutableArray.CreateBuilder<SecFilingMetadata>();
            for (var i = 0; i < dto.AccessionNumber.Length; i++)
            {
                var accession = dto.AccessionNumber[i]!;
                SecIdentity.Accession(accession);
                var form = dto.Form[i]!;
                if (!FormPattern().IsMatch(form)) throw new SecCollectionException("invalid-form");
                var document = dto.PrimaryDocument[i] ?? "";
                // Blank document paths catalogue as-is: genuine paper-era rows have none.
                // Non-blank paths must still be constrained relative names for every row.
                if (!string.IsNullOrWhiteSpace(document)) SecIdentity.Document(document);
                else document = "";
                var time = dto.AcceptanceDateTime[i]!;
                if (!ZonedTime().IsMatch(time) || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var accepted) || accepted == default || accepted > observedAt)
                    throw new SecCollectionException("invalid-acceptance-time");
                var filingDate = Date(dto.FilingDate[i]);
                var period = string.IsNullOrEmpty(dto.ReportDate[i]) ? (DateOnly?)null : Date(dto.ReportDate[i]);
                // Only periodic reports assert an ended period. Other forms use reportDate differently
                // (genuine DEF 14A proxies carry a future meeting date); they catalogue as-is and are
                // never selected. Malformed non-blank dates still fail above for every form.
                if (period > filingDate && PeriodicForms.Contains(form)) throw new SecCollectionException("invalid-report-period");
                rows.Add(new(expectedCik, accession, form, filingDate, period, accepted.ToUniversalTime(), document));
            }
            var unique = Merge(rows);
            var files = ImmutableArray.CreateBuilder<SecHistoryFile>();
            if (!history)
            {
                var linked = Required(filings, "files");
                if (linked.ValueKind != JsonValueKind.Array || linked.GetArrayLength() > 100)
                    throw new SecCollectionException("invalid-history-shape");
                foreach (var file in linked.EnumerateArray())
                {
                    Object(file);
                    var name = Required(file, "name").GetString() ?? throw new SecCollectionException("invalid-history-link");
                    _ = SecIdentity.History(expectedCik, name);
                    var count = Required(file, "filingCount").GetInt32();
                    var from = Date(Required(file, "filingFrom").GetString());
                    var to = Date(Required(file, "filingTo").GetString());
                    if (count < 0 || from > to) throw new SecCollectionException("invalid-history-range");
                    files.Add(new(name, count, from, to));
                }
                if (files.Select(x => x.Name).Distinct().Count() != files.Count)
                    throw new SecCollectionException("duplicate-history-link");
            }
            return new(unique, files.ToImmutable());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SecCollectionException("sec-json-shape");
        }
    }
    public static ImmutableArray<SecFilingMetadata> Merge(IEnumerable<SecFilingMetadata> rows)
    {
        var result = ImmutableArray.CreateBuilder<SecFilingMetadata>();
        foreach (var group in rows.GroupBy(x => x.Accession))
        {
            var first = group.First();
            if (group.Any(x => x != first)) throw new SecCollectionException("conflicting-accession");
            result.Add(first);
        }
        return result.ToImmutable();
    }
    private static DateOnly Date(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date : throw new SecCollectionException("invalid-filing-date");
    private static JsonElement Required(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value : throw new SecCollectionException("sec-required-field");
    private static void Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count())
            throw new SecCollectionException("sec-object-shape");
    }
    public static void ValidateDocument(byte[] bytes, string contentType)
    {
        if (bytes.Length == 0 || contentType is not ("text/html" or "text/plain" or "application/xhtml+xml" or "application/xml" or "text/xml"))
            throw new SecCollectionException("sec-document-content");
        // Originals are stored as opaque bytes, never rendered/executed by the web application.
        var prefix = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        if (prefix.Contains("Undeclared Automated Tool", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("Your Request Originates from an Undeclared", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("Request Rate Threshold Exceeded", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("Access Denied", StringComparison.OrdinalIgnoreCase))
            throw new SecCollectionException("sec-block-page");
    }
}
