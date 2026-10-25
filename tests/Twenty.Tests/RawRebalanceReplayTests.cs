using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static (RawSchedulePlan Schedule, byte[] Dataset) BoundRebalanceFixture()
    {
        var f = RebalanceScheduleFixture();
        var timeline = f.Schedule.Timeline.ToBuilder();
        timeline.Insert(2, new(new("decision", 0, timeline[0].Point.DatasetSha256), f.Decision.Valuation.At,
            new RawReplayRebalanceDecision(f.Decision)));
        for (var i = 3; i < 5; i++)
        {
            var fill = (RawReplayModelledFill)timeline[i].Operation;
            timeline[i] = timeline[i] with { Operation = fill with { Rebalance = new("decision", i - 3) } };
        }
        return (f.Schedule with { Timeline = timeline.ToImmutable() }, f.Dataset);
    }

    private static RawReplayReceipt ReplayBound(RawSchedulePlan plan, byte[] dataset)
    {
        var compiled = RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options), _ => dataset);
        return RawReplayArchive.Execute(compiled, _ => dataset);
    }

    [Fact]
    public void ReplayedRebalanceRecomputesOriginalWeightsAgainstTheReconstructedStartingLedger()
    {
        var f = BoundRebalanceFixture(); var receipt = ReplayBound(f.Schedule, f.Dataset);
        Assert.Equal(receipt, RawReplayArchive.Verify(JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options), _ => f.Dataset));
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(35.6m, result.CashGbp); Assert.Equal(7.2m, result.Holdings["listing-b"].Quantity);
    }

    [Theory]
    [InlineData("weights")]
    [InlineData("starting-cash")]
    [InlineData("starting-shares")]
    [InlineData("quote")]
    [InlineData("fee-budget")]
    public void RebalanceDecisionCannotRetainItsOrdersAfterItsInputsOrDerivedEvidenceChange(string change)
    {
        var f = BoundRebalanceFixture(); var timeline = f.Schedule.Timeline.ToBuilder();
        var decision = (RawReplayRebalanceDecision)timeline[2].Operation;
        if (change == "starting-cash") timeline[0] = timeline[0] with { Operation = new RawReplayDeposit(301) };
        else if (change == "starting-shares") timeline[1] = timeline[1] with { Operation = ((RawReplayAssumedFill)timeline[1].Operation) with { SignedQuantity = 1 } };
        else
        {
            var plan = decision.Plan;
            plan = change switch
            {
                "weights" => plan with { Weights = plan.Weights.SetItem("listing-b", .5m) },
                "quote" => plan with { Targets = plan.Targets.SetItem(0, plan.Targets[0] with { Quote = plan.Targets[0].Quote with { Price = plan.Targets[0].Quote.Price with { Close = 99 } } }) },
                _ => plan with { FeeBudgetGbp = 0 }
            };
            timeline[2] = timeline[2] with { Operation = new RawReplayRebalanceDecision(plan) };
        }
        Assert.Equal("raw-replay-rebalance-plan", Assert.Throws<ResearchException>(() =>
            ReplayBound(f.Schedule with { Timeline = timeline.ToImmutable() }, f.Dataset)).Code);
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("wrong-decision")]
    [InlineData("wrong-index")]
    [InlineData("fee")]
    [InlineData("order")]
    [InlineData("missing-fill")]
    [InlineData("intervening-cash")]
    public void BoundRebalanceCannotSubstituteSkipOrInterruptItsOrderedFills(string change)
    {
        var f = BoundRebalanceFixture(); var timeline = f.Schedule.Timeline.ToBuilder();
        var fill = (RawReplayModelledFill)timeline[3].Operation;
        if (change == "missing-fill") timeline.RemoveAt(4);
        else if (change == "intervening-cash") timeline[3] = timeline[3] with { Operation = new RawReplayDeposit(1) };
        else timeline[3] = timeline[3] with { Operation = change switch
        {
            "missing-reference" => fill with { Rebalance = null },
            "wrong-decision" => fill with { Rebalance = new("unknown", 0) },
            "wrong-index" => fill with { Rebalance = new("decision", 1) },
            "fee" => fill with { FeeGbp = 0 },
            _ => fill with { Order = fill.Order with { Quantity = 1 } }
        } };
        Assert.Equal("raw-replay-rebalance-order", Assert.Throws<ResearchException>(() =>
            ReplayBound(f.Schedule with { Timeline = timeline.ToImmutable() }, f.Dataset)).Code);
    }

    [Fact]
    public void AReferenceCannotClaimAnAbsentDecision()
    {
        var f = BoundRebalanceFixture();
        Assert.Equal("raw-replay-rebalance-order", Assert.Throws<ResearchException>(() =>
            ReplayBound(f.Schedule with { Timeline = f.Schedule.Timeline.RemoveAt(2) }, f.Dataset)).Code);
    }

    [Fact]
    public void CompletedBatchesCannotReuseAReferencedOrder()
    {
        var f = BoundRebalanceFixture(); var timeline = f.Schedule.Timeline.ToBuilder();
        timeline.Insert(5, timeline[4] with { Point = timeline[4].Point with { Id = "duplicate", TieOrder = 2 } });
        timeline[6] = timeline[6] with { Point = timeline[6].Point with { TieOrder = 3 } };
        Assert.Equal("raw-replay-rebalance-order", Assert.Throws<ResearchException>(() =>
            ReplayBound(f.Schedule with { Timeline = timeline.ToImmutable() }, f.Dataset)).Code);
    }

    [Fact]
    public void NoOpDecisionsNeedNoFillsAndDoNotManufactureFees()
    {
        var f = BoundRebalanceFixture(); var original = (RawReplayRebalanceDecision)f.Schedule.Timeline[2].Operation;
        var file = DatasetJson.Read(f.Dataset); var at = original.Plan.Valuation.At;
        var market = new PointInTimeDataset(file).AsOf(at);
        var plan = Rebalance(DividendBook(), market, new Dictionary<string, decimal> { ["listing-a"] = 1 }, 140, 1);
        Assert.Empty(plan.Orders);
        var schedule = f.Schedule with { Timeline = f.Schedule.Timeline.Take(2).Append(f.Schedule.Timeline[2] with
            { Operation = new RawReplayRebalanceDecision(plan) }).Append(f.Schedule.Timeline[^1]).ToImmutableArray() };
        var receipt = ReplayBound(schedule, f.Dataset);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(140, result.CashGbp); Assert.Equal(0, result.FeesGbp); Assert.Equal(2, result.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void RebalanceVerificationDoesNotDependOnKeepingTheScheduleWrapper()
    {
        var f = BoundRebalanceFixture();
        var bytes = RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(f.Schedule, RawReplayJson.Options), _ => f.Dataset);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(bytes, RawReplayJson.Options)!;
        var decision = (RawReplayRebalanceDecision)request.Events[2].Operation;
        var edited = request with { Schedule = null, Events = request.Events.SetItem(2, request.Events[2] with
            { Operation = decision with { Plan = decision.Plan with { ProjectedCashAfterFeesGbp = 123 } } }) };
        Assert.Equal("raw-replay-rebalance-plan", Assert.Throws<ResearchException>(() =>
            RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(edited, RawReplayJson.Options), _ => f.Dataset)).Code);
    }
}
