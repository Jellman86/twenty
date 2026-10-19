using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Ingestion.Listings;

public sealed record ListingPricesExclusion(string Symbol, string Reason);
public sealed record ListingPricesResult(DatasetFile Dataset, int BarsAdded, int ActionsAdded,
    int OutsideIntervalSkipped, ImmutableArray<ListingPricesExclusion> Excluded);

/// <summary>
/// Converts captured Tiingo daily histories into v1 PriceBar observations (raw and
/// total-return-adjusted bases) plus split/dividend CorporateAction observations over a
/// validated two-source identity dataset. Availability is capture time: a current-vintage
/// vendor history becomes *effective* history but can never be presented as point-in-time
/// knowledge from before it was captured, however old its session dates are. The latest
/// capture per symbol wins, mirroring the audit's capture selection; a listing whose capture
/// is absent, whose adjusted fields disagree on one factor, or whose rows all fall outside
/// its active listing interval is excluded with a recorded reason and dropped together with
/// its identity rows — an untrusted or unknowable price never reaches a panel. Rows outside
/// the identity interval are counted, not converted; session closes are 16:00 in the
/// listing's exchange timezone so the session date survives DST. Conversion is a pure,
/// deterministic function of the identity dataset and the store's captures, and re-converting
/// an already-priced dataset is refused rather than silently revised (a rebuild supersedes by
/// dataset id instead).</summary>
public static class ListingPrices
{
    public const string Version = "listing-prices-v1";
    public const string SourceId = "tiingo-history";
    public const string SourceLocation = "urn:twenty:capture:tiingo-history";
    /// <summary>Relative tolerance between the adjustment factor implied by adjClose and the
    /// factors implied by adjOpen/adjHigh/adjLow on the same row.</summary>
    public const decimal AdjustmentTolerance = 0.000001m;

