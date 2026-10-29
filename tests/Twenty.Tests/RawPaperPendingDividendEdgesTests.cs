using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    private void RewritePaperAccountMetadata(string id, Action<JsonNode> mutate)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM raw_paper_accounts WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        var node = JsonNode.Parse((string)cmd.ExecuteScalar()!)!; mutate(node);
        var json = node.ToJsonString(); var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        cmd.CommandText = "UPDATE raw_paper_accounts SET json=$json,sha256=$sha WHERE id=$id";
        cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$sha", sha); cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("negative-count")]
    [InlineData("large-count")]
    [InlineData("negative-fee")]
    [InlineData("total")]
    [InlineData("empty-count")]
    [InlineData("balance-null")]
    [InlineData("policy-null")]
    [InlineData("overflow")]
    public void RawPaperPendingDividendInspectionRejectsInvalidStoredSummaryShapes(string mutation)
    {
        var account = store.CreateRawPaperAccount(mutation == "legacy" ? DividendRequest() : PendingDividendRequest());
        if (mutation != "legacy") Assert.Equal("succeeded", ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))).Status);
        RewritePaperAccountMetadata(account.Id, node =>
        {
            if (mutation == "balance-null") { node["balance"] = null; return; }
            if (mutation == "policy-null") { node["policy"] = null; return; }
            var balance = node["balance"]!;
            if (mutation == "legacy")
            {
                balance["dividendClaims"] = JsonNode.Parse("{\"count\":0,\"netExpectedValueGbp\":0,\"expectedFeesGbp\":0,\"expectedEquityGbp\":0}"); return;
            }
            if (mutation == "missing") { balance.AsObject().Remove("dividendClaims"); return; }
            var claims = balance["dividendClaims"]!;
            switch (mutation)
            {
                case "negative-count": claims["count"] = -1; break;
                case "large-count": claims["count"] = 9; break;
                case "negative-fee": claims["expectedFeesGbp"] = -.01m; break;
                case "total": claims["expectedEquityGbp"] = 100m; break;
                case "empty-count": claims["count"] = 0; break;
                case "overflow": balance["equityGbp"] = decimal.MaxValue; claims["netExpectedValueGbp"] = decimal.MaxValue; break;
            }
        });
        Assert.Equal("raw-paper-integrity", Assert.Throws<ResearchException>(() => store.RawPaperAccounts()).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperPendingDividendPaymentExactlyAtTheLastMarkCompletesWithoutAnEstimate(bool includePolicy)
    {
        var node = PendingDividendNode(paymentAt: fixture.Create.Decisions[13]);
        node["decisions"] = JsonSerializer.SerializeToNode(fixture.Create.Decisions.Take(14), RawReplayJson.Options);
        if (!includePolicy) node["dividends"]![0]!.AsObject().Remove("markPolicy");
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var claim = PendingDividendBalance(preview.ProjectedBalance);
        Assert.Equal(0, claim.GetProperty("count").GetInt32());
        Assert.Equal(preview.ProjectedBalance.EquityGbp, claim.GetProperty("expectedEquityGbp").GetDecimal());
        var applied = ApplyPrepared(account, preview); Assert.Equal("succeeded", applied.Status);
        Assert.Equal("completed", store.RawPaperAccounts().Single().Status);
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(applied.ReceiptSha256))).Code);
        var receipt = JsonSerializer.Deserialize<RawReplayReceipt>(store.RawPaperReceiptBytes(applied.Id), RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Single(result.Entries.Where(e => e.Kind == "dividend-payment")); Assert.NotNull(result.DividendEntitlements.Single().PaidAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperPendingDividendRequiresCoverageAtTheFirstDecisionEvenWithoutOwnership(bool late)
    {
        var node = PendingDividendNode(cutoff: fixture.Create.FirstDecisionAt.AddDays(-1));
        if (late) node["dividends"]![0]!["markPolicy"]!["availableAt"] = fixture.Create.FirstDecisionAt.AddTicks(1).ToString("O");
        else node["dividends"]![0]!.AsObject().Remove("markPolicy");
        Assert.Equal("raw-paper-dividend-mark-coverage", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        Assert.Empty(store.RawPaperAccounts());
    }

    [Theory]
    [InlineData(.01)]
    [InlineData(.02)]
    public void RawPaperPendingDividendNegativeClaimReservesCashAndCannotBorrowALargerActualFee(double actualFee)
    {
        var node = PendingDividendNode(); var term = node["dividends"]![0]!;
        term["markPolicy"]!["expectedWithheldPerShare"] = 2m;
        term["delivery"]!["withheldPerShare"] = 2m; term["delivery"]!["fixedFeeGbp"] = (decimal)actualFee; term["delivery"]!["fxObservationId"] = null;
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(-.01m, PendingDividendBalance(first.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(19.74m, PendingDividendBalance(first.ProjectedBalance).GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Equal(0, first.ProjectedBalance.CashGbp); Assert.Equal(.25m, first.ProjectedBalance.FeesGbp);
        var applied = ApplyPrepared(account, first); Assert.Equal("succeeded", applied.Status);
        if (actualFee > .01)
        {
            Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(applied.ReceiptSha256))).Code);
            Assert.Equal(1, store.RawPaperAccounts().Single().AcceptedPeriods); Assert.Single(store.RawPaperOperations(account.Id)); return;
        }
        var second = store.PrepareRawPaperBatch(account.Id, new(applied.ReceiptSha256));
        Assert.Equal(0, second.ProjectedBalance.CashGbp); Assert.Equal(.493625m, second.ProjectedBalance.Holdings.Single().Value.Quantity);
        Assert.Equal(39.49m, second.ProjectedBalance.EquityGbp); Assert.Equal(.51m, second.ProjectedBalance.FeesGbp);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(9, false)]
    public void RawPaperPendingDividendBoundsActualClaimsRatherThanUnheldCatalogRows(int count, bool held)
    {
        var request = PendingDividendRequest(cutoff: held ? null : fixture.Create.FirstDecisionAt.AddDays(-2));
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", request.DatasetSha256 + ".json")));
        var original = file.Observations.OfType<CorporateAction>().Single(); var term = request.Dividends.Single();
        var actions = Enumerable.Range(0, count).Select(i => original with { Id = "pending-observation-" + i, ActionId = "pending-action-" + i,
            EffectiveAt = original.EffectiveAt.AddHours(i), Evidence = original.Evidence with { PublishedAt = original.EffectiveAt.AddHours(i), AvailableAt = original.EffectiveAt.AddHours(i) } }).ToArray();
        file = file with { Observations = file.Observations.Where(o => o is not CorporateAction).Concat(actions).ToImmutableArray() };
        var sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options));
        var terms = actions.Select((action, i) => term with { ObservationId = action.Id, ActionId = action.ActionId,
            Evidence = term.Evidence with { EntitlementAt = action.EffectiveAt, AvailableAt = action.EffectiveAt },
            Delivery = term.Delivery with { Booking = term.Delivery.Booking with { DatasetSha256 = sha }, SourcePaymentId = "pending-payment-" + i,
                PaidAt = term.Delivery.PaidAt.AddHours(i), AvailableAt = term.Delivery.AvailableAt.AddHours(i) } }).ToImmutableArray();
        var account = store.CreateRawPaperAccount(request with { DatasetSha256 = sha, Dividends = terms });
        if (held && count == 9)
        {
            Assert.Equal("raw-paper-dividend-claim-capacity", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
            Assert.Empty(store.RawPaperOperations(account.Id)); return;
        }
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(held ? 8 : 0, PendingDividendBalance(preview.ProjectedBalance).GetProperty("count").GetInt32());
        Assert.Equal(held ? 22.04m : 19.75m, PendingDividendBalance(preview.ProjectedBalance).GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp);
    }

    [Fact]
    public void RawPaperPendingDividendStopAndCreationRetryRetainTheAcceptedEstimate()
    {
        var request = PendingDividendRequest(); var account = store.CreateRawPaperAccount(request);
        var first = ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))); Assert.Equal("succeeded", first.Status);
        var changed = PendingDividendNode(expectedWithheld: .1m).Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)! with { RequestId = request.RequestId };
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(changed)).Code);
        var stopped = store.StopRawPaperAccount(account.Id); Assert.Equal("stopped", stopped.Status);
        Assert.Equal(1, PendingDividendBalance(stopped.Balance).GetProperty("count").GetInt32());
        Assert.Equal(0, stopped.Balance.CashGbp); Assert.Equal(19.75m, stopped.Balance.EquityGbp);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "hidden-inputs")); store = new(root);
        Assert.Equal(JsonSerializer.Serialize(stopped, RawReplayJson.Options), JsonSerializer.Serialize(store.CreateRawPaperAccount(request), RawReplayJson.Options));
        Assert.Equal("raw-paper-stopped", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(first.ReceiptSha256))).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawPaperPendingDividendLateCutoffProofCannotHideAKnownHeldAction(bool held)
    {
        var node = PendingDividendNode(cutoff: held ? null : fixture.Create.FirstDecisionAt.AddDays(-1));
        node["dividends"]![0]!["evidence"]!["availableAt"] = fixture.Create.Decisions[13].AddTicks(1).ToString("O");
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        if (held)
            Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        else
        {
            var preview = store.PrepareRawPaperBatch(account.Id, new(null));
            Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp);
            Assert.Equal(0, PendingDividendBalance(preview.ProjectedBalance).GetProperty("count").GetInt32());
        }
        Assert.Empty(store.RawPaperOperations(account.Id));
    }

    [Theory]
    [InlineData(.1, .1)]
    [InlineData(1.5, 3)]
    public void RawPaperPendingDividendFutureActualFxAndDeductionsCannotAlterTheEarlierMark(double withheld, double rate)
    {
        var request = DividendRequest(paymentAt: fixture.Create.Decisions[13].AddDays(5), withheld: (decimal)withheld, paymentRate: (decimal)rate);
        request = request with { ActionMode = PendingDividendMode, Dividends = request.Dividends.Select(t => t with {
            MarkPolicy = new("USD", .5m, .01m, t.Evidence.AvailableAt, "urn:authored:expected-income", new string('c', 64), RawStudyClaimMarks.AllocationRule)
        }).ToImmutableArray() };
        var account = store.CreateRawPaperAccount(request);
        var first = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(.28625m, PendingDividendBalance(first.ProjectedBalance).GetProperty("netExpectedValueGbp").GetDecimal());
        Assert.Equal(20.03625m, PendingDividendBalance(first.ProjectedBalance).GetProperty("expectedEquityGbp").GetDecimal());
        Assert.Equal(0, first.ProjectedBalance.CashGbp); Assert.Equal(.25m, first.ProjectedBalance.FeesGbp);
    }
}
