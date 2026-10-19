using System.Collections.Immutable;
using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Twenty.Core.Data;

namespace Twenty.Ingestion.Sec;

public sealed class SecCollectionException(string code) : Exception("SEC collection failed; inspect the safe error code.")
{
    public string Code { get; } = code;
}

public sealed record SecLimits
{
    public int MaximumRequests { get; init; } = 25;
    public int MaximumHistoryFiles { get; init; } = 3;
    public int MaximumDocuments { get; init; } = 5;
    // Measured genuine primary documents set this bound: Microsoft's 2026 10-K is
    // 8.19 MiB, ASML's 2025 20-F 23.71 MiB and its 2024 20-F 32.3 MiB (filing index
    // sizes, Oct 2026). 48 MiB covers the largest measured with headroom while one
    // buffered response stays well inside the archive cap below; larger documents
    // still fail response-size-limit.
    public int MaximumResponseBytes { get; init; } = 48 * 1024 * 1024;
    public long MaximumArchiveBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumQuarantineEntries { get; init; } = 20;
    public long MaximumQuarantineBytes { get; init; } = 16L * 1024 * 1024;
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public void Validate()
    {
        if (MaximumRequests is < 1 or > 100 || MaximumHistoryFiles is < 0 or > 10 ||
            MaximumDocuments is < 1 or > 20 || MaximumResponseBytes is < 1024 or > 48 * 1024 * 1024 ||
            MaximumArchiveBytes is < 1024 or > 1024L * 1024 * 1024 ||
            MaximumQuarantineEntries is < 1 or > 100 || MaximumQuarantineBytes is < 1024 or > 64L * 1024 * 1024 ||
            CacheLifetime < TimeSpan.Zero || CacheLifetime > TimeSpan.FromDays(7) ||
            RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(1) ||
            RunTimeout < RequestTimeout || RunTimeout > TimeSpan.FromMinutes(10))
            throw new SecCollectionException("invalid-limits");
    }
}

public static partial class SecIdentity
{
    [GeneratedRegex("^[0-9]{1,10}$", RegexOptions.CultureInvariant)] private static partial Regex CikPattern();
    [GeneratedRegex("^[0-9]{10}-[0-9]{2}-[0-9]{6}$", RegexOptions.CultureInvariant)] private static partial Regex AccessionPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,199}$", RegexOptions.CultureInvariant)] private static partial Regex DocumentPattern();
    public static string Cik(string value)
    {
        if (!CikPattern().IsMatch(value) || !ulong.TryParse(value, out var number) || number == 0)
            throw new SecCollectionException("invalid-cik");
        return number.ToString("D10", CultureInfo.InvariantCulture);
    }
    public static void Accession(string value)
    {
        if (!AccessionPattern().IsMatch(value)) throw new SecCollectionException("invalid-accession");
        // The accession prefix can be a filing agent's CIK, not the issuer's CIK.
    }
    public static void Document(string value)
    {
        // SEC non-target forms can use relative XSL paths (e.g. xslF345X05/ownership.xml).
        // Keep every segment constrained; absolute URLs, escapes and traversal are not filenames.
        // Segments must start alphanumeric, so "." and ".." segments are already unreachable:
        // a ".." substring inside one filename segment is genuine filer naming, not traversal
        // (e.g. "asml.holding.n.v..txt" in real 2013-2014 SC 13G rows).
        var segments = value.Split('/');
        if (value.Length > 400 || segments.Length > 4 ||
            segments.Any(segment => !DocumentPattern().IsMatch(segment)))
            throw new SecCollectionException("invalid-document-name");
    }
    public static string UserAgent(string contact)
    {
        if (contact.Length > 200 || contact.Any(char.IsWhiteSpace) || !MailAddress.TryCreate(contact, out var address) ||
            address.Address != contact || !address.Host.Contains('.') ||
            address.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            address.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            address.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase) ||
            address.Host.Equals("example.org", StringComparison.OrdinalIgnoreCase) ||
            address.Host.Equals("example.net", StringComparison.OrdinalIgnoreCase))
            throw new SecCollectionException("sec-contact-required");
        // Validation checks syntax, not that the mailbox is monitored. Never persist this value.
        return "Twenty/0.1 research " + contact;
    }
    public static Uri Submissions(string cik) => new("https://data.sec.gov/submissions/CIK" + Cik(cik) + ".json");
    public static Uri History(string cik, string file)
    {
        var prefix = "CIK" + Cik(cik) + "-submissions-";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal) ||
            file.Length != prefix.Length + 8 || !file.AsSpan(prefix.Length, 3).ToArray().All(char.IsAsciiDigit))
            throw new SecCollectionException("invalid-history-link");
        return new("https://data.sec.gov/submissions/" + file);
    }
    public static Uri PrimaryDocument(string cik, string accession, string document)
    {
        Accession(accession); Document(document);
        return new("https://www.sec.gov/Archives/edgar/data/" + ulong.Parse(Cik(cik), CultureInfo.InvariantCulture) +
            "/" + accession.Replace("-", "", StringComparison.Ordinal) + "/" + document);
    }
    public static void AllowedUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo != "" ||
            uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath.Contains('%') ||
            !(uri.Host == "data.sec.gov" && uri.AbsolutePath.StartsWith("/submissions/CIK", StringComparison.Ordinal) ||
              uri.Host == "www.sec.gov" && uri.AbsolutePath.StartsWith("/Archives/edgar/data/", StringComparison.Ordinal)))
            throw new SecCollectionException("off-origin-request");
    }
}

