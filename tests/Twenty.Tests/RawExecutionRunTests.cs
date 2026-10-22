using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

/// <summary>
/// Covers the raw execution run: what a strategy wanted becomes what it could have done, settled
/// through the fill planner into the raw ledger. Equity is valued at the decision, before the
/// fills that decision causes, because those fills happen after it.
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
    private static PlannedOrder Buy(decimal notional) => new("listing-aaa", true, notional, Jan31, Feb29);

    /// <summary>Three decisions: one buys at the next open, the next two only revalue.</summary>
    private static RawExecutionStep[] Stepped() =>
    [
        new(Slice(Jan31), Slice(Feb20), [Buy(1100m)]),
        new(Slice(Feb29), Slice(Mar20), Array.Empty<PlannedOrder>()),
        new(Slice(Mar20), Slice(Mar20), Array.Empty<PlannedOrder>())
    ];

    [Fact]
    public void ABuyIsBookedAtTheNextOpenAndEquityIsValuedAtTheDecision()
    {
        var result = RawExecutionRun.Run(Stepped(), 2000m, Flat, feeGbp: 1m, MaxAge, MaxAge);

        // The 31 January decision is taken holding only cash: its fill happens at the
        // 15 February open, after it.
        Assert.Equal(2000m, result.Months[0].EquityGbp);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        var fill = Assert.Single(result.Months[0].Fills);
        Assert.Equal("feb15-close", fill.PriceId);
        Assert.Equal(110m, fill.ObservedOpen);
        Assert.Equal(10m, fill.Quantity); // 1,100 at the 110 open
        Assert.Empty(result.Months[0].Rejections);

        // By 29 February the fill has happened: 899 cash plus 10 shares at the 110 close.
        Assert.Equal(899m, result.Months[1].CashGbp);      // 2,000 − 1,100 − 1 fee
        Assert.Equal(1999m, result.Months[1].EquityGbp);
        Assert.Empty(result.Months[1].Fills);

        // 20 March revalues the same 10 shares at the 15 March close of 120.
        Assert.Equal(2099m, result.Months[2].EquityGbp);
        Assert.Equal(2099m, result.ClosingEquityGbp);
        Assert.Equal(2000m, result.OpeningCashGbp);
    }

    [Fact]
    public void ARefusedOrderLeavesCashAndHoldingsExactlyAsTheyWere()
    {
        // 5% of the 15 February session's 100,000 shares is 5,000; this wants about 9,091.
        var steps = new[] { new RawExecutionStep(Slice(Jan31), Slice(Feb20), [Buy(1000000m)]) };
        var result = RawExecutionRun.Run(steps, 2000m, Flat with { MaximumParticipation = 0.05m }, 1m, MaxAge, MaxAge);

        Assert.Empty(result.Months[0].Fills);
        Assert.Equal(ExecutionRefusal.VolumeParticipation, Assert.Single(result.Months[0].Rejections).Reason);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        Assert.Equal(2000m, result.ClosingEquityGbp); // nothing bought, nothing lost
    }

    [Fact]
    public void ARevaluationMonthChangesEquityWithoutTrading()
    {
        var result = RawExecutionRun.Run(Stepped(), 2000m, Flat, 1m, MaxAge, MaxAge);
        Assert.Empty(result.Months[2].Fills);
        Assert.Empty(result.Months[2].Rejections);
        Assert.Equal(1999m, result.Months[1].EquityGbp);
        Assert.Equal(2099m, result.Months[2].EquityGbp);
    }

    [Fact]
    public void StepsMustArriveInTimeOrder()
    {
        var late = new RawExecutionStep(Slice(Mar20), Slice(Mar20), Array.Empty<PlannedOrder>());
        var early = new RawExecutionStep(Slice(Feb20), Slice(Feb20), Array.Empty<PlannedOrder>());
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([late, early], 2000m, Flat, 1m, MaxAge, MaxAge)).Code);
    }

    [Fact]
    public void AFillSliceCannotPredateItsOwnDecision()
    {
        var step = new RawExecutionStep(Slice(Mar20), Slice(Feb20), Array.Empty<PlannedOrder>());
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([step], 2000m, Flat, 1m, MaxAge, MaxAge)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnfundedOrEmptyRunIsRefused(int opening)
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([], opening, Flat, 1m, MaxAge, MaxAge)).Code);
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), Slice(Feb20), [])], opening, Flat, 1m, MaxAge, MaxAge)).Code);
    }

    [Fact]
    public void ANegativeFeeIsRefusedBeforeAnythingIsBooked()
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), Slice(Feb20), [])], 2000m, Flat, -1m, MaxAge, MaxAge)).Code);
    }
}
