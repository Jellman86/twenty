using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawGbpLedgerTests
{
    private static readonly DateTimeOffset BuyAt = new(2024, 1, 2, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Age = TimeSpan.FromDays(2);
    private static Evidence Known(DateTimeOffset at) => new("fixture-source", at, at, AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);
    private static DataSlice Market(DateTimeOffset at, Denomination? denomination = null, decimal price = 100m,
        decimal fx = .8m, FxPurpose purpose = FxPurpose.Executable, bool adjustedOnly = false,
        bool reverseFx = false, int fxAgeDays = 0, int priceAgeDays = 0, bool futureFx = false, bool split = false)
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var units = denomination ?? new("USD", QuoteUnit.Major);
        var start = BuyAt.AddDays(-1);
        var listing = fixture.Observations.OfType<Listing>().First() with { Evidence = Known(start), EffectiveFrom = start,
            EffectiveUntil = null, Denomination = units, Status = ListingStatus.Active };
        var close = new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero).AddHours(16).AddMinutes(30).AddDays(-priceAgeDays);
        var observed = at.AddDays(-fxAgeDays).AddMinutes(futureFx ? 1 : -1);
        var rows = ImmutableArray.CreateBuilder<Observation>(); rows.Add(listing);
        rows.Add(new PriceBar("raw", Known(close.AddMinutes(2)), listing.ListingId, DateOnly.FromDateTime(close.UtcDateTime), close,
            units, adjustedOnly ? PriceBasis.TotalReturnAdjusted : PriceBasis.Raw, price, price, price, price, 100));
        if (units.Currency != "GBP") rows.Add(new FxRate("fx", Known(observed), reverseFx ? "GBP" : units.Currency,
            reverseFx ? units.Currency : "GBP", fx, observed, purpose));
        if (split) rows.Add(new CorporateAction("split", Known(at.AddMinutes(-1)), listing.ListingId, "split-event", CorporateActionKind.Split,
            at.AddHours(-1), 2, null, null, null, null, TerminalOutcome.NotApplicable));
        return new PointInTimeDataset(fixture with { Observations = rows.ToImmutable() }).AsOf(at);
    }

    [Fact]
    public void ForeignRawQuantitiesAndFxRevaluationMatchHandCalculation()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 1, Age);
        Assert.Equal(39, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
        var next = Market(BuyAt.AddDays(1), price: 110, fx: .75m);
        var mark = ledger.Value(next, Age, Age, FxPurpose.Executable);
        Assert.Equal(204, mark.EquityGbp); Assert.Equal(165, Assert.Single(mark.Positions).ValueGbp);
        ledger.Fill("sell", next, "listing-a", -.5m, 110, PriceBasis.Raw, .5m, Age);
        Assert.Equal(79.75m, ledger.CashGbp); Assert.Equal(1.5m, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(203.5m, ledger.Value(next, Age, Age, FxPurpose.Executable).EquityGbp);
        Assert.Equal(1.5m, ledger.FeesGbp); Assert.Equal(200, ledger.ContributionsGbp);
        Assert.Equal("fx", ledger.Entries[1].FxId); Assert.Equal(.8m, ledger.Entries[1].GbpPerLocalUnit);
    }

    [Fact]
    public void FullSaleRemovesThePositionAndValuationDoesNotMutateTheBook()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 100);
        var market = Market(BuyAt);
        ledger.Fill("buy", market, "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var entries = ledger.Entries;
        _ = ledger.Value(Market(BuyAt.AddDays(1), price: 110), Age, Age, FxPurpose.Executable);
        Assert.Equal(entries, ledger.Entries); Assert.Equal(20, ledger.CashGbp);
        ledger.Fill("sell", market, "listing-a", -1, 100, PriceBasis.Raw, 0, Age);
        Assert.Empty(ledger.Holdings); Assert.Equal(100, ledger.CashGbp);
        Assert.Equal(100, ledger.Value(Market(BuyAt.AddDays(1), adjustedOnly: true), Age, Age, FxPurpose.Reference).EquityGbp);
    }

    [Fact]
    public void PenceScalingAppliesToRawPriceButNotShareQuantity()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 25);
        var gbp = new Denomination("GBP", QuoteUnit.Pence);
        ledger.Fill("buy", Market(BuyAt, gbp, 1050), "listing-a", 2, 1050, PriceBasis.Raw, .2m, Age);
        Assert.Equal(3.8m, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
        Assert.Equal(25.8m, ledger.Value(Market(BuyAt.AddDays(1), gbp, 1100), Age, Age, FxPurpose.Reference).EquityGbp);
        Assert.Null(ledger.Entries[1].FxId); Assert.Equal(1, ledger.Entries[1].GbpPerLocalUnit);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("reverse")]
    [InlineData("stale")]
    [InlineData("future")]
    public void FillRequiresAvailableFreshExecutableFxInTheDeclaredDirection(string problem)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        var market = Market(BuyAt, purpose: problem == "reference" ? FxPurpose.Reference : FxPurpose.Executable,
            reverseFx: problem == "reverse", fxAgeDays: problem == "stale" ? 3 : 0, futureFx: problem == "future");
        Assert.Throws<InvalidDatasetException>(() => ledger.Fill("buy", market, "listing-a", 1, 100, PriceBasis.Raw, 0, Age));
        Assert.Equal(200, ledger.CashGbp); Assert.Empty(ledger.Holdings); Assert.Single(ledger.Entries);
    }

    [Fact]
    public void ReferenceValuationIsLabelledAndCannotSupplyAnExecutableFill()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var next = Market(BuyAt.AddDays(1), purpose: FxPurpose.Reference, fx: .7m);
        var mark = ledger.Value(next, Age, Age, FxPurpose.Reference);
        Assert.Equal(190, mark.EquityGbp); Assert.Equal(FxPurpose.Reference, mark.FxPurpose);
        Assert.Throws<InvalidDatasetException>(() => ledger.Value(next, Age, Age, FxPurpose.Executable));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public void MissingRawOrStalePricesCannotUseAdjustedFallback(bool adjusted, int age)
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-price", Assert.Throws<ResearchException>(() => ledger.Value(
            Market(BuyAt.AddDays(4), adjustedOnly: adjusted, priceAgeDays: age), Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void RefusedFillDoesNotConsumeIdentityOrPartiallyMutateTheBook()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 100);
        Assert.Equal("raw-ledger-cash", Assert.Throws<ResearchException>(() => ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age)).Code);
        ledger.Deposit("more", BuyAt, 100);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 2, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal(40, ledger.CashGbp); Assert.Equal(3, ledger.Entries.Length);
        Assert.Equal("raw-ledger-event", Assert.Throws<ResearchException>(() => ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-quantity", Assert.Throws<ResearchException>(() => ledger.Fill("sell", Market(BuyAt), "listing-a", -3, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal("raw-ledger-basis", Assert.Throws<ResearchException>(() => ledger.Fill("adjusted", Market(BuyAt), "listing-a", 1, 100, PriceBasis.TotalReturnAdjusted, 0, Age)).Code);
        Assert.Equal(40, ledger.CashGbp); Assert.Equal(2, ledger.Holdings["listing-a"].Quantity);
    }

    [Fact]
    public void TimeAndChangedDenominationAreRefusedWithoutReinterpretingPositions()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        Assert.Equal("raw-ledger-time", Assert.Throws<ResearchException>(() => ledger.Deposit("past", BuyAt.AddMinutes(-1), 1)).Code);
        Assert.Equal("raw-ledger-units", Assert.Throws<ResearchException>(() => ledger.Value(Market(BuyAt.AddDays(1), new("GBP", QuoteUnit.Major)), Age, Age, FxPurpose.Executable)).Code);
    }

    [Fact]
    public void UnhandledCorporateActionsBlockRatherThanLeavingOldShareCounts()
    {
        var ledger = new RawGbpLedger(); ledger.Deposit("deposit", BuyAt, 200);
        ledger.Fill("buy", Market(BuyAt), "listing-a", 1, 100, PriceBasis.Raw, 0, Age);
        var next = Market(BuyAt.AddDays(1), split: true);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Value(next, Age, Age, FxPurpose.Executable)).Code);
        Assert.Equal("raw-ledger-action-unsupported", Assert.Throws<ResearchException>(() => ledger.Fill("after-split", next, "listing-a", 1, 100, PriceBasis.Raw, 0, Age)).Code);
        Assert.Equal(120, ledger.CashGbp); Assert.Equal(1, ledger.Holdings["listing-a"].Quantity);
    }
}
