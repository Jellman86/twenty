using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudySplitTests
{
    internal static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) Fixture(decimal ratio = 2)
    {
        var f = RawStudyDevelopmentRunTests.Fixture(); var file = DatasetJson.Read(f.Dataset);
        var at = f.Plan.DevelopmentStart.AddDays(15); var listing = file.Observations.OfType<Listing>().First();
        var evidence = file.Observations[0].Evidence with { AvailableAt = at, PublishedAt = at };
        var split = new CorporateAction("study-split", evidence, listing.ListingId, "study-split", CorporateActionKind.Split,
            at, ratio, null, null, null, null, TerminalOutcome.NotApplicable);
        var rows = file.Observations.Select(o => o is PriceBar p && p.ListingId == listing.ListingId && p.Basis == PriceBasis.Raw && p.SessionCloseAt >= at
            ? p with { Open = p.Open / ratio, High = p.High / ratio, Low = p.Low / ratio, Close = p.Close / ratio } : o).Append(split).ToImmutableArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options);
        var sha = Hash(bytes);
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>(
            [new RawSplitSchedule(listing.ListingId, split.ActionId, split.Id, new("split-application", 0, sha))], RawReplayJson.Options);
        return (f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Hash(actions) }, bytes, f.Calendar, actions);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static RawStudyDevelopmentReport Run((RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) f) =>
        RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, f.Actions);

    [Fact]
    public void RecoveryRefusesOmittedSplitOperationsAndForgedNoHoldingDispositions()
    {
        var f = Fixture(); var report = Run(f);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies.Single(s => s.Strategy == "equal_weight").Receipt.RequestBase64), RawReplayJson.Options)!;
        var splitIndex = Enumerable.Range(0, request.Events.Length).Single(i => request.Events[i].Operation is RawReplaySplit);
        var omitted = request with { Events = request.Events.RemoveAt(splitIndex).Select((e, i) => e with { Sequence = i }).ToImmutableArray() };
        Assert.Equal("raw-study-replay-binding", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(omitted, RawReplayJson.Options), _ => f.Dataset)).Code);
        var binding = request.Study!;
        var forged = request with { Study = binding with { Dispositions = binding.Dispositions.SetItem(0, binding.Dispositions[0] with { Disposition = "not-held-before-cutoff" }) } };
        Assert.Equal("raw-study-replay-binding", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(forged, RawReplayJson.Options), _ => f.Dataset)).Code);
    }

    [Fact]
    public void ADisclosureAfterAnotherPurchaseCannotRestateTheHistoricalQuantity()
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset); var action = file.Observations.OfType<CorporateAction>().Single();
        var at = f.Plan.DevelopmentContributions[1].At.AddDays(2);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Replace(action,
            action with { Evidence = action.Evidence with { AvailableAt = at, PublishedAt = at } }) }, DatasetJson.Options);
        var sha = Hash(bytes); var entries = RawStudyEvidence.Actions(f.Actions);
        var actions = JsonSerializer.SerializeToUtf8Bytes(entries.SetItem(0, entries[0] with { Capture = entries[0].Capture with { DatasetSha256 = sha } }), RawReplayJson.Options);
        Assert.Equal("raw-ledger-action-history", Assert.Throws<ResearchException>(() => Run((f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Hash(actions) }, bytes, f.Calendar, actions))).Code);
    }

    [Fact]
    public void DeclaredSplitsRequireTheirExactCatalogAndAValidSourceVintage()
    {
        var f = Fixture();
        Assert.Equal("raw-study-action-catalog", Assert.Throws<ResearchException>(() => RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar })).Code);
        var entries = RawStudyEvidence.Actions(f.Actions);
        var actions = JsonSerializer.SerializeToUtf8Bytes(entries.SetItem(0, entries[0] with { ObservationId = "missing" }), RawReplayJson.Options);
        Assert.Equal("raw-study-action-binding", Assert.Throws<ResearchException>(() => Run((f.Plan with { ActionCatalogSha256 = Hash(actions) }, f.Dataset, f.Calendar, actions))).Code);
        actions = "[]"u8.ToArray();
        Assert.Equal("raw-study-action-coverage", Assert.Throws<ResearchException>(() => Run((f.Plan with { ActionCatalogSha256 = Hash(actions) }, f.Dataset, f.Calendar, actions))).Code);
    }

    [Fact]
    public void SplitDisclosedAtTheMarkIsAppliedBeforeMarkAndNextDeposit()
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset); var action = file.Observations.OfType<CorporateAction>().Single();
        var at = f.Plan.DevelopmentContributions[1].At;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Replace(action,
            action with { Evidence = action.Evidence with { AvailableAt = at, PublishedAt = at } }) }, DatasetJson.Options);
        var sha = Hash(bytes); var entries = RawStudyEvidence.Actions(f.Actions);
        var actions = JsonSerializer.SerializeToUtf8Bytes(entries.SetItem(0, entries[0] with { Capture = entries[0].Capture with { DatasetSha256 = sha } }), RawReplayJson.Options);
        var report = Run((f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Hash(actions) }, bytes, f.Calendar, actions));
        Assert.All(report.Strategies, s => Assert.Equal(700, s.EndingEquityGbp));
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies.Single(s => s.Strategy == "equal_weight").Receipt.RequestBase64), RawReplayJson.Options)!;
        var index = Enumerable.Range(0, request.Events.Length).Single(i => request.Events[i].Operation is RawReplaySplit);
        Assert.Equal(at, request.Events[index].At);
        Assert.Equal("mark-0", request.Events[index + 1].Id); Assert.Equal("deposit-1", request.Events[index + 2].Id);
    }

    [Theory]
    [InlineData(CorporateActionKind.Dividend)]
    [InlineData(CorporateActionKind.Delisting)]
    public void IncomeAndTerminalActionsRemainExplicitlyRefused(CorporateActionKind kind)
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset); var action = file.Observations.OfType<CorporateAction>().Single();
        var changed = kind == CorporateActionKind.Dividend
            ? action with { Action = kind, SplitRatio = null, CashPerShare = 1, CashCurrency = "USD" }
            : action with { Action = kind, SplitRatio = null, TerminalOutcome = TerminalOutcome.Zero };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Replace(action, changed) }, DatasetJson.Options);
        Assert.Equal("raw-study-actions-unsupported", Assert.Throws<ResearchException>(() => Run((f.Plan with { DatasetSha256 = Hash(bytes) }, bytes, f.Calendar, f.Actions))).Code);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(.5)]
    public void PureSplitsPreserveIndependentAccountWealthAndRecordCashDisposition(double ratio)
    {
        var f = Fixture((decimal)ratio);
        var report = RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, f.Actions);
        Assert.All(report.Strategies, s => { Assert.Equal(700, s.EndingEquityGbp); Assert.All(s.Months, m => Assert.Equal(0, m.PeriodReturn)); });
        var equal = report.Strategies.Single(s => s.Strategy == "equal_weight");
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(equal.Receipt.ResultBase64), RawReplayJson.Options)!;
        var split = Assert.Single(result.Entries, e => e.Kind == "split");
        Assert.Equal(.125m * ((decimal)ratio - 1), split.Quantity); Assert.Equal((decimal)ratio, split.SplitRatio);
        using var cash = JsonDocument.Parse(Convert.FromBase64String(report.Strategies.Single(s => s.Strategy == "cash").Receipt.RequestBase64));
        Assert.Equal("not-held-before-cutoff", cash.RootElement.GetProperty("study").GetProperty("dispositions")[0].GetProperty("disposition").GetString());
        var encoded = RawStudyDevelopmentRun.EncodeReport(report);
        _ = RawStudyDevelopmentRun.Verify(encoded, _ => f.Dataset);
    }
}
