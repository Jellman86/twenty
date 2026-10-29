using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    [Fact]
    public void RawPaperTerminalClaimMultipleCashClaimsStayCompleteAcrossDecisionsAndSettleOnce()
    {
        var node = TerminalClaimNode(settlement: fixture.Create.Decisions[14].AddDays(5));
        var old = node["datasetSha256"]!.GetValue<string>();
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", old + ".json")));
        var first = file.Observations.OfType<CorporateAction>().Single();
        var listing = file.Observations.OfType<Listing>().First(l => l.ListingId != first.ListingId);
        var at = fixture.Create.Decisions[13].AddDays(5); var paid = fixture.Create.Decisions[14].AddDays(6);
        var second = first with { Id = "second-terminal-observation", ListingId = listing.ListingId, ActionId = "second-terminal",
            EffectiveAt = at, Evidence = first.Evidence with { PublishedAt = at, AvailableAt = at } };
        var fx = file.Observations.OfType<FxRate>().Single(r => r.Id == "terminal-settlement-fx") with { Id = "second-terminal-fx", ObservedAt = paid,
            Evidence = first.Evidence with { PublishedAt = paid, AvailableAt = paid } };
        var survivor = listing with { Id = "third-listing", ListingId = "ccc", SecurityId = "third-security", CompanyId = "third-company", Symbol = "CCC", ProviderIds = [new("authored", "CCC")] };
        var membership = file.Observations.OfType<Membership>().First(m => m.ListingId == listing.ListingId) with {
            Id = "third-membership", ListingId = "ccc", EffectiveUntil = null };
        var prices = file.Observations.OfType<PriceBar>().Where(p => p.ListingId == listing.ListingId)
            .Select(p => p with { Id = "third-" + p.Id, ListingId = "ccc" }).ToArray();
        file = file with { Observations = file.Observations.Where(o => o is not PriceBar p || p.ListingId != listing.ListingId || p.SessionCloseAt < at)
            .Select(o => o switch { Listing l when l.ListingId == listing.ListingId => l with { EffectiveUntil = at },
                Membership m when m.ListingId == listing.ListingId => m with { EffectiveUntil = at }, _ => o }).Append(second).Append(fx)
            .Append(survivor).Append(membership).Concat(prices).ToImmutableArray() };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options)); node["datasetSha256"] = sha;
        node["terminals"]![0]!["delivery"]!["booking"]!["datasetSha256"] = sha;
        var term = node["terminals"]![0]!.DeepClone(); term["listingId"] = listing.ListingId; term["actionId"] = second.ActionId; term["observationId"] = second.Id;
        term["evidence"]!["entitlementAt"] = at.ToString("O"); term["evidence"]!["availableAt"] = at.ToString("O");
        term["delivery"]!["sourceSettlementId"] = "second-settlement"; term["delivery"]!["settledAt"] = paid.ToString("O");
        term["delivery"]!["availableAt"] = paid.ToString("O"); term["delivery"]!["fxObservationId"] = fx.Id;
        term["markPolicy"]!["availableAt"] = at.ToString("O"); node["terminals"]!.AsArray().Add(term);
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var one = ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))); Assert.Equal("succeeded", one.Status);
        var preview = store.PrepareRawPaperBatch(account.Id, new(one.ReceiptSha256));
        Assert.Equal(2, TerminalClaimBalance(preview.ProjectedBalance).GetProperty("count").GetInt32());
        Assert.Equal(39.48m, TerminalClaimBalance(preview.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(0m, preview.ProjectedBalance.CashGbp); Assert.Empty(preview.ProjectedBalance.Holdings);
        var two = ApplyPrepared(store.RawPaperAccounts().Single(), preview); Assert.Equal("succeeded", two.Status);
        var third = store.PrepareRawPaperBatch(account.Id, new(two.ReceiptSha256));
        Assert.Equal(34.5425m, third.ProjectedBalance.CashGbp); Assert.Equal(54.2925m, third.ProjectedBalance.EquityGbp);
        Assert.Equal(.77m, third.ProjectedBalance.FeesGbp); Assert.Equal(.246875m, third.ProjectedBalance.Holdings["ccc"].Quantity);
        Assert.Equal(0, TerminalClaimBalance(third.ProjectedBalance).GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("negative-count")]
    [InlineData("large-count")]
    [InlineData("negative-fee")]
    [InlineData("total")]
    [InlineData("empty-count")]
    [InlineData("overflow")]
    [InlineData("legacy")]
    [InlineData("mixed")]
    public void RawPaperTerminalClaimInspectionRefusesCorruptOrMixedSummaries(string mutation)
    {
        var account = store.CreateRawPaperAccount(mutation == "legacy" ? TerminalRequest() : TerminalClaimRequest());
        Assert.Equal("succeeded", ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))).Status);
        RewritePaperAccountMetadata(account.Id, node =>
        {
            var balance = node["balance"]!;
            if (mutation == "missing") { balance.AsObject().Remove("terminalClaims"); return; }
            if (mutation == "legacy") { balance["terminalClaims"] = JsonNode.Parse("{\"count\":0,\"netExpectedValueGbp\":0,\"expectedFeesGbp\":0,\"expectedEquityGbp\":17.27125}"); return; }
            var claim = balance["terminalClaims"]!;
            switch (mutation)
            {
                case "negative-count": claim["count"] = -1; break;
                case "large-count": claim["count"] = 9; break;
                case "negative-fee": claim["expectedFeesGbp"] = -.01m; break;
                case "total": claim["expectedEquityGbp"] = 100m; break;
                case "empty-count": claim["count"] = 0; break;
                case "overflow": balance["equityGbp"] = decimal.MaxValue; claim["netExpectedValueGbp"] = decimal.MaxValue; break;
                case "mixed": balance["dividendClaims"] = claim.DeepClone(); break;
            }
        });
        Assert.Equal("raw-paper-integrity", Assert.Throws<ResearchException>(() => store.RawPaperAccounts()).Code);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("stock")]
    public void RawPaperTerminalClaimWithoutCutoffOwnershipCreatesNoEstimateOrActualFee(string kind)
    {
        var node = TerminalClaimNode(kind, cutoff: fixture.Create.FirstDecisionAt.AddDays(-1));
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp); Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp);
        Assert.Equal(0, TerminalClaimBalance(preview.ProjectedBalance).GetProperty("count").GetInt32());
        Assert.Equal(19.75m, TerminalClaimBalance(preview.ProjectedBalance).GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Equal("succeeded", ApplyPrepared(account, preview).Status);
    }

    [Fact]
    public void RawPaperTerminalClaimConfirmedZeroNeedsNoMarkPolicyOrReceivable()
    {
        var account = store.CreateRawPaperAccount(TerminalClaimRequest("zero")); var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(0m, first.ProjectedBalance.EquityGbp); Assert.Equal(0, TerminalClaimBalance(first.ProjectedBalance).GetProperty("count").GetInt32());
        var operation = ApplyPrepared(account, first); Assert.Equal("succeeded", operation.Status);
        Assert.Equal(19.75m, store.PrepareRawPaperBatch(account.Id, new(operation.ReceiptSha256)).ProjectedBalance.EquityGbp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperTerminalClaimRequiresFirstDecisionCoverageEvenWithoutOwnership(bool late)
    {
        var node = TerminalClaimNode(cutoff: fixture.Create.FirstDecisionAt.AddDays(-1), settlement: fixture.Create.FirstDecisionAt.AddDays(5));
        var term = node["terminals"]![0]!;
        if (late) term["markPolicy"]!["availableAt"] = fixture.Create.FirstDecisionAt.AddTicks(1).ToString("O");
        else term.AsObject().Remove("markPolicy");
        Assert.Equal("raw-paper-terminal-mark-coverage", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Theory]
    [InlineData(.01)]
    [InlineData(.02)]
    public void RawPaperTerminalClaimStockFeeFloorCannotBorrowAnUnexpectedActualFee(double fee)
    {
        var node = TerminalClaimNode("stock"); node["terminals"]![0]!["delivery"]!["fixedFeeGbp"] = (decimal)fee;
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var first = ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))); Assert.Equal("succeeded", first.Status);
        if (fee > .01)
        {
            Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
            Assert.Equal(1, store.RawPaperAccounts().Single().AcceptedPeriods); Assert.Single(store.RawPaperOperations(account.Id));
        }
        else Assert.Equal(39.49m, store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256)).ProjectedBalance.EquityGbp);
    }

    [Fact]
    public void RawPaperTerminalClaimStopAndCreationRetryRetainThePendingEstimate()
    {
        var request = TerminalClaimRequest(); var account = store.CreateRawPaperAccount(request);
        Assert.Equal("succeeded", ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))).Status);
        var stopped = store.StopRawPaperAccount(account.Id); Assert.Equal(0m, stopped.Balance.CashGbp);
        Assert.Equal(19.74m, TerminalClaimBalance(stopped.Balance).GetProperty("expectedEquityGbp").GetDecimal());
        var changed = TerminalClaimNode(expectedWithheld: 0); changed["requestId"] = request.RequestId;
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(changed.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "inputs-hidden")); store = new(root);
        Assert.Equal(JsonSerializer.Serialize(stopped, RawReplayJson.Options), JsonSerializer.Serialize(store.CreateRawPaperAccount(request), RawReplayJson.Options));
    }

    [Theory]
    [InlineData(.1, .1)]
    [InlineData(105, 3)]
    public void RawPaperTerminalClaimFutureActualFxAndDeductionsCannotImproveTheFirstMark(double withheld, double rate)
    {
        var node = TerminalClaimNode(); var old = node["datasetSha256"]!.GetValue<string>();
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", old + ".json")));
        file = file with { Observations = file.Observations.Select(o => o is FxRate { Id: "terminal-settlement-fx" } f
            ? f with { QuoteUnitsPerBase = (decimal)rate } : o).ToImmutableArray() };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        node["datasetSha256"] = sha; node["terminals"]![0]!["delivery"]!["booking"]!["datasetSha256"] = sha;
        node["terminals"]![0]!["delivery"]!["withheldPerShare"] = (decimal)withheld;
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.74m, TerminalClaimBalance(first.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(0m, first.ProjectedBalance.CashGbp); Assert.Equal(.25m, first.ProjectedBalance.FeesGbp);
    }

    [Fact]
    public void RawPaperTerminalClaimExactFinalSettlementNeedsNoUnpaidPolicy()
    {
        var node = TerminalClaimNode(settlement: fixture.Create.Decisions[13]); node["terminals"]![0]!.AsObject().Remove("markPolicy");
        node["decisions"] = JsonSerializer.SerializeToNode(fixture.Create.Decisions.Take(14), RawReplayJson.Options);
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(17.27125m, preview.ProjectedBalance.EquityGbp); Assert.Equal(0, TerminalClaimBalance(preview.ProjectedBalance).GetProperty("count").GetInt32());
        Assert.Equal("succeeded", ApplyPrepared(account, preview).Status); Assert.Equal("completed", store.RawPaperAccounts().Single().Status);
    }

    [Theory]
    [InlineData(.01)]
    [InlineData(.02)]
    public void RawPaperTerminalClaimNegativeExpectedCashReservesTheFeeWithoutBorrowing(double actualFee)
    {
        var node = TerminalClaimNode(expectedWithheld: 110m); var delivery = node["terminals"]![0]!["delivery"]!;
        delivery["withheldPerShare"] = 110m; delivery["fxObservationId"] = null; delivery["fixedFeeGbp"] = (decimal)actualFee;
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(-.01m, TerminalClaimBalance(first.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        var accepted = ApplyPrepared(account, first); Assert.Equal("succeeded", accepted.Status);
        if (actualFee > .01) Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(accepted.ReceiptSha256))).Code);
        else Assert.Equal(19.74m, store.PrepareRawPaperBatch(account.Id, new(accepted.ReceiptSha256)).ProjectedBalance.EquityGbp);
        Assert.Equal(1, store.RawPaperAccounts().Single().AcceptedPeriods); Assert.Single(store.RawPaperOperations(account.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawPaperTerminalClaimStockPolicyCannotInventCashOrWithholding(bool currency)
    {
        var node = TerminalClaimNode("stock"); var policy = node["terminals"]![0]!["markPolicy"]!;
        if (currency) policy["currency"] = "USD"; else policy["expectedWithheldPerShare"] = 1m;
        Assert.Equal("raw-paper-terminal-policy", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
    }

    [Theory]
    [InlineData("expectedWithheldPerShare")]
    [InlineData("expectedFeeGbp")]
    [InlineData("sourceReference")]
    public void RawPaperTerminalClaimNeverInfersOmittedExpectedTerms(string field)
    {
        var node = TerminalClaimNode(); node["terminals"]![0]!["markPolicy"]!.AsObject().Remove(field);
        Assert.Throws<JsonException>(() => node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)); Assert.Empty(store.RawPaperAccounts());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawPaperTerminalClaimLateProofCannotHideAKnownHeldAction(bool held)
    {
        var node = TerminalClaimNode(cutoff: held ? null : fixture.Create.FirstDecisionAt.AddDays(-1));
        node["terminals"]![0]!["evidence"]!["availableAt"] = fixture.Create.Decisions[13].AddTicks(1).ToString("O");
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        if (held) Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        else Assert.Equal(19.75m, store.PrepareRawPaperBatch(account.Id, new(null)).ProjectedBalance.EquityGbp);
        Assert.Empty(store.RawPaperOperations(account.Id));
    }
}