public sealed record SecFilingMetadata(string Cik, string Accession, string Form, DateOnly FilingDate,
    DateOnly? ReportDate, DateTimeOffset AcceptedAt, string PrimaryDocument)
{
    public bool IsAmendment => Form.EndsWith("/A", StringComparison.Ordinal);
}
public sealed record SecHistoryFile(string Name, int FilingCount, DateOnly From, DateOnly To);
public sealed record SecSubmissionPage(ImmutableArray<SecFilingMetadata> Filings, ImmutableArray<SecHistoryFile> History);
public sealed record SecReceipt(string Url, string ContentSha256, DateTimeOffset FirstSeenAt,
    string ContentType, string? ETag, DateTimeOffset? LastModified, string ParserVersion);
public sealed record SecCacheEntry(SecReceipt Receipt, DateTimeOffset CheckedAt);
public sealed record SecPayload(byte[] Bytes, SecReceipt Receipt);
public sealed record CollectedSecFiling(SecFilingMetadata Metadata, SecReceipt MetadataSource, SecReceipt DocumentSource)
{
    // Both metadata and the exact document bytes must have been observed. Never backdate a new fetch.
    public DateTimeOffset AvailableAt => MetadataSource.FirstSeenAt > DocumentSource.FirstSeenAt
        ? MetadataSource.FirstSeenAt : DocumentSource.FirstSeenAt;
    public Filing ToDomain(string companyId)
    {
        RequireDocumentProvenance();
        if (Metadata.ReportDate is null) throw new SecCollectionException("report-period-required");
        var id = "sec-" + Metadata.Cik + "-" + Metadata.Accession + "-" +
            MetadataSource.ContentSha256[..12] + "-" + DocumentSource.ContentSha256[..12];
        return new(id, new("sec-" + DocumentSource.ContentSha256, AvailableAt, AvailableAt,
            AvailabilityEvidence.ObservedPublication, TimePrecision.Second, "sec-first-observation-v1", null),
            companyId, Metadata.Accession, Metadata.Form, Metadata.ReportDate.Value, Metadata.AcceptedAt,
            DocumentSource.ContentSha256, null);
    }
    public SourceSnapshot DocumentProvenance()
    {
        RequireDocumentProvenance();
        return new("sec-" + DocumentSource.ContentSha256,
            DocumentSource.Url, "https://www.sec.gov/about/webmaster-frequently-asked-questions",
            DocumentSource.FirstSeenAt, DocumentSource.FirstSeenAt, DocumentSource.ContentSha256, SecParser.Version,
            "Preserve accession and original bytes; link verified corrections explicitly, never infer amendment ancestry.");
    }
    private void RequireDocumentProvenance()
    {
        // A later metadata correction cannot borrow an earlier document receipt's availability proof.
        // Keep it in the catalogue for a reviewed composite-source/revision import instead.
        if (MetadataSource.FirstSeenAt > DocumentSource.FirstSeenAt)
            throw new SecCollectionException("composite-provenance-required");
    }
}
public sealed record SecCollectionResult(string Cik, DateTimeOffset CompletedAt, ImmutableArray<CollectedSecFiling> Filings,
    int Requests, int HistoryFilesVisited, bool HistoryLimitReached, bool DocumentLimitReached, string ParserVersion)
{
    public ImmutableArray<CollectedSecFiling> KnownAt(DateTimeOffset decisionAt) => decisionAt > CompletedAt
        ? throw new SecCollectionException("future-decision")
        : Filings.Where(x => x.AvailableAt <= decisionAt).ToImmutableArray();
}
