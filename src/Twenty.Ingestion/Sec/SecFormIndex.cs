using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Twenty.Ingestion.Sec;

public sealed record SecFormIndexCount(string Form, int Year, int Filings);
public sealed record SecFormIndexFile(int Rows, DateOnly FirstFiled, DateOnly LastFiled,
    ImmutableArray<SecFormIndexCount> Delistings);
public sealed record SecFormIndexQuarter(int Year, int Quarter, string ContentSha256, DateTimeOffset RetrievedAt,
    int Rows, DateOnly FirstFiled, DateOnly LastFiled);
public sealed record SecFormIndexReport(string Version, DateOnly From, DateOnly To,
    ImmutableArray<SecFormIndexQuarter> Quarters, ImmutableArray<SecFormIndexCount> Counts,
    int TotalRows, int TotalDelistings, bool RequestLimitReached, ImmutableArray<string> Limitations);

/// <summary>Bounded EDGAR full-index reading. It counts delisting-form filings per year so a declared
/// sample can be compared with the population it is drawn from. It counts rows only: it never infers
/// that two forms describe one delisting, that a filing removed a security from trading, or that a row
/// is investable. The compressed quarterly index is requested because it is 4-5 MB where the plain
/// index is 39-50 MB, and the response cap applies to the wire bytes.</summary>
public static partial class SecFormIndex
{
    public const string Version = "sec-form-index-v1";
    /// <summary>A quarterly index inflates to about 47 MB; far beyond this is a corrupted or hostile file.</summary>
    public const long MaximumIndexBytes = 128L * 1024 * 1024;
    public static readonly ImmutableArray<string> DelistingForms = ["25", "25/A", "25-NSE", "25-NSE/A"];

    /// <summary>Reads one compressed quarterly index. Every data row must parse and lie inside the
    /// declared window: a row outside it means the wrong file or a corrupted one, and counting it
    /// would misstate the denominator.</summary>
    public static SecFormIndexFile Parse(byte[] compressed, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (compressed.Length is 0 or > 48 * 1024 * 1024 || from > to) throw new SecCollectionException("index-size-limit");
        byte[] plain;
        try { plain = Inflate(compressed, MaximumIndexBytes); }
        catch (InvalidDataException) { throw new SecCollectionException("index-shape"); }

        // Latin1 keeps byte and character counts aligned, so company names cannot shift a column.
        var lines = Encoding.Latin1.GetString(plain).Split('\n');
        var header = Array.FindIndex(lines, l => l.StartsWith("Form Type", StringComparison.Ordinal));
        var rule = header + 1 < lines.Length ? lines[header + 1].Trim() : "";
        if (header < 0 || rule.Length < 10 || rule.Any(c => c != '-'))
            throw new SecCollectionException("index-shape");
        var counts = new Dictionary<(string Form, int Year), int>();
        var rows = 0;
        var first = DateOnly.MaxValue;
        var last = DateOnly.MinValue;
        for (var i = header + 2; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Row().Match(line);
            if (!match.Success || match.Groups["form"].Value.Length is 0 or > 40 ||
                !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var filed))
                throw new SecCollectionException("index-row");
            if (filed < from || filed > to) throw new SecCollectionException("index-date-range");
            var form = match.Groups["form"].Value;
            rows++;
            if (filed < first) first = filed;
            if (filed > last) last = filed;
            if (!DelistingForms.Contains(form)) continue;
            counts[(form, filed.Year)] = counts.GetValueOrDefault((form, filed.Year)) + 1;
        }
        if (rows == 0) throw new SecCollectionException("index-shape");
        return new(rows, first, last, Counts(counts));
    }

    /// <summary>Decompresses a gzip member, refusing to inflate past the bound. A quarterly index is
    /// about 47 MB uncompressed; far beyond that is a corrupted or hostile file, and buffering it
    /// would be the damage.</summary>
    public static byte[] Inflate(byte[] compressed, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (maximumBytes < 1024) throw new SecCollectionException("index-size-limit");
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var count = gzip.Read(block, 0, block.Length);
            if (count == 0) break;
            output.Write(block, 0, count);
            if (output.Length > maximumBytes) throw new SecCollectionException("index-size-limit");
        }
        return output.ToArray();
    }

    public static async Task<SecFormIndexReport> Collect(SecClient client, int fromYear, int toYear,
        ISecClock? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = (clock ?? SecSystemClock.Instance).UtcNow;
        if (fromYear is < 1994 or > 2100 || toYear is < 1994 or > 2100 || fromYear > toYear || toYear > now.Year)
            throw new SecCollectionException("invalid-index-window");
        var quarters = ImmutableArray.CreateBuilder<SecFormIndexQuarter>();
        var totals = new Dictionary<(string Form, int Year), int>();
        var totalRows = 0;
        var limited = false;
        for (var year = fromYear; year <= toYear && !limited; year++)
        {
            for (var quarter = 1; quarter <= 4; quarter++)
            {
                // A quarter that has not started is not missing data; do not fetch it or flag it.
                if (year == now.Year && quarter > (now.Month - 1) / 3 + 1) break;
                ct.ThrowIfCancellationRequested();
                var window = Quarter(year, quarter);
                SecPayload payload;
                try
                {
                    payload = await client.Fetch(SecIdentity.FullIndex(year, quarter), json: false,
                        (bytes, _, _) => { _ = Parse(bytes, window.From, window.To); }, ct);
                }
                catch (SecCollectionException error) when (error.Code == "request-budget-exhausted")
                { limited = true; break; }
                var file = Parse(payload.Bytes, window.From, window.To);
                foreach (var count in file.Delistings)
                    totals[(count.Form, count.Year)] = totals.GetValueOrDefault((count.Form, count.Year)) + count.Filings;
                totalRows += file.Rows;
                quarters.Add(new(year, quarter, payload.Receipt.ContentSha256, payload.Receipt.FirstSeenAt,
                    file.Rows, file.FirstFiled, file.LastFiled));
            }
        }
        if (quarters.Count == 0) throw new SecCollectionException("index-shape");
        return new(Version, new DateOnly(fromYear, 1, 1), new DateOnly(toYear, 12, 31), quarters.ToImmutable(),
            Counts(totals), totalRows, totals.Values.Sum(), limited,
            ["These are filing counts, not delisting counts: one exit can carry both a Form 25 and a Form 25-NSE, and an amendment does not add an exit. Nothing here infers which.",
             "A filing date is the date the form was filed, not the date trading stopped or the date proceeds were received.",
             "This is a population count over the quarters actually fetched. It says nothing about whether any declared sample is representative of it.",
             "Rows are counted exactly as the index writes the form type; no form family is merged or normalized."]);
    }

    private static (DateOnly From, DateOnly To) Quarter(int year, int quarter)
    {
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        var to = quarter == 4 ? new DateOnly(year, 12, 31) : new DateOnly(year, quarter * 3 + 1, 1).AddDays(-1);
        return (from, to);
    }

    private static ImmutableArray<SecFormIndexCount> Counts(Dictionary<(string Form, int Year), int> counts) =>
        [.. counts.OrderBy(c => c.Key.Year).ThenBy(c => c.Key.Form, StringComparer.Ordinal)
            .Select(c => new SecFormIndexCount(c.Key.Form, c.Key.Year, c.Value))];

    // The header's column labels do not align with the data columns, and company names hold spaces,
    // so the row is anchored on its unambiguous tail: CIK, filing date and an edgar/ path.
    [GeneratedRegex(@"^(?<form>.*?)\s{2,}.*?(?<cik>[0-9]+)\s+(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\s+(?<file>edgar/\S+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Row();
}
