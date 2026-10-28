using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static (RawSchedulePlan Plan, Dictionary<string, byte[]> Datasets) ScheduleFixture(TerminalOutcome outcome = TerminalOutcome.Cash)
    {
        var dividend = Dividend(); var terminal = Terminal(outcome); var paid = TerminalAt.AddDays(1);
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var inputs = new Dictionary<string, byte[]>();
        string Store(DataSlice market)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture with { Observations = market.Observations }, DatasetJson.Options);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); inputs[hash] = bytes; return hash;
        }
        var initial = Store(Market(BuyAt));
        var ex = Store(Market(dividend.EffectiveAt, actions: [dividend]));
        var exit = Store(TerminalMarket(TerminalAt, terminal, ended: true, extra: [dividend]));
        var settlement = Store(TerminalMarket(paid, terminal, ended: true, extra: [dividend]));
        var actions = ImmutableArray.CreateBuilder<RawActionSchedule>();
        actions.Add(new RawDividendSchedule(dividend.ListingId, dividend.ActionId, dividend.Id,
            new("dividend-capture", 0, ex), EntitlementEvidence(dividend),
            new(new("dividend-payment", 1, settlement), Payment(paid))));
        actions.Add(new RawTerminalSchedule(terminal.ListingId, terminal.ActionId, terminal.Id,
            new("terminal-capture", 0, exit), TerminalProof(terminal),
            outcome == TerminalOutcome.Zero ? null : new(new("terminal-settlement", 0, settlement),
                outcome == TerminalOutcome.Cash ? CashSettlement(paid) : StockSettlement(paid))));
        var plan = new RawSchedulePlan(RawEventSchedule.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current,
            Age.Ticks, Age.Ticks, FxPurpose.Executable,
            [new(new("closing", 2, settlement), paid, new RawReplayMark([], [])),
             new(new("buy", 1, initial), BuyAt, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
             new(new("opening", 0, initial), BuyAt, new RawReplayDeposit(300))], actions.ToImmutable());
        return (plan, inputs);
    }

    [Theory]
    [InlineData(TerminalOutcome.Cash, 291.55)]
    [InlineData(TerminalOutcome.Stock, 321.55)]
    [InlineData(TerminalOutcome.Zero, 142.05)]
    public void EvidenceDirectedScheduleBooksEntitlementsBeforePaymentsAndRetainsItsPlan(TerminalOutcome outcome, decimal expected)
    {
        var (plan, inputs) = ScheduleFixture(outcome);
        var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options);
        var compiled = RawEventSchedule.Compile(planBytes, h => inputs[h]);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(compiled, RawReplayJson.Options)!;
        Assert.NotNull(request.Schedule); Assert.Equal(planBytes, Convert.FromBase64String(request.Schedule.PlanBase64));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(planBytes)), request.Schedule.PlanSha256);
        Assert.Equal(Enumerable.Range(0, request.Events.Length), request.Events.Select(e => e.Sequence));
        Assert.Equal("opening", request.Events[0].Id); Assert.Equal("closing", request.Events[^1].Id);
        var receipt = RawReplayArchive.Execute(compiled, h => inputs[h]);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(expected, result.Marks[^1].Valuation.ExpectedEquityGbp);
        Assert.Equal(300, result.ContributionsGbp);
        Assert.Equal(receipt, RawReplayArchive.Verify(JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options), h => inputs[h]));
    }
    private static byte[] CompileSchedule(RawSchedulePlan plan, Dictionary<string, byte[]> inputs) =>
        RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options), h => inputs[h]);
    private static RawEventReplayResult RunSchedule(RawSchedulePlan plan, Dictionary<string, byte[]> inputs)
    {
        var receipt = RawReplayArchive.Execute(CompileSchedule(plan, inputs), h => inputs[h]);
        return JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
    }
    private static string ScheduleData(Dictionary<string, byte[]> inputs, DataSlice market)
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture with { Observations = market.Observations }, DatasetJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); inputs[hash] = bytes; return hash;
    }

    [Fact]
    public void ScheduledSplitConservesValueAndDoesNotRequireInventedPaymentEvidence()
    {
        var inputs = new Dictionary<string, byte[]>(); var at = BuyAt.AddHours(1);
        var split = Split() with { EffectiveAt = at, Evidence = Known(at) };
        // The post-split close is available two minutes later; mark only after that publication.
        var markAt = at.AddMinutes(3);
        var initial = ScheduleData(inputs, Market(BuyAt)); var changed = ScheduleData(inputs, Market(markAt, price: 50, actions: [split], priceCloseAt: at));
        var plan = new RawSchedulePlan(RawEventSchedule.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current, Age.Ticks, Age.Ticks, FxPurpose.Executable,
            [new(new("opening", 0, initial), BuyAt, new RawReplayDeposit(300)),
             new(new("buy", 1, initial), BuyAt, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
             new(new("closing", 1, changed), markAt, new RawReplayMark([], []))],
            [new RawSplitSchedule(split.ListingId, split.ActionId, split.Id, new("split", 0, changed))]);
        var result = RunSchedule(plan, inputs);
        Assert.Equal(300, result.Marks[0].Valuation.ExpectedEquityGbp); Assert.Equal(4, result.Holdings["listing-a"].Quantity);
        Assert.Equal(at, result.Entries[^1].ActionEffectiveAt); Assert.Equal("split", result.Entries[^1].Kind);
    }

    [Fact]
    public void OptionalSettlementLeavesExplicitPendingClaimsRatherThanCreatingCash()
    {
        var (plan, inputs) = ScheduleFixture(); var dividend = (RawDividendSchedule)plan.Actions[0]; var terminal = (RawTerminalSchedule)plan.Actions[1];
        plan = plan with { Actions = [dividend with { Payment = null }, terminal with { Settlement = null }],
            Timeline = plan.Timeline.SetItem(0, plan.Timeline[0] with { Operation = new RawReplayMark([DividendMark(Dividend())], [TerminalMark(Terminal())]) }) };
        var result = RunSchedule(plan, inputs);
        Assert.Equal(140, result.CashGbp); Assert.Equal(0, result.FeesGbp);
        Assert.Equal(291.55m, result.Marks[0].Valuation.ExpectedEquityGbp);
        Assert.Single(result.Marks[0].Valuation.DividendReceivables); Assert.Single(result.Marks[0].Valuation.TerminalReceivables);
    }

    [Fact]
    public void LateCutoffEvidenceDelaysCaptureAndSettlementWithoutChangingActualPaymentTimeOrFx()
    {
        var (plan, inputs) = ScheduleFixture(); var terminal = (RawTerminalSchedule)plan.Actions[1];
        var actual = terminal.Settlement!.Settlement.SettledAt; var known = actual.AddHours(1);
        terminal = terminal with { Evidence = terminal.Evidence with { AvailableAt = known },
            Settlement = terminal.Settlement with { Booking = terminal.Settlement.Booking with { TieOrder = 1 } } };
        plan = plan with { Actions = plan.Actions.SetItem(1, terminal), Timeline = plan.Timeline.SetItem(0, plan.Timeline[0] with { At = known }) };
        var result = RunSchedule(plan, inputs);
        var claim = Assert.Single(result.TerminalEntitlements);
        Assert.Equal(known, claim.BookedAt); Assert.Equal(actual, claim.SettledAt); Assert.Equal(291.55m, result.CashGbp);
        var settlement = result.Entries.Single(e => e.Kind == "terminal-settlement");
        Assert.Equal(known, settlement.At); Assert.Equal(actual, settlement.TerminalSettlement!.SettledAt); Assert.Equal(.75m, settlement.GbpPerLocalUnit);
        var gap = new RawScheduleEvent(new("gap-mark", 0, terminal.Capture.DatasetSha256), TerminalAt.AddHours(12), new RawReplayMark([DividendMark(Dividend())], []));
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() =>
            RunSchedule(plan with { Timeline = plan.Timeline.Add(gap) }, inputs)).Code);
    }

    [Theory]
    [InlineData("version", "raw-schedule-version")]
    [InlineData("engine", "raw-schedule-version")]
    [InlineData("build", "raw-replay-build")]
    [InlineData("negative-age", "raw-schedule-input")]
    [InlineData("default-actions", "raw-schedule-input")]
    [InlineData("empty-timeline", "raw-schedule-input")]
    [InlineData("null-action", "raw-schedule-action")]
    [InlineData("null-point", "raw-schedule-point")]
    [InlineData("duplicate-id", "raw-schedule-point")]
    [InlineData("path", "raw-schedule-point")]
    [InlineData("negative-order", "raw-schedule-point")]
    [InlineData("duplicate-action", "raw-schedule-action")]
    [InlineData("unbound-action", "raw-schedule-timeline")]
    [InlineData("missing-time", "raw-schedule-timeline")]
    public void InvalidScheduleHeadersAndPointsAreRefusedBeforeDatasetResolution(string kind, string code)
    {
        var (plan, _) = ScheduleFixture(); var first = plan.Timeline[0];
        plan = kind switch
        {
            "version" => plan with { Version = "other" },
            "engine" => plan with { EngineVersion = "other" },
            "build" => plan with { Build = plan.Build with { CoreSha256 = new string('0', 64) } },
            "negative-age" => plan with { MaximumPriceAgeTicks = -1 },
            "default-actions" => plan with { Actions = default },
            "empty-timeline" => plan with { Timeline = [] },
            "null-action" => plan with { Actions = plan.Actions.SetItem(0, null!) },
            "null-point" => plan with { Timeline = plan.Timeline.SetItem(0, first with { Point = null! }) },
            "duplicate-id" => plan with { Timeline = plan.Timeline.SetItem(0, first with { Point = first.Point with { Id = "opening" } }) },
            "path" => plan with { Timeline = plan.Timeline.SetItem(0, first with { Point = first.Point with { DatasetSha256 = "../outside" } }) },
            "negative-order" => plan with { Timeline = plan.Timeline.SetItem(0, first with { Point = first.Point with { TieOrder = -1 } }) },
            "duplicate-action" => plan with { Actions = plan.Actions.Add(plan.Actions[0]) },
            "unbound-action" => plan with { Timeline = plan.Timeline.SetItem(0, first with { Operation = new RawReplaySplit("listing-a", "hidden") }) },
            _ => plan with { Timeline = plan.Timeline.SetItem(0, first with { At = default }) }
        };
        // Nullable/default structs are also rejected by the strict wire reader.
        if (kind is "default-actions" or "null-action" or "null-point")
        {
            var valid = JsonSerializer.Serialize(ScheduleFixture().Plan, RawReplayJson.Options);
            var node = System.Text.Json.Nodes.JsonNode.Parse(valid)!;
            if (kind == "default-actions") node["actions"] = null;
            else if (kind == "null-action") node["actions"]![0] = null;
            else node["timeline"]![0]!["point"] = null;
            var reads = 0;
            void Compile() => RawEventSchedule.Compile(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), _ => { reads++; return []; });
            if (kind == "null-action") Assert.Equal(code, Assert.Throws<ResearchException>(Compile).Code);
            else Assert.Throws<JsonException>(Compile);
            Assert.Equal(0, reads);
            return;
        }
        Assert.Equal(code, Assert.Throws<ResearchException>(() => RawEventSchedule.Compile(
            JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options), _ => throw new InvalidOperationException("must not resolve"))).Code);
    }

    [Theory]
    [InlineData("tie", "raw-schedule-tie")]
    [InlineData("dependency", "raw-schedule-dependency")]
    [InlineData("cutoff", "raw-schedule-cutoff")]
    [InlineData("proof-time", "raw-schedule-time")]
    [InlineData("actual-time", "raw-schedule-time")]
    [InlineData("payment-time", "raw-schedule-payment-time")]
    [InlineData("observation", "raw-schedule-action-vintage")]
    [InlineData("kind", "raw-schedule-action-kind")]
    [InlineData("final-mark", "raw-schedule-final-mark")]
    public void SchedulingRefusesAmbiguousOrderAndUnsupportedTimeOrActionAssertions(string kind, string code)
    {
        var (plan, inputs) = ScheduleFixture(); var terminal = (RawTerminalSchedule)plan.Actions[1];
        plan = kind switch
        {
            "tie" => plan with { Timeline = plan.Timeline.SetItem(1, plan.Timeline[1] with { Point = plan.Timeline[1].Point with { TieOrder = 0 } }) },
            "dependency" => plan with { Actions = plan.Actions.SetItem(1, terminal with { Settlement = terminal.Settlement! with {
                Settlement = terminal.Settlement.Settlement with { SettledAt = TerminalAt, AvailableAt = TerminalAt } } }) },
            "cutoff" => plan with { Actions = plan.Actions.SetItem(1, terminal with { Evidence = terminal.Evidence with { EntitlementAt = TerminalAt.AddTicks(1) } }) },
            "proof-time" => plan with { Actions = plan.Actions.SetItem(1, terminal with { Evidence = terminal.Evidence with { AvailableAt = default } }) },
            "actual-time" => plan with { Actions = plan.Actions.SetItem(1, terminal with { Settlement = terminal.Settlement! with { Settlement = terminal.Settlement.Settlement with { SettledAt = default } } }) },
            "payment-time" => plan with { Actions = plan.Actions.SetItem(1, terminal with { Settlement = terminal.Settlement! with {
                Settlement = terminal.Settlement.Settlement with { AvailableAt = TerminalAt } } }) },
            "observation" => plan with { Actions = plan.Actions.SetItem(1, terminal with { ObservationId = "absent" }) },
            "kind" => plan with { Actions = plan.Actions.SetItem(1, new RawSplitSchedule(terminal.ListingId, terminal.ActionId, terminal.ObservationId, terminal.Capture)) },
            _ => plan with { Timeline = plan.Timeline.SetItem(0, plan.Timeline[0] with { At = BuyAt.AddMinutes(1) }) }
        };
        Assert.Equal(code, Assert.Throws<ResearchException>(() => CompileSchedule(plan, inputs)).Code);
    }

    [Theory]
    [InlineData("age")]
    [InlineData("quantity")]
    [InlineData("plan-hash")]
    [InlineData("binding-version")]
    [InlineData("base64")]
    [InlineData("rehashed-plan")]
    public void RecoveryRecompilesTheOriginalPlanAndRefusesChangedDerivedInstructions(string kind)
    {
        var (plan, inputs) = ScheduleFixture();
        var request = JsonSerializer.Deserialize<RawReplayDocument>(CompileSchedule(plan, inputs), RawReplayJson.Options)!;
        var changedPlan = plan with { MaximumPriceAgeTicks = plan.MaximumPriceAgeTicks + 1 };
        var changedBytes = JsonSerializer.SerializeToUtf8Bytes(changedPlan, RawReplayJson.Options);
        request = kind switch
        {
            "age" => request with { MaximumFxAgeTicks = request.MaximumFxAgeTicks + 1 },
            "quantity" => request with { Events = request.Events.SetItem(1, request.Events[1] with { Operation = new RawReplayAssumedFill("listing-a", 1, 100, PriceBasis.Raw, 0) }) },
            "plan-hash" => request with { Schedule = request.Schedule! with { PlanSha256 = new string('0', 64) } },
            "binding-version" => request with { Schedule = request.Schedule! with { Version = "other" } },
            "base64" => request with { Schedule = request.Schedule! with { PlanBase64 = "!" } },
            _ => request with { Schedule = request.Schedule! with { PlanBase64 = Convert.ToBase64String(changedBytes), PlanSha256 = Convert.ToHexStringLower(SHA256.HashData(changedBytes)) } }
        };
        Assert.Equal("raw-schedule-binding", Assert.Throws<ResearchException>(() =>
            RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(request, RawReplayJson.Options), h => inputs[h])).Code);
    }

    [Fact]
    public void CatalogOrderDoesNotInventTiePriorityOrChangeAccounting()
    {
        var (plan, inputs) = ScheduleFixture(); var other = plan with { Actions = plan.Actions.Reverse().ToImmutableArray(), Timeline = plan.Timeline.Reverse().ToImmutableArray() };
        var first = JsonSerializer.Deserialize<RawReplayDocument>(CompileSchedule(plan, inputs), RawReplayJson.Options)!;
        var second = JsonSerializer.Deserialize<RawReplayDocument>(CompileSchedule(other, inputs), RawReplayJson.Options)!;
        Assert.Equal(JsonSerializer.Serialize(first.Events, RawReplayJson.Options), JsonSerializer.Serialize(second.Events, RawReplayJson.Options));
        Assert.NotEqual(first.Schedule!.PlanSha256, second.Schedule!.PlanSha256);
        Assert.Equal(JsonSerializer.Serialize(RunSchedule(plan, inputs), RawReplayJson.Options), JsonSerializer.Serialize(RunSchedule(other, inputs), RawReplayJson.Options));
    }

    [Fact]
    public void OmittedOrUnownedActionsCannotBecomeSuccessfulCompletedSchedules()
    {
        var (plan, inputs) = ScheduleFixture();
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => RunSchedule(plan with { Actions = [] }, inputs)).Code);
        Assert.Equal("raw-ledger-action-position", Assert.Throws<ResearchException>(() =>
            RunSchedule(plan with { Timeline = plan.Timeline.RemoveAt(1) }, inputs)).Code);
    }

    [Fact]
    public void ScheduledStockDeliveryCanBeFollowedByAnOrdinarySuccessorSplit()
    {
        var (plan, inputs) = ScheduleFixture(TerminalOutcome.Stock); var after = TerminalAt.AddDays(2);
        var split = Split() with { ListingId = "successor", EffectiveAt = after, Evidence = Known(after) };
        var markAt = after.AddMinutes(3);
        var data = ScheduleData(inputs, TerminalMarket(markAt, Terminal(TerminalOutcome.Stock), ended: true, successorPrice: 40, extra: [Dividend(), split], priceCloseAt: after));
        plan = plan with { Actions = plan.Actions.Add(new RawSplitSchedule("successor", split.ActionId, split.Id, new("successor-split", 0, data))),
            Timeline = plan.Timeline.SetItem(0, plan.Timeline[0] with { At = markAt, Point = new("closing", 1, data) }) };
        var result = RunSchedule(plan, inputs);
        Assert.Equal(6, result.Holdings["successor"].Quantity); Assert.Equal(321.55m, result.Marks[0].Valuation.ExpectedEquityGbp);
        Assert.Equal(3, result.TerminalEntitlements[0].SuccessorQuantity); Assert.Equal(2, result.DividendEntitlements[0].Quantity);
    }

    [Fact]
    public void ScheduledLateDividendBookingStillRequiresFxAvailableAtActualPayment()
    {
        var (plan, inputs) = ScheduleFixture(); var dividend = (RawDividendSchedule)plan.Actions[0];
        var paid = dividend.Payment!.Payment.PaidAt; var booked = paid.AddHours(1);
        var lateFx = new FxRate("late-fx", Known(booked), "USD", "GBP", .75m, paid.AddMinutes(-1), FxPurpose.Executable);
        var data = ScheduleData(inputs, TerminalMarket(booked, Terminal(), ended: true, extra: [Dividend()], rates: [lateFx]));
        dividend = dividend with { Payment = dividend.Payment with { Booking = dividend.Payment.Booking with { DatasetSha256 = data },
            Payment = dividend.Payment.Payment with { AvailableAt = booked, FxObservationId = lateFx.Id } } };
        plan = plan with { Actions = plan.Actions.SetItem(0, dividend), Timeline = plan.Timeline.SetItem(0, plan.Timeline[0] with { At = booked.AddMinutes(1) }) };
        Assert.Equal("raw-ledger-dividend-fx", Assert.Throws<ResearchException>(() => RunSchedule(plan, inputs)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedActionMustStillBeTheSelectedVintageWhenItsProofBecomesAvailable(bool revisedTerms)
    {
        var (plan, inputs) = ScheduleFixture(); var terminal = (RawTerminalSchedule)plan.Actions[1]; var known = TerminalAt.AddHours(1);
        var original = Terminal(); var revision = original with { Id = "revised-terminal", CashPerShare = revisedTerms ? 111 : original.CashPerShare,
            Evidence = Known(known) with { RevisionOf = original.Id } };
        var baseHash = ScheduleData(inputs, TerminalMarket(known, original, ended: true, extra: [Dividend()]));
        var snapshot = DatasetJson.Read(inputs[baseHash]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot with { Observations = snapshot.Observations.Add(revision) }, DatasetJson.Options);
        var data = Convert.ToHexStringLower(SHA256.HashData(bytes)); inputs[data] = bytes;
        terminal = terminal with { Capture = terminal.Capture with { DatasetSha256 = data }, Evidence = terminal.Evidence with { AvailableAt = known } };
        Assert.Equal("raw-schedule-action-vintage", Assert.Throws<ResearchException>(() => CompileSchedule(plan with { Actions = plan.Actions.SetItem(1, terminal) }, inputs)).Code);
    }

    [Fact]
    public void UnavailableFutureFxPerturbationDoesNotChangeEarlierScheduledAccounting()
    {
        var (plan, inputs) = ScheduleFixture(); var initial = plan.Timeline[1].Point.DatasetSha256;
        var dataset = DatasetJson.Read(inputs[initial]); var futureAt = TerminalAt.AddDays(10);
        var future = new FxRate("future-only", Known(futureAt), "USD", "GBP", 9, futureAt, FxPurpose.Executable);
        RawEventReplayResult Scenario(decimal rate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dataset with { Observations = dataset.Observations.Add(future with { QuoteUnitsPerBase = rate }) }, DatasetJson.Options);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); inputs[hash] = bytes;
            return RunSchedule(plan with { Timeline = plan.Timeline.Select(e => e.Point.DatasetSha256 == initial ? e with { Point = e.Point with { DatasetSha256 = hash } } : e).ToImmutableArray() }, inputs);
        }
        Assert.Equal(JsonSerializer.Serialize(Scenario(9), RawReplayJson.Options), JsonSerializer.Serialize(Scenario(1), RawReplayJson.Options));
    }

    [Fact]
    public void SchedulingBoundsPlanAndActionCountBeforeResolvingAnyInputs()
    {
        Assert.Equal("raw-schedule-size", Assert.Throws<ResearchException>(() => RawEventSchedule.Compile(new byte[RawEventSchedule.MaximumPlanBytes + 1], _ => throw new InvalidOperationException())).Code);
        var (plan, _) = ScheduleFixture();
        plan = plan with { Actions = Enumerable.Repeat<RawActionSchedule>(new RawSplitSchedule("a", "b", "c", new("p", 0, new string('a', 64))), RawEventSchedule.MaximumActions + 1).ToImmutableArray() };
        Assert.Equal("raw-schedule-input", Assert.Throws<ResearchException>(() => RawEventSchedule.Compile(
            JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options), _ => throw new InvalidOperationException())).Code);
    }

    [Fact]
    public void SchedulingCapsRepeatedActionLookupWorkEvenForCachedDatasets()
    {
        var (plan, inputs) = ScheduleFixture(); var at = BuyAt.AddHours(1); var split = Split() with { EffectiveAt = at, Evidence = Known(at), SplitRatio = 1 };
        var actions = Enumerable.Range(0, 1000).Select(i => split with { Id = "split-row-" + i, ActionId = "split-action-" + i }).ToImmutableArray();
        var data = ScheduleData(inputs, Market(at, actions: actions));
        plan = plan with { Actions = actions.Select((a, i) => (RawActionSchedule)new RawSplitSchedule(a.ListingId, a.ActionId, a.Id, new("split-" + i, i, data))).ToImmutableArray() };
        Assert.Equal("raw-schedule-work", Assert.Throws<ResearchException>(() => CompileSchedule(plan, inputs)).Code);
    }

    [Fact]
    public void ScheduleSnapshotsOriginalPlanBeforeResolverCallbacks()
    {
        var (plan, inputs) = ScheduleFixture(); var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, RawReplayJson.Options); var original = bytes.ToArray();
        var compiled = RawEventSchedule.Compile(bytes, h => { bytes[0] = 32; return inputs[h]; });
        var request = JsonSerializer.Deserialize<RawReplayDocument>(compiled, RawReplayJson.Options)!;
        Assert.Equal(original, Convert.FromBase64String(request.Schedule!.PlanBase64));
    }

    [Fact]
    public void ConfirmedZeroCannotInventASeparateCashSettlement()
    {
        var (plan, inputs) = ScheduleFixture(TerminalOutcome.Zero); var terminal = (RawTerminalSchedule)plan.Actions[1];
        terminal = terminal with { Settlement = new(new("invented", 0, plan.Timeline[0].Point.DatasetSha256), CashSettlement(TerminalAt.AddDays(1))) };
        Assert.Equal("raw-schedule-zero-settlement", Assert.Throws<ResearchException>(() => CompileSchedule(plan with { Actions = plan.Actions.SetItem(1, terminal) }, inputs)).Code);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("proof")]
    [InlineData("implicit-payment")]
    [InlineData("duplicate")]
    public void ScheduleWireRefusesUnknownKindsMissingProofAndImplicitPaymentDefaults(string kind)
    {
        var (plan, _) = ScheduleFixture(); var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(plan, RawReplayJson.Options))!;
        var action = node["actions"]![0]!;
        if (kind == "kind") action["kind"] = "unknown-action";
        else if (kind == "proof") action.AsObject().Remove("evidence");
        else if (kind == "implicit-payment") action.AsObject().Remove("payment");
        var text = node.ToJsonString();
        if (kind == "duplicate") text = text.Replace("\"tieOrder\":0", "\"tieOrder\":0,\"tieOrder\":1");
        var reads = 0;
        Assert.Throws<JsonException>(() => RawEventSchedule.Compile(System.Text.Encoding.UTF8.GetBytes(text), _ => { reads++; return []; }));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ExplicitTiePriorityCannotCreateOwnershipBeforeTheLegalCutoff()
    {
        var (plan, inputs) = ScheduleFixture(); var dividend = (RawDividendSchedule)plan.Actions[0]; var at = Dividend().EffectiveAt;
        dividend = dividend with { Capture = dividend.Capture with { TieOrder = 1 } };
        plan = plan with { Actions = plan.Actions.SetItem(0, dividend), Timeline = plan.Timeline.SetItem(1,
            plan.Timeline[1] with { At = at, Point = plan.Timeline[1].Point with { TieOrder = 0 } }) };
        Assert.Equal("raw-ledger-action-position", Assert.Throws<ResearchException>(() => RunSchedule(plan, inputs)).Code);
    }

}
