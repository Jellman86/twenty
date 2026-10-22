using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Listings;

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

    private static DataSlice Slice(DateTimeOffset at, Denomination? units = null, params Observation[] extra)
    {
        var observations = new List<Observation>
        {
            new Listing("listing-row", Ev(Jan31.AddDays(-30)), "security-aaa", "listing-aaa", "company-aaa", "AAA", "XNAS",
                AssetClass.Stock, [new ProviderIdentifier("tiingo.ticker", "AAA")],
                new Denomination("GBP", QuoteUnit.Major), "ny-calendar",
                Jan31.AddDays(-30), null, ListingStatus.Active),
            Bar("jan31-close", Jan31, 100m, 100000m),
            Bar("feb01-close", new(2024, 2, 1, 21, 0, 0, TimeSpan.Zero), 110m, 100000m),
            Bar("mar01-close", new(2024, 3, 1, 21, 0, 0, TimeSpan.Zero), 120m, 100000m),
        };
        if (units is not null) observations = observations.Select<Observation, Observation>(o => o switch
        {
            Listing l => l with { Denomination = units },
            PriceBar b => b with { Denomination = units },
            _ => o
        }).ToList();
        observations.AddRange(extra);
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "raw-execution-fixture", Mar20.AddDays(30),
            "features.v1", [new FeatureDefinition("execution", "v1", "Authored fixture", "Available at capture time")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:raw-execution", "Authored fixture; no market data",
                Mar20.AddDays(30), null, new string('a', 64), "fixture-v1", "Immutable")],
            [NasdaqSessions.Reference("ny-calendar")],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored fixture")], true);
        return new PointInTimeDataset(new(manifest, [.. observations])).AsOf(at);
    }

    private static readonly FillPolicy Flat = new(HalfSpreadBps: 0m, SlippageBps: 0m,
        MinimumOrderValue: 1m, MaximumParticipation: 1m);
    private static readonly ExecutionCalendar Calendar = NasdaqSessions.ExecutionCalendar("ny-calendar", new(2024, 1, 1), new(2024, 4, 30));
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3650);
    private static PlannedOrder Buy(decimal notional) => new("listing-aaa", true, notional, Jan31, Feb29);

    /// <summary>Three decisions: one buys at the next open, the next two only revalue.</summary>
    private static RawExecutionStep[] Stepped() =>
    [
        new(Slice(Jan31), Slice(Feb20), _ => [Buy(1100m)]),
        new(Slice(Feb29), Slice(Mar20), _ => Array.Empty<PlannedOrder>()),
        new(Slice(Mar20), Slice(Mar20), _ => Array.Empty<PlannedOrder>())
    ];

    [Fact]
    public void ABuyIsBookedAtTheNextOpenAndEquityIsValuedAtTheDecision()
    {
        var result = RawExecutionRun.Run(Stepped(), 2000m, Flat, feeGbp: 1m, MaxAge, MaxAge, Calendar);

        // The 31 January decision is taken holding only cash: its fill happens at the
        // 1 February open, after it.
        Assert.Equal(2000m, result.Months[0].EquityGbp);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        var fill = Assert.Single(result.Months[0].Fills);
        Assert.Equal("feb01-close", fill.PriceId);
        Assert.Equal(110m, fill.ObservedOpen);
        Assert.Equal(10m, fill.Quantity); // 1,100 at the 110 open
        Assert.Empty(result.Months[0].Rejections);

        // By 29 February the fill has happened: 899 cash plus 10 shares at the 110 close.
        Assert.Equal(899m, result.Months[1].CashGbp);      // 2,000 − 1,100 − 1 fee
        Assert.Equal(1999m, result.Months[1].EquityGbp);
        Assert.Empty(result.Months[1].Fills);

        // 20 March revalues the same 10 shares at the 1 March close of 120.
        Assert.Equal(2099m, result.Months[2].EquityGbp);
        Assert.Equal(2099m, result.ClosingEquityGbp);
        Assert.Equal(2000m, result.OpeningCashGbp);
    }

    [Fact]
    public void ARefusedOrderLeavesCashAndHoldingsExactlyAsTheyWere()
    {
        // 5% of the 1 February session's 100,000 shares is 5,000; this wants about 9,091.
        var steps = new[] { new RawExecutionStep(Slice(Jan31), Slice(Feb20), _ => [Buy(1000000m)]) };
        var result = RawExecutionRun.Run(steps, 2000m, Flat with { MaximumParticipation = 0.05m }, 1m, MaxAge, MaxAge, Calendar);

        Assert.Empty(result.Months[0].Fills);
        Assert.Equal(ExecutionRefusal.VolumeParticipation, Assert.Single(result.Months[0].Rejections).Reason);
        Assert.Equal(2000m, result.Months[0].CashGbp);
        Assert.Equal(2000m, result.ClosingEquityGbp); // nothing bought, nothing lost
    }

    [Fact]
    public void ARevaluationMonthChangesEquityWithoutTrading()
    {
        var result = RawExecutionRun.Run(Stepped(), 2000m, Flat, 1m, MaxAge, MaxAge, Calendar);
        Assert.Empty(result.Months[2].Fills);
        Assert.Empty(result.Months[2].Rejections);
        Assert.Equal(1999m, result.Months[1].EquityGbp);
        Assert.Equal(2099m, result.Months[2].EquityGbp);
    }

    [Fact]
    public void StepsMustArriveInTimeOrder()
    {
        var late = new RawExecutionStep(Slice(Mar20), Slice(Mar20), _ => Array.Empty<PlannedOrder>());
        var early = new RawExecutionStep(Slice(Feb20), Slice(Feb20), _ => Array.Empty<PlannedOrder>());
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([late, early], 2000m, Flat, 1m, MaxAge, MaxAge, Calendar)).Code);
    }

    [Fact]
    public void AFillSliceCannotPredateItsOwnDecision()
    {
        var step = new RawExecutionStep(Slice(Mar20), Slice(Feb20), _ => Array.Empty<PlannedOrder>());
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([step], 2000m, Flat, 1m, MaxAge, MaxAge, Calendar)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnfundedOrEmptyRunIsRefused(int opening)
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([], opening, Flat, 1m, MaxAge, MaxAge, Calendar)).Code);
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), Slice(Feb20), _ => [])], opening, Flat, 1m, MaxAge, MaxAge, Calendar)).Code);
    }

    [Fact]
    public void ANegativeFeeIsRefusedBeforeAnythingIsBooked()
    {
        Assert.Equal("invalid-execution-run", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run([new RawExecutionStep(Slice(Feb20), Slice(Feb20), _ => [])], 2000m, Flat, -1m, MaxAge, MaxAge, Calendar)).Code);
    }
    [Fact]
    public void AFinalBuyIsMarkedAfterItsFillAndFees()
    {
        var result = RawExecutionRun.Run([new(Slice(Jan31), Slice(Feb20), _ => [Buy(1100)])],
            2000, Flat, 1, MaxAge, MaxAge, Calendar);
        Assert.Equal(2000, result.Months[0].EquityGbp);
        Assert.Equal(1999, result.ClosingEquityGbp);
        Assert.Equal(Feb20, result.ClosingValuation.At);
        Assert.Equal(899, result.ClosingValuation.CashGbp);
        Assert.Equal(10, Assert.Single(result.ClosingValuation.Positions).Quantity);
    }

    [Fact]
    public void SellingTheEntirePositionCreditsCashAndRemovesShares()
    {
        var steps = new RawExecutionStep[]
        {
            new(Slice(Jan31), Slice(Feb20), _ => [Buy(1100)]),
            new(Slice(Feb29), Slice(Mar20), value =>
            {
                Assert.Equal(10, Assert.Single(value.Positions).Quantity);
                return [new("listing-aaa", false, 1200, Feb29, Mar20)];
            }),
            new(Slice(Mar20), Slice(Mar20), value =>
            {
                Assert.Empty(value.Positions);
                Assert.Equal(4098, value.CashGbp);
                return [];
            })
        };
        var result = RawExecutionRun.Run(steps, 4000, Flat, 1, MaxAge, MaxAge, Calendar);
        Assert.Equal(4098, result.ClosingEquityGbp);
        Assert.False(Assert.Single(result.Months[1].Fills).Buy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingOrRepeatedDecisionsAreRefusedBeforeCallingTheStrategy(bool repeated)
    {
        var calls = 0;
        var next = repeated ? Jan31 : Jan31.AddDays(1);
        var steps = new RawExecutionStep[]
        {
            new(Slice(Jan31), Slice(Feb20), _ => { calls++; return []; }),
            new(Slice(next), Slice(Feb20), _ => { calls++; return []; })
        };
        Assert.Equal("raw-execution-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run(steps, 2000, Flat, 0, MaxAge, MaxAge, Calendar)).Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void OrdersCannotUseAnotherDecisionOrExtendBeyondTheNextDecision(int decisionDays, int deadlineDays)
    {
        var steps = Stepped();
        steps[0] = steps[0] with { Orders = _ => [new("listing-aaa", true, 1100,
            Jan31.AddDays(decisionDays), Feb29.AddDays(deadlineDays))] };
        Assert.Equal("raw-execution-order-time", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run(steps, 2000, Flat, 0, MaxAge, MaxAge, Calendar)).Code);
    }

    [Fact]
    public void AnOverdrawnSellIsRefusedInsteadOfCreatingAShortPosition()
    {
        var steps = new RawExecutionStep[]
        {
            new(Slice(Jan31), Slice(Feb20), _ => [Buy(1100)]),
            new(Slice(Feb29), Slice(Mar20), _ => [new("listing-aaa", false, 1320, Feb29, Mar20)])
        };
        Assert.Equal("raw-ledger-quantity", Assert.Throws<ResearchException>(() =>
            RawExecutionRun.Run(steps, 4000, Flat, 1, MaxAge, MaxAge, Calendar)).Code);
    }

    [Fact]
    public void PenceRoundTripUsesMajorCashButRetainsQuotedPricesAndSignedReceipts()
    {
        var units = new Denomination("GBP", QuoteUnit.Pence);
        var steps = new RawExecutionStep[]
        {
            new(Slice(Jan31, units), Slice(Feb20, units), _ => [Buy(11)]),
            new(Slice(Feb29, units), Slice(Mar20, units), value =>
            {
                Assert.Equal(10, Assert.Single(value.Positions).Quantity);
                Assert.Equal(28, value.CashGbp);
                return [new("listing-aaa", false, 12, Feb29, Mar20)];
            })
        };
        var result = RawExecutionRun.Run(steps, 40, Flat, 1, MaxAge, MaxAge, Calendar);
        Assert.Equal(39, result.ClosingEquityGbp);
        Assert.Equal(Mar20, result.ClosingValuation.At);
        Assert.Equal(39, result.ClosingValuation.CashGbp);
        Assert.Empty(result.ClosingValuation.Positions);
        Assert.Equal(new decimal[] { 10, -10 }, result.Entries.Skip(1).Select(e => e.Quantity));
        Assert.Equal(new decimal?[] { 110, 120 }, result.Entries.Skip(1).Select(e => e.RawPrice));
        Assert.Equal(new decimal[] { -12, 11 }, result.Entries.Skip(1).Select(e => e.CashChangeGbp));
    }

    [Fact]
    public void ForeignFillUsesExecutableBookingFxWhileTheClosingMarkRetainsItsReferencePurpose()
    {
        var usd = new Denomination("USD", QuoteUnit.Major);
        var fxAt = DateTimeOffset.Parse("2024-02-01T14:29:00Z");
        var executable = new FxRate("execution-fx", Ev(fxAt), "USD", "GBP", .8m, fxAt, FxPurpose.Executable);
        var reference = new FxRate("reference-fx", Ev(fxAt), "USD", "GBP", .75m, fxAt, FxPurpose.Reference);
        var result = RawExecutionRun.Run([new(Slice(Jan31, usd), Slice(Feb20, usd, executable, reference), _ => [Buy(1100) with { FxObservationId = executable.Id }])],
            2000, Flat, 1, MaxAge, MaxAge, Calendar, FxPurpose.Reference);
        Assert.Equal(1119, result.ClosingValuation.CashGbp);
        Assert.Equal(1944, result.ClosingEquityGbp);
        Assert.Equal(FxPurpose.Reference, result.ClosingValuation.FxPurpose);
        Assert.Equal("reference-fx", Assert.Single(result.ClosingValuation.Positions).FxId);
        var receipt = result.Entries[1];
        Assert.Equal("execution-fx", receipt.FxId);
        Assert.Equal(.8m, receipt.GbpPerLocalUnit);
        Assert.Equal(-881, receipt.CashChangeGbp);
        Assert.Equal(Feb20, receipt.At);
    }

    [Fact]
    public void AReferenceMarkCannotFundAForeignFillWithoutExecutableFx()
    {
        var usd = new Denomination("USD", QuoteUnit.Major);
        var fxAt = DateTimeOffset.Parse("2024-02-01T14:29:00Z");
        var reference = new FxRate("reference-fx", Ev(fxAt), "USD", "GBP", .75m, fxAt, FxPurpose.Reference);
        var result = RawExecutionRun.Run(
            [new(Slice(Jan31, usd), Slice(Feb20, usd, reference), _ => [Buy(1100) with { FxObservationId = reference.Id }])],
            2000, Flat, 1, MaxAge, MaxAge, Calendar, FxPurpose.Reference);
        Assert.Empty(result.Months[0].Fills);
        Assert.Equal(ExecutionRefusal.InvalidExecutionFx, Assert.Single(result.Months[0].Rejections).Reason);
        Assert.Equal(2000, result.ClosingEquityGbp);
    }

    [Fact]
    public void UnfundedFillsAbortTheRunRatherThanReturningAReportedFill()
    {
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => RawExecutionRun.Run(
            [new(Slice(Jan31), Slice(Feb20), _ => [Buy(2000)])], 2000, Flat, 1, MaxAge, MaxAge, Calendar)).Code);
    }

}
