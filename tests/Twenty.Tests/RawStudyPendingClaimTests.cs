using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudyPendingClaimTests
{
    internal static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) Fixture(string kind = "dividend", bool final = false)
    {
        var f = final ? RawStudyFinalRunTests.Fixture(kind) : RawStudyDistributionTests.Fixture(kind, kind == "dividend" ? 2 : 0,
            kind == "dividend" ? .1m : 0, earlyTerminal: kind != "dividend");
        var contributions = final ? f.Plan.FinalContributions : f.Plan.DevelopmentContributions;
        var paid = contributions[1].At.AddDays(10); var file = DatasetJson.Read(f.Dataset);
        // Final terminal actions also need a pre-first-mark cutoff for these authored cases.
        var at = contributions[0].At.AddDays(15);
        var rows = file.Observations.Select(o => o switch
        {
            CorporateAction a => a with { EffectiveAt = at, Evidence = a.Evidence with { AvailableAt = at, PublishedAt = at } },
            FxRate fx when fx.Id == "distribution-fx" => fx with { ObservedAt = paid, Evidence = fx.Evidence with { AvailableAt = paid, PublishedAt = paid } },
            _ => o
        }).ToImmutableArray();
        if (final && kind != "dividend")
        {
            var retiring = file.Observations.OfType<CorporateAction>().Single().ListingId;
            rows = rows.Where(o => o is not PriceBar p || p.ListingId != retiring || p.SessionCloseAt < at).Select(o => o switch
            { Listing l when l.ListingId == retiring => l with { EffectiveUntil = at },
              Membership m when m.ListingId == retiring => m with { EffectiveUntil = at }, _ => o }).ToImmutableArray();
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options); var sha = Hash(bytes);
        var original = RawStudyEvidence.Actions(f.Actions)[0];
        var expected = new RawStudyClaimMarkPolicy(kind == "stock" ? null : "USD", kind == "dividend" ? 2 : 0,
            kind == "dividend" ? .1m : 0, at, "urn:authored:independent-expected-terms", new string('c', 64), RawStudyClaimMarks.AllocationRule);
        RawActionSchedule action = original switch
        {
            RawStudyDividendSchedule d => d with { Capture = d.Capture with { DatasetSha256 = sha },
                Evidence = d.Evidence with { EntitlementAt = at, AvailableAt = at }, MarkPolicy = expected,
                Delivery = d.Delivery! with { Booking = d.Delivery.Booking with { DatasetSha256 = sha }, PaidAt = paid, AvailableAt = paid } },
            RawStudyTerminalSchedule t => t with { Capture = t.Capture with { DatasetSha256 = sha },
                Evidence = t.Evidence with { EntitlementAt = at, AvailableAt = at }, MarkPolicy = expected,
                Delivery = t.Delivery! with { Booking = t.Delivery.Booking with { DatasetSha256 = sha }, SettledAt = paid, AvailableAt = paid } },
            _ => throw new InvalidOperationException()
        };
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>([action], RawReplayJson.Options);
        return (f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Hash(actions) }, bytes, f.Calendar, actions);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) Actions(
        (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) f, RawActionSchedule action)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>([action], RawReplayJson.Options);
        return (f.Plan with { ActionCatalogSha256 = Hash(bytes) }, f.Dataset, f.Calendar, bytes);
    }
    private static RawStudyDevelopmentReport Run((RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) f, bool final = false) => final
        ? RawStudyFinalRun.Run(f.Plan, "momentum:1", "base", f.Dataset, new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, f.Actions)
        : RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset, new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, f.Actions);

    [Theory]
    [InlineData(false, 701.1)]
    [InlineData(true, 481.1)]
    public void PendingIncomeCrossesAMarkAndDecisionWithoutFundingOrCountingPaymentTwice(bool final, decimal ending)
    {
        var f = Fixture(final: final); var report = Run(f, final); var momentum = report.Strategies.Single(s => s.Strategy == "momentum");
        Assert.Equal(ending, momentum.EndingEquityGbp);
        Assert.Equal(20, momentum.Months[0].OpeningEquityAfterDepositGbp); Assert.Equal(21.1m, momentum.Months[0].EndingEquityGbp);
        Assert.Equal(41.1m, momentum.Months[1].OpeningEquityAfterDepositGbp); Assert.Equal(41.1m, momentum.Months[1].EndingEquityGbp);
        Assert.Equal(0, momentum.Months[1].PeriodReturn);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(momentum.Receipt.RequestBase64), RawReplayJson.Options)!;
        var decision = (RawReplayRebalanceDecision)request.Events.Single(e => e.Id == "decision-1").Operation;
        Assert.Equal(40, decision.Plan.Valuation.EquityGbp); Assert.Equal(.25m, Assert.Single(decision.Plan.Orders).Quantity);
        Assert.Equal(.5m, Assert.Single(decision.Plan.ClaimAssumptions!.Dividends).ExpectedWithheldLocal);
        _ = RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report), _ => f.Dataset);
    }

    [Theory]
    [InlineData("cash", false)]
    [InlineData("stock", false)]
    [InlineData("cash", true)]
    [InlineData("stock", true)]
    public void PendingTerminalCashOrStockSurvivesDecisionsAndOnlyActualDeliveryChangesHoldings(string kind, bool final)
    {
        var f = Fixture(kind, final); var report = Run(f, final);
        Assert.All(report.Strategies, s => { Assert.Equal(final ? 480 : 700, s.EndingEquityGbp); Assert.All(s.Months, m => Assert.Equal(0, m.PeriodReturn)); });
        var momentum = report.Strategies.Single(s => s.Strategy == "momentum");
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(momentum.Receipt.RequestBase64), RawReplayJson.Options)!;
        var decision = (RawReplayRebalanceDecision)request.Events.Single(e => e.Id == "decision-1").Operation;
        Assert.Equal(20, decision.Plan.Valuation.EquityGbp); Assert.Empty(decision.Plan.Valuation.Positions);
        Assert.Equal(40, decision.Plan.ExpectedTargetEquityGbp); Assert.Equal(.25m, Assert.Single(decision.Plan.Orders).Quantity);
        Assert.Equal(kind == "cash" ? 20 : 0, decision.Plan.DeferredOrderValueGbp);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(momentum.Receipt.ResultBase64), RawReplayJson.Options)!;
        var mark = result.Marks.Single(m => m.Id == "mark-0").Valuation;
        Assert.Equal(20, mark.ExpectedTerminalReceivablesGbp); Assert.Equal(0, mark.HoldingsAndCash.EquityGbp);
        if (kind == "stock") Assert.Equal(.25m, Assert.Single(mark.TerminalReceivables).Successor!.Quantity);
        _ = RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report), _ => f.Dataset);
    }

    [Fact]
    public void FutureActualWithholdingAndFeesCannotImproveAnEarlierExpectedMark()
    {
        var f = Fixture(); var before = Run(f).Strategies[0];
        var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var changed = Run(Actions(f, d with { Delivery = d.Delivery! with { WithheldPerShare = 3, FixedFeeGbp = .2m } })).Strategies[0];
        Assert.Equal(before.Months[0], changed.Months[0]); Assert.Equal(41.1m, changed.Months[1].OpeningEquityAfterDepositGbp);
        Assert.Equal(40.8m, changed.Months[1].EndingEquityGbp);
        Assert.Equal(40.8m / 41.1m - 1m, changed.Months[1].PeriodReturn);
        Assert.Equal(700.8m, changed.EndingEquityGbp); Assert.Equal(.2m, changed.FeesGbp);
    }

    [Fact]
    public void MissingOrFutureExpectationPolicyNeverFallsBackToLaterPaymentTerms()
    {
        var f = Fixture(); var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        Assert.Equal("raw-study-claim-policy-missing", Assert.Throws<ResearchException>(() => Run(Actions(f, d with { MarkPolicy = null }))).Code);
        Assert.Equal("raw-study-claim-policy-unavailable", Assert.Throws<ResearchException>(() => Run(Actions(f,
            d with { MarkPolicy = d.MarkPolicy! with { AvailableAt = f.Plan.DevelopmentContributions[1].At.AddMinutes(1) } }))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClaimStillUnpaidAtTheStageEndRefusesRatherThanDisappearingFromTheScreen(bool final)
    {
        var f = Fixture(final: final); var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var at = (final ? f.Plan.ReservedEnd : f.Plan.DevelopmentEnd).AddDays(1);
        Assert.Equal("raw-ledger-dividend-unpaid", Assert.Throws<ResearchException>(() => Run(Actions(f,
            d with { Delivery = d.Delivery! with { PaidAt = at, AvailableAt = at } }), final)).Code);
    }
    [Fact]
    public void OnePerSharePolicyProducesDistinctAccountEstimatesAndNoFeeForUnownedIncome()
    {
        var f = Fixture(); var report = Run(f);
        Assert.Equal(new decimal[] { 1.1m, .5m, 0 }, report.Strategies.Select(s => s.Months[0].ExpectedClaimValueGbp));
        Assert.Equal(new decimal[] { .1m, .1m, 0 }, report.Strategies.Select(s => s.Months[0].ExpectedClaimFeesGbp));
        Assert.Equal(new[] { 1, 1, 0 }, report.Strategies.Select(s => s.Months[0].PendingClaimCount));
        foreach (var strategy in report.Strategies)
        {
            var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(strategy.Receipt.RequestBase64), RawReplayJson.Options)!;
            var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(strategy.Receipt.ResultBase64), RawReplayJson.Options)!;
            var decision = (RawReplayRebalanceDecision)request.Events.Single(e => e.Id == "decision-1").Operation;
            Assert.Equal(strategy.Months[0].EndingEquityGbp + 20, strategy.Months[1].OpeningEquityAfterDepositGbp);
            Assert.Equal(result.Marks.Single(m => m.Id == "mark-0").Valuation.HoldingsAndCash.EquityGbp + 20, decision.Plan.Valuation.EquityGbp);
        }
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("withholding")]
    [InlineData("fee")]
    [InlineData("source")]
    public void InvalidExpectedPolicyIsRefusedRatherThanReinterpreted(string change)
    {
        var f = Fixture(); var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0]; var p = d.MarkPolicy!;
        p = change switch { "currency" => p with { Currency = "GBP" }, "withholding" => p with { ExpectedWithheldPerShare = 9 },
            "fee" => p with { ExpectedFeeGbp = -1 }, _ => p with { ContentSha256 = "unbound" } };
        Assert.Equal("raw-study-claim-policy", Assert.Throws<ResearchException>(() => Run(Actions(f, d with { MarkPolicy = p }))).Code);
    }

    [Theory]
    [InlineData("assumption")]
    [InlineData("funded-target")]
    [InlineData("deferral")]
    [InlineData("version")]
    [InlineData("missing-assumptions")]
    [InlineData("opening-mark")]
    public void RegisteredReplayReconstructsAllClaimInputsAndDerivedPlans(string change)
    {
        var f = Fixture(); var report = Run(f);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        request = request with { Events = request.Events.Select(e =>
        {
            if (e.Id == "mark-open-1" && change == "opening-mark") return e with { Operation = new RawReplayMark([], []) };
            if (e.Id != "decision-1") return e;
            var d = (RawReplayRebalanceDecision)e.Operation; var p = d.Plan;
            p = change switch
            {
                "assumption" => p with { ClaimAssumptions = p.ClaimAssumptions! with { Dividends = p.ClaimAssumptions.Dividends.SetItem(0,
                    p.ClaimAssumptions.Dividends[0] with { ExpectedWithheldLocal = 0 }) } },
                "funded-target" => p with { Targets = p.Targets.SetItem(0, p.Targets[0] with { FundedTargetQuantity = 99 }) },
                "deferral" => p with { DeferredOrderValueGbp = 99 },
                "version" => p with { Version = RawRebalancePlanner.Version },
                "missing-assumptions" => p with { ClaimAssumptions = null }, _ => p
            };
            return e with { Operation = d with { Plan = p } };
        }).ToImmutableArray() };
        Assert.Equal("raw-study-replay-binding", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(request, RawReplayJson.Options), _ => f.Dataset)).Code);
    }

    [Fact]
    public void ACoherentOfflineAssumptionChangeStillCannotReplaceTheRegisteredCatalog()
    {
        var f = Fixture(); var original = Run(f);
        var action = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var changed = Run(Actions(f, action with { MarkPolicy = action.MarkPolicy! with { ExpectedWithheldPerShare = 0 } }));
        var first = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(original.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        var other = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(changed.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        // Engine-only inputs are explicit caller assertions; their internal recomputation succeeds.
        var offline = other with { Study = null };
        _ = RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(offline, RawReplayJson.Options), _ => f.Dataset);
        // Reattaching the original registration must reconstruct the independently frozen policies.
        Assert.Equal("raw-study-replay-binding", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(other with { Study = first.Study }, RawReplayJson.Options), _ => f.Dataset)).Code);
    }

    [Fact]
    public void RepeatedClaimMarksConsumeWorkEvenWhenTheirMarketSliceIsCached()
    {
        var f = Fixture(); var report = Run(f);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        var file = DatasetJson.Read(f.Dataset); var listing = file.Observations.OfType<Listing>().First();
        file = file with { Observations = file.Observations.AddRange(Enumerable.Range(0, 1000).Select(i => (Observation)(listing with
            { Id = "unheld-listing-" + i, ListingId = "unheld-" + i, SecurityId = "unheld-security-" + i,
              Symbol = "UNHELD" + i, ProviderIds = [new("authored", "unheld-" + i)] }))) };
        var dataset = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options); var sha = Hash(dataset);
        request = request with { Events = request.Events.Select(e => e with { DatasetSha256 = sha }).ToImmutableArray() };
        var prefix = request.Events.TakeWhile(e => e.Id != "mark-0").ToList(); var mark = request.Events.Single(e => e.Id == "mark-0");
        var observations = new PointInTimeDataset(file).AsOf(mark.At).Observations.Length;
        var repeats = (int)(RawReplayArchive.MaximumEventObservationWork / (2 * observations)) + 2;
        for (var i = 0; i < repeats; i++) prefix.Add(mark with { Id = "repeated-" + i, Sequence = prefix.Count });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request with { Study = null, Events = prefix.ToImmutableArray() }, RawReplayJson.Options);
        Assert.True(bytes.Length < RawReplayArchive.MaximumRequestBytes);
        Assert.Equal("raw-replay-work", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(bytes, _ => dataset)).Code);
    }

    [Fact]
    public void ActualPaymentFxReplacesTheInterimEstimateExactlyOnce()
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset);
        var dataset = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Select(o =>
            o is FxRate fx && fx.Id == "distribution-fx" ? fx with { QuoteUnitsPerBase = .9m } : o).ToImmutableArray() }, DatasetJson.Options);
        var sha = Hash(dataset); var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var action = d with { Capture = d.Capture with { DatasetSha256 = sha },
            Delivery = d.Delivery! with { Booking = d.Delivery.Booking with { DatasetSha256 = sha } } };
        var report = Run(Actions((f.Plan with { DatasetSha256 = sha }, dataset, f.Calendar, f.Actions), action));
        var momentum = report.Strategies[0];
        Assert.Equal(21.1m, momentum.Months[0].EndingEquityGbp); Assert.Equal(41.1m, momentum.Months[1].OpeningEquityAfterDepositGbp);
        Assert.Equal(41.25m, momentum.Months[1].EndingEquityGbp); Assert.Equal(41.25m / 41.1m - 1, momentum.Months[1].PeriodReturn);
        Assert.Equal(701.25m, momentum.EndingEquityGbp); Assert.Equal(.1m, momentum.FeesGbp);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(momentum.Receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(.9m, Assert.Single(result.Entries, e => e.Kind == "dividend-payment").GbpPerLocalUnit);
        _ = RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report), _ => dataset);
    }

    [Fact]
    public void PaymentExactlyAtTheMonthlyBoundaryReplacesTheClaimBeforeClosingAndOpening()
    {
        var f = Fixture(); var at = f.Plan.DevelopmentContributions[1].At;
        var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        // Keep the payment's pinned FX at its actual instant, independent of the later booking.
        var file = DatasetJson.Read(f.Dataset);
        var dataset = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Select(o =>
            o is FxRate fx && fx.Id == "distribution-fx" ? fx with { ObservedAt = at, Evidence = fx.Evidence with { AvailableAt = at, PublishedAt = at } } : o).ToImmutableArray() }, DatasetJson.Options);
        var sha = Hash(dataset);
        var report = Run(Actions((f.Plan with { DatasetSha256 = sha }, dataset, f.Calendar, f.Actions), d with
            { Capture = d.Capture with { DatasetSha256 = sha }, Delivery = d.Delivery! with { PaidAt = at, AvailableAt = at, Booking = d.Delivery.Booking with { DatasetSha256 = sha } } }));
        var momentum = report.Strategies[0]; Assert.Equal(0, momentum.Months[0].PendingClaimCount);
        Assert.Equal(21.1m, momentum.Months[0].EndingEquityGbp); Assert.Equal(41.1m, momentum.Months[1].OpeningEquityAfterDepositGbp);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(momentum.Receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Single(result.Entries, e => e.Kind == "dividend-payment"); Assert.Equal(701.1m, momentum.EndingEquityGbp);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing-assumptions")]
    public void EngineOnlyReplayRefusesAnInconsistentClaimPlanVersion(string change)
    {
        var f = Fixture(); var report = Run(f);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        request = request with { Study = null, Events = request.Events.Select(e => e.Id != "decision-1" ? e : e with
        { Operation = ((RawReplayRebalanceDecision)e.Operation) with { Plan = change == "version"
            ? ((RawReplayRebalanceDecision)e.Operation).Plan with { Version = RawRebalancePlanner.Version }
            : ((RawReplayRebalanceDecision)e.Operation).Plan with { ClaimAssumptions = null } } }).ToImmutableArray() };
        Assert.Equal("raw-replay-rebalance-plan", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(request, RawReplayJson.Options), _ => f.Dataset)).Code);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("stock-currency")]
    [InlineData("stock-withholding")]
    public void ZeroAndStockPoliciesCannotInventCashConsideration(string change)
    {
        var f = RawStudyDistributionTests.Fixture(change == "zero" ? "zero" : "stock");
        var t = (RawStudyTerminalSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var p = new RawStudyClaimMarkPolicy(change == "stock-currency" ? "USD" : null, change == "stock-withholding" ? 1 : 0,
            0, t.Evidence.AvailableAt, "urn:authored:invalid-policy", new string('a', 64), RawStudyClaimMarks.AllocationRule);
        Assert.Equal("raw-study-claim-policy", Assert.Throws<ResearchException>(() => Run(Actions(f, t with { MarkPolicy = p }))).Code);
    }

    [Fact]
    public void OverflowingExpectedFeesUseAClaimArithmeticRefusalInsteadOfAnInfrastructureCode()
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset); var a = file.Observations.OfType<CorporateAction>().Single();
        var later = a.EffectiveAt.AddMinutes(1);
        var second = a with { Id = a.Id + "-second", ActionId = a.ActionId + "-second", EffectiveAt = later,
            Evidence = a.Evidence with { AvailableAt = later, PublishedAt = later } };
        var dataset = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Add(second) }, DatasetJson.Options); var sha = Hash(dataset);
        var d = (RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        d = d with { Capture = d.Capture with { DatasetSha256 = sha }, MarkPolicy = d.MarkPolicy! with { ExpectedFeeGbp = decimal.MaxValue },
            Delivery = d.Delivery! with { Booking = d.Delivery.Booking with { DatasetSha256 = sha } } };
        var other = d with { ActionId = second.ActionId, ObservationId = second.Id, Capture = d.Capture with { Id = "second-capture" },
            Evidence = d.Evidence with { EntitlementAt = later, AvailableAt = later },
            Delivery = d.Delivery with { Booking = d.Delivery.Booking with { Id = "second-delivery", TieOrder = 2 }, SourcePaymentId = "second-payment" } };
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>([d, other], RawReplayJson.Options);
        Assert.Equal("raw-study-claim-arithmetic", Assert.Throws<ResearchException>(() => Run((f.Plan with
            { DatasetSha256 = sha, ActionCatalogSha256 = Hash(actions) }, dataset, f.Calendar, actions))).Code);
    }

}
