using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class ExecutionTimingTests
{
    [Fact]
    public void ACalendarGapCannotBeMistakenForAClosedDay()
    {
        var reference = NasdaqSessions.Reference("ny-calendar");
        var monday = new DateOnly(2024, 2, 5);
        Assert.Equal("invalid-execution-calendar", Assert.Throws<ResearchException>(() =>
            new ExecutionCalendar(reference, "XNAS", [new(monday, null, null), new(monday.AddDays(2), null, null)])).Code);
    }

    [Fact]
    public void TheFirstSessionAfterADecisionMustNotBeSkippedBecauseItsCloseMissesTheDeadline()
    {
        var calendar = NasdaqSessions.ExecutionCalendar("ny-calendar", new(2024, 2, 1), new(2024, 2, 8));
        var decision = DateTimeOffset.Parse("2024-02-02T21:00:00Z");
        var next = calendar.Next(decision, DateTimeOffset.Parse("2024-02-05T15:00:00Z"));
        Assert.NotNull(next);
        Assert.Equal(DateTimeOffset.Parse("2024-02-05T14:30:00Z"), next.OpenAt);
    }
    private static readonly DateTimeOffset Decision = DateTimeOffset.Parse("2024-01-31T21:00:00Z");
    private static readonly DateTimeOffset Open = DateTimeOffset.Parse("2024-02-01T14:30:00Z");
    private static readonly DateTimeOffset Booked = DateTimeOffset.Parse("2024-02-05T22:00:00Z");
    private static readonly FillPolicy Flat = new(0, 0, 1, 1);
    private static readonly ExecutionCalendar Calendar = NasdaqSessions.ExecutionCalendar("ny", new(2024, 1, 1), new(2024, 2, 29));
    private static Evidence Known(DateTimeOffset at) => new("source", at, at, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);
    private static DataSlice Market(bool firstBar = true, string currency = "GBP", params Observation[] fx) => Dataset(firstBar, currency, fx).AsOf(Booked);
    private static PointInTimeDataset Dataset(bool firstBar = true, string currency = "GBP", params Observation[] fx)
    {
        var units = new Denomination(currency, QuoteUnit.Major);
        var records = new List<Observation>
        {
            new Listing("listing", Known(Decision.AddDays(-1)), "security", "aaa", "company", "AAA", "XNAS", AssetClass.Stock,
                [new ProviderIdentifier("fixture", "AAA")], units, "ny", Decision.AddDays(-2), null, ListingStatus.Active),
            new PriceBar("later", Known(Booked), "aaa", new(2024, 2, 5), Booked.AddHours(-1), units, PriceBasis.Raw, 200, 200, 200, 200, 1000)
        };
        if (firstBar) records.Add(new PriceBar("first", Known(Open.AddHours(8)), "aaa", new(2024, 2, 1),
            Open.AddHours(6.5), units, PriceBasis.Raw, 100, 100, 100, 100, 1000));
        records.AddRange(fx);
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "execution-timing", Booked, "features.v1",
            [new FeatureDefinition("test", "v1", "Authored", "Declared")],
            [new SourceSnapshot("source", "urn:twenty:authored", "Authored", Booked, null, new string('a', 64), "v1", "Immutable")],
            [Calendar.Reference], [new UniverseDefinition("test", UniverseKind.Research, "Authored")], true);
        return new PointInTimeDataset(new(manifest, [.. records]));
    }

    [Fact]
    public void MissingNextSessionBarIsRefusedRatherThanFilledAtALaterPrice()
    {
        var plan = ExecutionPlanner.Plan(Market(firstBar: false), [new("aaa", true, 100, Decision, Booked)], Flat, Calendar, TimeSpan.FromDays(1));
        Assert.Empty(plan.Fills);
        Assert.Equal(ExecutionRefusal.NoNextSession, Assert.Single(plan.Rejections).Reason);
    }

    [Fact]
    public void AFutureFxObservationCannotPriceAnEarlierOpen()
    {
        var rate = new FxRate("later-fx", Known(Booked), "USD", "GBP", .9m, Booked, FxPurpose.Executable);
        var plan = ExecutionPlanner.Plan(Market(currency: "USD", fx: [rate]),
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = rate.Id }], Flat, Calendar, TimeSpan.FromDays(1));
        Assert.Empty(plan.Fills);
        Assert.Single(plan.Rejections);
    }

    [Theory]
    [InlineData("2024-02-16T21:00:00Z", "2024-02-20T14:30:00Z", "2024-02-20T21:00:00Z")]
    [InlineData("2024-03-08T21:00:00Z", "2024-03-11T13:30:00Z", "2024-03-11T20:00:00Z")]
    [InlineData("2024-11-27T21:00:00Z", "2024-11-29T14:30:00Z", "2024-11-29T18:00:00Z")]
    [InlineData("2025-01-08T21:00:00Z", "2025-01-10T14:30:00Z", "2025-01-10T21:00:00Z")]
    public void ReviewedSessionsHandleHolidaysDstEarlyClosesAndMourningDays(string decision, string open, string close)
    {
        var calendar = NasdaqSessions.ExecutionCalendar("ny", new(2024, 1, 1), new(2025, 1, 31));
        var at = DateTimeOffset.Parse(decision);
        var next = calendar.Next(at, at.AddDays(7));
        Assert.Equal(DateTimeOffset.Parse(open), next!.OpenAt);
        Assert.Equal(DateTimeOffset.Parse(close), next.CloseAt);
        Assert.Equal(64, calendar.Sha256.Length);
    }

    [Theory]
    [InlineData("missing-pin")]
    [InlineData("unknown-pin")]
    [InlineData("late-observation")]
    [InlineData("late-availability")]
    [InlineData("stale")]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("wrong-currency")]
    public void ForeignOrdersNeedTheExactAvailableFreshExecutionRate(string kind)
    {
        var rate = new FxRate("pin", Known(Open.AddSeconds(-30)), "USD", "GBP", .8m, Open.AddSeconds(-30), FxPurpose.Executable);
        rate = kind switch
        {
            "late-observation" => rate with { ObservedAt = Open.AddSeconds(1), Evidence = Known(Open.AddSeconds(1)) },
            "late-availability" => rate with { Evidence = Known(Open.AddSeconds(1)) },
            "stale" => rate with { ObservedAt = Open.AddMinutes(-2), Evidence = Known(Open.AddMinutes(-2)) },
            "reference" => rate with { Purpose = FxPurpose.Reference },
            "reverse" => rate with { BaseCurrency = "GBP", QuoteCurrency = "USD" },
            "wrong-currency" => rate with { BaseCurrency = "EUR" },
            _ => rate
        };
        var id = kind == "missing-pin" ? null : kind == "unknown-pin" ? "absent" : rate.Id;
        var plan = ExecutionPlanner.Plan(Market(currency: "USD", fx: [rate]),
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = id }], Flat, Calendar, TimeSpan.FromMinutes(1));
        Assert.Empty(plan.Fills);
        Assert.Equal(ExecutionRefusal.InvalidExecutionFx, Assert.Single(plan.Rejections).Reason);
    }

    [Fact]
    public void LaterBookingFxDoesNotReplaceThePinnedOpeningRate()
    {
        var pin = new FxRate("pin", Known(Open.AddSeconds(-30)), "USD", "GBP", .8m, Open.AddSeconds(-30), FxPurpose.Executable);
        var later = new FxRate("later-fx", Known(Booked), "USD", "GBP", .9m, Booked, FxPurpose.Executable);
        var reference = new FxRate("mark", Known(Booked), "USD", "GBP", .75m, Booked, FxPurpose.Reference);
        var dataset = Dataset(currency: "USD", fx: [pin, later, reference]);
        var result = RawExecutionRun.Run([new(dataset.AsOf(Decision), dataset.AsOf(Booked),
            _ => [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = pin.Id }])],
            1000, Flat, 1, TimeSpan.FromDays(1), TimeSpan.FromMinutes(1), Calendar, FxPurpose.Reference);
        var receipt = result.Entries[1];
        Assert.Equal(Booked, receipt.At);
        Assert.Equal(Open, receipt.ModelledExecution!.Timing.Session.OpenAt);
        Assert.Equal("first", receipt.ModelledExecution.PriceId);
        Assert.Equal(Calendar.Sha256, receipt.ModelledExecution.Timing.ScheduleSha256);
        Assert.Equal("pin", receipt.FxId);
        Assert.Equal(new string('a', 64), receipt.ModelledExecution.FxSource!.ContentSha256);
        Assert.Equal(new string('a', 64), receipt.ModelledExecution.PriceSource.ContentSha256);
        Assert.Equal(.8m, receipt.GbpPerLocalUnit);
        Assert.Equal(919, result.ClosingValuation.CashGbp);
        Assert.Equal(1069, result.ClosingEquityGbp);
        Assert.Equal("mark", Assert.Single(result.ClosingValuation.Positions).FxId);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("venue")]
    public void TheExecutionScheduleMustBindToTheDatasetCalendarAndVenue(string kind)
    {
        var reference = Calendar.Reference;
        reference = kind switch
        {
            "id" => reference with { Id = "other" },
            "hash" => reference with { ContentSha256 = new string('d', 64) },
            "version" => reference with { Version = "other" },
            _ => reference
        };
        var other = new ExecutionCalendar(reference, kind == "venue" ? "XNYS" : "XNAS", Calendar.Days);
        Assert.Equal("execution-calendar-binding", Assert.Throws<ResearchException>(() => ExecutionPlanner.Plan(Market(),
            [new("aaa", true, 100, Decision, Booked)], Flat, other, TimeSpan.FromDays(1))).Code);
    }

    [Fact]
    public void ADecisionAfterTodaysOpenCannotUseThatEarlierOpen()
    {
        var decision = Open.AddMinutes(1);
        var plan = ExecutionPlanner.Plan(Market(), [new("aaa", true, 100, decision, Booked)], Flat, Calendar, TimeSpan.FromDays(1));
        Assert.Empty(plan.Fills); // February 2 is expected, never February 1 or the later February 5 bar.
        Assert.Equal(ExecutionRefusal.NoNextSession, Assert.Single(plan.Rejections).Reason);
    }

    [Theory]
    [InlineData("reverse")]
    [InlineData("gap")]
    [InlineData("half-session")]
    [InlineData("wrong-local-date")]
    public void MalformedSessionRowsAreNotAcceptedAsCalendarEvidence(string kind)
    {
        var date = new DateOnly(2024, 2, 1);
        var day = new ExecutionDay(date, Open, Open.AddHours(6.5));
        var days = kind switch
        {
            "reverse" => new[] { day with { CloseAt = Open.AddSeconds(-1) } },
            "gap" => new[] { day, day with { Date = date.AddDays(2), OpenAt = Open.AddDays(2), CloseAt = Open.AddDays(2).AddHours(6.5) } },
            "half-session" => new[] { day with { CloseAt = null } },
            _ => new[] { day with { Date = date.AddDays(1) } }
        };
        Assert.Equal("invalid-execution-calendar", Assert.Throws<ResearchException>(() =>
            new ExecutionCalendar(Calendar.Reference, "XNAS", [.. days])).Code);
    }

    [Fact]
    public void UnsupportedOrExhaustedCalendarCoverageIsRefused()
    {
        Assert.Equal("execution-calendar-range", Assert.Throws<ResearchException>(() =>
            NasdaqSessions.ExecutionCalendar("ny", new(2014, 12, 31), new(2015, 1, 2))).Code);
        var shortCalendar = NasdaqSessions.ExecutionCalendar("ny", new(2024, 2, 1), new(2024, 2, 2));
        Assert.Equal("execution-calendar-range", Assert.Throws<ResearchException>(() =>
            shortCalendar.Next(DateTimeOffset.Parse("2024-02-02T21:00:00Z"), Booked)).Code);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("direction")]
    [InlineData("price")]
    [InlineData("bar")]
    [InlineData("schedule-hash")]
    [InlineData("source-hash")]
    [InlineData("session")]
    [InlineData("source")]
    public void TheLedgerRechecksExecutionEvidenceAndRejectionsDoNotConsumeEventIdentity(string kind)
    {
        var market = Market();
        var valid = Assert.Single(ExecutionPlanner.Plan(market, [new("aaa", true, 100, Decision, Booked)], Flat,
            Calendar, TimeSpan.FromDays(1)).Fills);
        var changed = kind switch
        {
            "quantity" => valid with { Quantity = 2 },
            "direction" => valid with { Buy = false },
            "price" => valid with { FillPrice = 101 },
            "bar" => valid with { PriceId = "later" },
            "source-hash" => valid with { PriceSource = valid.PriceSource with { ContentSha256 = new string('d', 64) } },
            "schedule-hash" => valid with { Timing = valid.Timing with { ScheduleSha256 = new string('d', 64) } },
            "session" => valid with { Timing = valid.Timing with { Session = valid.Timing.Session with { OpenAt = Open.AddMinutes(1) } } },
            _ => valid with { Timing = valid.Timing with { SourceReference = "urn:changed" } }
        };
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", Decision, 1000);
        Assert.Equal("raw-ledger-execution-evidence", Assert.Throws<ResearchException>(() =>
            ledger.Fill("fill", market, "aaa", 1, 100, PriceBasis.Raw, 1, TimeSpan.FromDays(1), changed, Calendar)).Code);
        Assert.Equal(1000, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Single(ledger.Entries);
        ledger.Fill("fill", market, "aaa", 1, 100, PriceBasis.Raw, 1, TimeSpan.FromDays(1), valid, Calendar);
        Assert.Equal(899, ledger.CashGbp);
        Assert.Equal(Open, ledger.Holdings["aaa"].OpenedAt);
    }

    [Fact]
    public void ABackdatedExecutionCannotSpendALaterDeposit()
    {
        var market = Market();
        var fill = Assert.Single(ExecutionPlanner.Plan(market, [new("aaa", true, 100, Decision, Booked)], Flat,
            Calendar, TimeSpan.FromDays(1)).Fills);
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", Booked, 1000);
        Assert.Equal("raw-ledger-execution-time", Assert.Throws<ResearchException>(() =>
            ledger.Fill("fill", market, "aaa", 1, 100, PriceBasis.Raw, 1, TimeSpan.FromDays(1), fill, Calendar)).Code);
        Assert.Equal(1000, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Single(ledger.Entries);
    }

    [Fact]
    public void FxAgeAndAvailabilityBoundsAreInclusiveAtTheSessionOpen()
    {
        var rate = new FxRate("pin", Known(Open), "USD", "GBP", .8m, Open.AddMinutes(-1), FxPurpose.Executable);
        var plan = ExecutionPlanner.Plan(Market(currency: "USD", fx: [rate]),
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = rate.Id }], Flat, Calendar, TimeSpan.FromMinutes(1));
        Assert.Equal(rate, Assert.Single(plan.Fills).ExecutionFx);
        var expired = ExecutionPlanner.Plan(Market(currency: "USD", fx: [rate]),
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = rate.Id }], Flat, Calendar, TimeSpan.FromSeconds(59));
        Assert.Equal(ExecutionRefusal.InvalidExecutionFx, Assert.Single(expired.Rejections).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectLedgerBookingCannotSubstituteAlteredOrFutureFx(bool future)
    {
        var pin = new FxRate("pin", Known(Open.AddSeconds(-30)), "USD", "GBP", .8m, Open.AddSeconds(-30), FxPurpose.Executable);
        var late = new FxRate("late", Known(Booked), "USD", "GBP", .9m, Booked, FxPurpose.Executable);
        var market = Market(currency: "USD", fx: [pin, late]);
        var fill = Assert.Single(ExecutionPlanner.Plan(market,
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = pin.Id }], Flat, Calendar, TimeSpan.FromDays(1)).Fills);
        var changed = fill with { ExecutionFx = future ? late : pin with { QuoteUnitsPerBase = 9 } };
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", Decision, 1000);
        Assert.Equal("raw-ledger-execution-fx", Assert.Throws<ResearchException>(() =>
            ledger.Fill("fill", market, "aaa", 1, 100, PriceBasis.Raw, 1, TimeSpan.FromDays(1), changed, Calendar)).Code);
        Assert.Equal(1000, ledger.CashGbp); Assert.Single(ledger.Entries);
    }

    [Fact]
    public void GbpExecutionDoesNotAcceptAFictitiousFxBinding()
    {
        var plan = ExecutionPlanner.Plan(Market(),
            [new PlannedOrder("aaa", true, 100, Decision, Booked) { FxObservationId = "invented" }], Flat, Calendar, TimeSpan.FromDays(1));
        Assert.Empty(plan.Fills);
        Assert.Equal(ExecutionRefusal.InvalidExecutionFx, Assert.Single(plan.Rejections).Reason);
    }

    [Fact]
    public void ScheduleHashPinsActualRowsAndNormalizesEquivalentOffsets()
    {
        var equivalent = new ExecutionCalendar(Calendar.Reference, Calendar.Venue,
            [.. Calendar.Days.Select(d => d with { OpenAt = d.OpenAt?.ToOffset(TimeSpan.FromHours(-5)), CloseAt = d.CloseAt?.ToOffset(TimeSpan.FromHours(-5)) })], Calendar.SourceReference);
        Assert.Equal(Calendar.Sha256, equivalent.Sha256);
        var changed = new ExecutionCalendar(Calendar.Reference, Calendar.Venue,
            [.. Calendar.Days.Select(d => d.OpenAt.HasValue ? d with { OpenAt = d.OpenAt.Value.AddMinutes(1) } : d)], Calendar.SourceReference);
        Assert.NotEqual(Calendar.Sha256, changed.Sha256);
    }

    [Fact]
    public void LateBookingCannotHideAnActionBetweenTheExecutionAndReceipt()
    {
        var action = new CorporateAction("dividend", Known(Booked), "aaa", "dividend-event", CorporateActionKind.Dividend,
            Open.AddDays(1), null, 1, "GBP", null, null, TerminalOutcome.NotApplicable);
        var market = Market(fx: [action]);
        var fill = Assert.Single(ExecutionPlanner.Plan(market, [new("aaa", true, 100, Decision, Booked)], Flat, Calendar, TimeSpan.FromDays(1)).Fills);
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", Decision, 1000);
        Assert.Equal("raw-ledger-execution-action", Assert.Throws<ResearchException>(() =>
            ledger.Fill("fill", market, "aaa", 1, 100, PriceBasis.Raw, 1, TimeSpan.FromDays(1), fill, Calendar)).Code);
        Assert.Equal(1000, ledger.CashGbp); Assert.Empty(ledger.Holdings);
    }

}
