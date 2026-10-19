using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class ListingIdentityTests
{
    private static readonly DateTimeOffset NasdaqObserved = DateTimeOffset.Parse("2026-10-18T09:00:00Z");
    private static readonly DateTimeOffset TiingoObserved = DateTimeOffset.Parse("2026-10-18T09:05:00Z");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-19T12:00:00Z");

    private static ListingInterval Nas(string symbol, string status = "present-at-final-snapshot", string assetType = "stock") =>
        new("nasdaq-listed", symbol, "NASDAQ", assetType, null, symbol + " Corp", null, null,
            NasdaqObserved, NasdaqObserved, null, 1, status, false, false);
    private static ListingInterval Tii(string symbol, DateOnly? start, DateOnly? end, string assetType = "Stock",
        string exchange = "NASDAQ", string? currency = "USD", string status = "present-at-final-snapshot") =>
        new("tiingo-tickers", symbol, exchange, assetType, currency, null, start, end,
            TiingoObserved, TiingoObserved, null, 1, status, false, false);

    private static ImmutableArray<SourceSnapshot> Sources() =>
    [
        new("nasdaq-listed", "https://www.nasdaqtrader.com/dynamic/symdir/nasdaqlisted.txt", "Authored fixture; no market data",
            NasdaqObserved, NasdaqObserved, new string('a', 64), "listing-intervals-v1", "Daily directory republication"),
        new("tiingo-tickers", "urn:twenty:synthetic:tiingo-tickers", "Authored fixture; no market data",
            TiingoObserved, TiingoObserved, new string('b', 64), "listing-intervals-v1", "Producer segments as listed"),
        new("listing-intervals", "urn:twenty:listing-intervals:fixture", "Derived from captured directories",
            TiingoObserved, NasdaqObserved, new string('c', 64), "listing-identity-v1", "Rebuild supersedes; prior datasets immutable")
    ];

    private static ListingIdentityResult Convert(ImmutableArray<ListingInterval> nas, ImmutableArray<ListingInterval> tii,
        params string[] symbols) =>
        ListingIdentity.Convert(nas, tii, symbols, NasdaqObserved, TiingoObserved, Created, "identity-fixture", Sources());

    [Fact]
    public void TwoSourceAgreementAssertsActiveListingAndMembership()
    {
        var result = Convert([Nas("AAA")], [Tii("AAA", new DateOnly(2015, 1, 2), null)], "AAA");
        Assert.True(DatasetValidator.Validate(result.Dataset).IsValid);
        var listing = Assert.Single(result.Dataset.Observations.OfType<Listing>());
        Assert.Equal(ListingStatus.Active, listing.Status);
        Assert.Equal("XNAS", listing.Venue);
        Assert.Equal("AAA", listing.Symbol);
        Assert.Equal(new DateTimeOffset(2015, 1, 2, 0, 0, 0, TimeSpan.Zero), listing.EffectiveFrom);
        Assert.Null(listing.EffectiveUntil);
        Assert.Equal(new Denomination("USD", QuoteUnit.Major), listing.Denomination);
        Assert.Contains(listing.ProviderIds, p => p.Namespace == "nasdaq.symbol" && p.Value == "AAA");
        Assert.Contains(listing.ProviderIds, p => p.Namespace == "tiingo.ticker" && p.Value == "AAA");
        var membership = Assert.Single(result.Dataset.Observations.OfType<Membership>());
        Assert.True(membership.Eligible);
        Assert.Equal("declared-sample", membership.UniverseId);
        Assert.Empty(result.Excluded);
        // Producer history is only *known* at capture: no point-in-time universe before it.
        var dataset = new PointInTimeDataset(result.Dataset);
        Assert.Single(dataset.AsOf(TiingoObserved).Universe("declared-sample"));
        Assert.Empty(dataset.AsOf(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)).Universe("declared-sample"));
    }

    [Fact]
    public void DelistingRequiresProducerEndPlusObservedNasdaqAbsence()
    {
        var result = Convert([], [Tii("OLD", new DateOnly(2010, 3, 1), new DateOnly(2023, 9, 29), status: "absent-from-later-snapshot")], "OLD");
        Assert.True(DatasetValidator.Validate(result.Dataset).IsValid);
        var listings = result.Dataset.Observations.OfType<Listing>().OrderBy(x => x.EffectiveFrom).ToArray();
        Assert.Equal(2, listings.Length);
        Assert.Equal(ListingStatus.Active, listings[0].Status);
        Assert.Equal(new DateTimeOffset(2023, 9, 30, 0, 0, 0, TimeSpan.Zero), listings[0].EffectiveUntil);
        Assert.Equal(ListingStatus.Delisted, listings[1].Status);
        Assert.Equal(listings[0].EffectiveUntil, listings[1].EffectiveFrom);
        var memberships = result.Dataset.Observations.OfType<Membership>().OrderBy(x => x.EffectiveFrom).ToArray();
        Assert.Equal([true, false], memberships.Select(m => m.Eligible).ToArray());
        Assert.Empty(new PointInTimeDataset(result.Dataset).AsOf(TiingoObserved).Universe("declared-sample"));
    }

    [Fact]
    public void ConflictsAndSingleSourceClaimsAreExcludedWithReasons()
    {
        var result = Convert(
            [Nas("KEEP"), Nas("STILL"), Nas("ETFX", assetType: "stock")],
            [Tii("KEEP", new DateOnly(2018, 1, 2), null),
             Tii("STILL", new DateOnly(2018, 1, 2), new DateOnly(2024, 1, 2)),
             Tii("ETFX", new DateOnly(2018, 1, 2), null, assetType: "ETF"),
             Tii("EURO", new DateOnly(2018, 1, 2), null, currency: "EUR"),
             Tii("NYSEX", new DateOnly(2018, 1, 2), null, exchange: "NYSE")],
            "KEEP", "STILL", "ETFX", "EURO", "NYSEX", "UNSEEN");
        Assert.Equal(["KEEP"], result.IncludedSymbols.ToArray());
        Assert.Equal("producer-ended-but-listed", result.Excluded.Single(x => x.Symbol == "STILL").Reason);
        Assert.Equal("asset-class-conflict", result.Excluded.Single(x => x.Symbol == "ETFX").Reason);
        Assert.Equal("currency-unexpected", result.Excluded.Single(x => x.Symbol == "EURO").Reason);
        Assert.Equal("not-observed", result.Excluded.Single(x => x.Symbol == "UNSEEN").Reason);
        Assert.Equal("venue-conflict", result.Excluded.Single(x => x.Symbol == "NYSEX").Reason);
    }

    [Fact]
    public void EarlierTiingoSegmentsStayUncorroborated()
    {
        var result = Convert([Nas("REUSE")],
            [Tii("REUSE", new DateOnly(1999, 1, 4), new DateOnly(2005, 6, 1), status: "absent-from-later-snapshot"),
             Tii("REUSE", new DateOnly(2016, 2, 1), null)], "REUSE");
        Assert.Equal(["REUSE"], result.IncludedSymbols.ToArray());
        Assert.Single(result.Dataset.Observations.OfType<Listing>());
        Assert.Contains(result.Excluded, x => x.Symbol == "REUSE" && x.Reason == "historical-segment-uncorroborated");
        Assert.Equal(new DateTimeOffset(2016, 2, 1, 0, 0, 0, TimeSpan.Zero),
            result.Dataset.Observations.OfType<Listing>().Single().EffectiveFrom);
    }

    [Fact]
    public void ProducerEndedWithoutLaterNasdaqObservationIsUnconfirmed()
    {
        // Nasdaq was last observed before the producer end: absence proves nothing.
        var result = ListingIdentity.Convert([Nas("KEEP")],
            [Tii("KEEP", new DateOnly(2018, 1, 2), null),
             Tii("LATE", new DateOnly(2020, 1, 2), new DateOnly(2023, 9, 29))],
            ["KEEP", "LATE"], DateTimeOffset.Parse("2023-01-01T09:00:00Z"), TiingoObserved, Created,
            "identity-fixture", Sources());
        Assert.Equal("ended-unconfirmed", result.Excluded.Single(x => x.Symbol == "LATE").Reason);
    }

    [Fact]
    public void ProducerEndAtCaptureFrontierIsCoverageNotDelisting()
    {
        // Genuine shape: active tickers carry endDate = last trading day before the
        // weekend capture (2026-10-16 vs Nasdaq observed 2026-10-18).
        var result = Convert([Nas("AAA")], [Tii("AAA", new DateOnly(1980, 12, 12), new DateOnly(2026, 10, 16))], "AAA");
        Assert.Equal(["AAA"], result.IncludedSymbols.ToArray());
        Assert.Equal(ListingStatus.Active, result.Dataset.Observations.OfType<Listing>().Single().Status);
    }

    [Fact]
    public void AllExcludedAndSymbolSanitization()
    {
        var error = Assert.Throws<ResearchException>(() => Convert([], [], "UNSEEN"));
        Assert.Equal("listing-identity-empty", error.Code);
        var result = Convert([Nas("BRK/B")], [Tii("BRK/B", new DateOnly(2010, 5, 3), null)], "BRK/B");
        Assert.True(DatasetValidator.Validate(result.Dataset).IsValid);
        Assert.Contains("brk-b", result.Dataset.Observations.OfType<Listing>().Single().ListingId);
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        var first = Convert([Nas("AAA")], [Tii("AAA", new DateOnly(2015, 1, 2), null)], "AAA");
        var second = Convert([Nas("AAA")], [Tii("AAA", new DateOnly(2015, 1, 2), null)], "AAA");
        Assert.Equal(
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(first.Dataset, DatasetJson.Options)),
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(second.Dataset, DatasetJson.Options)));
    }
}
