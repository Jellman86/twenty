using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

// All financial values are authored. The false synthetic flags exercise admission
// contracts only and are never evidence of historical investment performance.
public sealed class UniverseStudyGuardTests
{
    private sealed class Workspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-universe-study-" + Guid.NewGuid().ToString("N"));
        public readonly ResearchStore Store;
        public Workspace() => Store = new(Root);
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }

    internal static DatasetEntry Monthly(ResearchStore store, int startYear = 2010)
    {
        var csv = new StringBuilder("date,symbol,total_return_index_gbp\n");
        for (var i = 0; i < 180; i++)
            foreach (var symbol in new[] { "A", "B", "C", "BENCHMARK" })
                csv.Append(new DateOnly(startYear, 1, 1).AddMonths(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(',').Append(symbol).Append(',').Append(100 + i).Append('\n');
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        { source = "Authored policy-contract fixture, not historical data", synthetic = false, benchmark = "BENCHMARK", currency = "GBP", frequency = "monthly" });
        return store.Import(Encoding.UTF8.GetBytes(csv.ToString()), metadata, "Authored monthly guard test");
    }

    private static UniverseRunRequest Universe(ResearchStore store, bool synthetic = false, bool gap = false)
    {
        var file = UniverseBacktestTests.Scenario(gap);
        file = file with { Manifest = file.Manifest with { Synthetic = synthetic } };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        return new(Guid.NewGuid().ToString("D"), sha, "research-universe",
            Enumerable.Range(0, UniverseBacktestTests.Months).Select(UniverseBacktestTests.Decision).ToImmutableArray(), 45, new(), new());
    }

    private static ResearchStudy Register(ResearchStore store, DatasetEntry data) =>
        store.RegisterStudy(new(Guid.NewGuid().ToString("D"), data.Id, "Authored guard study", "research", 20m, new()), "unit");

    [Fact]
    public void ActiveFinalReservationBlocksOverlappingUniverseAdmission()
    {
        using var w = new Workspace();
        Register(w.Store, Monthly(w.Store));
        var request = Universe(w.Store);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() =>
            w.Store.ExecuteUniverseRun(request, "unit")).Code);
        Assert.Empty(w.Store.ListUniverseRuns());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlierUniverseAttemptsPreventReservedOutcomesBeingPresentedAsUnseen(bool gap)
    {
        using var w = new Workspace();
        var data = Monthly(w.Store);
        var run = w.Store.ExecuteUniverseRun(Universe(w.Store, gap: gap), "unit");
        Assert.Equal(gap ? "failed" : "succeeded", run.Status);
        Assert.Equal("study-known-reserved-exposure", Assert.Throws<ResearchException>(() => Register(w.Store, data)).Code);
        Assert.Empty(w.Store.Studies());
    }

    [Fact]
    public void NonOverlappingAndSyntheticUniverseRunsRemainAvailable()
    {
        using var older = new Workspace();
        Register(older.Store, Monthly(older.Store, 2000));
        Assert.Equal("succeeded", older.Store.ExecuteUniverseRun(Universe(older.Store), "unit").Status);
        using var synthetic = new Workspace();
        Register(synthetic.Store, Monthly(synthetic.Store));
        Assert.Equal("succeeded", synthetic.Store.ExecuteUniverseRun(Universe(synthetic.Store, synthetic: true), "unit").Status);
    }

    [Fact]
    public void ReservationChecksIncludeWarmupBeforeTheEvaluatedWindow()
    {
        using var w = new Workspace();
        var study = Register(w.Store, Monthly(w.Store, 2008));
        var request = Universe(w.Store);
        Assert.True(DateOnly.FromDateTime(request.Decisions[12].UtcDateTime) > study.Plan.ReservedEnd);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() =>
            w.Store.ExecuteUniverseRun(request, "unit")).Code);
    }

    [Theory]
    [InlineData(45, false)]
    [InlineData(92, true)]
    public void StalePriceLookbackIsIncludedConservatively(int days, bool conflicts)
    {
        using var w = new Workspace();
        var study = Register(w.Store, Monthly(w.Store, 2007));
        var request = Universe(w.Store) with { MaximumPriceAgeDays = days };
        Assert.True(DateOnly.FromDateTime(request.Decisions[0].UtcDateTime) > study.Plan.ReservedEnd);
        if (conflicts)
            Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() =>
                w.Store.ExecuteUniverseRun(request, "unit")).Code);
        else Assert.Equal("succeeded", w.Store.ExecuteUniverseRun(request, "unit").Status);
    }

    [Fact]
    public void PersistedWindowsAndCodeIdentitySurviveReplayAndLegacyTrialsFailClosed()
    {
        using var w = new Workspace();
        var data = Monthly(w.Store);
        var request = Universe(w.Store);
        var entry = w.Store.ExecuteUniverseRun(request, "unit");
        Assert.Equal(DateOnly.FromDateTime(request.Decisions[0].UtcDateTime).AddDays(-45), entry.InformationStart);
        Assert.Equal(DateOnly.FromDateTime(request.Decisions[^1].UtcDateTime), entry.InformationEnd);
        Assert.Equal(ResearchIdentity.Scheme, entry.IdentityScheme);
        Assert.Equal(ResearchIdentity.Current.Sha256, entry.ResearchSha256);
        // Authored legacy row: old genuine trials retained no usable request window.
        using (var db = new SqliteConnection($"Data Source={Path.Combine(w.Root, "research.db")}"))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE universe_runs SET json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", entry.Id);
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry with
                { InformationStart = null, InformationEnd = null, IdentityScheme = null, ResearchSha256 = null }, ResearchStore.Json));
            update.ExecuteNonQuery();
        }
        Assert.Equal("study-universe-exposure-unknown", Assert.Throws<ResearchException>(() => Register(w.Store, data)).Code);
        var replay = w.Store.ExecuteUniverseRun(request, "new-code");
        Assert.Equal(entry.Id, replay.Id);
        Assert.Null(replay.InformationStart); // legacy evidence is not rewritten on replay
        Assert.Single(w.Store.ListUniverseRuns());
    }

    [Fact]
    public void EarlierSyntheticUniverseTrialDoesNotReserveGenuineStudyOutcomes()
    {
        using var w = new Workspace();
        var prior = w.Store.ExecuteUniverseRun(Universe(w.Store, synthetic: true), "unit");
        var study = Register(w.Store, Monthly(w.Store));
        Assert.Equal("development", study.Status);
        Assert.Equal(prior, Assert.Single(study.PriorUniverseTrials));
        Assert.Equal(prior, Assert.Single(w.Store.Study(study.Id)!.PriorUniverseTrials));
    }

    private static void FillUniverseTrials(Workspace w, UniverseRunEntry template, int total)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(w.Root, "research.db")}");
        db.Open(); using var tx = db.BeginTransaction(); using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO universe_runs(id,request_id,dataset_sha,json,status) VALUES($id,$request,$dataset,$json,'failed')";
        insert.Parameters.AddWithValue("$id", ""); insert.Parameters.AddWithValue("$request", "");
        insert.Parameters.AddWithValue("$dataset", template.DatasetSha256); insert.Parameters.AddWithValue("$json", "");
        for (var i = 1; i < total; i++)
        {
            var row = template with { Id = Guid.NewGuid().ToString("N"), Status = "failed", ErrorCode = "authored-capacity-fixture" };
            insert.Parameters["$id"].Value = row.Id;
            insert.Parameters["$request"].Value = Guid.NewGuid().ToString("D");
            insert.Parameters["$json"].Value = JsonSerializer.Serialize(row, ResearchStore.Json);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Fact]
    public void TrialCapacityIsSharedAcrossBothRunRegistersAndStudyRegistration()
    {
        using var w = new Workspace();
        var data = Monthly(w.Store);
        var request = Universe(w.Store, synthetic: true);
        var prior = w.Store.ExecuteUniverseRun(request, "unit");
        FillUniverseTrials(w, prior, 1000);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() =>
            w.Store.ExecuteUniverseRun(request with { RequestId = Guid.NewGuid().ToString("D") }, "unit")).Code);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() =>
            w.Store.Enqueue(new(Guid.NewGuid().ToString("D"), data.Id, new(), new()), "unit")).Code);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() => Register(w.Store, data)).Code);
        Assert.Equal(prior.Id, w.Store.ExecuteUniverseRun(request, "replay-at-capacity").Id);
    }

    [Fact]
    public void UniverseRunsCannotConsumeTheTwoReservedFinalSlots()
    {
        using var w = new Workspace();
        var study = Register(w.Store, Monthly(w.Store));
        var request = Universe(w.Store, synthetic: true);
        var prior = w.Store.ExecuteUniverseRun(request, "unit");
        // Exactly 998 existing trials plus two slots reserved for the study's final pair.
        FillUniverseTrials(w, prior, 998 - study.Plan.Candidates.Length * 2);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() =>
            w.Store.ExecuteUniverseRun(request with { RequestId = Guid.NewGuid().ToString("D") }, "unit")).Code);
    }
}
