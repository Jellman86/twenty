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
    private const string DividendMode = "cash-dividends-v1";
    private RawPaperCreateRequest DividendRequest(DateTimeOffset? cutoff = null, DateTimeOffset? paymentAt = null, decimal withheld = .5m, decimal paymentRate = .7m)
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        var bar = file.Observations.OfType<PriceBar>().First(p => p.Basis == PriceBasis.Raw);
        var at = cutoff ?? fixture.Create.FirstDecisionAt.AddDays(5); var paid = paymentAt ?? at.AddDays(5);
        var action = new CorporateAction("paper-dividend-observation", bar.Evidence with { PublishedAt = at, AvailableAt = at },
            bar.ListingId, "paper-dividend", CorporateActionKind.Dividend, at, null, 2m, "USD", null, null, TerminalOutcome.NotApplicable);
        // Use an independently authored payment FX, different from the price/fill conversion (0.8).
        var fx = file.Observations.OfType<FxRate>().First() with { Id = "dividend-payment-fx", ObservedAt = paid,
            QuoteUnitsPerBase = paymentRate, Evidence = bar.Evidence with { PublishedAt = paid, AvailableAt = paid } };
        file = file with { Observations = file.Observations.Add(action).Add(fx) };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var terms = new RawPaperDividendTerms(action.ListingId, action.ActionId, action.Id,
            new(at, at, "urn:authored:dividend-cutoff", new string('a', 64)),
            new(new("paper-income-payment", 1, sha), "authored-payment-1", "urn:authored:payment", new string('b', 64),
                paid, paid, "USD", 2m, withheld, .01m, withheld == 2m ? null : fx.Id, RawStudyActionTerms.AllocationRule));
        return fixture.Create with { DatasetSha256 = sha, ActionMode = DividendMode, Dividends = [terms] };
    }

    [Fact]
    public void RawPaperDividendFreezesTermsAndPaysCutoffSharesAtPaymentFxExactlyOnce()
    {
        var request = DividendRequest(); var account = store.CreateRawPaperAccount(request);
        Assert.Equal("raw-paper-dividend-batches-v1", account.Version);
        Assert.Single(account.Policy.Dividends);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        // 19.75 / (100 * .8) = .246875 shares. Gross .49375 USD, withheld .1234375 USD.
        // (.49375 - .1234375) * .7 - .01 = .24921875 GBP, with fee charged once per account.
        Assert.Equal(.24921875m, preview.ProjectedBalance.CashGbp);
        Assert.Equal(19.99921875m, preview.ProjectedBalance.EquityGbp);
        Assert.Equal(.26m, preview.ProjectedBalance.FeesGbp);
        Assert.Equal(.246875m, Assert.Single(preview.ProjectedBalance.Holdings).Value.Quantity);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        var apply = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256);
        var first = store.AdvanceRawPaperAccount(account.Id, apply); Assert.Equal("succeeded", first.Status);
        var receipt = store.RawPaperReceiptBytes(first.Id);
        var decoded = JsonSerializer.Deserialize<RawReplayReceipt>(receipt, RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(decoded.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(.49375m, Assert.Single(result.Entries.Where(e => e.Kind == "dividend-payment")).DividendPayment!.GrossLocal);
        Assert.NotNull(Assert.Single(result.DividendEntitlements).PaidAt);
        store.Dispose(); store = new(root);
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, apply)); Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id));
        var second = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        Assert.Equal(39.74921875m, second.ProjectedBalance.EquityGbp);
        Assert.Equal(.51m, second.ProjectedBalance.FeesGbp);
        var secondResult = ApplyPrepared(account, second); Assert.Equal("succeeded", secondResult.Status);
        Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Equal(8, store.ListRawTrials().Count); Assert.Null(store.RawStudies().Single().FinalRequestId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-observation")]
    [InlineData("wrong-gross")]
    [InlineData("negative-fee")]
    [InlineData("allocation-rule")]
    public void RawPaperDividendRejectsInvalidTermsBeforeCreatingAnAccount(string mutation)
    {
        var request = DividendRequest(); var term = request.Dividends[0];
        request = mutation switch
        {
            "missing" => request with { Dividends = [] },
            "duplicate" => request with { Dividends = [term, term] },
            "wrong-observation" => request with { Dividends = [term with { ObservationId = "other-observation" }] },
            "wrong-gross" => request with { Dividends = [term with { Delivery = term.Delivery with { GrossPerShare = 3 } }] },
            "negative-fee" => request with { Dividends = [term with { Delivery = term.Delivery with { FixedFeeGbp = -1 } }] },
            // A different allocation rule is not silently accepted as an estimated mark policy.
            _ => request with { Dividends = [term with { Delivery = term.Delivery with { AllocationRule = RawStudyClaimMarks.AllocationRule } }] }
        };
        Assert.Equal("raw-paper-dividend-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperDividendCannotSpanAMonthlyMarkEvenIfTheAccountWouldNotHoldIt()
    {
        var request = DividendRequest(paymentAt: fixture.Create.Decisions[13].AddDays(1));
        Assert.Equal("raw-paper-dividend-unsettled", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperDividendExactClosingPaymentIsBookedOnceAndNotRepeatedAtNextOpening()
    {
        var account = store.CreateRawPaperAccount(DividendRequest(paymentAt: fixture.Create.Decisions[13]));
        var first = ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))); Assert.Equal("succeeded", first.Status);
        var second = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        var doc = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", second.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        Assert.Single(doc.Events.Where(e => e.Operation is RawReplayDividendCapture));
        Assert.Single(doc.Events.Where(e => e.Operation is RawReplayDividendPayment));
        // The exact-boundary payment rate .7 also sizes the next decision; its fills/close use .8.
        Assert.Equal(42.60625m, second.ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperDividendWithoutCutoffOwnershipCreatesNoPaymentOrFixedFee()
    {
        var request = DividendRequest(cutoff: fixture.Create.FirstDecisionAt.AddDays(-1), paymentAt: fixture.Create.FirstDecisionAt, paymentRate: .8m);
        var account = store.CreateRawPaperAccount(request);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp); Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp);
        var doc = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        Assert.Empty(doc.Events.Where(e => e.Operation is RawReplayDividendCapture or RawReplayDividendPayment));
    }

    [Fact]
    public void RawPaperDividendCannotDropOrForgePaymentInAnOtherwiseCoherentReplay()
    {
        var account = store.CreateRawPaperAccount(DividendRequest()); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var doc = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        var events = doc.Events.Select(e => e.Operation is RawReplayDividendPayment p ? e with { Operation = p with { Payment = p.Payment with { WithheldLocal = 0 } } } : e).ToImmutableArray();
        var failed = store.AdvanceRawPaperAccount(account.Id, Advance(1, changed: doc with { Events = events }));
        Assert.Equal("failed", failed.Status); Assert.Equal("raw-paper-action-binding", failed.ErrorCode);
        Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }

    [Fact]
    public void RawPaperDividendCatalogIsNeverIgnoredByLegacyOrSplitModes()
    {
        var request = DividendRequest();
        foreach (var mode in new string?[] { null, "pure-splits-v1" })
            Assert.Equal("raw-paper-dividend-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request with { ActionMode = mode })).Code);
        Assert.DoesNotContain("dividends", JsonSerializer.Serialize(fixture.Create, RawReplayJson.Options));
        Assert.DoesNotContain("dividends", JsonSerializer.Serialize(store.CreateRawPaperAccount(fixture.Create).Policy, RawReplayJson.Options));
    }

    [Fact]
    public void RawPaperDividendSameInstantCaptureAndPaymentKeepDependencyOrder()
    {
        var at = fixture.Create.FirstDecisionAt.AddDays(5);
        var account = store.CreateRawPaperAccount(DividendRequest(cutoff: at, paymentAt: at));
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(.24921875m, preview.ProjectedBalance.CashGbp);
        Assert.Equal("succeeded", ApplyPrepared(account, preview).Status);
    }

    [Fact]
    public void RawPaperDividendDuringACompleteFillBatchRefusesWithoutAdmittingAnAttempt()
    {
        var fill = fixture.Full.Events.First(e => e.Operation is RawReplayModelledFill);
        var at = fixture.Create.FirstDecisionAt.AddTicks(1);
        var account = store.CreateRawPaperAccount(DividendRequest(cutoff: at, paymentAt: fill.At.AddDays(1)));
        Assert.Equal("raw-paper-action-during-batch", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }

    [Fact]
    public void RawPaperDividendFullyWithheldIncomeCannotBorrowTheKnownFixedFee()
    {
        var account = store.CreateRawPaperAccount(DividendRequest(withheld: 2m));
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal("ready", store.RawPaperAccounts().Single().Status);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("split")]
    public void RawPaperDividendRejectsRevisedOrMixedActionDatasets(string mutation)
    {
        var request = DividendRequest();
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", request.DatasetSha256 + ".json")));
        var action = file.Observations.OfType<CorporateAction>().Single();
        var other = action with { Id = "revised-income", Evidence = action.Evidence with { RevisionOf = action.Id, PublishedAt = action.EffectiveAt.AddDays(1), AvailableAt = action.EffectiveAt.AddDays(1) } };
        if (mutation == "split") other = action with { Id = "other-split", ActionId = "other-split", Action = CorporateActionKind.Split, SplitRatio = 2, CashPerShare = null, CashCurrency = null };
        file = file with { Observations = file.Observations.Add(other) };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        Assert.Equal("raw-paper-dividend-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request with { DatasetSha256 = sha })).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperDividendCreationRetryCannotSubstituteFrozenWithholding()
    {
        var request = DividendRequest(); var account = store.CreateRawPaperAccount(request);
        Assert.Equal(account.Id, store.CreateRawPaperAccount(request).Id);
        var term = request.Dividends[0];
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request with
        { Dividends = [term with { Delivery = term.Delivery with { WithheldPerShare = 0 } }] })).Code);
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }

    [Fact]
    public void RawPaperDividendReceiptRemainsReadableAcrossChangedResearchIdentityWithoutOriginals()
    {
        var account = store.CreateRawPaperAccount(DividendRequest()); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var apply = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256);
        var first = store.AdvanceRawPaperAccount(account.Id, apply); Assert.Equal("succeeded", first.Status);
        var receipt = store.RawPaperReceiptBytes(first.Id);
        var changed = store.RawPaperAccounts().Single() with { CodeSha256 = new string('a', 64) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE raw_paper_accounts SET json=$json,sha256=$sha WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", System.Text.Encoding.UTF8.GetString(bytes)); cmd.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))); cmd.Parameters.AddWithValue("$id", account.Id); cmd.ExecuteNonQuery();
        }
        Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "inputs-hidden"));
        Assert.Equal(first, store.AdvanceRawPaperAccount(account.Id, apply)); Assert.Equal(receipt, store.RawPaperReceiptBytes(first.Id));
        Assert.Equal("study-code-changed", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
    }

    [Fact]
    public async Task RawPaperDividendHttpCreatePrepareApplyAndColdRetryUseTheRealLedger()
    {
        var request = DividendRequest();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var denied = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", request, RawReplayJson.Options); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var created = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", request, RawReplayJson.Options); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var account = (await created.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options))!;
            var path = "/api/research/raw-paper-accounts/" + account.Id;
            using var prepared = await client.PostAsJsonAsync(path + "/prepare", new { expectedReceiptSha256 = (string?)null }); Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            var preview = (await prepared.Content.ReadFromJsonAsync<RawPreparedPaperBatch>(RawReplayJson.Options))!;
            Assert.Equal(.24921875m, preview.ProjectedBalance.CashGbp); Assert.Empty(store.RawPaperOperations(account.Id));
            var apply = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), null, preview.ReplaySha256);
            using var applied = await client.PostAsJsonAsync(path + "/advance", apply); Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            var operation = (await applied.Content.ReadFromJsonAsync<RawPaperOperation>(RawReplayJson.Options))!; Assert.Equal("succeeded", operation.Status);
            using var capability = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts"));
            Assert.True(capability.RootElement.GetProperty("actionSupport").GetProperty("dividends").GetBoolean());
            Assert.False(capability.RootElement.GetProperty("actionSupport").GetProperty("pendingClaims").GetBoolean());
            store.Dispose(); store = new(root);
            Assert.Equal(operation, store.AdvanceRawPaperAccount(account.Id, apply)); Assert.Equal(19.99921875m, store.RawPaperAccounts().Single().Balance.EquityGbp);
        }
        finally { await app.StopAsync(); }
    }
}
