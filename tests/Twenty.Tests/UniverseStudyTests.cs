using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Twenty.Core.Research;
using Twenty.Research;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;

namespace Twenty.Tests;

public sealed class UniverseStudyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-universe-study-" + Guid.NewGuid().ToString("N"));
    private ResearchStore store;
    public UniverseStudyTests() => store = new(root);
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    // False synthetic flags below exercise guards using authored data only.
    private UniverseStudyRequest Request(bool synthetic = true) => new(Guid.NewGuid().ToString("D"), "Authored universe study", synthetic ? "engineering" : "research",
        store.ImportUniverseDataset(synthetic ? UniverseDiagnosticTests.DatasetBytes() : JsonSerializer.SerializeToUtf8Bytes(
            DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes()) with { Manifest = DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes()).Manifest with { Synthetic = false } }, DatasetJson.Options)), "research-universe",
        Enumerable.Range(0, 73).Select(i => new DateTimeOffset(2010, 2, 1, 23, 0, 0, TimeSpan.Zero).AddMonths(i).AddDays(-1)).ToImmutableArray(), 45, 20m, new());

    [Fact]
    public void DeclarationAndEveryTrialAreRetainedBeforeFirstComputation()
    {
        var request = Request(); var study = store.RegisterUniverseStudy(request, "authored-test");
        Assert.Equal("development", study.Status); Assert.Equal(4, study.DevelopmentRuns.Length);
        Assert.Equal(8, store.ListUniverseRuns().Count);
        Assert.All(store.ListUniverseRuns(), r => Assert.Equal("planned", r.Status));
        Assert.All(store.ListUniverseRuns(), r => Assert.NotNull(store.UniverseRequestBytes(r.Id)));
        var plan = store.UniverseStudyPlanJson(study.Id)!;
        Assert.Equal(study.PlanSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan))));
        Assert.Equal(study.Id, store.RegisterUniverseStudy(request, "different-host-commit").Id);
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() =>
            store.RegisterUniverseStudy(request with { Name = "Changed declaration" }, "test")).Code);
    }

    [Fact]
    public void EngineeringStudyFreezesOneChoiceAndSpendsExactlyOneFinalPair()
    {
        var study = store.RegisterUniverseStudy(Request(), "authored-test");
        for (var i = 0; i < 12; i++) _ = store.ProcessNextUniverseStudyRun("authored-test");
        study = store.UniverseStudy(study.Id)!;
        Assert.Equal("candidate-frozen", study.Status); Assert.NotNull(study.SelectedCandidateId);
        var evaluation = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        SharedFinalClaimFixture.AssertCaseAliasConflict(root, id => store.EvaluateUniverseStudy(study.Id, new(id, study.PlanSha256), "authored-test"));
        var final = store.EvaluateUniverseStudy(study.Id, evaluation, "authored-test");
        Assert.NotNull(final.FinalRequestedAt); Assert.Equal(10, store.ListUniverseRuns().Count);
        Assert.Equal(2, store.ListUniverseRuns().Count(r => r.Status == "planned"));
        Assert.Equal(final.FinalBaseRunId, store.EvaluateUniverseStudy(study.Id, evaluation with { RequestId = evaluation.RequestId.ToUpperInvariant() }, "test").FinalBaseRunId);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateUniverseStudy(
            study.Id, evaluation with { RequestId = Guid.NewGuid().ToString("D") }, "test")).Code);
        for (var i = 0; i < 4; i++) _ = store.ProcessNextUniverseStudyRun("authored-test");
        Assert.Equal("engineering-complete", store.UniverseStudy(study.Id)!.Status);
        Assert.Equal(10, store.ListUniverseRuns().Count);
    }

    [Fact]
    public void CancelledDeclarationCannotCreateReplacementTrialsOrFinals()
    {
        var request = Request(); var study = store.RegisterUniverseStudy(request, "test");
        Assert.Equal("cancelled", store.CancelUniverseStudy(study.Id).Status);
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
        Assert.All(store.ListUniverseRuns(), r => Assert.Equal("cancelled", r.Status));
        Assert.Equal(study.Id, store.RegisterUniverseStudy(request, "test").Id);
        Assert.Equal("study-candidate-not-frozen", Assert.Throws<ResearchException>(() => store.EvaluateUniverseStudy(study.Id,
            new(Guid.NewGuid().ToString("D"), study.PlanSha256), "test")).Code);
    }

    [Fact]
    public async Task IdleModelWorkerLeavesTheComputationSlotAvailable()
    {
        var request = Request(); using var worker = new ExperimentWorker(store, NullLogger<ExperimentWorker>.Instance);
        await worker.StartAsync(default);
        await Task.Delay(100); // Let the hosted worker reach its idle backoff.
        try
        {
            var run = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), request.DatasetSha256, request.UniverseId,
                request.Decisions, request.MaximumPriceAgeDays, new(), request.Costs), "test");
            Assert.Equal("succeeded", run.Status);
        }
        finally { await worker.StopAsync(default); }
    }

    private UniverseResearchStudy Freeze(bool synthetic = true)
    {
        var study = store.RegisterUniverseStudy(Request(synthetic), "test");
        for (var i = 0; i < 10; i++) _ = store.ProcessNextUniverseStudyRun("test");
        Assert.Equal("candidate-frozen", store.UniverseStudy(study.Id)!.Status); return store.UniverseStudy(study.Id)!;
    }

    [Fact]
    public void FinalFailureRetainsSpentClaimAndExactPlanAcrossRestart()
    {
        var study = Freeze(); var plan = store.UniverseStudyPlanJson(study.Id);
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        var final = store.EvaluateUniverseStudy(study.Id, request, "test");
        File.Delete(Path.Combine(root, "universe-datasets", study.Plan.DatasetSha256 + ".json"));
        _ = store.ProcessNextUniverseStudyRun("test");
        Assert.Equal("final-failed", store.UniverseStudy(study.Id)!.Status);
        store.Dispose(); store = new(root);
        Assert.Equal(plan, store.UniverseStudyPlanJson(study.Id));
        Assert.Equal(final.FinalBaseRunId, store.EvaluateUniverseStudy(study.Id, request, "test").FinalBaseRunId);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateUniverseStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "test")).Code);
        Assert.Equal(10, store.ListUniverseRuns().Count);
    }

    [Fact]
    public void MissingRequestStopsTheWholeDeclaredFamilyWithoutReplacement()
    {
        var request = Request(); var study = store.RegisterUniverseStudy(request, "test");
        File.AppendAllText(Path.Combine(root, "runs", study.DevelopmentRuns[0].BaseRunId, "universe-request.json"), " ");
        _ = store.ProcessNextUniverseStudyRun("test");
        Assert.Equal("research-failed", store.UniverseStudy(study.Id)!.Status);
        Assert.Equal("universe-request-integrity", store.ListUniverseRuns().Single(r => r.Status == "failed").ErrorCode);
        Assert.Equal(7, store.ListUniverseRuns().Count(r => r.Status == "cancelled"));
        Assert.All(store.ListUniverseRuns().Where(r => r.Status == "cancelled"), r => Assert.Equal("study-family-stopped", r.ErrorCode));
        Assert.Equal(study.Id, store.RegisterUniverseStudy(request, "test").Id);
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
    }

    private void ReplaceEntry(UniverseRunEntry entry)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "research.db")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE universe_runs SET json=$json,status=$status WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", entry.Id); cmd.Parameters.AddWithValue("$status", entry.Status);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, ResearchStore.Json)); cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("grid")]
    public void HashValidReportMustStillMatchItsDeclaredRoleAndGrid(string change)
    {
        var study = store.RegisterUniverseStudy(Request(), "test");
        for (var i = 0; i < 8; i++) Assert.True(store.ProcessNextUniverseStudyRun("test"));
        var entry = store.FindUniverseRun(study.DevelopmentRuns[0].BaseRunId)!; var report = store.UniverseReport(entry.Id)!;
        report = change == "config" ? report with { Config = report.Config with { TopK = 99 } }
            : report with { Equity = report.Equity.RemoveAt(0) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ResearchStore.Json);
        File.WriteAllBytes(Path.Combine(root, "runs", entry.Id, "universe-report.json"), bytes);
        ReplaceEntry(entry with { ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
        var failed = store.UniverseStudy(study.Id)!;
        Assert.Equal("research-failed", failed.Status); Assert.Equal("universe-study-report-contract", failed.ErrorCode);
        Assert.Null(failed.SelectedCandidateId);
    }

    [Fact]
    public void UniverseReservationGuardsOrdinaryTrialsAndModelStudyDeclarations()
    {
        var request = Request(synthetic: false); var study = store.RegisterUniverseStudy(request, "test");
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(
            new(Guid.NewGuid().ToString("D"), request.DatasetSha256, request.UniverseId, request.Decisions, 45, new(), new()), "test")).Code);
        var data = UniverseStudyGuardTests.Monthly(store, 2002);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.RegisterStudy(
            new(Guid.NewGuid().ToString("D"), data.Id, "Authored cross-engine guard", "research", 20m, new()), "test")).Code);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.Enqueue(
            new(Guid.NewGuid().ToString("D"), data.Id, new(HoldoutMonths: 12), new()), "test")).Code);
        Assert.Equal(8, store.ListUniverseRuns().Count); Assert.Empty(store.ListRuns());
        Assert.Equal(study.Id, store.RegisterUniverseStudy(request, "test").Id);
    }

    [Fact]
    public void ModelReservationGuardsUniverseStudyAdmission()
    {
        var request = Request(synthetic: false); var data = UniverseStudyGuardTests.Monthly(store, 2001);
        store.RegisterStudy(new(Guid.NewGuid().ToString("D"), data.Id, "Authored model reservation", "research", 20m, new()), "test");
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(request, "test")).Code);
        Assert.Empty(store.UniverseStudies());
    }

    [Fact]
    public async Task FinalReportsAreDisclosedButCannotBecomeDevelopmentAnchors()
    {
        var study = Freeze(); var final = store.EvaluateUniverseStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "test");
        for (var i = 0; i < 3; i++) _ = store.ProcessNextUniverseStudyRun("test");
        var service = new DiagnosticStore(store);
        var refused = await service.Create(new(Guid.NewGuid().ToString("D"), final.FinalBaseRunId!));
        Assert.Equal("failed", refused.Status); Assert.Equal("diagnostic-development-run-required", refused.ErrorCode);
        var result = await service.Create(new(Guid.NewGuid().ToString("D"), study.DevelopmentRuns[0].BaseRunId));
        Assert.Equal("succeeded", result.Status); Assert.Equal(10, result.Trials.Length);
        Assert.Equal(2, result.Trials.Count(t => t.Reason == "reserved-final-evaluation"));
    }

    [Fact]
    public void PriorSuccessfulOrFailedUniverseWorkCannotBecomeAnUnseenFinal()
    {
        var request = Request(synthetic: false);
        _ = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), request.DatasetSha256, request.UniverseId,
            request.Decisions, 45, new(), new()), "test");
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(request, "test")).Code);
        Assert.Empty(store.UniverseStudies());
    }

    [Fact]
    public void PlannedWorkResumesButAnInterruptedAttemptStopsTheFamily()
    {
        var study = store.RegisterUniverseStudy(Request(), "test"); var plan = store.UniverseStudyPlanJson(study.Id);
        Assert.True(store.ProcessNextUniverseStudyRun("test"));
        store.Dispose(); store = new(root);
        Assert.True(store.ProcessNextUniverseStudyRun("test"));
        Assert.Equal(2, store.ListUniverseRuns().Count(r => r.Status == "succeeded"));
        var running = store.ListUniverseRuns().First(r => r.Status == "planned");
        ReplaceEntry(running with { Status = "started", StartedAt = DateTimeOffset.UtcNow });
        store.Dispose(); store = new(root);
        Assert.Equal("interrupted", store.FindUniverseRun(running.Id)!.Status);
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
        Assert.Equal("research-failed", store.UniverseStudy(study.Id)!.Status);
        Assert.Equal(2, store.ListUniverseRuns().Count(r => r.Status == "succeeded"));
        Assert.Equal(8, store.ListUniverseRuns().Count); Assert.Equal(plan, store.UniverseStudyPlanJson(study.Id));
    }

    [Fact]
    public void ACodeChangeStopsUnstartedWorkWithoutRewritingItsPlan()
    {
        var study = store.RegisterUniverseStudy(Request(), "test");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "research.db")};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM universe_studies WHERE id=$id"; cmd.Parameters.AddWithValue("$id", study.Id);
            var node = System.Text.Json.Nodes.JsonNode.Parse((string)cmd.ExecuteScalar()!)!;
            node["plan"]!["codeSha256"] = new string('0', 64);
            node["planSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node["plan"]!.ToJsonString(ResearchStore.Json))));
            var json = node.ToJsonString(ResearchStore.Json);
            cmd.CommandText = "UPDATE universe_studies SET json=$json,sha256=$sha WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))); cmd.ExecuteNonQuery();
        }
        var plan = store.UniverseStudyPlanJson(study.Id);
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
        Assert.Equal("study-code-changed", store.UniverseStudy(study.Id)!.ErrorCode);
        Assert.All(store.ListUniverseRuns(), r => Assert.Equal("cancelled", r.Status));
        Assert.Equal(plan, store.UniverseStudyPlanJson(study.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CapacityReservesTheFinalPairAcrossBothRegisters(bool legacyMetadata)
    {
        var request = Request(); var template = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), request.DatasetSha256,
            request.UniverseId, request.Decisions, 45, new(), new()), "test");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "research.db")};Pooling=False"))
        {
            db.Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO universe_runs VALUES($id,$request,$dataset,$json,'failed')";
            cmd.Parameters.AddWithValue("$id", ""); cmd.Parameters.AddWithValue("$request", ""); cmd.Parameters.AddWithValue("$dataset", template.DatasetSha256); cmd.Parameters.AddWithValue("$json", "");
            for (var i = 1; i < 990; i++)
            {
                var row = template with { Id = Guid.NewGuid().ToString("N"), Status = "failed", ErrorCode = "authored-capacity-fixture",
                    StartedAt = legacyMetadata ? null : template.StartedAt, WorkerCommit = legacyMetadata ? null : new string('b', 40),
                    CodeCommit = legacyMetadata ? template.CodeCommit : new string('a', 40) };
                cmd.Parameters["$id"].Value = row.Id; cmd.Parameters["$request"].Value = Guid.NewGuid().ToString("D"); cmd.Parameters["$json"].Value = JsonSerializer.Serialize(row, ResearchStore.Json); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (!legacyMetadata)
        {
            Assert.Equal("study-payload-capacity", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(request, "test")).Code);
            Assert.Empty(store.UniverseStudies()); Assert.Equal(990, store.ListUniverseRuns().Count);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "runs"))); return;
        }
        var study = store.RegisterUniverseStudy(request, "test"); Assert.Equal(998, store.ListUniverseRuns().Count);
        Assert.Equal(990, study.PriorUniverseTrials.Length);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"),
            request.DatasetSha256, request.UniverseId, request.Decisions, 45, new(), new()), "test")).Code);
        var model = store.Import(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json")), "Authored shared-budget fixture");
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() => store.Enqueue(
            new(Guid.NewGuid().ToString("D"), model.Id, new(), new()), "test")).Code);
        for (var i = 0; i < 10; i++) _ = store.ProcessNextUniverseStudyRun("test");
        study = store.UniverseStudy(study.Id)!; Assert.Equal("candidate-frozen", study.Status);
        _ = store.EvaluateUniverseStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "test");
        Assert.Equal(1000, store.ListUniverseRuns().Count);
    }

    [Fact]
    public void CanonicalOffsetsAndMalformedGridsPreserveAdmissionSemantics()
    {
        var request = Request(); var study = store.RegisterUniverseStudy(request, "test");
        Assert.Equal(study.Id, store.RegisterUniverseStudy(request with { Decisions = request.Decisions.Select(d => d.ToOffset(TimeSpan.FromHours(-4))).ToImmutableArray() }, "test").Id);
        Assert.Equal("universe-panel-decisions", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(
            request with { RequestId = Guid.NewGuid().ToString("D"), Decisions = default }, "test")).Code);
        Assert.Equal(8, store.ListUniverseRuns().Count);
    }

    [Fact]
    public void SubdayOverlapWithASpentUniverseFinalCannotBecomeUnseen()
    {
        // Authored data with a false synthetic flag exercises reservation precision only.
        var study = Freeze(synthetic: false);
        _ = store.EvaluateUniverseStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "test");
        for (var i = 0; i < 3; i++) _ = store.ProcessNextUniverseStudyRun("test");
        Assert.Contains(store.UniverseStudy(study.Id)!.Status, new[] { "research-complete", "research-rejected" });
        var request = Request(synthetic: false);
        var shifted = request with { Decisions = request.Decisions.Select(d => d.AddYears(2).AddHours(-23)).ToImmutableArray() };
        Assert.Equal(study.Plan.ReservedEnd.Date, shifted.Decisions[48].Date);
        Assert.True(shifted.Decisions[48] < study.Plan.ReservedEnd);
        var error = Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(shifted, "test"));
        Assert.Contains(error.Code, new[] { "study-known-reserved-exposure", "study-holdout-spent" });
        var adjacent = request with { RequestId = Guid.NewGuid().ToString("D"), Decisions = request.Decisions.Select(d => d.AddYears(2)).ToImmutableArray() };
        Assert.Equal(study.Plan.ReservedEnd, adjacent.Decisions[48]);
        Assert.Equal("development", store.RegisterUniverseStudy(adjacent, "test").Status);
    }

    [Fact]
    public void CancellingAFinalPairRetainsItsOriginalSpentRequest()
    {
        var study = Freeze(); var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        var final = store.EvaluateUniverseStudy(study.Id, request, "test");
        Assert.Equal("cancelled", store.CancelUniverseStudy(study.Id).Status);
        Assert.False(store.ProcessNextUniverseStudyRun("test"));
        Assert.Equal(2, store.ListUniverseRuns().Count(r => r.Status == "cancelled"));
        Assert.Equal(final.FinalBaseRunId, store.EvaluateUniverseStudy(study.Id, request, "test").FinalBaseRunId);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateUniverseStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "test")).Code);
    }

    [Fact]
    public void StoredExposureCannotBeShortenedIndependentlyOfTheExactRequest()
    {
        var study = store.RegisterUniverseStudy(Request(), "test");
        var entry = store.FindUniverseRun(study.DevelopmentRuns[0].BaseRunId)!;
        ReplaceEntry(entry with { InformationEndAt = entry.InformationEndAt!.Value.AddHours(-23) });
        Assert.True(store.ProcessNextUniverseStudyRun("test"));
        var refused = store.FindUniverseRun(entry.Id)!;
        Assert.Equal("failed", refused.Status); Assert.Equal("universe-study-request-contract", refused.ErrorCode);
        Assert.Null(refused.StartedAt); Assert.Equal("research-failed", store.UniverseStudy(study.Id)!.Status);
    }
}
