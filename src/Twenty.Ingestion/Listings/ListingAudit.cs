using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Ingestion.Listings;

public sealed record AdjustmentStep(DateOnly Date, string Kind, decimal SplitFactor, decimal DivCash,
    decimal ExpectedStep, decimal ObservedStep, decimal RelativeError);
public sealed record SymbolAudit(string Symbol, string TerminalClass, ImmutableArray<string> Findings,
    int IntervalRows, int TiingoSegments, DateOnly? ProducerEndLatest, bool NasdaqPresentAtFinal, bool NasdaqSourcesPresent,
    int HistoryRows, DateOnly? HistoryFirst, DateOnly? HistoryLast, DateTimeOffset? HistoryCapturedAt,
    string? HistoryCaptureId, string? HistorySha256, decimal? LastClose,
    int SplitEvents, int DividendEvents, int AdjustmentMismatchCount, int UnexplainedStepCount,
    ImmutableArray<AdjustmentStep> AdjustmentMismatches,
    ImmutableArray<AdjustmentStep> UnexplainedSteps, decimal MaximumStepRelativeError);
public sealed record ListingAuditFiles(string Id, byte[] Csv, byte[] Audit, ImmutableArray<SymbolAudit> Subjects);

/// <summary>
/// Cross-checks a declared symbol sample between observed listing intervals and captured Tiingo daily
/// histories: producer coverage ends versus last traded bars, directory disappearance versus continuing
/// prices, and whether split/dividend adjustment steps in adjClose match the recorded corporate actions.
/// Findings are evidence classifications only. A delisting candidate records its last captured close;
/// terminal shareholder proceeds are never inferred, and identity across renames or segment restarts is
/// not resolved. Everything is offline and fail-closed: the interval artifact must verify against its
/// hash-named id, and histories are re-parsed with capture-validation rules.
/// </summary>
public static partial class ListingAudit
{
    public const string Version = "listing-audit-v1";
    public const string Restriction = "Sampled cross-source consistency audit between listing-directory intervals and captured vendor price histories. Producer coverage dates and directory absence are claims, not audited listing/delisting effective dates; terminal proceeds, symbol-change identity and broker eligibility are unresolved. Not a certified point-in-time universe.";
    public const int MaximumSymbols = 50, ProducerEndToleranceDays = 10, CurrentWindowDays = 45, CaptureFreshnessDays = 10;
    public const decimal StepTolerance = 0.01m;
    private const int MaximumIntervalRows = 2_000_000, MaximumRecordedSteps = 100;
    private const string IntervalHeader = "source,symbol,exchange,asset_type,price_currency,security_name,producer_start_date,producer_end_date,first_seen_utc,last_seen_utc,closed_absent_at_utc,snapshots_present,status,name_changed,producer_dates_changed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [GeneratedRegex("^[A-Z][A-Z0-9.-]{0,19}$", RegexOptions.CultureInvariant)] private static partial Regex SymbolPattern();

    private sealed record IntervalRow(string Source, string Symbol, string Exchange, DateOnly? ProducerStart, DateOnly? ProducerEnd,
        DateTimeOffset LastSeen, string Status, bool NameChanged, bool ProducerDatesChanged);

