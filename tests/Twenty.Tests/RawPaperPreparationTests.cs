using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Twenty.Core.Research;
using Twenty.Core.Data;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    [Fact]
    public void RawPaperPreparationIsDeterministicAndOnlyExplicitApplyChangesCapital()
    {
        var account = store.CreateRawPaperAccount(fixture.Create);
        var source = JsonSerializer.Serialize(store.ListRawTrials(), RawReplayJson.Options);
        var before = JsonSerializer.Serialize(account, RawReplayJson.Options);
        var prepared = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(JsonSerializer.Serialize(prepared, RawReplayJson.Options), JsonSerializer.Serialize(store.PrepareRawPaperBatch(account.Id, new(null)), RawReplayJson.Options));
        Assert.Equal(before, JsonSerializer.Serialize(store.RawPaperAccounts().Single(), RawReplayJson.Options)); Assert.Empty(store.RawPaperOperations(account.Id));
        Assert.Equal(1, prepared.Period); Assert.Equal(20, prepared.ProjectedBalance.ContributionsGbp); Assert.Equal(.25m, prepared.ProjectedBalance.FeesGbp);
        Assert.Equal(.246875m, Assert.Single(prepared.ProjectedBalance.Holdings).Value.Quantity); Assert.Equal(19.75m, prepared.ProjectedBalance.EquityGbp);
        var firstBytes = File.ReadAllBytes(Path.Combine(root, "imports", prepared.ReplaySha256 + ".raw-paper.json"));
        var first = store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), null, prepared.ReplaySha256)); Assert.Equal("succeeded", first.Status);
        var receipt = store.RawPaperReceiptBytes(first.Id);
        store.Dispose(); store = new(root);
        Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id));
        var second = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        var bytes = File.ReadAllBytes(Path.Combine(root, "imports", second.ReplaySha256 + ".raw-paper.json"));
        var appended = RawPaperContract.ValidateAppend(account.Policy, firstBytes, bytes);
        Assert.Equal(2, appended.Events.Count(e => e.Operation is RawReplayRebalanceDecision)); Assert.Equal(2, second.Period);
        Assert.Equal(39.50m, second.ProjectedBalance.EquityGbp); Assert.Equal(.49375m, Assert.Single(second.ProjectedBalance.Holdings).Value.Quantity);
        var request = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), first.ReceiptSha256, second.ReplaySha256);
        var applied = store.AdvanceRawPaperAccount(account.Id, request); Assert.Equal("succeeded", applied.Status);
        Assert.Equal(JsonSerializer.Serialize(second.ProjectedBalance, RawReplayJson.Options), JsonSerializer.Serialize(store.RawPaperAccounts().Single().Balance, RawReplayJson.Options));
        Assert.Equal(applied, store.AdvanceRawPaperAccount(account.Id, request with { RequestId = request.RequestId.ToUpperInvariant() }));
        Assert.Equal(2, store.RawPaperOperations(account.Id).Count); Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Equal(source, JsonSerializer.Serialize(store.ListRawTrials(), RawReplayJson.Options)); Assert.Null(store.RawStudies().Single().FinalRequestId);
    }

    [Fact]
    public void RawPaperPreparationExtendsAManualPrefixWithoutReadingSourceReports()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var first = store.AdvanceRawPaperAccount(account.Id, Advance(1));
        Directory.Move(Path.Combine(root, "runs"), Path.Combine(root, "runs-hidden"));
        var prepared = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        Assert.Equal(39.5m, prepared.ProjectedBalance.EquityGbp);
        var applied = store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), first.ReceiptSha256, prepared.ReplaySha256)); Assert.Equal("succeeded", applied.Status);
    }

    [Fact]
    public void RawPaperPreparationOlderIdentityRemainsReadableButCannotOpenOriginals()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var firstRequest = Advance(1); var first = store.AdvanceRawPaperAccount(account.Id, firstRequest);
        var changed = store.RawPaperAccounts().Single() with { CodeSha256 = new string('a', 64) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE raw_paper_accounts SET json=$json,sha256=$sha WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", System.Text.Encoding.UTF8.GetString(bytes)); cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(bytes))); cmd.Parameters.AddWithValue("$id", account.Id); cmd.ExecuteNonQuery();
        }
        var receipt = store.RawPaperReceiptBytes(first.Id); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "hidden-inputs"));
        Assert.Equal("study-code-changed", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, firstRequest)); Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id));
        Assert.Equal(20, store.RawPaperAccounts().Single().Balance.ContributionsGbp); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperPreparationCompletesTheFrozenGridAndStalePreviewCannotApplyAgain()
    {
        var account = store.CreateRawPaperAccount(fixture.Create with { Decisions = fixture.Create.Decisions.Take(14).ToImmutableArray() });
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var first = store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256)); Assert.Equal("succeeded", first.Status);
        Assert.Equal("completed", store.RawPaperAccounts().Single().Status);
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256))).Code);
        Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperPreparationChecksHeadAndStopBeforeOpeningOriginals()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var first = store.AdvanceRawPaperAccount(account.Id, Advance(1));
        Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "hidden-inputs"));
        Assert.Equal("raw-paper-head-conflict", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        store.StopRawPaperAccount(account.Id);
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
        Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperPreparationMissingInputAndCancellationDoNotAdmitAnAttempt()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var path = Path.Combine(root, "raw-paper-inputs"); var hidden = Path.Combine(root, "hidden-inputs"); Directory.Move(path, hidden);
        Assert.Equal("raw-paper-input-unavailable", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Directory.Move(hidden, path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.PrepareRawPaperBatch(account.Id, new(null), cancellation.Token));
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal("ready", store.RawPaperAccounts().Single().Status);
        Assert.Equal(19.75m, store.PrepareRawPaperBatch(account.Id, new(null)).ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperPreparationRefusesBusyAndQuotaWithoutAdmittingAnAttempt()
    {
        var account = store.CreateRawPaperAccount(fixture.Create);
        var begin = typeof(ResearchStore).GetMethod("TryBeginComputation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var end = typeof(ResearchStore).GetMethod("EndComputation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)begin.Invoke(store, null)!);
        try { Assert.Equal("research-worker-busy", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code); }
        finally { end.Invoke(store, null); }
        using (var pressure = File.Create(Path.Combine(root, "pressure.bin"))) pressure.SetLength(256L * 1024 * 1024);
        Assert.Equal("artifact-capacity", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.EquityGbp);
    }

    [Fact]
    public void RawPaperPreparationRefusesCorruptAcceptedReceiptWithoutChangingTheHead()
    {
        var account = store.CreateRawPaperAccount(fixture.Create); var first = store.AdvanceRawPaperAccount(account.Id, Advance(1));
        var before = JsonSerializer.Serialize(store.RawPaperAccounts(), RawReplayJson.Options);
        File.AppendAllText(Path.Combine(root, "raw-paper-operations", first.Id + ".receipt.json"), " ");
        Assert.Equal("raw-paper-integrity", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
        Assert.Equal(before, JsonSerializer.Serialize(store.RawPaperAccounts(), RawReplayJson.Options)); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperPreparationRefusesAnUnfundedOpeningGapWithoutBurningTheAccount()
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        var priceId = fixture.Full.Events.Select(e => e.Operation).OfType<RawReplayModelledFill>().First().Fill.PriceId;
        file = file with { Observations = file.Observations.Select(o => o is PriceBar p && p.Id == priceId ? p with { Open = 101, High = 101 } : o).ToImmutableArray() };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var account = store.CreateRawPaperAccount(fixture.Create with { DatasetSha256 = sha });
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal("ready", store.RawPaperAccounts().Single().Status); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "imports"), "*.raw-paper.json"));
    }

    [Fact]
    public async Task RawPaperPreparationHttpRequiresExplicitApplicationAndRejectsTuning()
    {
        var account = store.CreateRawPaperAccount(fixture.Create);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var path = "/api/research/raw-paper-accounts/" + account.Id + "/prepare";
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var response = await client.PostAsJsonAsync(path, new { expectedReceiptSha256 = (string?)null });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var preview = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var sha = preview.RootElement.GetProperty("replaySha256").GetString()!;
            Assert.Equal(19.75m, preview.RootElement.GetProperty("projectedBalance").GetProperty("equityGbp").GetDecimal());
            Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
            using var missing = await client.PostAsJsonAsync(path, new { }); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var tuning = await client.PostAsJsonAsync(path, new { expectedReceiptSha256 = (string?)null, monthlyGbp = 999 }); Assert.Equal(HttpStatusCode.BadRequest, tuning.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Twenty-Request");
            using var denied = await client.PostAsJsonAsync(path, new { expectedReceiptSha256 = (string?)null }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1"); client.DefaultRequestHeaders.Add("Origin", "https://other.example");
            using var foreign = await client.PostAsJsonAsync(path, new { expectedReceiptSha256 = (string?)null }); Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            using var applied = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/advance", new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), null, sha));
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode); Assert.Equal("succeeded", (await applied.Content.ReadFromJsonAsync<RawPaperOperation>(RawReplayJson.Options))!.Status);
            Assert.Equal(19.75m, store.RawPaperAccounts().Single().Balance.EquityGbp);
            using var capabilities = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts")); Assert.True(capabilities.RootElement.GetProperty("preparationAvailable").GetBoolean());
            Assert.Null(store.RawStudies().Single().FinalRequestId);
        }
        finally { await app.StopAsync(); }
    }
}
