using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    private RawResearchStudy CompleteRawDiagnosticFamily()
    {
        var study = RegisterFixture(RawStudyPendingClaimTests.Fixture());
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        for (var i = 0; i < 8; i++) Assert.True(store.ProcessNextRawStudyRun("authored"));
        return store.RawStudies().Single(s => s.Id == study.Id);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("stress")]
    public async Task RawDiagnosticUsesAllFourDeclaredCandidatesAndTheirActualComparators(string cost)
    {
        var study = CompleteRawDiagnosticFamily(); var other = Register(); store.CancelRawStudy(other.Id);
        var rows = store.ListRawTrials(); var anchor = rows.Single(r => r.StudyId == study.Id && r.TrialId == "momentum:1:" + cost);
        var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), anchor.Id); var service = new DiagnosticStore(store);
        var result = await service.Create(request);
        Assert.Equal("succeeded", result.Status); Assert.Equal("raw-universe", result.FamilyEngine);
        Assert.Equal(16, result.Trials.Length); Assert.Equal(4, result.Trials.Count(t => t.Included));
        Assert.All(result.Trials.Where(t => t.Included), t => Assert.Equal(cost, rows.Single(r => r.Id == t.RunId).CostScenario));
        Assert.Equal(4, result.Trials.Count(t => t.Reason == "different-cost-scenario"));
        Assert.Equal(8, result.Trials.Count(t => t.Reason == "different-registered-family"));
        foreach (var trial in result.Trials) Assert.Equal(rows.Single(r => r.Id == trial.RunId).ReportSha256, trial.ReportSha256);
        Assert.Equal("stationary-raw-active-mean-v1", result.Report!.MethodVersion);
        Assert.Equal(4, result.Report.Candidates); Assert.Equal(8, result.Report.Comparisons); Assert.Equal(35, result.Report.Months);
        Assert.All(result.Report.Sensitivities, s => Assert.Equal(new[] { "cash", "equal_weight" }, s.Intervals.Select(i => i.Comparator).Distinct().Order()));
        Assert.Contains(result.Report.Limitations, l => l.Contains("expected equity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Report.Limitations, l => l.Contains("no FX conversion", StringComparison.OrdinalIgnoreCase));
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(result, ResearchStore.Json));
        Assert.Equal(study.PlanSha256, json.RootElement.GetProperty("rawPlanSha256").GetString());
        Assert.Equal(cost, json.RootElement.GetProperty("rawCostScenario").GetString());
        Assert.Equal(RawStudyDevelopmentRun.Version, json.RootElement.GetProperty("rawReportVersion").GetString());
        foreach (var interval in result.Report.Sensitivities[0].Intervals)
        {
            var report = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(store.RawTrialReportBytes(interval.RunId), RawReplayJson.Options)!;
            var candidate = report.Strategies.Single(s => s.Strategy == "momentum");
            var comparator = report.Strategies.Single(s => s.Strategy == interval.Comparator);
            var mean = 12 * candidate.Months.Zip(comparator.Months, (a, b) => (double)a.PeriodReturn - (double)b.PeriodReturn).Average();
            Assert.Equal(mean, interval.AnnualisedMeanActiveReturn, 12);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, ResearchStore.Json);
        store.Dispose(); store = new(root);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(await new DiagnosticStore(store).Create(request), ResearchStore.Json));
        Assert.Equal(0, RawFinalClaims()); Assert.Equal(16, store.ListRawTrials().Count);
    }

    [Fact]
    public async Task RawDiagnosticRefusesAnIncompleteFamilyWithoutTrimmingItsSnapshot()
    {
        var study = Register(); store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        Assert.True(store.ProcessNextRawStudyRun("authored")); var anchor = store.ListRawTrials().Single(r => r.Status == "succeeded");
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), anchor.Id));
        Assert.Equal("failed", result.Status); Assert.Equal("raw-diagnostic-family-incomplete", result.ErrorCode);
        Assert.Equal(8, result.Trials.Length); Assert.Null(result.Report); Assert.Equal(0, RawFinalClaims());
    }

    [Fact]
    public async Task RawDiagnosticRejectsFinalAnchorBeforeReadingItsDamagedFinalReport()
    {
        var study = FreezeRawCandidate(); study = store.EvaluateRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "authored");
        Assert.True(store.ProcessNextRawStudyRun("authored")); Assert.True(store.ProcessNextRawStudyRun("authored"));
        File.WriteAllText(Path.Combine(root, "runs", study.FinalBaseTrialId!, "raw-study-report.json"), "deliberately damaged final artifact");
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), study.FinalBaseTrialId!));
        Assert.Equal("failed", result.Status); Assert.Equal("diagnostic-development-run-required", result.ErrorCode);
        Assert.Equal(10, result.Trials.Length); Assert.Null(result.Report); Assert.Equal(1, RawFinalClaims());
    }
    [Theory]
    [InlineData("request", "raw-trial-request-integrity")]
    [InlineData("report-grid", "diagnostic-report-contract")]
    [InlineData("report-era", "raw-diagnostic-report-era")]
    [InlineData("receipt-hash", "diagnostic-report-contract")]
    public async Task RawDiagnosticRetainsItsCompleteSnapshotWhenAnOriginalOrReportFails(string change, string code)
    {
        var study = CompleteRawDiagnosticFamily();
        var rows = store.ListRawTrials(); var anchor = rows.Single(r => r.TrialId == "momentum:1:base");
        var target = rows.First(r => r.CostScenario == "base" && r.Id != anchor.Id);
        if (change == "request") File.WriteAllText(Path.Combine(root, "runs", target.Id, "raw-study-request.json"), "{}");
        else
        {
            var path = Path.Combine(root, "runs", target.Id, "raw-study-report.json");
            var report = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(File.ReadAllBytes(path), RawReplayJson.Options)!;
            var strategy = report.Strategies[0];
            report = change switch
            {
                "report-era" => report with { Version = "raw-study-development-v3" },
                "receipt-hash" => report with { Strategies = report.Strategies.SetItem(0, strategy with
                    { Receipt = strategy.Receipt with { ResultSha256 = new string('f', 64) } }) },
                _ => report with { Strategies = report.Strategies.SetItem(0, strategy with
                    { Months = strategy.Months.SetItem(0, strategy.Months[0] with { EndAt = strategy.Months[0].EndAt.AddDays(1) }) }) }
            };
            var bytes = RawStudyDevelopmentRun.EncodeReport(report); File.WriteAllBytes(path, bytes);
            // Authored coherent hash/row rewrite probes the semantic gate, not an application write API.
            using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE raw_trials SET json=$json WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", target.Id); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(target with
                { ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, RawReplayJson.Options)); cmd.ExecuteNonQuery();
        }
        var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), anchor.Id); var service = new DiagnosticStore(store);
        var result = await service.Create(request);
        Assert.Equal("failed", result.Status); Assert.Equal(code, result.ErrorCode); Assert.Equal(8, result.Trials.Length);
        Assert.Equal(4, result.Trials.Count(t => t.Included)); Assert.Null(result.Report);
        Assert.Equal(JsonSerializer.Serialize(result, ResearchStore.Json), JsonSerializer.Serialize(await service.Create(request), ResearchStore.Json));
        Assert.Equal(0, RawFinalClaims()); Assert.Equal(8, store.ListRawTrials().Count);
    }

    [Fact]
    public async Task RawDiagnosticConsumesPublishedDevelopmentEvidenceWithAllMarketOriginalsUnavailable()
    {
        var study = CompleteRawDiagnosticFamily(); var anchor = store.ListRawTrials().Single(r => r.TrialId == "momentum:1:base");
        Directory.Move(Path.Combine(root, "raw-study-inputs"), Path.Combine(root, "unavailable-raw-study-inputs"));
        Directory.Move(Path.Combine(root, "universe-datasets"), Path.Combine(root, "unavailable-universe-datasets"));
        var result = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), anchor.Id));
        Assert.Equal("succeeded", result.Status); Assert.Equal(4, result.Report!.Candidates); Assert.Equal(0, RawFinalClaims());
    }

}
