using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static RawTerminalMarkAssumption TerminalMark(CorporateAction action, decimal withholding = 20, decimal fee = .5m) =>
        new(action.ListingId, action.ActionId, action.CashCurrency, withholding, fee, action.EffectiveAt,
            "urn:twenty:authored:terminal-mark", new string('d', 64));

    [Theory]
    [InlineData(TerminalOutcome.Cash, 289.5)]
    [InlineData(TerminalOutcome.Stock, 319.5)]
    public void PendingTerminalValueIsExplicitReadOnlyAndReplacedBySettlement(TerminalOutcome outcome, decimal expected)
    {
        var ledger = DividendBook(); var action = Terminal(outcome);
        var market = TerminalMarket(TerminalAt, action, ended: true);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var entries = ledger.Entries;
        var assumption = TerminalMark(action, outcome == TerminalOutcome.Cash ? 20 : 0);
        var before = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [assumption]);
        Assert.Equal(expected, before.ExpectedEquityGbp); Assert.Equal(140, before.HoldingsAndCash.CashGbp);
        Assert.Equal(expected - 140, before.ExpectedTerminalReceivablesGbp);
        Assert.Equal(140, before.HoldingsAndCash.EquityGbp); Assert.Empty(ledger.Holdings);
        Assert.Equal(entries, ledger.Entries); Assert.Equal(0, ledger.FeesGbp);
        Assert.Null(Assert.Single(before.TerminalReceivables).Entitlement.SettledAt);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.Fill("spend-claim", market,
            "successor", 3, 80, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() => ledger.Value(market, Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() => ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [])).Code);
        var later = TerminalAt.AddDays(1); var paidMarket = TerminalMarket(later, action, ended: true);
        ledger.SettleTerminal("settle", paidMarket, "listing-a", action.ActionId,
            outcome == TerminalOutcome.Cash ? CashSettlement(later) : StockSettlement(later), Age);
        var after = ledger.ValueWithReceivables(paidMarket, Age, Age, FxPurpose.Executable, [], []);
        Assert.Equal(before.ExpectedEquityGbp, after.ExpectedEquityGbp); Assert.Empty(after.TerminalReceivables);
        Assert.Null(before.TerminalReceivables[0].Entitlement.SettledAt);
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(paidMarket, Age, Age, FxPurpose.Executable, [], [assumption])).Code);
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
    [InlineData("hash")]
    [InlineData("source")]
    public void EveryPendingTerminalRequiresOneAvailableMatchingAssumption(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(); var market = TerminalMarket(TerminalAt, action);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var original = TerminalMark(action);
        var changed = kind switch
        {
            "unknown" => original with { ActionId = "absent" },
            "null" => null!,
            "currency" => original with { Currency = "GBP" },
            "negative-tax" => original with { ExpectedWithheldLocal = -1 },
            "excess-tax" => original with { ExpectedWithheldLocal = 221 },
            "negative-fee" => original with { ExpectedFeeGbp = -1 },
            "future" => original with { AvailableAt = TerminalAt.AddTicks(1) },
            "unknown-time" => original with { AvailableAt = default },
            "hash" => original with { ContentSha256 = "wrong" },
            "source" => original with { SourceReference = "" },
            _ => original
        };
        ImmutableArray<RawTerminalMarkAssumption> assumptions = kind switch
        {
            "default" => default,
            "missing" => [],
            "duplicate" => [original, original],
            _ => [changed]
        };
        var entries = ledger.Entries;
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], assumptions)).Code);
        Assert.Equal(entries, ledger.Entries); Assert.Equal(140, ledger.CashGbp);
    }

    [Fact]
    public void CompleteMarkIncludesUnpaidDividendAndPendingTerminalWithoutDroppingEither()
    {
        var ledger = DividendBook(); var dividend = Dividend(); var terminal = Terminal();
        ledger.CaptureDividend("dividend", Market(dividend.EffectiveAt, actions: [dividend]), "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        var market = TerminalMarket(TerminalAt, terminal, ended: true, extra: [dividend]);
        ledger.CaptureTerminal("terminal", market, "listing-a", terminal.ActionId, TerminalProof(terminal));
        Assert.Equal("raw-ledger-dividend-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [TerminalMark(terminal)])).Code);
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(dividend)], [])).Code);
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(dividend)], [TerminalMark(terminal)]);
        Assert.Equal(2.05m, mark.ExpectedDividendReceivablesGbp); Assert.Equal(149.5m, mark.ExpectedTerminalReceivablesGbp);
        Assert.Equal(291.55m, mark.ExpectedEquityGbp); Assert.Equal(140, mark.HoldingsAndCash.CashGbp);
        var later = TerminalAt.AddDays(1); var paidMarket = TerminalMarket(later, terminal, ended: true, extra: [dividend]);
        ledger.SettleTerminal("cash", paidMarket, "listing-a", terminal.ActionId, CashSettlement(later), Age);
        var half = ledger.ValueWithReceivables(paidMarket, Age, Age, FxPurpose.Executable, [DividendMark(dividend)], []);
        Assert.Equal(mark.ExpectedEquityGbp, half.ExpectedEquityGbp); Assert.Single(half.DividendReceivables); Assert.Empty(half.TerminalReceivables);
        ledger.PayDividend("dividend-payment", paidMarket, "listing-a", dividend.ActionId, Payment(later), Age);
        var final = ledger.ValueWithReceivables(paidMarket, Age, Age, FxPurpose.Executable, [], []);
        Assert.Equal(mark.ExpectedEquityGbp, final.ExpectedEquityGbp); Assert.Equal(291.55m, ledger.CashGbp);
    }

    [Theory]
    [InlineData(false, 342)]
    [InlineData(true, 409.5)]
    public void PendingStockAndExistingSuccessorHaveSeparateLotsAndCorrectQuoteUnits(bool pence, decimal expected)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        var units = pence ? new Denomination("GBP", QuoteUnit.Pence) : new("USD", QuoteUnit.Major);
        ledger.Fill("successor-buy", TerminalMarket(BuyAt, action, successorUnits: units), "successor", 1,
            pence ? 5000 : 50, PriceBasis.Raw, 0, Age);
        var market = TerminalMarket(TerminalAt, action, ended: true, successorUnits: units, successorPrice: pence ? 8000 : 80);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [TerminalMark(action, 0)]);
        Assert.Equal(expected, mark.ExpectedEquityGbp);
        Assert.Equal(1, Assert.Single(mark.HoldingsAndCash.Positions).Quantity);
        var claim = Assert.Single(mark.TerminalReceivables);
        Assert.Equal(3, claim.Successor!.Quantity); Assert.Equal(units, claim.Successor.Denomination);
        Assert.Equal(pence ? 240 : 180, claim.Successor.ValueGbp);
        Assert.Equal(pence ? null : "fx", claim.FxId);
    }

    [Fact]
    public void FullyWithheldTerminalIsAFeeLiabilityAndNeedsNoFx()
    {
        var ledger = DividendBook(); var action = Terminal(); var market = TerminalMarket(TerminalAt, action, rates: []);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Reference, [], [TerminalMark(action, 220, 150)]);
        Assert.Equal(-150, mark.ExpectedTerminalReceivablesGbp); Assert.Equal(-10, mark.ExpectedEquityGbp);
        Assert.Null(mark.TerminalReceivables[0].GbpPerLocalUnit); Assert.Equal(140, ledger.CashGbp);
    }

    [Fact]
    public void GbpCashClaimUsesMajorUnitsEvenForPenceQuotedOriginalShares()
    {
        var ledger = new RawGbpLedger(); var units = new Denomination("GBP", QuoteUnit.Pence);
        ledger.Deposit("cash", BuyAt, 25); ledger.Fill("buy", Market(BuyAt, units), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        var action = Terminal() with { CashPerShare = 1.1m, CashCurrency = "GBP" };
        var market = TerminalMarket(TerminalAt, action, ended: true, originalUnits: units, rates: []);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Reference, [], [TerminalMark(action, .2m, .1m)]);
        Assert.Equal(24.9m, mark.ExpectedEquityGbp); Assert.Null(mark.TerminalReceivables[0].FxId);
    }

    [Theory]
    [InlineData(TerminalOutcome.Cash)]
    [InlineData(TerminalOutcome.Stock)]
    public void PendingTerminalUsesOnlyAvailableCurrentFxOfTheChosenPurpose(TerminalOutcome outcome)
    {
        var ledger = DividendBook(); var action = Terminal(outcome);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var current = new FxRate("current-reference", Known(TerminalAt.AddMinutes(-1)), "USD", "GBP", .7m, TerminalAt.AddMinutes(-1), FxPurpose.Reference);
        var futureAt = TerminalAt.AddHours(1);
        var future = current with { Id = "future", Evidence = Known(futureAt), ObservedAt = futureAt, QuoteUnitsPerBase = 9 };
        var assumptions = ImmutableArray.Create(TerminalMark(action, outcome == TerminalOutcome.Cash ? 20 : 0));
        var before = ledger.ValueWithReceivables(TerminalMarket(TerminalAt, action, rates: [current, future]), Age, Age, FxPurpose.Reference, [], assumptions);
        var alternate = ledger.ValueWithReceivables(TerminalMarket(TerminalAt, action, rates: [current, future with { QuoteUnitsPerBase = 1 }]), Age, Age, FxPurpose.Reference, [], assumptions);
        Assert.Equal(before.ExpectedEquityGbp, alternate.ExpectedEquityGbp); Assert.Equal("current-reference", before.TerminalReceivables[0].FxId);
        Assert.Equal(outcome == TerminalOutcome.Cash ? 279.5m : 307.5m, before.ExpectedEquityGbp);
        Assert.Throws<InvalidDatasetException>(() => ledger.ValueWithReceivables(TerminalMarket(TerminalAt, action, rates: [current]), Age, Age, FxPurpose.Executable, [], assumptions));
    }

    [Theory]
    [InlineData("split")]
    [InlineData("dividend")]
    [InlineData("exit")]
    public void PendingStockCannotBypassSuccessorActions(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var at = TerminalAt.AddHours(1);
        var extra = kind switch
        {
            "split" => Split() with { ListingId = "successor", EffectiveAt = at },
            "dividend" => Dividend() with { ListingId = "successor", EffectiveAt = at },
            _ => Terminal(TerminalOutcome.Zero) with { Id = "successor-exit", ListingId = "successor", EffectiveAt = at }
        };
        var market = TerminalMarket(at, action, extra: [extra]);
        Assert.Equal("raw-ledger-terminal-successor-history", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [TerminalMark(action, 0)])).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revised")]
    public void CompleteClaimMarkRetainsOriginalActionBindings(string kind)
    {
        var ledger = DividendBook(); var action = Terminal();
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var at = TerminalAt.AddDays(1);
        var revision = action with { Id = "revision", CashPerShare = 111, Evidence = Known(at) with { RevisionOf = action.Id } };
        var market = kind == "missing" ? Market(at) : TerminalMarket(at, action, extra: [revision]);
        Assert.Equal(kind == "missing" ? "raw-ledger-action-evidence" : "raw-ledger-action-revised", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [TerminalMark(action)])).Code);
    }

    [Theory]
    [InlineData("adjusted")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("units")]
    public void PendingStockNeedsAFreshAvailableRawSuccessorPriceWithMatchingUnits(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        ledger.CaptureTerminal("exit", TerminalMarket(TerminalAt, action), "listing-a", action.ActionId, TerminalProof(action));
        var market = TerminalMarket(TerminalAt.AddDays(1), action);
        var rows = market.Observations.Where(o => o is not PriceBar { ListingId: "successor" }).ToList();
        var bar = market.Observations.OfType<PriceBar>().Single(p => p.ListingId == "successor");
        if (kind != "missing") rows.Add(kind switch
        {
            "adjusted" => bar with { Basis = PriceBasis.TotalReturnAdjusted },
            "stale" => bar with { SessionDate = bar.SessionDate.AddDays(-2), SessionCloseAt = bar.SessionCloseAt.AddDays(-2) },
            "future" => bar with { Evidence = Known(market.DecisionAt.AddHours(1)) },
            "units" => bar with { Denomination = new("GBP", QuoteUnit.Major) },
            _ => bar
        });
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var changed = new PointInTimeDataset(fixture with { Observations = [.. rows] }).AsOf(market.DecisionAt);
        if (kind == "units")
            Assert.Throws<InvalidDatasetException>(() => ledger.ValueWithReceivables(changed, Age, Age, FxPurpose.Executable, [], [TerminalMark(action, 0)]));
        else
            Assert.Equal("raw-ledger-price", Assert.Throws<ResearchException>(() =>
                ledger.ValueWithReceivables(changed, kind == "stale" ? TimeSpan.FromDays(1) : Age, Age, FxPurpose.Executable, [], [TerminalMark(action, 0)])).Code);
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("withholding")]
    public void StockMarksCannotInventCashOrWithholdSuccessorShares(string kind)
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock); var market = TerminalMarket(TerminalAt, action);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var mark = TerminalMark(action, 0);
        mark = kind == "currency" ? mark with { Currency = "USD" } : mark with { ExpectedWithheldLocal = 1 };
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [mark])).Code);
    }

    [Fact]
    public void ZeroTerminalHasNoPendingClaimAndNeedsNoAssumption()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Zero); var market = TerminalMarket(TerminalAt, action, rates: []);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Reference, [], []);
        Assert.Equal(140, mark.ExpectedEquityGbp); Assert.Empty(mark.TerminalReceivables);
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Reference, [], [TerminalMark(action, 0)])).Code);
    }

    [Fact]
    public void MultipleTerminalClaimsAreMarkedCompletelyInStableIdentityOrder()
    {
        var ledger = DividendBook(); var first = Terminal();
        var at = TerminalAt.AddHours(1);
        var second = first with { Id = "successor-exit", ListingId = "successor", ActionId = "second-terminal", EffectiveAt = at, Evidence = Known(at) };
        ledger.Fill("successor-buy", TerminalMarket(BuyAt, first), "successor", 1, 50, PriceBasis.Raw, 0, Age);
        ledger.CaptureTerminal("first", TerminalMarket(TerminalAt, first), "listing-a", first.ActionId, TerminalProof(first));
        var market = TerminalMarket(at, first, extra: [second]);
        ledger.CaptureTerminal("second", market, "successor", second.ActionId, TerminalProof(second));
        var assumptions = ImmutableArray.Create(TerminalMark(second, 10), TerminalMark(first));
        var mark = ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], assumptions);
        Assert.Equal(326.5m, mark.ExpectedEquityGbp); Assert.Equal(224, mark.ExpectedTerminalReceivablesGbp);
        Assert.Equal(new[] { "listing-a", "successor" }, mark.TerminalReceivables.Select(v => v.Entitlement.Action.ListingId));
        Assert.Equal("raw-ledger-terminal-valuation", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithReceivables(market, Age, Age, FxPurpose.Executable, [], [TerminalMark(first)])).Code);
    }

    [Fact]
    public void DividendOnlyWrapperKeepsTerminalRefusalAheadOfClaimFxSelection()
    {
        var ledger = DividendBook(); var dividend = Dividend(); var terminal = Terminal();
        ledger.CaptureDividend("dividend", Market(dividend.EffectiveAt, actions: [dividend]), "listing-a", dividend.ActionId, EntitlementEvidence(dividend));
        var market = TerminalMarket(TerminalAt, terminal, extra: [dividend], rates: []);
        ledger.CaptureTerminal("terminal", market, "listing-a", terminal.ActionId, TerminalProof(terminal));
        Assert.Equal("raw-ledger-terminal-unsettled", Assert.Throws<ResearchException>(() =>
            ledger.ValueWithDividendReceivables(market, Age, Age, FxPurpose.Executable, [DividendMark(dividend)])).Code);
    }

}
