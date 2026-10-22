using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Ingestion.Listings;

public sealed record ListingPricesExclusion(string Symbol, string Reason);
public sealed record ListingPricesResult(DatasetFile Dataset, int BarsAdded, int ActionsAdded,
    int OutsideIntervalSkipped, int InProgressSkipped, int TerminalRowSkipped,
    ImmutableArray<string> UnusedDispositions, ImmutableArray<ListingPricesExclusion> Excluded);

/// <summary>
/// Converts captured Tiingo daily histories into v1 PriceBar observations (raw and
/// total-return-adjusted bases) plus split/dividend CorporateAction observations over a
/// validated two-source identity dataset. Availability is capture time: a vendor's current
/// vintage becomes *effective* history but can never be presented as point-in-time knowledge
/// from before it was captured, however old its session dates are. Earlier vintages are
/// preserved as revisions rather than discarded: every capture that carried a session emits
/// a bar (and any dividend/split action) for it, available from that capture, with later
/// restatements linked through Evidence.RevisionOf on the (listing, session, basis) series
/// key. A listing whose capture is absent, whose adjusted fields disagree on one factor,
/// or whose rows all fall outside its active listing interval is excluded with a recorded
/// reason and dropped together with its identity rows. Rows outside the identity interval are
/// counted, not converted; session closes follow the reviewed Nasdaq regular/early-close table so
/// the session date survives DST. An optional terminal disposition report bounds the same history
/// with independently reviewed evidence: a vendor row the terminal audit refused as a non-session,
/// or any row dated after an accepted subject's reviewed final session, is counted and never
/// converted. Conversion is a pure, deterministic function of the identity dataset, the store's
/// captures and the supplied dispositions, and re-converting an already-priced dataset is refused
/// rather than silently revised (a rebuild supersedes by dataset id instead).</summary>
public static class ListingPrices
{
    public const string Version = "listing-prices-v5";
    public const string SourceId = "tiingo-history";
    public const string SourceLocation = "urn:twenty:capture:tiingo-history";
    public const string TerminalSourceId = "terminal-disposition";
    public const string TerminalSourceLocation = "urn:twenty:terminal-disposition";
    /// <summary>Relative tolerance between the adjustment factor implied by adjClose and the
    /// factors implied by adjOpen/adjHigh/adjLow on the same row.</summary>
    public const decimal AdjustmentTolerance = 0.000001m;