    public static ListingAuditFiles Build(byte[] intervalCsv, string intervalId, CaptureStore tiingoStore, ImmutableArray<string> symbols)
    {
        var requested = NormalizeSymbols(symbols);
        if (intervalId.Length != 64 || intervalId.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            intervalId != Hash(Encoding.UTF8.GetBytes(ListingIntervals.Version + "\n").Concat(intervalCsv).ToArray()))
            throw new ResearchException("listing-audit-interval-integrity");
        var rows = ParseIntervals(intervalCsv);
        var evidenceAsOf = rows.Max(r => r.LastSeen);
        var evidenceDate = DateOnly.FromDateTime(evidenceAsOf.UtcDateTime);
        var nasdaqSources = rows.Any(r => r.Source is "nasdaq-listed" or "nasdaq-other");
        var subjects = ImmutableArray.CreateBuilder<SymbolAudit>();
        var subjectAudit = new List<object>();
        foreach (var symbol in requested)
        {
            var subject = AuditSymbol(symbol, rows.Where(r => r.Symbol == symbol).ToArray(), nasdaqSources, evidenceDate, tiingoStore);
            subjects.Add(subject);
            subjectAudit.Add(new
            {
                symbol, terminalClass = subject.TerminalClass, findings = subject.Findings,
                history = subject.HistoryRows == 0 ? null : new
                {
                    captureId = subject.HistoryCaptureId, sha256 = subject.HistorySha256, capturedAt = subject.HistoryCapturedAt,
                    rows = subject.HistoryRows, first = subject.HistoryFirst, last = subject.HistoryLast, lastClose = subject.LastClose,
                    splitEvents = subject.SplitEvents, dividendEvents = subject.DividendEvents,
                    adjustmentMismatchCount = subject.AdjustmentMismatchCount, unexplainedStepCount = subject.UnexplainedStepCount,
                    maximumStepRelativeError = subject.MaximumStepRelativeError,
                    mismatches = subject.AdjustmentMismatches, unexplainedSteps = subject.UnexplainedSteps
                }
            });
        }
        var built = subjects.ToImmutable();
        var csv = new StringBuilder("symbol,terminal_class,findings,history_rows,history_first,history_last,history_captured_utc,last_close,producer_end_latest,tiingo_segments,nasdaq_sources_present,nasdaq_present_at_final,split_events,dividend_events,adjustment_mismatches,unexplained_steps,max_step_relative_error\n");
        foreach (var s in built)
            csv.AppendJoin(',', Csv(s.Symbol), s.TerminalClass, Csv(string.Join(';', s.Findings)), s.HistoryRows.ToString(CultureInfo.InvariantCulture),
                Day(s.HistoryFirst), Day(s.HistoryLast), s.HistoryCapturedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? "",
                s.LastClose?.ToString(CultureInfo.InvariantCulture) ?? "", Day(s.ProducerEndLatest), s.TiingoSegments.ToString(CultureInfo.InvariantCulture),
                s.NasdaqSourcesPresent ? "true" : "false", s.NasdaqPresentAtFinal ? "true" : "false",
                s.SplitEvents.ToString(CultureInfo.InvariantCulture), s.DividendEvents.ToString(CultureInfo.InvariantCulture),
                s.AdjustmentMismatchCount.ToString(CultureInfo.InvariantCulture), s.UnexplainedStepCount.ToString(CultureInfo.InvariantCulture),
                s.MaximumStepRelativeError.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var identity = Encoding.UTF8.GetBytes(Version + "\n" + intervalId + "\n" + string.Join(',', requested) + "\n").Concat(bytes).ToArray();
        var id = Hash(identity);
        var audit = JsonSerializer.SerializeToUtf8Bytes(new
        {
            converter = Version, auditId = id, csvSha256 = Hash(bytes), intervalId, intervalConverter = ListingIntervals.Version,
            intervalRows = rows.Length, evidenceAsOfUtc = evidenceAsOf, symbolsRequested = requested,
            stepToleranceRelative = StepTolerance, producerEndToleranceDays = ProducerEndToleranceDays,
            currentWindowDays = CurrentWindowDays, captureFreshnessDays = CaptureFreshnessDays,
            findingCounts = built.SelectMany(s => s.Findings).GroupBy(f => f).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { finding = g.Key, symbols = g.Count() }),
            subjects = subjectAudit,
            semantics = "For each history date after the first, observedStep = (adjClose[t-1]/close[t-1]) / (adjClose[t]/close[t]). The expected step is (1/splitFactor) on split ex-dates times (1 - divCash/close[t-1]) on dividend ex-dates, else 1. A step is a mismatch (event date) or unexplained (non-event date) when |observed/expected - 1| exceeds the tolerance (absolute step deviation when the expected factor is degenerate). Step lists are capped at 100 recorded entries per symbol; the mismatch/unexplained counts are uncapped totals. Corporate-action events on the first captured bar are counted but cannot be step-checked. Producer-end comparisons use the latest Tiingo segment end for the symbol and the declared tolerance in calendar days. Terminal classes combine history freshness (last bar versus evidence date and versus its own capture time), producer-ended status and Nasdaq directory presence at the final snapshot; the delisting-candidate class additionally requires Nasdaq directory sources to exist in the artifact, otherwise the class stays history-ended-unresolved.",
            limitations = new[]
            {
                Restriction,
                "The audit judges only the declared symbols and only against histories already captured; an absent capture is a finding about Twenty's archive, not about the security. Adjustment checks verify internal consistency of one vendor's adjClose against its own splitFactor/divCash fields; they do not independently verify corporate actions against exchange notices.",
                "Same-day split and dividend events use the product of both factors with the dividend measured on the pre-split close; vendor convention differences there would appear as mismatches to inspect, not to auto-correct."
            }
        }, Json);
        return new(id, bytes, audit, built);
    }

