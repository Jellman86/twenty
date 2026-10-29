using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    private const string TerminalMode = "terminal-settlements-v1";
    private JsonNode TerminalNode(string kind = "cash", DateTimeOffset? cutoff = null, DateTimeOffset? settlement = null)
    {
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", fixture.Create.DatasetSha256 + ".json")));
        var listing = file.Observations.OfType<Listing>().First();
        var successor = file.Observations.OfType<Listing>().First(l => l.ListingId != listing.ListingId);
        var at = cutoff ?? fixture.Create.FirstDecisionAt.AddDays(5); var paid = settlement ?? at.AddDays(5);
        var evidence = listing.Evidence with { PublishedAt = at, AvailableAt = at };
        var outcome = kind switch { "zero" => TerminalOutcome.Zero, "stock" => TerminalOutcome.Stock, _ => TerminalOutcome.Cash };
        var action = new CorporateAction("paper-terminal-observation", evidence, listing.ListingId, "paper-terminal",
            CorporateActionKind.Delisting, at, kind == "stock" ? 2m : null, kind == "cash" ? 110m : null,
            kind == "cash" ? "USD" : null, null, kind == "stock" ? successor.ListingId : null, outcome);
        var rows = file.Observations.Where(o => o is not PriceBar p || p.ListingId != listing.ListingId || p.SessionCloseAt < at)
            .Select(o => o switch
            {
                Listing l when l.ListingId == listing.ListingId => l with { EffectiveUntil = at },
                Membership m when m.ListingId == listing.ListingId => m with { EffectiveUntil = at },
                PriceBar p when kind == "stock" && p.ListingId == successor.ListingId && p.Basis == PriceBasis.Raw => p with { Open = 50, High = 50, Low = 50, Close = 50 },
                _ => o
            }).Append(action).ToImmutableArray();
        if (kind == "cash") rows = rows.Add(new FxRate("terminal-settlement-fx", evidence with { PublishedAt = paid, AvailableAt = paid },
            "USD", "GBP", .7m, paid, FxPurpose.Executable));
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options));
        var request = fixture.Create with { DatasetSha256 = sha, ActionMode = TerminalMode };
        var node = JsonSerializer.SerializeToNode(request, RawReplayJson.Options)!;
        RawStudyTerminalDelivery? delivery = kind == "zero" ? null : new(new("paper-terminal-delivery", 1, sha), "authored-terminal-settlement",
            "urn:authored:terminal-settlement", new string('b', 64), paid, paid, kind == "cash" ? "USD" : null,
            kind == "cash" ? 110m : 0m, kind == "cash" ? 10m : 0m, kind == "cash" ? .01m : 0m,
            kind == "cash" ? "terminal-settlement-fx" : null, kind == "stock" ? successor.ListingId : null, kind == "stock" ? 2m : 0m,
            RawStudyActionTerms.AllocationRule);
        node["terminals"] = JsonSerializer.SerializeToNode(new[] { new { listingId = listing.ListingId, actionId = action.ActionId,
            observationId = action.Id, evidence = new RawTerminalEvidence(at, at, "urn:authored:terminal-cutoff", new string('a', 64)), delivery } }, RawReplayJson.Options);
        return node;
    }
    private RawPaperCreateRequest TerminalRequest(string kind = "cash") => TerminalNode(kind).Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!;

    [Theory]
    [InlineData("cash")]
    [InlineData("zero")]
    [InlineData("stock")]
    public void RawPaperTerminalPreservesIndependentTwoPeriodArithmeticAndColdUuidReplay(string kind)
    {
        var request = TerminalRequest(kind); var account = store.CreateRawPaperAccount(request);
        Assert.Equal("raw-paper-terminal-batches-v1", account.Version);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(kind == "cash" ? 17.27125m : kind == "stock" ? 19.75m : 0m, first.ProjectedBalance.EquityGbp);
        Assert.Equal(kind == "cash" ? 17.27125m : 0m, first.ProjectedBalance.CashGbp);
        Assert.Equal(kind == "cash" ? .26m : .25m, first.ProjectedBalance.FeesGbp);
        Assert.Null(first.ProjectedBalance.DividendClaims);
        Assert.Equal(kind == "stock" ? .49375m : 0m, first.ProjectedBalance.Holdings.Values.Sum(h => h.Quantity));
        Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        var one = ApplyPrepared(account, first); Assert.Equal("succeeded", one.Status);
        var originalReceipt = store.RawPaperReceiptBytes(one.Id);
        var receipt = JsonSerializer.Deserialize<RawReplayReceipt>(originalReceipt, RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        var claim = Assert.Single(result.TerminalEntitlements); Assert.Equal(.246875m, claim.Quantity); Assert.NotNull(claim.SettledAt);
        Assert.DoesNotContain(claim.Action.ListingId, result.Holdings.Keys);
        Assert.Single(result.Entries.Where(e => e.Kind == "terminal-entitlement"));
        Assert.Equal(kind == "zero" ? 0 : 1, result.Entries.Count(e => e.Kind == "terminal-settlement"));
        var second = store.PrepareRawPaperBatch(account.Id, new(one.ReceiptSha256));
        Assert.Equal(kind == "cash" ? 37.02125m : kind == "stock" ? 39.5m : 19.75m, second.ProjectedBalance.EquityGbp);
        Assert.Equal(kind == "cash" ? .51m : .5m, second.ProjectedBalance.FeesGbp);
        Assert.Equal(0m, second.ProjectedBalance.CashGbp);
        var advance = new RawPaperAdvanceRequest(Guid.NewGuid().ToString("D"), one.ReceiptSha256, second.ReplaySha256);
        var two = store.AdvanceRawPaperAccount(account.Id, advance); Assert.Equal("succeeded", two.Status);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "inputs-hidden")); store = new(root);
        Assert.Equal(two, store.AdvanceRawPaperAccount(account.Id, advance)); Assert.Equal(originalReceipt, store.RawPaperReceiptBytes(one.Id));
        Assert.Equal(account.Id, store.CreateRawPaperAccount(request).Id);
        Assert.Equal(40, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        Assert.Equal(8, store.ListRawTrials().Count); Assert.Null(store.RawStudies().Single().FinalRequestId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("proof")]
    [InlineData("observation")]
    [InlineData("gross")]
    [InlineData("currency")]
    [InlineData("delivery")]
    [InlineData("withheld")]
    [InlineData("fee")]
    [InlineData("successor")]
    public void RawPaperTerminalRejectsMalformedFrozenTermsBeforeAccountAdmission(string mutation)
    {
        var node = TerminalNode(); var term = node["terminals"]![0]!;
        switch (mutation)
        {
            case "missing": node.AsObject().Remove("terminals"); break;
            case "empty": node["terminals"] = new JsonArray(); break;
            case "proof": term["evidence"]!["contentSha256"] = "invalid"; break;
            case "observation": term["observationId"] = "other"; break;
            case "gross": term["delivery"]!["grossPerShare"] = 111m; break;
            case "currency": term["delivery"]!["currency"] = "GBP"; break;
            case "delivery": term["delivery"] = null; break;
            case "withheld": term["delivery"]!["withheldPerShare"] = 111m; break;
            case "fee": term["delivery"]!["fixedFeeGbp"] = -.01m; break;
            case "successor": term["delivery"]!["successorListingId"] = "bbb"; break;
        }
        Assert.Equal("raw-paper-terminal-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts()); Assert.Equal(8, store.ListRawTrials().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperTerminalRefusesPendingBoundariesIncludingTheFirstDecision(bool first)
    {
        var node = TerminalNode(cutoff: first ? fixture.Create.FirstDecisionAt.AddDays(-1) : null,
            settlement: first ? fixture.Create.FirstDecisionAt.AddDays(5) : fixture.Create.Decisions[13].AddTicks(1));
        Assert.Equal("raw-paper-terminal-unsettled", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pure-splits-v1")]
    [InlineData("cash-dividends-v1")]
    public void RawPaperTerminalCatalogIsNeverIgnoredByAnOlderMode(string? mode)
    {
        var node = TerminalNode(); node["actionMode"] = mode;
        Assert.Equal("raw-paper-terminal-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public async Task RawPaperTerminalHttpRetainsPreparationAndExactFrozenDecimalTerms()
    {
        var node = TerminalNode(); node["terminals"]![0]!["delivery"]!["withheldPerShare"] = JsonNode.Parse("10.000000000000000000000000001");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" }); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var blocked = await client.PostAsJsonAsync("/api/research/raw-paper-accounts", node, RawReplayJson.Options); Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            client.DefaultRequestHeaders.Add("X-Twenty-Request", "1");
            using var created = await client.PostAsync("/api/research/raw-paper-accounts", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode); var account = (await created.Content.ReadFromJsonAsync<RawPaperAccount>(RawReplayJson.Options))!;
            using var response = await client.PostAsJsonAsync("/api/research/raw-paper-accounts/" + account.Id + "/prepare", new { expectedReceiptSha256 = (string?)null });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Empty(store.RawPaperOperations(account.Id));
            Assert.Equal(0m, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
            using var policy = JsonDocument.Parse(JsonSerializer.Serialize(store.RawPaperAccounts().Single().Policy, RawReplayJson.Options));
            Assert.Equal(10.000000000000000000000000001m, policy.RootElement.GetProperty("terminals")[0].GetProperty("delivery").GetProperty("withheldPerShare").GetDecimal());
            using var cap = JsonDocument.Parse(await client.GetByteArrayAsync("/api/research/raw-paper-accounts"));
            Assert.Equal(TerminalMode, cap.RootElement.GetProperty("terminalMode").GetString());
            Assert.Equal("settled-before-monthly-marks", cap.RootElement.GetProperty("terminalScope").GetString());
            Assert.False(cap.RootElement.GetProperty("actionSupport").GetProperty("pendingTerminalClaims").GetBoolean());
        }
        finally { await app.StopAsync(); }
    }
}