    public static ListingPricesResult Convert(CaptureStore store, DatasetFile identity, string datasetId, DateTimeOffset createdAt,
        TerminalDispositionReport? dispositions = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(datasetId)) throw new ResearchException("price-conversion-dataset-id");
        if (identity.Observations.OfType<PriceBar>().Any()) throw new ResearchException("price-conversion-duplicate");
        if (identity.Manifest.Sources.Any(s => s.Id is SourceId or TerminalSourceId)) throw new ResearchException("price-conversion-source-conflict");
        var terminalRules = TerminalRules(dispositions);
        var input = DatasetValidator.Validate(identity);
        if (!input.IsValid) throw new InvalidDatasetException(input);
        var calendars = identity.Manifest.Calendars.ToDictionary(c => c.Id, c => c.TimeZone, StringComparer.Ordinal);
        var listings = identity.Observations.OfType<Listing>().GroupBy(l => l.ListingId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
        if (listings.Count == 0) throw new ResearchException("price-conversion-empty");
        // v1 identities carry no revisions; accepting them here would silently merge superseded
        // intervals. Refuse rather than guess.
        foreach (var group in listings.Values)
        {
            if (group.Any(l => l.Evidence.RevisionOf is not null)) throw new ResearchException("price-conversion-revision");
            if (group.Select(l => (l.Symbol, string.Join(",", l.ProviderIds.Select(p => p.Namespace + "=" + p.Value)), l.CalendarId, l.Denomination)).Distinct().Count() != 1)
                throw new ResearchException("price-conversion-listing-conflict");
        }

        var excluded = ImmutableArray.CreateBuilder<ListingPricesExclusion>();
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedCalendars = new HashSet<string>(StringComparer.Ordinal);
        var bars = ImmutableArray.CreateBuilder<PriceBar>();
        var actions = ImmutableArray.CreateBuilder<CorporateAction>();
        var usedCaptureTimes = new List<DateTimeOffset>();
        var usedCaptures = new HashSet<CaptureObservation>();
        var outside = 0; var inProgress = 0; var terminalRows = 0; var barCount = 0; var actionCount = 0;
        var usedDispositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listingId in listings.Keys.Order(StringComparer.Ordinal))
        {
            var rows = listings[listingId];
            var symbol = rows[0].Symbol;
            var rule = ResolveTerminal(terminalRules, rows, usedDispositions);
            var listingTerminalRows = 0;
            var ticker = rows[0].ProviderIds.FirstOrDefault(p => p.Namespace == "tiingo.ticker")?.Value ?? symbol;
            var captures = TiingoHistoryIdentity.Observations(store, ticker).OrderBy(c => c.ObservedAt).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
            if (captures.Length == 0)
            { excluded.Add(new(symbol, "history-not-captured")); excludedIds.Add(listingId); continue; }
            // A timezone alone does not identify an exchange session calendar.
            if (rows[0].Venue != NasdaqSessions.Venue || calendars[rows[0].CalendarId] != NasdaqSessions.TimeZone)
                throw new ResearchException("price-conversion-calendar");
            // This converter is restricted to the USD-major identity contract; assigning a
            // different denomination would relabel vendor numbers without an FX conversion.
            if (rows[0].Denomination != new Denomination("USD", QuoteUnit.Major))
                throw new ResearchException("price-conversion-denomination");
            var vintages = captures.GroupBy(c => c.ObservedAt).ToArray();
            if (vintages.Any(g => g.Select(c => c.Sha256).Distinct(StringComparer.Ordinal).Count() != 1))
                throw new ResearchException("price-conversion-capture-conflict");
            // Retransmission at the same instant is one vintage, not a revision with a tie.
            captures = vintages.Select(g => g.First()).ToArray();
            // A later capture must not be narrower than an earlier one: that would silently
            // drop history rather than accumulate it.
            var parsed = captures.ToDictionary(c => c, c => TiingoClient.ParseHistory(store.Bytes(c.Sha256)));
            if (parsed.Values.Any(h => h.Any(b => !AdjustmentConsistent(b))))
            { excluded.Add(new(symbol, "adjustment-inconsistent")); excludedIds.Add(listingId); continue; }
            var previousDates = new HashSet<DateOnly>();
            foreach (var capture in captures)
            {
                var dates = parsed[capture].Select(b => b.Date).ToHashSet();
                if (dates.Count == 0 || !previousDates.IsSubsetOf(dates))
                    throw new ResearchException("price-conversion-vintage-narrower");
                previousDates = dates;
            }

            var active = rows.Where(l => l.Status == ListingStatus.Active)
                .Select(l => (From: DateOnly.FromDateTime(l.EffectiveFrom.UtcDateTime),
                    Until: l.EffectiveUntil is null ? (DateOnly?)null : DateOnly.FromDateTime(l.EffectiveUntil.Value.UtcDateTime)))
                .ToArray();
            var listingBars = 0;
            // Union of session dates across all captures: each capture that carried a session
            // emits a bar (and any action) for it, available from that capture.
            var allDates = parsed.Values.SelectMany(h => h.Select(b => b.Date)).Distinct().OrderBy(d => d).ToArray();
            foreach (var date in allDates)
            {
                // Terminal evidence outranks the identity interval: a row the audit refused as a
                // non-session, or any row after a reviewed final session, is never a bar however
                // the coverage-derived interval reads. Counted once per session date, like "outside".
                if (rule is not null && rule.Refuses(date)) { terminalRows++; listingTerminalRows++; continue; }
                if (!active.Any(iv => iv.From <= date && (iv.Until is null || date < iv.Until)))
                { outside++; continue; }
                // Count this row once for "outside" already handled above; emit per capture.
                var closeAt = NasdaqSessions.CloseAt(date)
                    ?? throw new ResearchException("price-conversion-non-session");
                var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                string? priorRawId = null, priorTraId = null;
                string? priorDivId = null, priorSplitId = null;
                var revisionIndex = 0;
                foreach (var capture in captures)
                {
                    var bar = parsed[capture].FirstOrDefault(b => b.Date == date);
                    if (bar is null) continue;
                    if (closeAt > capture.ObservedAt)
                    { inProgress++; continue; }
                    usedCaptureTimes.Add(capture.ObservedAt);
                    usedCaptures.Add(capture);
                    var evidence = new Evidence(SourceId, capture.ObservedAt, capture.ObservedAt,
                        AvailabilityEvidence.ObservedPublication, TimePrecision.Second, null, priorRawId);
                    string rawId, traId;
                    if (priorRawId is null)
                    {
                        rawId = $"{listingId}-raw-{day}";
                        traId = $"{listingId}-tra-{day}";
                    }
                    else
                    {
                        revisionIndex++;
                        rawId = $"{listingId}-raw-{day}-r{revisionIndex}";
                        traId = $"{listingId}-tra-{day}-r{revisionIndex}";
                    }
                    bars.Add(new(rawId, evidence, listingId, date, closeAt, rows[0].Denomination, PriceBasis.Raw,
                        bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                    var traEvidence = evidence with { RevisionOf = priorTraId };
                    bars.Add(new(traId, traEvidence, listingId, date, closeAt, rows[0].Denomination, PriceBasis.TotalReturnAdjusted,
                        bar.AdjOpen, bar.AdjHigh, bar.AdjLow, bar.AdjClose, bar.AdjVolume)); // adjusted basis carries split-adjusted volume
                    listingBars += 2;
                    priorRawId = rawId;
                    priorTraId = traId;
                    if (bar.DivCash > 0 || priorDivId is not null)
                    {
                        var divId = priorDivId is null ? $"{listingId}-div-{day}" : $"{listingId}-div-{day}-r{revisionIndex}";
                        actions.Add(new(divId, evidence with { RevisionOf = priorDivId }, listingId, $"{listingId}-div-{day}",
                            CorporateActionKind.Dividend, closeAt, null, bar.DivCash, rows[0].Denomination.Currency,
                            null, null, TerminalOutcome.NotApplicable));
                        actionCount++;
                        priorDivId = divId;
                    }
                    if (bar.SplitFactor != 1m || priorSplitId is not null)
                    {
                        var splitId = priorSplitId is null ? $"{listingId}-split-{day}" : $"{listingId}-split-{day}-r{revisionIndex}";
                        actions.Add(new(splitId, evidence with { RevisionOf = priorSplitId }, listingId, $"{listingId}-split-{day}",
                            CorporateActionKind.Split, closeAt, bar.SplitFactor, null, null, null, null,
                            TerminalOutcome.NotApplicable));
                        actionCount++;
                        priorSplitId = splitId;
                    }
                }
            }
            if (listingBars == 0)
            {
                excluded.Add(new(symbol, listingTerminalRows > 0 ? "terminal-rows-only" : "no-prices-in-interval"));
                excludedIds.Add(listingId); continue;
            }
            barCount += listingBars;
            usedCalendars.Add(rows[0].CalendarId);
        }
        if (barCount == 0) throw new ResearchException("price-conversion-empty");

        var manifest = identity.Manifest;
        var maxCapture = usedCaptureTimes.Count == 0 ? createdAt : usedCaptureTimes.Max();
        var minCapture = usedCaptureTimes.Count == 0 ? createdAt : usedCaptureTimes.Min();
        if (createdAt < maxCapture) throw new ResearchException("price-conversion-created-at");
        var bundle = new StringBuilder();
        foreach (var source in usedCaptures.OrderBy(c => c.SourceId, StringComparer.Ordinal).ThenBy(c => c.ObservedAt))
            bundle.Append(source.SourceId).Append(':').Append(source.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
                .Append(':').Append(source.Sha256).Append('\n');
        var historySha = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle.ToString())));
        var kept = identity.Observations.Where(o => o switch
        {
            Listing l => !excludedIds.Contains(l.ListingId),
            Membership m => !excludedIds.Contains(m.ListingId),
            PriceBar p => !excludedIds.Contains(p.ListingId),
            CorporateAction a => !excludedIds.Contains(a.ListingId),
            _ => true
        });
        var sources = manifest.Sources.Add(new SourceSnapshot(SourceId, SourceLocation,
            "Tiingo daily price histories as captured under the configured subscription; not redistributed",
            maxCapture, minCapture, historySha, Version, "Rebuild supersedes; prior datasets immutable"));
        if (dispositions is not null)
            sources = sources.Add(new SourceSnapshot(TerminalSourceId, TerminalSourceLocation,
                "Reviewed terminal dispositions and their private source documents; not redistributed",
                createdAt, null, TerminalReportHash(dispositions), TerminalDispositions.Version,
                "Rebuild supersedes; prior datasets immutable"));
        var dataset = new DatasetFile(manifest with
        {
            DatasetId = datasetId,
            CreatedAt = createdAt,
            Calendars = manifest.Calendars.Select(c => usedCalendars.Contains(c.Id) ? NasdaqSessions.Reference(c.Id) : c).ToImmutableArray(),
            Features = [new FeatureDefinition("daily-prices", "v1",
                "Captured Tiingo daily OHLC on raw and total-return bases", "Available at capture time only")],
            Sources = sources
        }, kept.Concat(bars.ToImmutable()).Concat(actions.ToImmutable()).ToImmutableArray());
        var validation = DatasetValidator.Validate(dataset);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        return new(dataset, barCount, actionCount, outside, inProgress, terminalRows,
            terminalRules.Keys.Except(usedDispositions, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray(),
            excluded.ToImmutable().OrderBy(x => x.Symbol, StringComparer.Ordinal).ToImmutableArray());
    }

