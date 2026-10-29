using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    private const string PendingDividendMode = "dividend-claims-v1";
    private JsonNode PendingDividendNode(DateTimeOffset? cutoff = null, DateTimeOffset? paymentAt = null, decimal expectedWithheld = .5m)
    {
        var request = DividendRequest(cutoff: cutoff, paymentAt: paymentAt ?? fixture.Create.Decisions[13].AddDays(5));
        var node = JsonNode.Parse(JsonSerializer.Serialize(request, RawReplayJson.Options))!;
        node["actionMode"] = PendingDividendMode;
        node["dividends"]![0]!["markPolicy"] = JsonSerializer.SerializeToNode(new RawStudyClaimMarkPolicy("USD", expectedWithheld, .01m,
            request.Dividends[0].Evidence.AvailableAt, "urn:authored:expected-income", new string('c', 64), RawStudyClaimMarks.AllocationRule), RawReplayJson.Options);
        return node;
    }
    private RawPaperCreateRequest PendingDividendRequest(DateTimeOffset? cutoff = null, DateTimeOffset? paymentAt = null, decimal expectedWithheld = .5m) =>
        PendingDividendNode(cutoff, paymentAt, expectedWithheld).Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!;
    private static JsonElement PendingDividendBalance(RawPaperBalance balance)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(balance, RawReplayJson.Options));
        return doc.RootElement.GetProperty("dividendClaims").Clone();
    }

    [Fact]
    public void RawPaperPendingDividendSurvivesTheDecisionWithoutFundingSharesAndPaysExactlyOnce()
    {
        var account = store.CreateRawPaperAccount(PendingDividendRequest());
        Assert.Equal("raw-paper-dividend-claim-batches-v1", account.Version);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        // Independently authored Python Decimal vector: paid after decision 2, not before decision 1's close.
        Assert.Equal(0, preview.ProjectedBalance.CashGbp); Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp);
        Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp);
        var claim = PendingDividendBalance(preview.ProjectedBalance);
        Assert.Equal(1, claim.GetProperty("count").GetInt32());
        Assert.Equal(.28625m, claim.GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(.01m, claim.GetProperty("expectedFeesGbp").GetDecimal());
        Assert.Equal(20.03625m, claim.GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        var first = ApplyPrepared(account, preview); Assert.Equal("succeeded", first.Status);
        var firstReceipt = store.RawPaperReceiptBytes(first.Id);
        var second = store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256));
        Assert.Equal(.24921875m, second.ProjectedBalance.CashGbp);
        Assert.Equal(.49375m, second.ProjectedBalance.Holdings.Single().Value.Quantity);
        Assert.Equal(39.74921875m, second.ProjectedBalance.EquityGbp);
        Assert.Equal(.51m, second.ProjectedBalance.FeesGbp);
        Assert.Equal(0, PendingDividendBalance(second.ProjectedBalance).GetProperty("count").GetInt32());
        var request = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), first.ReceiptSha256, second.ReplaySha256);
        var applied = store.AdvanceRawPaperAccount(account.Id, request); Assert.Equal("succeeded", applied.Status);
        var saved = JsonSerializer.Deserialize<RawReplayReceipt>(store.RawPaperReceiptBytes(applied.Id), RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(saved.ResultBase64), RawReplayJson.Options)!;
        Assert.Single(result.Entries.Where(e => e.Kind == "dividend-payment"));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(saved.RequestBase64), RawReplayJson.Options)!;
        var secondPlan = document.Events.Select(e => e.Operation).OfType<RawReplayRebalanceDecision>().Last().Plan;
        Assert.Equal(RawRebalancePlanner.ClaimsVersion, secondPlan.Version);
        Assert.Equal(.246875m, Assert.Single(secondPlan.Orders).Quantity);
        Assert.Equal(40.03625m, secondPlan.ExpectedTargetEquityGbp + secondPlan.FeeBudgetGbp);
        Assert.Equal(.28625m, secondPlan.DeferredOrderValueGbp); Assert.Equal(0m, secondPlan.ClaimCashReserveGbp);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "hidden-inputs")); store = new(root);
        Assert.Equal(applied, store.AdvanceRawPaperAccount(account.Id, request));
        Assert.Equal(firstReceipt, store.RawPaperReceiptBytes(first.Id));
        Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Equal(8, store.ListRawTrials().Count); Assert.Null(store.RawStudies().Single().FinalRequestId);
    }

    [Theory]
    [InlineData("missing", "raw-paper-dividend-mark-coverage")]
    [InlineData("late", "raw-paper-dividend-mark-coverage")]
    [InlineData("currency", "raw-paper-dividend-policy")]
    [InlineData("withholding", "raw-paper-dividend-policy")]
    [InlineData("fee", "raw-paper-dividend-policy")]
    [InlineData("allocation", "raw-paper-dividend-policy")]
    [InlineData("final", "raw-paper-dividend-final-unsettled")]
    public void RawPaperPendingDividendPolicyRefusesBeforeAccountAdmission(string mutation, string expected)
    {
        var node = PendingDividendNode(); var policy = node["dividends"]![0]!["markPolicy"]!;
        switch (mutation)
        {
            case "missing": node["dividends"]![0]!.AsObject().Remove("markPolicy"); break;
            case "late": policy["availableAt"] = fixture.Create.Decisions[13].AddTicks(1).ToString("O"); break;
            case "currency": policy["currency"] = "EUR"; break;
            case "withholding": policy["expectedWithheldPerShare"] = 3m; break;
            case "fee": policy["expectedFeeGbp"] = -.01m; break;
            case "allocation": policy["allocationRule"] = "unknown"; break;
            case "final": node["decisions"] = JsonSerializer.SerializeToNode(fixture.Create.Decisions.Take(14), RawReplayJson.Options); break;
        }
        var request = node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!;
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(request)).Code);
        Assert.Empty(store.RawPaperAccounts()); Assert.Equal(8, store.ListRawTrials().Count);
    }

    [Fact]
    public void RawPaperPendingDividendActualPaymentNeverSuppliesTheEarlierEstimate()
    {
        var account = store.CreateRawPaperAccount(PendingDividendRequest(expectedWithheld: .1m));
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(.36525m, PendingDividendBalance(first.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        var applied = ApplyPrepared(account, first); Assert.Equal("succeeded", applied.Status);
        var second = store.PrepareRawPaperBatch(account.Id, new(applied.ReceiptSha256));
        Assert.Equal(.24921875m, second.ProjectedBalance.CashGbp);
        Assert.Equal(39.74921875m, second.ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperPendingDividendWithoutCutoffOwnershipHasNoClaimOrFee()
    {
        var account = store.CreateRawPaperAccount(PendingDividendRequest(cutoff: fixture.Create.FirstDecisionAt.AddDays(-1)));
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, first.ProjectedBalance.EquityGbp);
        Assert.Equal(0, PendingDividendBalance(first.ProjectedBalance).GetProperty("count").GetInt32());
        var applied = ApplyPrepared(account, first); Assert.Equal("succeeded", applied.Status);
        var second = store.PrepareRawPaperBatch(account.Id, new(applied.ReceiptSha256));
        Assert.Equal(0, second.ProjectedBalance.CashGbp); Assert.Equal(.5m, second.ProjectedBalance.FeesGbp);
        Assert.Equal(39.5m, second.ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperPendingDividendCannotReplaceFrozenMarksWithACoherentOfflineEstimate()
    {
        var account = store.CreateRawPaperAccount(PendingDividendRequest());
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var doc = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        var changed = doc with { Events = doc.Events.Select(e => e.Operation is RawReplayMark m && m.Dividends.Length > 0
            ? e with { Operation = m with { Dividends = m.Dividends.Select(a => a with { ExpectedWithheldLocal = 0 }).ToImmutableArray() } } : e).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options);
        _ = RawReplayArchive.Execute(bytes, sha => File.ReadAllBytes(Path.Combine(root, "universe-datasets", sha + ".json")));
        var failed = store.AdvanceRawPaperAccount(account.Id, Advance(1, changed: changed));
        Assert.Equal("failed", failed.Status); Assert.Equal("raw-paper-action-binding", failed.ErrorCode);
        Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperPendingDividendPoliciesAreNeverIgnoredByTheOriginalPaidMode()
    {
        var node = PendingDividendNode(paymentAt: fixture.Create.FirstDecisionAt.AddDays(10));
        node["actionMode"] = DividendMode;
        Assert.Equal("raw-paper-dividend-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        var legacy = store.CreateRawPaperAccount(DividendRequest());
        Assert.DoesNotContain("markPolicy", JsonSerializer.Serialize(legacy.Policy, RawReplayJson.Options));
        Assert.DoesNotContain("dividendClaims", JsonSerializer.Serialize(legacy.Balance, RawReplayJson.Options));
    }

    [Fact]
    public async Task RawPaperPendingDividendHttpPersistsExactExpectedDecimalTokens()
    {
        var node = PendingDividendNode(); var policy = node["dividends"]![0]!["markPolicy"]!;
        policy["expectedWithheldPerShare"] = JsonNode.Parse("0.5000000000000000000000000001");
        policy["expectedFeeGbp"] = JsonNode.Parse("0.0100000000000000000000000001");
        var json = node.ToJsonString(); Assert.Contains("0.5000000000000000000000000001", json);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var created = await client.PostAsync("/api/research/raw-paper-accounts", content); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var frozen = store.RawPaperAccounts().Single().Policy.Dividends.Single().MarkPolicy!;
            Assert.Equal(0.5000000000000000000000000001m, frozen.ExpectedWithheldPerShare);
            Assert.Equal(0.0100000000000000000000000001m, frozen.ExpectedFeeGbp);
            store.Dispose(); store = new(root);
            Assert.Equal(frozen, store.RawPaperAccounts().Single().Policy.Dividends.Single().MarkPolicy);
            Assert.Empty(store.RawPaperOperations(store.RawPaperAccounts().Single().Id));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task RawPaperPendingDividendHttpBoundaryRetainsProjectedVersusAcceptedBalances()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var created = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", PendingDividendNode(), RawReplayJson.Options);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var account = (await created.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options))!;
            using var preview = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/prepare", new { expectedReceiptSha256 = (string?)null });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var projected = (await preview.Content.ReadFromJsonAsync<RawPreparedPaperBatch>(RawReplayJson.Options))!;
            Assert.Equal(20.03625m, PendingDividendBalance(projected.ProjectedBalance).GetProperty("expectedEquityGbp").GetDecimal());
            Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp); Assert.Empty(store.RawPaperOperations(account.Id));
            using var caps = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts"));
            Assert.Equal(PendingDividendMode, caps.RootElement.GetProperty("pendingDividendMode").GetString());
            Assert.Equal("cash-dividends-v1", caps.RootElement.GetProperty("dividendMode").GetString());
            Assert.Equal("paid-before-monthly-marks", caps.RootElement.GetProperty("dividendScope").GetString());
        }
        finally { await app.StopAsync(); }
    }
}
