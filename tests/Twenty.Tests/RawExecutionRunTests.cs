using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

/// <summary>
/// Covers the raw execution run: what a strategy wanted becomes what it could have done, settled
/// through the fill planner into the raw ledger, with equity valued before each month's fills
/// because those fills happen after the decision.
/// </summary>
public sealed class RawExecutionRunTests
{
    private static readonly DateTimeOffset Jan31 = new(2024, 1, 31, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb20 = new(2024, 2, 20, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb29 = new(2024, 2, 29, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar20 = new(2024, 3, 20, 21, 0, 0, TimeSpan.Zero);
    private static Evidence Ev(DateTimeOffset available) => new("fixture-source", available, available,
        AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);

    private static PriceBar Bar(string id, DateTimeOffset close, decimal price, decimal volume) =>
        new(id, Ev(close.AddHours(1)), "listing-aaa", DateOnly.FromDateTime(close.UtcDateTime), close,
            new Denomination("GBP", QuoteUnit.Major), PriceBasis.Raw, price, price, price, price, volume);

    /// <summary>Sessions at 31 January, 15 February and 15 March, all at the same price so a
    /// hand-computed equity is about cash and quantities rather than market moves.</summary>
    private static DataSlice Slice(DateTimeOffset at)
    {
        var observations = new List<Observation>
        {
            new Listing("listing-row", Ev(Jan31.AddDays(-30)), "security-aaa", "listing-aaa", "company-aaa", "AAA", "XNAS",
                AssetClass.Stock, [new ProviderIdentifier("tiingo.ticker", "AAA")],
                new Denomination("GBP", QuoteUnit.Major), "ny-calendar",
                Jan31.AddDays(-30), null, ListingStatus.Active),
            Bar("jan31-close", Jan31, 100m, 100000m),
            Bar("feb15-close", new(2024, 2, 15, 21, 0, 0, TimeSpan.Zero), 110m, 100000m),
            Bar("mar15-close", new(2024, 3, 15, 21, 0, 0, TimeSpan.Zero), 120m, 100000m),
        };
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "raw-execution-fixture", Mar20.AddDays(30),
            "features.v1", [new FeatureDefinition("execution", "v1", "Authored fixture", "Available at capture time")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:raw-execution", "Authored fixture; no market data",
                Mar20.AddDays(30), null, new string('a', 64), "fixture-v1", "Immutable")],
            [new CalendarReference("ny-calendar", "America/New_York", "v1", new string('b', 64))],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored fixture")], true);
        return new PointInTimeDataset(new(manifest, [.. observations])).AsOf(at);
    }

    private static readonly FillPolicy Flat = new(HalfSpreadBps: 0m, SlippageBps: 0m,
        MinimumOrderValue: 1m, MaximumParticipation: 1m);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3650);

    [Fact]
    public void ABuyIsBookedAtTheNextOpenAndEquityIsValuedBeforeIt()
    {
        var steps = new[]
        {
            new RawExecutionStep(Slice(Feb20), [new PlannedOrder("listing-aaa", true, 1100m, Jan31, Feb29)]),
            new RawExecutionStep(Slice(Mar20), Array.Empty<PlannedOrder>()),
        };
        var result = RawExecutionRun.Run(steps, 2000m, Flat, feeGbp: 1m, MaxAge, MaxAge);

        // Month 0 is valued before its fill: the fill happens at the 15 February open, after the
        // 31 January decision, so the decision is made holding only cash.
        Assert.Equal(2000m, result.Months[0].EquityGbp);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        var fill = Assert.Single(result.Months[0].Fills);
        Assert.Equal("feb15-close", fill.PriceId);
        Assert.Equal(10m, fill.Quantity); // 1,100 at the 110 open
        Assert.Empty(result.Months[0].Rejections);

        // By the next decision the position is worth 10 × 120 at the 15 March close.
        Assert.Equal(899m, result.Months[1].CashGbp);      // 2,000 − 1,100 − 1 fee
        Assert.Equal(2099m, result.Months[1].EquityGbp);   // 899 + 1,200
        Assert.Equal(2099m, result.ClosingEquityGbp);
        Assert.Equal(2000m, result.OpeningCashGbp);
    }

    [Fact]
    public void ARefusedOrderLeavesCashAndHoldingsExactlyAsTheyWere()
    {
        // 5% of the 15 February session's 100,000 shares is 5,000; this wants about 9,091.
        var steps = new[]
        {
            new RawExecutionStep(Slice(Feb20), [new PlannedOrder("listing-aaa", true, 1000000m, Jan31, Feb29)]),
        };
        var result = RawExecutionRun.Run(steps, 2000m, Flat with { MaximumParticipation = 0.05m }, 1m, MaxAge, MaxAge);

        Assert.Empty(result.Months[0].Fills);
        Assert.Equal(ExecutionRefusal.VolumeParticipation, Assert.Single(result.Months[0].Rejections).Reason);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        Assert.Equal(2000m, result.ClosingEquityGbp); // nothing bought, nothing lost
    }

    [Fact]
    public void AMonthWithNoTradeStillRevaluesWhatItHolds()
    {
        var steps = new[]
        {
            new RawExecutionStep(Slice(Feb20), [new PlannedOrder("listing-aaa", true, 1100m, Jan31, Feb29)]),
            new RawExecutionStep(Slice(Mar20), Array.Empty<PlannedOrder>()),
        };
        var result = RawExecutionRun.Run(steps, 2000m, Flat, 1m, MaxAge, MaxAge);
        Assert.Empty(result.Months[1].Fills);
        Assert.Empty(result.Months[1].Rejections);
        Assert.True(result.Months[1].EquityGbp > result.Months[0].EquityGbp); // the position revalued
    }

    [Fact]
    public void StepsMustArriveInTimeOrder()
    {
        var steps = new[]
        {
            new RawExecutionStep(Slice(Mar20), Array.Empty<PlannedOrder>()),
            new RawExecutionStep(Slice(Feb20), Array.Empty<PlannedOrder>()),
        };
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(
            () => RawExecutionRun.Run(steps, 2000m, Flat, 1m, MaxAge, MaxAge)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnfundedOrEmptyRunIsRefused(int opening)
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([], opening, Flat, 1m, MaxAge, MaxAge)).Code);
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), [])], opening, Flat, 1m, MaxAge, MaxAge)).Code);
    }

    [Fact]
    public void ANegativeFeeIsRefusedBeforeAnythingIsBooked()
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), [])], 2000m, Flat, -1m, MaxAge, MaxAge)).Code);
    }
}
