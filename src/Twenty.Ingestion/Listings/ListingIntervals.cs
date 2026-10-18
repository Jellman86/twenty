using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Listings;

public sealed record ListingSnapshotRow(string Symbol, string Exchange, string AssetType, string? Name,
    string? PriceCurrency, DateOnly? ProducerStart, DateOnly? ProducerEnd);
public sealed record ListingSnapshot(ImmutableArray<ListingSnapshotRow> Rows, int TestIssuesExcluded, string? ProducerCreation,
    int ExactDuplicatesDeduped = 0, int SegmentRowsCollapsed = 0);
public sealed record TimedSnapshot(DateTimeOffset ObservedAt, ListingSnapshot Snapshot);
public sealed record ListingGap(DateTimeOffset From, DateTimeOffset To);
public sealed record ListingInterval(string SourceId, string Symbol, string Exchange, string AssetType, string? PriceCurrency,
    string? Name, DateOnly? ProducerStart, DateOnly? ProducerEnd,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, DateTimeOffset? ClosedAbsentAt,
    int SnapshotsPresent, string Status, bool NameChanged, bool ProducerDatesChanged);
public sealed record ListingIntervalFiles(string Id, byte[] Csv, byte[] Audit, int Intervals);

/// <summary>
/// Turns daily listing-directory captures into observed listing intervals. Presence in a snapshot is evidence
/// the symbol was listed when we captured it; absence from a later snapshot closes an interval but is never
/// relabelled as a delisting effective date, and an unobserved gap longer than
/// <see cref="MaximumContinuityGap"/> breaks continuity rather than claiming the listing persisted unseen.
/// Intervals are per source: Nasdaq and Tiingo identities are never merged without a reconciliation contract.
/// </summary>
public static class ListingIntervals
{
    public const string Version = "listing-intervals-v1";
    public const string Restriction = "Observed-presence intervals from captured listing directories. First/last seen are capture times, not listing or delisting effective dates; absence, symbol reuse, venue changes and producer revisions are not resolved into security identity. Not a certified point-in-time universe and not broker eligibility evidence.";
    public static readonly TimeSpan MaximumContinuityGap = TimeSpan.FromDays(7);
    public static readonly ImmutableArray<string> SupportedSources = ["nasdaq-listed", "nasdaq-other", "tiingo-tickers"];
    private const int MaximumRowsPerSnapshot = 200_000;
    private const int MaximumSnapshotsPerSource = 10_000;
    private const int MaximumIntervals = 2_000_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ListingSnapshot ParseNasdaqListed(byte[] bytes) => ParseNasdaq(bytes, other: false);
    public static ListingSnapshot ParseNasdaqOther(byte[] bytes) => ParseNasdaq(bytes, other: true);

