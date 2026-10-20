using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Tests;

/// <summary>
/// Covers the price-conversion step: captured Tiingo daily histories become raw and
/// total-return PriceBar observations over a validated two-source identity dataset,
/// available only at capture time, and the result feeds the point-in-time research panel.
/// </summary>
public sealed class ListingPricesTests
{
    private static readonly DateTimeOffset NasdaqObserved = DateTimeOffset.Parse("2026-10-16T21:00:00Z");
    private static readonly DateTimeOffset TiingoObserved = DateTimeOffset.Parse("2026-10-16T21:05:00Z");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
    private static readonly DateTimeOffset CaptureAt = DateTimeOffset.Parse("2026-10-18T14:00:00Z");

    private sealed class Workspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-listing-prices-" + Guid.NewGuid().ToString("N"));
        public readonly CaptureStore Store;
        public Workspace() => Store = new(Root);
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }

    private static ListingInterval Nas(string symbol, DateTimeOffset observed) =>
        new("nasdaq-listed", symbol, "NASDAQ", "stock", null, symbol + " Corp", null, null,
            observed, observed, null, 1, "present-at-final-snapshot", false, false);
    private static ListingInterval Tii(string symbol, DateOnly? start, DateOnly? end, DateTimeOffset observed,
        string status = "present-at-final-snapshot") =>
        new("tiingo-tickers", symbol, "NASDAQ", "Stock", "USD", null, start, end,
            observed, observed, null, 1, status, false, false);

    private static ImmutableArray<SourceSnapshot> Sources(DateTimeOffset nasdaqObserved, DateTimeOffset tiingoObserved) =>
    [
        new("nasdaq-listed", "https://www.nasdaqtrader.com/dynamic/symdir/nasdaqlisted.txt", "Authored fixture; no market data",
            nasdaqObserved, nasdaqObserved, new string('a', 64), "listing-intervals-v1", "Daily directory republication"),
        new("tiingo-tickers", "urn:twenty:synthetic:tiingo-tickers", "Authored fixture; no market data",
            tiingoObserved, tiingoObserved, new string('b', 64), "listing-intervals-v1", "Producer segments as listed"),
        new("listing-intervals", "urn:twenty:listing-intervals:fixture", "Derived from captured directories",
            tiingoObserved, nasdaqObserved, new string('c', 64), "listing-identity-v1", "Rebuild supersedes; prior datasets immutable")
    ];

    private static ListingIdentityResult Identity(DateTimeOffset nasdaqObserved, DateTimeOffset tiingoObserved, DateTimeOffset created,
        string[] nasdaqSymbols, (string Symbol, DateOnly? Start, DateOnly? End, string Status)[] tiingo, string[] symbols) =>
        ListingIdentity.Convert(
            nasdaqSymbols.Select(s => Nas(s, nasdaqObserved)).ToImmutableArray(),
            tiingo.Select(t => Tii(t.Symbol, t.Start, t.End, tiingoObserved, t.Status)).ToImmutableArray(),
            symbols, nasdaqObserved, tiingoObserved, created, "identity-fixture", Sources(nasdaqObserved, tiingoObserved));

    // The standard three-symbol fixture: AAA and NOH are two-source active listings from
    // 2015-01-02; OLD is a Tiingo-ended segment absent from the later Nasdaq snapshot, so
    // identity proves a delisting at 2023-01-04.
    private static readonly (string Symbol, DateOnly? Start, DateOnly? End, string Status)[] Segments =
    [
        ("AAA", new DateOnly(2015, 1, 2), null, "present-at-final-snapshot"),
        ("NOH", new DateOnly(2015, 1, 2), null, "present-at-final-snapshot"),
        ("OLD", new DateOnly(2010, 3, 1), new DateOnly(2023, 1, 3), "absent-from-later-snapshot"),
    ];
    private static readonly string[] NasdaqSymbols = ["AAA", "NOH"];
    private static readonly string[] AllSymbols = ["AAA", "NOH", "OLD"];
    private static ListingIdentityResult BaseIdentity() =>
        Identity(NasdaqObserved, TiingoObserved, Created, NasdaqSymbols, Segments, AllSymbols);

    /// <summary>Authored Tiingo history JSON with internally consistent split/dividend adjustment
    /// factors, mirroring the audit fixture math. The optional override replaces adjOpen on the
    /// first row only, which is exactly the one-row factor disagreement conversion must exclude.</summary>
    private static string HistoryJson((string Date, decimal Close, decimal Div, decimal Split)[] rows, decimal? adjOpenOverride = null)
    {
        var steps = new decimal[rows.Length];
        for (var t = 1; t < rows.Length; t++)
        {
            var step = 1m;
            if (rows[t].Split != 1) step *= 1 / rows[t].Split;
            if (rows[t].Div > 0) step *= 1 - rows[t].Div / rows[t - 1].Close;
            steps[t] = step;
        }
        var json = new StringBuilder("[");
        for (var t = 0; t < rows.Length; t++)
        {
            var factor = 1m;
            for (var s = t + 1; s < rows.Length; s++) factor *= steps[s];
            var adj = rows[t].Close * factor;
            if (t > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"date\":\"{rows[t].Date}T00:00:00.000Z\",\"open\":{rows[t].Close},\"high\":{rows[t].Close},\"low\":{rows[t].Close},\"close\":{rows[t].Close},\"adjClose\":{adj:0.##########},\"adjOpen\":{(t == 0 && adjOpenOverride.HasValue ? adjOpenOverride.Value : adj):0.##########},\"adjHigh\":{adj:0.##########},\"adjLow\":{adj:0.##########},\"volume\":1000,\"adjVolume\":2000,\"divCash\":{rows[t].Div},\"splitFactor\":{rows[t].Split}}}");
        }
        return json.Append(']').ToString();
    }

    private static void Record(CaptureStore store, string symbol, string json, DateTimeOffset at)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        store.Record(new(Guid.NewGuid().ToString("N"), "tiingo-history-" + symbol.ToLowerInvariant().Replace('.', '-'),
            "https://api.tiingo.com/authored", CaptureStore.Hash(bytes), bytes.Length, "application/json",
            at, "authored-unit", false), bytes, false);
    }

    private static readonly (string Date, decimal Close, decimal Div, decimal Split)[] AaaBars =
    [
        ("2026-01-02", 10.0m, 0m, 1m), ("2026-01-05", 10.1m, 0m, 1m), ("2026-01-06", 10.2m, 0m, 1m),
        ("2026-01-07", 10.3m, 0m, 1m), ("2026-01-08", 10.4m, 0m, 1m), ("2026-07-02", 10.25m, 0m, 1m),
    ];
    private static readonly (string Date, decimal Close, decimal Div, decimal Split)[] OldBars =
    [
        ("2022-12-30", 20.0m, 0m, 1m), ("2023-01-03", 19.5m, 0m, 1m),
    ];

    [Fact]
    public void ConvertProducesBothBasesAtListingTimezoneSessionCloses()
    {
        using var w = new Workspace();
        Record(w.Store, "AAA", HistoryJson(AaaBars), CaptureAt);
        Record(w.Store, "OLD", HistoryJson(OldBars), CaptureAt);

        var result = ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created);

        // NOH never had its history captured: excluded with a reason, and its identity rows
        // are dropped with it so no unpriced listing survives into a priced dataset.
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal("NOH", exclusion.Symbol);
        Assert.Equal("history-not-captured", exclusion.Reason);
        var dataset = result.Dataset;
        Assert.DoesNotContain(dataset.Observations.OfType<Listing>(), l => l.Symbol == "NOH");
        // Delisted OLD was still inside its proven active interval when these bars traded,
        // so it converts too.
        Assert.Contains(dataset.Observations.OfType<Listing>(), l => l.ListingId == "listing-old-20100301");

        Assert.Equal(2 * (AaaBars.Length + OldBars.Length), result.BarsAdded);
        Assert.Equal(0, result.ActionsAdded);
        var janRaw = dataset.Observations.OfType<PriceBar>().Single(b => b.Id == "listing-aaa-20150102-raw-20260102");
        var julAdj = dataset.Observations.OfType<PriceBar>().Single(b => b.Id == "listing-aaa-20150102-tra-20260702");
        Assert.Equal(10.0m, janRaw.Close);
        Assert.Equal(PriceBasis.Raw, janRaw.Basis);
        // Volume follows its own basis: the raw bar carries raw share volume and the
        // total-return bar carries the split-adjusted volume for the very same session.
        Assert.Equal(1000m, janRaw.Volume);
        Assert.Equal(2000m, julAdj.Volume);
        // A January close is 21:00Z and a July close is 20:00Z for a US listing: the session
        // close is anchored to the listing's exchange timezone, not to UTC hours.
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T21:00:00Z"), janRaw.SessionCloseAt);
        Assert.Equal(10.25m, julAdj.Close);
        Assert.Equal(PriceBasis.TotalReturnAdjusted, julAdj.Basis);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T20:00:00Z"), julAdj.SessionCloseAt);
        // Availability is capture time, for every bar of that capture, however old the session.
        Assert.Equal(CaptureAt, janRaw.Evidence.AvailableAt);
        Assert.Equal(CaptureAt, janRaw.Evidence.PublishedAt);
        Assert.Equal(AvailabilityEvidence.ObservedPublication, janRaw.Evidence.AvailabilityEvidence);

        // Manifest: the identity-only feature is replaced by daily-prices, and the history
        // source snapshot records the used captures.
        Assert.Equal(["daily-prices"], dataset.Manifest.Features.Select(f => f.Name).ToArray());
        var source = Assert.Single(dataset.Manifest.Sources.Where(s => s.Id == "tiingo-history"));
        Assert.Equal(CaptureAt, source.RetrievedAt);
        Assert.True(DatasetValidator.Validate(dataset).IsValid);

        // Point-in-time: bars appear exactly at their capture, never before.
        var pit = new PointInTimeDataset(dataset);
        var before = pit.AsOf(CaptureAt.AddSeconds(-1));
        Assert.Empty(before.Prices("listing-aaa-20150102", PriceBasis.Raw));
        var at = pit.AsOf(CaptureAt);
        Assert.Equal(AaaBars.Length, at.Prices("listing-aaa-20150102", PriceBasis.Raw).Length);
        Assert.Equal(["listing-aaa-20150102"], at.Universe(ListingIdentity.UniverseId).Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void ConvertEmitsCorporateActionsForSplitsAndDividendsInsideTheInterval()
    {
        using var w = new Workspace();
        var aaa = new (string Date, decimal Close, decimal Div, decimal Split)[]
        {
            ("2026-01-02", 10.0m, 0m, 1m), ("2026-01-05", 10.25m, 0.10m, 1m), ("2026-01-06", 5.0m, 0m, 2m),
        };
        // The final OLD row trades after its proven delisting, so it is counted as outside the
        // interval, never converted into a bar or an action.
        var old = new (string Date, decimal Close, decimal Div, decimal Split)[]
        {
            ("2022-12-30", 20.0m, 0m, 1m), ("2023-01-03", 19.5m, 0m, 1m), ("2024-06-03", 25.0m, 0m, 1m),
        };
        Record(w.Store, "AAA", HistoryJson(aaa), CaptureAt);
        Record(w.Store, "OLD", HistoryJson(old), CaptureAt);

        var result = ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created);

        Assert.Equal(1, result.OutsideIntervalSkipped);
        Assert.Equal(2, result.ActionsAdded);
        var dataset = result.Dataset;
        Assert.DoesNotContain(dataset.Observations.OfType<PriceBar>(), b => b.SessionDate == new DateOnly(2024, 6, 3));
        var dividend = Assert.Single(dataset.Observations.OfType<CorporateAction>(), a => a.Action == CorporateActionKind.Dividend);
        Assert.Equal("listing-aaa-20150102-div-20260105", dividend.Id);
        Assert.Equal(0.10m, dividend.CashPerShare);
        Assert.Equal("USD", dividend.CashCurrency);
        Assert.Equal(DateTimeOffset.Parse("2026-01-05T21:00:00Z"), dividend.EffectiveAt);
        var split = Assert.Single(dataset.Observations.OfType<CorporateAction>(), a => a.Action == CorporateActionKind.Split);
        Assert.Equal("listing-aaa-20150102-split-20260106", split.Id);
        Assert.Equal(2m, split.SplitRatio);
        Assert.True(DatasetValidator.Validate(dataset).IsValid);
    }

    [Fact]
    public void InconsistentAdjustmentsExcludeTheListingNotTheWholeDataset()
    {
        using var w = new Workspace();
        // adjOpen on the first row implies half the adjClose factor: one row describes two
        // adjustments, so AAA is untrusted and excluded while OLD still converts.
        Record(w.Store, "AAA", HistoryJson([( "2026-01-02", 10.0m, 0m, 1m), ("2026-01-05", 10.1m, 0m, 1m)], adjOpenOverride: 5.0m), CaptureAt);
        Record(w.Store, "OLD", HistoryJson(OldBars), CaptureAt);

        var result = ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created);

        Assert.Contains(result.Excluded, e => e.Symbol == "AAA" && e.Reason == "adjustment-inconsistent");
        Assert.Contains(result.Excluded, e => e.Symbol == "NOH" && e.Reason == "history-not-captured");
        Assert.Equal(2 * OldBars.Length, result.BarsAdded);
        var dataset = result.Dataset;
        Assert.DoesNotContain(dataset.Observations.OfType<Listing>(), l => l.Symbol == "AAA");
        Assert.Contains(dataset.Observations.OfType<PriceBar>(), b => b.ListingId == "listing-old-20100301");
        Assert.True(DatasetValidator.Validate(dataset).IsValid);
    }

    [Fact]
    public void ConvertRefusesDuplicateAndConflictingInputs()
    {
        using var w = new Workspace();
        Record(w.Store, "AAA", HistoryJson(AaaBars), CaptureAt);
        var identity = BaseIdentity().Dataset;
        var result = ListingPrices.Convert(w.Store, identity, "converted-fixture", Created);

        var blank = Assert.Throws<ResearchException>(() => ListingPrices.Convert(w.Store, identity, " ", Created));
        Assert.Equal("price-conversion-dataset-id", blank.Code);
        // An identity dataset that already carries prices must be rebuilt, never re-priced.
        var duplicate = Assert.Throws<ResearchException>(() => ListingPrices.Convert(w.Store, result.Dataset, "converted-again", Created));
        Assert.Equal("price-conversion-duplicate", duplicate.Code);
        // A manifest that already declares the history source cannot absorb another one.
        var poisoned = identity with
        {
            Manifest = identity.Manifest with
            {
                Sources = identity.Manifest.Sources.Add(new SourceSnapshot("tiingo-history", "urn:x", "lic",
                    NasdaqObserved, NasdaqObserved, new string('d', 64), "v1", "policy"))
            }
        };
        var conflict = Assert.Throws<ResearchException>(() => ListingPrices.Convert(w.Store, poisoned, "converted-fixture", Created));
        Assert.Equal("price-conversion-source-conflict", conflict.Code);
        // Two captures claiming the same latest instant with different bytes are unreconcilable.
        Record(w.Store, "AAA", HistoryJson([( "2026-01-02", 11.0m, 0m, 1m)]), CaptureAt);
        var tie = Assert.Throws<ResearchException>(() => ListingPrices.Convert(w.Store, identity, "converted-fixture", Created));
        Assert.Equal("price-conversion-capture-conflict", tie.Code);
    }

    [Fact]
    public void ConvertRefusesWhenNothingCouldBePriced()
    {
        using var w = new Workspace();
        // Every listing lacks a capture, so there is no priced dataset to build.
        var empty = Assert.Throws<ResearchException>(() => ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created));
        Assert.Equal("price-conversion-empty", empty.Code);
    }

    [Fact]
    public void CapturesThatCarryNoRowsAreRefusedRatherThanCrashing()
    {
        using var w = new Workspace();
        // The producer can answer a valid window with an empty array; that is a capture with
        // no rows at all, not a fabricated price history.
        Record(w.Store, "AAA", "[]", CaptureAt);
        var error = Assert.Throws<ResearchException>(() =>
            ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created));
        Assert.Equal("price-conversion-vintage-narrower", error.Code);
    }

    [Fact]
    public void ALaterCaptureThatDropsEarlierHistoryIsRefused()
    {
        using var w = new Workspace();
        // History may accumulate across captures, never shrink: a later capture that starts
        // after an earlier one's first session is refused rather than silently dropping rows.
        Record(w.Store, "AAA", HistoryJson([("2026-01-02", 10m, 0m, 1m), ("2026-01-05", 10.1m, 0m, 1m)]), CaptureAt);
        Record(w.Store, "AAA", HistoryJson([("2026-01-05", 10.1m, 0m, 1m)]), CaptureAt.AddHours(1));
        var error = Assert.Throws<ResearchException>(() =>
            ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created));
        Assert.Equal("price-conversion-vintage-narrower", error.Code);
    }

    [Fact]
    public void VintagesMergeAndEachBarIsAvailableFromItsFirstCapture()
    {
        using var w = new Workspace();
        var early = HistoryJson([("2026-01-02", 9.0m, 0m, 1m)]);
        var late = HistoryJson([("2026-01-02", 11.0m, 0m, 1m)]);
        var firstCapture = CaptureAt.AddDays(-1);
        var secondCapture = CaptureAt;
        Record(w.Store, "AAA", early, firstCapture);
        Record(w.Store, "AAA", late, secondCapture);

        var result = ListingPrices.Convert(w.Store, BaseIdentity().Dataset, "converted-fixture", Created);

        Assert.DoesNotContain(result.Excluded, e => e.Symbol == "AAA");
        var pit = new PointInTimeDataset(result.Dataset);
        // Before the first capture: nothing.
        Assert.Empty(pit.AsOf(firstCapture.AddSeconds(-1)).Prices("listing-aaa-20150102", PriceBasis.Raw));
        // At the first capture: the first vintage's close, available only then.
        var firstBar = Assert.Single(pit.AsOf(firstCapture).Prices("listing-aaa-20150102", PriceBasis.Raw));
        Assert.Equal(9.0m, firstBar.Close);
        Assert.Equal(firstCapture, firstBar.Evidence.AvailableAt);
        Assert.Null(firstBar.Evidence.RevisionOf);
        // At the second capture: the restated close supersedes via a revision chain.
        var secondBar = Assert.Single(pit.AsOf(secondCapture).Prices("listing-aaa-20150102", PriceBasis.Raw));
        Assert.Equal(11.0m, secondBar.Close);
        Assert.Equal(secondCapture, secondBar.Evidence.AvailableAt);
        Assert.Equal(firstBar.Id, secondBar.Evidence.RevisionOf);
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        // Two independent stores with captures recorded in opposite order must serialize
        // identically: ordering of capture insertion must not affect the output.
        using var w1 = new Workspace();
        using var w2 = new Workspace();
        Record(w1.Store, "AAA", HistoryJson(AaaBars), CaptureAt);
        Record(w1.Store, "OLD", HistoryJson(OldBars), CaptureAt);
        Record(w2.Store, "OLD", HistoryJson(OldBars), CaptureAt);
        Record(w2.Store, "AAA", HistoryJson(AaaBars), CaptureAt);

        var first = ListingPrices.Convert(w1.Store, BaseIdentity().Dataset, "converted-fixture", Created);
        var second = ListingPrices.Convert(w2.Store, BaseIdentity().Dataset, "converted-fixture", Created);

        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(first.Dataset, DatasetJson.Options),
            JsonSerializer.SerializeToUtf8Bytes(second.Dataset, DatasetJson.Options));
    }

    [Fact]
    public void ParseHistoryRejectsLegacyRowsWithoutAdjustedOpenHighLow()
    {
        var legacy = """[{"date":"2015-01-02T00:00:00.000Z","open":10,"high":11,"low":9.5,"close":10.5,"adjClose":10.25,"volume":1000,"divCash":0,"splitFactor":1}]""";
        var error = Assert.Throws<ResearchException>(() => TiingoClient.ParseHistory(Encoding.UTF8.GetBytes(legacy)));
        Assert.Equal("tiingo-history-shape", error.Code);
    }

    [Fact]
    public void PointInTimePanelFromConvertedCapturesPricesDecisionsAfterCapture()
    {
        using var w = new Workspace();
        var observed = DateTimeOffset.Parse("2024-08-01T20:00:00Z");
        var identity = Identity(observed, observed, DateTimeOffset.Parse("2024-12-01T12:00:00Z"),
            ["AAA", "NOH"], [.. Segments.Select(s => (s.Symbol, s.Start, s.End, s.Status))], ["AAA", "NOH"]);
        var rows = new List<(string Date, decimal Close, decimal Div, decimal Split)>();
        for (var day = new DateOnly(2024, 8, 1); day <= new DateOnly(2024, 10, 10); day = day.AddDays(1))
            if (day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday)
                rows.Add((day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10m + ((day.DayNumber - new DateOnly(2024, 8, 1).DayNumber) % 7) * 0.25m, 0m, 1m));
        Record(w.Store, "AAA", HistoryJson([.. rows]), DateTimeOffset.Parse("2024-10-11T12:00:00Z"));

        var result = ListingPrices.Convert(w.Store, identity.Dataset, "converted-fixture", DateTimeOffset.Parse("2024-12-01T12:00:00Z"));
        Assert.Equal("NOH", Assert.Single(result.Excluded).Symbol);
        Assert.Equal(2 * rows.Count, result.BarsAdded);

        var pit = new PointInTimeDataset(result.Dataset);
        var decisions = new[] { DateTimeOffset.Parse("2024-10-15T23:00:00Z"), DateTimeOffset.Parse("2024-11-15T23:00:00Z") };
        var panel = ChangingUniversePanel.FromDataset(pit, ListingIdentity.UniverseId, decisions, TimeSpan.FromDays(45));

        Assert.False(panel.HasGaps);
        Assert.Equal(["listing-aaa-20150102"], panel.Months[0].EligibleListingIds.ToArray());
        Assert.Empty(panel.Months[0].UnpricedListingIds);
        var period = Assert.Single(panel.Periods);
        var ret = Assert.Single(period.Returns);
        Assert.Equal("listing-aaa-20150102", ret.ListingId);
        Assert.Equal(UniverseReturnStatus.Priced, ret.Status);
        // Both endpoints resolve to the last captured bar (2024-10-10); pin its converted
        // close so the zero return is not a flat-price artifact.
        var lastClose = pit.AsOf(decisions[0]).Prices("listing-aaa-20150102", PriceBasis.TotalReturnAdjusted).Last().Close;
        Assert.Equal(10m, lastClose);
        Assert.Equal(0m, ret.Return);
        Assert.NotEmpty(pit.AsOf(decisions[0]).Prices("listing-aaa-20150102", PriceBasis.TotalReturnAdjusted));
    }
}
