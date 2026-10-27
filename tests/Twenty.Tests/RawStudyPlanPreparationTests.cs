using System.Collections.Immutable;
using System.Security.Cryptography;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyAdmissionTests
{
    private static RawStudyPlanPreparationRequest Preparation(RawUniverseStudyPlan plan) => new(
        plan.DatasetSha256, plan.UniverseId, plan.Synthetic, plan.Purpose, new(2010, 1, 1), 73, new(23, 0),
        plan.MonthlyGbp, new(plan.BaseExecution.MaximumPriceAgeDays, plan.BaseExecution.MaximumFxAgeDays,
            plan.BaseExecution.CashReserveGbp, plan.BaseExecution.FeePerFillGbp,
            plan.BaseExecution.Fills.HalfSpreadBps, plan.BaseExecution.Fills.SlippageBps,
            plan.BaseExecution.Fills.MinimumOrderValue, plan.BaseExecution.Fills.MaximumParticipation, plan.BaseExecution.Fills.QuantityStep),
        plan.Calendars, plan.ActionCatalogSha256);

    [Fact]
    public void PreparationIsMetadataOnlyAndItsExactBytesCanBeAdmittedSeparately()
    {
        var registration = Request();
        var plan = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(registration.PlanBase64!));
        var request = Preparation(plan);
        // None of these originals may be read in preparation, even their JSON headers.
        var datasets = Path.Combine(root, "universe-datasets"); Directory.Move(datasets, datasets + ".hidden");
        var imports = Path.Combine(root, "imports"); Directory.Move(imports, imports + ".hidden");
        var prepared = store.PrepareRawStudyPlan(request);
        Assert.Equal(plan, RawUniverseStudyProtocol.Decode(Convert.FromBase64String(prepared.PlanBase64)), new PlanByteComparer());
        var bytes = Convert.FromBase64String(prepared.PlanBase64);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), prepared.PlanSha256);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(imports, prepared.PlanSha256 + ".raw-study.json")));
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
        store.Dispose(); store = new(root);
        Assert.Equal(prepared, store.PrepareRawStudyPlan(request));
        Assert.Single(Directory.GetFiles(imports));
        Assert.Equal("dataset-not-found", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(
            new(Guid.NewGuid().ToString("D"), "Separate admission", PlanSha256: prepared.PlanSha256), "authored")).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
        Directory.Move(datasets + ".hidden", datasets);
        foreach (var original in Directory.GetFiles(imports + ".hidden")) File.Move(original, Path.Combine(imports, Path.GetFileName(original)));
        var admitted = store.RegisterRawStudy(new(Guid.NewGuid().ToString("D"), "Separate admission", PlanSha256: prepared.PlanSha256), "authored");
        Assert.Equal(prepared.PlanBase64, admitted.PlanBase64); Assert.Equal(8, store.ListRawTrials().Count);
    }

    private sealed class PlanByteComparer : IEqualityComparer<RawUniverseStudyPlan>
    {
        public bool Equals(RawUniverseStudyPlan? x, RawUniverseStudyPlan? y) => x is not null && y is not null &&
            RawUniverseStudyProtocol.Encode(x).SequenceEqual(RawUniverseStudyProtocol.Encode(y));
        public int GetHashCode(RawUniverseStudyPlan obj) => 0;
    }

    [Fact]
    public void PreparedUtcMonthEndsHandleLeapYearsAndDoNotInferSessions()
    {
        var plan = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(Request().PlanBase64!));
        var prepared = store.PrepareRawStudyPlan(Preparation(plan) with { FirstMonth = new(2012, 1, 1), DecisionTimeUtc = new(0, 5) });
        var result = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(prepared.PlanBase64));
        Assert.Equal(new DateTimeOffset(2012, 2, 29, 0, 5, 0, TimeSpan.Zero), result.Decisions[1]);
        Assert.Equal(new DateTimeOffset(2013, 2, 28, 0, 5, 0, TimeSpan.Zero), result.Decisions[13]);
        Assert.All(result.Decisions, d => Assert.Equal(TimeSpan.Zero, d.Offset));
        Assert.Equal(24, result.FinalContributions.Length);
        Assert.Equal(new[] { "momentum:1", "momentum:2", "momentum:4", "momentum:8" }, result.Candidates.Select(c => c.Id));
        Assert.Equal(plan.BaseExecution, result.BaseExecution);
        Assert.Equal(plan.BaseExecution.Fills.HalfSpreadBps * 2, result.StressExecution.Fills.HalfSpreadBps);
    }

    [Theory]
    [InlineData("count")][InlineData("short")][InlineData("day")][InlineData("overflow")]
    [InlineData("policy")][InlineData("hash")][InlineData("pins")][InlineData("provenance")]
    public void InvalidPreparationCreatesNoPlanOrTrial(string mutation)
    {
        var plan = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(Request().PlanBase64!));
        var request = Preparation(plan);
        request = mutation switch {
            "count" => request with { DecisionCount = 601 }, "short" => request with { DecisionCount = 20 },
            "day" => request with { FirstMonth = new(2010, 1, 2) }, "overflow" => request with { FirstMonth = new(9999, 1, 1) },
            "policy" => request with { Execution = request.Execution with { MaximumParticipation = 0 } },
            "hash" => request with { DatasetSha256 = "../unsafe" }, "pins" => request with { Calendars = [] },
            _ => request with { Purpose = "research" }
        };
        Assert.Throws<ResearchException>(() => store.PrepareRawStudyPlan(request));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "imports"), "*.raw-study.json"));
        Assert.Empty(store.ListRawTrials()); Assert.Empty(store.RawStudies());
    }

    [Fact]
    public void PreparationIsBoundedAndRetainedContentCannotBeOverwritten()
    {
        var request = Preparation(RawUniverseStudyProtocol.Decode(Convert.FromBase64String(Request().PlanBase64!)));
        var prepared = store.PrepareRawStudyPlan(request);
        var path = Path.Combine(root, "imports", prepared.PlanSha256 + ".raw-study.json");
        File.WriteAllText(path, "corrupt");
        Assert.Equal("artifact-integrity", Assert.Throws<ResearchException>(() => store.PrepareRawStudyPlan(request)).Code);
        Assert.Equal("corrupt", File.ReadAllText(path));
        File.WriteAllBytes(path, Convert.FromBase64String(prepared.PlanBase64));
        for (var i = 0; i < 99; i++) File.WriteAllText(Path.Combine(root, "imports", i.ToString("x64") + ".raw-study.json"), "{}");
        Assert.Equal(prepared, store.PrepareRawStudyPlan(request));
        Assert.Equal("raw-study-plan-capacity", Assert.Throws<ResearchException>(() => store.PrepareRawStudyPlan(request with { MonthlyGbp = 21 })).Code);
        Assert.Empty(store.ListRawTrials());
    }
    [Fact]
    public void PreparationCannotConsumeArtifactHeadroomAndAnExactRetryNeedsNoNewSpace()
    {
        var request = Preparation(RawUniverseStudyProtocol.Decode(Convert.FromBase64String(Request().PlanBase64!)));
        var prepared = store.PrepareRawStudyPlan(request);
        using (var file = File.Create(Path.Combine(root, "authored-pressure.bin"))) file.SetLength(256L * 1024 * 1024);
        Assert.Equal(prepared, store.PrepareRawStudyPlan(request));
        Assert.Equal("artifact-capacity", Assert.Throws<ResearchException>(() => store.PrepareRawStudyPlan(request with { MonthlyGbp = 21 })).Code);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "imports"), "*.raw-study.json"));
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

}
