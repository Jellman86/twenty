using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static DataSlice RebalanceMarket(DataSlice market, bool eligibleA = true, bool eligibleB = true, decimal secondPrice = 50)
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var listing = market.Observations.OfType<Listing>().Single(l => l.ListingId == "listing-a");
        var bar = market.Observations.OfType<PriceBar>().First(p => p.ListingId == "listing-a");
        return new PointInTimeDataset(fixture with { Observations = market.Observations.AddRange(ImmutableArray.Create<Observation>(
            listing with { Id = "listing-b-row", ListingId = "listing-b", SecurityId = "security-b", CompanyId = "company-b", Symbol = "BBB",
                ProviderIds = [new("fixture", "BBB")] },
            bar with { Id = "raw-b", ListingId = "listing-b", Open = secondPrice, High = secondPrice, Low = secondPrice, Close = secondPrice },
            new Membership("member-a", Known(BuyAt.AddDays(-1)), "synthetic-isa", "listing-a", BuyAt.AddDays(-1), null, eligibleA),
            new Membership("member-b", Known(BuyAt.AddDays(-1)), "synthetic-isa", "listing-b", BuyAt.AddDays(-1), null, eligibleB))) }).AsOf(market.DecisionAt);
    }
    private static RawRebalancePlan Rebalance(RawGbpLedger ledger, DataSlice market, IReadOnlyDictionary<string, decimal> weights,
        decimal reserve = 0, decimal fee = 0) => RawRebalancePlanner.Plan(ledger, market, "synthetic-isa", weights,
            market.DecisionAt.AddDays(7), reserve, fee, Age, Age);

    [Fact]
    public void RawWeightsProduceFrozenShareTargetsSellsFirstAndAnExplicitFeeReserve()
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt.AddDays(1)));
        var before = ledger.Entries;
        var plan = Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-b"] = 1 }, 10, 1);
        Assert.Equal(300, plan.Valuation.EquityGbp); Assert.Equal(2, plan.FeeBudgetGbp);
        Assert.Equal(Age.Ticks, plan.MaximumPriceAgeTicks); Assert.Equal(Age.Ticks, plan.MaximumFxAgeTicks);
        Assert.Equal(288, plan.InvestableEquityGbp); Assert.Equal(10, plan.ProjectedCashAfterFeesGbp);
        Assert.Equal(new[] { "listing-a", "listing-b" }, plan.Orders.Select(o => o.ListingId));
        Assert.False(plan.Orders[0].Buy); Assert.Equal(2, plan.Orders[0].Quantity);
        Assert.True(plan.Orders[1].Buy); Assert.Equal(7.2m, plan.Orders[1].Quantity);
        Assert.All(plan.Orders, o => { Assert.Equal(0, o.NotionalMajor); Assert.Equal("fx", o.FxObservationId); Assert.Equal(market.DecisionAt, o.DecisionAt); });
        Assert.Equal("raw-b", plan.Targets[1].Quote.Price.Id); Assert.Equal("fixture-source", plan.Targets[1].Quote.PriceSource.Id);
        Assert.Equal(.8m, plan.Targets[1].Quote.Fx!.QuoteUnitsPerBase);
        Assert.Equal(before, ledger.Entries); Assert.Equal(140, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void PenceTargetsUseMajorCurrencyValueAndDoNotScaleShareCounts()
    {
        var units = new Denomination("GBP", QuoteUnit.Pence); var ledger = new RawGbpLedger();
        var market = RebalanceMarket(Market(BuyAt, units, 1000));
        ledger.Deposit("cash", BuyAt, 30); ledger.Fill("buy", market, "listing-a", 2, 1000, PriceBasis.Raw, 0, Age);
        var plan = Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = 1 }, 1, .5m);
        var order = Assert.Single(plan.Orders); Assert.True(order.Buy); Assert.Equal(.85m, order.Quantity); Assert.Null(order.FxObservationId);
        Assert.Equal(28.5m, plan.InvestableEquityGbp); Assert.Equal(1, plan.ProjectedCashAfterFeesGbp);
    }

    [Fact]
    public void LostEligibilityPermitsAnExitButRefusesPositiveTargetExposure()
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt.AddDays(1)), eligibleA: false);
        var exit = Rebalance(ledger, market, new Dictionary<string, decimal>());
        Assert.False(Assert.Single(exit.Orders).Buy); Assert.Equal(2, exit.Orders[0].Quantity);
        Assert.Equal("raw-rebalance-ineligible", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = 1 })).Code);
    }

    [Fact]
    public void PendingDividendClaimsCannotBecomeRebalanceBuyingPower()
    {
        var ledger = DividendBook(); var dividend = Dividend(); var at = dividend.EffectiveAt;
        var market = RebalanceMarket(Market(at, actions: [dividend]));
        ledger.CaptureDividend("dividend", market, "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        Assert.Equal("raw-ledger-dividend-unpaid", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-b"] = 1 })).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void ExplicitClaimAwarePlanUsesExpectedTargetsButFundsOnlyWithLiquidCapital()
    {
        var ledger = DividendBook(); var action = Dividend();
        var market = RebalanceMarket(Market(action.EffectiveAt, actions: [action]));
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var before = ledger.Entries;
        var plan = RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa",
            new Dictionary<string, decimal> { ["listing-b"] = 1 }, market.DecisionAt.AddDays(7), 0, 0, Age, Age,
            [DividendMark(action)], []);
        Assert.Equal(302.2m, plan.ExpectedTargetEquityGbp);
        Assert.Equal(DividendMark(action), Assert.Single(plan.ClaimAssumptions!.Dividends));
        Assert.Equal(2.2m, plan.DeferredOrderValueGbp);
        Assert.Equal(300, plan.Valuation.EquityGbp); Assert.Equal(300, plan.InvestableEquityGbp);
        Assert.Equal(7.5m, plan.Orders.Single(o => o.Buy).Quantity); Assert.Equal(0, plan.ProjectedCashAfterFeesGbp);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(before, ledger.Entries);
        Assert.Equal("raw-ledger-dividend-valuation", Assert.Throws<ResearchException>(() =>
            RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", new Dictionary<string, decimal>(),
                market.DecisionAt.AddDays(7), 0, 0, Age, Age, [], [])).Code);
    }

    [Fact]
    public void ExpectedTargetsAvoidLiquidatingSurvivorsWhileTerminalCashIsPending()
    {
        var ledger = new RawGbpLedger(); var opening = RebalanceMarket(Market(BuyAt));
        ledger.Deposit("deposit", BuyAt, 320); ledger.Fill("a", opening, "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        ledger.Fill("b", opening, "listing-b", 4, 50, PriceBasis.Raw, 0, Age);
        var action = Terminal() with { CashPerShare = 100 }; var market = RebalanceMarket(Market(TerminalAt, actions: [action]));
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var plan = RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", new Dictionary<string, decimal> { ["listing-b"] = .5m },
            market.DecisionAt.AddDays(7), 0, 1, Age, Age, [], [TerminalMark(action, 0, 0)]);
        Assert.Empty(plan.Orders); Assert.Equal(0, plan.FeeBudgetGbp); Assert.Equal(320, plan.ExpectedTargetEquityGbp);
        Assert.Equal(4, Assert.Single(plan.Targets).TargetQuantity); Assert.Equal(0, plan.DeferredOrderValueGbp);
    }

    [Fact]
    public void FullyIlliquidCashClaimDefersTargetsAndDisclosesReserveShortfallWithoutBorrowing()
    {
        var ledger = new RawGbpLedger(); var opening = RebalanceMarket(Market(BuyAt));
        ledger.Deposit("deposit", BuyAt, 320); ledger.Fill("a", opening, "listing-a", 4, 100, PriceBasis.Raw, 0, Age);
        var action = Terminal() with { CashPerShare = 100 }; var market = RebalanceMarket(Market(TerminalAt, actions: [action]));
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var plan = RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", new Dictionary<string, decimal> { ["listing-b"] = 1 },
            market.DecisionAt.AddDays(7), 20, 0, Age, Age, [], [TerminalMark(action, 0, 0)]);
        Assert.Empty(plan.Orders); Assert.Equal(0, plan.InvestableEquityGbp); Assert.Equal(0, plan.ProjectedCashAfterFeesGbp);
        Assert.Equal(20, plan.ReserveShortfallGbp); Assert.Equal(300, plan.DeferredOrderValueGbp);
        Assert.Equal(0, Assert.Single(plan.Targets).FundedTargetQuantity); Assert.Equal(0, ledger.CashGbp);
    }

    [Fact]
    public void NegativeExpectedIncomeKeepsItsFeeInCashInsteadOfSpendingItTwice()
    {
        var ledger = DividendBook(); var action = Dividend(); var market = RebalanceMarket(Market(action.EffectiveAt, actions: [action]));
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var plan = RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", new Dictionary<string, decimal> { ["listing-b"] = 1 },
            market.DecisionAt.AddDays(7), 0, 0, Age, Age, [DividendMark(action, 4, 1)], []);
        Assert.Equal(1, plan.ClaimCashReserveGbp); Assert.Equal(299, plan.ExpectedTargetEquityGbp);
        Assert.Equal(1, plan.ProjectedCashAfterFeesGbp); Assert.Equal(7.475m, plan.Orders.Single(o => o.Buy).Quantity);
        Assert.Equal(0, ledger.FeesGbp); Assert.Equal(140, ledger.CashGbp);
    }

    [Fact]
    public void RebalancePlansSnapshotWeightsAndAreDeterministic()
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt.AddDays(1)));
        var a = new Dictionary<string, decimal> { ["listing-b"] = .5m, ["listing-a"] = .25m };
        var b = new Dictionary<string, decimal> { ["listing-a"] = .25m, ["listing-b"] = .5m };
        var plan = Rebalance(ledger, market, a);
        Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(Rebalance(ledger, market, b)));
        a["listing-b"] = 1; Assert.Equal(.5m, plan.Weights["listing-b"]);
    }
    [Fact]
    public void AlreadyMatchingTargetsDoNotCreateTradesMerelyToFundUnneededFees()
    {
        var ledger = new RawGbpLedger(); var market = RebalanceMarket(Market(BuyAt));
        ledger.Deposit("cash", BuyAt, 160); ledger.Fill("buy", market, "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        var plan = Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = 1 }, fee: 1);
        Assert.Empty(plan.Orders); Assert.Equal(0, plan.FeeBudgetGbp); Assert.Equal(160, plan.InvestableEquityGbp);
        Assert.Equal(0, plan.ProjectedCashAfterFeesGbp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.1)]
    public void InvalidTargetWeightsRefuseWithoutMutatingTheLedger(decimal weight)
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt)); var before = ledger.Entries;
        Assert.Equal("raw-rebalance-weights", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = weight })).Code);
        Assert.Equal(before, ledger.Entries);
    }

    [Fact]
    public void OverallocatedTargetsAndInsufficientReserveAreRefused()
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt));
        Assert.Equal("raw-rebalance-weights", Assert.Throws<ResearchException>(() => Rebalance(ledger, market,
            new Dictionary<string, decimal> { ["listing-a"] = .6m, ["listing-b"] = .5m })).Code);
        Assert.Equal("raw-rebalance-reserve", Assert.Throws<ResearchException>(() => Rebalance(ledger, market,
            new Dictionary<string, decimal> { ["listing-b"] = 1 }, reserve: 299, fee: 1)).Code);
    }

    [Theory]
    [InlineData("missing-price")]
    [InlineData("adjusted-price")]
    [InlineData("stale-price")]
    [InlineData("stale-fx")]
    [InlineData("reference-fx")]
    public void NewTargetsCannotBypassRawPriceOrExecutableFxRequirements(string problem)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 300);
        var at = BuyAt.AddDays(4);
        var market = RebalanceMarket(Market(at, priceAgeDays: problem == "stale-price" ? 3 : 0,
            fxAgeDays: problem == "stale-fx" ? 3 : 0, purpose: problem == "reference-fx" ? FxPurpose.Reference : FxPurpose.Executable));
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        if (problem is "missing-price" or "adjusted-price") market = new PointInTimeDataset(fixture with
        { Observations = market.Observations.Where(o => !(problem == "missing-price" && o is PriceBar p && p.ListingId == "listing-b"))
            .Select(o => problem == "adjusted-price" && o is PriceBar p && p.ListingId == "listing-b" ? p with { Basis = PriceBasis.TotalReturnAdjusted } : o).ToImmutableArray() }).AsOf(at);
        if (problem is "stale-fx" or "reference-fx") Assert.Throws<InvalidDatasetException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-b"] = 1 }));
        else Assert.Equal("raw-rebalance-price", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-b"] = 1 })).Code);
        Assert.Single(ledger.Entries); Assert.Equal(300, ledger.CashGbp);
    }

    private static (RawGbpLedger Ledger, DatasetFile File, ExecutionCalendar Calendar, DateTimeOffset Decision, DateTimeOffset Booked) RebalanceExecutionFixture(
        decimal firstOpen = 80, decimal secondOpen = 40, decimal futureFx = .9m)
    {
        var decision = BuyAt.AddDays(1); var booked = decision.AddDays(1); var close = booked.AddMinutes(-30);
        var market = RebalanceMarket(Market(decision));
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var calendar = new ExecutionCalendar(market.Calendars.Single(c => c.Id == "fixture-london"), "XLON",
            [new(DateOnly.FromDateTime(decision.UtcDateTime), decision.AddHours(-9), decision.AddMinutes(-30)),
             new(DateOnly.FromDateTime(booked.UtcDateTime), booked.AddHours(-9), close)], "urn:twenty:authored:rebalance-sessions");
        var rows = market.Observations.ToBuilder();
        foreach (var p in market.Observations.OfType<PriceBar>())
        {
            var open = p.ListingId == "listing-a" ? firstOpen : secondOpen;
            rows.Add(p with { Id = "execution-" + p.ListingId, SessionDate = DateOnly.FromDateTime(close.UtcDateTime), SessionCloseAt = close,
                Evidence = Known(close.AddMinutes(2)), Open = open, High = open, Low = open, Close = open });
        }
        rows.Add(new FxRate("initial-fx", Known(BuyAt.AddMinutes(-1)), "USD", "GBP", .8m, BuyAt.AddMinutes(-1), FxPurpose.Executable));
        var nextFxAt = booked.AddHours(-9).AddSeconds(-30);
        rows.Add(new FxRate("later-fx", Known(nextFxAt), "USD", "GBP", futureFx, nextFxAt, FxPurpose.Executable));
        return (DividendBook(), fixture with { Observations = rows.ToImmutable() }, calendar, decision, booked);
    }

    [Fact]
    public void DecisionWeightsExecuteExactSharesAcrossGapsAndKeepTheDecisionFxPin()
    {
        var f = RebalanceExecutionFixture(); var dataset = new PointInTimeDataset(f.File);
        var decision = dataset.AsOf(f.Decision); var market = dataset.AsOf(f.Booked);
        var plan = Rebalance(f.Ledger, decision, new Dictionary<string, decimal> { ["listing-b"] = 1 }, 10, 1);
        var fills = ExecutionPlanner.Plan(market, plan.Orders, new(0, 0, 1, 1), f.Calendar, Age);
        Assert.Empty(fills.Rejections); Assert.Equal(2, fills.Fills.Length);
        for (var i = 0; i < fills.Fills.Length; i++)
        {
            var fill = fills.Fills[i];
            f.Ledger.Fill("rebalance-" + i, market, fill.ListingId, fill.Buy ? fill.Quantity : -fill.Quantity,
                fill.FillPrice, PriceBasis.Raw, 1, Age, fill, f.Calendar);
        }
        Assert.False(fills.Fills[0].Buy); Assert.Equal(2, fills.Fills[0].Quantity); Assert.Equal(80, fills.Fills[0].FillPrice);
        Assert.All(fills.Fills, fill => Assert.Equal("fx", fill.ExecutionFx!.Id));
        Assert.Equal(7.2m, f.Ledger.Holdings["listing-b"].Quantity); Assert.False(f.Ledger.Holdings.ContainsKey("listing-a"));
        Assert.Equal(35.6m, f.Ledger.CashGbp); Assert.Equal(2, f.Ledger.FeesGbp); Assert.Equal(300, f.Ledger.ContributionsGbp);
        var closing = f.Ledger.Value(market, Age, Age, FxPurpose.Executable);
        Assert.Equal(294.8m, closing.EquityGbp); Assert.Equal("later-fx", Assert.Single(closing.Positions).FxId);
    }

    [Fact]
    public void FutureOpeningPricesAndFxDoNotAlterTheDecisionPlan()
    {
        var a = RebalanceExecutionFixture(); var b = RebalanceExecutionFixture(1, 10000, 2);
        var weights = new Dictionary<string, decimal> { ["listing-b"] = 1 };
        Assert.Equal(JsonSerializer.Serialize(Rebalance(a.Ledger, new PointInTimeDataset(a.File).AsOf(a.Decision), weights, 10, 1)),
            JsonSerializer.Serialize(Rebalance(b.Ledger, new PointInTimeDataset(b.File).AsOf(b.Decision), weights, 10, 1)));
    }

    [Fact]
    public void FeesMustBeFundedAtEveryStepInTheDeclaredSellOrder()
    {
        var market = RebalanceMarket(Market(BuyAt)); var ledger = new RawGbpLedger();
        ledger.Deposit("cash", BuyAt, 40.08m);
        ledger.Fill("tiny", market, "listing-a", .001m, 100, PriceBasis.Raw, 0, Age);
        ledger.Fill("large", market, "listing-b", 1, 50, PriceBasis.Raw, 0, Age);
        var before = ledger.Entries;
        Assert.Equal("raw-rebalance-cash", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal>(), fee: 1)).Code);
        Assert.Equal(before, ledger.Entries); Assert.Equal(0, ledger.CashGbp);
    }

    [Fact]
    public void PendingTerminalClaimsAlsoRefuseDecisionFunding()
    {
        var ledger = DividendBook(); var terminal = Terminal();
        var market = RebalanceMarket(Market(TerminalAt, actions: [terminal]));
        ledger.CaptureTerminal("exit", market, "listing-a", terminal.ActionId, TerminalProof(terminal));
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-b"] = 1 })).Code);
    }

    [Fact]
    public void AStaleActiveListingWithKnownTerminalActionCannotBecomeANewTarget()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 300);
        var market = RebalanceMarket(Market(TerminalAt, actions: [Terminal()]));
        Assert.Equal("raw-rebalance-terminal", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = 1 })).Code);
    }

    [Fact]
    public void TargetCountIsBoundedBeforeUniverseResolution()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 300);
        var weights = Enumerable.Range(0, RawRebalancePlanner.MaximumTargets + 1).ToDictionary(i => "unknown-" + i, _ => .001m);
        Assert.Equal("raw-rebalance-weights", Assert.Throws<ResearchException>(() => Rebalance(ledger, RebalanceMarket(Market(BuyAt)), weights)).Code);
    }

    private static (RawSchedulePlan Schedule, byte[] Dataset, RawRebalancePlan Decision) RebalanceScheduleFixture(decimal secondOpen = 40)
    {
        var f = RebalanceExecutionFixture(secondOpen: secondOpen); var dataset = new PointInTimeDataset(f.File);
        var decision = Rebalance(f.Ledger, dataset.AsOf(f.Decision), new Dictionary<string, decimal> { ["listing-b"] = 1 }, 10, 1);
        var plan = ExecutionPlanner.Plan(dataset.AsOf(f.Booked), decision.Orders, new(0, 0, 1, 1), f.Calendar, Age);
        Assert.Empty(plan.Rejections);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(f.File, DatasetJson.Options);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var timeline = ImmutableArray.CreateBuilder<RawScheduleEvent>();
        timeline.Add(new(new("deposit", 0, hash), BuyAt, new RawReplayDeposit(300)));
        timeline.Add(new(new("buy", 1, hash), BuyAt, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)));
        for (var i = 0; i < plan.Fills.Length; i++) timeline.Add(new(new("rebalance-" + i, i, hash), f.Booked,
            new RawReplayModelledFill(decision.Orders[i], new(0, 0, 1, 1), plan.Fills[i], 1, f.Calendar)));
        timeline.Add(new(new("closing", plan.Fills.Length, hash), f.Booked, new RawReplayMark([], [])));
        return (new(RawEventSchedule.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current, Age.Ticks, Age.Ticks,
            FxPurpose.Executable, timeline.ToImmutable(), []), bytes, decision);
    }

    [Fact]
    public void QuantityRebalanceOrdersSurviveScheduleArchiveAndFreshReplay()
    {
        var f = RebalanceScheduleFixture();
        var compiled = RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(f.Schedule, RawReplayJson.Options), _ => f.Dataset);
        var receipt = RawReplayArchive.Execute(compiled, _ => f.Dataset);
        Assert.Equal(receipt, RawReplayArchive.Verify(JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options), _ => f.Dataset));
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(35.6m, result.CashGbp); Assert.Equal(7.2m, result.Holdings["listing-b"].Quantity);
        Assert.Equal(294.8m, Assert.Single(result.Marks).Valuation.ExpectedEquityGbp);
        Assert.Equal(new decimal?[] { 2, 7.2m }, result.Entries.Where(e => e.ModelledExecution is not null).Select(e => e.ModelledExecution!.RequestedQuantity));
    }

    [Fact]
    public void OpeningGapsCannotSilentlyResizeTheFrozenBuyOrPublishAPartialReplay()
    {
        var f = RebalanceScheduleFixture(secondOpen: 100);
        var compiled = RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(f.Schedule, RawReplayJson.Options), _ => f.Dataset);
        var root = Path.Combine(Path.GetTempPath(), "twenty-rebalance-gap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => RawReplayArchive.Store(root, compiled, _ => f.Dataset)).Code);
            Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnrepresentableDecisionUnitsOrShareCountsRefuseInsteadOfBecomingZeroTargets(bool unitUnderflows)
    {
        var ledger = new RawGbpLedger(); var tiny = .0000000000000000000000000001m;
        var units = new Denomination(unitUnderflows ? "USD" : "GBP", QuoteUnit.Major);
        var market = RebalanceMarket(Market(BuyAt, units, unitUnderflows ? tiny : 10000000000000000000000000000m,
            fx: unitUnderflows ? tiny : 1));
        ledger.Deposit("cash", BuyAt, unitUnderflows ? 1 : tiny);
        Assert.Equal("raw-rebalance-arithmetic", Assert.Throws<ResearchException>(() =>
            Rebalance(ledger, market, new Dictionary<string, decimal> { ["listing-a"] = 1 })).Code);
        Assert.Single(ledger.Entries);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("fee")]
    [InlineData("deadline")]
    public void RebalancePolicyCannotCreateMoneyOrUseANonfutureDeadline(string problem)
    {
        var ledger = DividendBook(); var market = RebalanceMarket(Market(BuyAt));
        Assert.Equal("raw-rebalance-policy", Assert.Throws<ResearchException>(() => RawRebalancePlanner.Plan(ledger, market, "synthetic-isa",
            new Dictionary<string, decimal> { ["listing-b"] = 1 }, problem == "deadline" ? BuyAt : BuyAt.AddDays(1),
            problem == "reserve" ? -1 : 0, problem == "fee" ? -1 : 0, Age, Age)).Code);
    }

    [Fact]
    public void EquivalentDecisionOffsetsProduceIdenticalFrozenPlans()
    {
        var f = RebalanceExecutionFixture(); var dataset = new PointInTimeDataset(f.File);
        var weights = new Dictionary<string, decimal> { ["listing-b"] = 1 };
        Assert.Equal(JsonSerializer.Serialize(Rebalance(f.Ledger, dataset.AsOf(f.Decision), weights)),
            JsonSerializer.Serialize(Rebalance(f.Ledger, dataset.AsOf(f.Decision.ToOffset(TimeSpan.FromHours(3))), weights)));
    }

    [Fact]
    public void AnOmittedHoldingCannotBecomeANoOpJustBecauseItsDecimalMarkIsZero()
    {
        var tiny = .0000000000000000000000000001m;
        var market = RebalanceMarket(Market(BuyAt, new("GBP", QuoteUnit.Major), tiny));
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 100);
        ledger.Fill("tiny-holding", market, "listing-a", tiny, tiny, PriceBasis.Raw, 0, Age);
        Assert.Equal(0, Assert.Single(ledger.Value(market, Age, Age, FxPurpose.Executable).Positions).ValueGbp);
        var plan = Rebalance(ledger, market, new Dictionary<string, decimal>());
        var exit = Assert.Single(plan.Orders); Assert.False(exit.Buy); Assert.Equal(tiny, exit.Quantity);
        Assert.Equal(0, Assert.Single(plan.Targets).TargetQuantity);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void ClaimAwarePlanningHasAnExplicitPendingClaimBound(int count)
    {
        var ledger = DividendBook(); var actions = Enumerable.Range(0, count).Select(i => Dividend("income-" + i) with { EffectiveAt = BuyAt.AddHours(1).AddMinutes(i) }).ToImmutableArray();
        foreach (var action in actions) ledger.CaptureDividend("claim-" + action.ActionId, RebalanceMarket(Market(action.EffectiveAt, actions: actions)), "listing-a", action.ActionId, EntitlementEvidence(action));
        var market = RebalanceMarket(Market(actions[^1].EffectiveAt, actions: actions));
        var assumptions = actions.Select(a => DividendMark(a)).ToImmutableArray();
        RawRebalancePlan Plan() => RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", new Dictionary<string, decimal> { ["listing-b"] = 1 },
            market.DecisionAt.AddDays(7), 0, 0, Age, Age, assumptions, []);
        if (count == 8)
        {
            var plan = Plan(); Assert.Equal(8, plan.ClaimAssumptions!.Dividends.Length);
            Assert.Equal(317.6m, plan.ExpectedTargetEquityGbp); Assert.Equal(0, plan.ProjectedCashAfterFeesGbp);
            Assert.Equal(17.6m, plan.DeferredOrderValueGbp);
        }
        else Assert.Equal("raw-rebalance-claims-capacity", Assert.Throws<ResearchException>(() => Plan()).Code);
    }

    [Fact]
    public void ClaimAwareTargetCannotSilentlyUnderflowToZeroShares()
    {
        var tiny = .0000000000000000000000000001m; var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, tiny);
        var market = RebalanceMarket(Market(BuyAt, new("GBP", QuoteUnit.Major), 10000000000000000000000000000m));
        Assert.Equal("raw-rebalance-arithmetic", Assert.Throws<ResearchException>(() => RawRebalancePlanner.PlanWithReceivables(ledger,
            market, "synthetic-isa", new Dictionary<string, decimal> { ["listing-a"] = 1 }, market.DecisionAt.AddDays(7), 0, 0, Age, Age, [], [])).Code);
    }

}
