using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudyFinalRunTests
{
    internal static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) Fixture(string kind)
    {
        var f = RawStudyDistributionTests.Fixture(kind, kind == "dividend" ? 2 : 0, kind == "dividend" ? .1m : 0);
        var at = kind == "dividend" ? f.Plan.ReservedStart.AddDays(15) : f.Plan.FinalContributions[^1].At.AddDays(10);
        var paid = at.AddDays(1); var file = DatasetJson.Read(f.Dataset);
        var rows = file.Observations.Select(o => o switch
        {
            CorporateAction a => a with { EffectiveAt = at, Evidence = a.Evidence with { PublishedAt = at, AvailableAt = at } },
            FxRate fx when fx.Id == "distribution-fx" => fx with { ObservedAt = paid, Evidence = fx.Evidence with { PublishedAt = paid, AvailableAt = paid } },
            _ => o
        }).ToImmutableArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var original = RawStudyEvidence.Actions(f.Actions)[0];
        RawActionSchedule action = original switch
        {
            RawStudyDividendSchedule d => d with { Capture = d.Capture with { DatasetSha256 = sha },
                Evidence = d.Evidence with { EntitlementAt = at, AvailableAt = at },
                Delivery = d.Delivery! with { Booking = d.Delivery.Booking with { DatasetSha256 = sha }, PaidAt = paid, AvailableAt = paid } },
            RawStudyTerminalSchedule t => t with { Capture = t.Capture with { DatasetSha256 = sha },
                Evidence = t.Evidence with { EntitlementAt = at, AvailableAt = at },
                Delivery = t.Delivery is null ? null : t.Delivery with { Booking = t.Delivery.Booking with { DatasetSha256 = sha }, SettledAt = paid, AvailableAt = paid } },
            _ => throw new InvalidOperationException()
        };
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>([action], RawReplayJson.Options);
        return (f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(actions)) }, bytes, f.Calendar, actions);
    }
    [Theory]
    [InlineData("dividend", 481.1, 480.5)]
    [InlineData("zero", 0, 240)]
    [InlineData("cash", 480, 480)]
    [InlineData("stock", 480, 480)]
    public void FinalDistributionsUseFinalOwnershipWithoutImportingDevelopmentBalances(string kind, decimal momentum, decimal equal)
    {
        var f = Fixture(kind);
        var report = RawStudyFinalRun.Run(f.Plan, "momentum:1", "base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, f.Actions);
        Assert.Equal(momentum, report.Strategies[0].EndingEquityGbp); Assert.Equal(equal, report.Strategies[1].EndingEquityGbp);
        Assert.Equal(480, report.Strategies[2].EndingEquityGbp);
        _ = RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report), _ => f.Dataset);
    }
    [Fact]
    public void FinalAccountsStartEmptyWithOnlyTwentyFourReservedDepositsAndIndependentFees()
    {
        var f = RawStudyDevelopmentRunTests.Fixture(.25m);
        var report = RawStudyFinalRun.Run(f.Plan, "momentum:1", "base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, "[]"u8.ToArray());
        Assert.Equal(RawStudyFinalRun.Version, report.Version);
        Assert.Equal("final:momentum:1:base", report.TrialId);
        Assert.All(report.Strategies, s =>
        {
            Assert.Equal(480, s.ContributionsGbp); Assert.Equal(24, s.Months.Length);
            Assert.Equal(f.Plan.ReservedStart, s.Months[0].DecisionAt);
            Assert.Equal(f.Plan.ReservedEnd, s.Months[^1].EndAt);
            Assert.Equal(20, s.Months[0].OpeningEquityAfterDepositGbp);
        });
        Assert.Equal(474, report.Strategies[0].EndingEquityGbp);
        Assert.Equal(468, report.Strategies[1].EndingEquityGbp);
        Assert.Equal(480, report.Strategies[2].EndingEquityGbp);
        var encoded = RawStudyDevelopmentRun.EncodeReport(report);
        Assert.Equal(encoded, RawStudyDevelopmentRun.EncodeReport(RawStudyDevelopmentRun.Verify(encoded, _ => f.Dataset)));
        Assert.Contains("no investment nomination", report.Limitations[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalStagePinsOnlyItsOwnActionsAndCannotMasqueradeAsDevelopmentReplay()
    {
        var f = RawStudySplitTests.Fixture(); var file = DatasetJson.Read(f.Dataset);
        var split = file.Observations.OfType<CorporateAction>().Single(); var at = f.Plan.ReservedStart.AddDays(15);
        var rows = file.Observations.Select(o => o switch
        {
            CorporateAction a => a with { EffectiveAt = at, Evidence = a.Evidence with { AvailableAt = at, PublishedAt = at } },
            PriceBar p when p.Basis == PriceBasis.Raw && p.ListingId == split.ListingId => p with
                { Open = p.SessionCloseAt >= at ? 50 : 100, High = p.SessionCloseAt >= at ? 50 : 100,
                  Low = p.SessionCloseAt >= at ? 50 : 100, Close = p.SessionCloseAt >= at ? 50 : 100 },
            _ => o
        }).ToImmutableArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); var entries = RawStudyEvidence.Actions(f.Actions);
        var actions = JsonSerializer.SerializeToUtf8Bytes(entries.SetItem(0, entries[0] with { Capture = entries[0].Capture with { DatasetSha256 = sha } }), RawReplayJson.Options);
        var plan = f.Plan with { DatasetSha256 = sha, ActionCatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(actions)) };
        var report = RawStudyFinalRun.Run(plan, "momentum:1", "base", bytes,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar }, actions);
        Assert.All(report.Strategies, s => Assert.Equal(480, s.EndingEquityGbp));
        var request = JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(report.Strategies[0].Receipt.RequestBase64), RawReplayJson.Options)!;
        Assert.Single(request.Events.Where(e => e.Operation is RawReplaySplit));
        Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(request with
            { Study = request.Study! with { Version = RawStudyDevelopmentRun.Version } }, RawReplayJson.Options), _ => bytes));
        _ = RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report), _ => bytes);
    }
}
