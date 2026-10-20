using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class UniverseBacktestTests
{
    internal const int Months = 30; // decisions Jan 2022 .. Jun 2024
    internal static DateTimeOffset Decision(int i)
    {
        var first = new DateTime(2022, 1, 1).AddMonths(i);
        var last = first.AddMonths(1).AddDays(-1);
        return new DateTimeOffset(last.Year, last.Month, last.Day, 23, 0, 0, TimeSpan.Zero);
    }
    private static Evidence Ev(DateTimeOffset available, string? revisionOf = null) =>
        new("fixture-source", available, available, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, revisionOf);

    private static PriceBar Bar(string listing, int i, decimal close, PriceBasis basis = PriceBasis.TotalReturnAdjusted) =>
        new($"{listing}-{(basis == PriceBasis.Raw ? "raw-" : "")}bar-{i}", Ev(Decision(i).AddHours(-2).AddMinutes(2)), listing,
            DateOnly.FromDateTime(Decision(i).UtcDateTime), Decision(i).AddHours(-2),
            new Denomination("USD", QuoteUnit.Major), basis, close, close, close, close, 100m);

    private static Listing ListingRow(string id, string listing, string symbol, DateTimeOffset available,
        DateTimeOffset from, DateTimeOffset? until, ListingStatus status, string? revisionOf = null) =>
        new(id, Ev(available, revisionOf), "security-" + listing, listing, "company-" + listing, symbol, "XNAS",
            AssetClass.Stock, [new ProviderIdentifier("fixture.ticker", symbol)],
            new Denomination("USD", QuoteUnit.Major), "ny-calendar", from, until, status);

    private static Membership Member(string id, string listing, DateTimeOffset available,
        DateTimeOffset from, DateTimeOffset? until, bool eligible, string? revisionOf = null) =>
        new(id, Ev(available, revisionOf), "research-universe", listing, from, until, eligible);

    // aaa +1%/mo, bbb flat, ccc -2%/mo then zero-proceeds delisting in period 23,
    // ddd enters at decision 15 with +5%/mo.
    internal static DatasetFile Scenario(bool gapBbb = false, TerminalOutcome cccOutcome = TerminalOutcome.Zero, decimal? cccCash = null,
        string? cccSuccessor = null, decimal? cccRatio = null)
    {
        var start = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var observations = new List<Observation>
        {
            ListingRow("listing-aaa", "aaa", "AAA", start, start, null, ListingStatus.Active),
            Member("member-aaa", "aaa", start, start, null, true),
            ListingRow("listing-bbb", "bbb", "BBB", start, start, null, ListingStatus.Active),
            Member("member-bbb", "bbb", start, start, null, true),
            ListingRow("listing-ccc", "ccc", "CCC", start, start, null, ListingStatus.Active),
            Member("member-ccc", "ccc", start, start, null, true),
        };
        var aaa = 100m; var ccc = 50m;
        for (var i = 0; i < Months; i++)
        {
            observations.Add(Bar("aaa", i, aaa)); aaa *= 1.01m;
            if (!gapBbb || i is not (20 or 21)) observations.Add(Bar("bbb", i, 10m));
            if (i <= 23) { observations.Add(Bar("ccc", i, ccc)); observations.Add(Bar("ccc", i, ccc, PriceBasis.Raw)); ccc *= 0.98m; }
        }
        var exitEffective = new DateTimeOffset(2024, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var exitKnown = new DateTimeOffset(2024, 1, 8, 0, 0, 0, TimeSpan.Zero);
        observations.Add(ListingRow("listing-ccc-closed", "ccc", "CCC", exitKnown, start, exitEffective, ListingStatus.Active, "listing-ccc"));
        observations.Add(ListingRow("listing-ccc-delisted", "ccc", "CCC", exitKnown, exitEffective, null, ListingStatus.Delisted));
        observations.Add(Member("member-ccc-closed", "ccc", exitKnown, start, exitEffective, true, "member-ccc"));
        observations.Add(Member("member-ccc-exit", "ccc", exitKnown, exitEffective, null, false));
        observations.Add(new CorporateAction("ccc-delisting", Ev(exitKnown), "ccc", "ccc-delisting-event",
            CorporateActionKind.Delisting, exitEffective, cccRatio, cccCash, cccCash is null ? null : "USD", null, cccSuccessor, cccOutcome));

        var dddFrom = new DateTimeOffset(2023, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var dddKnown = new DateTimeOffset(2023, 3, 20, 0, 0, 0, TimeSpan.Zero);
        observations.Add(ListingRow("listing-ddd", "ddd", "DDD", dddKnown, dddFrom, null, ListingStatus.Active));
        observations.Add(Member("member-ddd", "ddd", dddKnown, dddFrom, null, true));
        var ddd = 20m;
        for (var i = 15; i < Months; i++) { observations.Add(Bar("ddd", i, ddd)); observations.Add(Bar("ddd", i, ddd, PriceBasis.Raw)); ddd *= 1.05m; }

        var created = new DateTimeOffset(2024, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "universe-backtest-fixture", created,
            "features.v1", [new FeatureDefinition("lagged-close", "v1", "Adjusted close", "After session close")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:universe-backtest", "Authored fixture; no market data",
                created, null, new string('b', 64), "fixture-v1", "Append revisions")],
            [new CalendarReference("ny-calendar", "America/New_York", "v1", new string('c', 64))],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored synthetic eligibility")], true);
        return new(manifest, observations.ToImmutableArray());
    }

    private static ChangingUniversePanel Panel(DatasetFile file, int months = Months) =>
        ChangingUniversePanel.FromDataset(new PointInTimeDataset(file), "research-universe",
            Enumerable.Range(0, months).Select(Decision).ToArray(), TimeSpan.FromDays(45));

    private static readonly ResearchCosts ZeroCosts = new(0m, 0m, 0m, 0m, 0m);
    private static EquityObservation Step(UniverseBacktestReport report, string strategy, int evaluatedIndex) =>
        report.Equity.Where(e => e.Strategy == strategy).ElementAt(evaluatedIndex);

    [Fact]
    public void EqualWeightFirstEvaluatedPeriodMatchesHandCalculation()
    {
        var report = UniverseBacktest.Run(Panel(Scenario()), new(), ZeroCosts);
        var first = Step(report, "equal_weight", 0).State;
        // GBP 20 split across aaa/bbb/ccc earning +1%/0%/-2%: 20 * (3.01-0.02... ) = 20 * 2.99/3.
        Assert.Equal(19.9333m, first.EquityGbp, 3);
        Assert.Equal(20m, first.ContributionsGbp);
        Assert.Equal(0m, first.PeriodCostGbp);
    }

    [Fact]
    public void DelistingLossHitsEveryHolderInTheTerminalPeriod()
    {
        var report = UniverseBacktest.Run(Panel(Scenario()), new(), ZeroCosts);
        // Period 23 is the 12th evaluated period (first evaluated is period 12).
        var terminal = Step(report, "equal_weight", 23 - 12).State;
        Assert.Equal(-0.235m, terminal.PeriodReturn, 4); // (0.01 + 0 + 0.05 - 1) / 4
    }

    [Fact]
    public void CashTerminalDelistingMidRunDoesNotCrashTheBacktest()
    {
        // ccc exits with cash proceeds mid-evaluation (period 27). Before the fix, the
        // realized proceeds stayed in holdings under ccc's id and the next step threw
        // invalid-portfolio-step because ccc had left the universe. Now they move to cash.
        var report = UniverseBacktest.Run(Panel(Scenario(cccOutcome: TerminalOutcome.Cash, cccCash: 5m)), new(), ZeroCosts);
        Assert.NotNull(report);
        // The run reaches the final evaluated period without throwing.
        var finalStep = Step(report, "equal_weight", report.Equity.Where(e => e.Strategy == "equal_weight").Count() - 1).State;
        Assert.True(finalStep.EquityGbp > 0m);
    }

    [Fact]
    public void StockMergerBacktestRefusesToLiquidateSuccessorSharesForFree()
    {
        var panel = Panel(Scenario(cccOutcome: TerminalOutcome.Stock, cccSuccessor: "ddd", cccRatio: 0.5m));
        Assert.Equal(UniverseReturnStatus.TerminalStock,
            panel.Periods[23].Returns.Single(r => r.ListingId == "ccc").Status);
        var error = Assert.Throws<ResearchException>(() => UniverseBacktest.Run(panel, new(), ZeroCosts));
        Assert.Equal("universe-backtest-stock-settlement", error.Code);
    }

    [Fact]
    public void MomentumWaitsForACompleteTrailingWindowBeforeHoldingAnEntrant()
    {
        var report = UniverseBacktest.Run(Panel(Scenario()), new(TopK: 1), ZeroCosts);
        var buysAt = (int period) => Step(report, "momentum", period - 12).State.Trades
            .Where(t => t.Side == "buy").Select(t => t.Symbol).ToArray();
        Assert.Equal(["aaa"], buysAt(12));
        Assert.DoesNotContain("ddd", buysAt(26));
        Assert.Equal(["ddd"], buysAt(27));
    }

    [Fact]
    public void EntrantWhoseFirstPriceWasNotObservableAtTheDecisionIsNotBoughtThere()
    {
        // ddd-bar-15 only becomes visible after the 15th decision (late publication): the panel
        // computes an ex-post return for it, yet nobody could have priced or bought it then.
        var source = Scenario();
        var late = source with
        {
            Observations = source.Observations.Select(o => o is PriceBar b && b.Id == "ddd-bar-15"
                ? b with { Evidence = b.Evidence with { PublishedAt = Decision(16).AddMinutes(-10), AvailableAt = Decision(16).AddMinutes(-10) } }
                : o).ToImmutableArray()
        };
        var panel = Panel(late);
        Assert.Contains("ddd", panel.Months[15].UnpricedListingIds);
        Assert.False(panel.HasGaps);
        var report = UniverseBacktest.Run(panel, new(), ZeroCosts);
        var at15 = Step(report, "equal_weight", 15 - 12).State;
        Assert.DoesNotContain(at15.Trades, t => t.Symbol == "ddd");
        // Once a decision-time price exists (decision 16), the entrant becomes buyable.
        var at16 = Step(report, "equal_weight", 16 - 12).State;
        Assert.Contains(at16.Trades, t => t.Symbol == "ddd" && t.Side == "buy");
    }

    [Fact]
    public void CashEarnsNothingAndReportBindsThePanel()
    {
        var panel = Panel(Scenario());
        var report = UniverseBacktest.Run(panel, new(), ZeroCosts);
        var cash = report.Summaries.Single(s => s.Strategy == "cash");
        Assert.Equal(cash.ContributionsGbp, cash.EndingEquityGbp);
        Assert.Equal(0m, cash.ProfitGbp);
        Assert.Equal(panel.Sha256, report.PanelSha256);
        Assert.True(report.Synthetic);
        Assert.Equal(panel.Periods[12].From, report.EvaluationStart);
        Assert.Equal(report.Summaries.ToArray(),
            UniverseBacktest.Run(panel, new(), ZeroCosts).Summaries.ToArray());
    }

    [Fact]
    public void GapPanelsAndShortPanelsAreRefused()
    {
        var gap = Assert.Throws<ResearchException>(() =>
            UniverseBacktest.Run(Panel(Scenario(gapBbb: true)), new(), ZeroCosts));
        Assert.Equal("universe-backtest-gaps", gap.Code);
        var shortPanel = Assert.Throws<ResearchException>(() =>
            UniverseBacktest.Run(Panel(Scenario(), months: 10), new(), ZeroCosts));
        Assert.Equal("universe-backtest-history", shortPanel.Code);
    }

    [Fact]
    public void CostsReduceEqualWeightEquity()
    {
        var panel = Panel(Scenario());
        var free = UniverseBacktest.Run(panel, new(), ZeroCosts).Summaries.Single(s => s.Strategy == "equal_weight");
        var costly = UniverseBacktest.Run(panel, new(), new ResearchCosts()).Summaries.Single(s => s.Strategy == "equal_weight");
        Assert.True(costly.EndingEquityGbp < free.EndingEquityGbp);
        Assert.True(costly.TradingCostGbp > 0);
    }
}
