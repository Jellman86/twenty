using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingOrLateClaimPolicyIsRefusedAtAdmissionBeforeAnyClaimOrTrial(bool final, bool late)
    {
        var f = RawStudyPendingClaimTests.Fixture(final: final);
        var entry = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var firstMark = (final ? f.Plan.FinalContributions : f.Plan.DevelopmentContributions)[1].At;
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>(
            [entry with { MarkPolicy = late ? entry.MarkPolicy! with { AvailableAt = firstMark.AddTicks(1) } : null }], RawReplayJson.Options);
        var plan = f.Plan with { ActionCatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(actions)) };
        Assert.Equal("raw-study-claim-policy-coverage", Assert.Throws<ResearchException>(() => RegisterFixture((plan, f.Dataset, f.Calendar, actions))).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM study_holdout_claims"; Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("dividend")]
    [InlineData("cash")]
    [InlineData("stock")]
    public void RegisteredPendingClaimsRetainExpectedAndActualEvidenceAcrossRestart(string kind)
    {
        var f = RawStudyPendingClaimTests.Fixture(kind); var study = RegisterFixture(f);
        store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
        Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        var row = store.ListRawTrials().Single(t => t.Status == "succeeded"); var bytes = store.RawTrialReportBytes(row.Id);
        var report = RawStudyDevelopmentRun.Verify(bytes, _ => f.Dataset);
        var first = report.Strategies.Single(s => s.Strategy == "momentum").Months[0];
        Assert.Equal(1, first.PendingClaimCount); Assert.Equal(kind == "dividend" ? 1.1m : 20, first.ExpectedClaimValueGbp);
        Assert.Equal(kind == "dividend" ? .1m : 0, first.ExpectedClaimFeesGbp);
        store.Dispose(); store = new(root);
        Assert.Equal(bytes, store.RawTrialReportBytes(row.Id));
    }
}
