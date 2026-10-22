using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static readonly DateTimeOffset TerminalAt = BuyAt.AddDays(1);
    private static CorporateAction Terminal(TerminalOutcome outcome = TerminalOutcome.Cash) =>
        new("terminal-observation", Known(TerminalAt), "listing-a", "terminal", CorporateActionKind.Delisting,
            TerminalAt, outcome == TerminalOutcome.Stock ? 1.5m : null,
            outcome == TerminalOutcome.Cash ? 110 : null, outcome == TerminalOutcome.Cash ? "USD" : null,
            null, outcome == TerminalOutcome.Stock ? "successor" : null, outcome);
    private static RawTerminalEvidence TerminalProof(CorporateAction action) =>
        new(action.EffectiveAt, action.EffectiveAt, "urn:twenty:authored:terminal-entitlement", new string('a', 64));
    private static RawTerminalSettlement CashSettlement(DateTimeOffset at) =>
        new("terminal-cash-1", "urn:twenty:authored:terminal-settlement", new string('b', 64), at, at,
            "USD", 220, 20, .5m, "fx", null, 0);

    private static DataSlice TerminalMarket(DateTimeOffset at, CorporateAction action, bool ended = false,
        Denomination? successorUnits = null, decimal successorPrice = 80, ImmutableArray<CorporateAction> extra = default,
        ImmutableArray<FxRate> rates = default, Denomination? originalUnits = null, DateTimeOffset? successorStart = null)
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var market = Market(at, originalUnits, fx: .75m, fxRates: rates);
        var rows = market.Observations.ToList(); var original = rows.OfType<Listing>().Single();
        if (ended)
        {
            rows.Remove(original);
            rows.Add(original with { EffectiveUntil = TerminalAt });
        }
        var successor = original with { Id = "successor-listing", SecurityId = "successor-security", CompanyId = "successor-company",
            ListingId = "successor", Symbol = "NEW", ProviderIds = [new ProviderIdentifier("fixture", "NEW")],
            Denomination = successorUnits ?? new("USD", QuoteUnit.Major), EffectiveFrom = successorStart ?? original.EffectiveFrom };
        rows.Add(successor);
        var bar = rows.OfType<PriceBar>().Single();
        if (successor.EffectiveFrom <= bar.SessionCloseAt) rows.Add(bar with { Id = "successor-price", ListingId = "successor", Denomination = successor.Denomination,
            Open = successorPrice, High = successorPrice, Low = successorPrice, Close = successorPrice });
        if (ended && bar.SessionCloseAt >= TerminalAt) rows.Remove(bar);
        rows.Add(action); if (!extra.IsDefault) rows.AddRange(extra);
        return new PointInTimeDataset(fixture with { Observations = [.. rows] }).AsOf(at);
    }
    private static RawTerminalSettlement StockSettlement(DateTimeOffset at) =>
        new("terminal-stock-1", "urn:twenty:authored:stock-delivery", new string('c', 64), at, at,
            null, 0, 0, .5m, null, "successor", 3);

    [Fact]
    public void TerminalZeroRemovesSharesAndPreservesCashWithoutAConversion()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Zero);
        var market = Market(TerminalAt, actions: [action]);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        Assert.Empty(ledger.Holdings); Assert.Equal(140, ledger.CashGbp);
        Assert.Equal(TerminalAt, Assert.Single(ledger.TerminalEntitlements).SettledAt);
        Assert.Equal(140, ledger.Value(market, Age, Age, FxPurpose.Reference).EquityGbp);
        Assert.Null(ledger.Entries[^1].GbpPerLocalUnit);
        Assert.Equal(300, ledger.ContributionsGbp);
    }

    [Fact]
    public void TerminalCashIsPendingUntilExplicitNetSettlementAndIsNotCountedTwice()
    {
        var ledger = DividendBook(); var action = Terminal();
        var entitled = Market(TerminalAt, actions: [action]);
        ledger.CaptureTerminal("exit", entitled, "listing-a", action.ActionId, TerminalProof(action));
        Assert.Empty(ledger.Holdings); Assert.Equal(140, ledger.CashGbp);
        Assert.Equal(220, Assert.Single(ledger.TerminalEntitlements).GrossLocal);
        Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() =>
            ledger.Value(entitled, Age, Age, FxPurpose.Executable)).Code);
        var paid = TerminalAt.AddDays(2); var market = Market(paid, fx: .75m, actions: [action]);
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, CashSettlement(paid), Age);
        Assert.Equal(289.5m, ledger.CashGbp);
        Assert.Equal(289.5m, ledger.Value(market, Age, Age, FxPurpose.Reference).EquityGbp);
        Assert.Equal(.5m, ledger.FeesGbp); Assert.Equal(300, ledger.ContributionsGbp);
        Assert.Equal(paid, ledger.TerminalEntitlements[0].SettledAt);
    }
    [Fact]
    public void StockDeliveryRemovesTheOldPositionAndDeliversExactSuccessorShares()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action, ended: true), "listing-a", action.ActionId, TerminalProof(action));
        Assert.Empty(ledger.Holdings); Assert.Equal(140, ledger.CashGbp);
        Assert.Equal(3, ledger.TerminalEntitlements[0].SuccessorQuantity);
        var delivered = TerminalAt.AddDays(1); var market = TerminalMarket(delivered, action, ended: true);
        ledger.SettleTerminal("deliver", market, "listing-a", action.ActionId, StockSettlement(delivered), Age);
        Assert.Equal(139.5m, ledger.CashGbp); Assert.Equal(3, Assert.Single(ledger.Holdings).Value.Quantity);
        Assert.Equal(delivered, ledger.Holdings["successor"].OpenedAt);
        Assert.Equal(319.5m, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Null(ledger.Entries[^1].GbpPerLocalUnit); Assert.Null(ledger.Entries[^1].FxId);
        Assert.Equal(3, ledger.Entries[^1].TerminalSettlement!.SuccessorQuantity);
    }

    [Fact]
    public void StockDeliveryAddsToAnExistingSuccessorWithoutSellingOrDoubleCounting()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        ledger.Fill("buy-successor", TerminalMarket(BuyAt, action), "successor", 1, 50, PriceBasis.Raw, 0, Age);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var paid = TerminalAt.AddDays(1); var market = TerminalMarket(paid, action);
        ledger.SettleTerminal("deliver", market, "listing-a", action.ActionId, StockSettlement(paid), Age);
        Assert.Equal(4, ledger.Holdings["successor"].Quantity); Assert.False(ledger.Holdings.ContainsKey("listing-a"));
        Assert.Equal(BuyAt, ledger.Holdings["successor"].OpenedAt);
        Assert.Equal(102.5m, ledger.CashGbp + .5m); // one share at 50 USD using the fixture's 0.75 GBP/USD
        Assert.Equal(342, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Theory]
    [InlineData(TerminalOutcome.Zero)]
    [InlineData(TerminalOutcome.Cash)]
    public void TerminalCaptureDoesNotRequireAnActiveOriginalListingAfterItsExit(TerminalOutcome outcome)
    {
        var ledger = DividendBook(); var action = Terminal(outcome);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action, ended: true), "listing-a", action.ActionId, TerminalProof(action));
        Assert.Empty(ledger.Holdings);
    }

    [Theory]
    [InlineData("cutoff")]
    [InlineData("future")]
    [InlineData("unknown-time")]
    [InlineData("source")]
    [InlineData("hash")]
    public void TerminalEntitlementRequiresAvailableSourceBoundCutoffEvidence(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(); var evidence = TerminalProof(action);
        evidence = kind switch
        {
            "cutoff" => evidence with { EntitlementAt = TerminalAt.AddSeconds(-1) },
            "future" => evidence with { AvailableAt = TerminalAt.AddSeconds(1) },
            "unknown-time" => evidence with { AvailableAt = default },
            "source" => evidence with { SourceReference = "" },
            _ => evidence with { ContentSha256 = "" }
        };
        var market = Market(TerminalAt, actions: [action]);
        Assert.Equal("raw-ledger-terminal-evidence", Assert.Throws<ResearchException>(() =>
            ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, evidence)).Code);
        Assert.Equal(2, ledger.Holdings["listing-a"].Quantity); Assert.Empty(ledger.TerminalEntitlements); Assert.Equal(2, ledger.Entries.Length);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("gross")]
    [InlineData("negative-withheld")]
    [InlineData("excess-withheld")]
    [InlineData("fee")]
    [InlineData("future-settled")]
    [InlineData("future-available")]
    [InlineData("before-entitlement")]
    [InlineData("before-settled")]
    [InlineData("source")]
    [InlineData("hash")]
    [InlineData("mixed-stock")]
    public void InvalidTerminalSettlementsLeaveTheClaimCashAndEventIdentityUntouched(string kind)
    {
        var ledger = DividendBook(); var action = Terminal();
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var paid = TerminalAt.AddDays(1); var settlement = CashSettlement(paid);
        settlement = kind switch
        {
            "currency" => settlement with { Currency = "GBP" },
            "gross" => settlement with { GrossLocal = 221 },
            "negative-withheld" => settlement with { WithheldLocal = -1 },
            "excess-withheld" => settlement with { WithheldLocal = 221 },
            "fee" => settlement with { FeeGbp = -1 },
            "future-settled" => settlement with { SettledAt = paid.AddSeconds(1), AvailableAt = paid.AddSeconds(1) },
            "future-available" => settlement with { AvailableAt = paid.AddSeconds(1) },
            "before-entitlement" => settlement with { SettledAt = BuyAt },
            "before-settled" => settlement with { AvailableAt = paid.AddSeconds(-1) },
            "source" => settlement with { SourceReference = "" },
            "hash" => settlement with { ContentSha256 = "" },
            _ => settlement with { SuccessorListingId = "successor", SuccessorQuantity = 1 }
        };
        var market = Market(paid, fx: .75m, actions: [action]);
        Assert.Equal("raw-ledger-terminal-settlement", Assert.Throws<ResearchException>(() =>
            ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, settlement, Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(3, ledger.Entries.Length); Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, CashSettlement(paid), Age);
        Assert.Equal(289.5m, ledger.CashGbp);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("ratio")]
    [InlineData("symbol")]
    [InlineData("missing-outcome")]
    public void ComplexOrUnpricedTerminalTermsAreRefusedWithoutRemovingTheHolding(string kind)
    {
        var ledger = DividendBook(); var action = Terminal();
        action = kind switch
        {
            "mixed" => action with { SuccessorListingId = "successor", SplitRatio = 1 },
            "ratio" => action with { SplitRatio = 2 },
            "symbol" => action with { NewSymbol = "NEW" },
            _ => action with { TerminalOutcome = TerminalOutcome.Missing }
        };
        var expected = kind == "missing-outcome" ? "raw-ledger-terminal" : "raw-ledger-action-complex";
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action),
            "listing-a", action.ActionId, TerminalProof(action))).Code);
        Assert.Equal(2, ledger.Holdings["listing-a"].Quantity); Assert.Empty(ledger.TerminalEntitlements);
    }

    [Fact]
    public void LaterCapturesCannotReopenAClosedListingOrDuplicateItsSettlement()
    {
        var ledger = DividendBook(); var action = Terminal(); var at = TerminalAt.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var market = Market(at, fx: .75m, actions: [action]);
        Assert.Equal("raw-ledger-terminal-closed", Assert.Throws<ResearchException>(() => ledger.Fill("rebuy", market,
            "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-action-already-booked", Assert.Throws<ResearchException>(() => ledger.CaptureTerminal("exit-again", market,
            "listing-a", action.ActionId, TerminalProof(action))).Code);
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, CashSettlement(at), Age);
        Assert.Equal("raw-ledger-terminal-settled", Assert.Throws<ResearchException>(() => ledger.SettleTerminal("again", market,
            "listing-a", action.ActionId, CashSettlement(at), Age)).Code);
        Assert.Equal(289.5m, ledger.CashGbp); Assert.Equal(4, ledger.Entries.Length);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    public void CapturedTerminalEvidenceCannotBeChangedOrOmittedAfterClosingTheShares(string kind)
    {
        var ledger = DividendBook(); var action = Terminal();
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var later = TerminalAt.AddDays(1);
        var revision = action with { Id = "terminal-revision", CashPerShare = 120,
            Evidence = Known(later.AddMinutes(-1)) with { RevisionOf = action.Id } };
        var market = kind == "missing" ? Market(later) : Market(later, actions: [action, revision]);
        var expected = kind == "missing" ? "raw-ledger-action-evidence" : "raw-ledger-action-revised";
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => ledger.SettleTerminal("settle", market,
            "listing-a", action.ActionId, CashSettlement(later), Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(220, ledger.TerminalEntitlements[0].GrossLocal);
    }

    [Theory]
    [InlineData("future-observed")]
    [InlineData("future-available")]
    [InlineData("stale")]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("missing")]
    public void TerminalCashNeedsItsExactFreshExecutableSettlementRate(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(); var paid = TerminalAt.AddDays(1); var booked = paid.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var rate = new FxRate("fx", Known(paid), "USD", "GBP", .75m, paid, FxPurpose.Executable);
        rate = kind switch
        {
            "future-observed" => rate with { ObservedAt = paid.AddMinutes(1), Evidence = Known(paid.AddMinutes(1)) },
            "future-available" => rate with { Evidence = Known(paid.AddMinutes(1)) },
            "stale" => rate with { ObservedAt = paid.AddDays(-3), Evidence = Known(paid.AddDays(-3)) },
            "reference" => rate with { Purpose = FxPurpose.Reference },
            "reverse" => rate with { BaseCurrency = "GBP", QuoteCurrency = "USD" },
            _ => rate
        };
        var market = Market(booked, actions: [action], fxRates: kind == "missing" ? [] : [rate]);
        Assert.Equal("raw-ledger-terminal-fx", Assert.Throws<ResearchException>(() => ledger.SettleTerminal("settle", market,
            "listing-a", action.ActionId, CashSettlement(paid), Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
    }

    [Fact]
    public void ANewerBookingRateCannotReplaceThePaymentRateAndOldReceiptsRemainUnchanged()
    {
        var ledger = DividendBook(); var action = Terminal(); var paid = TerminalAt.AddDays(1); var booked = paid.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var before = ledger.TerminalEntitlements;
        var actual = new FxRate("paid-fx", Known(paid), "USD", "GBP", .75m, paid, FxPurpose.Executable);
        var later = new FxRate("booking-fx", Known(booked), "USD", "GBP", 9, booked, FxPurpose.Executable);
        var market = Market(booked, actions: [action], fxRates: [actual, later]);
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, CashSettlement(paid) with { FxObservationId = actual.Id }, Age);
        Assert.Equal(289.5m, ledger.CashGbp); Assert.Equal(actual.Id, ledger.Entries[^1].FxId);
        Assert.Equal(booked, ledger.Entries[^1].At); Assert.Equal(paid, ledger.Entries[^1].TerminalSettlement!.SettledAt);
        Assert.Null(before[0].SettledAt); Assert.Equal(220, before[0].GrossLocal);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("successor")]
    [InlineData("cash")]
    [InlineData("fx")]
    public void AStockSettlementCannotInventFractionalCashOrDifferentDeliveryTerms(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock); var paid = TerminalAt.AddDays(1);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var settlement = StockSettlement(paid);
        settlement = kind switch
        {
            "quantity" => settlement with { SuccessorQuantity = 2.999m },
            "successor" => settlement with { SuccessorListingId = "listing-a" },
            "cash" => settlement with { Currency = "USD", GrossLocal = 1 },
            _ => settlement with { FxObservationId = "fx" }
        };
        Assert.Equal("raw-ledger-terminal-settlement", Assert.Throws<ResearchException>(() => ledger.SettleTerminal("deliver",
            TerminalMarket(paid, action), "listing-a", action.ActionId, settlement, Age)).Code);
        Assert.Empty(ledger.Holdings); Assert.Equal(140, ledger.CashGbp);
    }

    [Fact]
    public void SuccessorQuoteUnitsAndCurrencyDoNotChangeTheDeliveredShareCount()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock); var paid = TerminalAt.AddDays(1);
        var units = new Denomination("GBP", QuoteUnit.Pence);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action, successorUnits: units, successorPrice: 8000),
            "listing-a", action.ActionId, TerminalProof(action));
        var market = TerminalMarket(paid, action, successorUnits: units, successorPrice: 8000);
        ledger.SettleTerminal("deliver", market, "listing-a", action.ActionId, StockSettlement(paid), Age);
        Assert.Equal(3, ledger.Holdings["successor"].Quantity);
        Assert.Equal(379.5m, ledger.Value(market, Age, Age, FxPurpose.Reference).EquityGbp);
        Assert.Null(ledger.Entries[^1].GbpPerLocalUnit);
    }

    [Fact]
    public void AKnownTerminalEventCannotBeBoughtThroughAnOutdatedActiveListing()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 300);
        var action = Terminal();
        Assert.Equal("raw-ledger-terminal-closed", Assert.Throws<ResearchException>(() => ledger.Fill("buy", Market(TerminalAt, actions: [action]),
            "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal(300, ledger.CashGbp); Assert.Empty(ledger.Holdings);
    }

    [Fact]
    public void TerminalCashAmountsRemainMajorUnitsWhenOriginalQuotesWerePence()
    {
        var units = new Denomination("GBP", QuoteUnit.Pence);
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 25);
        ledger.Fill("buy", Market(BuyAt, units, price: 100), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        var action = Terminal() with { CashCurrency = "GBP", CashPerShare = 1.1m };
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action, originalUnits: units), "listing-a", action.ActionId, TerminalProof(action));
        var paid = TerminalAt.AddDays(1); var market = TerminalMarket(paid, action, originalUnits: units);
        var settlement = CashSettlement(paid) with { Currency = "GBP", GrossLocal = 2.2m, WithheldLocal = .2m, FeeGbp = .1m, FxObservationId = null };
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, settlement, Age);
        Assert.Equal(24.9m, ledger.CashGbp); Assert.Equal(2.2m, ledger.TerminalEntitlements[0].GrossLocal);
        Assert.Equal(1, ledger.Entries[^1].GbpPerLocalUnit); Assert.Null(ledger.Entries[^1].FxId);
    }

    [Fact]
    public void CashConsiderationMayBeExplicitlyInADifferentCurrencyFromTheOldShares()
    {
        var ledger = DividendBook(); var action = Terminal() with { CashCurrency = "GBP" };
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var paid = TerminalAt.AddDays(1);
        ledger.SettleTerminal("settle", Market(paid, actions: [action], fxRates: []), "listing-a", action.ActionId,
            CashSettlement(paid) with { Currency = "GBP", FxObservationId = null }, Age);
        Assert.Equal(339.5m, ledger.CashGbp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APhysicalPaymentIdentifierCannotBeCreditedAsBothDividendAndTerminalCash(bool terminalFirst)
    {
        var ledger = DividendBook(); var dividend = Dividend(); var action = Terminal();
        ledger.CaptureDividend("dividend", Market(BuyAt.AddHours(1), actions: [dividend]), "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [dividend, action]), "listing-a", action.ActionId, TerminalProof(action));
        var paid = TerminalAt.AddDays(1); var market = Market(paid, fx: .75m, actions: [dividend, action]);
        var cash = CashSettlement(paid) with { SourceSettlementId = "shared-physical-payment" };
        var income = Payment(paid) with { SourcePaymentId = cash.SourceSettlementId };
        if (terminalFirst)
        {
            ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, cash, Age);
            Assert.Equal("raw-ledger-dividend-payment-id", Assert.Throws<ResearchException>(() =>
                ledger.PayDividend("pay", market, "listing-a", dividend.ActionId, income, Age)).Code);
            Assert.Equal(289.5m, ledger.CashGbp); Assert.Null(ledger.DividendEntitlements[0].PaidAt);
        }
        else
        {
            ledger.PayDividend("pay", market, "listing-a", dividend.ActionId, income, Age);
            Assert.Equal("raw-ledger-terminal-settlement-id", Assert.Throws<ResearchException>(() =>
                ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, cash, Age)).Code);
            Assert.Equal(142.05m, ledger.CashGbp); Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
        }
    }

    [Theory]
    [InlineData("split")]
    [InlineData("trade")]
    public void SuccessorHistoryCannotBeReinterpretedByALateDelivery(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock); var delivered = TerminalAt.AddDays(1); var booked = delivered.AddDays(1);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var split = new CorporateAction("successor-split", Known(delivered), "successor", "successor-split", CorporateActionKind.Split,
            delivered, 2, null, null, null, null, TerminalOutcome.NotApplicable);
        if (kind == "trade") ledger.Fill("buy-successor", TerminalMarket(delivered, action), "successor", 1, 80, PriceBasis.Raw, 0, Age);
        var market = TerminalMarket(booked, action, extra: kind == "split" ? [split] : []);
        Assert.Equal("raw-ledger-terminal-successor-history", Assert.Throws<ResearchException>(() =>
            ledger.SettleTerminal("deliver", market, "listing-a", action.ActionId, StockSettlement(delivered), Age)).Code);
        Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
    }

    [Theory]
    [InlineData(TerminalOutcome.Cash)]
    [InlineData(TerminalOutcome.Stock)]
    public void PendingTerminalClaimsCannotFundPurchasesOrDisappearThroughTheDividendValuationWrapper(TerminalOutcome outcome)
    {
        var ledger = DividendBook(); var action = Terminal(outcome); var market = TerminalMarket(TerminalAt, action);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() =>
            ledger.Fill("buy-successor", market, "successor", 2, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [])).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void FullyWithheldCashNeedsNoFxButItsActualFeeStillReducesCash()
    {
        var ledger = DividendBook(); var action = Terminal(); var paid = TerminalAt.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var settlement = CashSettlement(paid) with { WithheldLocal = 220, FxObservationId = null };
        ledger.SettleTerminal("settle", Market(paid, actions: [action], fxRates: []), "listing-a", action.ActionId, settlement, Age);
        Assert.Equal(139.5m, ledger.CashGbp); Assert.Null(ledger.Entries[^1].GbpPerLocalUnit); Assert.Null(ledger.Entries[^1].FxId);
    }

    [Theory]
    [InlineData(TerminalOutcome.Cash)]
    [InlineData(TerminalOutcome.Stock)]
    public void FeeOverdraftDoesNotConsumeTheClaimOrPartiallyDeliverShares(TerminalOutcome outcome)
    {
        var ledger = DividendBook(); var action = Terminal(outcome); var paid = TerminalAt.AddDays(1);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var valid = outcome == TerminalOutcome.Cash ? CashSettlement(paid) : StockSettlement(paid);
        var invalid = valid with { FeeGbp = outcome == TerminalOutcome.Cash ? 291 : 141 };
        var market = TerminalMarket(paid, action);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.SettleTerminal("settle", market,
            "listing-a", action.ActionId, invalid, Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Null(ledger.TerminalEntitlements[0].SettledAt);
        ledger.SettleTerminal("settle", market, "listing-a", action.ActionId, valid, Age);
        Assert.NotNull(ledger.TerminalEntitlements[0].SettledAt);
    }

    [Fact]
    public void ANewIdenticalActionVintagePreservesTheOriginalEntitlementAndReceipt()
    {
        var ledger = DividendBook(); var action = Terminal(); var paid = TerminalAt.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        var revision = action with { Id = "terminal-vintage", Evidence = Known(paid.AddMinutes(-1)) with { RevisionOf = action.Id } };
        ledger.SettleTerminal("settle", Market(paid, fx: .75m, actions: [action, revision]), "listing-a", action.ActionId, CashSettlement(paid), Age);
        Assert.Equal(289.5m, ledger.CashGbp); Assert.Equal(action.Id, ledger.TerminalEntitlements[0].Action.Id);
        Assert.Equal(action.Id, ledger.Entries[^1].ActionObservationId);
    }

    [Fact]
    public void APostCutoffBuyerCannotClaimTheEarlierTerminalConsideration()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 300);
        ledger.Fill("buy", Market(TerminalAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Terminal();
        Assert.Equal("raw-ledger-action-position", Assert.Throws<ResearchException>(() => ledger.CaptureTerminal("exit",
            Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action))).Code);
        Assert.Single(ledger.Holdings); Assert.Empty(ledger.TerminalEntitlements);
    }

    [Fact]
    public void APostCutoffTradeCannotBeReinterpretedAsTheOldEntitlementQuantity()
    {
        var ledger = DividendBook(); var action = Terminal();
        ledger.Fill("sell", Market(TerminalAt), "listing-a", -1, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-action-history", Assert.Throws<ResearchException>(() => ledger.CaptureTerminal("exit",
            Market(TerminalAt.AddDays(1), actions: [action]), "listing-a", action.ActionId, TerminalProof(action))).Code);
        Assert.Equal(1, ledger.Holdings["listing-a"].Quantity); Assert.Empty(ledger.TerminalEntitlements);
    }

    [Fact]
    public void StockSettlementRequiresTheSuccessorToExistAtDeliveryNotJustAtBooking()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock); var delivered = TerminalAt.AddDays(1); var booked = delivered.AddDays(1);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action, successorStart: booked), "listing-a", action.ActionId, TerminalProof(action));
        Assert.Equal("raw-ledger-terminal-successor", Assert.Throws<ResearchException>(() => ledger.SettleTerminal("deliver",
            TerminalMarket(booked, action, successorStart: booked), "listing-a", action.ActionId, StockSettlement(delivered), Age)).Code);
        Assert.Empty(ledger.Holdings); Assert.Equal(140, ledger.CashGbp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewEconomicActionsCannotBeSilentlyIgnoredAfterTerminalRetirement(bool priorSplit)
    {
        var ledger = DividendBook(); var action = Terminal(); var paid = TerminalAt.AddDays(1); var later = paid.AddDays(1);
        ledger.CaptureTerminal("exit", Market(TerminalAt, actions: [action]), "listing-a", action.ActionId, TerminalProof(action));
        ledger.SettleTerminal("settle", Market(paid, fx: .75m, actions: [action]), "listing-a", action.ActionId, CashSettlement(paid), Age);
        var extra = priorSplit
            ? new CorporateAction("late-split", Known(later), "listing-a", "late-split", CorporateActionKind.Split,
                TerminalAt.AddHours(-1), 2, null, null, null, null, TerminalOutcome.NotApplicable)
            : new CorporateAction("later-distribution", Known(later), "listing-a", "later-distribution", CorporateActionKind.Dividend,
                paid, null, 1, "USD", null, null, TerminalOutcome.NotApplicable);
        Assert.Equal("raw-ledger-terminal-action", Assert.Throws<ResearchException>(() =>
            ledger.Value(Market(later, actions: [action, extra]), Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal(289.5m, ledger.CashGbp); Assert.Equal(2, ledger.TerminalEntitlements[0].Quantity);
    }

}
