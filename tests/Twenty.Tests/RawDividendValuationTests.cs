using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static RawDividendMarkAssumption DividendMark(CorporateAction action, decimal withholding = 1, decimal fee = .2m) =>
        new(action.ListingId, action.ActionId, action.CashCurrency!, withholding, fee, action.EffectiveAt,
            "urn:twenty:authored:dividend-mark", new string('c', 64));

    [Fact]
    public void ExplicitReceivableMarkIncludesIncomeButDoesNotMakeItSpendable()
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1);
        var market = Market(at, price: 98, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var entries = ledger.Entries;
        var mark = ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(action)]);
        Assert.Equal(296.8m, mark.HoldingsAndCash.EquityGbp); Assert.Equal(2.2m, mark.ExpectedDividendReceivablesGbp);
        Assert.Equal(299, mark.ExpectedEquityGbp); Assert.Equal(140, ledger.CashGbp); Assert.Equal(0, ledger.FeesGbp);
        Assert.Equal(3, Assert.Single(mark.DividendReceivables).ExpectedNetLocal);
        Assert.Equal(entries, ledger.Entries); Assert.Null(ledger.DividendEntitlements[0].PaidAt);
        Assert.Equal("raw-ledger-dividend-unpaid", Assert.Throws<ResearchException>(() => ledger.Value(market, Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.Fill("buy-more", market, "listing-a", 1.76m, 100, PriceBasis.Raw, 0, Age)).Code);
    }
    [Theory]
    [InlineData("default")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("currency")]
    [InlineData("negative-tax")]
    [InlineData("excess-tax")]
    [InlineData("negative-fee")]
    [InlineData("future")]
    [InlineData("unknown-time")]
    [InlineData("missing-hash")]
    [InlineData("missing-reference")]
    public void ReceivableMarksRequireExactlyOneKnownAssumptionForEveryPendingClaim(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1); var market = Market(at, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var original = DividendMark(action);
        var changed = kind switch
        {
            "unknown" => original with { ActionId = "absent" },
            "null" => null!,
            "currency" => original with { Currency = "GBP" },
            "negative-tax" => original with { ExpectedWithheldLocal = -1 },
            "excess-tax" => original with { ExpectedWithheldLocal = 5 },
            "negative-fee" => original with { ExpectedFeeGbp = -1 },
            "future" => original with { AvailableAt = at.AddMinutes(1) },
            "unknown-time" => original with { AvailableAt = default },
            "missing-hash" => original with { ContentSha256 = "" },
            "missing-reference" => original with { SourceReference = "" },
            _ => original
        };
        System.Collections.Immutable.ImmutableArray<RawDividendMarkAssumption> assumptions = kind switch
        {
            "default" => default,
            "missing" => [],
            "duplicate" => [original, original],
            _ => [changed]
        };
        Assert.Equal("raw-ledger-dividend-valuation", Assert.Throws<ResearchException>(() => ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, assumptions)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(3, ledger.Entries.Length);
    }

    [Fact]
    public void PaymentReplacesTheReceivableWithoutDoubleCountingIncome()
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1); var market = Market(at, price: 98, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var assumption = DividendMark(action);
        var before = ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [assumption]);
        var paid = BuyAt.AddDays(1); var paidMarket = Market(paid, price: 98, actions: [action]);
        ledger.PayDividend("pay", paidMarket, "listing-a", action.ActionId, Payment(paid), Age);
        var after = ledger.ValueWithDividendReceivables(paidMarket, Age, Age, FxPurpose.Executable, []);
        Assert.Equal(299, after.ExpectedEquityGbp); Assert.Equal(before.ExpectedEquityGbp, after.ExpectedEquityGbp);
        Assert.Equal(142.2m, after.HoldingsAndCash.CashGbp); Assert.Equal(0, after.ExpectedDividendReceivablesGbp); Assert.Empty(after.DividendReceivables);
        Assert.Null(before.DividendReceivables[0].Entitlement.PaidAt);
        Assert.Equal("raw-ledger-dividend-valuation", Assert.Throws<ResearchException>(() => ledger.ValueWithDividendReceivables(paidMarket, Age, Age, FxPurpose.Executable, [assumption])).Code);
    }

    [Fact]
    public void GrossNoDeductionScenarioConservesValueAcrossARawExDividendPriceDrop()
    {
        var ledger = DividendBook(); var action = Dividend(); var market = Market(BuyAt.AddHours(1), price: 98, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var mark = ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(action, 0, 0)]);
        Assert.Equal(3.2m, mark.ExpectedDividendReceivablesGbp); Assert.Equal(300, mark.ExpectedEquityGbp);
    }

    [Fact]
    public void ReferenceReceivableMarkDoesNotClaimFutureExecutableFx()
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1);
        ledger.CaptureDividend("claim", Market(at, actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var current = new FxRate("current-reference", Known(at.AddMinutes(-1)), "USD", "GBP", .75m, at.AddMinutes(-1), FxPurpose.Reference);
        var paid = BuyAt.AddDays(1); var future = new FxRate("future-execution", Known(paid), "USD", "GBP", .7m, paid, FxPurpose.Executable);
        var earlyMarket = Market(at, price: 98, actions: [action], fxRates: [current, future]);
        var before = ledger.ValueWithDividendReceivables(earlyMarket, Age, Age, FxPurpose.Reference, [DividendMark(action)]);
        Assert.Equal(289.05m, before.ExpectedEquityGbp); Assert.Equal(2.05m, before.ExpectedDividendReceivablesGbp);
        Assert.Equal("current-reference", before.DividendReceivables[0].FxId); Assert.Equal(FxPurpose.Reference, before.HoldingsAndCash.FxPurpose);
        var alternate = ledger.ValueWithDividendReceivables(Market(at, price: 98, actions: [action], fxRates: [current, future with { QuoteUnitsPerBase = 9 }]), Age, Age, FxPurpose.Reference, [DividendMark(action)]);
        Assert.Equal(before.ExpectedEquityGbp, alternate.ExpectedEquityGbp);
        var laterMarket = Market(paid, price: 98, actions: [action], fxRates: [current, future]);
        ledger.PayDividend("pay", laterMarket, "listing-a", action.ActionId, Payment(paid) with { FxObservationId = future.Id }, Age);
        var after = ledger.ValueWithDividendReceivables(laterMarket, Age, Age, FxPurpose.Reference, []);
        Assert.Equal(288.9m, after.ExpectedEquityGbp); Assert.Equal(141.9m, ledger.CashGbp);
    }

    [Fact]
    public void ReceivableSurvivesSaleAndLaterSplitWithoutChangingFrozenQuantity()
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1);
        var market = Market(at, actions: [action]); ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        ledger.Fill("sell", market, "listing-a", -1, 100, PriceBasis.Raw, 0, Age);
        var split = Split(effectiveHours: 2); var next = Market(BuyAt.AddHours(2), price: 50, actions: [action, split]);
        ledger.ApplySplit("split", next, "listing-a", split.ActionId);
        var mark = ledger.ValueWithDividendReceivables(next, Age, Age, FxPurpose.Executable, [DividendMark(action)]);
        Assert.Equal(2, mark.DividendReceivables[0].Entitlement.Quantity); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(302.2m, mark.ExpectedEquityGbp);
    }

    [Fact]
    public void FullyWithheldReceivableNeedsNoFxWhenAllSharesHaveBeenSold()
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1); var market = Market(at, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        ledger.Fill("sell", market, "listing-a", -2, 100, PriceBasis.Raw, 0, Age);
        var mark = ledger.ValueWithDividendReceivables(Market(at, actions: [action], fxRates: []), Age, Age, FxPurpose.Reference, [DividendMark(action, 4, .2m)]);
        Assert.Equal(-.2m, mark.ExpectedDividendReceivablesGbp); Assert.Equal(299.8m, mark.ExpectedEquityGbp);
        Assert.Null(mark.DividendReceivables[0].GbpPerLocalUnit); Assert.Equal(300, ledger.CashGbp); Assert.Equal(0, ledger.FeesGbp);
    }

    [Fact]
    public void PencePricesDoNotScaleGbpDividendReceivables()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 300); var units = new Denomination("GBP", QuoteUnit.Pence);
        ledger.Fill("buy", Market(BuyAt, units), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        var action = Dividend() with { CashCurrency = "GBP" }; var market = Market(BuyAt.AddHours(1), units, actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        var mark = ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Reference, [DividendMark(action)]);
        Assert.Equal(2.8m, mark.ExpectedDividendReceivablesGbp); Assert.Equal(302.8m, mark.ExpectedEquityGbp);
        Assert.Equal(298, ledger.CashGbp); Assert.Null(mark.DividendReceivables[0].FxId);
    }

    [Fact]
    public void EveryPendingDividendIsIncludedInStableIdentityOrder()
    {
        var ledger = DividendBook(); var first = Dividend(); var second = Dividend("second", 2);
        var market = Market(BuyAt.AddHours(2), actions: [first, second]);
        ledger.CaptureDividend("first", market, "listing-a", first.ActionId, EntitlementEvidence(first));
        ledger.CaptureDividend("second", market, "listing-a", second.ActionId, EntitlementEvidence(second));
        Assert.Equal("raw-ledger-dividend-valuation", Assert.Throws<ResearchException>(() => ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(first)])).Code);
        var mark = ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(second, 0, 0), DividendMark(first)]);
        Assert.Equal(new[] { first.ActionId, second.ActionId }, mark.DividendReceivables.Select(r => r.Entitlement.ActionId));
        Assert.Equal(5.4m, mark.ExpectedDividendReceivablesGbp); Assert.Equal(305.4m, mark.ExpectedEquityGbp);
    }

    [Theory]
    [InlineData("adjusted")]
    [InlineData("stale")]
    [InlineData("missing-action")]
    [InlineData("revised-action")]
    public void ReceivableValuationRetainsRawPriceAndActionEvidenceGuards(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1);
        ledger.CaptureDividend("claim", Market(at, actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var later = at.AddDays(kind == "stale" ? 4 : 1); var revision = action with { Id = "revised", CashPerShare = 1, Evidence = Known(later.AddMinutes(-1)) with { RevisionOf = action.Id } };
        var market = kind switch
        {
            "missing-action" => Market(later),
            "revised-action" => Market(later, actions: [action, revision]),
            _ => Market(later, adjustedOnly: kind == "adjusted", priceAgeDays: kind == "stale" ? 3 : 0, actions: [action])
        };
        var expected = kind == "missing-action" ? "raw-ledger-action-evidence" : kind == "revised-action" ? "raw-ledger-action-revised" : "raw-ledger-price";
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(action)])).Code);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing")]
    public void ForeignReceivableNeedsTheSelectedFreshAvailableFxPurpose(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1); var original = Market(at, actions: [action]);
        ledger.CaptureDividend("claim", original, "listing-a", action.ActionId, EntitlementEvidence(action));
        ledger.Fill("sell", original, "listing-a", -2, 100, PriceBasis.Raw, 0, Age);
        var market = kind == "missing" ? Market(at, actions: [action], fxRates: []) : Market(at,
            purpose: kind == "reference" ? FxPurpose.Reference : FxPurpose.Executable, reverseFx: kind == "reverse",
            fxAgeDays: kind == "stale" ? 3 : 0, futureFx: kind == "future", actions: [action]);
        Assert.Throws<InvalidDatasetException>(() => ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(action)]));
        Assert.Equal(300, ledger.CashGbp); Assert.Equal(4, ledger.Entries.Length);
    }

}
