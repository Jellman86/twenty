using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Twenty.Core.Data;
using Twenty.Ingestion.Sec;

namespace Twenty.Tests;

public sealed class SecCompanyFactsTests
{
    private static readonly DateTimeOffset Retrieved = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private const string BulkSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static DatasetFile Base(params Observation[] extra)
    {
        var evidence = new Evidence("fixture-source", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), AvailabilityEvidence.ArchivedPublication,
            TimePrecision.Second, null, null);
        var listing = new Listing("listing-fruit", evidence, "security-fruit", "listing-fruit", "company-fruit",
            "FRUT", "XNAS", AssetClass.Stock, [new ProviderIdentifier("sec-cik", "0000320193")],
            new Denomination("USD", QuoteUnit.Major), "ny-calendar",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, ListingStatus.Active);
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, "base-dataset",
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), "features.v1",
            [new FeatureDefinition("lagged-close", "v1", "Raw close", "After session close")],
            [new SourceSnapshot("fixture-source", "urn:twenty:synthetic:sec-facts", "Authored fixture; no market data",
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), null, new string('b', 64), "fixture-v1", "Append revisions")],
            [new CalendarReference("ny-calendar", "America/New_York", "v1", new string('c', 64))],
            [new UniverseDefinition("research-only", UniverseKind.Research, "Authored research universe")], true);
        return new(manifest, [listing, .. extra]);
    }
    private static byte[] Bulk(params (string Name, string Json)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, json) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(json);
            }
        return buffer.ToArray();
    }
    private static SecFactsRequest Request(params string[] concepts) => new([.. concepts], ["us-gaap", "dei"], Retrieved);
    private static SecFactsResult Convert(DatasetFile input, byte[] zip, SecFactsRequest? request = null) =>
        SecCompanyFacts.Convert(input, new MemoryStream(zip), BulkSha, request ?? Request("Revenues", "Assets"));
    private const string FruitFacts = """
        {"cik":320193,"entityName":"Authored Fruit Co","facts":{"us-gaap":{
          "Revenues":{"label":"Revenues","units":{"USD":[
            {"start":"2022-10-01","end":"2023-09-30","val":100,"accn":"0000320193-23-000001","fy":2023,"fp":"FY","form":"10-K","filed":"2023-11-03"},
            {"start":"2022-10-01","end":"2023-09-30","val":105,"accn":"0000320193-24-000002","fy":2023,"fp":"FY","form":"10-K/A","filed":"2024-02-01"}]}},
          "Assets":{"label":"Assets","units":{"USD":[
            {"end":"2023-09-30","val":500,"accn":"0000320193-23-000001","fy":2023,"fp":"FY","form":"10-K","filed":"2023-11-03"}]}},
          "UnrequestedConcept":{"label":"Other","units":{"USD":[
            {"end":"2023-09-30","val":1,"accn":"0000320193-23-000001","filed":"2023-11-03"}]}}
        }}}
        """;

    [Fact]
    public void MapsFactsWithDayPrecisionConservativeAvailabilityAndRevisionChain()
    {
        var result = Convert(Base(), Bulk(("CIK0000320193.json", FruitFacts)));
        Assert.Equal(1, result.Companies); Assert.Equal(3, result.FactsAdded); Assert.Equal(0, result.FactsSkippedExisting);
        var dataset = new PointInTimeDataset(result.Dataset); // must validate
        var before = dataset.AsOf(new DateTimeOffset(2023, 11, 3, 23, 59, 59, TimeSpan.Zero)).Fundamentals("company-fruit");
        Assert.Empty(before); // filed 2023-11-03 is not usable that day under the conservative policy
        var afterFiling = dataset.AsOf(new DateTimeOffset(2023, 11, 4, 0, 0, 0, TimeSpan.Zero)).Fundamentals("company-fruit");
        Assert.Equal(2, afterFiling.Length);
        var revenue = Assert.Single(afterFiling, f => f.Concept == "us-gaap:Revenues");
        Assert.Equal(100m, revenue.Value); Assert.Equal(new DateOnly(2022, 10, 1), revenue.PeriodStart);
        Assert.Equal(new DateOnly(2023, 9, 30), revenue.PeriodEnd); Assert.Equal("USD", revenue.Unit);
        Assert.Equal(TimePrecision.Day, revenue.Evidence.Precision);
        Assert.Equal(AvailabilityEvidence.ConservativePolicy, revenue.Evidence.AvailabilityEvidence);
        Assert.False(string.IsNullOrWhiteSpace(revenue.Evidence.PolicyReference));
        var afterAmendment = dataset.AsOf(new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero)).Fundamentals("company-fruit");
        var restated = Assert.Single(afterAmendment, f => f.Concept == "us-gaap:Revenues");
        Assert.Equal(105m, restated.Value); Assert.Equal(revenue.Id, restated.Evidence.RevisionOf);
        var assets = Assert.Single(afterFiling, f => f.Concept == "us-gaap:Assets");
        Assert.Null(assets.PeriodStart); Assert.Equal(500m, assets.Value);
        Assert.DoesNotContain(result.Dataset.Observations.OfType<FundamentalFact>(), f => f.Concept.Contains("Unrequested"));
    }

    [Fact]
    public void IdenticalDuplicatePointsDedupeAndConflictingContextsFail()
    {
        var duplicate = FruitFacts.Replace("\"val\":105", "\"val\":100").Replace("0000320193-24-000002", "0000320193-23-000001");
        // second point now identical except filed date differs; make it fully identical instead:
        duplicate = duplicate.Replace("\"filed\":\"2024-02-01\"", "\"filed\":\"2023-11-03\"");
        var result = Convert(Base(), Bulk(("CIK0000320193.json", duplicate)));
        Assert.Equal(2, result.FactsAdded);
        var conflict = FruitFacts.Replace("\"val\":105", "\"val\":999").Replace("0000320193-24-000002", "0000320193-23-000001");
        Assert.Equal("sec-facts-ambiguous-context", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000320193.json", conflict)))).Code);
    }

    [Fact]
    public void SameDayRevisionsAndInterleavedExistingFactsFailClosed()
    {
        var sameDay = FruitFacts.Replace("\"filed\":\"2024-02-01\"", "\"filed\":\"2023-11-03\"");
        Assert.Equal("sec-facts-same-day-revision", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000320193.json", sameDay)))).Code);
        var first = Convert(Base(), Bulk(("CIK0000320193.json", FruitFacts))).Dataset;
        // Re-running over the converted dataset: everything already exists exactly, nothing is added.
        var rerun = SecCompanyFacts.Convert(first, new MemoryStream(Bulk(("CIK0000320193.json", FruitFacts))), BulkSha, Request("Revenues", "Assets"));
        Assert.Equal(0, rerun.FactsAdded); Assert.Equal(3, rerun.FactsSkippedExisting);
        Assert.Equal(first.Observations.Length, rerun.Dataset.Observations.Length);
    }

    [Fact]
    public void EntryAndContentValidationFailsClosed()
    {
        Assert.Equal("sec-facts-zip", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), [1, 2, 3])).Code);
        Assert.Equal("sec-facts-entry", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("readme.txt", "hello")))).Code);
        Assert.Equal("sec-facts-entry-missing", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000999999.json", FruitFacts)))).Code);
        Assert.Equal("sec-facts-cik-mismatch", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000320193.json", FruitFacts.Replace("\"cik\":320193", "\"cik\":999999"))))).Code);
        Assert.Equal("sec-facts-point", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000320193.json", FruitFacts.Replace("0000320193-23-000001", "not-an-accession"))))).Code);
        Assert.Equal("sec-facts-point", Assert.Throws<SecCollectionException>(() =>
            Convert(Base(), Bulk(("CIK0000320193.json", FruitFacts.Replace("\"filed\":\"2023-11-03\"", "\"filed\":\"2023-09-01\""))))).Code);
        Assert.Equal("sec-facts-request", Assert.Throws<SecCollectionException>(() =>
            SecCompanyFacts.Convert(Base(), new MemoryStream(Bulk(("CIK0000320193.json", FruitFacts))), BulkSha, new([], ["us-gaap"], Retrieved))).Code);
        Assert.Equal("sec-facts-request", Assert.Throws<SecCollectionException>(() =>
            SecCompanyFacts.Convert(Base(), new MemoryStream(Bulk(("CIK0000320193.json", FruitFacts))), "ZZZ", Request("Revenues"))).Code);
    }

    [Fact]
    public void ConversionIsDeterministicArchivableAndMergeIsAppendOnly()
    {
        var zip = Bulk(("CIK0000320193.json", FruitFacts));
        var first = Convert(Base(), zip).Dataset;
        var second = Convert(Base(), zip).Dataset;
        Assert.Equal(DatasetJson.Options.GetHashCode(), DatasetJson.Options.GetHashCode()); // options are shared/frozen
        var firstBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(first, DatasetJson.Options);
        var secondBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(second, DatasetJson.Options);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Contains(first.Manifest.Sources, s => s.Id == SecCompanyFacts.SourceId && s.ContentSha256 == BulkSha);
        Assert.Contains(first.Manifest.Features, f => f.Name == "sec-fundamentals");
        Assert.Equal(Retrieved, first.Manifest.CreatedAt);
        var root = Path.Combine(Path.GetTempPath(), "twenty-sec-facts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = DatasetArchive.Store(root, firstBytes);
            var digest = System.Convert.ToHexStringLower(SHA256.HashData(firstBytes));
            var loaded = DatasetArchive.Load(root, digest);
            Assert.Equal(2, loaded.AsOf(new DateTimeOffset(2023, 11, 4, 0, 0, 0, TimeSpan.Zero)).Fundamentals("company-fruit").Length);
            Assert.EndsWith(digest + ".json", path);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        // A later filed value may append to the chain head of the converted dataset.
        var laterFacts = FruitFacts.Replace("\"val\":105", "\"val\":110").Replace("0000320193-24-000002", "0000320193-24-000009")
            .Replace("\"filed\":\"2024-02-01\"", "\"filed\":\"2024-03-01\"");
        var appended = SecCompanyFacts.Convert(first, new MemoryStream(Bulk(("CIK0000320193.json", laterFacts))), BulkSha, Request("Revenues", "Assets"));
        Assert.Equal(1, appended.FactsAdded); Assert.Equal(2, appended.FactsSkippedExisting);
        var view = new PointInTimeDataset(appended.Dataset).AsOf(new DateTimeOffset(2024, 3, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(110m, Assert.Single(view.Fundamentals("company-fruit"), f => f.Concept == "us-gaap:Revenues").Value);
    }
}