    public static ListingPricesResult Convert(CaptureStore store, DatasetFile identity, string datasetId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(datasetId)) throw new ResearchException("price-conversion-dataset-id");
        if (identity.Observations.OfType<PriceBar>().Any()) throw new ResearchException("price-conversion-duplicate");
        if (identity.Manifest.Sources.Any(s => s.Id == SourceId)) throw new ResearchException("price-conversion-source-conflict");
        var input = DatasetValidator.Validate(identity);
        if (!input.IsValid) throw new InvalidDatasetException(input);
        var calendars = identity.Manifest.Calendars.ToDictionary(c => c.Id, c => c.TimeZone, StringComparer.Ordinal);
        var listings = identity.Observations.OfType<Listing>().GroupBy(l => l.ListingId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
        if (listings.Count == 0) throw new ResearchException("price-conversion-empty");
        // v1 identities carry no revisions; accepting them here would silently merge
        // superseded intervals. Refuse rather than guess.
        foreach (var group in listings.Values)
        {
            if (group.Any(l => l.Evidence.RevisionOf is not null)) throw new ResearchException("price-conversion-revision");
            if (group.Select(l => (l.Symbol, string.Join(",", l.ProviderIds.Select(p => p.Namespace + "=" + p.Value)), l.CalendarId, l.Denomination)).Distinct().Count() != 1)
                throw new ResearchException("price-conversion-listing-conflict");
        }

        var excluded = ImmutableArray.CreateBuilder<ListingPricesExclusion>();
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        var bars = ImmutableArray.CreateBuilder<PriceBar>();
        var actions = ImmutableArray.CreateBuilder<CorporateAction>();
        var usedCaptures = new Dictionary<string, CaptureObservation>(StringComparer.Ordinal);
        var outside = 0; var barCount = 0; var actionCount = 0;
        foreach (var listingId in listings.Keys.Order(StringComparer.Ordinal))
        {
            var rows = listings[listingId];
            var symbol = rows[0].Symbol;
            var ticker = rows[0].ProviderIds.FirstOrDefault(p => p.Namespace == "tiingo.ticker")?.Value ?? symbol;
            var historySource = "tiingo-history-" + ticker.ToLowerInvariant().Replace('.', '-');
            var captures = store.Observations(historySource);
            if (captures.Count == 0)
            { excluded.Add(new(symbol, "history-not-captured")); excludedIds.Add(listingId); continue; }
            var latestAt = captures.Max(o => o.ObservedAt);
            var latest = captures.Where(o => o.ObservedAt == latestAt).ToArray();
            if (latest.Select(o => o.Sha256).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new ResearchException("price-conversion-capture-conflict");
            var capture = latest.OrderBy(o => o.Id, StringComparer.Ordinal).First();
            // v1 only converts NYSE-timezone listings; any other calendar is outside its scope.
            if (calendars[rows[0].CalendarId] != "America/New_York")
                throw new ResearchException("price-conversion-calendar");
            // Latest capture wins, but only if it still covers every date an earlier capture
            // carried: a narrower follow-up fetch must not silently drop older history.
            if (captures.Count > 1)
            {
                var parsed = captures.ToDictionary(c => c, c => TiingoClient.ParseHistory(store.Bytes(c.Sha256)));
                var earliestFirst = parsed.Values.Where(h => h.Length > 0).Min(h => h[0].Date);
                var latestHistory = parsed[capture];
                if (latestHistory.Length == 0 || latestHistory[0].Date > earliestFirst)
                    throw new ResearchException("price-conversion-vintage-narrower");
            }
            var history = TiingoClient.ParseHistory(store.Bytes(capture.Sha256));
            if (history.Any(b => !AdjustmentConsistent(b)))
            { excluded.Add(new(symbol, "adjustment-inconsistent")); excludedIds.Add(listingId); continue; }
            var evidence = new Evidence(SourceId, capture.ObservedAt, capture.ObservedAt,
                AvailabilityEvidence.ObservedPublication, TimePrecision.Second, null, null);
            var active = rows.Where(l => l.Status == ListingStatus.Active)
                .Select(l => (From: DateOnly.FromDateTime(l.EffectiveFrom.UtcDateTime),
                    Until: l.EffectiveUntil is null ? (DateOnly?)null : DateOnly.FromDateTime(l.EffectiveUntil.Value.UtcDateTime)))
                .ToArray();
            var calendar = TimeZoneInfo.FindSystemTimeZoneById(calendars[rows[0].CalendarId]);
            var listingBars = 0;
            foreach (var bar in history)
            {
                if (!active.Any(iv => iv.From <= bar.Date && (iv.Until is null || bar.Date < iv.Until)))
                { outside++; continue; }
                var closeAt = SessionClose(bar.Date, calendar);
                var day = bar.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                bars.Add(new($"{listingId}-raw-{day}", evidence, listingId, bar.Date, closeAt,
                    rows[0].Denomination, PriceBasis.Raw, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                bars.Add(new($"{listingId}-tra-{day}", evidence, listingId, bar.Date, closeAt,
                    rows[0].Denomination, PriceBasis.TotalReturnAdjusted, bar.AdjOpen, bar.AdjHigh, bar.AdjLow, bar.AdjClose, bar.Volume));
                listingBars += 2;
                if (bar.DivCash > 0)
                {
                    actions.Add(new($"{listingId}-div-{day}", evidence, listingId, $"{listingId}-div-{day}",
                        CorporateActionKind.Dividend, closeAt, null, bar.DivCash, rows[0].Denomination.Currency,
                        null, null, TerminalOutcome.NotApplicable));
                    actionCount++;
                }
                if (bar.SplitFactor != 1m)
                {
                    actions.Add(new($"{listingId}-split-{day}", evidence, listingId, $"{listingId}-split-{day}",
                        CorporateActionKind.Split, closeAt, bar.SplitFactor, null, null, null, null,
                        TerminalOutcome.NotApplicable));
                    actionCount++;
                }
            }
            if (listingBars == 0)
            { excluded.Add(new(symbol, "no-prices-in-interval")); excludedIds.Add(listingId); continue; }
            barCount += listingBars;
            usedCaptures[historySource] = capture;
        }
        if (barCount == 0) throw new ResearchException("price-conversion-empty");

        var manifest = identity.Manifest;
        var maxCapture = usedCaptures.Values.Max(o => o.ObservedAt);
        var minCapture = usedCaptures.Values.Min(o => o.ObservedAt);
        if (createdAt < maxCapture) throw new ResearchException("price-conversion-created-at");
        var bundle = new StringBuilder();
        foreach (var source in usedCaptures.Keys.Order(StringComparer.Ordinal))
            bundle.Append(source).Append(':').Append(usedCaptures[source].Sha256).Append('\n');
        var historySha = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle.ToString())));
        var kept = identity.Observations.Where(o => o switch
        {
            Listing l => !excludedIds.Contains(l.ListingId),
            Membership m => !excludedIds.Contains(m.ListingId),
            PriceBar p => !excludedIds.Contains(p.ListingId),
            CorporateAction a => !excludedIds.Contains(a.ListingId),
            _ => true
        });
        var dataset = new DatasetFile(manifest with
        {
            DatasetId = datasetId,
            CreatedAt = createdAt,
            Features = [new FeatureDefinition("daily-prices", "v1",
                "Captured Tiingo daily OHLC on raw and total-return bases", "Available at capture time only")],
            Sources = manifest.Sources.Add(new SourceSnapshot(SourceId, SourceLocation,
                "Tiingo daily price histories as captured under the configured subscription; not redistributed",
                maxCapture, minCapture, historySha, Version, "Rebuild supersedes; prior datasets immutable"))
        }, kept.Concat(bars.ToImmutable()).Concat(actions.ToImmutable()).ToImmutableArray());
        var validation = DatasetValidator.Validate(dataset);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        return new(dataset, barCount, actionCount, outside,
            excluded.ToImmutable().OrderBy(x => x.Symbol, StringComparer.Ordinal).ToImmutableArray());
    }

    // Regular daily close at 16:00 local. Supported exchange calendars shift hours at
    // 01:00-03:00 local, so 16:00 is never ambiguous or skipped and the offset is unambiguous.
    private static DateTimeOffset SessionClose(DateOnly date, TimeZoneInfo calendar)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 16, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, calendar.GetUtcOffset(local));
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
}
