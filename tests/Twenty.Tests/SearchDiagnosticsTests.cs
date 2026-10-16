using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Research;
using Twenty.Core.Research;

namespace Twenty.Tests;

public class SearchDiagnosticsTests
{
    private static DiagnosticReturns Stream(string id, double[] values, double[]? comparison = null, int offset = 0) =>
        new(id, new string('a', 64), Enumerable.Range(offset, values.Length).Select(i => new DateOnly(2000, 1, 1).AddMonths(i)).ToImmutableArray(),
            values.ToImmutableArray(), SearchDiagnostics.Comparators.ToImmutableDictionary(s => s, _ => (comparison ?? new double[values.Length]).ToImmutableArray()));

    [Fact]
    public void PairingCancelsSharedMarketShocksAndAnnualisesTheMeanDifference()
    {
        var market = Enumerable.Range(0, 72).Select(i => (i % 7 - 3) * .02).ToArray();
        var result = SearchDiagnostics.Build([Stream("a", market.Select(x => x + .01).ToArray(), market)]);
        Assert.Equal(4, result.Comparisons); Assert.Equal(72, result.Months);
        foreach (var s in result.Sensitivities)
        {
            Assert.Equal(1 + 71d / s.ExpectedBlockMonths, s.ExpectedBlocksPerPath);
            Assert.InRange(s.SimultaneousRadius, 0, 1e-14);
            Assert.All(s.Intervals, x => { Assert.InRange(x.AnnualisedMeanActiveReturn, .12 - 1e-14, .12 + 1e-14); Assert.InRange(x.PointwiseUpper - x.PointwiseLower, 0, 1e-14); });
        }
    }
    [Fact]
    public void DuplicatedPathsDoNotPretendToBeIndependentTrialsAndOrderDoesNotChangeResults()
    {
        var values = Enumerable.Range(0, 72).Select(i => Math.Sin(i * .7) * .03).ToArray();
        var one = SearchDiagnostics.Build([Stream("a", values)]);
        var two = SearchDiagnostics.Build([Stream("a", values), Stream("b", values)]);
        Assert.Equal(one.Sensitivities.Select(s => s.SimultaneousRadius), two.Sensitivities.Select(s => s.SimultaneousRadius));
        var reversed = SearchDiagnostics.Build([Stream("b", values), Stream("a", values)]);
        Assert.Equal(JsonSerializer.Serialize(two), JsonSerializer.Serialize(reversed));
    }
    [Fact]
    public void AddingAReversalChallengerCannotShrinkTheJointRadius()
    {
        var a = Stream("a", Enumerable.Range(0, 72).Select(i => i % 2 == 0 ? .01 : -.01).ToArray());
        var b = Stream("b", Enumerable.Range(0, 72).Select(i => i < 36 ? .1 : -.1).ToArray());
        var one = SearchDiagnostics.Build([a]); var two = SearchDiagnostics.Build([a, b]);
        for (var i = 0; i < one.Sensitivities.Length; i++) Assert.True(two.Sensitivities[i].SimultaneousRadius >= one.Sensitivities[i].SimultaneousRadius);
        Assert.True(two.Sensitivities[^1].SimultaneousRadius > two.Sensitivities[0].SimultaneousRadius);
    }
    [Fact]
    public void TheCommonCalendarWindowIsAlignedWithoutFillingOrUsingEarlierOutcomes()
    {
        var a = Stream("a", Enumerable.Repeat(.01, 72).ToArray());
        var b = Stream("b", Enumerable.Repeat(.02, 60).ToArray(), offset: 12);
        var before = SearchDiagnostics.Build([a, b]);
        var changed = a with { Candidate = a.Candidate.Select((v, i) => i < 12 ? 100d : v).ToImmutableArray() };
        var after = SearchDiagnostics.Build([changed, b]);
        Assert.Equal(60, before.Months); Assert.Equal(new DateOnly(2001, 1, 1), before.FirstPeriodEnd);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }
    [Fact]
    public void InvalidStreamsGapsMismatchedComparatorsAndCancellationAreRejected()
    {
        var a = Stream("a", new double[60]);
        Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([]));
        Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([a, a]));
        Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([a with { Candidate = [double.NaN] }]));
        Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([a with { Comparators = a.Comparators.Remove("cash") }]));
        Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([a with { PeriodEnds = a.PeriodEnds.SetItem(20, a.PeriodEnds[19]) }]));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SearchDiagnostics.Build([a], cancel.Token));
        var later = Stream("b", new double[60], offset: 50);
        Assert.Equal("diagnostic-common-window-too-short", Assert.Throws<ResearchException>(() => SearchDiagnostics.Build([a, later])).Code);
    }
    [Fact]
    public void CalendarMonthEndsAreValidEvenWhenDayNumbersVary()
    {
        var a = Stream("a", new double[60]);
        a = a with { PeriodEnds = a.PeriodEnds.Select(d => d.AddMonths(1).AddDays(-1)).ToImmutableArray() };
        Assert.Equal(60, SearchDiagnostics.Build([a]).Months);
    }
}

