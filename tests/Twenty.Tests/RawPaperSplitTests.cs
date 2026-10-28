using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    private RawPaperOperation ApplyPrepared(RawPaperAccount account, RawPreparedPaperBatch preview) =>
        store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), preview.ExpectedReceiptSha256, preview.ReplaySha256));

    private RawPaperCreateRequest SplitRequest(DateTimeOffset? effective = null, DateTimeOffset? disclosed = null)
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        var bar = file.Observations.OfType<PriceBar>().First(p => p.Basis == PriceBasis.Raw);
        var at = effective ?? fixture.Create.FirstDecisionAt.AddDays(5); var available = disclosed ?? at;
        var action = new CorporateAction("paper-split-observation", bar.Evidence with { PublishedAt = available, AvailableAt = available },
            bar.ListingId, "paper-split", CorporateActionKind.Split, at, 2, null, null, null, null, TerminalOutcome.NotApplicable);
        file = file with { Observations = file.Observations.Select(o => o is PriceBar p && p.Basis == PriceBasis.Raw && p.SessionCloseAt >= at
            ? p with { Open = p.Open / 2, High = p.High / 2, Low = p.Low / 2, Close = p.Close / 2 } : o).Append(action).ToImmutableArray() };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        return fixture.Create with { DatasetSha256 = sha, ActionMode = RawPaperContract.SplitMode };
    }

    [Fact]
    public void RawPaperSplitsRequireExplicitOptInAndBookSharesWithoutCreatingWealth()
    {
        var request = SplitRequest();
        Assert.Equal("raw-paper-actions-unsupported", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request with { ActionMode = null })).Code);
        var account = store.CreateRawPaperAccount(request); Assert.Equal(RawPaperContract.SplitVersion, account.Version);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp); Assert.Equal(.49375m, Assert.Single(preview.ProjectedBalance.Holdings).Value.Quantity);
        Assert.Empty(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperSplitsPreserveTwoPeriodCapitalAndExactColdReceiptRetry()
    {
        var account = store.CreateRawPaperAccount(SplitRequest()); var firstPreview = store.PrepareRawPaperBatch(account.Id, new(null));
        var first = ApplyPrepared(account, firstPreview); Assert.Equal("succeeded", first.Status);
        var receipt = store.RawPaperReceiptBytes(first.Id); var firstBytes = File.ReadAllBytes(Path.Combine(root, "imports", firstPreview.ReplaySha256 + ".raw-paper.json"));
        store.Dispose(); store = new(root);
        var secondPreview = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        var secondBytes = File.ReadAllBytes(Path.Combine(root, "imports", secondPreview.ReplaySha256 + ".raw-paper.json"));
        var document = RawPaperContract.ValidateAppend(account.Policy, firstBytes, secondBytes);
        Assert.Single(document.Events.Where(e => e.Operation is RawReplaySplit));
        Assert.Equal(39.5m, secondPreview.ProjectedBalance.EquityGbp); Assert.Equal(.9875m, Assert.Single(secondPreview.ProjectedBalance.Holdings).Value.Quantity);
        var request = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), first.ReceiptSha256, secondPreview.ReplaySha256);
        var second = store.AdvanceRawPaperAccount(account.Id, request); Assert.Equal("succeeded", second.Status);
        Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp); Assert.Equal(.5m, store.RawPaperAccounts().Single().Balance.FeesGbp);
        store.Dispose(); store = new(root);
        Assert.Equal(second, store.AdvanceRawPaperAccount(account.Id, request with { RequestId = request.RequestId.ToUpperInvariant() }));
        Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id)); Assert.Equal(2, store.RawPaperOperations(account.Id).Count);
    }

    [Fact]
    public void RawPaperSplitsAtTheExactClosingBoundaryAreAppliedOnce()
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        var lastClose = file.Observations.OfType<PriceBar>().Where(p => p.Basis == PriceBasis.Raw && p.SessionCloseAt <= fixture.Create.Decisions[13]).Max(p => p.SessionCloseAt);
        var account = store.CreateRawPaperAccount(SplitRequest(lastClose, fixture.Create.Decisions[13]));
        var preview = store.PrepareRawPaperBatch(account.Id, new(null)); Assert.Equal(.49375m, Assert.Single(preview.ProjectedBalance.Holdings).Value.Quantity); Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp);
        var first = ApplyPrepared(account, preview); Assert.Equal("succeeded", first.Status);
        var next = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", next.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        Assert.Single(document.Events.Where(e => e.Operation is RawReplaySplit)); Assert.Equal(39.5m, next.ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperSplitsCannotMultiplySharesAgainstAPreSplitClosingQuote()
    {
        var account = store.CreateRawPaperAccount(SplitRequest(fixture.Create.Decisions[13]));
        Assert.Equal("raw-paper-split-price", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.EquityGbp);
    }

    [Fact]
    public void RawPaperSplitsBeforeAnyOwnershipDoNotCreateAnEntitlement()
    {
        var account = store.CreateRawPaperAccount(SplitRequest(fixture.Create.FirstDecisionAt.AddDays(-1)));
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        Assert.Empty(document.Events.Where(e => e.Operation is RawReplaySplit));
        Assert.Equal(.49375m, Assert.Single(preview.ProjectedBalance.Holdings).Value.Quantity); Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperSplitsDuringAnIndivisibleFillBatchRefuseBeforeAnAttempt()
    {
        var fill = fixture.Full.Events.First(e => e.Operation is RawReplayModelledFill);
        var account = store.CreateRawPaperAccount(SplitRequest(fill.At.AddSeconds(-1)));
        Assert.Equal("raw-paper-action-during-batch", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal("ready", store.RawPaperAccounts().Single().Status);
    }

    [Fact]
    public void RawPaperSplitsLateAfterQuantityChangesCannotRewriteTheAcceptedPrefix()
    {
        var account = store.CreateRawPaperAccount(SplitRequest(disclosed: fixture.Create.Decisions[13].AddDays(5)));
        var first = ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))); Assert.Equal("succeeded", first.Status);
        var accepted = JsonSerializer.Serialize(store.RawPaperAccounts(), RawReplayJson.Options);
        Assert.Equal("raw-ledger-action-history", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
        Assert.Equal(accepted, JsonSerializer.Serialize(store.RawPaperAccounts(), RawReplayJson.Options)); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Theory]
    [InlineData("dividend", "raw-paper-actions-unsupported")]
    [InlineData("mixed", "raw-paper-actions-unsupported")]
    [InlineData("revision", "raw-paper-split-vintage")]
    [InlineData("tie", "raw-paper-split-ambiguous")]
    public void RawPaperSplitsRejectUnsupportedOrAmbiguousOriginalsAtAdmission(string mutation, string code)
    {
        var request = SplitRequest(); var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", request.DatasetSha256 + ".json")));
        var action = file.Observations.OfType<CorporateAction>().Single(); var rows = file.Observations;
        if (mutation == "dividend") rows = rows.Replace(action, action with { Action = CorporateActionKind.Dividend, SplitRatio = null, CashPerShare = 1, CashCurrency = "USD" });
        else if (mutation == "mixed") rows = rows.Replace(action, action with { CashPerShare = 1, CashCurrency = "USD" });
        else if (mutation == "revision") rows = rows.Add(action with { Id = "revised-split", Evidence = action.Evidence with { RevisionOf = action.Id, AvailableAt = action.Evidence.AvailableAt!.Value.AddDays(1), PublishedAt = action.Evidence.AvailableAt!.Value.AddDays(1) } });
        else rows = rows.Add(action with { Id = "other-split", ActionId = "other-split" });
        file = file with { Observations = rows }; var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        Assert.Equal(code, Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request with { DatasetSha256 = sha })).Code); Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperSplitsCannotOmitAnActionEvenWithARehashedCanonicalRequest()
    {
        var account = store.CreateRawPaperAccount(SplitRequest()); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        document = document with { Events = document.Events.Where(e => e.Operation is not RawReplaySplit).Select((e, i) => e with { Sequence = i }).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options); var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(root, "imports", sha + ".raw-paper.json"), bytes);
        var failed = store.AdvanceRawPaperAccount(account.Id, new(Guid.NewGuid().ToString("D"), null, sha));
        Assert.Equal("failed", failed.Status); Assert.Equal("raw-paper-action-binding", failed.ErrorCode);
        Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperSplitsDoNotChangeLegacyNullBytesOrAcceptUnknownModes()
    {
        Assert.DoesNotContain("actionMode", JsonSerializer.Serialize(fixture.Create, RawReplayJson.Options));
        Assert.Equal("raw-paper-request", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(fixture.Create with { ActionMode = "all-actions" })).Code);
        var account = store.CreateRawPaperAccount(fixture.Create); Assert.Equal(RawPaperContract.Version, account.Version);
        Assert.DoesNotContain("actionMode", JsonSerializer.Serialize(account.Policy, RawReplayJson.Options));
    }

    [Fact]
    public async Task RawPaperSplitsHttpOptInReachesThePreparedReplayConsumer()
    {
        var request = SplitRequest();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var created = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", request, RawReplayJson.Options);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var account = (await created.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options))!;
            using var prepared = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/prepare", new { expectedReceiptSha256 = (string?)null });
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            var preview = (await prepared.Content.ReadFromJsonAsync<RawPreparedPaperBatch>(RawReplayJson.Options))!;
            var bytes = File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json"));
            var document = JsonSerializer.Deserialize<RawReplayDocument>(bytes, RawReplayJson.Options)!;
            Assert.Single(document.Events.Where(e => e.Operation is RawReplaySplit));
            using var applied = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/advance", new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256));
            Assert.Equal("succeeded", (await applied.Content.ReadFromJsonAsync<RawPaperOperation>(RawReplayJson.Options))!.Status);
        }
        finally { await app.StopAsync(); }
    }
}
