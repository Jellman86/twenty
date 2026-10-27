using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class RawPaperContractTests
{
    private static readonly Lazy<(RawPaperPolicy Policy, RawReplayDocument Full, RawReplayStudyBinding? Binding)> fixture = new(() =>
    {
        var f = RawStudyDevelopmentRunTests.Fixture();
        var report = RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar });
        var bound = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        var full = bound with { Study = null };
        return (new(f.Plan.DatasetSha256, f.Plan.UniverseId, full.Events[0].At, f.Plan.MonthlyGbp,
            f.Plan.Candidates[0].Config, f.Plan.BaseExecution, f.Plan.Calendars, full.Build, f.Plan.DevelopmentDecisions), full, bound.Study);
    });
    private static byte[] Bytes(RawReplayDocument doc) => JsonSerializer.SerializeToUtf8Bytes(doc, RawReplayJson.Options);
    internal static RawReplayDocument Periods(RawReplayDocument full, int count)
    {
        var close = full.Events.Where(e => e.Id.StartsWith("mark-", StringComparison.Ordinal) && !e.Id.StartsWith("mark-open-", StringComparison.Ordinal)).ElementAt(count - 1);
        return full with { Events = full.Events.Take(close.Sequence + 1).ToImmutableArray() };
    }
    [Fact]
    public void RawPaperAcceptsOneIndependentCompleteBatchThenOneExactPrefixExtension()
    {
        var f = fixture.Value; var first = Bytes(Periods(f.Full, 1)); var second = Bytes(Periods(f.Full, 2));
        Assert.Equal(f.Policy.FirstDecisionAt, RawPaperContract.ValidateAppend(f.Policy, null, first).Events[0].At);
        Assert.Equal(Periods(f.Full, 2).Events.Length, RawPaperContract.ValidateAppend(f.Policy, first, second).Events.Length);
    }
    [Theory]
    [InlineData("prefix")]
    [InlineData("deposit")]
    [InlineData("strategy")]
    [InlineData("fee")]
    [InlineData("history")]
    [InlineData("partial")]
    [InlineData("two-periods")]
    [InlineData("study")]
    [InlineData("mark")]
    [InlineData("assumed-fill")]
    public void RawPaperRefusesChangedPastOrAnUnboundNewBatch(string change)
    {
        var f = fixture.Value; var first = Periods(f.Full, 1); var next = Periods(f.Full, 2); var offset = first.Events.Length;
        var events = next.Events;
        if (change == "prefix") events = events.SetItem(0, events[0] with { Id = "changed-past" });
        if (change == "deposit") events = events.SetItem(offset, events[offset] with { Operation = new RawReplayDeposit(21) });
        if (change is "strategy" or "fee" or "history")
        {
            var item = events[offset + 2]; var d = (RawReplayRebalanceDecision)item.Operation;
            events = events.SetItem(offset + 2, item with
            {
                Operation = change == "fee"
                ? d with { Plan = d.Plan with { FeePerFillGbp = 99 } }
                : change == "history" ? d with { UniverseStrategy = d.UniverseStrategy! with { Spec = d.UniverseStrategy.Spec with { Decisions = d.UniverseStrategy.Spec.Decisions.SetItem(0, d.UniverseStrategy.Spec.Decisions[0].AddHours(-1)) } } }
                : d with { UniverseStrategy = d.UniverseStrategy! with { Spec = d.UniverseStrategy.Spec with { Strategy = "cash" } } }
            });
        }
        if (change == "partial") events = events.RemoveAt(events.Length - 1);
        if (change == "two-periods") events = Periods(f.Full, 3).Events;
        if (change == "mark") events = events.SetItem(events.Length - 1, events[^1] with { At = events[^1].At.AddDays(1) });
        if (change == "assumed-fill") events = events.SetItem(offset + 1, events[offset + 1] with { Operation = new RawReplayAssumedFill("aaa", 1, 1, Twenty.Core.Data.PriceBasis.Raw, 0) });
        next = next with { Events = events };
        if (change == "study") next = next with { Study = f.Binding };
        Assert.Throws<Twenty.Core.Research.ResearchException>(() => RawPaperContract.ValidateAppend(f.Policy, Bytes(first), Bytes(next)));
    }
}
