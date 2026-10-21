using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static CorporateAction Dividend(string id = "dividend", int effectiveHours = 1) =>
        new(id + "-observation", Known(BuyAt.AddMinutes(1)), "listing-a", id, CorporateActionKind.Dividend,
            BuyAt.AddHours(effectiveHours), null, 2, "USD", null, null, TerminalOutcome.NotApplicable);
    private static RawDividendEvidence EntitlementEvidence(CorporateAction action) =>
        new(action.EffectiveAt, BuyAt.AddMinutes(1), "urn:twenty:authored:entitlement", new string('a', 64));
    private static RawDividendPayment Payment(DateTimeOffset at, string sourceId = "payment-1") =>
        new(sourceId, "urn:twenty:authored:payment", new string('b', 64), at, at, "USD", 4, 1, .2m, "fx");
    private static RawGbpLedger DividendBook()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 300);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        return ledger;
    }

    [Fact]
    public void DividendFreezesEntitlementWithoutCashAndPaysNetOnlyAtPayment()
    {
        var ledger = DividendBook(); var action = Dividend();
        var entitled = Market(BuyAt.AddHours(1), actions: [action]);
        ledger.CaptureDividend("entitlement", entitled, "listing-a", action.ActionId, EntitlementEvidence(action));
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(4, Assert.Single(ledger.DividendEntitlements).GrossLocal);
        Assert.Null(ledger.DividendEntitlements[0].PaidAt); Assert.Null(ledger.Entries[^1].GbpPerLocalUnit);
        Assert.Equal("raw-ledger-dividend-unpaid", Assert.Throws<ResearchException>(() => ledger.Value(entitled, Age, Age, FxPurpose.Executable)).Code);
        // Selling ex-entitlement shares cannot sell the already frozen payment claim.
        ledger.Fill("sell", entitled, "listing-a", -1, 100, PriceBasis.Raw, 0, Age);
        var paidAt = BuyAt.AddDays(1); var market = Market(paidAt, fx: .75m, actions: [action]);
        ledger.PayDividend("payment", market, "listing-a", action.ActionId, Payment(paidAt), Age);
        Assert.Equal(222.05m, ledger.CashGbp); Assert.Equal(1, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(300, ledger.ContributionsGbp); Assert.Equal(.2m, ledger.FeesGbp);
        Assert.Equal(2, ledger.DividendEntitlements[0].Quantity); Assert.Equal(paidAt, ledger.DividendEntitlements[0].PaidAt);
        Assert.Equal(297.05m, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(2.05m, ledger.Entries[^1].CashChangeGbp);
    }
    [Theory]
    [InlineData("future-cutoff")]
    [InlineData("mismatched-cutoff")]
    [InlineData("future-evidence")]
    [InlineData("missing-hash")]
    public void EntitlementNeedsExplicitAvailableCutoffEvidence(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var at = BuyAt.AddHours(1);
        var proof = EntitlementEvidence(action);
        proof = kind switch
        {
            "future-cutoff" => proof with { EntitlementAt = at.AddHours(1) },
            "mismatched-cutoff" => proof with { EntitlementAt = at.AddMinutes(-1) },
            "future-evidence" => proof with { AvailableAt = at.AddMinutes(1) },
            _ => proof with { ContentSha256 = "" }
        };
        Assert.Equal("raw-ledger-dividend-evidence", Assert.Throws<ResearchException>(() => ledger.CaptureDividend("claim", Market(at, actions: [action]), "listing-a", action.ActionId, proof)).Code);
        Assert.Empty(ledger.DividendEntitlements); Assert.Equal(2, ledger.Entries.Length); Assert.Equal(140, ledger.CashGbp);
    }

    [Theory]
    [InlineData("future-paid")]
    [InlineData("future-available")]
    [InlineData("scheduled")]
    [InlineData("pre-entitlement")]
    [InlineData("wrong-gross")]
    [InlineData("wrong-currency")]
    [InlineData("negative-withholding")]
    [InlineData("excess-withholding")]
    [InlineData("negative-fee")]
    [InlineData("missing-hash")]
    public void InvalidPaymentsDoNotCreditCashOrSpendThePendingClaim(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var paid = BuyAt.AddDays(1);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var payment = Payment(paid);
        payment = kind switch
        {
            "future-paid" => payment with { PaidAt = paid.AddMinutes(1), AvailableAt = paid.AddMinutes(1) },
            "future-available" => payment with { AvailableAt = paid.AddMinutes(1) },
            "scheduled" => payment with { AvailableAt = paid.AddMinutes(-1) },
            "pre-entitlement" => payment with { PaidAt = BuyAt },
            "wrong-gross" => payment with { GrossLocal = 5 },
            "wrong-currency" => payment with { Currency = "GBP" },
            "negative-withholding" => payment with { WithheldLocal = -1 },
            "excess-withholding" => payment with { WithheldLocal = 5 },
            "negative-fee" => payment with { FeeGbp = -1 },
            _ => payment with { ContentSha256 = "" }
        };
        var market = Market(paid, actions: [action]);
        Assert.Equal("raw-ledger-dividend-payment", Assert.Throws<ResearchException>(() => ledger.PayDividend("payment", market, "listing-a", action.ActionId, payment, Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Null(ledger.DividendEntitlements[0].PaidAt); Assert.Equal(3, ledger.Entries.Length);
        ledger.PayDividend("payment", market, "listing-a", action.ActionId, Payment(paid), Age);
        Assert.Equal(142.2m, ledger.CashGbp);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("after-payment")]
    [InlineData("stale")]
    [InlineData("missing")]
    public void PaymentBindsAnExplicitExecutableFxObservationAtPaymentTime(string kind)
    {
        var ledger = DividendBook(); var action = Dividend(); var paid = BuyAt.AddDays(1); var book = paid.AddDays(1);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var observed = kind == "after-payment" ? paid.AddMinutes(1) : kind == "stale" ? paid.AddDays(-3) : paid.AddMinutes(-1);
        var fx = new FxRate("payment-fx", Known(observed), kind == "reverse" ? "GBP" : "USD", kind == "reverse" ? "USD" : "GBP", .75m, observed, kind == "reference" ? FxPurpose.Reference : FxPurpose.Executable);
        var payment = Payment(paid) with { AvailableAt = book, FxObservationId = kind == "missing" ? "absent" : fx.Id };
        Assert.Equal("raw-ledger-dividend-fx", Assert.Throws<ResearchException>(() => ledger.PayDividend("payment", Market(book, actions: [action], fxRates: [fx]), "listing-a", action.ActionId, payment, Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Null(ledger.DividendEntitlements[0].PaidAt);
    }

    [Fact]
    public void LatePaymentUsesPinnedPaymentFxRatherThanLaterBookDateFx()
    {
        var ledger = DividendBook(); var action = Dividend(); var paid = BuyAt.AddDays(1); var book = paid.AddDays(5);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var oldFx = new FxRate("payment-fx", Known(paid.AddMinutes(-1)), "USD", "GBP", .75m, paid.AddMinutes(-1), FxPurpose.Executable);
        var newerFx = new FxRate("newer-fx", Known(book.AddMinutes(-1)), "USD", "GBP", .9m, book.AddMinutes(-1), FxPurpose.Executable);
        var payment = Payment(paid) with { AvailableAt = book, FxObservationId = oldFx.Id };
        var market = Market(book, actions: [action], fxRates: [oldFx, newerFx]);
        ledger.PayDividend("payment", market, "listing-a", action.ActionId, payment, Age);
        Assert.Equal(142.05m, ledger.CashGbp); Assert.Equal(.75m, ledger.Entries[^1].GbpPerLocalUnit);
        Assert.Equal(book, ledger.Entries[^1].At); Assert.Equal(paid, ledger.Entries[^1].DividendPayment!.PaidAt);
    }

    [Fact]
    public void EntitlementCannotBeCapturedTwiceAndPaymentCannotBeRepeated()
    {
        var ledger = DividendBook(); var action = Dividend(); var market = Market(BuyAt.AddHours(1), actions: [action]);
        ledger.CaptureDividend("claim", market, "listing-a", action.ActionId, EntitlementEvidence(action));
        Assert.Equal("raw-ledger-action-already-booked", Assert.Throws<ResearchException>(() => ledger.CaptureDividend("claim-again", market, "listing-a", action.ActionId, EntitlementEvidence(action))).Code);
        var paid = BuyAt.AddDays(1); var paidMarket = Market(paid, actions: [action]);
        ledger.PayDividend("payment", paidMarket, "listing-a", action.ActionId, Payment(paid), Age);
        Assert.Equal("raw-ledger-dividend-paid", Assert.Throws<ResearchException>(() => ledger.PayDividend("replacement", paidMarket, "listing-a", action.ActionId, Payment(paid, "replacement"), Age)).Code);
        Assert.Equal(142.2m, ledger.CashGbp); Assert.Equal(4, ledger.Entries.Length);
    }

    [Fact]
    public void PenceQuotedSharesStillReceiveMajorCurrencyDividendAmounts()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 300);
        var units = new Denomination("GBP", QuoteUnit.Pence); var action = Dividend() with { CashCurrency = "GBP" };
        ledger.Fill("buy", Market(BuyAt, units), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), units, actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        Assert.Equal(4, ledger.DividendEntitlements[0].GrossLocal); Assert.Equal(298, ledger.CashGbp);
        var paid = BuyAt.AddDays(1); var payment = Payment(paid) with { Currency = "GBP", FxObservationId = null };
        ledger.PayDividend("pay", Market(paid, units, actions: [action]), "listing-a", action.ActionId, payment, Age);
        Assert.Equal(300.8m, ledger.CashGbp); Assert.Equal(1, ledger.Entries[^1].GbpPerLocalUnit);
    }

    [Fact]
    public void FullyWithheldIncomeUsesNoInventedFxConversion()
    {
        var ledger = DividendBook(); var action = Dividend(); var paid = BuyAt.AddDays(1);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        ledger.PayDividend("pay", Market(paid, actions: [action], fxRates: []), "listing-a", action.ActionId,
            Payment(paid) with { WithheldLocal = 4, FeeGbp = 0, FxObservationId = null }, Age);
        Assert.Equal(140, ledger.CashGbp); Assert.Null(ledger.Entries[^1].GbpPerLocalUnit);
        Assert.Equal(4, ledger.Entries[^1].DividendPayment!.WithheldLocal);
        Assert.NotNull(ledger.DividendEntitlements[0].PaidAt);
    }

    [Fact]
    public void ASourcePaymentCannotBeReusedForAnotherEntitlement()
    {
        var ledger = DividendBook(); var first = Dividend(); var second = Dividend("second", 2);
        var market = Market(BuyAt.AddHours(2), actions: [first, second]);
        ledger.CaptureDividend("claim-first", market, "listing-a", first.ActionId, EntitlementEvidence(first));
        ledger.CaptureDividend("claim-second", market, "listing-a", second.ActionId, EntitlementEvidence(second));
        var paid = BuyAt.AddDays(1); var paidMarket = Market(paid, actions: [first, second]);
        ledger.PayDividend("pay-first", paidMarket, "listing-a", first.ActionId, Payment(paid), Age);
        Assert.Equal("raw-ledger-dividend-payment-id", Assert.Throws<ResearchException>(() => ledger.PayDividend("pay-second", paidMarket, "listing-a", second.ActionId, Payment(paid), Age)).Code);
        Assert.Null(ledger.DividendEntitlements.Single(e => e.ActionId == second.ActionId).PaidAt);
        ledger.PayDividend("pay-second", paidMarket, "listing-a", second.ActionId, Payment(paid, "payment-2"), Age);
        Assert.Equal(144.4m, ledger.CashGbp); Assert.Equal(.4m, ledger.FeesGbp);
    }

    [Fact]
    public void SubsequentSplitCannotMultiplyAFrozenDividendEntitlement()
    {
        var ledger = DividendBook(); var dividend = Dividend(); var split = Split(effectiveHours: 2);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [dividend]), "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        ledger.ApplySplit("split", Market(BuyAt.AddHours(2), actions: [dividend, split]), "listing-a", split.ActionId);
        Assert.Equal(4, ledger.Holdings["listing-a"].Quantity); Assert.Equal(2, ledger.DividendEntitlements[0].Quantity);
        var paid = BuyAt.AddDays(1); ledger.PayDividend("pay", Market(paid, actions: [dividend, split]), "listing-a", dividend.ActionId, Payment(paid), Age);
        Assert.Equal(142.2m, ledger.CashGbp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void LateEntitlementCannotReinterpretPostCutoffTrades(decimal quantity)
    {
        var ledger = DividendBook(); var action = Dividend();
        ledger.Fill("unknown-dividend-trade", Market(BuyAt.AddHours(2)), "listing-a", quantity, 100, PriceBasis.Raw, 0, Age);
        var cash = ledger.CashGbp;
        Assert.Equal("raw-ledger-action-history", Assert.Throws<ResearchException>(() => ledger.CaptureDividend("claim", Market(BuyAt.AddDays(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action))).Code);
        Assert.Equal(cash, ledger.CashGbp); Assert.Empty(ledger.DividendEntitlements);
    }

    [Fact]
    public void DividendRevisionAfterSaleBlocksPaymentRatherThanRewritingEntitlement()
    {
        var ledger = DividendBook(); var original = Dividend();
        var market = Market(BuyAt.AddHours(1), actions: [original]);
        ledger.CaptureDividend("claim", market, "listing-a", original.ActionId, EntitlementEvidence(original));
        ledger.Fill("sell", market, "listing-a", -2, 100, PriceBasis.Raw, 0, Age);
        var revised = original with { Id = "revised-dividend", CashPerShare = 1, Evidence = Known(BuyAt.AddHours(2)) with { RevisionOf = original.Id } };
        var paid = BuyAt.AddDays(1);
        Assert.Equal("raw-ledger-action-revised", Assert.Throws<ResearchException>(() => ledger.PayDividend("pay", Market(paid, actions: [original, revised]), "listing-a", original.ActionId, Payment(paid), Age)).Code);
        Assert.Equal(300, ledger.CashGbp); Assert.Equal(4, ledger.DividendEntitlements[0].GrossLocal); Assert.Empty(ledger.Holdings);
    }

    [Fact]
    public void DividendPaymentFeeCannotBorrowAndFailureDoesNotConsumeSourceId()
    {
        var ledger = DividendBook(); var action = Dividend(); var paid = BuyAt.AddDays(1);
        ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action));
        var market = Market(paid, actions: [action]);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.PayDividend("pay", market, "listing-a", action.ActionId, Payment(paid) with { FeeGbp = 200 }, Age)).Code);
        Assert.Equal(140, ledger.CashGbp); Assert.Equal(0, ledger.FeesGbp);
        ledger.PayDividend("pay", market, "listing-a", action.ActionId, Payment(paid), Age);
        Assert.Equal(142.2m, ledger.CashGbp);
    }

    [Fact]
    public void UnpaidIncomeCannotFundPurchasesButPaidIncomeCan()
    {
        var ledger = DividendBook(); var action = Dividend(); var entitled = Market(BuyAt.AddHours(1), actions: [action]);
        ledger.CaptureDividend("claim", entitled, "listing-a", action.ActionId, EntitlementEvidence(action));
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.Fill("buy-more", entitled, "listing-a", 1.76m, 100, PriceBasis.Raw, 0, Age)).Code);
        var paid = BuyAt.AddDays(1); var market = Market(paid, actions: [action]);
        ledger.PayDividend("pay", market, "listing-a", action.ActionId, Payment(paid), Age);
        ledger.Fill("buy-more", market, "listing-a", 1.76m, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal(1.4m, ledger.CashGbp); Assert.Equal(3.76m, ledger.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void SellingAllSharesRetainsTheFrozenDividendUntilPayment()
    {
        var ledger = DividendBook(); var action = Dividend(); var entitled = Market(BuyAt.AddHours(1), actions: [action]);
        ledger.CaptureDividend("claim", entitled, "listing-a", action.ActionId, EntitlementEvidence(action));
        ledger.Fill("sell", entitled, "listing-a", -2, 100, PriceBasis.Raw, 0, Age);
        Assert.Empty(ledger.Holdings); Assert.Equal(300, ledger.CashGbp);
        Assert.Equal("raw-ledger-dividend-unpaid", Assert.Throws<ResearchException>(() => ledger.Value(entitled, Age, Age, FxPurpose.Executable)).Code);
        var paid = BuyAt.AddDays(1); var market = Market(paid, actions: [action]);
        ledger.PayDividend("pay", market, "listing-a", action.ActionId, Payment(paid), Age);
        Assert.Equal(302.2m, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("successor")]
    [InlineData("symbol")]
    [InlineData("terminal")]
    public void DividendEntitlementCannotDiscardMixedActionTerms(string kind)
    {
        var ledger = DividendBook(); var action = Dividend();
        action = kind switch
        {
            "split" => action with { SplitRatio = 2 },
            "successor" => action with { SuccessorListingId = "listing-a" },
            "symbol" => action with { NewSymbol = "NEW" },
            _ => action with { TerminalOutcome = TerminalOutcome.Cash }
        };
        Assert.Equal("raw-ledger-action-complex", Assert.Throws<ResearchException>(() => ledger.CaptureDividend("claim", Market(BuyAt.AddHours(1), actions: [action]), "listing-a", action.ActionId, EntitlementEvidence(action))).Code);
        Assert.Empty(ledger.DividendEntitlements); Assert.Equal(2, ledger.Entries.Length);
    }

    [Fact]
    public void EarlierSplitMustBeBookedBeforeCapturingPostSplitEntitlement()
    {
        var ledger = DividendBook(); var split = Split(); var dividend = Dividend(effectiveHours: 2);
        var market = Market(BuyAt.AddHours(2), actions: [split, dividend]);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.CaptureDividend("claim", market, "listing-a", dividend.ActionId, EntitlementEvidence(dividend))).Code);
        ledger.ApplySplit("split", market, "listing-a", split.ActionId);
        ledger.CaptureDividend("claim", market, "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        Assert.Equal(4, ledger.DividendEntitlements[0].Quantity); Assert.Equal(8, ledger.DividendEntitlements[0].GrossLocal);
    }

}
