using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class ChangingUniversePanelTests
{
    private static DateTimeOffset At(int month, int day, int hour = 0, int minute = 0) =>
        new(2024, month, day, hour, minute, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset[] Decisions = [At(1, 31, 23), At(2, 29, 23), At(3, 31, 23), At(4, 30, 23)];
    private static Evidence Ev(DateTimeOffset available, string? revisionOf = null) =>
        new("fixture-source", available, available, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, revisionOf);

    private enum ExitKind { Delisted, Unexplained }

    private static PriceBar Bar(string id, string listing, int month, int day, PriceBasis basis, decimal close,
        DateTimeOffset? available = null, string? revisionOf = null)
    {
        var sessionClose = At(month, day, 21);
        return new(id, Ev(available ?? sessionClose.AddMinutes(2), revisionOf), listing,
            new DateOnly(2024, month, day), sessionClose, new Denomination("USD", QuoteUnit.Major), basis,
            close, close, close, close, 100m);
    }

    private static Listing ListingRow(string id, string listing, string symbol, DateTimeOffset available,
        DateTimeOffset from, DateTimeOffset? until, ListingStatus status, string? revisionOf = null) =>
        new(id, Ev(available, revisionOf), "security-" + listing, listing, "company-" + listing, symbol, "XNAS",
            AssetClass.Stock, [new ProviderIdentifier("fixture.ticker", symbol)],
            new Denomination("USD", QuoteUnit.Major), "ny-calendar", from, until, status);

    private static Membership Member(string id, string listing, DateTimeOffset available,
        DateTimeOffset from, DateTimeOffset? until, bool eligible, string? revisionOf = null) =>
        new(id, Ev(available, revisionOf), "research-universe", listing, from, until, eligible);

    // AAA splits 2:1 in February and continues; BBB enters in February; CCC exits 29 March,
    // known 8 April.
    private static DatasetFile Scenario(TerminalOutcome outcome = TerminalOutcome.Zero, decimal? cash = null,
        ExitKind exit = ExitKind.Delisted, bool cccSplitBeforeExit = false, bool bbbBarsAfterFebruary = true,
        decimal aaaFebruaryAdjusted = 55m, string? successor = null, decimal? exchangeRatio = null,
        bool successorHasPrices = true)
    {
        var observations = new List<Observation>
        {
            ListingRow("listing-aaa", "aaa", "AAA", At(1, 1), At(1, 1), null, ListingStatus.Active),
            Member("member-aaa", "aaa", At(1, 1), At(1, 1), null, true),
            Bar("aaa-adj-jan-original", "aaa", 1, 31, PriceBasis.TotalReturnAdjusted, 100m),
            Bar("aaa-adj-jan-split", "aaa", 1, 31, PriceBasis.TotalReturnAdjusted, 50m, At(2, 29, 21, 5), "aaa-adj-jan-original"),
            Bar("aaa-adj-feb", "aaa", 2, 29, PriceBasis.TotalReturnAdjusted, aaaFebruaryAdjusted),
            Bar("aaa-adj-mar", "aaa", 3, 28, PriceBasis.TotalReturnAdjusted, 55m),
            Bar("aaa-adj-apr", "aaa", 4, 30, PriceBasis.TotalReturnAdjusted, 60.5m),
            new CorporateAction("aaa-split", Ev(At(1, 31)), "aaa", "aaa-split-event", CorporateActionKind.Split,
                At(2, 15), 2m, null, null, null, null, TerminalOutcome.NotApplicable),

            ListingRow("listing-bbb", "bbb", "BBB", At(1, 20), At(2, 1), null, ListingStatus.Active),
            Member("member-bbb", "bbb", At(1, 20), At(2, 1), null, true),
            Bar("bbb-adj-feb", "bbb", 2, 29, PriceBasis.TotalReturnAdjusted, 10m),
        };
        if (bbbBarsAfterFebruary)
        {
            observations.Add(Bar("bbb-adj-mar", "bbb", 3, 28, PriceBasis.TotalReturnAdjusted, 11m));
            observations.Add(Bar("bbb-adj-apr", "bbb", 4, 30, PriceBasis.TotalReturnAdjusted, 11m));
        }

        observations.Add(ListingRow("listing-ccc", "ccc", "CCC", At(1, 1), At(1, 1), null, ListingStatus.Active));
        observations.Add(Member("member-ccc", "ccc", At(1, 1), At(1, 1), null, true));
        foreach (var (id, month, day, close) in new[] { ("jan", 1, 31, 20m), ("feb", 2, 29, 19m), ("mar", 3, 28, 18m) })
        {
            observations.Add(Bar($"ccc-adj-{id}", "ccc", month, day, PriceBasis.TotalReturnAdjusted, close));
            observations.Add(Bar($"ccc-raw-{id}", "ccc", month, day, PriceBasis.Raw, close));
        }
        // Exit effective 29 March, known 8 April: listing/membership intervals close in revisions.
        observations.Add(ListingRow("listing-ccc-closed", "ccc", "CCC", At(4, 8), At(1, 1), At(3, 29, 12), ListingStatus.Active, "listing-ccc"));
        observations.Add(Member("member-ccc-closed", "ccc", At(4, 8), At(1, 1), At(3, 29, 12), true, "member-ccc"));
        observations.Add(Member("member-ccc-exit", "ccc", At(4, 8), At(3, 29, 12), null, false));
        if (exit == ExitKind.Delisted)
        {
            observations.Add(ListingRow("listing-ccc-delisted", "ccc", "CCC", At(4, 8), At(3, 29, 12), null, ListingStatus.Delisted));
            observations.Add(new CorporateAction("ccc-delisting", Ev(At(4, 8)), "ccc", "ccc-delisting-event",
                CorporateActionKind.Delisting, At(3, 29, 12), exchangeRatio, cash, cash is null ? null : "USD", null, successor, outcome));
            if (cccSplitBeforeExit)
                observations.Add(new CorporateAction("ccc-split", Ev(At(4, 1)), "ccc", "ccc-split-event",
                    CorporateActionKind.Split, At(3, 29), 2m, null, null, null, null, TerminalOutcome.NotApplicable));
            if (successor is not null)
            {
                // A successor must be a known listing; its raw close at the exit decision prices
                // the stock leg. No membership: the successor is not in this research universe.
                observations.Add(ListingRow("listing-ddd", successor, "DDD", At(1, 1), At(1, 1), null, ListingStatus.Active));
                if (successorHasPrices)
                    observations.Add(Bar("ddd-raw-apr", successor, 4, 30, PriceBasis.Raw, 45m));
            }
        }
        else
        {
            observations.Add(ListingRow("listing-ccc-continues", "ccc", "CCC", At(4, 8), At(3, 29, 12), null, ListingStatus.Active));
        }

        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "changing-universe-fixture", At(5, 2),
            "features.v1", [new FeatureDefinition("lagged-close", "v1", "Adjusted close", "After session close")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:changing-universe", "Authored fixture; no market data",
                At(5, 2), null, new string('b', 64), "fixture-v1", "Append revisions")],
            [new CalendarReference("ny-calendar", "America/New_York", "v1", new string('c', 64))],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored synthetic eligibility")], true);
        return new(manifest, observations.ToImmutableArray());
    }

    private static ChangingUniversePanel Build(DatasetFile file, IReadOnlyList<DateTimeOffset>? decisions = null) =>
        ChangingUniversePanel.FromDataset(new PointInTimeDataset(file), "research-universe",
            decisions ?? Decisions, TimeSpan.FromDays(45));

    private static UniverseReturn Return(ChangingUniversePanel panel, int period, string listing) =>
        panel.Periods[period].Returns.Single(r => r.ListingId == listing);

    [Fact]
    public void MembershipChangesByDecisionWithoutBackfillingEntrants()
    {
        var panel = Build(Scenario());
        Assert.Equal(["aaa", "ccc"], panel.Months[0].EligibleListingIds.ToArray());
        Assert.Equal(["aaa", "bbb", "ccc"], panel.Months[1].EligibleListingIds.ToArray());
        Assert.Equal(["aaa", "bbb"], panel.Months[3].EligibleListingIds.ToArray());
        Assert.Equal(UniverseReturnStatus.Entered, Return(panel, 0, "bbb").Status);
        Assert.Null(Return(panel, 0, "bbb").Return);
        Assert.Equal(0.1m, Return(panel, 1, "bbb").Return);
    }

    [Fact]
    public void SplitMonthReturnUsesOneAdjustmentVintage()
    {
        var panel = Build(Scenario());
        // January revised to 50 after the 2:1 split; February 55. The as-of-January
        // vintage (100) would fabricate a -45% month.
        Assert.Equal(UniverseReturnStatus.Priced, Return(panel, 0, "aaa").Status);
        Assert.Equal(0.1m, Return(panel, 0, "aaa").Return);
        Assert.Equal(-0.05m, Return(panel, 0, "ccc").Return);
    }

    [Fact]
    public void ZeroProceedsDelistingRealizesMinusOneHundredPercent()
    {
        var panel = Build(Scenario());
        Assert.Equal(UniverseReturnStatus.TerminalZero, Return(panel, 2, "ccc").Status);
        Assert.Equal(-1m, Return(panel, 2, "ccc").Return);
        Assert.False(panel.HasGaps);
        Assert.Equal(1, panel.ReturnStatusCounts[UniverseReturnStatus.TerminalZero]);
    }

    [Fact]
    public void CashDelistingIsPricedFromRawCloses()
    {
        var panel = Build(Scenario(TerminalOutcome.Cash, 9m));
        Assert.Equal(UniverseReturnStatus.TerminalCash, Return(panel, 2, "ccc").Status);
        Assert.Equal(-0.5m, Return(panel, 2, "ccc").Return);
    }

    [Fact]
    public void MissingTerminalProceedsFailTheWholeBuild()
    {
        var error = Assert.Throws<InvalidDatasetException>(() => Build(Scenario(TerminalOutcome.Missing)));
        Assert.Contains(error.Validation.Issues, x => x.Code == "universe-panel-terminal-missing");
    }

    [Fact]
    public void CashDelistingAfterAnInterveningSplitFailsClosed()
    {
        var error = Assert.Throws<InvalidDatasetException>(() =>
            Build(Scenario(TerminalOutcome.Cash, 9m, cccSplitBeforeExit: true)));
        Assert.Contains(error.Validation.Issues, x => x.Code == "universe-panel-terminal-complex");
    }

    [Fact]
    public void StockMergerIsPricedFromTheSuccessorRawClose()
    {
        // Ratio 0.5 successor shares per CCC share; CCC's 28 March raw close is 18 and the
        // successor's 30 April raw close is 45: 0.5 * 45 / 18 - 1 = +25%, not a cash or zero leg.
        var panel = Build(Scenario(TerminalOutcome.Stock, successor: "ddd", exchangeRatio: 0.5m));
        Assert.Equal(UniverseReturnStatus.TerminalStock, Return(panel, 2, "ccc").Status);
        Assert.Equal(0.25m, Return(panel, 2, "ccc").Return);
        Assert.False(panel.HasGaps);
    }

    [Fact]
    public void StockMergerWithoutASuccessorListingIsRefused()
    {
        var error = Assert.Throws<InvalidDatasetException>(() =>
            Build(Scenario(TerminalOutcome.Stock, exchangeRatio: 2m)));
        Assert.Contains(error.Validation.Issues, x => x.Code == "terminal-stock");
    }

    [Fact]
    public void StockMergerWithoutAnExchangeRatioIsRefused()
    {
        var error = Assert.Throws<InvalidDatasetException>(() =>
            Build(Scenario(TerminalOutcome.Stock, successor: "ddd")));
        Assert.Contains(error.Validation.Issues, x => x.Code == "terminal-stock");
    }

    [Fact]
    public void StockMergerWithoutSuccessorPricesFailsClosed()
    {
        var error = Assert.Throws<InvalidDatasetException>(() =>
            Build(Scenario(TerminalOutcome.Stock, successor: "ddd", exchangeRatio: 0.5m, successorHasPrices: false)));
        Assert.Contains(error.Validation.Issues, x => x.Code == "universe-panel-terminal-successor-price");
    }

    [Fact]
    public void StockMergerAfterAnInterveningSplitFailsClosed()
    {
        var error = Assert.Throws<InvalidDatasetException>(() =>
            Build(Scenario(TerminalOutcome.Stock, successor: "ddd", exchangeRatio: 0.5m, cccSplitBeforeExit: true)));
        Assert.Contains(error.Validation.Issues, x => x.Code == "universe-panel-terminal-complex");
    }

    [Fact]
    public void ExitWithoutADelistingActionIsDisclosedNotDropped()
    {
        var panel = Build(Scenario(exit: ExitKind.Unexplained));
        Assert.Equal(UniverseReturnStatus.ExitedUnexplained, Return(panel, 2, "ccc").Status);
        Assert.Null(Return(panel, 2, "ccc").Return);
        Assert.True(panel.HasGaps);
    }

    [Fact]
    public void StaleOrMissingContinuingPriceIsAGap()
    {
        // Last BBB bar is 29 February: fresh enough in March (31 days), stale by 30 April (61 days).
        var panel = Build(Scenario(bbbBarsAfterFebruary: false));
        Assert.Equal(UniverseReturnStatus.MissingPrice, Return(panel, 2, "bbb").Status);
        Assert.Contains("bbb", panel.Months[3].UnpricedListingIds);
        Assert.True(panel.HasGaps);
    }

    [Fact]
    public void DelistingKnownAfterTheExitPeriodStartsStillExplainsTheExit()
    {
        // The 29 March exit is not yet known at the 31 March decision (published 8 April), so
        // CCC is still eligible there and carries its March close; the loss lands in the next
        // period, not at the effective date and not never.
        var decisions = new[] { At(2, 29, 23), At(3, 31, 23), At(4, 30, 23) };
        var panel = Build(Scenario(), decisions);
        Assert.Contains("ccc", panel.Months[1].EligibleListingIds);
        Assert.Equal(UniverseReturnStatus.Priced, Return(panel, 0, "ccc").Status);
        Assert.Equal(-0.0526m, Return(panel, 0, "ccc").Return!.Value, 4); // 18/19 - 1
        Assert.Equal(UniverseReturnStatus.TerminalZero, Return(panel, 1, "ccc").Status);
        Assert.Equal(-1m, Return(panel, 1, "ccc").Return);
        Assert.False(panel.HasGaps);
    }

    [Fact]
    public void HashIsDeterministicAndSensitiveToPrices()
    {
        var first = Build(Scenario());
        Assert.Equal(first.Sha256, Build(Scenario()).Sha256);
        Assert.NotEqual(first.Sha256, Build(Scenario(aaaFebruaryAdjusted: 56m)).Sha256);
        Assert.Equal("changing-universe-fixture", first.DatasetId);
        Assert.True(first.Synthetic);
    }

    [Fact]
    public void InvalidDecisionGridsAreRejected()
    {
        var dataset = new PointInTimeDataset(Scenario());
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(2, 29, 23), At(1, 31, 23)], TimeSpan.FromDays(45)));
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", Decisions, TimeSpan.Zero));
        // Past the snapshot cutoff: the monthly grid passes and the AsOf slice refuses.
        Assert.Throws<InvalidDatasetException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(4, 30, 23), At(5, 31, 23)], TimeSpan.FromDays(45)));
        // The grid must be monthly: hourly, twice-monthly and month-skipping cadences are
        // refused rather than later deposited, measured and annualised as if they were months.
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(1, 31, 23), At(1, 31, 23).AddHours(1), At(1, 31, 23).AddHours(2)], TimeSpan.FromDays(45)));
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(1, 15, 23), At(1, 31, 23)], TimeSpan.FromDays(45)));
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(1, 31, 23), At(3, 31, 23)], TimeSpan.FromDays(45)));
        // The 20/45-day bounds are independently enforced, not merely implied by the month count.
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(1, 31, 23), At(2, 1, 1)], TimeSpan.FromDays(45)));
        Assert.Throws<ResearchException>(() => ChangingUniversePanel.FromDataset(
            dataset, "research-universe", [At(1, 1, 23), At(2, 28, 23)], TimeSpan.FromDays(45)));
    }
}
