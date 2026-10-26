using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class RawStudyWorkerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-raw-worker-" + Guid.NewGuid().ToString("N"));
    private ResearchStore store;
    public RawStudyWorkerTests() => store = new(root);
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private RawResearchStudy Register(bool withSplit = false, string? distribution = null)
    {
        var original = RawStudyDevelopmentRunTests.Fixture();
        var f = withSplit ? RawStudySplitTests.Fixture() : (original.Plan, original.Dataset, original.Calendar, Actions: "[]"u8.ToArray());
        if (distribution is not null) f = RawStudyDistributionTests.Fixture(distribution);
        return RegisterFixture(f);
    }
    private RawResearchStudy RegisterFixture((RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) f)
    {
        var sha = store.ImportUniverseDataset(f.Dataset);
        var plan = RawUniverseStudyProtocol.Declare(sha, f.Plan.UniverseId, f.Plan.Synthetic, f.Plan.Purpose,
            ResearchIdentity.Current.Sha256, ResearchIdentity.Scheme, f.Plan.Decisions, f.Plan.MonthlyGbp,
            f.Plan.BaseExecution, f.Plan.Calendars, f.Plan.ActionCatalogSha256);
        var inbox = Path.Combine(root, "imports"); Directory.CreateDirectory(inbox);
        File.WriteAllBytes(Path.Combine(inbox, f.Calendar.Sha256 + ".calendar.json"), JsonSerializer.SerializeToUtf8Bytes(f.Calendar, RawReplayJson.Options));
        File.WriteAllBytes(Path.Combine(inbox, plan.ActionCatalogSha256 + ".actions.json"), f.Actions);
        return store.RegisterRawStudy(new(Guid.NewGuid().ToString("D"), "Authored execution", Convert.ToBase64String(RawUniverseStudyProtocol.Encode(plan))), "authored");
    }
    [Fact]
    public void AuthoredFalseProvenanceExercisesNoCandidateWithoutSpendingARealHoldout()
    {
        // Authored values with a false synthetic flag test the guard; these are not historical evidence.
        var f = RawStudyDevelopmentRunTests.Fixture();var file=Twenty.Core.Data.DatasetJson.Read(f.Dataset);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(file with { Manifest=file.Manifest with { Synthetic=false } },Twenty.Core.Data.DatasetJson.Options);
        var study=RegisterFixture((f.Plan with { Synthetic=false,Purpose="research" },bytes,f.Calendar,"[]"u8.ToArray()));
        store.StartRawStudy(study.Id,new(Guid.NewGuid().ToString("D"),study.PlanSha256));
        for(var i=0;i<8;i++) Assert.True(store.ProcessNextRawStudyRun("authored"));
        var result=store.SelectRawStudy(study.Id,new(Guid.NewGuid().ToString("D"),study.PlanSha256),"authored");
        Assert.Equal("no-candidate",result.Status);Assert.Null(result.SelectedCandidateId);
        Assert.Equal(8,store.ListRawTrials().Count);
        using var db=new SqliteConnection("Data Source="+Path.Combine(root,"research.db"));db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM study_holdout_claims";Assert.Equal(0L,(long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InvalidPerSharePolicyCannotAdmitAnyTrial()
    {
        var f = RawStudyDistributionTests.Fixture(); var entry = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var actions = JsonSerializer.SerializeToUtf8Bytes<System.Collections.Immutable.ImmutableArray<RawActionSchedule>>(
            [entry with { Delivery = entry.Delivery! with { WithheldPerShare = 9 } }], RawReplayJson.Options);
        var plan = f.Plan with { ActionCatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(actions)) };
        Assert.Equal("raw-study-payment-policy", Assert.Throws<ResearchException>(() => RegisterFixture((plan, f.Dataset, f.Calendar, actions))).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

    [Theory]
    [InlineData("dividend",701.6)]
    [InlineData("cash",700)]
    [InlineData("zero",0)]
    [InlineData("stock",700)]
    public void RegisteredPerShareActionsUseRetainedInputsAndRecoverTheirExactReports(string kind,decimal expected)
    {
        var study = Register(distribution: kind);
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        var row = store.ListRawTrials().Single(t => t.Status == "succeeded");
        var bytes = store.RawTrialReportBytes(row.Id);
        var dataset = store.RawStudyOriginalBytes(study.Id, study.Originals.Single(o => o.Kind == "dataset").OriginalSha256);
        var report = RawStudyDevelopmentRun.Verify(bytes, _ => dataset);
        Assert.Equal(expected, report.Strategies.Single(s => s.Strategy == "momentum").EndingEquityGbp);
        store.Dispose(); store = new(root);
        Assert.Equal(bytes, store.RawTrialReportBytes(row.Id));
    }

    [Fact]
    public void RegisteredWorkerConsumesTheRetainedSplitCatalogAndPublishesReplayableReports()
    {
        var study = Register(withSplit: true);
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        var row = store.ListRawTrials().Single(t => t.Status == "succeeded");
        var dataset = store.RawStudyOriginalBytes(study.Id, study.Originals.Single(o => o.Kind == "dataset").OriginalSha256);
        var report = RawStudyDevelopmentRun.Verify(store.RawTrialReportBytes(row.Id), _ => dataset);
        Assert.All(report.Strategies, s => Assert.Equal(700, s.EndingEquityGbp));
        Assert.Contains("applied-split", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64)));
    }

    [Fact]
    public void RestartInterruptsAStartedAttemptAndNeverReplacesIt()
    {
        var study = Register();
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        var row = store.ListRawTrials()[0] with { Status = "started", StartedAt = DateTimeOffset.UtcNow, WorkerCommit = "authored" };
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE raw_trials SET status='started',json=$json WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, RawReplayJson.Options)); cmd.Parameters.AddWithValue("$id", row.Id); cmd.ExecuteNonQuery();
        }
        store.Dispose(); store = new(root);
        Assert.Equal("failed", store.RawStudies().Single().Status);
        Assert.Equal("interrupted", store.ListRawTrials().Single(t => t.Id == row.Id).Status);
        Assert.Equal(7, store.ListRawTrials().Count(t => t.Status == "cancelled"));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
    }

    [Fact]
    public void UniverseQueueUsesStartTimeRatherThanRawAdmissionTime()
    {
        var raw = Register();
        var f = RawStudyDevelopmentRunTests.Fixture();
        var universe = store.RegisterUniverseStudy(new(Guid.NewGuid().ToString("D"), "Authored queue", "engineering",
            raw.Plan.DatasetSha256, raw.Plan.UniverseId, f.Plan.Decisions, 45, 20, new()), "authored");
        store.StartRawStudy(raw.Id, new(Guid.NewGuid().ToString("D"), raw.PlanSha256));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.True(store.ProcessNextUniverseStudyRun("authored"));
        store.CancelUniverseStudy(universe.Id);
        Assert.True(store.ProcessNextRawStudyRun("authored"));
    }

    [Fact]
    public void ExplicitStartRunsAllEightOriginalTrialsAndRecoveryPreservesReports()
    {
        var study = Register();
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        var request = new RawStudyStartRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        store.StartRawStudy(study.Id, request);
        Assert.Equal("raw-study-already-started", Assert.Throws<ResearchException>(() => store.StartRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") })).Code);
        Directory.Delete(Path.Combine(root, "imports"), true);
        for (var i = 0; i < 8; i++) Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("development-complete", store.RawStudies().Single().Status);
        Assert.All(store.ListRawTrials(), t => Assert.Equal("succeeded", t.Status));
        var hashes = store.ListRawTrials().ToDictionary(t => t.Id, t => Convert.ToHexStringLower(SHA256.HashData(store.RawTrialReportBytes(t.Id))));
        var first = store.ListRawTrials()[0]; var path = Path.Combine(root,"runs",first.Id,"raw-study-report.json");var original = File.ReadAllBytes(path);
        var selectionRequest = new RawStudySelectionRequest(Guid.NewGuid().ToString("D"),study.PlanSha256);
        try
        {
            File.WriteAllText(path,"{}");
            Assert.Equal("raw-study-report-integrity",Assert.Throws<ResearchException>(()=>store.SelectRawStudy(study.Id,selectionRequest,"authored")).Code);
            Assert.Null(store.RawStudies().Single().SelectionRequestId);
        }
        finally { File.WriteAllBytes(path,original); }
        var selected = store.SelectRawStudy(study.Id,selectionRequest,"authored");
        Assert.Equal("candidate-frozen",selected.Status);Assert.Equal("momentum:1",selected.SelectedCandidateId);
        var selection = store.RawStudySelectionBytes(study.Id);
        Assert.Equal("raw-study-selection-frozen",Assert.Throws<ResearchException>(()=>store.SelectRawStudy(study.Id,
            selectionRequest with { RequestId=Guid.NewGuid().ToString("D") },"authored")).Code);
        store.Dispose(); store = new(root);
        Assert.Equal("candidate-frozen", store.StartRawStudy(study.Id, request).Status);
        Assert.Equal("candidate-frozen",store.SelectRawStudy(study.Id,selectionRequest,"authored").Status);
        Assert.Equal(selection,store.RawStudySelectionBytes(study.Id));
        Assert.All(store.ListRawTrials(), t => Assert.Equal(hashes[t.Id], Convert.ToHexStringLower(SHA256.HashData(store.RawTrialReportBytes(t.Id)))));
    }
    [Fact]
    public void CancellationPreservesSuccessAndReleasesOnlyUnspentReservations()
    {
        var study = Register();
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        var success = store.ListRawTrials().Single(t => t.Status == "succeeded");
        var bytes = store.RawTrialReportBytes(success.Id);
        store.CancelRawStudy(study.Id);
        Assert.Equal(7, store.ListRawTrials().Count(t => t.Status == "cancelled"));
        Assert.Equal(bytes, store.RawTrialReportBytes(success.Id));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
    }

    [Fact]
    public async Task InFlightCancellationStopsPublicationAndRetainsTheAttempt()
    {
        var study = Register();
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        var worker = Task.Run(() => store.ProcessNextRawStudyRun("authored"));
        Assert.True(SpinWait.SpinUntil(() => store.ListRawTrials().Any(t => t.Status == "started"), TimeSpan.FromSeconds(10)));
        store.CancelRawStudy(study.Id);
        Assert.True(await worker);
        Assert.All(store.ListRawTrials(), t => Assert.Equal("cancelled", t.Status));
        Assert.Single(store.ListRawTrials(), t => t.StartedAt is not null);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "runs"), "raw-study-report.json", SearchOption.AllDirectories));
        store.Dispose(); store = new(root);
        Assert.False(store.ProcessNextRawStudyRun("authored"));
    }

    [Fact]
    public void MissingRetainedInputFailsOneAttemptAndCancelsRemainingWithoutReplacement()
    {
        var study = Register();
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        var input = study.Originals.First(i => i.Kind == "calendar");
        File.Delete(Path.Combine(root, "raw-study-inputs", "calendar", input.OriginalSha256 + ".json"));
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("failed", store.RawStudies().Single().Status);
        Assert.Single(store.ListRawTrials(), t => t.Status == "failed");
        Assert.Equal(7, store.ListRawTrials().Count(t => t.Status == "cancelled"));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
    }
}
