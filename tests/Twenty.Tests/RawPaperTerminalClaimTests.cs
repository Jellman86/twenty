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
    private const string TerminalClaimsMode = "terminal-claims-v1";
    private JsonNode TerminalClaimNode(string kind = "cash", DateTimeOffset? cutoff = null, DateTimeOffset? settlement = null, decimal expectedWithheld = 10m)
    {
        var node = TerminalNode(kind, cutoff, settlement ?? fixture.Create.Decisions[13].AddDays(5));
        node["actionMode"] = TerminalClaimsMode;
        if (kind != "zero")
        {
            var term = node["terminals"]![0]!;
            term["delivery"]!["fixedFeeGbp"] = .01m;
            term["markPolicy"] = JsonSerializer.SerializeToNode(new RawStudyClaimMarkPolicy(kind == "cash" ? "USD" : null,
                kind == "cash" ? expectedWithheld : 0m, .01m, term["evidence"]!["availableAt"]!.GetValue<DateTimeOffset>(),
                "urn:authored:terminal-expectation", new string('c', 64), RawStudyClaimMarks.AllocationRule), RawReplayJson.Options);
        }
        return node;
    }
    private RawPaperCreateRequest TerminalClaimRequest(string kind = "cash") => TerminalClaimNode(kind).Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!;
    private static JsonElement TerminalClaimBalance(RawPaperBalance balance)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(balance, RawReplayJson.Options));
        return doc.RootElement.GetProperty("terminalClaims").Clone();
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("stock")]
    public void RawPaperTerminalClaimSeparatesExpectedValueFromCashAndDeliveredInventory(string kind)
    {
        var account = store.CreateRawPaperAccount(TerminalClaimRequest(kind));
        Assert.Equal("raw-paper-terminal-claim-batches-v1", account.Version);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(0m, first.ProjectedBalance.EquityGbp); Assert.Equal(0m, first.ProjectedBalance.CashGbp); Assert.Empty(first.ProjectedBalance.Holdings);
        Assert.Equal(.25m, first.ProjectedBalance.FeesGbp); Assert.Null(first.ProjectedBalance.DividendClaims);
        var mark = TerminalClaimBalance(first.ProjectedBalance);
        Assert.Equal(1, mark.GetProperty("count").GetInt32()); Assert.Equal(19.74m, mark.GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(.01m, mark.GetProperty("expectedFeesGbp").GetDecimal()); Assert.Equal(19.74m, mark.GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Empty(store.RawPaperOperations(account.Id));
        var one = ApplyPrepared(account, first); Assert.Equal("succeeded", one.Status);
        var original = store.RawPaperReceiptBytes(one.Id);
        var second = store.PrepareRawPaperBatch(account.Id, new(one.ReceiptSha256));
        Assert.Equal(kind == "cash" ? 37.02125m : 39.49m, second.ProjectedBalance.EquityGbp);
        Assert.Equal(kind == "cash" ? 17.27125m : 0m, second.ProjectedBalance.CashGbp);
        Assert.Equal(kind == "cash" ? .246875m : .98725m, second.ProjectedBalance.Holdings.Single().Value.Quantity);
        Assert.Equal(.51m, second.ProjectedBalance.FeesGbp); Assert.Equal(0, TerminalClaimBalance(second.ProjectedBalance).GetProperty("count").GetInt32());
        var advance = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), one.ReceiptSha256, second.ReplaySha256);
        var two = store.AdvanceRawPaperAccount(account.Id, advance); Assert.Equal("succeeded", two.Status);
        var receipt = JsonSerializer.Deserialize<RawReplayReceipt>(store.RawPaperReceiptBytes(two.Id), RawReplayJson.Options)!;
        var document = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(receipt.RequestBase64), RawReplayJson.Options)!;
        var plan = document.Events.Select(e => e.Operation).OfType<RawReplayRebalanceDecision>().Last().Plan;
        Assert.Equal(RawRebalancePlanner.ClaimsVersion, plan.Version);
        Assert.Equal(kind == "stock" ? .01m : 0m, plan.ClaimCashReserveGbp);
        Assert.Equal(kind == "stock" ? .4935m : .246875m, Assert.Single(plan.Orders).Quantity);
        if (kind == "cash") Assert.Equal(19.74m, plan.DeferredOrderValueGbp);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "inputs-hidden")); store = new(root);
        Assert.Equal(two, store.AdvanceRawPaperAccount(account.Id, advance)); Assert.Equal(original, store.RawPaperReceiptBytes(one.Id));
        Assert.Equal(40m, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Equal(8, store.ListRawTrials().Count); Assert.Null(store.RawStudies().Single().FinalRequestId);
    }

    [Theory]
    [InlineData("missing", "raw-paper-terminal-mark-coverage")]
    [InlineData("late", "raw-paper-terminal-mark-coverage")]
    [InlineData("currency", "raw-paper-terminal-policy")]
    [InlineData("withheld", "raw-paper-terminal-policy")]
    [InlineData("fee", "raw-paper-terminal-policy")]
    [InlineData("rule", "raw-paper-terminal-policy")]
    [InlineData("final", "raw-paper-terminal-final-unsettled")]
    public void RawPaperTerminalClaimRejectsUncoveredOrInvalidPolicyBeforeAdmission(string mutation, string code)
    {
        var node = TerminalClaimNode(); var term = node["terminals"]![0]!; var mark = term["markPolicy"]!;
        switch (mutation)
        {
            case "missing": term.AsObject().Remove("markPolicy"); break;
            case "late": mark["availableAt"] = fixture.Create.Decisions[13].AddTicks(1).ToString("O"); break;
            case "currency": mark["currency"] = "GBP"; break;
            case "withheld": mark["expectedWithheldPerShare"] = 111m; break;
            case "fee": mark["expectedFeeGbp"] = -.01m; break;
            case "rule": mark["allocationRule"] = "unsupported"; break;
            case "final": node["decisions"] = JsonSerializer.SerializeToNode(fixture.Create.Decisions.Take(14), RawReplayJson.Options); break;
        }
        Assert.Equal(code, Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperTerminalClaimPolicyIsNeverIgnoredByTheOriginalSettledMode()
    {
        var node = TerminalClaimNode(settlement: fixture.Create.FirstDecisionAt.AddDays(10)); node["actionMode"] = TerminalMode;
        Assert.Equal("raw-paper-terminal-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
    }

    [Fact]
    public void RawPaperTerminalClaimCanonicalAdmissionRejectsCoherentAlternateMarks()
    {
        var account = store.CreateRawPaperAccount(TerminalClaimRequest()); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var doc = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        var changed = doc with { Events = doc.Events.Select(e => e.Operation is RawReplayMark m && m.Terminals.Length > 0
            ? e with { Operation = m with { Terminals = m.Terminals.Select(t => t with { ExpectedFeeGbp = 0m }).ToImmutableArray() } } : e).ToImmutableArray() };
        _ = RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options), sha => File.ReadAllBytes(Path.Combine(root, "universe-datasets", sha + ".json")));
        var failed = store.AdvanceRawPaperAccount(account.Id, Advance(1, changed: changed));
        Assert.Equal("failed", failed.Status); Assert.Equal("raw-paper-action-binding", failed.ErrorCode);
        Assert.Equal(0m, store.RawPaperAccounts().Single().Balance.ContributionsGbp); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public async Task RawPaperTerminalClaimHttpPersistsExactExpectedTermsAndSeparateCapabilities()
    {
        var node = TerminalClaimNode(); node["terminals"]![0]!["markPolicy"]!["expectedWithheldPerShare"] = JsonNode.Parse("10.000000000000000000000000001");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var response = await client.PostAsync("/api/research/raw-paper-accounts", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var account = (await response.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options))!;
            using var policy = JsonDocument.Parse(JsonSerializer.Serialize(account.Policy, RawReplayJson.Options));
            Assert.Equal(10.000000000000000000000000001m, policy.RootElement.GetProperty("terminals")[0].GetProperty("markPolicy").GetProperty("expectedWithheldPerShare").GetDecimal());
            using var cap = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts"));
            Assert.Equal(TerminalClaimsMode, cap.RootElement.GetProperty("pendingTerminalMode").GetString());
            Assert.Equal("settled-by-final-monthly-mark", cap.RootElement.GetProperty("pendingTerminalScope").GetString());
            Assert.Equal(TerminalMode, cap.RootElement.GetProperty("terminalMode").GetString());
            Assert.Equal("settled-before-monthly-marks", cap.RootElement.GetProperty("terminalScope").GetString());
            Assert.Empty(store.RawPaperOperations(account.Id));
        }
        finally { await app.StopAsync(); }
    }
}