    private static SymbolAudit AuditSymbol(string symbol, IntervalRow[] rows, bool nasdaqSources, DateOnly evidenceDate, CaptureStore store)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var tiingoRows = rows.Where(r => r.Source == "tiingo-tickers").ToArray();
        var producerEndLatest = tiingoRows.Select(r => r.ProducerEnd).Where(d => d is not null).Select(d => d!.Value)
            .DefaultIfEmpty().Max() is var maxEnd && tiingoRows.Any(r => r.ProducerEnd is not null) ? maxEnd : (DateOnly?)null;
        var nasdaqPresent = rows.Any(r => r.Source is "nasdaq-listed" or "nasdaq-other" && r.Status == "present-at-final-snapshot");
        if (rows.Length == 0) findings.Add("no-interval-rows");
        if (rows.Any(r => r.NameChanged)) findings.Add("name-changed");
        if (rows.Any(r => r.ProducerDatesChanged)) findings.Add("producer-dates-changed");
        if (tiingoRows.Select(r => (r.Exchange, r.ProducerStart)).Distinct().Count() > 1) findings.Add("segment-restart");
        if (rows.Any(r => r.Status == "absent-from-later-snapshot")) findings.Add("closed-absent-from-directory");
        if (!nasdaqSources) findings.Add("no-nasdaq-directory-evidence");

        var observations = TiingoHistoryIdentity.Observations(store, symbol);
        CaptureObservation? latest = null;
        var bars = ImmutableArray<TiingoBar>.Empty;
        if (observations.Count == 0) findings.Add("no-history-capture");
        else
        {
            var latestAt = observations.Max(o => o.ObservedAt);
            var atLatest = observations.Where(o => o.ObservedAt == latestAt).ToArray();
            if (atLatest.Select(o => o.Sha256).Distinct().Count() != 1) throw new ResearchException("listing-audit-capture-conflict");
            latest = atLatest[0];
            bars = TiingoClient.ParseHistory(store.Bytes(latest.Sha256));
            if (bars.Length == 0) findings.Add("no-history-capture");
        }
        if (bars.Length == 0)
            return new(symbol, "no-history-evidence", [.. findings], rows.Length, tiingoRows.Length, producerEndLatest,
                nasdaqPresent, nasdaqSources, 0, null, null, latest?.ObservedAt, latest?.Id, latest?.Sha256, null,
                0, 0, 0, 0, [], [], 0);

        var capturedDate = DateOnly.FromDateTime(latest!.ObservedAt.UtcDateTime);
        var last = bars[^1].Date;
        var staleCapture = capturedDate.DayNumber < evidenceDate.DayNumber - CurrentWindowDays && last.DayNumber >= capturedDate.DayNumber - CaptureFreshnessDays;
        if (staleCapture) findings.Add("history-capture-stale");
        var producerEnded = producerEndLatest is { } end && end.DayNumber < evidenceDate.DayNumber - ProducerEndToleranceDays;
        if (producerEndLatest is { } producerEnd)
        {
            // Continuing past the producer end is inconsistent within the capture itself, stale or not.
            // Ending before it only counts when the capture was taken after the producer end had passed.
            var diff = last.DayNumber - producerEnd.DayNumber;
            if (diff > ProducerEndToleranceDays) findings.Add("history-continues-past-producer-end");
            else if (diff < -ProducerEndToleranceDays && last.DayNumber < capturedDate.DayNumber - CaptureFreshnessDays) findings.Add("history-ends-before-producer-end");
        }
        var historyCurrent = last.DayNumber >= evidenceDate.DayNumber - CurrentWindowDays;
        if (producerEnded && !historyCurrent && !staleCapture) findings.Add("producer-ended-and-history-ended");
        if (historyCurrent && nasdaqSources && !nasdaqPresent && rows.Length > 0) findings.Add("history-continues-but-directory-absent");

