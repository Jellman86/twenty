using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudyDistributionTests
{
    internal static (RawUniverseStudyPlan Plan, byte[] Dataset, ExecutionCalendar Calendar, byte[] Actions) Fixture(string kind = "dividend", decimal withheld = 0, decimal fee = 0, bool earlyTerminal = false)
    {
        var f = RawStudyDevelopmentRunTests.Fixture(); var file = DatasetJson.Read(f.Dataset); var listing = file.Observations.OfType<Listing>().First();
        var at = kind == "dividend" || earlyTerminal ? f.Plan.DevelopmentStart.AddDays(15) : f.Plan.DevelopmentContributions[^1].At.AddDays(10);
        var paid = at.AddDays(1); var evidence = file.Observations[0].Evidence with { AvailableAt = at, PublishedAt = at };
        var terminal = kind != "dividend"; var outcome = kind switch { "zero" => TerminalOutcome.Zero, "cash" => TerminalOutcome.Cash, "stock" => TerminalOutcome.Stock, _ => TerminalOutcome.NotApplicable };
        var successor = file.Observations.OfType<Listing>().First(l => l.ListingId != listing.ListingId);
        var action = new CorporateAction("study-distribution", evidence, listing.ListingId, "study-distribution",
            terminal ? CorporateActionKind.Delisting : CorporateActionKind.Dividend, at, kind == "stock" ? 1 : null,
            kind == "dividend" ? 8 : kind == "cash" ? 100 : null, kind is "dividend" or "cash" ? "USD" : null,
            null, kind == "stock" ? successor.ListingId : null, outcome);
        var fxEvidence = evidence with { AvailableAt = paid, PublishedAt = paid };
        var rows = file.Observations.Add(action).Add(new FxRate("distribution-fx", fxEvidence, "USD", "GBP", .8m, paid, FxPurpose.Executable));
        if (earlyTerminal)
            rows = rows.Where(o => o is not PriceBar p || p.ListingId != listing.ListingId || p.SessionCloseAt < at).Select(o => o switch
            { Listing l when l.ListingId == listing.ListingId => l with { EffectiveUntil = at },
              Membership m when m.ListingId == listing.ListingId => m with { EffectiveUntil = at }, _ => o }).ToImmutableArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Observations = rows }, DatasetJson.Options); var sha = Hash(bytes);
        var capture = new RawSchedulePoint("distribution-capture", 0, sha); var delivery = new RawSchedulePoint("distribution-delivery", 1, sha);
        RawActionSchedule schedule = !terminal
            ? new RawStudyDividendSchedule(listing.ListingId, action.ActionId, action.Id, capture, new(at, at, "urn:authored:cutoff", new string('a',64)),
                new(delivery,"authored-payment","urn:authored:policy",new string('b',64),paid,paid,"USD",8,withheld,fee,"distribution-fx",RawStudyActionTerms.AllocationRule))
            : new RawStudyTerminalSchedule(listing.ListingId, action.ActionId, action.Id, capture, new(at,at,"urn:authored:cutoff",new string('a',64)),
                kind == "zero" ? null : new(delivery,"authored-settlement","urn:authored:policy",new string('b',64),paid,paid,
                    kind == "cash" ? "USD" : null,kind == "cash" ? 100 : 0,withheld,fee,kind == "cash" ? "distribution-fx" : null,
                    kind == "stock" ? successor.ListingId : null,kind == "stock" ? 1 : 0,RawStudyActionTerms.AllocationRule));
        var actions = JsonSerializer.SerializeToUtf8Bytes<ImmutableArray<RawActionSchedule>>([schedule],RawReplayJson.Options);
        return(f.Plan with { DatasetSha256=sha,ActionCatalogSha256=Hash(actions) },bytes,f.Calendar,actions);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static RawStudyDevelopmentReport Run((RawUniverseStudyPlan Plan,byte[] Dataset,ExecutionCalendar Calendar,byte[] Actions) f) =>
        RawStudyDevelopmentRun.Run(f.Plan,"momentum:1:base",f.Dataset,new Dictionary<string,ExecutionCalendar>{{f.Calendar.Reference.Id,f.Calendar}},f.Actions);
    private static (RawUniverseStudyPlan Plan,byte[] Dataset,ExecutionCalendar Calendar,byte[] Actions) WithActions(
        (RawUniverseStudyPlan Plan,byte[] Dataset,ExecutionCalendar Calendar,byte[] Actions) f, ImmutableArray<RawActionSchedule> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries,RawReplayJson.Options);
        return (f.Plan with { ActionCatalogSha256=Hash(bytes) },f.Dataset,f.Calendar,bytes);
    }

    [Fact]
    public void DeliveredSuccessorSharesParticipateInTheirLaterDividendCutoff()
    {
        var f=Fixture("stock"); var file=DatasetJson.Read(f.Dataset); var old=file.Observations.OfType<CorporateAction>().Single();
        var at=old.EffectiveAt.AddDays(5); var paid=at.AddDays(1); var evidence=old.Evidence with { AvailableAt=at,PublishedAt=at };
        var dividend=new CorporateAction("successor-income",evidence,old.SuccessorListingId!,"successor-income",CorporateActionKind.Dividend,
            at,null,8,"USD",null,null,TerminalOutcome.NotApplicable);
        var rows=file.Observations.Add(dividend).Add(new FxRate("successor-fx",evidence with { AvailableAt=paid,PublishedAt=paid },"USD","GBP",.8m,paid,FxPurpose.Executable));
        var bytes=JsonSerializer.SerializeToUtf8Bytes(file with { Observations=rows },DatasetJson.Options); var sha=Hash(bytes);
        var terminal=(RawStudyTerminalSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        terminal=terminal with { Capture=terminal.Capture with { DatasetSha256=sha },Delivery=terminal.Delivery! with
            { Booking=terminal.Delivery.Booking with { DatasetSha256=sha } } };
        var income=new RawStudyDividendSchedule(dividend.ListingId,dividend.ActionId,dividend.Id,new("successor-capture",0,sha),
            new(at,at,"urn:authored:cutoff",new string('a',64)),new(new("successor-payment",1,sha),"successor-payment","urn:authored:policy",
                new string('b',64),paid,paid,"USD",8,0,0,"successor-fx",RawStudyActionTerms.AllocationRule));
        var report=Run(WithActions((f.Plan with { DatasetSha256=sha },bytes,f.Calendar,f.Actions),[terminal,income]));
        Assert.Equal(756,report.Strategies.Single(s=>s.Strategy=="momentum").EndingEquityGbp);
        Assert.Equal(756,report.Strategies.Single(s=>s.Strategy=="equal_weight").EndingEquityGbp);
        Assert.Equal(700,report.Strategies.Single(s=>s.Strategy=="cash").EndingEquityGbp);
    }

    [Fact]
    public void PositiveEarlyTerminalCashFundsSubsequentPeriodsWithoutRetainingRetiredShares()
    {
        var report=Run(Fixture("cash",earlyTerminal:true));
        Assert.All(report.Strategies,s=>Assert.Equal(700,s.EndingEquityGbp));
        var result=JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(report.Strategies[0].Receipt.ResultBase64),RawReplayJson.Options)!;
        Assert.Single(result.Entries,e=>e.Kind=="terminal-entitlement");
        Assert.Single(result.Entries,e=>e.Kind=="terminal-settlement");
        var retired=result.TerminalEntitlements.Single().Action.ListingId;
        Assert.False(result.Holdings.ContainsKey(retired));
        Assert.Contains(result.Entries,e=>e.Kind=="buy" && e.ListingId!=retired && e.At>result.TerminalEntitlements.Single().SettledAt);
    }

    [Theory]
    [InlineData("dividend","raw-study-claim-policy-missing")]
    [InlineData("cash","raw-ledger-terminal-unsettled")]
    public void DeliveryBeyondTheDevelopmentMarkCannotDisappearOrUseFinalWindowCash(string kind,string expected)
    {
        var f=Fixture(kind);var entry=RawStudyEvidence.Actions(f.Actions)[0];var later=f.Plan.DevelopmentEnd.AddDays(2);
        var altered=entry switch
        { RawStudyDividendSchedule d=>d with { Delivery=d.Delivery! with { PaidAt=later,AvailableAt=later } },
          RawStudyTerminalSchedule t=>t with { Delivery=t.Delivery! with { SettledAt=later,AvailableAt=later } }, _=>entry };
        Assert.Equal(expected,Assert.Throws<ResearchException>(()=>Run(WithActions(f,[altered]))).Code);
    }

    [Fact]
    public void UnsupportedAllocationAndWrongPerShareGrossAreRefusedBeforePerformanceIsCompared()
    {
        var f=Fixture();var entry=(RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        Assert.Equal("raw-study-payment-policy",Assert.Throws<ResearchException>(()=>Run(WithActions(f,[entry with
            { Delivery=entry.Delivery! with { AllocationRule="round-to-pennies" } }]))).Code);
        Assert.Equal("raw-study-payment-policy",Assert.Throws<ResearchException>(()=>Run(WithActions(f,[entry with
            { Delivery=entry.Delivery! with { GrossPerShare=800 } }]))).Code);
    }

    [Fact]
    public void FullyWithheldIncomeUsesNoFxAndChargesTheFixedFeeOnlyAgainstExistingCash()
    {
        var f=Fixture(withheld:8,fee:.1m); var p=f.Plan;
        var plan=RawUniverseStudyProtocol.Declare(p.DatasetSha256,p.UniverseId,p.Synthetic,p.Purpose,p.CodeSha256,p.IdentityScheme,p.Decisions,p.MonthlyGbp,
            p.BaseExecution with { CashReserveGbp=1 },p.Calendars,p.ActionCatalogSha256);
        var entry=(RawStudyDividendSchedule)RawStudyEvidence.Actions(f.Actions)[0];
        var report=Run(WithActions((plan,f.Dataset,f.Calendar,f.Actions),[entry with { Delivery=entry.Delivery! with { FxObservationId=null } }]));
        Assert.Equal(699.9m,report.Strategies.Single(s=>s.Strategy=="momentum").EndingEquityGbp);
        Assert.Equal(699.9m,report.Strategies.Single(s=>s.Strategy=="equal_weight").EndingEquityGbp);
        Assert.Equal(700,report.Strategies.Single(s=>s.Strategy=="cash").EndingEquityGbp);
    }
    [Fact]
    public void DividendAmountsFollowFrozenOwnershipAndFixedFeesDoNotScaleWithShares()
    {
        var f=Fixture(withheld:2,fee:.1m);var report=Run(f);
        // First account owns .25 shares; equal weight owns .125. Net USD/share=6, GBP/USD=.8, fixed fee=.1.
        Assert.Equal(701.1m,report.Strategies.Single(s=>s.Strategy=="momentum").EndingEquityGbp);
        Assert.Equal(700.5m,report.Strategies.Single(s=>s.Strategy=="equal_weight").EndingEquityGbp);
        Assert.Equal(700,report.Strategies.Single(s=>s.Strategy=="cash").EndingEquityGbp);
        _=RawStudyDevelopmentRun.Verify(RawStudyDevelopmentRun.EncodeReport(report),_=>f.Dataset);
    }
    [Theory]
    [InlineData("zero",0,350)]
    [InlineData("cash",700,700)]
    [InlineData("stock",700,700)]
    public void TerminalOutcomesRemoveOldSharesAndSettleOnlyTheDeclaredConsideration(string kind,decimal momentum,decimal equal)
    {
        var report=Run(Fixture(kind));
        Assert.Equal(momentum,report.Strategies.Single(s=>s.Strategy=="momentum").EndingEquityGbp);
        Assert.Equal(equal,report.Strategies.Single(s=>s.Strategy=="equal_weight").EndingEquityGbp);
        Assert.Equal(700,report.Strategies.Single(s=>s.Strategy=="cash").EndingEquityGbp);
    }
}