    private static ListingSnapshot ParseNasdaq(byte[] bytes, bool other)
    {
        var header = other
            ? "ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot Size|Test Issue|NASDAQ Symbol"
            : "Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares";
        var lines = Lines(bytes);
        if (lines.Length < 3 || lines[0] != header) throw new ResearchException("listing-header");
        var trailer = lines[^1];
        if (!trailer.StartsWith("File Creation Time:", StringComparison.Ordinal)) throw new ResearchException("listing-trailer-missing");
        var creation = trailer["File Creation Time:".Length..].Split('|')[0].Trim();
        if (!DateTime.TryParseExact(creation, "MMddyyyyHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ResearchException("listing-trailer-shape");
        var rows = ImmutableArray.CreateBuilder<ListingSnapshotRow>();
        var seen = new HashSet<(string, string)>();
        var testIssues = 0;
        for (var i = 1; i < lines.Length - 1; i++)
        {
            if (rows.Count + testIssues >= MaximumRowsPerSnapshot) throw new ResearchException("listing-row-limit");
            var cells = lines[i].Split('|');
            if (cells.Length != 8) throw new ResearchException("listing-row-shape");
            var (symbol, name, exchange, test, etf, nextShares) = other
                ? (cells[0], cells[1], cells[2], cells[6], cells[4], "N")
                : (cells[0], cells[1], "NASDAQ", cells[3], cells[6], cells[7]);
            if (!Flag(test) || !Flag(etf) || !Flag(nextShares)) throw new ResearchException("listing-row-flag");
            if (!other && cells[2] is not ("Q" or "G" or "S")) throw new ResearchException("listing-market-category");
            if (other && (exchange.Length != 1 || !char.IsAsciiLetterUpper(exchange[0]))) throw new ResearchException("listing-exchange-code");
            name = name.Trim(); // Producer padding is not part of a security name; identity fields stay untrimmed and strict.
            CheckText(symbol, 32, "listing-symbol"); CheckText(name, 256, "listing-name");
            if (test == "Y") { testIssues++; continue; }
            if (!seen.Add((exchange, symbol))) throw new ResearchException("listing-snapshot-duplicate");
            rows.Add(new(symbol, exchange, etf == "Y" ? "etf" : nextShares == "Y" ? "nextshares" : "stock", name, null, null, null));
        }
        if (rows.Count == 0) throw new ResearchException("listing-empty");
        return new(rows.ToImmutable(), testIssues, creation);
    }

    public static ListingSnapshot ParseTiingoTickers(byte[] zipBytes)
    {
        if (zipBytes.Length is 0 or > 64 * 1024 * 1024) throw new ResearchException("listing-zip-size");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var entries = zip.Entries.Where(e => string.Equals(e.FullName, "supported_tickers.csv", StringComparison.Ordinal)).ToArray();
            if (entries.Length != 1 || entries[0].Length is <= 0 or > 64L * 1024 * 1024) throw new ResearchException("listing-zip-entry");
            using var input = entries[0].Open(); using var content = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer)) > 0)
            { if (content.Length + count > 64L * 1024 * 1024) throw new ResearchException("listing-zip-entry-size"); content.Write(buffer, 0, count); }
            var lines = Lines(content.ToArray());
            if (lines.Length < 2 || lines[0] != "ticker,exchange,assetType,priceCurrency,startDate,endDate") throw new ResearchException("listing-header");
            var rows = ImmutableArray.CreateBuilder<ListingSnapshotRow>();
            // Tiingo lists history segments: one ticker can recur on an exchange over disjoint producer date
            // ranges (symbol reuse), so segments stay separate rows. The genuine file also repeats a few
            // rows byte-identically; those carry no extra information, so they are deduped and counted
            // in the audit rather than silently kept or used to reject the whole directory.
            var seen = new HashSet<ListingSnapshotRow>();
            var deduped = 0;
            for (var i = 1; i < lines.Length; i++)
            {
                if (rows.Count >= MaximumRowsPerSnapshot) throw new ResearchException("listing-row-limit");
                var cells = CsvCells(lines[i]);
                if (cells.Length != 6) throw new ResearchException("listing-row-shape");
                var (symbol, exchange, assetType, currency) = (cells[0].Trim(), cells[1].Trim(), cells[2].Trim(), cells[3].Trim());
                CheckText(symbol, 32, "listing-symbol");
                if (exchange.Length > 16 || exchange.Any(char.IsControl)) throw new ResearchException("listing-exchange");
                CheckText(assetType, 32, "listing-asset-type");
                if (currency.Length > 0 && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))) throw new ResearchException("listing-currency");
                var start = ProducerDate(cells[4]); var end = ProducerDate(cells[5]);
                if (start is { } s && end is { } e && e < s) throw new ResearchException("listing-producer-dates");
                var row = new ListingSnapshotRow(symbol, exchange, assetType, null, currency.Length == 0 ? null : currency, start, end);
                if (!seen.Add(row)) { deduped++; continue; }
                rows.Add(row);
            }
            if (rows.Count == 0) throw new ResearchException("listing-empty");
            // The genuine file also repeats a segment within one snapshot differing only by end date
            // (a one-day predecessor row beside the continuing row). The continuing row subsumes it:
            // keep the latest end for the segment and count the collapse. Rows that also differ by
            // asset type or currency stay separate segments; nothing is merged across those fields.
            var segments = new Dictionary<(string, string, DateOnly?, string, string?), ListingSnapshotRow>();
            foreach (var row in rows)
            {
                var key = (row.Exchange, row.Symbol, row.ProducerStart, row.AssetType, row.PriceCurrency);
                if (!segments.TryGetValue(key, out var kept) || (row.ProducerEnd ?? DateOnly.MaxValue) > (kept.ProducerEnd ?? DateOnly.MaxValue))
                    segments[key] = row;
            }
            var collapsed = rows.Count - segments.Count;
            return new([.. segments.Values.OrderBy(r => r.Exchange, StringComparer.Ordinal).ThenBy(r => r.Symbol, StringComparer.Ordinal)
                .ThenBy(r => r.ProducerStart)], 0, null, deduped, collapsed);
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException)
        { throw new ResearchException("listing-file-invalid"); }
    }

    private static DateOnly? ProducerDate(string cell)
    {
        var value = cell.Trim();
        if (value.Length == 0) return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ResearchException("listing-producer-dates");
        return date;
    }
    private static bool Flag(string value) => value is "Y" or "N";
    private static void CheckText(string value, int maximum, string code)
    {
        if (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl) || value != value.Trim())
            throw new ResearchException(code);
    }
    private static string[] Lines(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new ResearchException("listing-file-invalid"); }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--;
        if (end != lines.Length) Array.Resize(ref lines, end);
        if (lines.Any(l => l.Length == 0)) throw new ResearchException("listing-row-shape");
        return lines;
    }
    private static string[] CsvCells(string line)
    {
        var cells = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        if (quoted) throw new ResearchException("listing-row-shape");
        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    private sealed class Open(ListingSnapshotRow row, DateTimeOffset at)
    {
        public ListingSnapshotRow Row = row;
        public DateTimeOffset First = at, Last = at;
        public int Count = 1;
        public bool NameChanged, DatesChanged;
    }

    /// <summary>Builds one source's intervals. Input is re-sorted; a repeated capture instant is rejected (Build dedupes identical replays first).</summary>
    public static (ImmutableArray<ListingInterval> Intervals, ImmutableArray<ListingGap> Gaps) IntervalsFor(
        string sourceId, IEnumerable<TimedSnapshot> snapshots)
    {
        var ordered = snapshots.OrderBy(s => s.ObservedAt).ToArray();
        if (ordered.Length is 0 or > MaximumSnapshotsPerSource) throw new ResearchException("listing-snapshot-count");
        var done = ImmutableArray.CreateBuilder<ListingInterval>();
        var gaps = ImmutableArray.CreateBuilder<ListingGap>();
        // Producer start date, asset type and currency are part of segment identity: a reused ticker's
        // later segment never merges into the earlier one, and a revised start/type/currency visibly
        // closes and opens segments instead of rewriting the earlier interval.
        var open = new Dictionary<(string Exchange, string Symbol, DateOnly? ProducerStart, string AssetType, string? Currency), Open>();
        void Close((string, string, DateOnly?, string, string?) key, Open value, string status, DateTimeOffset? absentAt)
        {
            if (done.Count >= MaximumIntervals) throw new ResearchException("listing-interval-limit");
            done.Add(new(sourceId, key.Item2, key.Item1, value.Row.AssetType, value.Row.PriceCurrency, value.Row.Name,
                value.Row.ProducerStart, value.Row.ProducerEnd, value.First, value.Last, absentAt, value.Count, status,
                value.NameChanged, value.DatesChanged));
        }
        DateTimeOffset? previousAt = null;
        foreach (var (at, snapshot) in ordered)
        {
            if (previousAt is { } prior)
            {
                if (at == prior) throw new ResearchException("listing-snapshot-conflict");
                if (at - prior > MaximumContinuityGap)
                {
                    gaps.Add(new(prior, at));
                    foreach (var (key, value) in open) Close(key, value, "continuity-gap", null);
                    open.Clear();
                }
            }
            previousAt = at;
            var present = new HashSet<(string, string, DateOnly?, string, string?)>();
            foreach (var row in snapshot.Rows)
            {
                var key = (row.Exchange, row.Symbol, row.ProducerStart, row.AssetType, row.PriceCurrency);
                if (!present.Add(key)) throw new ResearchException("listing-snapshot-duplicate");
                if (!open.TryGetValue(key, out var value)) { open[key] = new(row, at); continue; }
                value.NameChanged |= value.Row.Name != row.Name;
                // A producer end date that only extends is coverage growing; shrinkage or appearing/vanishing is a revision.
                value.DatesChanged |= (value.Row.ProducerEnd is null) != (row.ProducerEnd is null)
                    || value.Row.ProducerEnd is { } oldEnd && row.ProducerEnd is { } newEnd && newEnd < oldEnd;
                value.Row = row; value.Last = at; value.Count++;
            }
            foreach (var key in open.Keys.Where(k => !present.Contains(k)).ToArray())
            { Close(key, open[key], "absent-from-later-snapshot", at); open.Remove(key); }
        }
        foreach (var (key, value) in open) Close(key, value, "present-at-final-snapshot", null);
        return (done.OrderBy(i => i.Exchange, StringComparer.Ordinal).ThenBy(i => i.Symbol, StringComparer.Ordinal)
            .ThenBy(i => i.FirstSeen).ToImmutableArray(), gaps.ToImmutable());
    }

    public static ListingSnapshot Parse(string sourceId, byte[] bytes) => sourceId switch
    {
        "nasdaq-listed" => ParseNasdaqListed(bytes),
        "nasdaq-other" => ParseNasdaqOther(bytes),
        "tiingo-tickers" => ParseTiingoTickers(bytes),
        _ => throw new ResearchException("listing-source-unsupported")
    };

    /// <summary>Builds the immutable interval artifact from every captured snapshot of the named sources.</summary>
    public static ListingIntervalFiles Build(CaptureStore store, ImmutableArray<string> sourceIds)
    {
        if (sourceIds.Length == 0 || sourceIds.Distinct().Count() != sourceIds.Length ||
            sourceIds.Any(id => !SupportedSources.Contains(id))) throw new ResearchException("listing-source-unsupported");
        var intervals = ImmutableArray.CreateBuilder<ListingInterval>();
        var auditSources = new List<object>();
        foreach (var sourceId in sourceIds.Order(StringComparer.Ordinal))
        {
            var source = CaptureSources.Find(sourceId);
            var observations = store.Observations(sourceId);
            if (observations.Count == 0) throw new ResearchException("listing-capture-missing");
            // Replays of one observation and same-instant duplicates of identical bytes are one snapshot;
            // the same instant naming different bytes is ambiguous and must not be ordered by guesswork.
            var instants = observations.GroupBy(o => o.ObservedAt).OrderBy(g => g.Key).ToArray();
            var parsed = new Dictionary<string, ListingSnapshot>();
            var timed = new List<TimedSnapshot>();
            var snapshotAudit = new List<object>();
            foreach (var group in instants)
            {
                var hashes = group.Select(o => o.Sha256).Distinct().ToArray();
                if (hashes.Length != 1) throw new ResearchException("listing-snapshot-conflict");
                var observation = group.First();
                if (!parsed.TryGetValue(hashes[0], out var snapshot))
                {
                    var captured = store.Bytes(hashes[0]);
                    if (source.Validate(captured) is not null) throw new ResearchException("listing-capture-invalid");
                    snapshot = parsed[hashes[0]] = Parse(sourceId, captured);
                }
                timed.Add(new(observation.ObservedAt, snapshot));
                snapshotAudit.Add(new
                {
                    observationId = observation.Id, observedAt = observation.ObservedAt, sha256 = hashes[0],
                    collector = observation.Collector, notModified = observation.NotModified,
                    rows = snapshot.Rows.Length, testIssuesExcluded = snapshot.TestIssuesExcluded,
                    exactDuplicatesDeduped = snapshot.ExactDuplicatesDeduped,
                    segmentRowsCollapsed = snapshot.SegmentRowsCollapsed,
                    producerCreation = snapshot.ProducerCreation
                });
            }
            var (sourceIntervals, gaps) = IntervalsFor(sourceId, timed);
            intervals.AddRange(sourceIntervals);
            auditSources.Add(new
            {
                sourceId, snapshots = timed.Count, observations = observations.Count,
                intervals = sourceIntervals.Length,
                presentAtFinalSnapshot = sourceIntervals.Count(i => i.Status == "present-at-final-snapshot"),
                gaps = gaps.Select(g => new { from = g.From, to = g.To, days = (g.To - g.From).TotalDays }),
                captures = snapshotAudit
            });
        }
        var ordered = intervals.OrderBy(i => i.SourceId, StringComparer.Ordinal).ThenBy(i => i.Exchange, StringComparer.Ordinal)
            .ThenBy(i => i.Symbol, StringComparer.Ordinal).ThenBy(i => i.FirstSeen).ToImmutableArray();
        var csv = new StringBuilder("source,symbol,exchange,asset_type,price_currency,security_name,producer_start_date,producer_end_date,first_seen_utc,last_seen_utc,closed_absent_at_utc,snapshots_present,status,name_changed,producer_dates_changed\n");
        foreach (var i in ordered)
            csv.AppendJoin(',', Csv(i.SourceId), Csv(i.Symbol), Csv(i.Exchange), Csv(i.AssetType), Csv(i.PriceCurrency ?? ""), Csv(i.Name ?? ""),
                i.ProducerStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", i.ProducerEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                Stamp(i.FirstSeen), Stamp(i.LastSeen), i.ClosedAbsentAt is { } closed ? Stamp(closed) : "",
                i.SnapshotsPresent.ToString(CultureInfo.InvariantCulture), i.Status,
                i.NameChanged ? "true" : "false", i.ProducerDatesChanged ? "true" : "false").Append('\n');
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var id = Hash(Encoding.UTF8.GetBytes(Version + "\n").Concat(bytes).ToArray());
        var audit = JsonSerializer.SerializeToUtf8Bytes(new
        {
            converter = Version, intervalId = id, csvSha256 = Hash(bytes), intervals = ordered.Length,
            maximumContinuityGapDays = MaximumContinuityGap.TotalDays, sources = auditSources,
            semantics = "first_seen/last_seen are Twenty capture times. closed_absent_at is the first later snapshot without the symbol, not a delisting date. A gap over the continuity limit closes intervals as continuity-gap and reopens them as new intervals. Intervals are keyed by (exchange, symbol, producer startDate, assetType, priceCurrency) so reused tickers stay separate segments; producer end dates come from the latest snapshot, extension counts as coverage growth, and shrinkage or a revised key field is left visible. Within one Tiingo snapshot, byte-identical rows are deduped and a segment repeated with only an earlier end date is collapsed to its latest end; both are counted per capture.",
            limitations = new[]
            {
                Restriction,
                "Nasdaq other-listed exchange codes are recorded raw and undecoded; Tiingo exchange and asset-type strings are producer values. The same company across sources, symbol changes and symbol reuse after delisting are unresolved.",
                "Coverage begins at the first capture: a symbol present in the first snapshot may have listed long before, and history before collection starts cannot be backfilled from these files."
            }
        }, Json);
        return new(id, bytes, audit, ordered.Length);
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
