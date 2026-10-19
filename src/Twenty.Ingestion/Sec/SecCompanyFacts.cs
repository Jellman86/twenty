using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twenty.Core.Data;

namespace Twenty.Ingestion.Sec;

public sealed record SecFactsRequest(ImmutableArray<string> Concepts, ImmutableArray<string> Taxonomies, DateTimeOffset RetrievedAt);
public sealed record SecFactsResult(DatasetFile Dataset, int Companies, int FactsAdded, int FactsSkippedExisting, string BulkSha256);

/// <summary>
/// Offline mapping from an SEC Company Facts bulk archive (companyfacts.zip) into v1 FundamentalFact
/// observations for companies already identified in a base dataset by a sec-cik provider identifier.
/// No network access happens here: the bulk file is supplied by the caller, so downloading one still
/// requires the genuine monitored contact flow. Company Facts collapses XBRL dimensional contexts;
/// conflicting values for one accession/period therefore fail closed instead of guessing a context.
/// Filed dates are day-precision (no acceptance timestamp exists in this payload), so availability is
/// the conservative start of the day after filing. Conversions are append-only per fact series:
/// interleaving an older fact into an existing revision chain fails rather than rewriting history.
/// Amendment ancestry is never inferred; a later accession simply revises the fact by filed order.
/// </summary>
public static partial class SecCompanyFacts
{
    public const string ParserVersion = "sec-companyfacts-v1", SourceId = "sec-companyfacts-bulk", CikNamespace = "sec-cik";
    public const string AvailabilityPolicy =
        "sec-companyfacts-filed-date-v1: companyfacts reports filing at day precision without an acceptance timestamp; "
        + "a fact is published at 00:00 UTC on its filed date and available from 00:00 UTC the following day.";
    private const int MaximumEntries = 60000, MaximumPoints = 200000;
    private const long MaximumEntryBytes = 32L * 1024 * 1024;
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex ConceptPattern();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex TaxonomyPattern();
    [GeneratedRegex("^CIK[0-9]{10}\\.json$", RegexOptions.CultureInvariant)] private static partial Regex EntryPattern();
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();

    private sealed record Point(string SeriesConcept, string Unit, DateOnly? Start, DateOnly End, string Accession, DateOnly Filed, decimal Value);

    public static SecFactsResult Convert(DatasetFile input, Stream bulkZip, string bulkSha256, SecFactsRequest request)
    {
        if (!HashPattern().IsMatch(bulkSha256) || request.RetrievedAt == default ||
            request.Concepts.IsDefaultOrEmpty || request.Concepts.Length > 50 ||
            request.Concepts.Any(c => c is null || !ConceptPattern().IsMatch(c)) ||
            request.Concepts.Distinct().Count() != request.Concepts.Length ||
            request.Taxonomies.IsDefaultOrEmpty || request.Taxonomies.Length > 8 ||
            request.Taxonomies.Any(t => t is null || !TaxonomyPattern().IsMatch(t)) ||
            request.Taxonomies.Distinct().Count() != request.Taxonomies.Length)
            throw new SecCollectionException("sec-facts-request");
        if (!DatasetValidator.Validate(input).IsValid) throw new SecCollectionException("sec-facts-input-dataset");

        var byCik = new Dictionary<string, string>(StringComparer.Ordinal);
        var byCompany = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var listing in input.Observations.OfType<Listing>())
            foreach (var provider in listing.ProviderIds.Where(p => p.Namespace == CikNamespace))
            {
                var cik = SecIdentity.Cik(provider.Value);
                if (byCik.TryGetValue(cik, out var company) && company != listing.CompanyId ||
                    byCompany.TryGetValue(listing.CompanyId, out var existing) && existing != cik)
                    throw new SecCollectionException("sec-facts-company-mapping");
                byCik[cik] = listing.CompanyId; byCompany[listing.CompanyId] = cik;
            }
        if (byCik.Count == 0) throw new SecCollectionException("sec-facts-company-mapping");

        var pointsByCompany = new Dictionary<string, List<Point>>(StringComparer.Ordinal);
        var matched = 0;
        ReadBulk(bulkZip, byCik, request, pointsByCompany, ref matched);
        if (matched == 0) throw new SecCollectionException("sec-facts-entry-missing");

