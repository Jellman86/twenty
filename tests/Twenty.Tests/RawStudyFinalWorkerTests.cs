using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    private RawResearchStudy FreezeRawCandidate()
    {
        var study = Register(); store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        for (var i = 0; i < 8; i++) Assert.True(store.ProcessNextRawStudyRun("authored"));
        return store.SelectRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "authored");
    }
    private long RawFinalClaims()
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM study_holdout_claims";
        return (long)cmd.ExecuteScalar()!;
    }
    [Fact]
    public void FinalRequiresFrozenSelectionAndRollsBackPartialAdmissionButRetainsFailedExposure()
    {
        var unselected = Register();
        Assert.Equal("study-candidate-not-frozen", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(unselected.Id,
            new(Guid.NewGuid().ToString("D"), unselected.PlanSha256), "authored")).Code);
        Assert.Equal(0, RawFinalClaims()); store.CancelRawStudy(unselected.Id);
        var study = FreezeRawCandidate(); var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        var runs = Path.Combine(root, "runs"); var saved = Path.Combine(root, "saved-runs");
        Directory.Move(runs, saved); File.WriteAllText(runs, "authored IO refusal");
        try { Assert.ThrowsAny<IOException>(() => store.EvaluateRawStudy(study.Id, request, "authored")); }
        finally { File.Delete(runs); Directory.Move(saved, runs); }
        Assert.Equal(0, RawFinalClaims()); Assert.Equal(16, store.ListRawTrials().Count);
        Assert.Equal("candidate-frozen", store.RawStudies().Single(s => s.Id == study.Id).Status);
        store.EvaluateRawStudy(study.Id, request, "authored");
        var calendar = study.Originals.Single(o => o.Kind == "calendar");
        File.Delete(Path.Combine(root, "raw-study-inputs", "calendar", calendar.OriginalSha256 + ".json"));
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("final-failed", store.RawStudies().Single(s => s.Id == study.Id).Status);
        Assert.Equal(1, RawFinalClaims());
        var final = store.ListRawTrials().Where(t => t.StudyId == study.Id && t.TrialId.StartsWith("final:", StringComparison.Ordinal)).ToArray();
        Assert.Single(final, t => t.Status == "failed"); Assert.Single(final, t => t.Status == "cancelled");
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "authored")).Code);
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM universe_holdout_windows"; cmd.ExecuteNonQuery();
        Assert.Equal("raw-study-final-claim-integrity", Assert.Throws<ResearchException>(() => store.ListRawTrials()).Code);
    }
    [Fact]
    public void FinalClaimAndExactPairPrecedeWorkAndNeverChangeSelectionOrDevelopmentArtifacts()
    {
        var study = FreezeRawCandidate(); var selection = store.RawStudySelectionBytes(study.Id);
        var originals = store.ListRawTrials().ToDictionary(t => t.Id, t => store.RawTrialReportBytes(t.Id));
        var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        var started = store.EvaluateRawStudy(study.Id, request, "authored");
        Assert.Equal("final-evaluation", started.Status); Assert.Equal(1, RawFinalClaims());
        Assert.Equal(10, store.ListRawTrials().Count); Assert.Equal(2, store.ListRawTrials().Count(t => t.Status == "planned"));
        Assert.All(store.ListRawTrials().Where(t => t.Status == "planned"), t =>
        { Assert.Equal(study.SelectedCandidateId, t.CandidateId); Assert.Null(t.StartedAt); Assert.Null(t.ReportSha256); });
        Assert.Equal(selection, store.RawStudySelectionBytes(study.Id));
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "authored")).Code);
        Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.True(store.ProcessNextRawStudyRun("authored")); Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        var completed = store.RawStudies().Single(); Assert.Equal("engineering-complete", completed.Status);
        Assert.Equal(0, completed.FinalWorstMarginGbp); Assert.False(completed.FinalPassed);
        Assert.Equal(study.SelectedCandidateId, completed.SelectedCandidateId); Assert.Equal(1, RawFinalClaims());
        var reports = store.ListRawTrials().ToDictionary(t => t.Id, t => store.RawTrialReportBytes(t.Id));
        Assert.All(originals, pair => Assert.Equal(pair.Value, reports[pair.Key]));
        foreach (var row in store.ListRawTrials().Where(t => !originals.ContainsKey(t.Id)))
        {
            var report = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(reports[row.Id], RawReplayJson.Options)!;
            Assert.Equal(RawStudyFinalRun.Version, report.Version);
            Assert.All(report.Strategies, s => { Assert.Equal(480, s.ContributionsGbp); Assert.Equal(480, s.EndingEquityGbp); Assert.Equal(24, s.Months.Length); });
        }
        store.Dispose(); store = new(root);
        Assert.Equal(JsonSerializer.Serialize(completed, RawReplayJson.Options), JsonSerializer.Serialize(store.EvaluateRawStudy(study.Id, request, "changed-deployment"), RawReplayJson.Options));
        Assert.Equal(selection, store.RawStudySelectionBytes(study.Id));
        Assert.All(reports, pair => Assert.Equal(pair.Value, store.RawTrialReportBytes(pair.Key)));
        Assert.Equal(1, RawFinalClaims());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCancellationOrRestartKeepsPermanentClaimAndCannotReplaceThePair(bool restart)
    {
        var study = FreezeRawCandidate(); var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        store.EvaluateRawStudy(study.Id, request, "authored");
        if (restart)
        {
            var row = store.ListRawTrials().First(t => t.Status == "planned") with { Status = "started", StartedAt = DateTimeOffset.UtcNow, WorkerCommit = "authored" };
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
            { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE raw_trials SET status='started',json=$json WHERE id=$id";
              cmd.Parameters.AddWithValue("$id", row.Id); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, RawReplayJson.Options)); cmd.ExecuteNonQuery(); }
        }
        else store.CancelRawStudy(study.Id);
        store.Dispose(); store = new(root);
        Assert.Equal(restart ? "final-failed" : "cancelled", store.RawStudies().Single().Status);
        Assert.Equal(1, RawFinalClaims()); Assert.Equal(10, store.ListRawTrials().Count);
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "authored")).Code);
        Assert.Equal(JsonSerializer.Serialize(store.RawStudies().Single(), RawReplayJson.Options), JsonSerializer.Serialize(store.EvaluateRawStudy(study.Id, request, "authored"), RawReplayJson.Options));
        Assert.Equal(8, store.ListRawTrials().Count(t => t.Status == "succeeded"));
    }
}