    // Every adjusted field must imply the same factor as adjClose on a row; the audit checks
    // the steps between rows, conversion only checks that one row describes one adjustment.
    // The tolerance is applied to the relative deviation of each field's factor from adjClose's
    // so that heavily back-adjusted rows are judged on the same scale as unadjusted ones.
    private static bool AdjustmentConsistent(TiingoBar bar)
    {
        if (bar.AdjLow > bar.AdjOpen || bar.AdjOpen > bar.AdjHigh || bar.AdjLow > bar.AdjHigh) return false;
        var factor = bar.AdjClose / bar.Close;
        bool Same(decimal adjusted, decimal raw) => Math.Abs(adjusted / raw / factor - 1m) <= AdjustmentTolerance;
        return Same(bar.AdjOpen, bar.Open) && Same(bar.AdjHigh, bar.High) && Same(bar.AdjLow, bar.Low);
    }

    // Terminal evidence for one symbol: the reviewed final session (accepted subjects only) and the
    // vendor rows the terminal audit refused as non-sessions. Ticker matching is case-insensitive
    // because symbols are compared across artifacts; an ambiguous match is refused, never guessed.
    private sealed record TerminalRule(string Symbol, DateOnly? FinalSession, ImmutableHashSet<DateOnly> RefusedRows)
    {
        public bool Refuses(DateOnly date) => RefusedRows.Contains(date) || (FinalSession is { } final && date > final);
    }

