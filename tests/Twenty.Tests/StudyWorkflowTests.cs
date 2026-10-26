using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Twenty.Research;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class StudyWorkflowTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "twenty-study-" + Guid.NewGuid().ToString("N"));
        public ResearchStore Store { get; private set; }
        public DatasetEntry Dataset { get; }
        public bool Synthetic { get; }
        public Fixture(bool synthetic = true, bool finalCollapse = false)
        {
            Synthetic = synthetic; Store = new(Root); var (csv, metadata) = Data(synthetic, finalCollapse); Dataset = Store.Import(csv, metadata, "Authored study unit fixture");
        }
        public ResearchStudy Register() => Store.RegisterStudy(new(Guid.NewGuid().ToString("D"), Dataset.Id, "Unit study", Synthetic ? "engineering" : "research", 20, new()), "unit");
        public void Restart() { Store.Dispose(); Store = new(Root); }
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
    private static (byte[] Csv, byte[] Metadata) Data(bool synthetic, bool collapse = false, string source = "Authored deterministic unit fixture, NOT historical investment evidence")
    {
        var csv = new StringBuilder("date,symbol,total_return_index_gbp\n"); var a = 100m; var b = 100m; var c = 100m;
        for (var t = 0; t < 180; t++)
        {
            if (t > 0) { a *= collapse && t >= 156 ? .8m : 1.08m; b *= 1.005m; c *= .999m; }
            var date = new DateOnly(2000, 1, 1).AddMonths(t).ToString("yyyy-MM-dd");
            foreach (var (name, value) in new[] { ("A", a), ("B", b), ("C", c), ("D", 100m), ("BENCHMARK", 100m) })
                csv.Append(date).Append(',').Append(name).Append(',').Append(value.ToString("G29", CultureInfo.InvariantCulture)).Append('\n');
        }
        return (Encoding.UTF8.GetBytes(csv.ToString()), JsonSerializer.SerializeToUtf8Bytes(new { source, synthetic, benchmark = "BENCHMARK", currency = "GBP", frequency = "monthly" }));
    }
    private static void Drain(ResearchStore store)
    {
        for (var i = 0; i < 100; i++)
        {
            var run = store.Claim(); if (run is null) return;
            store.Complete(run.Id, Backtest.Run(store.Panel(run.DatasetId), run.Config, run.Costs));
        }
        throw new Exception("Study did not stop at its finite declared budget.");
    }
    [Fact]
    public void RegistrationIsAtomicIdempotentAndDeclaresTheWholeFamilyBeforeWork()
    {
        using var f = new Fixture(); var request = new StudyRequest(Guid.NewGuid().ToString("D"), f.Dataset.Id, "Declared test", "engineering", 20, new());
        var study = f.Store.RegisterStudy(request, "unit");
        Assert.Equal(12, study.Plan.Candidates.Length); Assert.Equal(24, f.Store.ListRuns().Count);
        Assert.All(f.Store.ListRuns(), r => { Assert.Equal("planned", r.Status); Assert.Null(r.StartedAt); Assert.Equal(study.Id, r.StudyId); Assert.Equal(study.Plan.DevelopmentStart, r.Config.DevelopmentStart); });
        Assert.Equal(study.Id, f.Store.RegisterStudy(request, "new-commit-does-not-change-a-retry").Id);
        Assert.Equal(24, f.Store.ListRuns().Count); Assert.Single(f.Store.Studies());
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => f.Store.RegisterStudy(request with { MonthlyGbp = 30 }, "unit")).Code);
        Assert.Equal(study.PlanSha256, Hash(f.Store.StudyPlanJson(study.Id)!));
        Assert.Equal(ResearchIdentity.Scheme, study.Plan.IdentityScheme); Assert.Equal(ResearchIdentity.Current.BuildSdk, study.Plan.BuildSdk);
        Assert.Equal(study.Plan.DevelopmentEnd.AddMonths(1), study.Plan.ReservedStart);
        Assert.Equal(study.Plan.BaseCosts.MinimumOrderGbp, study.Plan.StressCosts.MinimumOrderGbp);
        Assert.Equal(study.Plan.BaseCosts.BuyRate * 2, study.Plan.StressCosts.BuyRate);
    }
    [Fact]
    public void SelectionUsesIdenticalWindowsAndTheFinalPairIsSpentBeforeItStarts()
    {
        using var f = new Fixture(); var study = f.Register(); Drain(f.Store); study = f.Store.Study(study.Id)!;
        Assert.Equal("candidate-frozen", study.Status); Assert.Equal(12, study.Scores.Length); Assert.NotNull(study.SelectionDiagnostics);
        Assert.Equal(24, study.SelectionDiagnostics.Candidates); Assert.Equal(StudyProtocol.Choose(study.Scores).CandidateId, study.SelectedCandidateId);
        var reports = f.Store.ListRuns().Select(r => f.Store.Report(r.Id)!).ToArray();
        Assert.Single(reports.Select(r => r.EvaluationStart).Distinct()); Assert.Single(reports.Select(r => r.EvaluationEnd).Distinct());
        Assert.Single(reports.SelectMany(r => r.Summaries).Select(s => s.ContributionsGbp).Distinct());
        Assert.All(reports.SelectMany(r => r.Folds), fold => Assert.True(fold.LastLabelEnd < fold.DecisionDate));
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        SharedFinalClaimFixture.AssertCaseAliasConflict(f.Root, id => f.Store.EvaluateStudy(study.Id, new(id, study.PlanSha256), "unit"));
        study = f.Store.EvaluateStudy(study.Id, request, "unit");
        Assert.Equal("final-evaluation", study.Status); Assert.NotNull(study.FinalRequestedAt);
        Assert.All(f.Store.ListRuns().Where(r => r.Config.EvaluateHoldout), r => { Assert.Equal("planned", r.Status); Assert.Null(r.StartedAt); Assert.Equal(study.SelectedCandidateId, r.StudyCandidateId); });
        Assert.Equal(2, f.Store.ListRuns().Count(r => r.Config.EvaluateHoldout));
        Assert.Equal(study.FinalBaseRunId, f.Store.EvaluateStudy(study.Id, request with { RequestId = request.RequestId.ToUpperInvariant() }, "unit").FinalBaseRunId);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => f.Store.EvaluateStudy(study.Id, request with { RequestId = Guid.NewGuid().ToString("D") }, "unit")).Code);
        Drain(f.Store); study = f.Store.Study(study.Id)!;
        Assert.Equal("engineering-complete", study.Status); Assert.NotNull(study.FinalBaseReportSha256);
        var final = f.Store.Find(study.FinalBaseRunId!)!; Assert.Equal("reserved_holdout", f.Store.Report(final.Id)!.Scope);
        Assert.Contains("reserved-evaluation", f.Store.Report(final.Id)!.Forward.Method);
        f.Store.AssertStudyExecutionRun(final, false);
        Assert.Equal("execution-study-selection", Assert.Throws<ResearchException>(() => f.Store.AssertStudyExecutionRun(final, true)).Code);
        Assert.Equal("execution-study-selection", Assert.Throws<ResearchException>(() => f.Store.AssertStudyExecutionRun(f.Store.Find(study.FinalStressRunId!)!, false)).Code);
    }
    [Fact]
    public void NonStudyRunCannotApproveLiveExecutionButMayRehearseInDemo()
    {
        using var f = new Fixture();
        var ordinary = f.Store.Enqueue(new(Guid.NewGuid().ToString("D"), f.Dataset.Id, new(), new()), "unit");
        Assert.Null(ordinary.StudyId);
        f.Store.AssertStudyExecutionRun(ordinary, false);
        Assert.Equal("execution-study-required", Assert.Throws<ResearchException>(() => f.Store.AssertStudyExecutionRun(ordinary, true)).Code);
    }
    [Fact]
    public void ChangedReservedPricesCannotChangeTheDevelopmentChoice()
    {
        using var before = new Fixture(); using var after = new Fixture(finalCollapse: true);
        var a = before.Register(); var b = after.Register(); Drain(before.Store); Drain(after.Store);
        a = before.Store.Study(a.Id)!; b = after.Store.Study(b.Id)!;
        Assert.Equal(a.SelectedCandidateId, b.SelectedCandidateId);
        Assert.Equal(a.Scores.Select(s => (s.CandidateId, s.BaseEndingGbp, s.StressEndingGbp, s.WorstMarginGbp)), b.Scores.Select(s => (s.CandidateId, s.BaseEndingGbp, s.StressEndingGbp, s.WorstMarginGbp)));
    }
    [Fact]
    public void AFailedOrCancelledCandidateCannotDisappearFromSelection()
    {
        using var f = new Fixture(); var study = f.Register(); f.Store.Cancel(study.DevelopmentRuns[0].StressRunId);
        Assert.Null(f.Store.Claim()); study = f.Store.Study(study.Id)!;
        Assert.Equal("research-failed", study.Status); Assert.Null(study.SelectedCandidateId); Assert.Empty(study.Scores);
        Assert.All(f.Store.ListRuns(), r => Assert.Equal("cancelled", r.Status));
        Assert.Equal("study-candidate-not-frozen", Assert.Throws<ResearchException>(() => f.Store.EvaluateStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "unit")).Code);
    }
    [Fact]
    public void GenuineReservationsCoverOrdinaryRunsAliasesAndPreviouslyRequestedExposure()
    {
        // The false provenance flag exercises policy contracts only; these are authored unit data, never historical evidence.
        using var f = new Fixture(synthetic: false); var alias = Data(false, source: "Another metadata identity for the same authored unit series");
        var data = f.Store.Import(alias.Csv, alias.Metadata, "Unit alias");
        var exposed = f.Store.Enqueue(new(Guid.NewGuid().ToString("D"), data.Id, new(HoldoutMonths: 12), new()), "unit");
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => f.Register()).Code);
        Assert.Equal(exposed.Id, f.Store.Claim()!.Id);
        f.Store.Save(f.Store.Find(exposed.Id)! with { Status = "failed", ErrorCode = "authored-mid-run-failure" }, "unit-failure");
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => f.Register()).Code);
        using var clean = new Fixture(synthetic: false); var study = clean.Register();
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => clean.Store.Enqueue(new(Guid.NewGuid().ToString("D"), clean.Dataset.Id, new(HoldoutMonths: 12), new()), "unit")).Code);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => clean.Register()).Code);
        Assert.Equal("holdout-gate-closed", Assert.Throws<ResearchException>(() => clean.Store.Enqueue(new(Guid.NewGuid().ToString("D"), clean.Dataset.Id, new(EvaluateHoldout: true), new()), "unit")).Code);
        Assert.Equal("development", clean.Store.Study(study.Id)!.Status);
    }
    [Fact]
    public void FinalRejectionRetainsTheFrozenChoiceAndCannotTryARunnerUp()
    {
        using var f = new Fixture(synthetic: false, finalCollapse: true); var study = f.Register(); Drain(f.Store); study = f.Store.Study(study.Id)!;
        Assert.Equal("candidate-frozen", study.Status); var candidate = study.SelectedCandidateId;
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        f.Store.EvaluateStudy(study.Id, request, "unit"); Drain(f.Store); study = f.Store.Study(study.Id)!;
        Assert.Equal("rejected", study.Status); Assert.False(study.FinalPassed); Assert.Equal(candidate, study.SelectedCandidateId);
        Assert.Equal(2, f.Store.ListRuns().Count(r => r.Config.EvaluateHoldout)); Assert.True(study.FinalWorstMarginGbp < 0);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => f.Store.EvaluateStudy(study.Id, request with { RequestId = Guid.NewGuid().ToString("D") }, "unit")).Code);
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => f.Register()).Code);
        Assert.Equal("execution-study-selection", Assert.Throws<ResearchException>(() => f.Store.AssertStudyExecutionRun(f.Store.Find(study.FinalBaseRunId!)!, true)).Code);
    }
    private static DatasetEntry ShortPanel(Fixture f)
    {
        var data = Data(false);
        var lines = Encoding.UTF8.GetString(data.Csv).Split('\n').Where(line => line.StartsWith("date,", StringComparison.Ordinal) ||
            line.Length > 0 && DateOnly.ParseExact(line.Split(',')[0], "yyyy-MM-dd") <= new DateOnly(2010, 12, 1));
        return f.Store.Import(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"), data.Metadata, "Short authored unit panel");
    }
    [Fact]
    public void LaterEvaluationDatesCannotHideExpandingTrainingExposure()
    {
        using var prior = new Fixture(synthetic: false); var shortPrior = ShortPanel(prior);
        prior.Store.Enqueue(new(Guid.NewGuid().ToString("D"), prior.Dataset.Id, new(DevelopmentStart: new DateOnly(2011, 1, 1)), new()), "unit");
        var request = new StudyRequest(Guid.NewGuid().ToString("D"), shortPrior.Id, "Earlier window", "research", 20, new());
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => prior.Store.RegisterStudy(request, "unit")).Code);
        using var active = new Fixture(synthetic: false); var shortActive = ShortPanel(active);
        active.Store.RegisterStudy(request with { RequestId = Guid.NewGuid().ToString("D"), DatasetId = shortActive.Id }, "unit");
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => active.Store.Enqueue(new(Guid.NewGuid().ToString("D"), active.Dataset.Id,
            new(DevelopmentStart: new DateOnly(2011, 1, 1)), new()), "unit")).Code);
    }
    [Fact]
    public void ANewStudysDevelopmentCannotConsumeAnExistingFinalReservation()
    {
        using var f = new Fixture(synthetic: false); var shortData = ShortPanel(f);
        var earlier = f.Store.RegisterStudy(new(Guid.NewGuid().ToString("D"), shortData.Id, "Earlier final window", "research", 20, new()), "unit");
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => f.Register()).Code);
        Assert.Single(f.Store.Studies()); Assert.Equal(earlier.Plan.Candidates.Length * 2, f.Store.ListRuns().Count);
        Assert.Equal("development", f.Store.Study(earlier.Id)!.Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClaimValidationFailureIsTerminalAndDoesNotFaultTheWorker(bool oversized)
    {
        using var f = new Fixture(); var data = Data(true, source: "Separate authored corrupt-claim fixture"); var badData = f.Store.Import(data.Csv, data.Metadata, "Bad claim test");
        var bad = f.Store.Enqueue(new(Guid.NewGuid().ToString("D"), badData.Id, new(), new()), "unit");
        var good = f.Store.Enqueue(new(Guid.NewGuid().ToString("D"), f.Dataset.Id, new(), new()), "unit");
        var path = Path.Combine(f.Root, "datasets", badData.Id, "prices.csv");
        if (oversized) File.WriteAllBytes(path, new byte[Twenty.Core.Data.DatasetJson.MaximumBytes + 1]); else File.AppendAllText(path, "invalid\n");
        using var worker = new ExperimentWorker(f.Store, NullLogger<ExperimentWorker>.Instance);
        await worker.StartAsync(default);
        for (var i = 0; i < 500 && f.Store.Find(good.Id)!.Status != "succeeded"; i++) await Task.Delay(10);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(shutdown.Token);
        Assert.Equal("failed", f.Store.Find(bad.Id)!.Status); Assert.NotNull(f.Store.Find(bad.Id)!.ErrorCode);
        var completed = f.Store.Find(good.Id)!;
        Assert.True(completed.Status == "succeeded", $"Good run finished as {completed.Status}; safe error code: {completed.ErrorCode ?? "none"}");
        Assert.Equal(2, f.Store.ListRuns().Count);
    }
    [Fact]
    public void CancellingTheFinalPairKeepsTheClaimAndItsOriginalIds()
    {
        using var f = new Fixture(); var study = f.Register(); Drain(f.Store); study = f.Store.Study(study.Id)!;
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256); study = f.Store.EvaluateStudy(study.Id, request, "unit");
        var a = study.FinalBaseRunId; var b = study.FinalStressRunId; f.Store.CancelStudy(study.Id); f.Restart();
        Assert.Null(f.Store.Claim()); study = f.Store.EvaluateStudy(study.Id, request, "unit");
        Assert.Equal("cancelled", study.Status); Assert.NotNull(study.FinalRequestedAt); Assert.Equal(a, study.FinalBaseRunId); Assert.Equal(b, study.FinalStressRunId);
        Assert.Equal(2, f.Store.ListRuns().Count(r => r.Config.EvaluateHoldout));
    }
    [Fact]
    public void InterruptedFinalWorkIsNotReplacedAfterRestart()
    {
        using var f = new Fixture(); var study = f.Register(); Drain(f.Store); study = f.Store.Study(study.Id)!;
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256); study = f.Store.EvaluateStudy(study.Id, request, "unit");
        Assert.NotNull(f.Store.Claim()); f.Restart(); Assert.Null(f.Store.Claim()); study = f.Store.Study(study.Id)!;
        Assert.Equal("final-failed", study.Status); Assert.Equal("study-trial-incomplete", study.ErrorCode);
        Assert.Equal(study.FinalBaseRunId, f.Store.EvaluateStudy(study.Id, request, "unit").FinalBaseRunId);
        Assert.Equal(2, f.Store.ListRuns().Count(r => r.Config.EvaluateHoldout));
        Assert.All(f.Store.ListRuns().Where(r => r.Config.EvaluateHoldout), r => Assert.Contains(r.Status, new[] { "interrupted", "cancelled" }));
    }
    private static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private static void RewriteStudy(Fixture f, ResearchStudy study, Action<JsonObject> change)
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(study, ResearchStore.Json))!.AsObject(); change(document);
        document["planSha256"] = Hash(document["plan"]!.ToJsonString(ResearchStore.Json));
        var json = document.ToJsonString(ResearchStore.Json);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Root, "research.db"), Pooling = false }.ToString()); db.Open();
        using var update = db.CreateCommand(); update.CommandText = "UPDATE studies SET json=$json,sha256=$sha WHERE id=$id";
        update.Parameters.AddWithValue("$json", json); update.Parameters.AddWithValue("$sha", Hash(json)); update.Parameters.AddWithValue("$id", study.Id); Assert.Equal(1, update.ExecuteNonQuery());
    }
    [Fact]
    public void CodeChangesStopWorkAndHistoricalPlanBytesSurviveDtoEvolution()
    {
        using var changed = new Fixture(); var study = changed.Register();
        RewriteStudy(changed, study, root => root["plan"]!["codeSha256"] = new string('f', 64));
        Assert.Null(changed.Store.Claim()); Assert.Equal("study-code-changed", changed.Store.Study(study.Id)!.ErrorCode);
        Assert.All(changed.Store.ListRuns(), r => Assert.Null(r.StartedAt));
        Assert.Equal("execution-study-code-changed", Assert.Throws<ResearchException>(() => changed.Store.AssertStudyExecutionRun(changed.Store.ListRuns()[0], false)).Code);
        using var evolved = new Fixture(); study = evolved.Register(); RewriteStudy(evolved, study, root => root["plan"]!["futureOptionalField"] = "retain original bytes");
        var plan = evolved.Store.StudyPlanJson(study.Id); evolved.Store.CancelStudy(study.Id);
        Assert.Equal(plan, evolved.Store.StudyPlanJson(study.Id)); Assert.Equal(Hash(plan!), evolved.Store.Study(study.Id)!.PlanSha256);
    }
    [Fact]
    public void LegacyPlansStayReadableButCannotBorrowTheCurrentFingerprint()
    {
        using var f = new Fixture(); var study = f.Register();
        RewriteStudy(f, study, root => root["plan"]!.AsObject().Remove("identityScheme"));
        var plan = f.Store.StudyPlanJson(study.Id); Assert.Equal("legacy-core-api-assemblies-v1", f.Store.Study(study.Id)!.Plan.IdentityScheme);
        Assert.Null(f.Store.Claim()); Assert.Equal("study-code-changed", f.Store.Study(study.Id)!.ErrorCode);
        Assert.Equal(plan, f.Store.StudyPlanJson(study.Id)); Assert.All(f.Store.ListRuns(), r => Assert.Null(r.StartedAt));
    }
}
