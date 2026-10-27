using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    internal static (RawResearchStudy Study, string Root) CreatePaperSource()
    {
        var fixture = new RawStudyWorkerTests();
        try
        {
            var f = RawStudyDevelopmentRunTests.Fixture(.25m);
            var study = fixture.RegisterFixture((f.Plan, f.Dataset, f.Calendar, "[]"u8.ToArray()));
            fixture.store.StartRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256));
            for (var i = 0; i < 8; i++) Assert.True(fixture.store.ProcessNextRawStudyRun("authored-paper"));
            study = fixture.store.SelectRawStudy(study.Id, new(Guid.NewGuid().ToString("D"), study.PlanSha256), "authored-paper");
            fixture.store.Dispose();
            var target = fixture.root + "-paper-source"; Directory.Move(fixture.root, target); return (study, target);
        }
        finally { fixture.store.Dispose(); if (Directory.Exists(fixture.root)) Directory.Delete(fixture.root, true); }
    }
}
public sealed class RawPaperFixture : IDisposable
{
    public string Root { get; }
    public RawResearchStudy Study { get; }
    public RawPaperCreateRequest Create { get; }
    public RawReplayDocument Full { get; }
    public RawPaperFixture()
    {
        (Study, Root) = RawStudyWorkerTests.CreatePaperSource();
        var f = RawStudyDevelopmentRunTests.Fixture(.25m); var file = DatasetJson.Read(f.Dataset); var shift = TimeSpan.FromDays(3652);
        Observation Move(Observation o)
        {
            var evidence = o.Evidence with { PublishedAt = o.Evidence.PublishedAt + shift, AvailableAt = o.Evidence.AvailableAt + shift };
            return o switch
            {
                Listing l => l with { Evidence = evidence, EffectiveFrom = l.EffectiveFrom + shift, EffectiveUntil = l.EffectiveUntil + shift },
                Membership m => m with { Evidence = evidence, EffectiveFrom = m.EffectiveFrom + shift, EffectiveUntil = m.EffectiveUntil + shift },
                PriceBar p => p with { Evidence = evidence, SessionDate = p.SessionDate.AddDays(3652), SessionCloseAt = p.SessionCloseAt + shift },
                FxRate r => r with { Evidence = evidence, ObservedAt = r.ObservedAt + shift },
                _ => throw new InvalidOperationException("Unexpected authored observation")
            };
        }
        file = file with
        {
            Manifest = file.Manifest with
            {
                DatasetId = "authored-paper",
                CreatedAt = file.Manifest.CreatedAt + shift,
                Sources = file.Manifest.Sources.Select(s => s with { RetrievedAt = s.RetrievedAt + shift, FirstSeenAt = s.FirstSeenAt + shift }).ToImmutableArray()
            },
            Observations = file.Observations.Select(Move).ToImmutableArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        using var store = new ResearchStore(Root); var sha = store.ImportUniverseDataset(bytes);
        var calendar = new ExecutionCalendar(f.Calendar.Reference, f.Calendar.Venue, f.Calendar.Days.Select(d => new ExecutionDay(
            d.Date.AddDays(3652), d.OpenAt + shift, d.CloseAt + shift)).ToImmutableArray(), "urn:twenty:authored-paper");
        var pins = ImmutableArray.Create(new RawStudyCalendarPin(calendar.Reference.Id, calendar.Venue, calendar.Sha256));
        var plan = RawUniverseStudyProtocol.Declare(sha, f.Plan.UniverseId, true, "engineering", ResearchIdentity.Current.Sha256,
            ResearchIdentity.Scheme, f.Plan.Decisions.Select(d => d + shift).ToImmutableArray(), 20, f.Plan.BaseExecution, pins, f.Plan.ActionCatalogSha256);
        var report = RawStudyDevelopmentRun.Run(plan, "momentum:1:base", bytes,
            new Dictionary<string, ExecutionCalendar> { [calendar.Reference.Id] = calendar });
        Full = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)! with { Study = null };
        File.WriteAllBytes(Path.Combine(Root, "imports", calendar.Sha256 + ".calendar.json"), JsonSerializer.SerializeToUtf8Bytes(calendar, RawReplayJson.Options));
        Create = new(Guid.NewGuid().ToString("D"), Study.Id, Study.PlanSha256, Study.SelectionReportSha256!, "base", sha, pins, Full.Events[0].At, plan.DevelopmentDecisions);
    }
    public void Dispose() => Directory.Delete(Root, true);
}
public sealed class RawPaperStoreTests : IClassFixture<RawPaperFixture>, IDisposable
{
    private readonly RawPaperFixture fixture;
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-paper-" + Guid.NewGuid().ToString("N"));
    private ResearchStore store;
    public RawPaperStoreTests(RawPaperFixture fixture)
    {
        this.fixture = fixture;
        foreach (var directory in Directory.GetDirectories(fixture.Root, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(fixture.Root, root));
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(fixture.Root, root));
        store = new(root);
    }
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private RawPaperAdvanceRequest Advance(int periods, string? head = null, string? requestId = null, RawReplayDocument? changed = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed ?? RawPaperContractTests.Periods(fixture.Full, periods), RawReplayJson.Options);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(root, "imports", sha + ".raw-paper.json"), bytes);
        return new(requestId ?? Guid.NewGuid().ToString("D"), head, sha);
    }
    [Fact]
    public void RawPaperHasItsOwnUsdSharesGbpCashAndExactColdOperationReplay()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); Assert.Equal(0, account.Balance.ContributionsGbp);
        var request = Advance(1); var first = store.AdvanceRawPaperAccount(account.Id, request);
        Assert.Equal("succeeded", first.Status);
        var current = Assert.Single(store.RawPaperAccounts()); Assert.Equal(20, current.Balance.ContributionsGbp);
        Assert.Equal(0, current.Balance.CashGbp); Assert.Equal(.246875m, Assert.Single(current.Balance.Holdings).Value.Quantity); Assert.Equal(.25m, current.Balance.FeesGbp); Assert.Equal(19.75m, current.Balance.EquityGbp);
        var receipt = store.RawPaperReceiptBytes(first.Id);
        var second = store.AdvanceRawPaperAccount(account.Id, Advance(2, first.ReceiptSha256)); Assert.Equal("succeeded", second.Status);
        current = Assert.Single(store.RawPaperAccounts()); Assert.Equal(40, current.Balance.ContributionsGbp);
        Assert.Equal(.49375m, Assert.Single(current.Balance.Holdings).Value.Quantity); Assert.Equal(.5m, current.Balance.FeesGbp); Assert.Equal(39.5m, current.Balance.EquityGbp);
        Assert.Equal(8, store.ListRawTrials().Count); Assert.Null(store.RawStudies().Single().FinalRequestId);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "originals-hidden")); store = new(root);
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, request)); Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id));
        Assert.Equal(account.Id, store.CreateRawPaperAccount(fixture.Create).Id);
        Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }
    [Theory]
    [InlineData("prefix")]
    [InlineData("deposit")]
    [InlineData("policy")]
    public void RawPaperRetainsRefusalsAndCannotReplaceAFailedOperation(string mutation)
    {
        var account = store.CreateRawPaperAccount(fixture.Create);
        var first = store.AdvanceRawPaperAccount(account.Id, Advance(1));
        var full = RawPaperContractTests.Periods(fixture.Full, 2); var events = full.Events;
        var offset = RawPaperContractTests.Periods(fixture.Full, 1).Events.Length;
        if (mutation == "prefix") events = events.SetItem(0, events[0] with { Id = "rewrite" });
        if (mutation == "deposit") events = events.SetItem(offset, events[offset] with { Operation = new RawReplayDeposit(999) });
        if (mutation == "policy") { var d = (RawReplayRebalanceDecision)events[offset + 2].Operation; events = events.SetItem(offset + 2, events[offset + 2] with { Operation = d with { Plan = d.Plan with { FeePerFillGbp = 1 } } }); }
        var request = Advance(2, first.ReceiptSha256, changed: full with { Events = events });
        var failed = store.AdvanceRawPaperAccount(account.Id, request); Assert.Equal("failed", failed.Status); Assert.NotNull(failed.ErrorCode);
        var current = store.RawPaperAccounts().Single(); Assert.Equal("failed", current.Status); Assert.Equal(20, current.Balance.ContributionsGbp);
        Assert.Equal(failed, store.AdvanceRawPaperAccount(account.Id, request));
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, Advance(2, first.ReceiptSha256))).Code);
        Assert.Equal(2, store.RawPaperOperations(account.Id).Count);
    }
    [Fact]
    public void RawPaperPinsUuidAliasesHeadsOneAccountAndStopWithoutDoubleBooking()
    {
        var account = store.CreateRawPaperAccount(fixture.Create);
        Assert.Equal(JsonSerializer.Serialize(account, RawReplayJson.Options), JsonSerializer.Serialize(store.CreateRawPaperAccount(fixture.Create with { RequestId = fixture.Create.RequestId.ToUpperInvariant() }), RawReplayJson.Options));
        Assert.Equal("raw-paper-already-created", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(fixture.Create with { RequestId = Guid.NewGuid().ToString("D") })).Code);
        var request = Advance(1); var first = store.AdvanceRawPaperAccount(account.Id, request);
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, request with { RequestId = request.RequestId.ToUpperInvariant() }));
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, request with { ReplaySha256 = new string('b', 64) })).Code);
        Assert.Equal("raw-paper-head-conflict", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, Advance(2))).Code);
        Assert.Single(store.RawPaperOperations(account.Id)); Assert.Equal(20, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        var stopped = store.StopRawPaperAccount(account.Id); Assert.Equal("stopped", stopped.Status); Assert.Equal(JsonSerializer.Serialize(stopped, RawReplayJson.Options), JsonSerializer.Serialize(store.StopRawPaperAccount(account.Id), RawReplayJson.Options));
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, request));
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, Advance(2, first.ReceiptSha256))).Code);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperRefusesGenuineFlagsAndCorporateActionsBeforeCreatingAnAccount(bool action)
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        if (action)
        {
            var bar = file.Observations.OfType<PriceBar>().First(); var at = fixture.Create.FirstDecisionAt.AddDays(2);
            var entry = new CorporateAction("authored-income", bar.Evidence with { PublishedAt = at, AvailableAt = at }, bar.ListingId, "income", CorporateActionKind.Dividend, at, null, 1, "USD", null, null, TerminalOutcome.NotApplicable);
            file = file with { Observations = file.Observations.Add(entry) };
        }
        else file = file with { Manifest = file.Manifest with { Synthetic = false } };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        if (!action) File.Move(Path.Combine(root, "universe-datasets", sha + ".json"), Path.Combine(root, "universe-datasets", sha + ".hidden"));
        Assert.Equal(action ? "raw-paper-actions-unsupported" : "raw-paper-authored-only", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(fixture.Create with { DatasetSha256 = sha })).Code);
        Assert.Empty(store.RawPaperAccounts());
    }
    [Fact]
    public void RawPaperRefusesSharedBusyWorkerAndQuotaBeforeAnyOperationAttempt()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var request = Advance(1);
        var begin = typeof(ResearchStore).GetMethod("TryBeginComputation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var end = typeof(ResearchStore).GetMethod("EndComputation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)begin.Invoke(store, null)!);
        try { Assert.Equal("research-worker-busy", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, request)).Code); }
        finally { end.Invoke(store, null); }
        using (var pressure = File.Create(Path.Combine(root, "pressure.bin"))) pressure.SetLength(256L * 1024 * 1024);
        Assert.Equal("artifact-capacity", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, request)).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }

    [Fact]
    public void RawPaperInterruptedPublicationKeepsThePriorAcceptedBalanceAndAllAttempts()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var first = store.AdvanceRawPaperAccount(account.Id, Advance(1));
        var secondRequest = Advance(2, first.ReceiptSha256);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER refuse_paper_success BEFORE UPDATE ON raw_paper_operations WHEN json_extract(NEW.json,'$.status')='succeeded' BEGIN SELECT RAISE(FAIL,'authored-publication-fault');END"; cmd.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => store.AdvanceRawPaperAccount(account.Id, secondRequest));
        Assert.Equal("running", store.RawPaperAccounts().Single().Status); Assert.Equal(20, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        store.Dispose(); store = new(root);
        var recovered = store.RawPaperAccounts().Single(); Assert.Equal("failed", recovered.Status); Assert.Equal(first.ReceiptSha256, recovered.HeadReceiptSha256);
        Assert.Equal(20, recovered.Balance.ContributionsGbp); Assert.Equal(.246875m, Assert.Single(recovered.Balance.Holdings).Value.Quantity);
        var interrupted = store.AdvanceRawPaperAccount(account.Id, secondRequest); Assert.Equal("interrupted", interrupted.Status); Assert.Equal("process-restarted", interrupted.ErrorCode);
        Assert.Equal(2, store.RawPaperOperations(account.Id).Count); Assert.NotEmpty(store.RawPaperReceiptBytes(first.Id));
    }
    [Fact]
    public void RawPaperCompletesExactlyItsDeclaredGrid()
    {
        var account = store.CreateRawPaperAccount(fixture.Create with { Decisions = fixture.Create.Decisions.Take(14).ToImmutableArray() });
        var first = store.AdvanceRawPaperAccount(account.Id, Advance(1)); Assert.Equal("succeeded", first.Status);
        var completed = store.RawPaperAccounts().Single(); Assert.Equal("completed", completed.Status); Assert.Equal(1, completed.AcceptedPeriods);
        Assert.Equal("completed", store.StopRawPaperAccount(account.Id).Status);
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, Advance(2, first.ReceiptSha256))).Code);
    }

    [Fact]
    public async Task RawPaperHttpPreservesTheWriteGuardAndExposesOnlyExplicitReceipts()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16384);
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var denied = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", fixture.Create); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(store.RawPaperAccounts()); client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var created = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", fixture.Create); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var account = await created.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options); Assert.NotNull(account);
            using var advanced = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/advance", Advance(1));
            Assert.Equal(HttpStatusCode.OK, advanced.StatusCode); var operation = await advanced.Content.ReadFromJsonAsync<RawPaperOperation>(RawReplayJson.Options); Assert.Equal("succeeded", operation!.Status);
            Assert.Equal(store.RawPaperReceiptBytes(operation.Id), await client.GetByteArrayAsync("/api/research/raw-paper-operations/" + operation.Id + "/receipt"));
            using var body = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts"));
            Assert.True(body.RootElement.GetProperty("authoredOnly").GetBoolean()); Assert.False(body.RootElement.GetProperty("corporateActions").GetBoolean());
            Assert.False(body.RootElement.GetProperty("asynchronousFills").GetBoolean()); Assert.Null(store.RawStudies().Single().FinalRequestId);
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site"); using var refused = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/stop", new { });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); Assert.Equal("active", store.RawPaperAccounts().Single().Status);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void RawPaperDoesNotOpenAnUnattestedLegacyOriginal()
    {
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM universe_dataset_provenance WHERE dataset_sha=$sha"; cmd.Parameters.AddWithValue("$sha", fixture.Create.DatasetSha256); cmd.ExecuteNonQuery(); }
        File.Move(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json"), Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".hidden"));
        Assert.Equal("raw-paper-dataset-unattested", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(fixture.Create)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

}
