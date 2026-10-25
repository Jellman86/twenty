using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawUniverseReplayTests
{
    private static (DatasetFile File, RawSchedulePlan Schedule) Fixture()
    {
        var source = UniverseBacktestTests.Scenario(); var decisionAt = UniverseBacktestTests.Decision(12);
        var booked = decisionAt.AddDays(1); var close = booked.AddHours(-2);
        var rows = source.Observations.ToBuilder();
        foreach (var bar in source.Observations.OfType<PriceBar>().Where(p => p.Basis == PriceBasis.TotalReturnAdjusted))
            if (!source.Observations.OfType<PriceBar>().Any(p => p.ListingId == bar.ListingId && p.SessionCloseAt == bar.SessionCloseAt && p.Basis == PriceBasis.Raw))
                rows.Add(bar with { Id = "raw-" + bar.Id, Basis = PriceBasis.Raw,
                    Open = 100, High = 100, Low = 100, Close = 100 });
        var raw = rows.OfType<PriceBar>().Single(p => p.Id == "raw-aaa-bar-12");
        rows.Add(raw with { Id = "execution-aaa", SessionCloseAt = close, SessionDate = DateOnly.FromDateTime(close.UtcDateTime),
            Evidence = raw.Evidence with { AvailableAt = close.AddMinutes(2), PublishedAt = close.AddMinutes(2) },
            Open = 90, High = 90, Low = 90, Close = 90 });
        var fxAt = decisionAt.AddMinutes(-10);
        rows.Add(new FxRate("decision-fx", raw.Evidence with { AvailableAt = fxAt, PublishedAt = fxAt }, "USD", "GBP", .8m, fxAt, FxPurpose.Executable));
        var file = source with { Observations = rows.ToImmutable() }; var dataset = new PointInTimeDataset(file);
        var spec = new RawUniverseStrategySpec(RawUniverseDecision.Version, "research-universe",
            Enumerable.Range(0, 13).Select(UniverseBacktestTests.Decision).ToImmutableArray(), 45, new(TopK: 1, MaximumWeight: 1), "momentum");
        var allocation = RawUniverseDecision.Allocate(dataset, spec);
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", decisionAt.AddDays(-1), 300);
        var rebalance = RawRebalancePlanner.Plan(ledger, dataset.AsOf(decisionAt), spec.UniverseId, allocation.Weights,
            decisionAt.AddDays(7), 10, 1, TimeSpan.FromDays(45), TimeSpan.FromDays(45));
        var calendar = new ExecutionCalendar(source.Manifest.Calendars.Single(), "XNAS",
            [new(DateOnly.FromDateTime(decisionAt.UtcDateTime), decisionAt.AddHours(-8.5), decisionAt.AddHours(-2)),
             new(DateOnly.FromDateTime(booked.UtcDateTime), booked.AddHours(-8.5), close)]);
        var policy = new FillPolicy(0, 0, 1, 1);
        var execution = ExecutionPlanner.Plan(dataset.AsOf(booked), rebalance.Orders, policy, calendar, TimeSpan.FromDays(45));
        Assert.Empty(execution.Rejections); var fill = Assert.Single(execution.Fills);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (file, new(RawEventSchedule.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current,
            TimeSpan.FromDays(45).Ticks, TimeSpan.FromDays(45).Ticks, FxPurpose.Executable,
            [new(new("deposit", 0, hash), decisionAt.AddDays(-1), new RawReplayDeposit(300)),
             new(new("decision", 0, hash), decisionAt, new RawReplayRebalanceDecision(rebalance) { UniverseStrategy = new(spec, allocation) }),
             new(new("fill", 0, hash), booked, new RawReplayModelledFill(rebalance.Orders[0], policy, fill, 1, calendar) { Rebalance = new("decision", 0) }),
             new(new("mark", 1, hash), booked, new RawReplayMark([], []))], []));
    }

    private static RawReplayReceipt Execute(DatasetFile file, RawSchedulePlan schedule)
    {
        var dataset = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        return RawReplayArchive.Execute(RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(schedule, RawReplayJson.Options), _ => dataset), _ => dataset);
    }

    [Fact]
    public void OriginalDatasetRecomputesStrategyWeightsThenSharesBeforeArchiveRecovery()
    {
        var f = Fixture(); var receipt = Execute(f.File, f.Schedule);
        Assert.Equal(receipt, RawReplayArchive.Verify(JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options),
            _ => JsonSerializer.SerializeToUtf8Bytes(f.File, DatasetJson.Options)));
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(3.6125m, result.Holdings["aaa"].Quantity); Assert.Equal(38.9m, result.CashGbp);
        Assert.Equal(299, Assert.Single(result.Marks).Valuation.ExpectedEquityGbp); Assert.Equal(1, result.FeesGbp);
    }

    [Theory]
    [InlineData("strategy")]
    [InlineData("candidate")]
    [InlineData("scores")]
    [InlineData("panel")]
    [InlineData("weights")]
    [InlineData("decision-time")]
    public void StrategyEvidenceCannotBeChangedWhileRetainingTheSameShareOrders(string change)
    {
        var f = Fixture(); var decision = (RawReplayRebalanceDecision)f.Schedule.Timeline[1].Operation;
        var binding = decision.UniverseStrategy!;
        binding = change switch
        {
            "strategy" => binding with { Spec = binding.Spec with { Strategy = "cash" } },
            "candidate" => binding with { Spec = binding.Spec with { Config = binding.Spec.Config with { TopK = 2 } } },
            "scores" => binding with { Allocation = binding.Allocation with { MomentumScores = binding.Allocation.MomentumScores.SetItem(0, new("aaa", 999)) } },
            "panel" => binding with { Allocation = binding.Allocation with { PanelSha256 = new string('a', 64) } },
            "decision-time" => binding with { Spec = binding.Spec with { Decisions = binding.Spec.Decisions.SetItem(12, binding.Spec.Decisions[^1].AddMinutes(-1)) } },
            _ => binding
        };
        decision = decision with { UniverseStrategy = binding };
        if (change == "weights") decision = decision with { Plan = decision.Plan with { Weights = decision.Plan.Weights.SetItem("aaa", .5m) } };
        Assert.Equal("raw-replay-universe-binding", Assert.Throws<ResearchException>(() => Execute(f.File,
            f.Schedule with { Timeline = f.Schedule.Timeline.SetItem(1, f.Schedule.Timeline[1] with { Operation = decision }) })).Code);
    }

    [Fact]
    public void SliceOnlyReplayRefusesToTrustAnUnverifiedHistoricalStrategy()
    {
        var f = Fixture(); var dataset = new PointInTimeDataset(f.File);
        var items = f.Schedule.Timeline.Select((e, i) => new RawReplayItem(e.Point.Id, i, dataset.AsOf(e.At), e.Operation)).ToArray();
        Assert.Equal("raw-replay-universe-context", Assert.Throws<ResearchException>(() =>
            RawEventReplay.Run(items, TimeSpan.FromDays(45), TimeSpan.FromDays(45), FxPurpose.Executable)).Code);
    }

    [Fact]
    public void CachedRepeatedStrategiesStillConsumeTheHistoricalObservationWorkBudget()
    {
        var f = Fixture(); var listing = f.File.Observations.OfType<Listing>().First();
        var file = f.File with { Observations = f.File.Observations.AddRange(Enumerable.Range(0, 1000).Select(i => listing with
            { Id = "extra-row-" + i, ListingId = "extra-" + i, SecurityId = "extra-security-" + i, CompanyId = "extra-company-" + i,
                Symbol = "EXTRA" + i, ProviderIds = [new("fixture.extra", i.ToString(System.Globalization.CultureInfo.InvariantCulture))] })) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var timeline = f.Schedule.Timeline.Select(e => e with { Point = e.Point with { DatasetSha256 = hash } }).ToImmutableArray();
        var compiled = RawEventSchedule.Compile(JsonSerializer.SerializeToUtf8Bytes(f.Schedule with { Timeline = timeline }, RawReplayJson.Options), _ => bytes);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(compiled, RawReplayJson.Options)!;
        var events = ImmutableArray.CreateBuilder<RawReplayEventSpec>(); events.Add(request.Events[0]);
        for (var i = 0; i < 50; i++) events.Add(request.Events[1] with { Id = "decision-" + i, Sequence = events.Count });
        events.Add(request.Events[^1] with { Sequence = events.Count });
        request = request with { Schedule = null, Events = events.ToImmutable() };
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, RawReplayJson.Options);
        Assert.True(requestBytes.Length < RawReplayArchive.MaximumRequestBytes);
        Assert.Equal("raw-replay-work", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(requestBytes, _ => bytes)).Code);
    }
}