        var facts = ImmutableArray.CreateBuilder<Observation>();
        facts.AddRange(input.Observations);
        var added = 0; var skipped = 0;
        foreach (var (companyId, points) in pointsByCompany.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var deduped = Dedupe(points);
            foreach (var series in deduped.GroupBy(p => (p.SeriesConcept, p.Unit, p.Start, p.End)).OrderBy(g => g.Key.SeriesConcept, StringComparer.Ordinal))
            {
                var existing = input.Observations.OfType<FundamentalFact>()
                    .Where(f => f.CompanyId == companyId && f.Concept == series.Key.SeriesConcept && f.Unit == series.Key.Unit &&
                        f.PeriodStart == series.Key.Start && f.PeriodEnd == series.Key.End)
                    .OrderBy(f => f.Evidence.AvailableAt).ToArray();
                string? headId = existing.Length > 0 ? existing[^1].Id : null;
                var headAvailable = existing.Length > 0 ? existing[^1].Evidence.AvailableAt!.Value : DateTimeOffset.MinValue;
                foreach (var point in series.OrderBy(p => p.Filed).ThenBy(p => p.Accession, StringComparer.Ordinal))
                {
                    var published = new DateTimeOffset(point.Filed, TimeOnly.MinValue, TimeSpan.Zero);
                    var available = published.AddDays(1);
                    if (existing.Any(f => f.Accession == point.Accession && f.Value == point.Value && f.Evidence.AvailableAt == available))
                    { skipped++; continue; }
                    // Day precision cannot order two revisions filed the same day, and an older fact may not
                    // be interleaved into an existing chain; acceptance timestamps (submissions path) resolve both.
                    if (available == headAvailable) throw new SecCollectionException("sec-facts-same-day-revision");
                    if (available < headAvailable) throw new SecCollectionException("sec-facts-revision-interleave");
                    var id = FactId(companyId, point);
                    if (facts.Any(f => f.Id == id)) throw new SecCollectionException("sec-facts-duplicate-id");
                    facts.Add(new FundamentalFact(id,
                        new(SourceId, published, available, AvailabilityEvidence.ConservativePolicy, TimePrecision.Day, AvailabilityPolicy, headId),
                        companyId, point.Accession, point.SeriesConcept, point.Unit, point.Start, point.End, point.Value));
                    headId = id; headAvailable = available; added++;
                }
            }
        }

