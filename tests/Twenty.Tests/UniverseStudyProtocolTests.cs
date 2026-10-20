using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class UniverseStudyProtocolTests
{
    private static ImmutableArray<DateTimeOffset> Decisions(int count) => Enumerable.Range(0, count)
        .Select(i => new DateTimeOffset(2010, 2, 1, 23, 0, 0, TimeSpan.Zero).AddMonths(i).AddDays(-1)).ToImmutableArray();

    private static UniverseStudyPlan Declare(int count = 73, bool synthetic = true, string purpose = "engineering",
        ResearchCosts? costs = null, ImmutableArray<DateTimeOffset>? decisions = null) => UniverseStudyProtocol.Declare(
            new string('a', 64), "research-universe", synthetic, decisions ?? Decisions(count), 45,
            20m, costs ?? new(), purpose, new string('b', 64), "authored-artifacts-v1");

    [Fact]
    public void DeclarationSeparatesDevelopmentGapAndTwentyFourFinalOutcomes()
    {
        var plan = Declare(); var grid = Decisions(73);
        Assert.Equal(grid[12], plan.DevelopmentStart);
        Assert.Equal(grid[47], plan.DevelopmentEnd);
        Assert.Equal(grid[48], plan.ReservedStart);
        Assert.Equal(grid[72], plan.ReservedEnd);
        Assert.Equal(grid.Take(48), plan.DevelopmentDecisions);
        Assert.Equal(grid.Skip(36), plan.FinalDecisions);
        Assert.Equal(35, plan.DevelopmentDecisions.Length - 1 - 12);
        Assert.Equal(24, plan.FinalDecisions.Length - 1 - 12);
        Assert.True(plan.DevelopmentEnd < plan.ReservedStart);
        Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(Declare()));
    }

    [Fact]
    public void FixedFamilyUsesNoFutureUniverseSizeToPruneCandidates()
    {
        var plan = Declare();
        Assert.Equal(new[] { 1, 2, 4, 8 }, plan.Candidates.Select(c => c.Config.TopK));
        Assert.All(plan.Candidates, c => Assert.Equal(1m / c.Config.TopK, c.Config.MaximumWeight));
        Assert.All(plan.Candidates, c => Assert.Equal(20m, c.Config.MonthlyGbp));
        Assert.Equal(plan.BaseCosts.FxBps * 2, plan.StressCosts.FxBps);
        Assert.Equal(plan.BaseCosts.HalfSpreadBps * 2, plan.StressCosts.HalfSpreadBps);
        Assert.Equal(plan.BaseCosts.SlippageBps * 2, plan.StressCosts.SlippageBps);
        Assert.Equal(plan.BaseCosts.BuyTaxBps * 2, plan.StressCosts.BuyTaxBps);
        Assert.Equal(plan.BaseCosts.MinimumOrderGbp, plan.StressCosts.MinimumOrderGbp);
        Assert.Contains("not achievable GBP wealth", plan.UnitContract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "research")]
    [InlineData(false, "engineering")]
    public void PurposeMustFollowDatasetProvenance(bool synthetic, string purpose) => Assert.Equal(
        "study-purpose-provenance", Assert.Throws<ResearchException>(() => Declare(synthetic: synthetic, purpose: purpose)).Code);

    [Fact]
    public void MinimumDevelopmentEvidenceAndCalendarGridAreRequired()
    {
        Assert.Equal(24, Declare(62).DevelopmentDecisions.Length - 13);
        Assert.Equal("universe-study-development-window-too-short", Assert.Throws<ResearchException>(() => Declare(61)).Code);
        var bad = Decisions(73).SetItem(20, Decisions(73)[19].AddDays(1));
        Assert.Equal("universe-panel-decisions", Assert.Throws<ResearchException>(() => Declare(decisions: bad)).Code);
        Assert.Equal("universe-panel-decisions", Assert.Throws<ResearchException>(() => Declare(decisions: default(ImmutableArray<DateTimeOffset>))).Code);
    }

    [Fact]
    public void InvalidStressScheduleIsRejectedBeforeAnyPlanExists() => Assert.Equal("invalid-costs",
        Assert.Throws<ResearchException>(() => Declare(costs: new(FxBps: 3000m))).Code);

    [Fact]
    public void FinalPricePerturbationCannotChangeDevelopmentAndFinalStartsEmpty()
    {
        var bytes = UniverseDiagnosticTests.DatasetBytes(); var file = DatasetJson.Read(bytes);
        var plan = UniverseStudyProtocol.Declare(Convert.ToHexStringLower(SHA256.HashData(bytes)), "research-universe", true,
            Decisions(73), 45, 20m, new(), "engineering", new string('b', 64), "authored-artifacts-v1");
        UniverseBacktestReport Run(DatasetFile input, ImmutableArray<DateTimeOffset> dates) => UniverseBacktest.Run(
            ChangingUniversePanel.FromDataset(new PointInTimeDataset(input), plan.UniverseId, dates, TimeSpan.FromDays(plan.MaximumPriceAgeDays)),
            plan.Candidates[0].Config, plan.BaseCosts);
        var baseline = Run(file, plan.DevelopmentDecisions);
        var changed = file with { Observations = file.Observations.Select(o =>
            o is PriceBar b && b.SessionCloseAt >= plan.ReservedStart
                ? b with { Open = b.Open * 0.1m, High = b.High * 0.1m, Low = b.Low * 0.1m, Close = b.Close * 0.1m } : o).ToImmutableArray() };
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(Run(changed, plan.DevelopmentDecisions)));
        var final = Run(file, plan.FinalDecisions);
        Assert.Equal(plan.ReservedStart, final.EvaluationStart);
        Assert.Equal(plan.ReservedEnd, final.EvaluationEnd);
        Assert.Equal(24, final.Equity.Count(e => e.Strategy == "momentum"));
        Assert.All(final.Equity.Take(3), e => Assert.Equal(20m, e.State.ContributionsGbp));
        Assert.All(final.Summaries, s => Assert.Equal(480m, s.ContributionsGbp));
    }

    [Fact]
    public void EquivalentOffsetRepresentationsHaveOneCanonicalDeclaration()
    {
        var grid = Decisions(73).Select(d => d.ToOffset(TimeSpan.FromHours(-4))).ToImmutableArray();
        Assert.Equal(JsonSerializer.Serialize(Declare()), JsonSerializer.Serialize(Declare(decisions: grid)));
    }

    [Theory]
    [InlineData("dataset", "universe-study-declaration")]
    [InlineData("code", "universe-study-declaration")]
    [InlineData("universe", "universe-study-declaration")]
    [InlineData("scheme", "universe-study-declaration")]
    [InlineData("price-age", "universe-panel-decisions")]
    [InlineData("too-many", "universe-panel-decisions")]
    [InlineData("monthly", "invalid-universe-backtest-config")]
    public void InvalidIdentityAndExecutionAssumptionsCannotProduceAPlan(string change, string expected)
    {
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => UniverseStudyProtocol.Declare(
            change == "dataset" ? "not-a-sha" : new string('a', 64), change == "universe" ? "\n" : "research-universe", true,
            Decisions(change == "too-many" ? 601 : 73), change == "price-age" ? 93 : 45,
            change == "monthly" ? 0m : 20m, new(), "engineering", change == "code" ? "" : new string('b', 64),
            change == "scheme" ? " " : "authored-artifacts-v1")).Code);
    }
}
