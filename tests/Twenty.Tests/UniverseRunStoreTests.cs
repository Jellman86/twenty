using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class UniverseRunStoreTests
{
    private sealed class Workspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-universe-store-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static byte[] DatasetBytes(bool gapBbb = false) =>
        JsonSerializer.SerializeToUtf8Bytes(UniverseBacktestTests.Scenario(gapBbb), DatasetJson.Options);

    private static UniverseRunRequest Request(string sha, string? requestId = null, int topK = 3) =>
        new(requestId ?? Guid.NewGuid().ToString("D"), sha, "research-universe",
            Enumerable.Range(0, UniverseBacktestTests.Months).Select(UniverseBacktestTests.Decision).ToImmutableArray(),
            45, new(TopK: topK), new());

    [Fact]
    public void SuccessfulTrialIsRegisteredWithHashBoundReport()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var request = Request(sha);
        var entry = store.ExecuteUniverseRun(request, "test-code");
        Assert.Equal("succeeded", entry.Status);
        Assert.Equal("universe-backtest-fixture", entry.DatasetId);
        Assert.True(entry.Synthetic);
        var panel = ChangingUniversePanel.FromDataset(new PointInTimeDataset(UniverseBacktestTests.Scenario()),
            "research-universe", request.Decisions, TimeSpan.FromDays(45));
        Assert.Equal(panel.Sha256, entry.PanelSha256);
        var report = store.UniverseReport(entry.Id)!;
        var expected = UniverseBacktest.Run(panel, request.Config, request.Costs);
        Assert.Equal(expected.Summaries.ToArray(), report.Summaries.ToArray());
        Assert.Equal(entry.PanelSha256, report.PanelSha256);
        var requestBytes = store.UniverseRequestBytes(entry.Id)!;
        Assert.Equal(entry.RequestArtifactSha256, Convert.ToHexStringLower(SHA256.HashData(requestBytes)));
        var retained = JsonSerializer.Deserialize<UniverseRunRequest>(requestBytes, ResearchStore.Json)!;
        Assert.Equal(request.RequestId, retained.RequestId);
        Assert.Equal(request.DatasetSha256, retained.DatasetSha256);
        Assert.Equal(request.Decisions.ToArray(), retained.Decisions.ToArray());
        Assert.Equal(request.Config, retained.Config);
        Assert.Equal(request.Costs, retained.Costs);
        Assert.Single(store.ListUniverseRuns());
    }

    [Fact]
    public void ReplayReturnsTheOriginalTrialAndConflictIsRejected()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var request = Request(sha);
        var first = store.ExecuteUniverseRun(request, "older-code");
        var replay = store.ExecuteUniverseRun(request, "newer-code");
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal("older-code", replay.CodeCommit);
        Assert.Single(store.ListUniverseRuns());
        Assert.Equal("request-id-conflict",
            Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(Request(sha, request.RequestId, topK: 1), "test")).Code);
    }

    [Fact]
    public void FailedTrialIsRetainedAndCannotBeRetriedUnderTheSameId()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes(gapBbb: true));
        var request = Request(sha);
        var entry = store.ExecuteUniverseRun(request, "test-code");
        Assert.Equal("failed", entry.Status);
        Assert.Equal("universe-backtest-gaps", entry.ErrorCode);
        Assert.NotNull(entry.PanelSha256);
        Assert.Null(entry.ReportSha256);
        Assert.Null(store.UniverseReport(entry.Id));
        Assert.NotNull(store.UniverseRequestBytes(entry.Id));
        Assert.Equal(entry.Id, store.ExecuteUniverseRun(request, "test-code").Id);
        Assert.Single(store.ListUniverseRuns());
    }

    [Fact]
    public void UnknownDatasetIsRejectedBeforeAnyTrialExists()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var absent = new string('a', 64);
        Assert.Equal("dataset-not-found",
            Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(Request(absent), "test")).Code);
        Assert.Empty(store.ListUniverseRuns());
    }

    [Fact]
    public void RestartMarksStartedTrialsInterruptedAndKeepsTerminalOnes()
    {
        using var w = new Workspace();
        string succeededId, interruptedId;
        using (var store = new ResearchStore(w.Root))
        {
            var sha = store.ImportUniverseDataset(DatasetBytes());
            succeededId = store.ExecuteUniverseRun(Request(sha), "test").Id;
            interruptedId = store.ExecuteUniverseRun(Request(sha), "test").Id;
        }
        // Simulate a process death mid-trial: rewind one terminal record to "started".
        using (var db = new SqliteConnection($"Data Source={Path.Combine(w.Root, "research.db")}"))
        {
            db.Open();
            using var read = db.CreateCommand();
            read.CommandText = "SELECT json FROM universe_runs WHERE id=$id";
            read.Parameters.AddWithValue("$id", interruptedId);
            var json = (string)read.ExecuteScalar()!;
            var entry = JsonSerializer.Deserialize<UniverseRunEntry>(json, ResearchStore.Json)!
                with { Status = "started", FinishedAt = null };
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE universe_runs SET json=$json,status='started' WHERE id=$id";
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, ResearchStore.Json));
            update.Parameters.AddWithValue("$id", interruptedId);
            update.ExecuteNonQuery();
        }
        using var reopened = new ResearchStore(w.Root);
        Assert.Equal("succeeded", reopened.FindUniverseRun(succeededId)!.Status);
        var interrupted = reopened.FindUniverseRun(interruptedId)!;
        Assert.Equal("interrupted", interrupted.Status);
        Assert.Equal("process-restarted", interrupted.ErrorCode);
        Assert.Equal(2, reopened.ListUniverseRuns().Count);
        Assert.NotNull(reopened.UniverseReport(succeededId));
    }

    [Fact]
    public void TamperedReportFailsIntegrity()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var entry = store.ExecuteUniverseRun(Request(sha), "test");
        File.AppendAllText(Path.Combine(w.Root, "runs", entry.Id, "universe-report.json"), " ");
        Assert.Equal("report-integrity", Assert.Throws<ResearchException>(() => store.UniverseReport(entry.Id)).Code);
    }

    [Fact]
    public void TamperedRequestFailsIntegrityWithoutChangingTheRecordedTrial()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var request = Request(sha);
        var entry = store.ExecuteUniverseRun(request, "test");
        File.AppendAllText(Path.Combine(w.Root, "runs", entry.Id, "universe-request.json"), " ");
        Assert.Equal("universe-request-integrity", Assert.Throws<ResearchException>(() => store.UniverseRequestBytes(entry.Id)).Code);
        Assert.Equal(entry, store.ExecuteUniverseRun(request, "replay-does-not-repair-evidence"));
        Assert.NotNull(store.UniverseReport(entry.Id));
        Assert.Null(store.UniverseRequestBytes(new string('a', 32)));
    }

    [Fact]
    public void ReplayDoesNotNeedTheOriginalDatasetAndDoesNotRecompute()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var request = Request(sha);
        var entry = store.ExecuteUniverseRun(request, "test");
        File.Delete(Path.Combine(w.Root, "universe-datasets", sha + ".json"));
        Assert.Equal(entry, store.ExecuteUniverseRun(request, "different-code"));
        Assert.NotNull(store.UniverseRequestBytes(entry.Id));
        Assert.NotNull(store.UniverseReport(entry.Id));
        Assert.Single(store.ListUniverseRuns());
    }

    [Fact]
    public void UniverseArtifactsRespectTheSameWorkspaceCapacityAsModelReports()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        using (var sparse = File.Create(Path.Combine(w.Root, "authored-capacity-fixture"))) sparse.SetLength(256L * 1024 * 1024);
        Assert.Equal("artifact-capacity", Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(Request(sha), "test")).Code);
        Assert.Empty(store.ListUniverseRuns());
    }

    [Fact]
    public void LegacyTrialCannotInventARequestArtifactButItsReportAndReplayRemainReadable()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root);
        var sha = store.ImportUniverseDataset(DatasetBytes());
        var request = Request(sha);
        var entry = store.ExecuteUniverseRun(request, "legacy-fixture");
        File.Delete(Path.Combine(w.Root, "runs", entry.Id, "universe-request.json"));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(w.Root, "research.db")}"))
        {
            db.Open(); using var update = db.CreateCommand();
            update.CommandText = "UPDATE universe_runs SET json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", entry.Id);
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry with { RequestArtifactSha256 = null }, ResearchStore.Json));
            update.ExecuteNonQuery();
        }
        Assert.Equal("universe-request-unavailable", Assert.Throws<ResearchException>(() => store.UniverseRequestBytes(entry.Id)).Code);
        Assert.Null(store.ExecuteUniverseRun(request, "newer-code").RequestArtifactSha256);
        Assert.NotNull(store.UniverseReport(entry.Id));
        Assert.False(File.Exists(Path.Combine(w.Root, "runs", entry.Id, "universe-request.json")));
    }
}
