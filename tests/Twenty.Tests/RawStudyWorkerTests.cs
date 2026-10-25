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
    private RawResearchStudy Register()
    {
        var f = RawStudyDevelopmentRunTests.Fixture();
        var sha = store.ImportUniverseDataset(f.Dataset);
        var plan = RawUniverseStudyProtocol.Declare(sha, f.Plan.UniverseId, true, "engineering",
            ResearchIdentity.Current.Sha256, ResearchIdentity.Scheme, f.Plan.Decisions, f.Plan.MonthlyGbp,
            f.Plan.BaseExecution, f.Plan.Calendars, f.Plan.ActionCatalogSha256);
        var inbox = Path.Combine(root, "imports"); Directory.CreateDirectory(inbox);
        File.WriteAllBytes(Path.Combine(inbox, f.Calendar.Sha256 + ".calendar.json"), JsonSerializer.SerializeToUtf8Bytes(f.Calendar, RawReplayJson.Options));
        File.WriteAllBytes(Path.Combine(inbox, plan.ActionCatalogSha256 + ".actions.json"), "[]"u8.ToArray());
        return store.RegisterRawStudy(new(Guid.NewGuid().ToString("D"), "Authored execution", Convert.ToBase64String(RawUniverseStudyProtocol.Encode(plan))), "authored");
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
        store.Dispose(); store = new(root);
        Assert.Equal("development-complete", store.StartRawStudy(study.Id, request).Status);
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
