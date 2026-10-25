using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

// False synthetic flags exercise guards with authored fixtures, not historical evidence.
public sealed class RawStudyAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-raw-study-" + Guid.NewGuid().ToString("N"));
    private ResearchStore store;
    public RawStudyAdmissionTests() => store = new(root);
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private static ImmutableArray<DateTimeOffset> Grid => Enumerable.Range(0, 73)
        .Select(i => new DateTimeOffset(2010, 2, 1, 23, 0, 0, TimeSpan.Zero).AddMonths(i).AddDays(-1)).ToImmutableArray();
    private RawStudyRegistrationRequest Request(bool synthetic = true)
    {
        var file = DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes());
        file = file with { Manifest = file.Manifest with { Synthetic = synthetic } };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var days = Enumerable.Range(0, 2300).Select(i => new DateOnly(2010, 1, 1).AddDays(i))
            .Select(d => new ExecutionDay(d, new DateTimeOffset(d.Year, d.Month, d.Day, 14, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(d.Year, d.Month, d.Day, 21, 0, 0, TimeSpan.Zero))).ToImmutableArray();
        var calendar = new ExecutionCalendar(file.Manifest.Calendars.Single(), "XNAS", days, "urn:twenty:authored:admission");
        var calendarBytes = JsonSerializer.SerializeToUtf8Bytes(calendar, RawReplayJson.Options);
        var actionBytes = JsonSerializer.SerializeToUtf8Bytes(ImmutableArray<RawActionSchedule>.Empty, RawReplayJson.Options);
        var inbox = Path.Combine(root, "imports"); Directory.CreateDirectory(inbox);
        static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
        File.WriteAllBytes(Path.Combine(inbox, Hash(actionBytes) + ".actions.json"), actionBytes);
        // Pins name the canonical table; admission separately freezes the wire hash.
        File.WriteAllBytes(Path.Combine(inbox, calendar.Sha256 + ".calendar.json"), calendarBytes);
        var plan = RawUniverseStudyProtocol.Declare(sha, "research-universe", synthetic, synthetic ? "engineering" : "research",
            ResearchIdentity.Current.Sha256, ResearchIdentity.Scheme, Grid, 20, new(45, 2, 5, 1, new()),
            [new(calendar.Reference.Id, calendar.Venue, calendar.Sha256)], Hash(actionBytes));
        return new(Guid.NewGuid().ToString("D"), "Authored raw declaration", Convert.ToBase64String(RawUniverseStudyProtocol.Encode(plan)));
    }

    [Fact]
    public void AdmissionRetainsAllEightUnstartedTrialsAndRetryReturnsTheOriginalStudy()
    {
        var request = Request(); var study = store.RegisterRawStudy(request, "authored");
        Assert.Equal("registered", study.Status); Assert.Equal(8, store.ListRawTrials().Count);
        Assert.All(store.ListRawTrials(), t => Assert.Equal("planned", t.Status));
        Assert.Equal(study.Id, store.RegisterRawStudy(request, "other-host").Id);
        Assert.Equal(request.PlanBase64, study.PlanBase64);
        Assert.Equal(1, JsonSerializer.SerializeToElement(store.RawStudyEvents(study.Id)).GetArrayLength());
        Assert.All(store.ListRawTrials(), t => Assert.NotEmpty(store.RawTrialRequestBytes(t.Id)));
        foreach (var original in study.Originals) Assert.Equal(original.Bytes, store.RawStudyOriginalBytes(study.Id, original.OriginalSha256).Length);
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request with { Name = "different" }, "authored")).Code);
    }

    [Fact]
    public void SqlFailureRollsBackTheWholeFamilyAndLeavesTheRequestReusable()
    {
        var request = Request();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_raw_family BEFORE INSERT ON raw_trials WHEN (SELECT COUNT(*) FROM raw_trials)=2 BEGIN SELECT RAISE(FAIL,'authored-admission-failure'); END";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => store.RegisterRawStudy(request, "authored"));
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TRIGGER fail_raw_family"; cmd.ExecuteNonQuery(); }
        Assert.Equal(8, store.RegisterRawStudy(request, "authored").DevelopmentTrialIds.Length);
    }

    [Fact]
    public void MissingOriginalInputsCannotCommitADeclaration()
    {
        var request = Request(); Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.Equal("raw-study-input-unavailable", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

    [Fact]
    public void RegisteredGenuineRawReservationBlocksUniverseAndModelWork()
    {
        var raw = Request(false); store.RegisterRawStudy(raw, "authored");
        var file = DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes());
        file = file with { Manifest = file.Manifest with { Synthetic = false } };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.ExecuteUniverseRun(
            new(Guid.NewGuid().ToString("D"), sha, "research-universe", Grid, 45, new(), new()), "authored")).Code);
        var model = UniverseStudyGuardTests.Monthly(store);
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.RegisterStudy(
            new(Guid.NewGuid().ToString("D"), model.Id, "Overlap", "research", 20, new()), "authored")).Code);
    }

    [Fact]
    public void ExistingGenuineStudyReservationBlocksRawAdmission()
    {
        var model = UniverseStudyGuardTests.Monthly(store, 2001);
        store.RegisterStudy(new(Guid.NewGuid().ToString("D"), model.Id, "Reserved", "research", 20, new()), "authored");
        Assert.Equal("study-reserved-window-conflict", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(Request(false), "authored")).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

    [Fact]
    public void RestartPreservesOriginalPlanAndDoesNotStartOrReplaceTrials()
    {
        var request = Request(); var study = store.RegisterRawStudy(request, "authored");
        var ids = store.ListRawTrials().Select(t => t.Id).ToArray(); store.Dispose(); store = new(root);
        Assert.Equal(study.PlanBase64, store.RawStudies().Single().PlanBase64);
        Assert.Equal(ids, store.ListRawTrials().Select(t => t.Id));
        Assert.Equal(study.Id, store.RegisterRawStudy(request, "after-restart").Id);
    }

    [Fact]
    public void CancellationReleasesTheActiveReservationButRetainsTrialsAndRetryIdentity()
    {
        var request = Request(); var study = store.RegisterRawStudy(request, "authored");
        var cancelled = store.CancelRawStudy(study.Id);
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal(study.PlanBase64, cancelled.PlanBase64);
        Assert.All(store.ListRawTrials(), t => Assert.Equal("cancelled", t.Status));
        Assert.Equal("cancelled", store.RegisterRawStudy(request, "retry").Status);
        Assert.Equal(cancelled.Id, store.CancelRawStudy(study.Id).Id);
        var slots = typeof(ResearchStore).GetMethod("ReservedStudySlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(0, slots.Invoke(store, null));
    }

    [Fact]
    public void RegistrationSurvivesInboxRemovalButRetainedInputTamperingIsRefused()
    {
        var request = Request(); var study = store.RegisterRawStudy(request, "authored");
        Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.Equal(study.Id, store.RegisterRawStudy(request, "retry").Id);
        var input = study.Originals.Single(i => i.Kind == "actions");
        var path = Path.Combine(root, "raw-study-inputs", "actions", input.OriginalSha256 + ".json");
        File.WriteAllText(path, "{}");
        Assert.Equal("raw-study-input-integrity", Assert.Throws<ResearchException>(() => store.RawStudyOriginalBytes(study.Id, input.OriginalSha256)).Code);
    }

    [Fact]
    public void ExistingStudiesDiscloseEveryPriorRawAttemptIncludingCancelledTrials()
    {
        var first = store.RegisterRawStudy(Request(), "authored"); store.CancelRawStudy(first.Id);
        var second = store.RegisterRawStudy(Request(), "authored"); Assert.Equal(8, second.PriorRawTrials.Length);
        var file = DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes());
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var universe = store.RegisterUniverseStudy(new(Guid.NewGuid().ToString("D"), "Disclosure", "engineering", sha, "research-universe", Grid, 45, 20, new()), "authored");
        Assert.Equal(16, universe.PriorRawTrials.Length);
        Assert.Equal(8, universe.PriorRawTrials.Count(t => t.Status == "cancelled"));
    }

    [Fact]
    public void FailureDuringOriginalPersistenceCannotCommitTheFamily()
    {
        var request = Request(); File.WriteAllText(Path.Combine(root, "raw-study-inputs"), "block directory creation");
        Assert.ThrowsAny<IOException>(() => store.RegisterRawStudy(request, "authored"));
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

    [Fact]
    public void RawRegistrationsShareTheSixteenStudyLimitWithOlderEngines()
    {
        for (var i = 0; i < 16; i++) _ = store.RegisterRawStudy(Request(), "authored");
        Assert.Equal("study-capacity", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(Request(), "authored")).Code);
        var model = UniverseStudyGuardTests.Monthly(store, 2001);
        Assert.Equal("study-capacity", Assert.Throws<ResearchException>(() => store.RegisterStudy(
            new(Guid.NewGuid().ToString("D"), model.Id, "Full registry", "research", 20, new()), "authored")).Code);
    }

    [Fact]
    public void RawAdmissionReservesItsTwoFinalSlotsWithinTheSharedThousandTrialBudget()
    {
        var request = Request();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            var model = new RunEntry("seed", "dataset", "Authored capacity row", true, "failed", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, 0,
                "authored", "unit", new string('a',64), new(), new(), false);
            cmd.CommandText = "INSERT INTO runs VALUES($id,$request,'dataset',$json,'failed')";
            for(var i=0;i<991;i++) { cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id","seed-"+i); cmd.Parameters.AddWithValue("$request","request-"+i);
                cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(model with { Id="seed-"+i },ResearchStore.Json)); cmd.ExecuteNonQuery(); }
        }
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }

    [Fact]
    public void MissingAdmittedTrialRowsCannotSilentlyFreeLegacyCapacity()
    {
        store.RegisterRawStudy(Request(), "authored");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd=db.CreateCommand(); cmd.CommandText="DELETE FROM raw_trials WHERE rowid=(SELECT MIN(rowid) FROM raw_trials)"; cmd.ExecuteNonQuery(); }
        Assert.Equal("raw-trial-integrity", Assert.Throws<ResearchException>(()=>store.ListRawTrials()).Code);
        var sha = store.ImportUniverseDataset(UniverseDiagnosticTests.DatasetBytes());
        Assert.Equal("raw-trial-integrity", Assert.Throws<ResearchException>(() => store.RegisterUniverseStudy(
            new(Guid.NewGuid().ToString("D"), "Corrupt registry", "engineering", sha, "research-universe", Grid, 45, 20, new()), "authored")).Code);
    }

    [Fact]
    public async Task HttpAdmissionKeepsTheWriteGuardAndReportsTheThirdTrialRegister()
    {
        var request = Request(); var planBytes = Convert.FromBase64String(request.PlanBase64!);
        var sha = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        File.WriteAllBytes(Path.Combine(root, "imports", sha + ".raw-study.json"), planBytes);
        var inboxRequest = new RawStudyInboxRequest(request.RequestId, request.Name, sha);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16384);
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            using var denied = await client.PostAsJsonAsync("/api/research/raw-studies", inboxRequest);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Empty(store.RawStudies());
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var admitted = await client.PostAsJsonAsync("/api/research/raw-studies", inboxRequest);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            using var body = JsonDocument.Parse(await admitted.Content.ReadAsByteArrayAsync()); var id = body.RootElement.GetProperty("id").GetString();
            Assert.Equal(Convert.FromBase64String(request.PlanBase64!), await client.GetByteArrayAsync("/api/research/raw-studies/" + id + "/plan"));
            using var stats = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/trials"));
            Assert.Equal(8, stats.RootElement.GetProperty("total").GetInt32()); Assert.Equal(8, stats.RootElement.GetProperty("rawTrials").GetInt32());
            using var history = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-studies"));
            Assert.False(history.RootElement.GetProperty("executionAvailable").GetBoolean());
            Assert.False(history.RootElement.GetProperty("finalEvaluationAvailable").GetBoolean());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task DiagnosticsDiscloseRawTrialsWithoutTreatingThemAsComparableReturns()
    {
        store.RegisterRawStudy(Request(), "authored"); var sha = store.ImportUniverseDataset(UniverseDiagnosticTests.DatasetBytes());
        var a = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), sha, "research-universe", Grid, 45, new(TopK: 1), new()), "authored");
        var b = store.ExecuteUniverseRun(new(Guid.NewGuid().ToString("D"), sha, "research-universe", Grid, 45, new(TopK: 2), new()), "authored");
        Assert.Equal("succeeded", a.Status); Assert.Equal("succeeded", b.Status);
        var diagnostic = await new DiagnosticStore(store).Create(new(Guid.NewGuid().ToString("D"), a.Id));
        Assert.Equal("succeeded", diagnostic.Status); Assert.Equal(10, diagnostic.Trials.Length);
        var raw = diagnostic.Trials.Where(t => t.ResearchEngine == "raw-universe").ToArray();
        Assert.Equal(8, raw.Length); Assert.All(raw, t => { Assert.False(t.Included); Assert.Equal("different-research-engine", t.Reason); });
    }

    [Fact]
    public void ModelAdmissionCountsRawTrialsAndTheirReservedFinalSlots()
    {
        store.RegisterRawStudy(Request(), "authored"); var data = UniverseStudyGuardTests.Monthly(store);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            var row = new RunEntry("seed", "dataset", "Authored capacity row", true, "failed", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow,
                0, "authored", "unit", new string('a',64), new(), new(), false);
            cmd.CommandText = "INSERT INTO runs VALUES($id,$request,'dataset',$json,'failed')";
            for(var i=0;i<989;i++) { cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id","seed-"+i); cmd.Parameters.AddWithValue("$request","request-"+i);
                cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row with { Id="seed-"+i },ResearchStore.Json)); cmd.ExecuteNonQuery(); }
        }
        Assert.Equal("queued", store.Enqueue(new(Guid.NewGuid().ToString("D"), data.Id, new(), new()), "authored").Status);
        Assert.Equal("trial-capacity", Assert.Throws<ResearchException>(() => store.Enqueue(new(Guid.NewGuid().ToString("D"), data.Id, new(), new()), "authored")).Code);
    }

    [Fact]
    public void ActualDatasetProvenanceOverridesAnOtherwiseConsistentClientDeclaration()
    {
        var request = Request(); var p = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(request.PlanBase64!));
        p = p with { Synthetic = false, Purpose = "research" };
        request = request with { PlanBase64 = Convert.ToBase64String(RawUniverseStudyProtocol.Encode(p)) };
        Assert.Equal("raw-study-provenance", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
        Assert.Empty(store.RawStudies());
    }

    [Fact]
    public void ChangedScientificIdentityCannotAdmitNewWork()
    {
        var request = Request(); var p = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(request.PlanBase64!));
        p = p with { CodeSha256 = new string('f', 64) };
        request = request with { PlanBase64 = Convert.ToBase64String(RawUniverseStudyProtocol.Encode(p)) };
        Assert.Equal("study-code-changed", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
    }

    [Fact]
    public void AValidDifferentCalendarCannotReplaceThePinnedTable()
    {
        var request = Request(); var p = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(request.PlanBase64!));
        var path = Path.Combine(root, "imports", p.Calendars[0].Sha256 + ".calendar.json");
        var original = RawStudyEvidence.Calendar(File.ReadAllBytes(path));
        var changed = new ExecutionCalendar(original.Reference, original.Venue, original.Days, original.SourceReference + ":changed");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options));
        Assert.Equal("raw-study-calendar-binding", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
    }

    [Fact]
    public void ActionCatalogEntriesMustResolveToTheOriginalDataset()
    {
        var request = Request(); var p = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(request.PlanBase64!));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ImmutableArray.Create<RawActionSchedule>(
            new RawSplitSchedule("aaa", "unverified", "missing", new("capture", 0, p.DatasetSha256))), RawReplayJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); File.WriteAllBytes(Path.Combine(root, "imports", hash + ".actions.json"), bytes);
        p = p with { ActionCatalogSha256 = hash }; request = request with { PlanBase64 = Convert.ToBase64String(RawUniverseStudyProtocol.Encode(p)) };
        Assert.Equal("raw-study-action-binding", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
        Assert.Empty(store.ListRawTrials());
    }

    [Fact]
    public void PermanentClaimsFromAnOlderEngineAlsoBlockRawAdmission()
    {
        var request = Request(false);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO study_holdout_claims VALUES('old-study','old-request','2014-01-01','2016-02-01',0,'2017-01-01T00:00:00.0000000+00:00')"; cmd.ExecuteNonQuery(); }
        Assert.Equal("study-holdout-spent", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(request, "authored")).Code);
        Assert.Empty(store.RawStudies());
    }

    [Fact]
    public async Task ConcurrentRetriesCannotAdmitTwoFamilies()
    {
        var request = Request();
        var rows = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.RegisterRawStudy(request, "authored"))));
        Assert.Single(rows.Select(s => s.Id).Distinct(StringComparer.Ordinal));
        Assert.Single(store.RawStudies()); Assert.Equal(8, store.ListRawTrials().Count);
    }

    [Fact]
    public void AuthoredOlderBuildSnapshotRemainsInspectableButCannotAuthorizeNewAdmission()
    {
        var request = Request(); var study = store.RegisterRawStudy(request, "authored"); var trials = store.ListRawTrials();
        // A coherent restored snapshot with an authored older build, not a claim
        // that a real prior raw-study binary has completed migration acceptance.
        var plan = study.Plan with { Build = study.Plan.Build with { CoreSha256 = new string('f', 64) } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options);
        static string Hash(byte[] input) => Convert.ToHexStringLower(SHA256.HashData(input));
        var sha = Hash(bytes);
        study = study with { Plan = plan, PlanSha256 = sha, PlanBase64 = Convert.ToBase64String(bytes),
            RequestSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(new { study.Name, PlanSha256 = sha }, RawReplayJson.Options)) };
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            var stored = JsonSerializer.SerializeToUtf8Bytes(study, RawReplayJson.Options);
            cmd.CommandText = "UPDATE raw_studies SET json=$json,sha256=$sha"; cmd.Parameters.AddWithValue("$json", System.Text.Encoding.UTF8.GetString(stored));
            cmd.Parameters.AddWithValue("$sha", Hash(stored)); cmd.ExecuteNonQuery();
            foreach (var trial in trials)
            {
                var input = JsonSerializer.SerializeToUtf8Bytes(new RawStudyTrialRequest(study.Id, sha, trial.TrialId, "development"), RawReplayJson.Options);
                File.WriteAllBytes(Path.Combine(root, "runs", trial.Id, "raw-study-request.json"), input);
                cmd.Parameters.Clear(); cmd.CommandText = "UPDATE raw_trials SET json=$json WHERE id=$id";
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(trial with { RequestSha256 = Hash(input) }, RawReplayJson.Options));
                cmd.Parameters.AddWithValue("$id", trial.Id); cmd.ExecuteNonQuery();
            }
        }
        store.Dispose(); store = new(root);
        var retry = request with { PlanBase64 = study.PlanBase64 };
        Assert.Equal(study.PlanBase64, store.RawStudies().Single().PlanBase64);
        Assert.All(store.ListRawTrials(), t => Assert.NotEmpty(store.RawTrialRequestBytes(t.Id)));
        Assert.Equal(study.Id, store.RegisterRawStudy(retry, "new-host").Id);
        Assert.Equal("raw-study-build", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(retry with
            { RequestId = Guid.NewGuid().ToString("D") }, "new-host")).Code);
    }

    [Fact]
    public void HashAddressedPlanAdmissionReplaysWithoutRereadingTheInbox()
    {
        var direct = Request(); var bytes = Convert.FromBase64String(direct.PlanBase64!);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(root, "imports", sha + ".raw-study.json"), bytes);
        var request = new RawStudyRegistrationRequest(direct.RequestId, direct.Name, PlanSha256: sha);
        var study = store.RegisterRawStudy(request, "authored"); Directory.Delete(Path.Combine(root, "imports"), true);
        Assert.Equal(study.Id, store.RegisterRawStudy(request, "retry").Id);
        Assert.Equal(study.Id, store.RegisterRawStudy(direct, "direct-byte-retry").Id);
        Assert.Equal(8, store.ListRawTrials().Count);
    }

    [Fact]
    public void InboxPlanCannotSubstituteBytesUnderTheDeclaredDigest()
    {
        var direct = Request(); var sha = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(direct.PlanBase64!)));
        File.WriteAllText(Path.Combine(root, "imports", sha + ".raw-study.json"), "{}");
        Assert.Equal("raw-study-plan-integrity", Assert.Throws<ResearchException>(() => store.RegisterRawStudy(
            new(direct.RequestId, direct.Name, PlanSha256: sha), "authored")).Code);
        Assert.Empty(store.RawStudies()); Assert.Empty(store.ListRawTrials());
    }
}