        var sources = ImmutableArray.CreateBuilder<SourceSnapshot>();
        sources.AddRange(input.Manifest.Sources);
        var prior = input.Manifest.Sources.FirstOrDefault(s => s.Id == SourceId);
        if (prior is null)
            sources.Add(new(SourceId, "https://www.sec.gov/Archives/edgar/daily-index/xbrl/companyfacts.zip",
                "SEC EDGAR public bulk data; verify current SEC terms before any redistribution; raw payload remains private outside Git",
                request.RetrievedAt, request.RetrievedAt, bulkSha256, ParserVersion,
                "Append-only conversion; day-precision conservative availability; superseding values chain by revisionOf"));
        else if (prior.ContentSha256 != bulkSha256) throw new SecCollectionException("sec-facts-source-conflict");
        var features = ImmutableArray.CreateBuilder<FeatureDefinition>();
        features.AddRange(input.Manifest.Features);
        if (!input.Manifest.Features.Any(f => f.Name == "sec-fundamentals"))
            features.Add(new("sec-fundamentals", "v1", "SEC companyfacts bulk values for declared taxonomy concepts",
                "Available from 00:00 UTC the day after the SEC filed date (day precision, conservative policy)"));
        var created = new[] { input.Manifest.CreatedAt, request.RetrievedAt }.Max();
        var merged = new DatasetFile(
            input.Manifest with { Sources = sources.ToImmutable(), Features = features.ToImmutable(), CreatedAt = created },
            facts.ToImmutable());
        var validation = DatasetValidator.Validate(merged);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        return new(merged, pointsByCompany.Count, added, skipped, bulkSha256);
    }

    private static List<Point> Dedupe(List<Point> points)
    {
        var byKey = new Dictionary<(string, string, DateOnly?, DateOnly, string), Point>();
        foreach (var point in points)
        {
            var key = (point.SeriesConcept, point.Unit, point.Start, point.End, point.Accession);
            if (byKey.TryGetValue(key, out var prior))
            {
                // Company Facts collapses XBRL dimensional contexts; identical repeats dedupe, conflicting
                // values for one accession/period are ambiguous and must not be guessed between.
                if (prior.Value != point.Value || prior.Filed != point.Filed)
                    throw new SecCollectionException("sec-facts-ambiguous-context");
                continue;
            }
            byKey[key] = point;
        }
        // The same value for one series filed the same day under several accessions is republication
        // (genuinely: an earnings 8-K and the 10-Q filed together), not a revision - day precision
        // cannot order them and does not need to. Keep the lowest accession deterministically.
        // Differing values filed the same day stay for the chain builder to reject.
        return byKey.Values
            .GroupBy(p => (p.SeriesConcept, p.Unit, p.Start, p.End, p.Filed))
            .SelectMany(group => group.Select(p => p.Value).Distinct().Count() == 1
                ? new[] { group.OrderBy(p => p.Accession, StringComparer.Ordinal).First() }.AsEnumerable()
                : group.AsEnumerable())
            .ToList();
    }

    private static void ReadBulk(Stream bulkZip, Dictionary<string, string> byCik, SecFactsRequest request,
        Dictionary<string, List<Point>> pointsByCompany, ref int matched)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(bulkZip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new SecCollectionException("sec-facts-zip"); }
        using (zip)
        {
            if (zip.Entries.Count is 0 or > MaximumEntries) throw new SecCollectionException("sec-facts-zip");
            var examined = 0;
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (!EntryPattern().IsMatch(entry.FullName)) throw new SecCollectionException("sec-facts-entry");
                var cik = entry.FullName[3..13];
                if (!byCik.TryGetValue(cik, out var companyId)) continue;
                matched++;
                if (pointsByCompany.ContainsKey(companyId)) throw new SecCollectionException("sec-facts-entry-duplicate");
                var bytes = ReadEntry(entry);
                var points = ParseCompany(bytes, cik, request, ref examined);
                pointsByCompany[companyId] = points;
            }
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length is 0 or > MaximumEntryBytes) throw new SecCollectionException("sec-facts-entry-size");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = stream.Read(block, 0, block.Length)) > 0)
        {
            buffer.Write(block, 0, read);
            if (buffer.Length > MaximumEntryBytes) throw new SecCollectionException("sec-facts-entry-size");
        }
        return buffer.ToArray();
    }

    private static List<Point> ParseCompany(byte[] bytes, string expectedCik, SecFactsRequest request, ref int examined)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new SecCollectionException("sec-facts-json"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("cik", out var cikValue) || cikValue.ValueKind != JsonValueKind.Number ||
                !cikValue.TryGetDecimal(out var cikNumber) ||
                !root.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Object)
                throw new SecCollectionException("sec-facts-json");
            string actualCik;
            try { actualCik = SecIdentity.Cik(cikNumber.ToString("0", CultureInfo.InvariantCulture)); }
            catch (SecCollectionException) { throw new SecCollectionException("sec-facts-cik-mismatch"); }
            if (actualCik != expectedCik) throw new SecCollectionException("sec-facts-cik-mismatch");
            var points = new List<Point>();
            foreach (var taxonomy in request.Taxonomies)
            {
                if (!facts.TryGetProperty(taxonomy, out var concepts) || concepts.ValueKind != JsonValueKind.Object) continue;
                foreach (var concept in request.Concepts)
                {
                    if (!concepts.TryGetProperty(concept, out var node) || node.ValueKind != JsonValueKind.Object ||
                        !node.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Object) continue;
                    foreach (var unit in units.EnumerateObject().OrderBy(u => u.Name, StringComparer.Ordinal))
                    {
                        if (unit.Value.ValueKind != JsonValueKind.Array) throw new SecCollectionException("sec-facts-point");
                        foreach (var item in unit.Value.EnumerateArray())
                        {
                            if (++examined > MaximumPoints) throw new SecCollectionException("sec-facts-limit");
                            points.Add(ParsePoint(item, taxonomy + ":" + concept, unit.Name, request.RetrievedAt));
                        }
                    }
                }
            }
            return points;
        }
    }

    private static Point ParsePoint(JsonElement item, string seriesConcept, string unit, DateTimeOffset retrievedAt)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new SecCollectionException("sec-facts-point");
        DateOnly Date(string name, bool required)
        {
            if (!item.TryGetProperty(name, out var value))
                return required ? throw new SecCollectionException("sec-facts-point") : default;
            if (value.ValueKind != JsonValueKind.String ||
                !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new SecCollectionException("sec-facts-point");
            return parsed;
        }
        var end = Date("end", true);
        var filed = Date("filed", true);
        var hasStart = item.TryGetProperty("start", out _);
        DateOnly? start = hasStart ? Date("start", true) : null;
        if (!item.TryGetProperty("val", out var val) || val.ValueKind != JsonValueKind.Number || !val.TryGetDecimal(out var value) ||
            !item.TryGetProperty("accn", out var accn) || accn.ValueKind != JsonValueKind.String ||
            start is { } s && s > end || filed < end ||
            new DateTimeOffset(filed, TimeOnly.MinValue, TimeSpan.Zero) > retrievedAt)
            throw new SecCollectionException("sec-facts-point");
        var accession = accn.GetString()!;
        try { SecIdentity.Accession(accession); }
        catch (SecCollectionException) { throw new SecCollectionException("sec-facts-point"); }
        return new(seriesConcept, unit, start, end, accession, filed, value);
    }

    private static string FactId(string companyId, Point point)
    {
        var material = string.Join("\n", ParserVersion, companyId, point.SeriesConcept, point.Unit,
            point.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "instant",
            point.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), point.Accession,
            point.Filed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), point.Value.ToString(CultureInfo.InvariantCulture));
        return "sec-fact-" + System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }
}
