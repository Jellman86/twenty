using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

/// <summary>
/// Covers the terminal-action step: an accepted, corroborated cash-only disposition becomes one
/// modelled delisting action, and everything else becomes an explicit omission rather than a
/// terminal outcome nobody established.
/// </summary>
public sealed class TerminalActionsTests
{
    private static readonly DateTimeOffset Retrieved = DateTimeOffset.Parse("2026-10-20T17:37:29Z");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
    private static readonly DateOnly FinalSession = new(2024, 3, 15); // a reviewed Friday session
    private static readonly DateOnly EventDate = new(2024, 3, 15);
    private static readonly DateOnly Suspension = new(2024, 3, 20);

    private static Evidence Ev(DateTimeOffset at) => new("fixture-source", at, at,
        AvailabilityEvidence.ArchivedPublication, TimePrecision.Second, null, null);

    private static DatasetFile Dataset(string venue = "XNAS") =>
        new(new DatasetManifest(DatasetValidator.SchemaVersion, "identity-fixture", Created, "feature-v1",
            [new FeatureDefinition("identity-only", "v1", "Authored fixture; no market data", "Available at capture time")],
            [new SourceSnapshot("fixture-source", "urn:twenty:fixture:terminal", "Authored fixture; no market data",
                Retrieved, Retrieved, new string('f', 64), "fixture-v1", "Immutable")],
            [NasdaqSessions.Reference("ny-calendar")],
            [new UniverseDefinition("research-universe", UniverseKind.Research, "Authored fixture")], true),
        [
            new Listing("listing-row", Ev(Retrieved), "security-aaa", "listing-aaa", "company-aaa", "AAA", venue,
                AssetClass.Stock, [new ProviderIdentifier("tiingo.ticker", "AAA")],
                new Denomination("USD", QuoteUnit.Major), "ny-calendar",
                new DateTimeOffset(2015, 1, 2, 0, 0, 0, TimeSpan.Zero), null, ListingStatus.Active),
            new Membership("membership-row", Ev(Retrieved), "research-universe", "listing-aaa",
                new DateTimeOffset(2015, 1, 2, 0, 0, 0, TimeSpan.Zero), null, true)
        ]);

    private static byte[] Reference(string symbol = "AAA", string kind = "cash-only", DateOnly? lastTrading = null,
        DateOnly? eventDate = null, decimal? cash = 102.5m, string? currency = "USD", string? successor = null,
        decimal? shares = null, decimal? rights = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new TerminalReferences("terminal-reference.v1",
            [new TerminalSource("src-aaa", "https://www.nasdaqtrader.com/fixture", Retrieved, new string('a', 64), "original-response"),
             new TerminalSource("src-aaa-2", "https://www.sec.gov/fixture", Retrieved, new string('b', 64), "original-response")],
            [new TerminalSubject(symbol, "completed-merger", eventDate ?? EventDate, lastTrading ?? FinalSession, Suspension,
                new TerminalConsideration(kind, cash, currency, successor, shares, rights), ["src-aaa", "src-aaa-2"])]),
            DatasetJson.Options);

    private static TerminalDispositionReport Report(byte[] reference, string symbol = "AAA", string outcome = "accepted",
        DateOnly? final = null, string? referenceSha = null) =>
        new(TerminalDispositions.Version, new string('d', 64), TerminalHistoryAudit.Version,
            referenceSha ?? CaptureStore.Hash(reference),
            [new TerminalDisposition(symbol, outcome, outcome == "accepted" ? final ?? FinalSession : null, [], 0, [], ["src-aaa", "src-aaa-2"])],
            outcome == "accepted" ? 1 : 0, outcome == "accepted" ? 0 : 1, ["Authored fixture; no market data"]);

