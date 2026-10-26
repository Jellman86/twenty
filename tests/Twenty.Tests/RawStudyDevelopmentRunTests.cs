using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudyDevelopmentRunTests
{
    private static byte[] Write<T>(T value, int maximum)
    { var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RawReplayJson.Options); Assert.True(bytes.Length <= maximum); return bytes; }

    internal static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar) Fixture(decimal fee = 0)
    {
        var file = DatasetJson.Read(UniverseDiagnosticTests.DatasetBytes()); var rows = file.Observations.ToBuilder();
        var grid = Enumerable.Range(0, 73).Select(i => new DateTimeOffset(2010, 2, 1, 23, 0, 0, TimeSpan.Zero).AddMonths(i).AddDays(-1)).ToImmutableArray();
        foreach (var bar in file.Observations.OfType<PriceBar>())
        {
            rows.Add(bar with { Id = "raw-" + bar.Id, Basis = PriceBasis.Raw, Open = 100, High = 100, Low = 100, Close = 100, Volume = 1000000 });
            var close = bar.SessionCloseAt.AddDays(1);
            rows.Add(bar with { Id = "next-" + bar.Id, Basis = PriceBasis.Raw, SessionDate = DateOnly.FromDateTime(close.UtcDateTime),
                SessionCloseAt = close, Evidence = bar.Evidence with { AvailableAt = close.AddMinutes(1), PublishedAt = close.AddMinutes(1) },
                Open = 100, High = 100, Low = 100, Close = 100, Volume = 1000000 });
        }
        foreach (var at in grid)
        {
            var observed = at.AddHours(-9); var evidence = file.Observations[0].Evidence with { AvailableAt = observed, PublishedAt = observed };
            rows.Add(new FxRate("fx-" + at.ToUnixTimeSeconds(), evidence, "USD", "GBP", .8m, observed, FxPurpose.Executable));
        }
        file = file with { Observations = rows.ToImmutable() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        var days = Enumerable.Range(0, 2300).Select(i => new DateOnly(2010, 1, 1).AddDays(i)).Select(d => new ExecutionDay(d,
            new DateTimeOffset(d.Year,d.Month,d.Day,14,30,0,TimeSpan.Zero),new DateTimeOffset(d.Year,d.Month,d.Day,21,0,0,TimeSpan.Zero))).ToImmutableArray();
        var calendar = new ExecutionCalendar(file.Manifest.Calendars.Single(), "XNAS", days, "urn:twenty:authored:raw-study");
        var plan = RawUniverseStudyProtocol.Declare(Convert.ToHexStringLower(SHA256.HashData(bytes)), "research-universe", true,
            "engineering", new string('b',64), "authored-engine-v1", grid,20,new(45,2,0,fee,new(0,0,1,1)),
            [new(calendar.Reference.Id,calendar.Venue,calendar.Sha256)],Convert.ToHexStringLower(SHA256.HashData("[]"u8)));
        return (plan,bytes,calendar);
    }

    [Fact]
    public void AllThreeAccountsReceiveExactlyThirtyFiveDepositsAndNoHoldoutFunding()
    {
        var f=Fixture(); var report=RawStudyDevelopmentRun.Run(f.Plan,"momentum:1:base",f.Dataset,
            new Dictionary<string,ExecutionCalendar>{{f.Calendar.Reference.Id,f.Calendar}});
        Assert.Equal(new[]{"momentum","equal_weight","cash"},report.Strategies.Select(s=>s.Strategy));
        Assert.All(report.Strategies,s=>{ Assert.Equal(700,s.ContributionsGbp);Assert.Equal(700,s.EndingEquityGbp);
            Assert.Equal(35,s.Months.Length);Assert.Equal(f.Plan.DevelopmentEnd,s.Months[^1].EndAt);
            Assert.All(s.Months,m=>Assert.Equal(0,m.PeriodReturn)); });
    }

    [Fact]
    public void IndependentFeeArithmeticDistinguishesOneAndTwoAssetAccountsFromCash()
    {
        var f=Fixture(.25m); var report=RawStudyDevelopmentRun.Run(f.Plan,"momentum:1:base",f.Dataset,
            new Dictionary<string,ExecutionCalendar>{{f.Calendar.Reference.Id,f.Calendar}});
        var momentum=report.Strategies.Single(s=>s.Strategy=="momentum");var equal=report.Strategies.Single(s=>s.Strategy=="equal_weight");
        Assert.Equal(8.75m,momentum.FeesGbp);Assert.Equal(691.25m,momentum.EndingEquityGbp);
        Assert.Equal(17.5m,equal.FeesGbp);Assert.Equal(682.5m,equal.EndingEquityGbp);
        Assert.Equal(700,report.Strategies.Single(s=>s.Strategy=="cash").EndingEquityGbp);
    }

    [Fact]
    public void RecoveryRejectsForgedLimitationsAndMissingCalendarTables()
    {
        var f = Fixture();
        var report = RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset,
            new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar });
        var bytes = Write(report, RawStudyDevelopmentRun.MaximumReportBytes);
        Assert.Equal(bytes, Write(RawStudyDevelopmentRun.Verify(bytes, _ => f.Dataset), RawStudyDevelopmentRun.MaximumReportBytes));
        Assert.Throws<ResearchException>(() => RawStudyDevelopmentRun.Verify(
            Write(report with { Limitations = ["Certified investment returns"] }, RawStudyDevelopmentRun.MaximumReportBytes), _ => f.Dataset));
        var receipt = report.Strategies[0].Receipt;
        var requestBytes = Convert.FromBase64String(receipt.RequestBase64);
        var request = JsonSerializer.Deserialize<RawReplayDocument>(requestBytes, RawReplayJson.Options)!;
        Assert.Single(request.Calendars);
        Assert.All(request.Events.Select(e => e.Operation).OfType<RawReplayModelledFill>(), fill =>
        { Assert.Null(fill.Calendar); Assert.Equal(request.Calendars[0].Sha256, fill.CalendarSha256); });
        Assert.Equal(receipt.RequestBase64, RawReplayArchive.Execute(requestBytes, _ => f.Dataset).RequestBase64);
        Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            Write(request with { Calendars = [] }, RawReplayArchive.MaximumRequestBytes), _ => f.Dataset));
        Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            Write(request with { Calendars = [request.Calendars[0], request.Calendars[0]] }, RawReplayArchive.MaximumRequestBytes), _ => f.Dataset));
        Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            Write(request with { Events = request.Events.SetItem(0, request.Events[0] with { Operation = new RawReplayDeposit(21) }) },
                RawReplayArchive.MaximumRequestBytes), _ => f.Dataset));
        Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            Write(request with { Events = request.Events.RemoveAt(request.Events.Length - 1) }, RawReplayArchive.MaximumRequestBytes), _ => f.Dataset));
    }

    [Fact]
    public void AlteredReservedPricesCannotChangeDevelopmentWealth()
    {
        var f = Fixture(); var calendars = new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar };
        var baseline = RawStudyDevelopmentRun.Run(f.Plan, "momentum:1:base", f.Dataset, calendars);
        var file = DatasetJson.Read(f.Dataset);
        var changed = file with { Observations = file.Observations.Select(o => o is PriceBar p && p.SessionCloseAt > f.Plan.DevelopmentEnd
            ? p with { Open = p.Open * 100, High = p.High * 100, Low = p.Low * 100, Close = p.Close * 100 } : o).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(changed, DatasetJson.Options);
        var plan = f.Plan with { DatasetSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        var altered = RawStudyDevelopmentRun.Run(plan, "momentum:1:base", bytes, calendars);
        for (var i = 0; i < 3; i++) Assert.Equal(baseline.Strategies[i].Months.ToArray(), altered.Strategies[i].Months.ToArray());
    }

    [Fact]
    public void UndeclaredDevelopmentSplitsAreRefusedButFutureSplitsDoNotGateTheStage()
    {
        var f = Fixture(); var file = DatasetJson.Read(f.Dataset);
        var at = f.Plan.DevelopmentStart.AddDays(1);
        var evidence = file.Observations[0].Evidence with { AvailableAt = at, PublishedAt = at };
        var listing = file.Observations.OfType<Listing>().First();
        var action = new CorporateAction("authored-split", evidence, listing.ListingId, "authored-split", CorporateActionKind.Split,
            at, 2, null, null, null, null, TerminalOutcome.NotApplicable);
        RawStudyDevelopmentReport Run(CorporateAction value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = file.Observations.Add(value) }, DatasetJson.Options);
            return RawStudyDevelopmentRun.Run(f.Plan with { DatasetSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, "momentum:1:base", bytes,
                new Dictionary<string, ExecutionCalendar> { [f.Calendar.Reference.Id] = f.Calendar });
        }
        Assert.Equal("raw-study-action-coverage", Assert.Throws<ResearchException>(() => Run(action)).Code);
        var later = f.Plan.ReservedStart.AddDays(1);
        var future = Run(action with { EffectiveAt = later, Evidence = evidence with { AvailableAt = later, PublishedAt = later } });
        Assert.All(future.Strategies, s => Assert.Equal(700, s.EndingEquityGbp));
    }

    [Fact]
    public void CancellationAbortsBeforeAnyReportIsReturned()
    {
        var f=Fixture();using var cancellation=new CancellationTokenSource();var calls=0;
        Assert.Throws<OperationCanceledException>(()=>RawStudyDevelopmentRun.Run(f.Plan,"momentum:1:base",f.Dataset,
            new Dictionary<string,ExecutionCalendar>{{f.Calendar.Reference.Id,f.Calendar}},cancellation.Token,
            ()=>{if(++calls==2)cancellation.Cancel();}));
    }
}
