using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    [Fact]
    public void ExplicitReplayMarksBothClaimsThenSettlesThemWithoutSpendingTheEstimates()
    {
        var dividend = Dividend(); var terminal = Terminal(); var paid = TerminalAt.AddDays(1);
        var initial = Market(BuyAt); var ex = Market(dividend.EffectiveAt, actions: [dividend]);
        var exit = TerminalMarket(TerminalAt, terminal, ended: true, extra: [dividend]);
        var cash = TerminalMarket(paid, terminal, ended: true, extra: [dividend]);
        RawReplayItem[] events =
        [
            new("opening", 0, initial, new RawReplayDeposit(300)),
            new("purchase", 1, initial, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
            new("dividend", 2, ex, new RawReplayDividendCapture("listing-a", dividend.ActionId, EntitlementEvidence(dividend))),
            new("exit", 3, exit, new RawReplayTerminalCapture("listing-a", terminal.ActionId, TerminalProof(terminal))),
            new("pending", 4, exit, new RawReplayMark([DividendMark(dividend)], [TerminalMark(terminal)])),
            new("settlement", 5, cash, new RawReplayTerminalSettlement("listing-a", terminal.ActionId, CashSettlement(paid))),
            new("payment", 6, cash, new RawReplayDividendPayment("listing-a", dividend.ActionId, Payment(paid))),
            new("closing", 7, cash, new RawReplayMark([], []))
        ];
        var result = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
        Assert.Equal(RawEventReplay.Version, result.Version); Assert.Equal(300, result.ContributionsGbp);
        Assert.Equal(.7m, result.FeesGbp); Assert.Equal(291.55m, result.CashGbp);
        Assert.Equal(291.55m, result.Marks[0].Valuation.ExpectedEquityGbp);
        Assert.Equal(140, result.Marks[0].Valuation.HoldingsAndCash.CashGbp);
        Assert.Equal(291.55m, result.Marks[1].Valuation.ExpectedEquityGbp);
        Assert.Equal("pending", result.Marks[0].Id); Assert.Equal(4, result.Marks[0].Sequence);
        Assert.Equal(6, result.Entries.Length); Assert.Empty(result.Holdings);
        Assert.Single(result.TerminalEntitlements); Assert.Single(result.DividendEntitlements);
        var replay = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(result), System.Text.Json.JsonSerializer.Serialize(replay));
    }
    private static RawReplayItem[] CashReplay() =>
    [
        new("cash", 0, Market(BuyAt), new RawReplayDeposit(300)),
        new("mark", 1, Market(BuyAt), new RawReplayMark([], []))
    ];

    [Theory]
    [InlineData("empty")]
    [InlineData("null-item")]
    [InlineData("null-market")]
    [InlineData("null-operation")]
    [InlineData("sequence-gap")]
    [InlineData("sequence-duplicate")]
    [InlineData("duplicate-id")]
    [InlineData("empty-id")]
    [InlineData("long-id")]
    [InlineData("backwards")]
    [InlineData("no-final-mark")]
    [InlineData("negative-price-age")]
    [InlineData("negative-fx-age")]
    [InlineData("purpose")]
    [InlineData("too-many")]
    public void ReplayRefusesAmbiguousOrUnboundedInputs(string kind)
    {
        var events = CashReplay();
        events = kind switch
        {
            "empty" => [],
            "null-item" => [null!, events[1]],
            "null-market" => [events[0] with { Market = null! }, events[1]],
            "null-operation" => [events[0] with { Operation = null! }, events[1]],
            "sequence-gap" => [events[0], events[1] with { Sequence = 2 }],
            "sequence-duplicate" => [events[0], events[1] with { Sequence = 0 }],
            "duplicate-id" => [events[0], events[1] with { Id = events[0].Id }],
            "empty-id" => [events[0] with { Id = " " }, events[1]],
            "long-id" => [events[0] with { Id = new string('x', 129) }, events[1]],
            "backwards" => [events[0] with { Market = Market(BuyAt.AddHours(1)) }, events[1]],
            "no-final-mark" => [events[0]],
            "too-many" => Enumerable.Repeat(events[0], 10001).ToArray(),
            _ => events
        };
        var expected = kind == "backwards" ? "raw-replay-time" : kind == "no-final-mark" ? "raw-replay-final-mark" : "raw-replay-input";
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => RawEventReplay.Run(events,
            kind == "negative-price-age" ? -Age : Age, kind == "negative-fx-age" ? -Age : Age,
            kind == "purpose" ? (FxPurpose)999 : FxPurpose.Executable)).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitSameTimeOrderDeterminesWhetherSettledCashCanBeSpent(bool settleFirst)
    {
        var terminal = Terminal(); var paid = TerminalAt.AddDays(1);
        var initial = Market(BuyAt); var exit = TerminalMarket(TerminalAt, terminal, ended: true);
        var cash = TerminalMarket(paid, terminal, ended: true);
        var settlement = new RawReplayItem("settle", settleFirst ? 3 : 4, cash,
            new RawReplayTerminalSettlement("listing-a", terminal.ActionId, CashSettlement(paid)));
        var buy = new RawReplayItem("buy-successor", settleFirst ? 4 : 3, cash,
            new RawReplayAssumedFill("successor", 3, 80, PriceBasis.Raw, 0));
        RawReplayItem[] events =
        [
            new("cash", 0, initial, new RawReplayDeposit(300)),
            new("old-buy", 1, initial, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
            new("capture", 2, exit, new RawReplayTerminalCapture("listing-a", terminal.ActionId, TerminalProof(terminal))),
            settleFirst ? settlement : buy,
            settleFirst ? buy : settlement,
            new("mark", 5, cash, new RawReplayMark([], []))
        ];
        if (settleFirst)
        {
            var result = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
            Assert.Equal(109.5m, result.CashGbp); Assert.Equal(3, result.Holdings["successor"].Quantity);
            Assert.Equal(289.5m, result.Marks[0].Valuation.ExpectedEquityGbp);
        }
        else Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => RawEventReplay.Run(events, Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void ReplayHandlesStockClaimDeliveryAndThenAnOrdinarySuccessorSplit()
    {
        var terminal = Terminal(TerminalOutcome.Stock); var paid = TerminalAt.AddDays(1); var after = paid.AddDays(1);
        var split = Split() with { ListingId = "successor", EffectiveAt = after, Evidence = Known(after) };
        var initial = Market(BuyAt); var exit = TerminalMarket(TerminalAt, terminal, ended: true);
        var delivered = TerminalMarket(paid, terminal, ended: true);
        var splitMarket = TerminalMarket(after.AddMinutes(3), terminal, ended: true, successorPrice: 40, extra: [split], priceCloseAt: after);
        RawReplayItem[] events =
        [
            new("cash", 0, initial, new RawReplayDeposit(300)),
            new("buy", 1, initial, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
            new("capture", 2, exit, new RawReplayTerminalCapture("listing-a", terminal.ActionId, TerminalProof(terminal))),
            new("pending", 3, exit, new RawReplayMark([], [TerminalMark(terminal, 0)])),
            new("deliver", 4, delivered, new RawReplayTerminalSettlement("listing-a", terminal.ActionId, StockSettlement(paid))),
            new("split", 5, splitMarket, new RawReplaySplit("successor", split.ActionId)),
            new("closing", 6, splitMarket, new RawReplayMark([], []))
        ];
        var result = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
        Assert.Equal(319.5m, result.Marks[0].Valuation.ExpectedEquityGbp);
        Assert.Equal(319.5m, result.Marks[1].Valuation.ExpectedEquityGbp);
        Assert.Equal(6, result.Holdings["successor"].Quantity); Assert.Equal(139.5m, result.CashGbp);
        Assert.Null(result.Marks[0].Valuation.TerminalReceivables[0].Entitlement.SettledAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalMarkCannotSilentlyDropAnUncapturedOrPendingTerminal(bool capture)
    {
        var terminal = Terminal(); var initial = Market(BuyAt); var exit = TerminalMarket(TerminalAt, terminal);
        var events = new List<RawReplayItem>
        {
            new("cash", 0, initial, new RawReplayDeposit(300)),
            new("buy", 1, initial, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0))
        };
        if (capture) events.Add(new("capture", 2, exit, new RawReplayTerminalCapture("listing-a", terminal.ActionId, TerminalProof(terminal))));
        events.Add(new("closing", events.Count, exit, new RawReplayMark([], [])));
        Assert.Equal(capture ? "raw-ledger-terminal-valuation" : "raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() =>
            RawEventReplay.Run(events, Age, Age, FxPurpose.Executable)).Code);
    }

    private sealed record UnknownReplayOperation : RawReplayOperation;
    [Fact]
    public void UnknownOperationsAndMalformedModelledFillsAreRefused()
    {
        foreach (RawReplayOperation operation in new RawReplayOperation[] { new UnknownReplayOperation(), new RawReplayModelledFill(null!, null!, null!, 0, null!) })
        {
            var events = CashReplay(); events[0] = events[0] with { Operation = operation };
            Assert.Equal("raw-replay-operation", Assert.Throws<ResearchException>(() => RawEventReplay.Run(events, Age, Age, FxPurpose.Executable)).Code);
        }
    }

    [Fact]
    public void ReplayEmitsFinalHoldingsInOrdinalIdentityOrder()
    {
        var market = TerminalMarket(BuyAt, Terminal());
        RawReplayItem[] events =
        [
            new("cash", 0, market, new RawReplayDeposit(300)),
            new("z-first", 1, market, new RawReplayAssumedFill("successor", 1, 80, PriceBasis.Raw, 0)),
            new("a-second", 2, market, new RawReplayAssumedFill("listing-a", 1, 100, PriceBasis.Raw, 0)),
            new("mark", 3, market, new RawReplayMark([], []))
        ];
        var result = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
        Assert.Equal(new[] { "listing-a", "successor" }, result.Holdings.Select(h => h.Key));
        Assert.Equal(300, result.Marks[0].Valuation.ExpectedEquityGbp);
    }

}