public class DiagnosticStoreTests
{
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "twenty-diagnostic-" + Guid.NewGuid().ToString("N"));
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static DatasetEntry Import(ResearchStore store) => store.Import(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv")),
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json")), "Synthetic diagnostics");
    private static RunEntry Complete(ResearchStore store, string data, ExperimentConfig? config = null, ResearchCosts? costs = null)
    {
        config ??= new(); costs ??= new();
        var run = store.Enqueue(new(Guid.NewGuid().ToString("D"), data, config, costs), "test");
        Assert.Equal(run.Id, store.Claim()!.Id); store.Complete(run.Id, Backtest.Run(store.Panel(data), config, costs));
        return store.Find(run.Id)!;
    }
    [Fact]
    public async Task FamilyIncludesAllCompatibleTrialsAndDisclosesFailedAndDifferentSearches()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root); var d = Import(store);
        var anchor = Complete(store, d.Id); var challenger = Complete(store, d.Id, new(TopK: 1, TrainMonths: 66, Hypothesis: "momentum"));
        var policy = Complete(store, d.Id, new(AllocationPolicy: "contribution-only"));
        var cost = Complete(store, d.Id, costs: new(FxBps: 0));
        var holdout = Complete(store, d.Id, new(HoldoutMonths: 12));
        var failed = store.Enqueue(new(Guid.NewGuid().ToString("D"), d.Id, new(), new()), "test"); store.Cancel(failed.Id);
        var service = new DiagnosticStore(store); var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), anchor.Id);
        var result = await service.Create(request);
        Assert.Equal("succeeded", result.Status); Assert.NotNull(result.Report); Assert.Equal(6, result.Trials.Length); Assert.Equal(2, result.Report.Candidates);
        Assert.True(result.Trials.Single(t => t.RunId == challenger.Id).Included);
        Assert.Equal("different-account-assumptions", result.Trials.Single(t => t.RunId == policy.Id).Reason);
        Assert.Equal("different-account-assumptions", result.Trials.Single(t => t.RunId == cost.Id).Reason);
        Assert.Equal("different-reserved-window", result.Trials.Single(t => t.RunId == holdout.Id).Reason);
        Assert.Equal("incomplete-or-failed-trial", result.Trials.Single(t => t.RunId == failed.Id).Reason);
        Assert.Equal(result.Id, (await service.Create(request)).Id);
        Assert.Equal("request-id-conflict", (await Assert.ThrowsAsync<ResearchException>(() => service.Create(request with { AnchorRunId = challenger.Id }))).Code);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(service.Find(result.Id)));
    }
    [Fact]
    public async Task CorruptReportsFailTheWholeAttemptAndTheFailureRemainsRecorded()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root); var d = Import(store); var run = Complete(store, d.Id);
        File.AppendAllText(Path.Combine(w.Root, "runs", run.Id, "report.json"), " ");
        var service = new DiagnosticStore(store); var result = await service.Create(new(Guid.NewGuid().ToString("D"), run.Id));
        Assert.Equal("failed", result.Status); Assert.Equal("report-integrity", result.ErrorCode); Assert.Null(result.Report);
        Assert.Single(result.Trials); Assert.Equal(result.Status, service.Find(result.Id)!.Status);
    }
    [Fact]
    public async Task AttemptsPersistAcrossRestartAndFailedAnchorsAreNotErased()
    {
        using var w = new Workspace(); DiagnosticAttempt good, bad;
        using (var store = new ResearchStore(w.Root))
        {
            var d = Import(store); var run = Complete(store, d.Id); var service = new DiagnosticStore(store);
            good = await service.Create(new(Guid.NewGuid().ToString("D"), run.Id));
            var queued = store.Enqueue(new(Guid.NewGuid().ToString("D"), d.Id, new(), new()), "test");
            bad = await service.Create(new(Guid.NewGuid().ToString("D"), queued.Id));
            Assert.Equal("failed", bad.Status); Assert.Equal("diagnostic-development-run-required", bad.ErrorCode);
        }
        using var resumed = new ResearchStore(w.Root); var restored = new DiagnosticStore(resumed);
        Assert.Equal(JsonSerializer.Serialize(good), JsonSerializer.Serialize(restored.Find(good.Id)));
        Assert.Equal(JsonSerializer.Serialize(bad), JsonSerializer.Serialize(restored.Find(bad.Id)));
    }
    [Fact]
    public async Task NearCapacityAdmissionDoesNotLeaveAnUnfinishableRunningAttempt()
    {
        using var w = new Workspace(); using var store = new ResearchStore(w.Root); var d = Import(store); var run = Complete(store, d.Id);
        var service = new DiagnosticStore(store);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(w.Root, "research.db"), Pooling = false }.ToString()); db.Open();
        const long target = 32L * 1024 * 1024 - 512 * 1024;
        long used = 0; var inserted = 0;
        while (target - used > 1024)
        {
            var filler = new DiagnosticAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), run.Id, "failed",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "authored-capacity-fixture", "", new string('a', 64), d.Id, true, [], null);
            var baseLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(filler, ResearchStore.Json));
            var size = (int)Math.Min(900 * 1024, target - used);
            if (size < baseLength) break;
            filler = filler with { WorkerCommit = new string('x', size - baseLength) };
            var json = JsonSerializer.Serialize(filler, ResearchStore.Json);
            using var insert = db.CreateCommand(); insert.CommandText = "INSERT INTO research_diagnostics VALUES($id,$request,'failed',$json,$sha)";
            insert.Parameters.AddWithValue("$id", filler.Id); insert.Parameters.AddWithValue("$request", filler.RequestId); insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))); insert.ExecuteNonQuery();
            used += Encoding.UTF8.GetByteCount(json); inserted++;
        }
        Assert.True(32L * 1024 * 1024 - used < 1024 * 1024);
        var error = await Assert.ThrowsAsync<ResearchException>(() => service.Create(new(Guid.NewGuid().ToString("D"), run.Id)));
        Assert.Equal("diagnostic-storage-capacity", error.Code);
        using var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM research_diagnostics"; Assert.Equal(inserted, (long)count.ExecuteScalar()!);
        count.CommandText = "SELECT COUNT(*) FROM research_diagnostics WHERE status='running'"; Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }
    [Fact]
    public async Task AProcessInterruptedAnalysisIsRecordedWithoutAutomaticReplay()
    {
        using var w = new Workspace(); DiagnosticAttempt running;
        using (var store = new ResearchStore(w.Root))
        {
            var d = Import(store); var run = Complete(store, d.Id); var service = new DiagnosticStore(store);
            var completed = await service.Create(new(Guid.NewGuid().ToString("D"), run.Id));
            running = completed with { Status = "running", FinishedAt = null, Report = null };
            var json = JsonSerializer.Serialize(running, ResearchStore.Json);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(w.Root, "research.db"), Pooling = false }.ToString()); db.Open();
            using var update = db.CreateCommand(); update.CommandText = "UPDATE research_diagnostics SET status='running',json=$json,sha256=$sha WHERE id=$id";
            update.Parameters.AddWithValue("$json", json); update.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))); update.Parameters.AddWithValue("$id", running.Id);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        using var resumed = new ResearchStore(w.Root); var restored = new DiagnosticStore(resumed);
        var result = restored.Find(running.Id)!; Assert.Equal("interrupted", result.Status); Assert.Equal("process-restarted", result.ErrorCode); Assert.Null(result.Report);
        Assert.Equal(result.Id, (await restored.Create(new(result.RequestId, result.AnchorRunId))).Id);
        Assert.Equal("interrupted", restored.Find(running.Id)!.Status);
    }
}
