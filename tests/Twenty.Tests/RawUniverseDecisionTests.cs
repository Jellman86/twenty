using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawUniverseDecisionTests
{
    private static RawUniverseStrategySpec Spec(int end = 12, string strategy = "momentum", int topK = 1) =>
        new(RawUniverseDecision.Version, "research-universe", Enumerable.Range(end - 12, 13)
            .Select(UniverseBacktestTests.Decision).ToImmutableArray(), 45, new(TopK: topK, MaximumWeight: 1m / topK), strategy);
    private static RawUniverseAllocation Allocate(DatasetFile? file = null, RawUniverseStrategySpec? spec = null) =>
        RawUniverseDecision.Allocate(new(file ?? UniverseBacktestTests.Scenario()), spec ?? Spec());

    [Fact]
    public void MomentumUsesElevenCompletedPeriodsAndSkipsTheLatestPeriod()
    {
        var allocation = Allocate();
        Assert.Equal(UniverseBacktestTests.Decision(12), allocation.DecisionAt);
        Assert.Equal(new[] { "aaa", "bbb", "ccc" }, allocation.HoldableListingIds);
        Assert.Equal(1, allocation.Weights["aaa"]); Assert.Single(allocation.Weights);
        Assert.Equal(Math.Pow(1.01, 11) - 1, allocation.MomentumScores.Single(s => s.ListingId == "aaa").Score!.Value, 10);
        var source = UniverseBacktestTests.Scenario();
        var last = source.Observations.OfType<PriceBar>().Single(p => p.Id == "bbb-bar-12");
        var changed = source with { Observations = source.Observations.Replace(last, last with { Open = 10000, High = 10000, Low = 10000, Close = 10000 }) };
        Assert.Equal(JsonSerializer.Serialize(allocation.Weights), JsonSerializer.Serialize(Allocate(changed).Weights));
    }

    [Fact]
    public void FuturePricesMembershipAndTerminalOutcomesCannotChangeTheDecision()
    {
        var source = UniverseBacktestTests.Scenario(); var cutoff = UniverseBacktestTests.Decision(12);
        var prefix = source with { Observations = source.Observations.Where(o => o.Evidence.AvailableAt <= cutoff).ToImmutableArray() };
        Assert.Equal(JsonSerializer.Serialize(Allocate(source)), JsonSerializer.Serialize(Allocate(prefix)));
        Assert.Equal(JsonSerializer.Serialize(Allocate(source)), JsonSerializer.Serialize(Allocate(UniverseBacktestTests.Scenario(cccOutcome: TerminalOutcome.Cash, cccCash: 999))));
    }

    [Fact]
    public void NewEntrantsRemainVisibleButDoNotReceiveInventedMomentumHistory()
    {
        var a = Allocate(spec: Spec(16));
        Assert.Contains("ddd", a.HoldableListingIds);
        Assert.Null(a.MomentumScores.Single(s => s.ListingId == "ddd").Score);
        Assert.DoesNotContain("ddd", a.Weights.Keys);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("equal_weight")]
    public void ComparatorWeightsAreDeclaredWithoutFutureReturns(string strategy)
    {
        var a = Allocate(spec: Spec(strategy: strategy));
        if (strategy == "cash") Assert.Empty(a.Weights);
        else { Assert.Equal(3, a.Weights.Count); Assert.True(a.Weights.Values.Sum() <= 1); Assert.All(a.Weights.Values, w => Assert.Equal(a.Weights["aaa"], w)); }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("future")]
    [InlineData("strategy")]
    [InlineData("version")]
    public void InvalidDecisionContractsRefuseBeforeReadingOutcomes(string change)
    {
        var spec = Spec();
        spec = change switch
        {
            "short" => spec with { Decisions = spec.Decisions.RemoveAt(0) },
            "future" => spec with { Decisions = spec.Decisions.SetItem(12, spec.Decisions[11]) },
            "strategy" => spec with { Strategy = "winner" },
            _ => spec with { Version = "unknown" }
        };
        Assert.Equal("universe-decision-spec", Assert.Throws<ResearchException>(() => Allocate(spec: spec)).Code);
    }

    [Fact]
    public void RankCapsLeaveCashInsteadOfRenormalisingSparseEligibleHistory()
    {
        var allocation = Allocate(spec: Spec(topK: 8));
        Assert.Equal(3, allocation.Weights.Count); Assert.Equal(.375m, allocation.Weights.Values.Sum());
    }

    [Fact]
    public void ExactScoreTiesUseOrdinalListingIdentity()
    {
        var source = UniverseBacktestTests.Scenario();
        source = source with { Observations = source.Observations.Select(o => o is PriceBar p && p.ListingId is "aaa" or "bbb"
            ? p with { Open = 100, High = 100, Low = 100, Close = 100 } : o).ToImmutableArray() };
        Assert.Equal("aaa", Assert.Single(Allocate(source).Weights).Key);
    }

    [Fact]
    public void IncompleteHistoricalCoverageIsRefusedRatherThanDroppingTheBadName()
    {
        var spec = Spec(22);
        Assert.Equal("universe-decision-history-gaps", Assert.Throws<ResearchException>(() =>
            Allocate(UniverseBacktestTests.Scenario(gapBbb: true), spec)).Code);
    }

    [Fact]
    public void EquivalentOffsetsRetainExactlyTheSameAllocation()
    {
        var spec = Spec();
        Assert.Equal(JsonSerializer.Serialize(Allocate(spec: spec)), JsonSerializer.Serialize(Allocate(spec: spec with
            { Decisions = spec.Decisions.Select(d => d.ToOffset(TimeSpan.FromHours(3))).ToImmutableArray() })));
    }

    [Fact]
    public void ALaterRevisionOfAHistoricalBarCannotLeakIntoEarlierRanking()
    {
        var source = UniverseBacktestTests.Scenario(); var original = source.Observations.OfType<PriceBar>().Single(p => p.Id == "bbb-bar-0");
        var later = UniverseBacktestTests.Decision(13);
        var revised = original with { Id = "bbb-late-revision", Open = 1, High = 1, Low = 1, Close = 1,
            Evidence = original.Evidence with { AvailableAt = later, PublishedAt = later, RevisionOf = original.Id } };
        Assert.Equal(JsonSerializer.Serialize(Allocate(source)), JsonSerializer.Serialize(Allocate(source with { Observations = source.Observations.Add(revised) })));
    }
}
