using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class UniverseDiagnosticTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-universe-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly ResearchStore store;
    public UniverseDiagnosticTests() => store = new(root);
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private static DateTimeOffset Decision(int i) => new DateTimeOffset(2010, 1, 31, 23, 0, 0, TimeSpan.Zero).AddDays(1)
        .AddMonths(i).AddDays(-1);
    private static Evidence Evidence(DateTimeOffset at) => new("fixture-source", at, at, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);

    private static byte[] DatasetBytes()
    {
        var original = UniverseBacktestTests.Scenario(); var start = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var observations = new List<Observation>();
        foreach (var id in new[] { "aaa", "bbb" })
        {
            observations.Add(original.Observations.OfType<Listing>().Single(o => o.Id == "listing-" + id) with
                { Evidence = Evidence(start), EffectiveFrom = start });
            observations.Add(original.Observations.OfType<Membership>().Single(o => o.Id == "member-" + id) with
                { Evidence = Evidence(start), EffectiveFrom = start });
            var close = 100m;
            for (var month = 0; month < 73; month++)
            {
                var at = Decision(month).AddHours(-2);
                observations.Add(new PriceBar($"{id}-price-{month}", Evidence(at.AddMinutes(1)), id,
                    DateOnly.FromDateTime(at.UtcDateTime), at, new("USD", QuoteUnit.Major), PriceBasis.TotalReturnAdjusted,
                    close, close, close, close, 100m));
                close *= id == "aaa" ? month % 7 == 0 ? 0.97m : 1.02m : month % 5 == 0 ? 1.04m : 0.995m;
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(original with { Observations = observations.ToImmutableArray() }, DatasetJson.Options);
    }

    private UniverseRunEntry Universe(int topK = 1, decimal monthly = 20m, int age = 45)
    {
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var result = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), sha, "research-universe",
            Enumerable.Range(0, 73).Select(Decision).ToImmutableArray(), age, new(MonthlyGbp: monthly, TopK: topK), new()), "authored-test");
        Assert.Equal("succeeded", result.Status); return result;
    }

    private RunEntry Model()
    {
        var dataset = store.Import(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json")), "Authored fixture");
        var run = store.Enqueue(new(Guid.NewGuid().ToString("D"), dataset.Id, new(), new()), "authored-test");
        Assert.Equal(run.Id, store.Claim()!.Id); store.Complete(run.Id, Backtest.Run(store.Panel(dataset.Id), run.Config, run.Costs));
        return store.Find(run.Id)!;
    }

    [Fact]
    public async Task ModelDiagnosticDisclosesUniverseTrialsRatherThanErasingAnotherRegister()
    {
        var model = Model(); var universe = Universe();
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), model.Id));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(2, result.Trials.Length);
        var disclosed = Assert.Single(result.Trials.Where(t => t.RunId == universe.Id));
        Assert.False(disclosed.Included); Assert.Equal("different-research-engine", disclosed.Reason);
    }

    [Fact]
    public async Task UniverseFamilyUsesRecordedMomentumAgainstActualEqualWeightAndCash()
    {
        var anchor = Universe(); var challenger = Universe(topK: 2); var otherAccount = Universe(monthly: 21m);
        var otherPricePolicy = Universe(age: 46); var model = Model();
        var service = new DiagnosticStore(store); var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), anchor.Id);
        var result = await service.Create(request);
        Assert.Equal("succeeded", result.Status); Assert.NotNull(result.Report); Assert.True(result.Synthetic);
        Assert.Equal(5, result.Trials.Length); Assert.Equal(2, result.Report.Candidates); Assert.Equal(60, result.Report.Months);
        Assert.Equal(4, result.Report.Comparisons);
        Assert.True(result.Trials.Single(t => t.RunId == challenger.Id).Included);
        Assert.Equal("different-account-assumptions", result.Trials.Single(t => t.RunId == otherAccount.Id).Reason);
        Assert.Equal("different-price-policy", result.Trials.Single(t => t.RunId == otherPricePolicy.Id).Reason);
        Assert.Equal("different-research-engine", result.Trials.Single(t => t.RunId == model.Id).Reason);
        Assert.Equal(new[] { "cash", "equal_weight" }, result.Report.Sensitivities[0].Intervals.Select(i => i.Comparator).Distinct().Order().ToArray());
        foreach (var interval in result.Report.Sensitivities[0].Intervals)
        {
            var source = store.UniverseReport(interval.RunId)!;
            var momentum = source.Equity.Where(e => e.Strategy == "momentum").Select(e => e.State.PeriodReturn);
            var comparator = source.Equity.Where(e => e.Strategy == interval.Comparator).Select(e => e.State.PeriodReturn);
            var expected = 12m * momentum.Zip(comparator, (candidate, baseline) => candidate - baseline).Average();
            Assert.Equal((double)expected, interval.AnnualisedMeanActiveReturn, 12);
        }
        Assert.Equal(JsonSerializer.Serialize(result, ResearchStore.Json), JsonSerializer.Serialize(await service.Create(request), ResearchStore.Json));
        Assert.Contains(result.Report.Limitations, s => s.Contains("FX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UniverseDiagnosticRefusesTamperedExactRequestsAndPreservesTheFailure()
    {
        var anchor = Universe(); _ = Universe(topK: 2); _ = Model();
        File.AppendAllText(Path.Combine(root, "runs", anchor.Id, "universe-request.json"), " ");
        var service = new DiagnosticStore(store); var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), anchor.Id);
        var result = await service.Create(request);
        Assert.Equal("failed", result.Status); Assert.Equal("universe-request-integrity", result.ErrorCode);
        Assert.Null(result.Report); Assert.Equal(result.Id, (await service.Create(request)).Id);
        Assert.Equal(3, result.Trials.Length); Assert.Equal("changing-universe", result.FamilyEngine);
        Assert.NotNull(result.RegistrySnapshotAt); Assert.NotNull(result.ResearchSha256);
        Assert.All(result.Trials, t => Assert.False(t.Included));
    }

    private void ReplaceEntry(UniverseRunEntry entry)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "research.db")};Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "UPDATE universe_runs SET json=$json,status=$status WHERE id=$id";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, ResearchStore.Json)); command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$status", entry.Status);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Theory]
    [InlineData("config")]
    [InlineData("trailing-period")]
    public async Task HashValidReportsMustStillMatchTheRetainedRequest(string change)
    {
        var anchor = Universe(); var report = store.UniverseReport(anchor.Id)!;
        report = change == "config" ? report with { Config = report.Config with { TopK = 2 } }
            : report with { Equity = report.Equity.Where(e => e.PeriodEnd != report.Equity[^1].PeriodEnd).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ResearchStore.Json);
        File.WriteAllBytes(Path.Combine(root, "runs", anchor.Id, "universe-report.json"), bytes);
        ReplaceEntry(anchor with { ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), anchor.Id));
        Assert.Equal("failed", result.Status); Assert.Equal("diagnostic-report-contract", result.ErrorCode);
        Assert.Single(result.Trials); Assert.Null(result.Report);
    }

    [Fact]
    public async Task LegacyUniverseRequestEvidenceIsNotReconstructedForAnalysis()
    {
        var anchor = Universe(); ReplaceEntry(anchor with { RequestArtifactSha256 = null });
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), anchor.Id));
        Assert.Equal("failed", result.Status); Assert.Equal("universe-request-unavailable", result.ErrorCode);
        Assert.Single(result.Trials); Assert.Equal("changing-universe", result.FamilyEngine);
    }

    [Fact]
    public async Task FailedAndOtherCodeUniverseAttemptsRemainDisclosed()
    {
        var anchor = Universe(); var failed = Universe(topK: 2); var differentCode = Universe(topK: 3);
        ReplaceEntry(failed with { Status = "failed", ErrorCode = "authored-refusal" });
        ReplaceEntry(differentCode with { ResearchSha256 = new string('f', 64) });
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), anchor.Id));
        Assert.Equal("succeeded", result.Status); Assert.Equal(3, result.Trials.Length);
        Assert.Equal("incomplete-or-failed-trial", result.Trials.Single(t => t.RunId == failed.Id).Reason);
        Assert.Equal("different-engine-artifact", result.Trials.Single(t => t.RunId == differentCode.Id).Reason);
        Assert.Equal(1, result.Report!.Candidates);
    }

    [Fact]
    public void AddedMetadataDoesNotChangeLegacyDiagnosticJsonShapes()
    {
        var trial = new DiagnosticTrial("old", "dataset", "succeeded", "config", "report", "engine", true, "included");
        var attempt = new DiagnosticAttempt("old", "request", "anchor", "succeeded", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, null, "commit", "diagnostic-engine", "dataset", true, [trial], null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(attempt, ResearchStore.Json);
        using var document = JsonDocument.Parse(bytes);
        Assert.False(document.RootElement.TryGetProperty("familyEngine", out _));
        Assert.False(document.RootElement.TryGetProperty("researchSha256", out _));
        Assert.False(document.RootElement.GetProperty("trials")[0].TryGetProperty("researchEngine", out _));
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<DiagnosticAttempt>(bytes, ResearchStore.Json), ResearchStore.Json));
    }
}