        var mismatches = ImmutableArray.CreateBuilder<AdjustmentStep>();
        var unexplained = ImmutableArray.CreateBuilder<AdjustmentStep>();
        decimal maxError = 0; var mismatchCount = 0; var unexplainedCount = 0;
        // Events on the first bar are counted but have no prior bar to step-check against.
        var splits = bars.Count(b => b.SplitFactor != 1m);
        var dividends = bars.Count(b => b.DivCash > 0);
        for (var t = 1; t < bars.Length; t++)
        {
            var hasSplit = bars[t].SplitFactor != 1m; var hasDividend = bars[t].DivCash > 0;
            var expected = (hasSplit ? 1m / bars[t].SplitFactor : 1m) * (hasDividend ? 1m - bars[t].DivCash / bars[t - 1].Close : 1m);
            var observed = bars[t - 1].AdjClose / bars[t - 1].Close / (bars[t].AdjClose / bars[t].Close);
            // divCash >= prior close cannot form a valid adjustment factor; report it, never divide by it.
            if (expected <= 0) findings.Add("degenerate-corporate-action");
            var error = expected <= 0 ? Math.Abs(observed - expected) : Math.Abs(observed / expected - 1m);
            if (error > maxError) maxError = error;
            if (error <= StepTolerance) continue;
            var step = new AdjustmentStep(bars[t].Date,
                hasSplit && hasDividend ? "split-dividend" : hasSplit ? "split" : hasDividend ? "dividend" : "none",
                bars[t].SplitFactor, bars[t].DivCash, expected, observed, error);
            if (hasSplit || hasDividend) { mismatchCount++; if (mismatches.Count < MaximumRecordedSteps) mismatches.Add(step); }
            else { unexplainedCount++; if (unexplained.Count < MaximumRecordedSteps) unexplained.Add(step); }
        }
        if (mismatchCount > 0) findings.Add("adjustment-step-mismatch");
        if (unexplainedCount > 0) findings.Add("unexplained-adjustment-step");

        var terminal = historyCurrent ? "history-current"
            : staleCapture ? "history-capture-stale"
            // A delisting candidate needs evidence from a second source: without any Nasdaq directory
            // rows in the artifact, ended Tiingo coverage plus ended Tiingo history is one vendor twice.
            : producerEnded && nasdaqSources && !nasdaqPresent ? "delisting-candidate-proceeds-unresolved"
            : nasdaqPresent ? "history-ended-but-directory-present"
            : "history-ended-unresolved";
        return new(symbol, terminal, [.. findings], rows.Length, tiingoRows.Length, producerEndLatest,
            nasdaqPresent, nasdaqSources, bars.Length, bars[0].Date, last, latest.ObservedAt, latest.Id, latest.Sha256,
            bars[^1].Close, splits, dividends, mismatchCount, unexplainedCount, mismatches.ToImmutable(), unexplained.ToImmutable(), maxError);
    }

    private static ImmutableArray<string> NormalizeSymbols(ImmutableArray<string> symbols)
    {
        var normalized = symbols.Select(s => s?.Trim().ToUpperInvariant() ?? "").ToArray();
        if (normalized.Length is 0 or > MaximumSymbols || normalized.Any(s => !SymbolPattern().IsMatch(s)) ||
            normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ResearchException("listing-audit-symbols");
        return [.. normalized.Order(StringComparer.Ordinal)];
    }

    private static IntervalRow[] ParseIntervals(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new ResearchException("listing-audit-interval-shape"); }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--;
        if (end < 1 || lines[0] != IntervalHeader) throw new ResearchException("listing-audit-interval-header");
        if (end - 1 > MaximumIntervalRows) throw new ResearchException("listing-audit-interval-limit");
        var rows = new List<IntervalRow>();
        for (var i = 1; i < end; i++)
        {
            var cells = CsvCells(lines[i]);
            if (cells.Length != 15 || cells[0] is not ("nasdaq-listed" or "nasdaq-other" or "tiingo-tickers") ||
                cells[1].Length is 0 or > 32 || cells[12] is not ("present-at-final-snapshot" or "absent-from-later-snapshot" or "continuity-gap") ||
                !int.TryParse(cells[11], NumberStyles.None, CultureInfo.InvariantCulture, out var snapshots) || snapshots < 1 ||
                !DateTimeOffset.TryParseExact(cells[9], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastSeen) ||
                !DateTimeOffset.TryParseExact(cells[8], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstSeen) ||
                cells[10].Length > 0 && !DateTimeOffset.TryParseExact(cells[10], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                !TryFlag(cells[13], out var nameChanged) || !TryFlag(cells[14], out var datesChanged) ||
                !TryDate(cells[6], out var start) || !TryDate(cells[7], out var producerEnd) || firstSeen > lastSeen)
                throw new ResearchException("listing-audit-interval-row");
            rows.Add(new(cells[0], cells[1], cells[2], start, producerEnd, lastSeen, cells[12], nameChanged, datesChanged));
        }
        if (rows.Count == 0) throw new ResearchException("listing-audit-interval-row");
        return rows.ToArray();
    }
    private static bool TryFlag(string value, out bool flag) { flag = value == "true"; return value is "true" or "false"; }
    private static bool TryDate(string value, out DateOnly? date)
    {
        date = null;
        return value.Length == 0 || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && (date = parsed) is not null;
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
        if (quoted) throw new ResearchException("listing-audit-interval-row");
        cells.Add(cell.ToString());
        return cells.ToArray();
    }
    private static string Day(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
