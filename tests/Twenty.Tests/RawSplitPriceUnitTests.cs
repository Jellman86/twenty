using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(.5)]
    public void RawSplitQuoteCannotCreateAForwardOrReverseSplitGainOrLoss(decimal ratio)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(ratio: ratio); var market = Market(BuyAt.AddDays(1), priceAgeDays: 1, actions: [action]);
        ledger.ApplySplit("split", market, "listing-a", action.ActionId); var entries = ledger.Entries;
        Assert.Equal("raw-price-share-units", Assert.Throws<ResearchException>(() => ledger.Value(market, Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal(ratio, ledger.Holdings["listing-a"].Quantity); Assert.Equal(120, ledger.CashGbp); Assert.Equal(entries, ledger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawSplitQuoteTargetsRequireCurrentUnitsWithoutAnExistingHolding(bool claims)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 200);
        var market = RebalanceMarket(Market(BuyAt.AddDays(1), priceAgeDays: 1, actions: [Split()]));
        var weights = new Dictionary<string, decimal> { ["listing-a"] = 1 };
        var error = Assert.Throws<ResearchException>(() =>
        {
            if (claims) _ = RawRebalancePlanner.PlanWithReceivables(ledger, market, "synthetic-isa", weights, market.DecisionAt.AddDays(7), 0, 0, Age, Age, [], []);
            else _ = Rebalance(ledger, market, weights);
        });
        Assert.Equal("raw-price-share-units", error.Code); Assert.Empty(ledger.Holdings); Assert.Equal(200, ledger.CashGbp);
    }

    [Fact]
    public void RawSplitQuoteReplayCannotPublishAnOldUnitClosingMark()
    {
        var initial = Market(BuyAt); var action = Split(); var market = Market(BuyAt.AddDays(1), priceAgeDays: 1, actions: [action]);
        RawReplayItem[] events = [new("cash", 0, initial, new RawReplayDeposit(200)),
            new("buy", 1, initial, new RawReplayAssumedFill("listing-a", 1, 100, PriceBasis.Raw, 0)),
            new("split", 2, market, new RawReplaySplit("listing-a", action.ActionId)),
            new("close", 3, market, new RawReplayMark([], []))];
        Assert.Equal("raw-price-share-units", Assert.Throws<ResearchException>(() => RawEventReplay.Run(events, Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void RawSplitQuoteAfterTheEffectiveBoundaryPreservesEconomicValue()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(); var market = Market(BuyAt.AddDays(1), price: 50, actions: [action]);
        ledger.ApplySplit("split", market, "listing-a", action.ActionId);
        Assert.Equal(200, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Fact]
    public void RawSplitQuoteIdentityRatioDoesNotChangeShareUnits()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var action = Split(ratio: 1); var market = Market(BuyAt.AddDays(1), priceAgeDays: 1, actions: [action]);
        ledger.ApplySplit("identity", market, "listing-a", action.ActionId);
        Assert.Equal(200, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Fact]
    public void RawSplitQuoteDoesNotPeekAtLaterSplitDisclosure()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("cash", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var at = BuyAt.AddDays(1); var action = Split() with { Evidence = Known(at.AddHours(1)) };
        var market = Market(at, priceAgeDays: 1, actions: [action]); Assert.Empty(market.Observations.OfType<CorporateAction>());
        Assert.Equal(200, ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp);
    }

    [Fact]
    public void RawSplitQuotePendingStockClaimRequiresSuccessorQuoteInCurrentUnits()
    {
        var ledger = DividendBook(); var action = Terminal(TerminalOutcome.Stock);
        var split = new CorporateAction("successor-before-exit", Known(TerminalAt.AddMinutes(-15)), "successor", "successor-split",
            CorporateActionKind.Split, TerminalAt.AddMinutes(-15), 2, null, null, null, null, TerminalOutcome.NotApplicable);
        var market = TerminalMarket(TerminalAt, action, extra: [split]);
        ledger.CaptureTerminal("exit", market, "listing-a", action.ActionId, TerminalProof(action));
        Assert.Equal("raw-price-share-units", Assert.Throws<ResearchException>(() => ledger.ValueWithReceivables(
            market, Age, Age, FxPurpose.Executable, [], [TerminalMark(action, 0, 0)])).Code);
    }
}

public sealed partial class RawPaperStoreTests
{
    [Fact]
    public void RawSplitQuoteNeutralPaperSplitDoesNotDemandANewerClose()
    {
        var request = SplitRequest(fixture.Create.Decisions[13]);
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", request.DatasetSha256 + ".json")));
        var action = file.Observations.OfType<CorporateAction>().Single();
        file = file with { Observations = file.Observations.Replace(action, action with { SplitRatio = 1 }) };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var account = store.CreateRawPaperAccount(request with { DatasetSha256 = sha });
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp); Assert.Equal(.246875m, Assert.Single(preview.ProjectedBalance.Holdings).Value.Quantity);
    }
}