    private static Dictionary<string, TerminalRule> TerminalRules(TerminalDispositionReport? dispositions)
    {
        var rules = new Dictionary<string, TerminalRule>(StringComparer.OrdinalIgnoreCase);
        if (dispositions is null) return rules;
        static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        if (dispositions.Version != TerminalDispositions.Version || dispositions.Subjects.IsDefault ||
            !Digest(dispositions.AuditReportSha256) || !Digest(dispositions.ReferenceSha256) ||
            dispositions.Subjects.Length > 50 || dispositions.Subjects.Any(s => s is null || string.IsNullOrWhiteSpace(s.Symbol) ||
            s.RefusedRows.IsDefault || s.Outcome is not ("accepted" or "refused") ||
            (s.Outcome == "accepted") != (s.FinalSession is not null)))
            throw new ResearchException("price-conversion-terminal-shape");
        if (dispositions.Accepted != dispositions.Subjects.Count(s => s.Outcome == "accepted") ||
            dispositions.Refused != dispositions.Subjects.Count(s => s.Outcome == "refused"))
            throw new ResearchException("price-conversion-terminal-shape");
        foreach (var subject in dispositions.Subjects)
        {
            // The audit's refused-row date list is capped, so a truncated list cannot bound history.
            if (subject.RefusedRowCount != subject.RefusedRows.Length)
                throw new ResearchException("price-conversion-terminal-truncated");
            if (!rules.TryAdd(subject.Symbol, new(subject.Symbol, subject.FinalSession, subject.RefusedRows.ToImmutableHashSet())))
                throw new ResearchException("price-conversion-terminal-conflict");
        }
        return rules;
    }

    private static TerminalRule? ResolveTerminal(Dictionary<string, TerminalRule> rules, ImmutableArray<Listing> rows, HashSet<string> used)
    {
        if (rules.Count == 0) return null;
        var names = rows.Select(r => r.Symbol)
            .Concat(rows.SelectMany(r => r.ProviderIds).Where(p => p is not null && p.Namespace == "tiingo.ticker").Select(p => p.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matches = rules.Where(r => names.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).Select(r => r.Value).ToArray();
        if (matches.Length > 1) throw new ResearchException("price-conversion-terminal-conflict");
        if (matches.Length == 0) return null;
        used.Add(matches[0].Symbol);
        return matches[0];
    }

    private static string TerminalReportHash(TerminalDispositionReport report) => TerminalDispositions.ContentHash(report);
}