    [Fact]
    public void AnAcceptedCashMergerBecomesOneModelledDelisting()
    {
        var reference = Reference();
        var result = TerminalActions.Convert(Dataset(), reference, Report(reference), "terminal-fixture", Created);

        var action = Assert.Single(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(1, result.ActionsAdded);
        Assert.Empty(result.Omitted);
        Assert.Equal(CorporateActionKind.Delisting, action.Action);
        Assert.Equal(TerminalOutcome.Cash, action.TerminalOutcome);
        Assert.Equal("listing-aaa", action.ListingId);
        Assert.Equal("listing-aaa-delisting", action.ActionId);
        Assert.Equal("listing-aaa-delisting-20240315", action.Id);
        Assert.Equal(102.5m, action.CashPerShare);
        Assert.Equal("USD", action.CashCurrency);
        // The last trading session closes at 16:00 New York, which is 20:00Z in March.
        Assert.Equal(DateTimeOffset.Parse("2024-03-15T20:00:00Z"), action.EffectiveAt);
        // Availability is the retrieval of the cited documents, never the event date.
        Assert.Equal(Retrieved, action.Evidence.AvailableAt);
        Assert.Equal(Retrieved, action.Evidence.PublishedAt);
        Assert.Equal(AvailabilityEvidence.ConservativePolicy, action.Evidence.AvailabilityEvidence);
        Assert.Equal(TerminalActions.AvailabilityPolicy, action.Evidence.PolicyReference);
        var source = Assert.Single(result.Dataset.Manifest.Sources.Where(s => s.Id == TerminalActions.SourceId));
        Assert.Equal(Retrieved, source.RetrievedAt);
        Assert.Equal(TerminalActions.Version, source.ParserVersion);
        Assert.True(DatasetValidator.Validate(result.Dataset).IsValid);
    }

    [Fact]
    public void ARefusedSubjectYieldsNoAssertionAtAll()
    {
        var reference = Reference();
        var result = TerminalActions.Convert(Dataset(), reference, Report(reference, outcome: "refused"), "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(0, result.ActionsAdded);
        Assert.Equal(new TerminalActionOmission("AAA", "disposition-refused"), Assert.Single(result.Omitted));
        Assert.True(DatasetValidator.Validate(result.Dataset).IsValid);
    }

    [Theory]
    [InlineData("cash-and-stock", "consideration-not-supported")]
    [InlineData("cash-and-cvr", "consideration-not-supported")]
    public void UnsupportedConsiderationIsOmittedNotApproximated(string kind, string reason)
    {
        var reference = Reference(kind: kind, cash: 380m,
            successor: kind == "cash-and-stock" ? "BBB" : null, shares: kind == "cash-and-stock" ? 0.3619m : null,
            rights: kind == "cash-and-cvr" ? 1m : null);
        var result = TerminalActions.Convert(Dataset(), reference, Report(reference), "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(new TerminalActionOmission("AAA", reason), Assert.Single(result.Omitted));
    }

    [Fact]
    public void AFinalSessionDisagreeingWithTheReviewedClaimIsOmitted()
    {
        var reference = Reference();
        // The disposition asserts a different final session from the reviewed last trading date.
        var report = Report(reference, final: new DateOnly(2024, 3, 14));
        var result = TerminalActions.Convert(Dataset(), reference, report, "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(new TerminalActionOmission("AAA", "event-not-terminal"), Assert.Single(result.Omitted));
    }

    [Fact]
    public void AFinalSessionThatIsNotAReviewedTradingSessionIsOmitted()
    {
        // 2024-03-16 is a Saturday: a reviewed "last trading date" that is not a session cannot be timestamped.
        var saturday = new DateOnly(2024, 3, 16);
        var reference = Reference(lastTrading: saturday, eventDate: saturday);
        var report = Report(reference, final: saturday);
        var result = TerminalActions.Convert(Dataset(), reference, report, "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(new TerminalActionOmission("AAA", "final-session-not-in-calendar"), Assert.Single(result.Omitted));
    }

    [Fact]
    public void ASymbolWithNoListingInThisDatasetIsOmitted()
    {
        var reference = Reference(symbol: "ZZZ");
        var report = Report(reference, symbol: "ZZZ");
        var result = TerminalActions.Convert(Dataset(), reference, report, "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(new TerminalActionOmission("ZZZ", "listing-missing"), Assert.Single(result.Omitted));
    }

    [Fact]
    public void AListingOutsideTheReviewedCalendarIsOmitted()
    {
        var reference = Reference();
        var result = TerminalActions.Convert(Dataset(venue: "XNYS"), reference, Report(reference), "terminal-fixture", Created);

        Assert.Empty(result.Dataset.Observations.OfType<CorporateAction>());
        Assert.Equal(new TerminalActionOmission("AAA", "calendar-unsupported"), Assert.Single(result.Omitted));
    }

    [Fact]
    public void DispositionsReachedFromAnotherReferenceBundleAreRefused()
    {
        var reference = Reference();
        var report = Report(reference, referenceSha: new string('e', 64));
        var error = Assert.Throws<ResearchException>(() =>
            TerminalActions.Convert(Dataset(), reference, report, "terminal-fixture", Created));
        Assert.Equal("terminal-actions-reference-conflict", error.Code);
    }

    [Fact]
    public void ADatasetThatAlreadySettlesADelistingIsRefused()
    {
        var reference = Reference();
        var input = Dataset();
        var settled = input with
        {
            Observations = input.Observations.Add(new CorporateAction("existing-delisting", Ev(Retrieved), "listing-aaa",
                "listing-aaa-delisting", CorporateActionKind.Delisting, Retrieved, null, 1m, "USD", null, null,
                TerminalOutcome.Cash))
        };
        var error = Assert.Throws<ResearchException>(() =>
            TerminalActions.Convert(settled, reference, Report(reference), "terminal-fixture", Created));
        Assert.Equal("terminal-actions-duplicate", error.Code);
    }

    [Fact]
    public void EvidenceRetrievedAfterTheDatasetCutoffIsRefused()
    {
        var reference = Reference();
        var error = Assert.Throws<ResearchException>(() =>
            TerminalActions.Convert(Dataset(), reference, Report(reference), "terminal-fixture", Retrieved.AddDays(-1)));
        Assert.Equal("terminal-actions-created-at", error.Code);
    }

    [Fact]
    public void TheStepIsDeterministicOverIdenticalInputs()
    {
        var reference = Reference();
        var first = TerminalActions.Convert(Dataset(), reference, Report(reference), "terminal-fixture", Created);
        var second = TerminalActions.Convert(Dataset(), reference, Report(reference), "terminal-fixture", Created);

        Assert.Equal(Convert.ToHexStringLower(JsonSerializer.SerializeToUtf8Bytes(first.Dataset, DatasetJson.Options)),
            Convert.ToHexStringLower(JsonSerializer.SerializeToUtf8Bytes(second.Dataset, DatasetJson.Options)));
    }
}
