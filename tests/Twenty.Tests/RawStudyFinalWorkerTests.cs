using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    private sealed class RawFinalClock(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }
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
    private void WriteAuthoredFinalIntegrityFixture(RawResearchStudy study, RawStudyTrialEntry? row = null)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open();
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(study, RawReplayJson.Options);
        cmd.CommandText = "UPDATE raw_studies SET json=$json,sha256=$sha WHERE id=$id";
        cmd.Parameters.AddWithValue("$json", System.Text.Encoding.UTF8.GetString(bytes));
        cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(bytes))); cmd.Parameters.AddWithValue("$id", study.Id); cmd.ExecuteNonQuery();
        if (row is not null)
        { cmd.Parameters.Clear(); cmd.CommandText = "UPDATE raw_trials SET json=$json WHERE id=$id";
          cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, RawReplayJson.Options)); cmd.Parameters.AddWithValue("$id", row.Id); cmd.ExecuteNonQuery(); }
        tx.Commit();
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
        var slots = typeof(ResearchStore).GetMethod("RawReservedStudySlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(2, slots.Invoke(store, null));
        SharedFinalClaimFixture.AssertCaseAliasConflict(root, id => store.EvaluateRawStudy(study.Id, new(id, study.PlanSha256), "authored"));
        var started = store.EvaluateRawStudy(study.Id, request, "authored");
        Assert.Equal("authored", started.FinalRequestedCommit); Assert.Equal(0, slots.Invoke(store, null));
        Assert.Equal("final-evaluation", started.Status); Assert.Equal(1, RawFinalClaims());
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT c.start_date,c.end_date,c.at,c.synthetic,w.start_at,w.end_at FROM study_holdout_claims c JOIN universe_holdout_windows w ON c.study_id=w.study_id";
            using var reader = cmd.ExecuteReader(); Assert.True(reader.Read());
            Assert.Equal(DateOnly.FromDateTime(study.Plan.ReservedStart.UtcDateTime).ToString("yyyy-MM-dd"), reader.GetString(0));
            Assert.Equal(DateOnly.FromDateTime(study.Plan.ReservedEnd.UtcDateTime).AddDays(1).ToString("yyyy-MM-dd"), reader.GetString(1));
            Assert.Equal(started.FinalRequestedAt!.Value.ToString("O"), reader.GetString(2)); Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(study.Plan.ReservedStart.ToString("O"), reader.GetString(4));
            Assert.Equal(study.Plan.ReservedEnd.ToString("O"), reader.GetString(5)); Assert.False(reader.Read());
        }
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
        Assert.Equal("engineering-complete", store.CancelRawStudy(study.Id).Status);
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "authored")).Code);
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
        Assert.Equal(completed.FinalBaseTrialId, store.EvaluateRawStudy(study.Id, request with { RequestId = request.RequestId.ToUpperInvariant() }, "other").FinalBaseTrialId);
        Assert.Equal(selection, store.RawStudySelectionBytes(study.Id));
        Assert.All(reports, pair => Assert.Equal(pair.Value, store.RawTrialReportBytes(pair.Key)));
        Assert.Equal(1, RawFinalClaims());
        try
        {
            WriteAuthoredFinalIntegrityFixture(completed with { FinalBaseReportSha256 = new string('a', 64) });
            Assert.Equal("raw-study-final-integrity", Assert.Throws<ResearchException>(() => store.ListRawTrials()).Code);
        }
        finally { WriteAuthoredFinalIntegrityFixture(completed); }
        // Authored coherent hash/marker corruption probes role/era checks rather than the outer hash gate.
        var finalRow = store.ListRawTrials().Single(t => t.Id == completed.FinalBaseTrialId);
        var reportPath = Path.Combine(root, "runs", finalRow.Id, "raw-study-report.json"); var original = File.ReadAllBytes(reportPath);
        foreach (var version in new[] { RawStudyDevelopmentRun.Version, "raw-study-final-v1" })
        {
            var wrong = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<RawStudyDevelopmentReport>(original, RawReplayJson.Options)! with
                { Version = version }, RawReplayJson.Options);
            var wrongSha = Convert.ToHexStringLower(SHA256.HashData(wrong));
            try
            {
                File.WriteAllBytes(reportPath, wrong);
                WriteAuthoredFinalIntegrityFixture(completed with { FinalBaseReportSha256 = wrongSha }, finalRow with
                    { ReportSha256 = wrongSha, Publication = finalRow.Publication! with { ReportSha256 = wrongSha, ReportBytes = wrong.Length } });
                Assert.Equal("raw-study-report-integrity", Assert.Throws<ResearchException>(() => store.RawTrialReportBytes(finalRow.Id)).Code);
            }
            finally { File.WriteAllBytes(reportPath, original); WriteAuthoredFinalIntegrityFixture(completed, finalRow); }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCancellationOrRestartKeepsPermanentClaimAndCannotReplaceThePair(bool restart)
    {
        var study = FreezeRawCandidate(); var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        if (!restart)
        {
            store.Dispose(); store = new(root, new RawFinalClock(study.SelectionRequestedAt!.Value.AddDays(-1)));
            Assert.Equal("raw-study-clock-regression", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id, request, "authored")).Code);
            Assert.Equal(0, RawFinalClaims()); Assert.Equal("candidate-frozen", store.RawStudies().Single().Status);
            store.Dispose(); store = new(root);
        }
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
        if (restart) Assert.Contains("final-interrupted", JsonSerializer.Serialize(store.RawStudyEvents(study.Id)));
    }

    [Fact]
    public void ForeignCapacityGrowthAfterClaimCannotRerunAVerifiedFinalWithMissingStaging()
    {
        var study = FreezeRawCandidate(); var request = new StudyEvaluationRequest(Guid.NewGuid().ToString("D"), study.PlanSha256);
        store.EvaluateRawStudy(study.Id, request, "authored");
        var blocker = Path.Combine(root, "authored-quota-blocker");
        using (var file = File.Create(blocker)) file.SetLength(256L * 1024 * 1024); // Sparse authored file; no large download.
        Assert.True(store.ProcessNextRawStudyRun("authored")); File.Delete(blocker);
        Assert.Equal("final-evaluation", store.RawStudies().Single().Status); Assert.Equal(1, RawFinalClaims());
        var failed = Assert.Single(store.ListRawTrials(), t => t.Status == "publishing"); Assert.Equal("artifact-capacity", failed.ErrorCode);
        Assert.Contains("final-publication-pending", JsonSerializer.Serialize(store.RawStudyEvents(study.Id)));
        Assert.Equal("raw-study-publication-stage-unavailable", Assert.Throws<ResearchException>(() => RecoverPublication(study, request)).Code);
        Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.EvaluateRawStudy(study.Id,
            request with { RequestId = Guid.NewGuid().ToString("D") }, "authored")).Code);
    }

    [Fact]
    public void AuthoredFalseProvenancePinsNonSyntheticFinalGuardsAndBlocksAnOutcomeAlias()
    {
        // Deliberately false provenance flag exercises the genuine-path guards; all values are authored.
        var f = RawStudyDistributionTests.Fixture(); var file = Twenty.Core.Data.DatasetJson.Read(f.Dataset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Manifest = file.Manifest with { Synthetic = false } }, Twenty.Core.Data.DatasetJson.Options);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var entry = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var actions = JsonSerializer.SerializeToUtf8Bytes<System.Collections.Immutable.ImmutableArray<RawActionSchedule>>(
            [entry with { Capture = entry.Capture with { DatasetSha256 = sha },
                Delivery = entry.Delivery! with { Booking = entry.Delivery.Booking with { DatasetSha256 = sha } } }], RawReplayJson.Options);
        var study = RegisterFixture((f.Plan with { Synthetic = false, Purpose = "research", ActionCatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(actions)) }, bytes, f.Calendar, actions));
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        for (var i = 0; i < 8; i++) Assert.True(store.ProcessNextRawStudyRun("authored"));
        study = store.SelectRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "authored");
        Assert.Equal("candidate-frozen", study.Status);
        store.EvaluateRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "authored");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT synthetic FROM study_holdout_claims"; Assert.Equal(0L, (long)cmd.ExecuteScalar()!); }
        Assert.True(store.ProcessNextRawStudyRun("authored")); Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("research-rejected", store.RawStudies().Single().Status); // Flat final returns fail the strict positive margin screen.
        var aliasFile = Twenty.Core.Data.DatasetJson.Read(bytes);
        var alias = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(aliasFile with
            { Manifest = aliasFile.Manifest with { DatasetId = "authored-outcome-alias" } }, Twenty.Core.Data.DatasetJson.Options));
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(
            new(Guid.NewGuid().ToString("D"), "Authored alias guard", "research", alias, study.Plan.UniverseId,
                study.Plan.Decisions, 45, 20, new()), "authored")).Code);
    }
}
