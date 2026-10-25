using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawUniverseStudyProtocolTests
{
    private static ImmutableArray<DateTimeOffset> Grid(int count = 73) => Enumerable.Range(0, count)
        .Select(i => new DateTimeOffset(2010, 2, 1, 23, 0, 0, TimeSpan.Zero).AddMonths(i).AddDays(-1)).ToImmutableArray();
    private static readonly RawStudyExecutionPolicy Execution = new(45, 2, 5, 1, new(5, 7, 1, .05m, .001m));
    private static readonly ImmutableArray<RawStudyCalendarPin> Calendars = [new("nasdaq", "XNAS", new string('c', 64)), new("london", "XLON", new string('d', 64))];
    private static RawUniverseStudyPlan Declare(ImmutableArray<DateTimeOffset>? grid = null, RawStudyExecutionPolicy? execution = null,
        ImmutableArray<RawStudyCalendarPin>? calendars = null, bool synthetic = true, string purpose = "engineering") =>
        RawUniverseStudyProtocol.Declare(new string('a', 64), "research-universe", synthetic, purpose, new string('b', 64),
            "authored-science-v1", grid ?? Grid(), 20, execution ?? Execution, calendars ?? Calendars, new string('e', 64));

    [Fact]
    public void DeclarationFreezesAllEightTrialsWithoutReadingAnyMarketInputs()
    {
        var plan = Declare();
        Assert.Equal(new[] { 1, 2, 4, 8 }, plan.Candidates.Select(c => c.Config.TopK));
        Assert.All(plan.Candidates, c => { Assert.Equal(20, c.Config.MonthlyGbp); Assert.Equal(1m / c.Config.TopK, c.Config.MaximumWeight); });
        Assert.Equal(8, plan.DevelopmentTrials.Length); Assert.Equal(2, plan.ReservedFinalTrials);
        foreach (var c in plan.Candidates)
            Assert.Equal(new[] { "base", "stress" }, plan.DevelopmentTrials.Where(t => t.CandidateId == c.Id).Select(t => t.CostScenario));
        Assert.Equal(RawReplayBuildIdentity.Current, plan.Build);
        Assert.Contains("raw", plan.UnitContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not broker", plan.UnitContract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContributionsStartAfterWarmupAndNeverTransferAcrossTheSelectionGap()
    {
        var plan = Declare(); var grid = Grid();
        Assert.Equal(grid[12], plan.DevelopmentStart); Assert.Equal(grid[47], plan.DevelopmentEnd);
        Assert.Equal(grid[48], plan.ReservedStart); Assert.Equal(grid[72], plan.ReservedEnd);
        Assert.Equal(grid.Skip(12).Take(35), plan.DevelopmentContributions.Select(c => c.At));
        Assert.Equal(grid.Skip(48).Take(24), plan.FinalContributions.Select(c => c.At));
        Assert.All(plan.DevelopmentContributions.Concat(plan.FinalContributions), c => Assert.Equal(20, c.AmountGbp));
        Assert.DoesNotContain(plan.DevelopmentContributions, c => c.At >= plan.DevelopmentEnd);
        Assert.DoesNotContain(plan.FinalContributions, c => c.At == plan.ReservedEnd);
        Assert.Contains("empty", plan.StartingLedgerRule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-transfer", plan.StartingLedgerRule, StringComparison.Ordinal);
    }

    [Fact]
    public void StressDoublesSpreadAndSlippageWithoutRelaxingOtherExecutionConstraints()
    {
        var p = Declare(); var stress = p.StressExecution;
        Assert.Equal(10, stress.Fills.HalfSpreadBps); Assert.Equal(14, stress.Fills.SlippageBps);
        Assert.Equal(p.BaseExecution, stress with { Fills = p.BaseExecution.Fills });
        Assert.Equal(p.BaseExecution.Fills, stress.Fills with { HalfSpreadBps = 5, SlippageBps = 7 });
        Assert.Equal(5, Execution.Fills.HalfSpreadBps);
    }

    [Fact]
    public void EquivalentOffsetsAndCalendarOrderingProduceIdenticalPlanBytes()
    {
        var a = RawUniverseStudyProtocol.Encode(Declare());
        var b = RawUniverseStudyProtocol.Encode(Declare(Grid().Select(d => d.ToOffset(TimeSpan.FromHours(3))).ToImmutableArray(),
            calendars: Calendars.Reverse().ToImmutableArray()));
        Assert.Equal(a, b);
        Assert.Equal(a, RawUniverseStudyProtocol.Encode(RawUniverseStudyProtocol.Decode(a)));
    }

    [Theory]
    [InlineData(61, false)]
    [InlineData(62, true)]
    public void TheMinimumGridRetainsTwentyFourDevelopmentAndFinalIntervals(int months, bool accepted)
    {
        if (!accepted) Assert.Equal("universe-study-development-window-too-short", Assert.Throws<ResearchException>(() => Declare(Grid(months))).Code);
        else { var plan = Declare(Grid(months)); Assert.Equal(24, plan.DevelopmentContributions.Length); Assert.Equal(24, plan.FinalContributions.Length); }
    }

    [Theory]
    [InlineData("fee")]
    [InlineData("reserve")]
    [InlineData("fx-age")]
    [InlineData("stress")]
    [InlineData("participation")]
    public void UnsupportedExecutionAssumptionsRefuseAtDeclaration(string change)
    {
        var execution = change switch
        {
            "fee" => Execution with { FeePerFillGbp = -1 },
            "reserve" => Execution with { CashReserveGbp = -1 },
            "fx-age" => Execution with { MaximumFxAgeDays = 93 },
            "stress" => Execution with { Fills = Execution.Fills with { HalfSpreadBps = 3000 } },
            _ => Execution with { Fills = Execution.Fills with { MaximumParticipation = 0 } }
        };
        Assert.Equal("raw-study-execution-policy", Assert.Throws<ResearchException>(() => Declare(execution: execution)).Code);
    }

    [Theory]
    [InlineData(true, "research")]
    [InlineData(false, "engineering")]
    public void SyntheticPurposeCannotMasqueradeAsResearch(bool synthetic, string purpose) =>
        Assert.Equal("study-purpose-provenance", Assert.Throws<ResearchException>(() => Declare(synthetic: synthetic, purpose: purpose)).Code);

    [Fact]
    public void DuplicateOrUnboundCalendarTablesAreRefused()
    {
        Assert.Equal("raw-study-calendar-pins", Assert.Throws<ResearchException>(() => Declare(calendars: [Calendars[0], Calendars[0]])).Code);
        Assert.Equal("raw-study-calendar-pins", Assert.Throws<ResearchException>(() => Declare(calendars: [Calendars[0] with { Sha256 = "unknown" }])).Code);
    }

    [Theory]
    [InlineData("contribution")]
    [InlineData("candidate")]
    [InlineData("stress")]
    [InlineData("trial")]
    [InlineData("rule")]
    public void DecodingRecomputesDerivedSchedulesAndFamilyInsteadOfTrustingRehashedFields(string change)
    {
        var p = Declare();
        p = change switch
        {
            "contribution" => p with { FinalContributions = p.FinalContributions.SetItem(0, p.FinalContributions[0] with { AmountGbp = 200 }) },
            "candidate" => p with { Candidates = p.Candidates.RemoveAt(0) },
            "stress" => p with { StressExecution = p.BaseExecution },
            "trial" => p with { DevelopmentTrials = p.DevelopmentTrials.RemoveAt(0) },
            _ => p with { StartingLedgerRule = "carry-development-winner" }
        };
        Assert.Equal("raw-study-plan-integrity", Assert.Throws<ResearchException>(() => RawUniverseStudyProtocol.Decode(
            JsonSerializer.SerializeToUtf8Bytes(p, RawReplayJson.Options))).Code);
    }

    [Fact]
    public void DevelopmentInstructionsUseTheFrozenCandidateCostsAndIdenticalComparatorDeposits()
    {
        var p = Declare(); var at = p.DevelopmentStart;
        var d = RawUniverseStudyProtocol.DevelopmentDecision(p, "momentum:4:stress", at);
        Assert.Equal("momentum:4", d.CandidateId); Assert.Equal(p.StressExecution, d.Execution);
        Assert.Equal(new RawStudyContribution(at, 20), d.Contribution); Assert.Equal(Grid()[13], d.NextDecisionAt);
        Assert.Equal(new[] { "momentum", "equal_weight", "cash" }, d.Strategies.Select(s => s.Strategy));
        Assert.All(d.Strategies, s => { Assert.Equal(4, s.Config.TopK); Assert.Equal(.25m, s.Config.MaximumWeight);
            Assert.Equal(13, s.Decisions.Length); Assert.Equal(at, s.Decisions[^1]); });
    }

    [Fact]
    public void DevelopmentInstructionsRetainTheirExactPlanAndOriginalInputReferences()
    {
        var p = Declare(); var d = RawUniverseStudyProtocol.DevelopmentDecision(p, "momentum:1:base", p.DevelopmentStart);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(RawUniverseStudyProtocol.Encode(p)));
        Assert.Equal(hash, d.PlanSha256); Assert.Equal(p.DatasetSha256, d.DatasetSha256);
        Assert.Equal(p.ActionCatalogSha256, d.ActionCatalogSha256); Assert.Equal(p.Calendars, d.Calendars);
    }

    [Fact]
    public void PureDevelopmentInstructionsCannotAuthorizeReservedFinalDecisions()
    {
        var p = Declare();
        foreach (var at in new[] { Grid()[11], p.DevelopmentEnd, p.ReservedStart, p.ReservedEnd })
            Assert.Equal("raw-study-development-decision", Assert.Throws<ResearchException>(() =>
                RawUniverseStudyProtocol.DevelopmentDecision(p, "momentum:1:base", at)).Code);
        Assert.Equal("raw-study-development-decision", Assert.Throws<ResearchException>(() =>
            RawUniverseStudyProtocol.DevelopmentDecision(p, "undeclared", p.DevelopmentStart)).Code);
    }

    [Fact]
    public void MaximumGridStillFitsTheBoundedPlanAndContributionsNeverExceedSixHundred()
    {
        var p = Declare(Grid(600)); var bytes = RawUniverseStudyProtocol.Encode(p);
        Assert.True(bytes.Length < RawUniverseStudyProtocol.MaximumPlanBytes);
        Assert.True(p.DevelopmentContributions.Length + p.FinalContributions.Length < 600);
        Assert.Equal(bytes, RawUniverseStudyProtocol.Encode(RawUniverseStudyProtocol.Decode(bytes)));
    }

    [Fact]
    public void LastDevelopmentInstructionStopsAtTheDevelopmentBoundary()
    {
        var p = Declare(); var last = p.DevelopmentContributions[^1];
        var instruction = RawUniverseStudyProtocol.DevelopmentDecision(p, "momentum:8:base", last.At.ToOffset(TimeSpan.FromHours(3)));
        Assert.Equal(p.DevelopmentEnd, instruction.NextDecisionAt);
        Assert.True(instruction.NextDecisionAt < p.ReservedStart);
        Assert.Equal(last, instruction.Contribution);
    }

    [Theory]
    [InlineData("version", "raw-study-version")]
    [InlineData("build", "raw-study-build")]
    [InlineData("default", "raw-study-plan-integrity")]
    public void UnknownVersionsBuildsAndUninitialisedDerivedArraysFailClosed(string change, string code)
    {
        var p = Declare();
        p = change switch
        {
            "version" => p with { Version = "unknown" },
            "build" => p with { Build = p.Build with { CoreSha256 = new string('f', 64) } },
            _ => p with { Candidates = default }
        };
        Assert.Equal(code, Assert.Throws<ResearchException>(() => RawUniverseStudyProtocol.Encode(p)).Code);
    }

    [Fact]
    public void WireReaderRejectsDuplicateMembersUnknownMembersAndOversizedInputs()
    {
        var text = System.Text.Encoding.UTF8.GetString(RawUniverseStudyProtocol.Encode(Declare()));
        Assert.Throws<JsonException>(() => RawUniverseStudyProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(text.Insert(1, "\"unexpected\":true,"))));
        Assert.Throws<JsonException>(() => RawUniverseStudyProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(text.Insert(1, "\"version\":\"unknown\","))));
        Assert.Equal("raw-replay-size", Assert.Throws<ResearchException>(() => RawUniverseStudyProtocol.Decode(new byte[RawUniverseStudyProtocol.MaximumPlanBytes + 1])).Code);
    }
}
