using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

/// <summary>
/// Covers the execution contract: a decision taken after a session closes fills at the next
/// session's open on the raw basis, and everything that cannot be priced or sized is refused
/// rather than filled at the close that was just observed.
/// </summary>
public sealed class ExecutionPlannerTests
{
    private static readonly DateTimeOffset Jan31 = new(2024, 1, 31, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb29 = new(2024, 2, 29, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar20 = new(2024, 3, 20, 21, 0, 0, TimeSpan.Zero);
    private static Evidence Ev(DateTimeOffset available) => new("fixture-source", available, available,
        AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);

    private static PriceBar Bar(string id, DateTimeOffset close, decimal open, decimal volume,
        DateTimeOffset? available = null) =>
        new(id, Ev(available ?? close.AddHours(1)), "listing-aaa", DateOnly.FromDateTime(close.UtcDateTime), close,
            new Denomination("GBP", QuoteUnit.Major), PriceBasis.Raw, open, open, open, open, volume);

    /// <summary>Bars are named for their session: the decision session's close is at Jan31 and a
    /// fill must come from a strictly later session.</summary>
    private static DataSlice Slice(DateTimeOffset at, params Observation[] extra)
    {
        var observations = new List<Observation>
        {
            new Listing("listing-row", Ev(Jan31.AddDays(-30)), "security-aaa", "listing-aaa", "company-aaa", "AAA", "XNAS",
                AssetClass.Stock, [new ProviderIdentifier("tiingo.ticker", "AAA")],
                new Denomination("GBP", QuoteUnit.Major), "ny-calendar",
                Jan31.AddDays(-30), null, ListingStatus.Active),
            Bar("jan31-open", Jan31, 100m, 10000m),
            Bar("feb01-open", new(2024, 2, 1, 21, 0, 0, TimeSpan.Zero), 110m, 5000m),
            Bar("feb15-open", new(2024, 2, 15, 21, 0, 0, TimeSpan.Zero), 120m, 1000m),
            Bar("mar01-open", new(2024, 3, 1, 21, 0, 0, TimeSpan.Zero), 130m, 1000m),
        };
        observations.AddRange(extra);
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "execution-fixture", Mar20.AddDays(30),
            "features.v1", [new FeatureDefinition("execution", "v1", "Authored fixture", "Available at capture time")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:execution", "Authored fixture; no market data",
                Mar20.AddDays(30), null, new string('a', 64), "fixture-v1", "Immutable")],
            [new CalendarReference("ny-calendar", "America/New_York", "v1", new string('b', 64))],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored fixture")], true);
        return new PointInTimeDataset(new(manifest, [.. observations])).AsOf(at);
    }

    private static readonly FillPolicy Loose = new(HalfSpreadBps: 10m, SlippageBps: 10m,
        MinimumOrderValue: 1m, MaximumParticipation: 1m);

    [Fact]
    public void ABuyFillsAtTheNextSessionOpenAndNeverAtTheDecisionClose()
    {
        var order = new PlannedOrder("listing-aaa", Buy: true, 1102.2m, Jan31, Feb29);
        var plan = ExecutionPlanner.Plan(Slice(Mar20), [order], Loose);

        var fill = Assert.Single(plan.Fills);
        Assert.Empty(plan.Rejections);
        // 110 open × (1 + 10bps + 10bps) = 110.22; 1102.2 / 110.22 = 10 shares.
        Assert.Equal(110.22m, fill.FillPrice);
        Assert.Equal(110m, fill.ObservedOpen);
        Assert.Equal(10m, fill.Quantity);
        Assert.Equal(0.002m, fill.SlippageFromOpen);
        Assert.Equal("feb01-open", fill.PriceId);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 21, 0, 0, TimeSpan.Zero), fill.FillSessionCloseAt);
        // The decision session's close is the input to the decision, never the fill.
        Assert.True(fill.FillSessionCloseAt > Jan31);
        Assert.NotEqual("jan31-open", fill.PriceId);
    }

    [Fact]
    public void ASellFillsBelowTheObservedOpen()
    {
        var fill = Assert.Single(ExecutionPlanner.Plan(Slice(Mar20),
            [new PlannedOrder("listing-aaa", Buy: false, 1097.8m, Jan31, Feb29)], Loose).Fills);
        Assert.Equal(109.78m, fill.FillPrice); // 110 × (1 − 20bps)
        Assert.Equal(10m, fill.Quantity);
        Assert.Equal(-0.002m, fill.SlippageFromOpen);
    }

    [Fact]
    public void ADecisionWithNoFollowingSessionIsRefused()
    {
        var plan = ExecutionPlanner.Plan(Slice(Mar20),
            [new PlannedOrder("listing-aaa", true, 1000m, Mar20, Mar20.AddDays(30))], Loose);
        Assert.Equal(ExecutionRefusal.NoNextSession, Assert.Single(plan.Rejections).Reason);
        Assert.Empty(plan.Fills);
    }

    [Fact]
    public void AnOpenNotObservableBeforeTheNextDecisionIsRefused()
    {
        // The 10 February session's bar only became available well after the next decision.
        var late = Bar("feb10-open", new(2024, 2, 10, 21, 0, 0, TimeSpan.Zero), 115m, 4000m,
            new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero));
        var plan = ExecutionPlanner.Plan(Slice(Mar20, late),
            [new PlannedOrder("listing-aaa", true, 1000m, new(2024, 2, 1, 21, 0, 0, TimeSpan.Zero), Feb29)], Loose);
        Assert.Equal(ExecutionRefusal.OpenNotObservable, Assert.Single(plan.Rejections).Reason);
        Assert.Empty(plan.Fills);
    }

    [Fact]
    public void AMinimumOrderAndAQuantityStepRefuseRatherThanShrinkSilently()
    {
        var tooSmall = ExecutionPlanner.Plan(Slice(Mar20), [new PlannedOrder("listing-aaa", true, 50m, Jan31, Feb29)],
            Loose with { MinimumOrderValue = 100m });
        Assert.Equal(ExecutionRefusal.BelowMinimum, Assert.Single(tooSmall.Rejections).Reason);

        // A step that snaps the derived quantity to zero is the same refusal, not a zero fill.
        var stepped = ExecutionPlanner.Plan(Slice(Mar20), [new PlannedOrder("listing-aaa", true, 50m, Jan31, Feb29)],
            Loose with { QuantityStep = 1m });
        Assert.Equal(ExecutionRefusal.BelowMinimum, Assert.Single(stepped.Rejections).Reason);
    }

    [Fact]
    public void AnOrderBeyondTheParticipationCapIsRefused()
    {
        // feb01 trades 5,000 shares; 5% is 250, and 100,000 at 110.22 wants about 907.
        var plan = ExecutionPlanner.Plan(Slice(Mar20), [new PlannedOrder("listing-aaa", true, 100000m, Jan31, Feb29)],
            Loose with { MaximumParticipation = 0.05m });
        Assert.Equal(ExecutionRefusal.VolumeParticipation, Assert.Single(plan.Rejections).Reason);
        Assert.Empty(plan.Fills);
    }

    [Fact]
    public void AStructuralActionBetweenDecisionAndFillIsRefused()
    {
        var split = new CorporateAction("aaa-split", Ev(Jan31), "listing-aaa", "aaa-split-event",
            CorporateActionKind.Split, new DateTimeOffset(2024, 2, 1, 12, 0, 0, TimeSpan.Zero), 2m,
            null, null, null, null, TerminalOutcome.NotApplicable);
        var plan = ExecutionPlanner.Plan(Slice(Mar20, split), [new PlannedOrder("listing-aaa", true, 1000m, Jan31, Feb29)], Loose);
        Assert.Equal(ExecutionRefusal.UnsupportedAction, Assert.Single(plan.Rejections).Reason);
        Assert.Empty(plan.Fills);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnOrderThatIsNotAnOrderIsRefused(int notional)
    {
        var plan = ExecutionPlanner.Plan(Slice(Mar20),
            [new PlannedOrder("listing-aaa", true, notional, Jan31, Feb29),
             new PlannedOrder("listing-aaa", true, 100m, Feb29, Jan31)], Loose);
        Assert.Equal(2, plan.Rejections.Length);
        Assert.All(plan.Rejections, r => Assert.Equal(ExecutionRefusal.InvalidOrder, r.Reason));
        Assert.Empty(plan.Fills);
    }

    [Fact]
    public void AnUnknownOrInactiveListingIsRefused()
    {
        var plan = ExecutionPlanner.Plan(Slice(Mar20), [new PlannedOrder("listing-gone", true, 1000m, Jan31, Feb29)], Loose);
        Assert.Equal(ExecutionRefusal.InvalidOrder, Assert.Single(plan.Rejections).Reason);
    }

    [Theory]
    [InlineData(-1, 5, 1, 0.05, 0)]
    [InlineData(5, -1, 1, 0.05, 0)]
    [InlineData(5, 5, 0, 0.05, 0)]
    [InlineData(5, 5, 1, 0, 0)]
    [InlineData(5, 5, 1, 2, 0)]
    [InlineData(5, 5, 1, 0.05, -1)]
    public void AnInvalidPolicyIsRefusedBeforeAnyPlanning(decimal spread, decimal slippage,
        decimal minimum, decimal participation, decimal step)
    {
        Assert.Equal("invalid-execution-policy", Assert.Throws<ResearchException>(() =>
            ExecutionPlanner.Plan(Slice(Mar20), [new PlannedOrder("listing-aaa", true, 1000m, Jan31, Feb29)],
                new(spread, slippage, minimum, participation, step))).Code);
    }

    [Fact]
    public void ThePlanIsADeterministicFunctionOfItsInputs()
    {
        var orders = new[] { new PlannedOrder("listing-aaa", true, 1000m, Jan31, Feb29),
            new PlannedOrder("listing-aaa", false, 2000m, Jan31, Feb29) };
        var first = ExecutionPlanner.Plan(Slice(Mar20), orders, Loose);
        var second = ExecutionPlanner.Plan(Slice(Mar20), orders, Loose);
        Assert.Equal(first.Fills.ToArray(), second.Fills.ToArray());
        Assert.Equal(first.Rejections.ToArray(), second.Rejections.ToArray());
    }

    [Fact]
    public void APlannedFillIsWhatTheLedgerBooks()
    {
        var fill = Assert.Single(ExecutionPlanner.Plan(Slice(Mar20),
            [new PlannedOrder("listing-aaa", true, 1102.2m, Jan31, Feb29)], Loose).Fills);
        var ledger = new RawGbpLedger();
        ledger.Deposit("deposit-1", Jan31, 2000m);
        ledger.Fill("fill-1", Slice(Mar20), fill.ListingId, fill.Quantity, fill.FillPrice, PriceBasis.Raw, 1m,
            TimeSpan.FromDays(3650));

        // 10 shares at 110.22 with a 1 GBP fee on a GBP listing: no FX is invented.
        Assert.Equal(896.8m, ledger.CashGbp);
        Assert.Equal(10m, ledger.Holdings["listing-aaa"].Quantity);
    }
}
