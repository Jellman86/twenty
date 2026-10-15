using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Data.Upstream;

namespace Twenty.Tests;

public class DataContractTests
{
    private static DatasetFile Fixture() => DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static DatasetFile Change(DatasetFile file, string id, Func<Observation, Observation> change) =>
        file with { Observations = file.Observations.Select(x => x.Id == id ? change(x) : x).ToImmutableArray() };
    private static DataSlice View(DatasetFile file, string time) => new PointInTimeDataset(file).AsOf(At(time));
    private static void HasIssue(DatasetFile file, string code) => Assert.Contains(DatasetValidator.Validate(file).Issues, x => x.Code == code);

    [Fact]
    public void SyntheticFixtureHasAValidVersionedManifestAndRoundTrips()
    {
        var file = Fixture();
        Assert.True(file.Manifest.Synthetic);
        Assert.True(DatasetValidator.Validate(file).IsValid);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(file, DatasetJson.Options);
        var copy = DatasetJson.Read(encoded);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(file, DatasetJson.Options),
            JsonSerializer.SerializeToElement(copy, DatasetJson.Options)));
        Assert.Equal(file.Manifest.FeatureSchemaVersion, copy.Manifest.FeatureSchemaVersion);
    }

    [Fact]
    public void HistoricalUniverseDoesNotUseFutureAvailabilityOrFutureEffectiveMembership()
    {
        var file = Fixture();
        Assert.Equal(["listing-a"], View(file, "2024-01-10T12:00:00Z").Universe("synthetic-isa").Select(x => x.ListingId));
        Assert.Equal(["listing-a"], View(file, "2024-01-25T12:00:00Z").Universe("synthetic-isa").Select(x => x.ListingId));
        Assert.Equal(["listing-a", "listing-b"], View(file, "2024-02-02T12:00:00Z").Universe("synthetic-isa").Select(x => x.ListingId));
        var changed = Change(file, "member-b", x => ((Membership)x) with { Eligible = false });
        Assert.Equal<Listing>(View(file, "2024-01-10T12:00:00Z").Universe("synthetic-isa"), View(changed, "2024-01-10T12:00:00Z").Universe("synthetic-isa"));
    }

    [Fact]
    public void TickerChangeUsesTheKnownEffectiveListingAndHalfOpenBoundary()
    {
        var file = Fixture();
        Assert.Equal("OLD", View(file, "2024-03-20T12:00:00Z").ListingsAt(At("2024-03-20T12:00:00Z")).Single(x => x.ListingId == "listing-a").Symbol);
        var april = View(file, "2024-04-01T00:00:00Z");
        Assert.Equal("NEW", april.ListingsAt(april.DecisionAt).Single(x => x.ListingId == "listing-a").Symbol);
        Assert.Equal("OLD", april.ListingsAt(At("2024-01-02T16:30:00Z")).Single().Symbol);
    }

    [Fact]
    public void RestatementCannotReplaceAnEarlierAsReportedFact()
    {
        var file = Fixture();
        Assert.Empty(View(file, "2024-02-01T00:00:00Z").Fundamentals("company-a"));
        Assert.Equal(100m, View(file, "2024-03-01T00:00:00Z").Fundamentals("company-a").Single().Value);
        Assert.Equal(60m, View(file, "2024-05-16T00:00:00Z").Fundamentals("company-a").Single().Value);
        var changed = Change(file, "profit-restated", x => ((FundamentalFact)x) with { Value = 9999 });
        Assert.Equal<FundamentalFact>(View(file, "2024-03-01T00:00:00Z").Fundamentals("company-a"), View(changed, "2024-03-01T00:00:00Z").Fundamentals("company-a"));
    }

    [Fact]
    public void LaterAdjustmentDoesNotRewriteEarlierPricesAndCannotBecomeARawQuote()
    {
        var file = Fixture();
        var before = View(file, "2024-01-10T12:00:00Z");
        Assert.Equal(1050m, before.Prices("listing-a", PriceBasis.TotalReturnAdjusted).Single().Close);
        var after = View(file, "2024-02-03T12:00:00Z");
        Assert.Equal(525m, after.Prices("listing-a", PriceBasis.TotalReturnAdjusted).Single().Close);
        Assert.Equal(1050m, after.Prices("listing-a", PriceBasis.Raw).Single().Close);
        var changed = Change(file, "adjusted-jan2-split", x => ((PriceBar)x) with { Open = 700, High = 800, Low = 650, Close = 750 });
        Assert.Equal<PriceBar>(before.Prices("listing-a", PriceBasis.TotalReturnAdjusted), View(changed, "2024-01-10T12:00:00Z").Prices("listing-a", PriceBasis.TotalReturnAdjusted));
    }

    [Fact]
    public void ACompletedBarIsUnavailableBeforeItsRelease()
    {
        var file = Fixture();
        Assert.Empty(View(file, "2024-01-02T16:31:59Z").Prices("listing-a", PriceBasis.Raw));
        Assert.Single(View(file, "2024-01-02T16:32:00Z").Prices("listing-a", PriceBasis.Raw));
    }

    [Fact]
    public void AnnouncedActionsAreNotAppliedBeforeTheirEffectiveTime()
    {
        var file = Fixture();
        Assert.Empty(View(file, "2024-01-31T12:00:00Z").EffectiveActions("listing-a"));
        Assert.Equal(2m, View(file, "2024-02-02T08:00:00Z").EffectiveActions("listing-a").Single().SplitRatio);
    }

    [Fact]
    public void DelistedMembersDoNotDisappearRetrospectivelyAndMissingTerminalValueFailsClosed()
    {
        var file = Fixture();
        Assert.Contains(View(file, "2024-06-02T12:00:00Z").Universe("synthetic-isa"), x => x.ListingId == "listing-a");
        var after = View(file, "2024-06-05T12:00:00Z");
        Assert.DoesNotContain(after.Universe("synthetic-isa"), x => x.ListingId == "listing-a");
        Assert.Throws<InvalidDatasetException>(() => after.TerminalDistribution("delisting-event"));
        var zero = Change(file, "delisting", x => ((CorporateAction)x) with { TerminalOutcome = TerminalOutcome.Zero });
        Assert.Equal((0m, "GBP"), View(zero, "2024-06-05T12:00:00Z").TerminalDistribution("delisting-event"));
        var cash = Change(file, "delisting", x => ((CorporateAction)x) with { TerminalOutcome = TerminalOutcome.Cash, CashPerShare = 1.25m, CashCurrency = "GBP" });
        Assert.Equal((1.25m, "GBP"), View(cash, "2024-06-05T12:00:00Z").TerminalDistribution("delisting-event"));
    }

    [Fact]
    public void PenceAndFxDirectionPurposeAndFreshnessAreExplicit()
    {
        var view = View(Fixture(), "2024-01-02T16:32:00Z");
        var quote = view.Prices("listing-a", PriceBasis.Raw).Single();
        Assert.Equal(10.50m, quote.Denomination.ToMajor(quote.Close));
        var fx = view.Fx("USD", "GBP", FxPurpose.Reference, TimeSpan.FromHours(1));
        Assert.Equal(80m, fx.Convert(100, "USD"));
        Assert.Throws<InvalidOperationException>(() => fx.Convert(100, "GBP"));
        Assert.Throws<InvalidDatasetException>(() => view.Fx("GBP", "USD", FxPurpose.Reference, TimeSpan.FromHours(1)));
        Assert.Throws<InvalidDatasetException>(() => view.Fx("USD", "GBP", FxPurpose.Executable, TimeSpan.FromHours(1)));
        Assert.Throws<InvalidDatasetException>(() => view.Fx("USD", "GBP", FxPurpose.Reference, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void PriceWithContradictoryListingCurrencyIsQuarantinedAtSelection()
    {
        var bad = Change(Fixture(), "raw-jan2", x => ((PriceBar)x) with { Denomination = new("USD", QuoteUnit.Major) });
        Assert.Throws<InvalidDatasetException>(() => View(bad, "2024-01-10T12:00:00Z").Prices("listing-a", PriceBasis.Raw));
    }

    [Fact]
    public void OverlappingKnownListingsOrMembershipsCannotBeSilentlyChosen()
    {
        var file = Fixture();
        var old = (Listing)file.Observations.Single(x => x.Id == "listing-a-original");
        var overlap = old with { Id = "overlap", EffectiveFrom = At("2024-01-03T00:00:00Z") };
        var bad = file with { Observations = file.Observations.Add(overlap) };
        Assert.Throws<InvalidDatasetException>(() => View(bad, "2024-01-10T12:00:00Z").Universe("synthetic-isa"));
        var member = (Membership)file.Observations.Single(x => x.Id == "member-a");
        bad = file with { Observations = file.Observations.Add(member with { Id = "overlap", EffectiveFrom = At("2024-01-03T00:00:00Z"), Eligible = false }) };
        Assert.Throws<InvalidDatasetException>(() => View(bad, "2024-01-10T12:00:00Z").Universe("synthetic-isa"));
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("publication")]
    [InlineData("evidence")]
    [InlineData("date-policy")]
    [InlineData("ordering")]
    public void MissingOrAmbiguousAvailabilityFailsBeforeSelection(string mutation)
    {
        var file = Change(Fixture(), "raw-jan2", x => x with
        {
            Evidence = mutation switch
            {
                "availability" => x.Evidence with { AvailableAt = null },
                "publication" => x.Evidence with { PublishedAt = null },
                "evidence" => x.Evidence with { AvailabilityEvidence = AvailabilityEvidence.Unknown },
                "date-policy" => x.Evidence with { Precision = TimePrecision.Day },
                _ => x.Evidence with { AvailableAt = At("2024-01-01T00:00:00Z") }
            }
        });
        Assert.Throws<InvalidDatasetException>(() => new PointInTimeDataset(file));
    }

    [Theory]
    [InlineData("source-hash")]
    [InlineData("licence")]
    [InlineData("schema")]
    [InlineData("query-token")]
    [InlineData("calendar")]
    public void IncompleteOrUnsafeProvenanceIsRejected(string mutation)
    {
        var file = Fixture();
        var m = file.Manifest;
        var source = m.Sources[0];
        m = mutation switch
        {
            "schema" => m with { SchemaVersion = "unknown" },
            "calendar" => m with { Calendars = [m.Calendars[0] with { TimeZone = "Unknown/Clock" }] },
            _ => m with
            {
                Sources = [mutation switch
            {
                "source-hash" => source with { ContentSha256 = "not-a-digest" },
                "licence" => source with { LicenceReference = "" },
                _ => source with { Location = "https://example.invalid/prices?api_token=redacted" }
            }]
            }
        };
        Assert.Throws<InvalidDatasetException>(() => new PointInTimeDataset(file with { Manifest = m }));
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("cycle")]
    [InlineData("same-time")]
    [InlineData("different-series")]
    public void BrokenRevisionAncestryIsRejected(string mutation)
    {
        var file = Change(Fixture(), "profit-restated", x => ((FundamentalFact)x) with
        {
            Evidence = x.Evidence with
            {
                RevisionOf = mutation switch { "dangling" => "absent", "cycle" => x.Id, "different-series" => "raw-jan2", _ => "profit-original" },
                AvailableAt = mutation == "same-time" ? At("2024-02-15T10:00:00Z") : x.Evidence.AvailableAt,
                PublishedAt = mutation == "same-time" ? At("2024-02-15T10:00:00Z") : x.Evidence.PublishedAt
            }
        });
        HasIssue(file, "revision-chain");
    }

    [Fact]
    public void UnknownUnitsInvalidPricesAndNullEntriesAreRejected()
    {
        HasIssue(Change(Fixture(), "raw-jan2", x => ((PriceBar)x) with { Denomination = new("GBX", QuoteUnit.Major) }), "price-unit");
        HasIssue(Change(Fixture(), "raw-jan2", x => ((PriceBar)x) with { Denomination = new("ZZZ", QuoteUnit.Major) }), "price-unit");
        HasIssue(Change(Fixture(), "raw-jan2", x => ((PriceBar)x) with { Low = 2000 }), "price-range");
        var file = Fixture();
        HasIssue(file with { Observations = file.Observations.Add(null!) }, "null-record");
        HasIssue(Change(file, "raw-jan2", x => x with { Evidence = null! }), "null-record");
    }

    [Fact]
    public void UnknownJsonFieldsEnumsAndUnzonedTimestampsAreNotDefaulted()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json"));
        foreach (var bad in new[] { text.Replace("\"synthetic\": true", "\"synthetic\": true, \"unexpected\": 1"),
            text.Replace("\"raw\"", "\"unknown-adjustment\""), text.Replace("2025-01-01T00:00:00Z", "2025-01-01T00:00:00") })
            Assert.Throws<JsonException>(() => DatasetJson.Read(Encoding.UTF8.GetBytes(bad)));
        var missing = text.Replace("\"close\": 1050,", "");
        Assert.Throws<JsonException>(() => DatasetJson.Read(Encoding.UTF8.GetBytes(missing)));
    }

    [Fact]
    public void SecArrayShapeErrorsAreQuarantinedRatherThanZippedAndTruncated()
    {
        var dto = UpstreamJson.Read<SecRecentFilingsDto>("""
            {"accessionNumber":["synthetic-1","synthetic-2"],"acceptanceDateTime":["2024-01-01T12:00:00Z"],
             "filingDate":["2024-01-01","2024-01-01"],"reportDate":["2023-12-31","2023-12-31"],
             "form":["10-K","10-K/A"],"primaryDocument":["first.htm","amendment.htm"]}
            """)!;
        Assert.False(dto.ValidateShape().IsValid);
        Assert.True((dto with { AcceptanceDateTime = ["2024-01-01T12:00:00Z", "2024-01-02T12:00:00Z"] }).ValidateShape().IsValid);
    }

    [Fact]
    public void UpstreamDtoOptionalityDoesNotManufactureAMissingQuote()
    {
        var dto = UpstreamJson.Read<EodPriceDto>("""{"date":"2024-01-02","open":10,"adjusted_close":9,"new_field":true}""")!;
        Assert.Null(dto.Close);
        Assert.Equal(9m, dto.AdjustedClose);
    }

    [Fact]
    public void ImportIsContentAddressedIdempotentAndRefusesCorruptExistingContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-data-tests-" + Guid.NewGuid().ToString("N"));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json"));
        try
        {
            var path = DatasetArchive.Store(root, bytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".json", Path.GetFileName(path));
            Assert.Equal(path, DatasetArchive.Store(root, bytes));
            File.WriteAllText(path, "corrupt");
            Assert.Throws<InvalidDataException>(() => DatasetArchive.Store(root, bytes));
            Assert.Equal("corrupt", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".import-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InvalidDatasetDoesNotCreateAnArchiveAndOversizedInputIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-data-tests-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<JsonException>(() => DatasetArchive.Store(root, Encoding.UTF8.GetBytes("{}")));
        Assert.False(Directory.Exists(root));
        Assert.Throws<JsonException>(() => DatasetJson.Read(new byte[DatasetJson.MaximumBytes + 1]));
    }
    [Fact]
    public void FixtureSourceAndCalendarFingerprintsMatchTheirAuthoredOriginals()
    {
        var file = Fixture();
        string Digest(string name) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
        Assert.Equal(Digest("upstream-events.v1.json"), file.Manifest.Sources[0].ContentSha256);
        Assert.All(file.Manifest.Calendars, x => Assert.Equal(Digest("calendars.v1.json"), x.ContentSha256));
    }

    [Fact]
    public void SessionDateUsesExchangeTimezoneRatherThanUtcDate()
    {
        var file = Fixture();
        var close = At("2024-02-02T00:30:00Z");
        var sample = (PriceBar)file.Observations.Single(x => x.Id == "raw-jan2");
        var row = sample with
        {
            Id = "ny-session",
            ListingId = "listing-b",
            SessionDate = new(2024, 2, 1),
            SessionCloseAt = close,
            Denomination = new("USD", QuoteUnit.Major),
            Evidence = sample.Evidence with { PublishedAt = close.AddMinutes(1), AvailableAt = close.AddMinutes(2) }
        };
        var withBar = file with { Observations = file.Observations.Add(row) };
        Assert.Single(View(withBar, "2024-02-02T01:00:00Z").Prices("listing-b", PriceBasis.Raw));
        var wrong = Change(withBar, "ny-session", x => ((PriceBar)x) with { SessionDate = new(2024, 2, 2) });
        Assert.Throws<InvalidDatasetException>(() => View(wrong, "2024-02-02T01:00:00Z").Prices("listing-b", PriceBasis.Raw));
    }

    [Fact]
    public void ADecisionCannotExceedSnapshotCoverageOrQueryAnUnseenFutureListing()
    {
        var dataset = new PointInTimeDataset(Fixture());
        Assert.Throws<InvalidDatasetException>(() => dataset.AsOf(At("2026-01-01T00:00:00Z")));
        var view = dataset.AsOf(At("2024-01-10T12:00:00Z"));
        Assert.Throws<InvalidDatasetException>(() => view.ListingsAt(At("2024-02-02T00:00:00Z")));
    }

    [Fact]
    public void ProviderIdentifiersAreVersionedAndAmbiguousMappingsNeedVenueEvidence()
    {
        var file = Fixture();
        var before = View(file, "2024-01-10T12:00:00Z");
        Assert.Equal("listing-a", before.ResolveListing("fixture.ticker", "OLD").ListingId);
        Assert.Throws<InvalidDatasetException>(() => before.ResolveListing("fixture.ticker", "NEW"));
        Assert.Equal("listing-a", View(file, "2024-04-02T12:00:00Z").ResolveListing("fixture.ticker", "NEW").ListingId);
        var collision = Change(file, "listing-b", x => ((Listing)x) with
        { ProviderIds = [new("fixture.isin", "FICTIONAL-A")] });
        var view = View(collision, "2024-02-02T12:00:00Z");
        Assert.Throws<InvalidDatasetException>(() => view.ResolveListing("fixture.isin", "FICTIONAL-A"));
        Assert.Equal("listing-a", view.ResolveListing("fixture.isin", "FICTIONAL-A", "XLON").ListingId);
    }

    [Fact]
    public void DeclaredConservativeAvailabilityIsAllowedButUnknownScopeIsNot()
    {
        var file = Change(Fixture(), "raw-jan2", x => x with
        {
            Evidence = x.Evidence with
            {
                Precision = TimePrecision.Day,
                AvailabilityEvidence = AvailabilityEvidence.ConservativePolicy,
                PolicyReference = "Synthetic next-session policy; not observed dissemination evidence"
            }
        });
        Assert.True(DatasetValidator.Validate(file).IsValid);
        HasIssue(Change(file, "member-a", x => ((Membership)x) with { UniverseId = "absent" }), "membership-identity");
        Assert.Throws<InvalidDatasetException>(() => View(file, "2024-01-10T12:00:00Z").Universe("absent"));
        HasIssue(Change(file, "listing-a-new", x => ((Listing)x) with { SecurityId = "another-security" }), "listing-identity-change");
    }

    [Fact]
    public void ArchiveLoadVerifiesBytesEvenWhenTamperedJsonStillPassesTheSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-data-tests-" + Guid.NewGuid().ToString("N"));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json"));
        try
        {
            var path = DatasetArchive.Store(root, bytes);
            var digest = Path.GetFileNameWithoutExtension(path);
            Assert.True(DatasetArchive.Load(root, digest).Manifest.Synthetic);
            var tampered = Encoding.UTF8.GetString(bytes).Replace("synthetic-pit-v1", "tampered-pit-v1");
            Assert.True(DatasetValidator.Validate(DatasetJson.Read(Encoding.UTF8.GetBytes(tampered))).IsValid);
            File.WriteAllText(path, tampered);
            Assert.Throws<InvalidDataException>(() => DatasetArchive.Load(root, digest));
            Assert.Throws<ArgumentException>(() => DatasetArchive.Load(root, "../outside"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("2024-12-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void ObservedPublicationCannotBeBackdatedBeforeFirstSeenOrUseDefaultEvidence(string firstSeen)
    {
        var file = Fixture();
        file = file with
        {
            Manifest = file.Manifest with { Sources = file.Manifest.Sources.Select(x => x with { FirstSeenAt = At(firstSeen) }).ToImmutableArray() },
            Observations = file.Observations.Select(x => x.Id == "profit-original"
                ? x with { Evidence = x.Evidence with { AvailabilityEvidence = AvailabilityEvidence.ObservedPublication } } : x).ToImmutableArray()
        };
        HasIssue(file, "availability-evidence");
        Assert.Throws<InvalidDatasetException>(() => new PointInTimeDataset(file));
    }

    [Fact]
    public void ObservedAvailabilityMayFollowFirstSeenWithoutLeakingIntoEarlierDecisions()
    {
        var file = Fixture();
        var seen = At("2024-02-16T12:00:00Z");
        var available = seen.AddDays(1);
        file = file with
        {
            Manifest = file.Manifest with { Sources = file.Manifest.Sources.Select(x => x with { FirstSeenAt = seen }).ToImmutableArray() },
            Observations = file.Observations.Select(x => x.Id == "profit-original"
                ? x with { Evidence = x.Evidence with { AvailabilityEvidence = AvailabilityEvidence.ObservedPublication, AvailableAt = available } } : x).ToImmutableArray()
        };
        Assert.True(DatasetValidator.Validate(file).IsValid);
        Assert.Empty(new PointInTimeDataset(file).AsOf(available.AddTicks(-1)).Fundamentals("company-a"));
        Assert.Equal(100m, new PointInTimeDataset(file).AsOf(available).Fundamentals("company-a").Single().Value);
    }

}
