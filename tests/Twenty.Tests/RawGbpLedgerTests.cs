using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static readonly DateTimeOffset BuyAt = new(2024, 1, 2, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Age = TimeSpan.FromDays(2);
    private static Evidence Known(DateTimeOffset at) => new("fixture-source", at, at, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);
    private static DataSlice Market(DateTimeOffset at, Denomination? denomination = null, decimal price = 100m,
        decimal fx = .8m, FxPurpose purpose = FxPurpose.Executable, bool adjustedOnly = false,
        bool reverseFx = false, int fxAgeDays = 0, int priceAgeDays = 0, bool futureFx = false, bool split = false, ImmutableArray<CorporateAction> actions = default, ImmutableArray<FxRate> fxRates = default)
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var units = denomination ?? new("USD", QuoteUnit.Major);
        var start = BuyAt.AddDays(-1);
        var listing = fixture.Observations.OfType<Listing>().First() with { Evidence = Known(start), EffectiveFrom = start,
            EffectiveUntil = null, Denomination = units, Status = ListingStatus.Active };
        var close = new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero).AddHours(16).AddMinutes(30).AddDays(-priceAgeDays);
        var observed = at.AddDays(-fxAgeDays).AddMinutes(futureFx ? 1 : -1);
        var rows = ImmutableArray.CreateBuilder<Observation>(); rows.Add(listing);
        rows.Add(new PriceBar("raw", Known(close.AddMinutes(2)), listing.ListingId, DateOnly.FromDateTime(close.UtcDateTime), close,
            units, adjustedOnly ? PriceBasis.TotalReturnAdjusted : PriceBasis.Raw, price, price, price, price, 100));
        if (!fxRates.IsDefault) rows.AddRange(fxRates);
        else if (units.Currency != "GBP") rows.Add(new FxRate("fx", Known(observed), reverseFx ? "GBP" : units.Currency,
            reverseFx ? units.Currency : "GBP", fx, observed, purpose));
        if (split) rows.Add(new CorporateAction("split", Known(at.AddMinutes(-1)), listing.ListingId, "split-event", CorporateActionKind.Split,
            at.AddHours(-1), 2, null, null, null, null, TerminalOutcome.NotApplicable));
        if (!actions.IsDefault) rows.AddRange(actions);
        return new PointInTimeDataset(fixture with { Observations = rows.ToImmutable() }).AsOf(at);
    }

    [Fact]
    public void ForeignRawQuantitiesAndFxRevaluationMatchHandCalculation()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 1, Age);
        Assert.Equal(39, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
        var next = Market(BuyAt.AddDays(1), price: 110, fx: .75m);
        var mark = ledger.Value(next, Age, Age, FxPurpose.Executable);
        Assert.Equal(204, mark.EquityGbp); Assert.Equal(165, Assert.Single(mark.Positions).ValueGbp);
        ledger.Fill("sell", next, "listing-a", -.5m, 110, PriceBasis.Raw, .5m, Age);
        Assert.Equal(79.75m, ledger.CashGbp); Assert.Equal(1.5m, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(203.5m, ledger.Value(next, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(1.5m, ledger.FeesGbp); Assert.Equal(200, ledger.ContributionsGbp);
        Assert.Equal("fx", ledger.Entries[1].FxId); Assert.Equal(.8m, ledger.Entries[1].GbpPerLocalUnit);
    }

    [Fact]
    public void FullSaleRemovesThePositionAndValuationDoesNotMutateTheBook()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 100);
        var market = Market(BuyAt);
        ledger.Fill("buy", market, "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var entries = ledger.Entries;
        _ = ledger.Value(Market(BuyAt.AddDays(1), price: 110), Age, Age, FxPurpose.Executable);
        Assert.Equal(entries, ledger.Entries); Assert.Equal(20, ledger.CashGbp);
        ledger.Fill("sell", market, "listing-a", -1, 100, PriceBasis.Raw, 0, Age);
        Assert.Empty(ledger.Holdings); Assert.Equal(100, ledger.CashGbp);
        Assert.Equal(100, ledger.Value(Market(BuyAt.AddDays(1), adjustedOnly: true), Age, Age, FxPurpose.Reference).EquityGbp);
    }

    [Fact]
    public void PenceScalingAppliesToRawPriceButNotShareQuantity()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 25);
        var gbp = new Denomination("GBP", QuoteUnit.Pence);
        ledger.Fill("buy", Market(BuyAt, gbp, 1050), "listing-a", 2, 1050, PriceBasis.Raw, .2m, Age);
        Assert.Equal(3.8m, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(25.8m, ledger.Value(Market(BuyAt.AddDays(1), gbp, 1100), Age, Age, FxPurpose.Reference).EquityGbp);
        Assert.Null(ledger.Entries[1].FxId); Assert.Equal(1, ledger.Entries[1].GbpPerLocalUnit);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("stale")]
    [InlineData("future")]
    public void FillRequiresAvailableFreshExecutableFxInTheDeclaredDirection(string problem)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        var market = Market(BuyAt, purpose: problem == "reference" ? FxPurpose.Reference : FxPurpose.Executable,
            reverseFx: problem == "reverse", fxAgeDays: problem == "stale" ? 3 : 0, futureFx: problem == "future");
        Assert.Throws<InvalidDatasetException>(() => ledger.Fill("buy", market, "listing-a", 1, 100, PriceBasis.Raw, 0, Age));
        Assert.Equal(200, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Single(ledger.Entries);
    }

    [Fact]
    public void ReferenceValuationIsLabelledAndCannotSupplyAnExecutableFill()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var next = Market(BuyAt.AddDays(1), purpose: FxPurpose.Reference, fx: .7m);
        var mark = ledger.Value(next, Age, Age, FxPurpose.Reference);
        Assert.Equal(190, mark.EquityGbp); Assert.Equal(FxPurpose.Reference, mark.FxPurpose);
        Assert.Throws<InvalidDatasetException>(() => ledger.Value(next, Age, Age, FxPurpose.Executable));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public void MissingRawOrStalePricesCannotUseAdjustedFallback(bool adjusted, int age)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-price", Assert.Throws<ResearchException>(() => ledger.Value(
            Market(BuyAt.AddDays(4), adjustedOnly: adjusted, priceAgeDays: age), Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void RefusedFillDoesNotConsumeIdentityOrPartiallyMutateTheBook()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 100);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age)).Code);
        ledger.Deposit("more", BuyAt, 100);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal(40, ledger.CashGbp); Assert.Equal(3, ledger.Entries.Length);
        Assert.Equal("raw-ledger-event", Assert.Throws<ResearchException>(() => ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-quantity", Assert.Throws<ResearchException>(() => ledger.Fill("sell", Market(BuyAt), "listing-a", -3, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-basis", Assert.Throws<ResearchException>(() => ledger.Fill("adjusted", Market(BuyAt), "listing-a", 1, 100, PriceBasis.TotalReturnAdjusted, 0, Age)).Code);
        Assert.Equal(40, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void TimeAndChangedDenominationAreRefusedWithoutReinterpretingPositions()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-time", Assert.Throws<ResearchException>(() => ledger.Deposit("past", BuyAt.AddMinutes(-1), 1)).Code);
        Assert.Equal("raw-ledger-units", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1), new("GBP", QuoteUnit.Major)), Age, Age, FxPurpose.Executable)).Code);
    }

    private static CorporateAction Split(string id = "split-event", decimal ratio = 2, int effectiveHours = 1) =>
        new(id + "-observation", Known(BuyAt.AddMinutes(1)), "listing-a", id, CorporateActionKind.Split,
            BuyAt.AddHours(effectiveHours), ratio, null, null, null, null, TerminalOutcome.NotApplicable);

    [Fact]
    public void ExplicitSplitPreservesCashAndValueAndCannotBeBookedTwice()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(); var next = Market(BuyAt.AddDays(1), price: 50, actions: [action]);
        ledger.ApplySplit("settle", next, "listing-a", action.ActionId);
        Assert.Equal(2, ledger.Holdings["listing-a"].Quantity); Assert.Equal(120, ledger.CashGbp);
        Assert.Equal(200, ledger.Value(next, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(0, ledger.FeesGbp); Assert.Equal(200, ledger.ContributionsGbp);
        Assert.Null(ledger.Entries[^1].GbpPerLocalUnit); Assert.Null(ledger.Entries[^1].FxId);
        Assert.Equal("raw-ledger-action-already-booked", Assert.Throws<ResearchException>(() => ledger.ApplySplit("another", next, "listing-a", action.ActionId)).Code);
        Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void PendingSplitsMustBeSettledInEffectiveOrderEvenWhenBookedLate()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var first = Split(); var second = Split("second", 2, 2);
        var next = Market(BuyAt.AddDays(1), price: 25, actions: [first, second]);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.ApplySplit("out-of-order", next, "listing-a", second.ActionId)).Code);
        ledger.ApplySplit("first", next, "listing-a", first.ActionId);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Value(next, Age, Age, FxPurpose.Executable)).Code);
        ledger.ApplySplit("out-of-order", next, "listing-a", second.ActionId);
        Assert.Equal(4, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(200, ledger.Value(next, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(first.EffectiveAt, ledger.Entries[2].ActionEffectiveAt);
        Assert.Equal(first.Id, ledger.Entries[2].ActionObservationId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-.5)]
    public void LateSplitCannotReinterpretAnInterveningFill(decimal quantity)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 400);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        ledger.Fill("unknown-action-fill", Market(BuyAt.AddHours(2)), "listing-a", quantity, 100, PriceBasis.Raw, 0, Age);
        var cash = ledger.CashGbp; var held = ledger.Holdings["listing-a"].Quantity;
        Assert.Equal("raw-ledger-action-history", Assert.Throws<ResearchException>(() => ledger.ApplySplit("split", Market(BuyAt.AddDays(1), actions: [Split()]), "listing-a", "split-event")).Code);
        Assert.Equal(cash, ledger.CashGbp); Assert.Equal(held, ledger.Holdings["listing-a"].Quantity); Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void BookedSplitRevisionOrMissingEvidenceRequiresExplicitReconstruction()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var split = Split();
        ledger.ApplySplit("settle", Market(BuyAt.AddHours(1), actions: [split]), "listing-a", split.ActionId);
        var revision = split with { Id = "revised-split", SplitRatio = 1,
            Evidence = Known(BuyAt.AddHours(2)) with { RevisionOf = split.Id } };
        Assert.Equal("raw-ledger-action-revised", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1), actions: [split, revision]), Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal("raw-ledger-action-evidence", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1)), Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal(2, ledger.Holdings["listing-a"].Quantity); Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void UnchangedSplitVintageDoesNotRewriteTheReceiptOrBlockLaterValuation()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var split = Split();
        ledger.ApplySplit("settle", Market(BuyAt.AddHours(1), actions: [split]), "listing-a", split.ActionId);
        var receipt = ledger.Entries;
        var repeated = split with { Id = "repeated", Evidence = Known(BuyAt.AddHours(2)) with { RevisionOf = split.Id } };
        Assert.Equal(200, ledger.Value(Market(BuyAt.AddDays(1), price: 50, actions: [split, repeated]), Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(receipt, ledger.Entries); Assert.Equal(split.Id, ledger.Entries[2].ActionObservationId);
    }

    [Fact]
    public void FutureSplitIsNotBookableAndFailedAttemptDoesNotConsumeIdentity()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split();
        Assert.Equal("raw-ledger-action", Assert.Throws<ResearchException>(() => ledger.ApplySplit("settle", Market(BuyAt.AddMinutes(30), actions: [action]), "listing-a", action.ActionId)).Code);
        ledger.ApplySplit("settle", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId);
        Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void EarlierUnsettledDividendCannotBeSkippedToApplyASplit()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var split = Split(); var dividend = split with { Id = "dividend", ActionId = "dividend-event", Action = CorporateActionKind.Dividend,
            EffectiveAt = BuyAt.AddMinutes(30), SplitRatio = null, CashPerShare = 1, CashCurrency = "USD" };
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.ApplySplit("settle", Market(BuyAt.AddDays(1), actions: [split, dividend]), "listing-a", split.ActionId)).Code);
        Assert.Equal(1, ledger.Holdings["listing-a"].Quantity); Assert.Equal(2, ledger.Entries.Length);
    }

    [Theory]
    [InlineData(.25, 400)]
    [InlineData(.5, 200)]
    [InlineData(4, 25)]
    public void ExactFractionalOrForwardSplitQuantitiesConserveValue(decimal ratio, decimal price)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(ratio: ratio); var next = Market(BuyAt.AddDays(1), price: price, actions: [action]);
        ledger.ApplySplit("settle", next, "listing-a", action.ActionId);
        Assert.Equal(ratio, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(200, ledger.Value(next, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Fact]
    public void APostSplitBuyerHasNoOldQuantityToSettle()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        var action = Split(); var next = Market(BuyAt.AddHours(2), price: 50, actions: [action]);
        ledger.Fill("buy", next, "listing-a", 1, 50, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-action-position", Assert.Throws<ResearchException>(() => ledger.ApplySplit("settle", next, "listing-a", action.ActionId)).Code);
        Assert.Equal(1, ledger.Holdings["listing-a"].Quantity); Assert.Equal(160, ledger.CashGbp);
    }

    [Fact]
    public void ClosingAPositionDoesNotEraseItsBookedActionEvidence()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(); var next = Market(BuyAt.AddHours(1), price: 50, actions: [action]);
        ledger.ApplySplit("settle", next, "listing-a", action.ActionId);
        ledger.Fill("sell", next, "listing-a", -2, 50, PriceBasis.Raw, 0, Age);
        Assert.Empty(ledger.Holdings);
        var revised = action with { Id = "changed", SplitRatio = 3, Evidence = Known(BuyAt.AddHours(2)) with { RevisionOf = action.Id } };
        Assert.Equal("raw-ledger-action-revised", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1), actions: [action, revised]), Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal(200, ledger.CashGbp);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("successor")]
    [InlineData("terminal")]
    [InlineData("symbol")]
    public void SplitSettlementCannotDiscardAdditionalConsideration(string kind)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split();
        action = kind switch
        {
            "cash" => action with { CashPerShare = 1, CashCurrency = "USD" },
            "successor" => action with { SuccessorListingId = "listing-a" },
            "terminal" => action with { TerminalOutcome = TerminalOutcome.Zero },
            _ => action with { NewSymbol = "NEW" }
        };
        Assert.Equal("raw-ledger-action-complex", Assert.Throws<ResearchException>(() => ledger.ApplySplit("settle", Market(BuyAt.AddDays(1), actions: [action]), "listing-a", action.ActionId)).Code);
        Assert.Equal(1, ledger.Holdings["listing-a"].Quantity); Assert.Equal(2, ledger.Entries.Length);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("symbol")]
    [InlineData("dividend")]
    public void NominallyNeutralActionsCannotHideOtherEconomicTerms(string kind)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(ratio: 1);
        action = kind switch
        {
            "split" => action with { CashPerShare = 1, CashCurrency = "USD" },
            "symbol" => action with { Action = CorporateActionKind.SymbolChange, NewSymbol = "NEW", CashPerShare = 1, CashCurrency = "USD" },
            _ => action with { Action = CorporateActionKind.Dividend, SplitRatio = 2, CashPerShare = 0, CashCurrency = "USD" }
        };
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1), actions: [action]), Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void UnhandledCorporateActionsBlockRatherThanLeavingOldShareCounts()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var next = Market(BuyAt.AddDays(1), split: true);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Value(next, Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Fill("after-split", next, "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal(120, ledger.CashGbp); Assert.Equal(1, ledger.Holdings["listing-a"].Quantity);
    }
}
