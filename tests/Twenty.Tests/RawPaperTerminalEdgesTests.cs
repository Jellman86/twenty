using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Research;
namespace Twenty.Tests;

public sealed partial class RawPaperStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperTerminalInspectionRefusesMissingOrLegacyInjectedCatalogs(bool legacy)
    {
        var account = store.CreateRawPaperAccount(legacy ? fixture.Create : TerminalRequest());
        RewritePaperAccountMetadata(account.Id, node =>
        {
            if (legacy) node["policy"]!["terminals"] = new JsonArray();
            else node["policy"]!.AsObject().Remove("terminals");
        });
        Assert.Equal("raw-paper-integrity", Assert.Throws<ResearchException>(() => store.RawPaperAccounts()).Code);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("zero")]
    [InlineData("stock")]
    public void RawPaperTerminalWithoutCutoffOwnershipCreatesNoClaimSettlementOrFee(string kind)
    {
        // Keep this unused settlement FX older than the decision's independent 0.8 market observation.
        var node = TerminalNode(kind, cutoff: fixture.Create.FirstDecisionAt.AddDays(-2), settlement: fixture.Create.FirstDecisionAt.AddDays(-1));
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(19.75m, preview.ProjectedBalance.EquityGbp); Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp);
        var operation = ApplyPrepared(account, preview); Assert.Equal("succeeded", operation.Status);
        var receipt = JsonSerializer.Deserialize<RawReplayReceipt>(store.RawPaperReceiptBytes(operation.Id), RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Empty(result.TerminalEntitlements); Assert.DoesNotContain(result.Entries, e => e.Kind.StartsWith("terminal-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("stock")]
    public void RawPaperTerminalSettlementAtTheFinalBoundaryPrecedesTheMark(string kind)
    {
        var node = TerminalNode(kind, settlement: fixture.Create.Decisions[13]);
        node["decisions"] = JsonSerializer.SerializeToNode(fixture.Create.Decisions.Take(14), RawReplayJson.Options);
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        Assert.Equal(kind == "cash" ? 17.27125m : 19.75m, preview.ProjectedBalance.EquityGbp);
        Assert.Equal("succeeded", ApplyPrepared(account, preview).Status); Assert.Equal("completed", store.RawPaperAccounts().Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperTerminalFullyWithheldCashHasNoFxAndCannotBorrowTheActualFee(bool fee)
    {
        var node = TerminalNode(); var delivery = node["terminals"]![0]!["delivery"]!;
        delivery["withheldPerShare"] = 110m; delivery["fxObservationId"] = null; delivery["fixedFeeGbp"] = fee ? .01m : 0m;
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        if (fee)
        {
            Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
            Assert.Empty(store.RawPaperOperations(account.Id)); Assert.Equal(0m, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
        }
        else
        {
            var preview = store.PrepareRawPaperBatch(account.Id, new(null)); Assert.Equal(0m, preview.ProjectedBalance.EquityGbp);
            Assert.Equal(.25m, preview.ProjectedBalance.FeesGbp); Assert.Equal("succeeded", ApplyPrepared(account, preview).Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPaperTerminalLateProofCannotConcealAKnownHeldRetirement(bool held)
    {
        var node = TerminalNode(cutoff: held ? null : fixture.Create.FirstDecisionAt.AddDays(-2));
        node["terminals"]![0]!["evidence"]!["availableAt"] = fixture.Create.Decisions[13].AddTicks(1).ToString("O");
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        if (held) Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        else Assert.Equal(19.75m, store.PrepareRawPaperBatch(account.Id, new(null)).ProjectedBalance.EquityGbp);
        Assert.Empty(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperTerminalCannotInterruptTheOrderedFillBatch()
    {
        var fill = fixture.Full.Events.First(e => e.Operation is RawReplayModelledFill);
        var node = TerminalNode(cutoff: fill.At.AddSeconds(-1));
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        Assert.Equal("raw-paper-action-during-batch", Assert.Throws<ResearchException>(() => store.PrepareRawPaperBatch(account.Id, new(null))).Code);
        Assert.Empty(store.RawPaperOperations(account.Id));
    }

    [Fact]
    public void RawPaperTerminalCoherentOfflineSettlementCannotReplaceTheFrozenPolicy()
    {
        var account = store.CreateRawPaperAccount(TerminalRequest()); var preview = store.PrepareRawPaperBatch(account.Id, new(null));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(File.ReadAllBytes(Path.Combine(root, "imports", preview.ReplaySha256 + ".raw-paper.json")), RawReplayJson.Options)!;
        var changed = document with { Events = document.Events.Select(e => e.Operation is RawReplayTerminalSettlement s
            ? e with { Operation = s with { Settlement = s.Settlement with { FeeGbp = 0m } } } : e).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed, RawReplayJson.Options);
        _ = RawReplayArchive.Execute(bytes, sha => File.ReadAllBytes(Path.Combine(root, "universe-datasets", sha + ".json")));
        var failed = store.AdvanceRawPaperAccount(account.Id, Advance(1, changed: changed));
        Assert.Equal("failed", failed.Status); Assert.Equal("raw-paper-action-binding", failed.ErrorCode);
        Assert.Single(store.RawPaperOperations(account.Id)); Assert.Equal(0m, store.RawPaperAccounts().Single().Balance.ContributionsGbp);
    }

    [Fact]
    public void RawPaperTerminalStopAndCreationRetryKeepSettledCapitalWithoutOriginals()
    {
        var request = TerminalRequest(); var account = store.CreateRawPaperAccount(request);
        Assert.Equal("succeeded", ApplyPrepared(account, store.PrepareRawPaperBatch(account.Id, new(null))).Status);
        var stopped = store.StopRawPaperAccount(account.Id); Assert.Equal("stopped", stopped.Status); Assert.Equal(17.27125m, stopped.Balance.CashGbp);
        var changed = TerminalNode(); changed["requestId"] = request.RequestId; changed["terminals"]![0]!["delivery"]!["fixedFeeGbp"] = .02m;
        Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => store.CreateRawPaperAccount(changed.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!)).Code);
        store.Dispose(); Directory.Move(Path.Combine(root, "raw-paper-inputs"), Path.Combine(root, "inputs-hidden")); store = new(root);
        Assert.Equal(JsonSerializer.Serialize(stopped, RawReplayJson.Options), JsonSerializer.Serialize(store.CreateRawPaperAccount(request), RawReplayJson.Options));
    }

    [Theory]
    [InlineData("withheldPerShare")]
    [InlineData("fixedFeeGbp")]
    [InlineData("settledAt")]
    [InlineData("allocationRule")]
    public void RawPaperTerminalNeverInfersOmittedActualTerms(string field)
    {
        var node = TerminalNode(); node["terminals"]![0]!["delivery"]!.AsObject().Remove(field);
        Assert.Throws<JsonException>(() => node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)); Assert.Empty(store.RawPaperAccounts());
    }

    [Fact]
    public void RawPaperTerminalDeliveredSuccessorSharesCanRetireInALaterDeclaredAction()
    {
        var node = TerminalNode("stock"); var sha = node["datasetSha256"]!.GetValue<string>();
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(root, "universe-datasets", sha + ".json")));
        var stock = file.Observations.OfType<CorporateAction>().Single(); var successor = stock.SuccessorListingId!;
        var at = stock.EffectiveAt.AddDays(10);
        var zero = new CorporateAction("successor-terminal-observation", stock.Evidence with { PublishedAt = at, AvailableAt = at }, successor,
            "successor-terminal", CorporateActionKind.Delisting, at, null, null, null, null, null, TerminalOutcome.Zero);
        var rows = file.Observations.Where(o => o is not PriceBar p || p.ListingId != successor || p.SessionCloseAt < at).Select(o => o switch
        {
            Listing l when l.ListingId == successor => l with { EffectiveUntil = at },
            Membership m when m.ListingId == successor => m with { EffectiveUntil = at }, _ => o
        }).Append(zero).ToImmutableArray();
        sha = store.ImportUniverseDataset(JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options));
        node["datasetSha256"] = sha; node["terminals"]![0]!["delivery"]!["booking"]!["datasetSha256"] = sha;
        node["terminals"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { listingId = successor, actionId = zero.ActionId, observationId = zero.Id,
            evidence = new RawTerminalEvidence(at, at, "urn:authored:successor-cutoff", new string('c', 64)), delivery = (RawStudyTerminalDelivery?)null }, RawReplayJson.Options));
        var account = store.CreateRawPaperAccount(node.Deserialize<RawPaperCreateRequest>(RawReplayJson.Options)!);
        var preview = store.PrepareRawPaperBatch(account.Id, new(null)); Assert.Equal(0m, preview.ProjectedBalance.EquityGbp); Assert.Empty(preview.ProjectedBalance.Holdings);
        var operation = ApplyPrepared(account, preview); Assert.Equal("succeeded", operation.Status);
        var receipt = JsonSerializer.Deserialize<RawReplayReceipt>(store.RawPaperReceiptBytes(operation.Id), RawReplayJson.Options)!;
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(2, result.TerminalEntitlements.Length);
        Assert.Equal(.49375m, result.TerminalEntitlements.Single(t => t.Action.ListingId == successor).Quantity);
    }
}
