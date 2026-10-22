using System.IO.Compression;
using System.Net;
using System.Text;
using Twenty.Ingestion.Sec;

namespace Twenty.Tests;

/// <summary>
/// Covers bounded EDGAR full-index reading: the delisting-form denominator is counted exactly as the
/// index writes it, and anything that would misstate it — a row outside its quarter, a row that does
/// not parse, a file that is not an index, an archive that inflates past the bound — is refused.
/// </summary>
public sealed class SecFormIndexTests
{
    private static readonly DateOnly From = new(2013, 1, 1), To = new(2013, 3, 31);
    private const string Contact = "offline-fixture@sec.gov";

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.Latin1)) writer.Write(text);
        return output.ToArray();
    }

    private static string Row(string form, string company, string cik, string filed, string path) =>
        form + "  " + company.PadRight(56) + cik.PadLeft(7) + "  " + filed + "  " + path;

    private static byte[] Index(params string[] rows) => Gzip(
        "Description:           Master Index of EDGAR Dissemination Feed by Form Type\n" +
        "Last Data Received:    March 31, 2013\n" +
        "Comments:              webmaster@sec.gov\n\n\n\n" +
        "Form Type   Company Name                                                  CIK         Date Filed  File Name\n" +
        new string('-', 120) + "\n" + string.Join("\n", rows) + "\n");

    [Fact]
    public void TheDelistingFamilyIsCountedPerFilingYear()
    {
        var file = SecFormIndex.Parse(Index(
            Row("25", "AMERICAN LEARNING Corp", "774517", "2013-03-15", "edgar/data/774517/0001144204-13-015227.txt"),
            Row("25-NSE", "NASDAQ STOCK MARKET LLC", "1000000", "2013-01-08", "edgar/data/3570/0000003570-13-000011.txt"),
            Row("25-NSE/A", "NASDAQ STOCK MARKET LLC", "1000000", "2013-02-01", "edgar/data/3570/0000003570-13-000012.txt"),
            Row("25/A", "SOME ISSUER INC", "12345", "2013-02-02", "edgar/data/12345/0000012345-13-000013.txt"),
            Row("10-K", "APPLE INC", "320193", "2013-01-30", "edgar/data/320193/0000320193-13-000014.txt")), From, To);

        Assert.Equal(5, file.Rows);
        Assert.Equal(new DateOnly(2013, 1, 8), file.FirstFiled);
        Assert.Equal(new DateOnly(2013, 3, 15), file.LastFiled);
        // Exactly as the index writes them: no family is merged or normalized, and an amendment
        // is its own row rather than an additional delisting.
        Assert.Equal(new[] { "25", "25-NSE", "25-NSE/A", "25/A" }, file.Delistings.Select(c => c.Form).ToArray());
        Assert.All(file.Delistings, c => { Assert.Equal(2013, c.Year); Assert.Equal(1, c.Filings); });
    }

    [Fact]
    public void FormTypesWithSpacesDoNotConfuseTheFormColumn()
    {
        var file = SecFormIndex.Parse(Index(
            Row("SC 13G/A", "BERKSHIRE HATHAWAY INC", "1067983", "2013-02-14", "edgar/data/1067983/0001067983-13-000015.txt"),
            Row("SC 13D/A", "ACTIVIST HOLDINGS LLC", "1234567", "2013-02-20", "edgar/data/1234567/0001234567-13-000016.txt")), From, To);

        Assert.Equal(2, file.Rows);
        Assert.Empty(file.Delistings);
    }

    [Fact]
    public void CompanyNamesHoldingDigitsAndDatesDoNotMoveTheColumns()
    {
        var file = SecFormIndex.Parse(Index(
            Row("25", "ACME 2013-03-15 42 HOLDINGS", "774517", "2013-03-15", "edgar/data/774517/0001144204-13-015227.txt"),
            Row("10-Q", "M3 25-NSE TRUST", "1", "2013-01-02", "edgar/data/1/0000000001-13-000018.txt")), From, To);

        Assert.Equal(2, file.Rows);
        // The company names carry form-like and date-like text; the tail still decides the columns.
        Assert.Equal("25", Assert.Single(file.Delistings).Form);
    }

    [Fact]
    public void ARowOutsideItsQuarterIsRefused()
    {
        var index = Index(Row("25", "LATE FILER INC", "774517", "2013-04-01", "edgar/data/774517/0001144204-13-015227.txt"));
        Assert.Equal("index-date-range", Assert.Throws<SecCollectionException>(() => SecFormIndex.Parse(index, From, To)).Code);
    }

    [Fact]
    public void ARowThatDoesNotParseIsRefused()
    {
        var index = Index(Row("25", "LATE FILER INC", "774517", "2013-03-15", "not-an-edgar-path.txt"));
        Assert.Equal("index-row", Assert.Throws<SecCollectionException>(() => SecFormIndex.Parse(index, From, To)).Code);
    }

    [Theory]
    [InlineData("no-header")]
    [InlineData("no-rule")]
    [InlineData("no-rows")]
    public void AFileThatIsNotAnIndexIsRefused(string kind)
    {
        var text = kind switch
        {
            "no-header" => "Hello\n" + new string('-', 120) + "\n" + Row("25", "X", "1", "2013-03-15", "edgar/data/1/1.txt"),
            "no-rule" => "Form Type   Company Name\n" + new string('*', 120) + "\n" + Row("25", "X", "1", "2013-03-15", "edgar/data/1/1.txt"),
            _ => "Form Type   Company Name\n" + new string('-', 120) + "\n\n"
        };
        Assert.Equal("index-shape", Assert.Throws<SecCollectionException>(() => SecFormIndex.Parse(Gzip(text), From, To)).Code);
    }

    [Fact]
    public void EmptyAndNonArchiveInputsAreRefused()
    {
        Assert.Equal("index-size-limit", Assert.Throws<SecCollectionException>(() => SecFormIndex.Parse([], From, To)).Code);
        Assert.Equal("index-shape", Assert.Throws<SecCollectionException>(
            () => SecFormIndex.Parse(Encoding.Latin1.GetBytes("not gzip at all"), From, To)).Code);
    }

    [Fact]
    public void AnArchiveThatInflatesPastTheBoundIsRefused()
    {
        var bomb = Gzip(new string(' ', 20000));
        Assert.Equal("index-size-limit", Assert.Throws<SecCollectionException>(() => SecFormIndex.Inflate(bomb, 1024)).Code);
        Assert.Equal(20000, SecFormIndex.Inflate(bomb, 20000).Length); // the same bytes are fine inside the bound
    }

    private sealed class Clock : ISecClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2024-03-04T12:00:00Z");
        public TimeSpan MonotonicNow { get; private set; }
        public Task Delay(TimeSpan delay, CancellationToken ct) { MonotonicNow += delay; return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-sec-form-index-" + Guid.NewGuid().ToString("N"));
        public readonly SecArchive Archive;
        public readonly SecClient Client;
        public readonly List<string> Requested = [];
        private readonly HttpClient http;
        public Rig(Func<string, byte[]> payload, SecLimits? limits = null)
        {
            var effective = limits ?? new();
            var clock = new Clock();
            Archive = new(Root, effective);
            http = new(new Handler(request =>
            {
                Requested.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(payload(request.RequestUri.AbsolutePath)) });
            }));
            Client = new(http, Archive, Contact, effective, clock, new SecRequestGate(clock));
        }
        public void Dispose() { http.Dispose(); Archive.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    [Fact]
    public async Task CollectAggregatesEveryQuarterAndStopsAtTheRequestBudget()
    {
        // Each quarter's index carries rows from that quarter, as EDGAR writes them.
        static byte[] QuarterPayload(string path)
        {
            var month = (int.Parse(path.Split('/')[^2].AsSpan(3)) - 1) * 3 + 1;
            return Index(
                Row("25", "FIRST INC", "1", $"2013-{month:00}-08", "edgar/data/1/1.txt"),
                Row("25-NSE", "NASDAQ STOCK MARKET LLC", "1000000", $"2013-{month:00}-15", "edgar/data/2/2.txt"));
        }
        using var rig = new Rig(QuarterPayload);
        var report = await SecFormIndex.Collect(rig.Client, 2013, 2013);

        Assert.Equal(4, report.Quarters.Length);
        Assert.Equal(8, report.TotalRows);
        // Every quarter carries the same two rows, so the family totals are four each.
        Assert.Equal(4, Assert.Single(report.Counts.Where(c => c.Form == "25")).Filings);
        Assert.Equal(4, Assert.Single(report.Counts.Where(c => c.Form == "25-NSE")).Filings);
        Assert.Equal(8, report.TotalDelistings);
        Assert.False(report.RequestLimitReached);
        Assert.All(report.Quarters, q => Assert.Equal(2, q.Rows));

        using var tight = new Rig(QuarterPayload, new SecLimits { MaximumRequests = 2 });
        var limited = await SecFormIndex.Collect(tight.Client, 2013, 2013);
        Assert.True(limited.RequestLimitReached);
        Assert.Equal(2, limited.Quarters.Length);
    }
}
